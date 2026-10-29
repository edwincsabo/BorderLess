using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BorderLess.Core;

internal static class FileTransferTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        async Task Until(Func<bool> predicate, string name, int ms = 6000)
        { var until = DateTime.UtcNow.AddMilliseconds(ms); while (!predicate() && DateTime.UtcNow < until) await Task.Delay(20); check(predicate(), name); }
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "file-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
        using var ac = NodeIdentity.CreateCertificate(aId); using var bc = NodeIdentity.CreateCertificate(bId);
        await using var a = new NetworkHub(new(aId, "Origen", ac)); await using var b = new NetworkHub(new(bId, "Destino", bc));
        await using var af = new FileTransferService(a, Path.Combine(root, "a")); await using var bf = new FileTransferService(b, Path.Combine(root, "b"));
        a.Start(0); b.Start(0); await a.PairAsync(b.CreateInvitation() with { Host = "127.0.0.1" });
        check(a.Inputs[bId].FileRevision == 1 && b.Inputs[aId].FileRevision == 1, "Capacidad de archivos se negocia en ambos extremos TLS");
        check(!af.Enabled && !bf.Enabled && a.Files.IsEmpty && b.Files.IsEmpty, "Archivos desactivados por defecto no abren canal ni carpeta de recepción");
        try { await a.EnsureFileSessionAsync(bId, CancellationToken.None); throw new Exception("Canal inesperado"); } catch (InvalidOperationException) { check(true, "No se puede abrir el canal con la función desactivada"); }
        af.SetEnabled(true);
        check(!a.CanTransferFiles(bId), "Activar en un solo PC no concede recepción al otro");
        bf.SetEnabled(true); await Until(() => a.CanTransferFiles(bId) && b.CanTransferFiles(aId), "Disponibilidad se propaga sin reconectar entrada");
        var source = Path.Combine(root, "fixture.bin"); var data = RandomNumberGenerator.GetBytes(256 * 1024); await File.WriteAllBytesAsync(source, data);
        var send = af.SendAsync(bId, source);
        await Until(() => bf.Snapshot().Any(t => t.State == TransferState.Waiting), "Oferta aparece antes de escribir contenido en disco");
        check(!Directory.Exists(bf.ReceiveDirectory), "No se crean archivos antes de aceptar explícitamente");
        var first = bf.Snapshot().Single(); await Task.WhenAll(bf.AcceptAsync(first.Id), bf.AcceptAsync(first.Id)); await send.WaitAsync(TimeSpan.FromSeconds(15));
        var saved = bf.Snapshot().Single(t => t.Id == first.Id);
        check(saved.State == TransferState.Completed && SHA256.HashData(await File.ReadAllBytesAsync(saved.SavedPath)).SequenceEqual(SHA256.HashData(data)), "Archivo recibido conserva bytes y SHA-256");
        check(af.Snapshot().Single().State == TransferState.Completed && a.Inputs[bId].Connected && a.Clipboards[bId].Connected, "Confirmación final conserva entrada y portapapeles conectados");
        var changedRoot = Path.Combine(root, "chosen-folder"); bf.SetReceiveDirectory(changedRoot);
        try { bf.SetReceiveDirectory("relative-folder"); throw new Exception("Relative folder accepted"); } catch (ArgumentException) { check(true, "Destino de recepción exige ruta absoluta"); }
        send = af.SendAsync(bId, source); await Until(() => bf.Snapshot().Any(t => t.State == TransferState.Waiting), "Segunda oferta queda pendiente");
        var second = bf.Snapshot().First(t => t.State == TransferState.Waiting); await bf.AcceptAsync(second.Id); await send.WaitAsync(TimeSpan.FromSeconds(15));
        check(bf.Snapshot().First(t => t.Id == second.Id).SavedPath != saved.SavedPath && File.Exists(saved.SavedPath), "Archivos con igual nombre se conservan en carpetas únicas sin sobrescribir");
        check(bf.Snapshot().First(t => t.Id == second.Id).SavedPath.StartsWith(changedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(saved.SavedPath), "Cambiar carpeta redirige la siguiente recepción y conserva archivos anteriores");
        var empty = Path.Combine(root, "empty.txt"); File.WriteAllBytes(empty, Array.Empty<byte>());
        var reverse = bf.SendAsync(aId, empty); await Until(() => af.Snapshot().Any(t => t.Incoming && t.State == TransferState.Waiting), "Equipo sin dirección guardada puede iniciar un envío inverso");
        var reverseOffer = af.Snapshot().First(t => t.Incoming && t.State == TransferState.Waiting); await af.AcceptAsync(reverseOffer.Id); await reverse.WaitAsync(TimeSpan.FromSeconds(15));
        check(af.Snapshot().First(t => t.Id == reverseOffer.Id).State == TransferState.Completed && new FileInfo(af.Snapshot().First(t => t.Id == reverseOffer.Id).SavedPath).Length == 0, "Archivo vacío y transferencia inversa se verifican correctamente");
        send = af.SendAsync(bId, source); await Until(() => bf.Snapshot().Any(t => t.State == TransferState.Waiting), "Oferta para prueba de rechazo recibida");
        var rejected = bf.Snapshot().First(t => t.State == TransferState.Waiting); await bf.RejectAsync(rejected.Id); await send.WaitAsync(TimeSpan.FromSeconds(8));
        check(af.Snapshot().First(t => t.Id == rejected.Id).State == TransferState.Rejected, "Rechazo llega al emisor sin transferir contenido");
        var large = Path.Combine(root, "large-generated.bin"); await File.WriteAllBytesAsync(large, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
        send = af.SendAsync(bId, large); await Until(() => bf.Snapshot().Any(t => t.State == TransferState.Waiting), "Oferta para cancelación recibida");
        var canceled = bf.Snapshot().First(t => t.State == TransferState.Waiting); await bf.AcceptAsync(canceled.Id);
        await Until(() => bf.Snapshot().First(t => t.Id == canceled.Id).Bytes > 0, "Recepción escribe bloques acotados en archivo temporal");
        var duringRoot = Path.Combine(root, "changed-during-receive"); bf.SetReceiveDirectory(duringRoot);
        check(!Directory.Exists(duringRoot) && Directory.EnumerateFiles(changedRoot, "*.partial", SearchOption.AllDirectories).Any(), "Cambiar carpeta durante recepción no mueve ni recrea el parcial activo");
        await bf.CancelAsync(canceled.Id); await send.WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => !Directory.EnumerateFiles(changedRoot, "*.partial", SearchOption.AllDirectories).Any(), "Cancelar elimina el archivo parcial sin borrar archivos completos");
        check(File.Exists(saved.SavedPath) && a.Inputs[bId].Connected, "Cancelar archivos mantiene controles y archivos ya recibidos");
        send = af.SendAsync(bId, large); await Until(() => bf.Snapshot().Any(t => t.State == TransferState.Waiting), "Oferta para deshabilitar recibida");
        var disabled = bf.Snapshot().First(t => t.State == TransferState.Waiting); await bf.AcceptAsync(disabled.Id);
        await Until(() => bf.Snapshot().First(t => t.Id == disabled.Id).Bytes > 0, "Transferencia activa antes de revocar la función");
        bf.SetEnabled(false); await send.WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => a.Files.IsEmpty && b.Files.IsEmpty && !a.CanTransferFiles(bId), "Deshabilitar cierra solo el canal de archivos y revoca disponibilidad");
        await Until(() => !Directory.EnumerateFiles(bf.ReceiveDirectory, "*.partial", SearchOption.AllDirectories).Any(), "Deshabilitar limpia el archivo incompleto");
        bf.SetEnabled(true); await Until(() => a.CanTransferFiles(bId), "Se puede volver a habilitar sin vincular otra vez");
        foreach (var bad in new[] { "../escape.exe", "C:\\bad.exe", "CON.txt", "nul", "name.", "bad\n.txt", "COM1.bin" })
        { try { FileTransferService.ValidateOffer(new(Guid.NewGuid(), bad, 1)); throw new Exception("Nombre aceptado: " + bad); } catch (InvalidDataException) { } }
        check(true, "Nombres con rutas, caracteres de control o dispositivos Windows se rechazan");
        try { FileTransferService.ValidateOffer(new(Guid.NewGuid(), "huge.bin", FileTransferService.MaxFileSize + 1)); throw new Exception("Tamaño admitido"); } catch (InvalidDataException) { check(true, "Se rechazan archivos mayores que 1 GiB antes de recibir contenido"); }
        var lane = await a.EnsureFileSessionAsync(bId, CancellationToken.None);
        var corrupt = Guid.NewGuid(); await lane.SendFileAsync(Frame.Json(PacketKind.FileOffer, new FileOffer(corrupt, "bad-hash.txt", 0)), CancellationToken.None);
        await Until(() => bf.Snapshot().Any(t => t.Id == corrupt), "Oferta sintética para integridad recibida"); await bf.AcceptAsync(corrupt);
        await lane.SendFileAsync(Frame.Json(PacketKind.FileFinish, new FileFinish(corrupt, 0, new string('0', 64))), CancellationToken.None);
        await Until(() => bf.Snapshot().First(t => t.Id == corrupt).State == TransferState.Failed, "SHA-256 incorrecto impide publicar un archivo completo");
        await Until(() => !Directory.EnumerateFiles(bf.ReceiveDirectory, "*.partial", SearchOption.AllDirectories).Any(), "Fallo de integridad limpia datos temporales");
        // Unknown synthetic reply above closes only this file lane on the source.
        await Until(() => a.Files.IsEmpty, "Respuesta ajena a transferencia válida cierra únicamente el canal de archivos");
        check(a.Inputs[bId].Connected, "Mensaje de archivos inválido no interrumpe la sesión de entrada");
        lane = await a.EnsureFileSessionAsync(bId, CancellationToken.None);
        await lane.SendFileAsync(Frame.Json(PacketKind.FileOffer, new FileOffer(Guid.NewGuid(), "../escape", 10)), CancellationToken.None);
        await Until(() => a.Files.IsEmpty && b.Files.IsEmpty, "Oferta con ruta maliciosa se rechaza en el protocolo autenticado");
        check(!File.Exists(Path.Combine(root, "escape")), "El receptor no escribe fuera de su carpeta");
        await using (var relay = new RelayServer())
        {
            relay.Start(0, System.Net.IPAddress.Loopback); a.EnableRelay("127.0.0.1", relay.Port);
            await Until(() => a.RelayWaiting >= 2, "Relay de prueba preparado para tercer canal");
            var cId = Guid.NewGuid(); using var cc = NodeIdentity.CreateCertificate(cId);
            await using var c = new NetworkHub(new(cId, "Emisor por relay", cc));
            await using var cf = new FileTransferService(c, Path.Combine(root, "c")); cf.SetEnabled(true);
            await c.PairAsync(a.CreateInvitation(true));
            var relayed = cf.SendAsync(aId, source); await Until(() => af.Snapshot().Any(t => t.Incoming && t.PeerId == cId && t.State == TransferState.Waiting), "Oferta de archivos llega por relay TLS");
            var offer = af.Snapshot().First(t => t.Incoming && t.PeerId == cId && t.State == TransferState.Waiting); await af.AcceptAsync(offer.Id); await relayed.WaitAsync(TimeSpan.FromSeconds(15));
            check(af.Snapshot().First(t => t.Id == offer.Id).State == TransferState.Completed && c.Files[aId].ViaRelay, "Archivo se completa por el tercer canal cifrado del relay");
            c.Forget(aId); await Until(() => c.Files.IsEmpty && c.Inputs.IsEmpty, "Desvincular cierra el canal de archivos además del control");
            a.DisableRelay();
        }

        // Product implementation, loopback TLS, actual disk I/O, synthetic input
        // packets only: no hooks, SendInput, real files or OS clipboard access.
        var values = new List<double>(); var results = new List<object>(); var stamps = new Dictionary<long, long>(); var sync = new object(); long sequence = 0;
        b.Message += (_, f) => { if (f.Kind != PacketKind.Input) return; lock (sync) { if (stamps.TryGetValue(f.ReadInput().Sequence, out var sent)) values.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds); } };
        async Task Measure(string scenario)
        {
            lock (sync) { values.Clear(); stamps.Clear(); }
            for (var i = 0; i < 200; i++) { var n = ++sequence; lock (sync) stamps[n] = Stopwatch.GetTimestamp(); a.Inputs[bId].Send(Frame.FromInput(new(InputKind.KeyDown, n, aId, 0, 0, 65, 30))); await Task.Delay(7); }
            await Until(() => { lock (sync) return values.Count == 200; }, "Entrada sintética completa durante " + scenario);
            double[] sorted; lock (sync) sorted = values.Order().ToArray();
            results.Add(new { Scenario = scenario, Actions = sorted.Length, P50 = sorted[99], P95 = sorted[189], P99 = sorted[197], Maximum = sorted[^1] });
        }
        await Measure("warmup"); results.Clear();
        for (var round = 0; round < 3; round++) await Measure("without-file");
        var bigger = Path.Combine(root, "performance-generated.bin"); await File.WriteAllBytesAsync(bigger, RandomNumberGenerator.GetBytes(8 * 1024 * 1024));
        send = af.SendAsync(bId, bigger); await Until(() => bf.Snapshot().Any(t => t.State == TransferState.Waiting), "Oferta de rendimiento recibida");
        var perf = bf.Snapshot().First(t => t.State == TransferState.Waiting); var start = Stopwatch.GetTimestamp(); await bf.AcceptAsync(perf.Id);
        for (var round = 0; round < 3; round++) await Measure("with-product-file");
        await send.WaitAsync(TimeSpan.FromSeconds(30)); var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var final = bf.Snapshot().First(t => t.Id == perf.Id);
        var finalHash = SHA256.HashData(await File.ReadAllBytesAsync(final.SavedPath)); var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(bigger));
        check(final.State == TransferState.Completed && finalHash.SequenceEqual(sourceHash), "Archivo real de 8 MiB verificado durante medición de entrada");
        File.WriteAllText(Path.Combine(root, "performance.json"), JsonSerializer.Serialize(new { Scope = "Product file service; loopback TLS; generated files, no Windows input injection. Does not measure physical LAN or visible pointer latency.", MiBPerSecond = 8 / elapsed, Measurements = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("File test artifacts: " + root);
    }
}
