using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BorderLess.Core;

if (args.ElementAtOrDefault(0) == "--file-evaluation") { await FileTransferEvaluation.RunAsync(args.ElementAtOrDefault(1) ?? "artifacts/performance/file-evaluation.json"); return; }

var label = args.ElementAtOrDefault(0) ?? "measurement";
var destination = args.ElementAtOrDefault(1) ?? Path.Combine("artifacts", "performance", label + ".json");
var memoryFrame = Frame.FromInput(new(InputKind.Move, 1, Guid.NewGuid(), 10, 20));
using var memoryWire = new MemoryStream(128);
for (var i = 0; i < 100; i++) { memoryWire.Position = 0; await Wire.WriteAsync(memoryWire, memoryFrame, CancellationToken.None); }
var writeThread = Environment.CurrentManagedThreadId;
var writeBefore = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10000; i++) { memoryWire.Position = 0; await Wire.WriteAsync(memoryWire, memoryFrame, CancellationToken.None); }
var wireAllocation = GC.GetAllocatedBytesForCurrentThread() - writeBefore;
var wireAllocationValid = writeThread == Environment.CurrentManagedThreadId;
var hostId = Guid.NewGuid(); var sourceId = Guid.NewGuid();
using var hostCertificate = NodeIdentity.CreateCertificate(hostId);
using var sourceCertificate = NodeIdentity.CreateCertificate(sourceId);
await using var host = new NetworkHub(new(hostId, "Benchmark receiver", hostCertificate));
await using var source = new NetworkHub(new(sourceId, "Benchmark source", sourceCertificate));
host.Start(0); source.Start(0);
await source.PairAsync(host.CreateInvitation() with { Host = IPAddress.Loopback.ToString() });
var session = source.Inputs[hostId];
var epoch = Guid.NewGuid();
long burstReceived = 0, burstLast = 0;
TaskCompletionSource? burstComplete = null;
Run? active = null;
host.Message += (_, frame) =>
{
    if (frame.Kind != PacketKind.Input) return;
    var stamp = Stopwatch.GetTimestamp(); var input = frame.ReadInput();
    if (input.Kind == InputKind.Move)
    {
        Interlocked.Increment(ref burstReceived); Interlocked.Exchange(ref burstLast, input.Sequence);
        if (input.Sequence == 20000) burstComplete?.TrySetResult();
        return;
    }
    Volatile.Read(ref active)?.Accept(input.Sequence, stamp);
};
async Task<Measurement> Measure(string scenario, int count, int round, bool bulk)
{
    var run = new Run(count); Volatile.Write(ref active, run);
    var payload = bulk ? Frame.Json(PacketKind.Clipboard, new ClipboardPayload(sourceId, Guid.NewGuid(), "benchmark", new string('X', 4_000_000))) : default;
    using var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var allocated = GC.GetTotalAllocatedBytes(true);
    var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
    var started = Stopwatch.GetTimestamp();
    var bytesBefore = session.Sent;
    if (bulk) source.Clipboards[hostId].Send(payload);
    for (var i = 0; i < count; i++)
    {
        // Irregular pacing prevents intentional alignment with a 4 ms timer.
        await Task.Delay(5 + (i % 5) * 2);
        var frame = Frame.FromInput(new(i % 2 == 0 ? InputKind.KeyDown : InputKind.KeyUp, i + 1, epoch, 0, 0, 65, 30));
        run.Sent[i] = Stopwatch.GetTimestamp();
        if (!session.Send(frame)) throw new IOException("Benchmark session closed.");
    }
    await run.Complete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var elapsed = Stopwatch.GetElapsedTime(started);
    var allocationBytes = GC.GetTotalAllocatedBytes(true) - allocated;
    var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
    var sorted = run.Milliseconds.Order().ToArray();
    double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
    var result = new Measurement(scenario, round, count, run.Received, run.Duplicates, run.OrderErrors, Percentile(.5), Percentile(.95), Percentile(.99), sorted.Max(), allocationBytes, cpuMs, elapsed.TotalMilliseconds, session.Sent - bytesBefore, GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2);
    Console.WriteLine($"{scenario} #{round}: p50={result.P50:F3} p95={result.P95:F3} p99={result.P99:F3} ms; CPU={cpuMs:F1} ms; alloc={allocationBytes}");
    if (run.OrderErrors != 0 || run.Duplicates != 0 || run.Received != count) throw new InvalidDataException("Loss, duplicate or ordering regression.");
    if (bulk) await Task.Delay(4500); // finish the limited-rate bulk frame before the next round
    return result;
}
await Measure("warmup", 120, 0, false);
var measurements = new List<Measurement>();
for (var round = 1; round <= 3; round++)
{
    measurements.Add(await Measure("idle", 160, round, false));
    measurements.Add(await Measure("clipboard-load", 160, round, true));
}
async Task<BurstMeasurement> MeasureBurst(int round)
{
Interlocked.Exchange(ref burstReceived, 0); Interlocked.Exchange(ref burstLast, 0);
burstComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var burstCpu = Process.GetCurrentProcess().TotalProcessorTime;
var burstAllocated = GC.GetTotalAllocatedBytes(true);
var burstBytes = session.Sent;
var burstStart = Stopwatch.GetTimestamp();
for (var batch = 0; batch < 100; batch++)
{
    for (var j = 0; j < 200; j++)
    {
        var index = batch * 200 + j + 1;
        if (!session.Send(Frame.FromInput(new(InputKind.Move, index, epoch, index, 0)))) throw new IOException("Burst rejected.");
    }
    await Task.Delay(7);
}
await burstComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
var result = new BurstMeasurement(round, 20000, Interlocked.Read(ref burstReceived), Interlocked.Read(ref burstLast), session.Sent - burstBytes, (Process.GetCurrentProcess().TotalProcessorTime - burstCpu).TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - burstAllocated, Stopwatch.GetElapsedTime(burstStart).TotalMilliseconds);
Console.WriteLine($"burst #{round}: received={result.ReceivedSnapshots}/20000 snapshots; CPU={result.CpuMilliseconds:F1} ms; alloc={result.AllocatedBytes}");
return result;
}
var bursts = new List<BurstMeasurement>();
for (var round = 0; round <= 3; round++) { bursts.Add(await MeasureBurst(round)); await Task.Delay(300); }
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
File.WriteAllText(destination, JsonSerializer.Serialize(new
{
    Label = label, Timestamp = DateTimeOffset.Now, Runtime = Environment.Version.ToString(),
    Scope = "Loopback, same process, TLS, enqueue-to-receive callback. No physical input or clipboard APIs. CPU/allocation are whole-process and include benchmark overhead; not visible cursor latency.",
    Measurements = measurements, BurstRounds = bursts, WireMemory = new { Writes = 10000, AllocatedBytes = wireAllocation, SameThread = wireAllocationValid, Scope = "Warm MemoryStream writes, prebuilt input frame; excludes TLS, capture and reader allocations." }
}, new JsonSerializerOptions { WriteIndented = true }));

sealed class Run(int count)
{
    public readonly long[] Sent = new long[count];
    public readonly double[] Milliseconds = new double[count];
    private readonly bool[] seen = new bool[count];
    public readonly TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Received, Duplicates, OrderErrors;
    private long last;
    public void Accept(long sequence, long receivedAt)
    {
        var index = (int)sequence - 1;
        if (index < 0 || index >= Sent.Length || Sent[index] == 0) return;
        if (seen[index]) { Duplicates++; return; }
        seen[index] = true;
        if (sequence <= last) OrderErrors++;
        last = sequence;
        Milliseconds[index] = Stopwatch.GetElapsedTime(Sent[index], receivedAt).TotalMilliseconds;
        if (++Received == Sent.Length) Complete.TrySetResult();
    }
}
record Measurement(string Scenario, int Round, int Sent, int Received, int Duplicates, int OrderErrors, double P50, double P95, double P99, double Maximum, long AllocatedBytes, double CpuMilliseconds, double ElapsedMilliseconds, long SentBytes, int Gen0, int Gen1, int Gen2);
record BurstMeasurement(int Round, int RawEvents, long ReceivedSnapshots, long LastSequence, long SentBytes, double CpuMilliseconds, long AllocatedBytes, double ElapsedMilliseconds);
