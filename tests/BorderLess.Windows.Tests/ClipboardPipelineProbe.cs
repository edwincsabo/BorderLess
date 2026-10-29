using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BorderLess.App;
using BorderLess.Core;

internal static class ClipboardPipelineProbe
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () => { try { await ExecuteAsync(dispatcher); } catch (Exception ex) { failure = ex; } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("Clipboard pipeline regression", failure);
    }
    private static async Task ExecuteAsync(Dispatcher dispatcher)
    {
        var passed = 0; var uiThread = Environment.CurrentManagedThreadId;
        void Check(bool test, string text) { if (!test) throw new Exception(text); passed++; Console.WriteLine("PASS " + text); }
        async Task Until(Func<bool> test, string text, int ms = 8000) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (!test() && DateTime.UtcNow < end) await Task.Delay(10); Check(test(), text); }
        var codec = new ClipboardCodec(); var origin = Guid.NewGuid(); var image = Image(64, 64);
        var unicode = new string('a', 8191) + "\U0001f600" + new string('ñ', 11000);
        Check(ClipboardCodec.Hash("text", unicode) == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("text:" + unicode))), "Hash por bloques conserva Unicode y compatibilidad con el formato anterior");
        var prepared = await Task.Run(() => codec.Encode(new("png", Image: image), origin, CancellationToken.None));
        var decoded = await Task.Run(() => codec.Decode(prepared.Frame, CancellationToken.None));
        Check(decoded.Image?.IsFrozen == true && decoded.Image.PixelWidth == 64 && decoded.Image.PixelHeight == 64 && prepared.Hash == decoded.Hash, "PNG se procesa en segundo plano y vuelve como imagen congelada válida");
        var sourcePixels = new byte[64 * 64 * 4]; var targetPixels = new byte[sourcePixels.Length];
        image.CopyPixels(sourcePixels, 64 * 4, 0); decoded.Image!.CopyPixels(targetPixels, 64 * 4, 0);
        Check(sourcePixels.SequenceEqual(targetPixels), "Codificar y decodificar PNG conserva los píxeles del ensayo");
        var hugeHeader = Convert.FromBase64String(prepared.Payload.Content); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hugeHeader.AsSpan(16), uint.MaxValue);
        try { await Task.Run(() => codec.Decode(Frame.Json(PacketKind.Clipboard, prepared.Payload with { Content = Convert.ToBase64String(hugeHeader) }), CancellationToken.None)); throw new Exception("Oversized dimensions accepted"); } catch (InvalidDataException) { Check(true, "Dimensiones PNG fuera de límites se rechazan antes de decodificar"); }
        using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); try { codec.Encode(new("text", Text: "fixture"), origin, cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { Check(true, "Trabajo cancelado no comienza la preparación del mensaje"); } }
        var aId = Guid.NewGuid(); var bId = Guid.NewGuid(); using var ac = NodeIdentity.CreateCertificate(aId); using var bc = NodeIdentity.CreateCertificate(bId);
        await using var a = new NetworkHub(new(aId, "Emisor sintético", ac)); await using var b = new NetworkHub(new(bId, "Receptor sintético", bc));
        var settingsA = new AppSettings { AutoEdges = false }; var settingsB = new AppSettings { AutoEdges = false };
        var ar = new InputRouter(a, settingsA, new FakeDesktop()); var br = new InputRouter(b, settingsB, new FakeDesktop());
        var dataA = new TestClipboard(dispatcher); var dataB = new TestClipboard(dispatcher); var tracking = new TestCodec(codec);
        await using var syncA = new ClipboardSync(a, settingsA, ar, dispatcher, dataA);
        await using var syncB = new ClipboardSync(b, settingsB, br, dispatcher, dataB, tracking);
        dataA.Written = () => dispatcher.BeginInvoke(new Action(syncA.ObserveLocalChange)); dataB.Written = () => dispatcher.BeginInvoke(new Action(syncB.ObserveLocalChange));
        a.Start(0); b.Start(0); await a.PairAsync(b.CreateInvitation() with { Host = "127.0.0.1" });
        dataA.ExternalText("Texto generado A"); syncA.ShareNow();
        await Until(() => dataB.Text == "Texto generado A", "Texto sintético atraviesa TLS y el nuevo proceso de portapapeles");
        var writes = dataB.WriteCount; await Task.Delay(220);
        Check(dataB.WriteCount == writes, "Notificación del propio portapapeles no crea eco ni otro envío");
        dataB.ExternalText("Copia remota intermedia"); syncB.ShareNow();
        await Until(() => dataA.Text == "Copia remota intermedia", "Se recibe una copia intermedia en sentido inverso");
        dataA.ExternalText("Texto generado A"); syncA.ObserveLocalChange();
        await Until(() => dataB.Text == "Texto generado A", "Volver a copiar un valor previamente enviado no se pierde tras recibir otro contenido");
        settingsB.ShareImages = false; var callsBeforeDisabled = tracking.DecodeCalls;
        var disabledFrameSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Message += (_, frame) => { if (frame.Kind == PacketKind.Clipboard && frame.Read<ClipboardPayload>().Change == prepared.Payload.Change) disabledFrameSeen.TrySetResult(); };
        if (!a.Clipboards[bId].Send(prepared.Frame)) throw new IOException("Test channel unexpectedly full");
        await disabledFrameSeen.Task.WaitAsync(TimeSpan.FromSeconds(6)); await Task.Delay(40);
        Check(tracking.DecodeCalls == callsBeforeDisabled && dataB.ImageWrites == 0, "Imágenes deshabilitadas se descartan antes de decodificar"); settingsB.ShareImages = true;
        tracking.BlockNext(); dataA.ExternalImage(image); syncA.ShareNow();
        await Until(() => tracking.Entered.IsSet, "Decodificación lenta de imagen empieza fuera de la interfaz");
        dataA.ExternalText("Texto posterior a imagen"); syncA.ShareNow(); await Task.Delay(180); tracking.Release.Set();
        await Until(() => dataB.Text == "Texto posterior a imagen", "Un texto más reciente prevalece sobre una imagen procesada con retraso");
        Check(dataB.ImageWrites == 0, "La imagen antigua no llega a sobrescribir el texto nuevo");
        tracking.BlockNext(); dataA.ExternalImage(image); syncA.ShareNow(); await Until(() => tracking.Entered.IsSet, "Imagen remota queda en preparación para probar una copia local");
        dataB.ExternalText("Copia local más reciente"); syncB.ObserveLocalChange(); tracking.Release.Set(); await Task.Delay(300);
        Check(dataB.Text == "Copia local más reciente" && dataB.ImageWrites == 0, "Copiar localmente invalida un resultado remoto todavía pendiente");
        tracking.BlockNext(); dataA.ExternalImage(image); syncA.ShareNow(); await Until(() => tracking.Entered.IsSet, "Se prepara una ráfaga con un codec ocupado");
        var burstSeen = 0; var receivedBurst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Message += (_, frame) => { if (frame.Kind == PacketKind.Clipboard && frame.Payload.Length < 1024 && frame.Read<ClipboardPayload>().Content.StartsWith("Ráfaga sintética ", StringComparison.Ordinal) && Interlocked.Increment(ref burstSeen) == 25) receivedBurst.TrySetResult(); };
        for (var i = 1; i <= 25; i++)
        {
            var frame = Frame.Json(PacketKind.Clipboard, new ClipboardPayload(aId, Guid.NewGuid(), "text", "Ráfaga sintética " + i));
            while (!a.Clipboards[bId].Send(frame)) await Task.Delay(5);
        }
        await receivedBurst.Task.WaitAsync(TimeSpan.FromSeconds(5)); var callsAtRelease = tracking.DecodeCalls; tracking.Release.Set();
        await Until(() => dataB.Text == "Ráfaga sintética 25", "La última copia de una ráfaga llega sin aplicar las intermedias");
        Check(tracking.DecodeCalls <= callsAtRelease + 1, "La espera está acotada a un trabajo pendiente en vez de acumular 25 decodificaciones");
        tracking.BlockNext(); dataA.ExternalImage(image); syncA.ShareNow(); await Until(() => tracking.Entered.IsSet, "Se prepara trabajo antes de pausar");
        br.Pause(true); tracking.Release.Set(); await Task.Delay(180);
        Check(dataB.ImageWrites == 0, "Pausar invalida el trabajo pendiente antes de tocar el portapapeles");
        br.Pause(false); await a.ConnectAsync(bId); await Until(() => a.Clipboards.ContainsKey(bId) && b.Clipboards.ContainsKey(aId), "Reanudar recupera el canal para nuevas copias");
        dataB.BusyWrites = 3; var attempts = dataB.Attempts; dataA.ExternalText("Reintento antiguo"); syncA.ShareNow();
        await Until(() => dataB.Attempts > attempts, "Portapapeles ocupado inicia reintento sin bloquear la UI");
        dataA.ExternalText("Reintento vigente"); syncA.ShareNow();
        await Until(() => dataB.Text == "Reintento vigente", "Copia nueva cancela el reintento anterior y acaba aplicada");
        Check(!dataB.Values.Contains("Reintento antiguo"), "Reintento obsoleto nunca se aplica después de la copia nueva");
        dataA.ExternalImage(image); syncA.ShareNow(); await Until(() => dataB.ImageWrites == 1, "Nueva imagen válida sí se aplica después de cancelar trabajos anteriores");
        Check(tracking.Threads.All(id => id != uiThread) && dataA.AccessOnUi && dataB.AccessOnUi, "Codec permanece fuera del dispatcher y backend se usa solo en UI");
        Check(tracking.MaximumConcurrent == 1, "Codificaciones y decodificaciones se procesan con concurrencia acotada a uno");
        tracking.BlockNext(); dataA.ExternalImage(image); syncA.ShareNow(); await Until(() => tracking.Entered.IsSet, "Se prepara trabajo para cierre con procesamiento activo");
        syncB.Dispose(); tracking.Release.Set(); await syncB.DisposeAsync();
        Check(dataB.ImageWrites == 1, "Cerrar descarta un resultado pendiente y espera a que termine el trabajador");

        // Same generated image and codec in both cases, isolating the cost of
        // doing this work on the dispatcher. No system clipboard or input used.
        var large = Image(1024, 1024); var results = new List<object>();
        for (var round = 1; round <= 3; round++)
        {
            var start = Stopwatch.GetTimestamp(); var uiDelay = 0d;
            var probe = dispatcher.InvokeAsync(() => uiDelay = Stopwatch.GetElapsedTime(start).TotalMilliseconds, DispatcherPriority.Background);
            _ = codec.Encode(new("png", Image: large), origin, CancellationToken.None); await probe.Task;
            var blockingDelay = uiDelay;
            var workerStart = Stopwatch.GetTimestamp(); var background = Task.Run(() => codec.Encode(new("png", Image: large), origin, CancellationToken.None));
            await Task.Delay(1); start = Stopwatch.GetTimestamp();
            await dispatcher.InvokeAsync(() => uiDelay = Stopwatch.GetElapsedTime(start).TotalMilliseconds, DispatcherPriority.Background).Task;
            await background; var workerMs = Stopwatch.GetElapsedTime(workerStart).TotalMilliseconds;
            results.Add(new { Round = round, BlockingDispatcherMilliseconds = blockingDelay, BackgroundDispatcherMilliseconds = uiDelay, BackgroundWorkMilliseconds = workerMs });
        }
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "performance", "clipboard-ui-0.1.8.json"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Scope = "Generated 1024x1024 PNG, same codec on UI versus Task.Run. Dispatcher delay only; no user clipboard, physical input or full network latency measurement.", Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{passed} comprobaciones de portapapeles con backend simulado. Medición: {output}");
    }
    private static BitmapSource Image(int width, int height)
    {
        var pixels = RandomNumberGenerator.GetBytes(width * height * 4); for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
    private sealed class TestClipboard(Dispatcher dispatcher) : IClipboardAccess
    {
        private uint sequence = 1; public string? Text; private BitmapSource? image;
        public int WriteCount, ImageWrites, Attempts, BusyWrites;
        public bool AccessOnUi = true;
        public readonly List<string> Values = new();
        public Action? Written;
        private void Verify() { if (!dispatcher.CheckAccess()) { AccessOnUi = false; throw new InvalidOperationException("Backend accessed off dispatcher"); } }
        public uint Sequence { get { Verify(); return sequence; } }
        public bool ContainsImage() { Verify(); return image != null; }
        public bool ContainsText() { Verify(); return Text != null; }
        public BitmapSource? GetImage() { Verify(); return image; }
        public string GetText() { Verify(); return Text ?? ""; }
        public void ExternalText(string value) { Verify(); Text = value; image = null; sequence++; }
        public void ExternalImage(BitmapSource value) { Verify(); image = value; Text = null; sequence++; }
        private void TryWrite() { Verify(); Attempts++; if (BusyWrites > 0) { BusyWrites--; throw new COMException("Synthetic clipboard busy"); } WriteCount++; }
        public void SetText(string value) { TryWrite(); ExternalText(value); Values.Add(value); Written?.Invoke(); }
        public void SetImage(BitmapSource value) { TryWrite(); ExternalImage(value); ImageWrites++; Written?.Invoke(); }
    }
    private sealed class TestCodec(IClipboardCodec inner) : IClipboardCodec
    {
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public readonly ConcurrentBag<int> Threads = new();
        private int block, active;
        public int DecodeCalls;
        public int MaximumConcurrent;
        public void BlockNext() { Entered.Reset(); Release.Reset(); Interlocked.Exchange(ref block, 1); }
        private void Begin()
        {
            Threads.Add(Environment.CurrentManagedThreadId); var count = Interlocked.Increment(ref active); MaximumConcurrent = Math.Max(MaximumConcurrent, count);
            if (Interlocked.Exchange(ref block, 0) != 0) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(12))) throw new TimeoutException("Synthetic codec gate timed out"); }
        }
        public PreparedClipboard Encode(ClipboardSnapshot snapshot, Guid origin, CancellationToken ct) { Begin(); try { return inner.Encode(snapshot, origin, ct); } finally { Interlocked.Decrement(ref active); } }
        public PreparedClipboard Decode(Frame frame, CancellationToken ct) { Interlocked.Increment(ref DecodeCalls); Begin(); try { return inner.Decode(frame, CancellationToken.None); } finally { Interlocked.Decrement(ref active); } }
    }
}
