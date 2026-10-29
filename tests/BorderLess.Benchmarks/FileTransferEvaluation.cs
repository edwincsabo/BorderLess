using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BorderLess.Core;

internal static class FileTransferEvaluation
{
    public static async Task RunAsync(string output)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "file-evaluation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "generated-source.bin"); var destinationPath = Path.Combine(directory, "received.bin");
        const int size = 16 * 1024 * 1024, chunkSize = 64 * 1024;
        var generation = RandomNumberGenerator.GetBytes(chunkSize);
        using (var generated = File.Create(sourcePath)) for (var offset = 0; offset < size; offset += chunkSize) generated.Write(generation);
        var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
        using var aCert = NodeIdentity.CreateCertificate(aId); using var bCert = NodeIdentity.CreateCertificate(bId);
        await using var a = new NetworkHub(new(aId, "Evaluación origen", aCert));
        await using var b = new NetworkHub(new(bId, "Evaluación destino", bCert));
        a.Start(0); b.Start(0); await a.PairAsync(b.CreateInvitation() with { Host = IPAddress.Loopback.ToString() });
        using var receivedFile = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, chunkSize);
        long receivedBytes = 0;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long[] stamps = Array.Empty<long>(); double[] values = Array.Empty<double>(); bool[] seen = Array.Empty<bool>();
        var sequenceStart = 0L; var inputReceived = 0; var orderErrors = 0; var duplicates = 0; long previous = 0;
        TaskCompletionSource? actionsDone = null;
        b.Message += (session, frame) =>
        {
            if (frame.Kind == PacketKind.Clipboard)
            {
                // Test sink only: bytes go to an owned fixture, never the OS clipboard.
                receivedFile.Write(frame.Payload); receivedBytes += frame.Payload.Length;
                if (receivedBytes == size) { receivedFile.Flush(); finished.TrySetResult(); }
            }
            else if (frame.Kind == PacketKind.Input)
            {
                var at = Stopwatch.GetTimestamp(); var input = frame.ReadInput(); var index = (int)(input.Sequence - sequenceStart);
                if (index < 0 || index >= stamps.Length) return;
                if (seen[index]) { duplicates++; return; } seen[index] = true;
                if (input.Sequence <= previous) orderErrors++; previous = input.Sequence;
                values[index] = Stopwatch.GetElapsedTime(stamps[index], at).TotalMilliseconds;
                if (++inputReceived == stamps.Length) actionsDone?.TrySetResult();
            }
        };
        var results = new List<object>(); var epoch = Guid.NewGuid(); var nextSequence = 0L;
        async Task Measure(string scenario, int round, int count)
        {
            stamps = new long[count]; values = new double[count]; seen = new bool[count]; inputReceived = orderErrors = duplicates = 0;
            sequenceStart = nextSequence + 1; actionsDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            for (var i = 0; i < count; i++)
            {
                await Task.Delay(5 + i % 5 * 2);
                var action = Frame.FromInput(new(i % 2 == 0 ? InputKind.KeyDown : InputKind.KeyUp, ++nextSequence, epoch, 0, 0, 65, 30));
                stamps[i] = Stopwatch.GetTimestamp(); if (!a.Inputs[bId].Send(action)) throw new IOException("Sesión de entrada cerrada.");
            }
            await actionsDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var sorted = values.Order().ToArray(); double P(double q) => sorted[(int)Math.Ceiling(q * count) - 1];
            var row = new { Scenario = scenario, Round = round, Actions = count, Received = inputReceived, OrderErrors = orderErrors, Duplicates = duplicates, P50 = P(.5), P95 = P(.95), P99 = P(.99), Maximum = sorted[^1] };
            results.Add(row); Console.WriteLine($"{scenario} #{round}: p95={row.P95:F3} p99={row.P99:F3} ms");
            if (inputReceived != count || orderErrors != 0 || duplicates != 0) throw new InvalidDataException("Regresión en acciones durante transferencia.");
        }
        await Measure("warmup", 0, 100); results.Clear();
        for (var round = 1; round <= 3; round++) await Measure("without-file", round, 160);
        var transferStart = Stopwatch.GetTimestamp();
        var sending = Task.Run(async () =>
        {
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[chunkSize];
            while (true)
            {
                var count = await input.ReadAsync(buffer); if (count == 0) break;
                var frame = new Frame(PacketKind.Clipboard, buffer.AsSpan(0, count).ToArray(), Stopwatch.GetTimestamp());
                while (!a.Clipboards[bId].Send(frame)) { if (!a.Clipboards[bId].Connected) throw new IOException(); await Task.Delay(5); }
                await Task.Delay(62); // proposal: about 1 MiB/s, with bounded channel backpressure
            }
        });
        for (var round = 1; round <= 3; round++) await Measure("with-file-io", round, 160);
        await sending.WaitAsync(TimeSpan.FromSeconds(60)); await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var transferMs = Stopwatch.GetElapsedTime(transferStart).TotalMilliseconds;
        receivedFile.Dispose();
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
        var targetHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destinationPath)));
        var report = new
        {
            Timestamp = DateTimeOffset.Now,
            Scope = "Evaluation only, not a product feature. Loopback TLS, generated 16 MiB fixture, real source/destination file I/O, separate limited-rate data lane. No OS clipboard or input injection. Does not saturate a physical LAN or measure visible input latency.",
            Bytes = size, ReceivedBytes = receivedBytes, MatchingSha256 = sourceHash == targetHash, TransferMilliseconds = transferMs,
            MiBPerSecond = size / 1048576d / (transferMs / 1000), Measurements = results
        };
        if (sourceHash != targetHash) throw new InvalidDataException("Hash del archivo no coincide.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Transferencia verificada: {receivedBytes} bytes; {report.MiBPerSecond:F2} MiB/s. Solo evaluación.");
    }
}
