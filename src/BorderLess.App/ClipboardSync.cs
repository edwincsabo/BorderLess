using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BorderLess.App;

internal sealed class ClipboardSync : IDisposable, IAsyncDisposable
{
    private sealed record Work(long Revision, uint Sequence, ClipboardSnapshot? Snapshot, PeerSession? Session, Frame? Frame);
    private readonly NetworkHub hub;
    private readonly AppSettings settings;
    private readonly InputRouter router;
    private readonly Dispatcher dispatcher;
    private readonly IClipboardAccess access;
    private readonly IClipboardCodec codec;
    private readonly DispatcherTimer debounce;
    private readonly Channel<Work> jobs = Channel.CreateBounded<Work>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<Guid, byte> seen = new();
    private readonly Queue<Guid> seenOrder = new(); // dispatcher-owned
    private readonly Task worker;
    private readonly object jobGate = new();
    private CancellationTokenSource? runningJob;
    private PeerSession? processingSession;
    private HwndSource? source;
    private long revision;
    private uint observedSequence, writtenSequence;
    private int paused;
    private string lastApplied = "", lastSent = "";
    private Guid lastOutgoing;
    private volatile bool disposed;
    public string Status { get; private set; } = "Listo para compartir texto e imágenes";
    public event Action? Changed;

    public ClipboardSync(IntPtr window, NetworkHub hub, AppSettings settings, InputRouter router, Dispatcher dispatcher)
        : this(hub, settings, router, dispatcher, new WindowsClipboardAccess())
    {
        source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("No se pudo registrar el portapapeles.");
        source.AddHook(WindowMessage);
        if (!Native.AddClipboardFormatListener(window)) { Dispose(); throw new InvalidOperationException("Windows no permitió observar el portapapeles."); }
    }
    // A backend is injected only by tests: it observes generated data and never
    // registers with or modifies the user's Windows clipboard.
    internal ClipboardSync(NetworkHub hub, AppSettings settings, InputRouter router, Dispatcher dispatcher, IClipboardAccess access, IClipboardCodec? codec = null)
    {
        dispatcher.VerifyAccess();
        this.hub = hub; this.settings = settings; this.router = router; this.dispatcher = dispatcher; this.access = access; this.codec = codec ?? new ClipboardCodec();
        observedSequence = access.Sequence; paused = router.Paused ? 1 : 0;
        debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => { debounce!.Stop(); Capture(); }, dispatcher); debounce.Stop();
        hub.Message += OnMessage; hub.Disconnected += OnDisconnected; router.Changed += RouterChanged;
        worker = Task.Run(RunAsync);
    }
    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wp, IntPtr lp, ref bool handled)
    { if (message == 0x31d && !disposed) ObserveLocalChange(); return IntPtr.Zero; }
    internal void ObserveLocalChange()
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        var sequence = access.Sequence; Volatile.Write(ref observedSequence, sequence);
        if (sequence != 0 && sequence == writtenSequence) return;
        Advance(); debounce.Stop(); debounce.Start();
    }
    private long Advance()
    {
        var value = Interlocked.Increment(ref revision);
        lock (jobGate) runningJob?.Cancel();
        return value;
    }
    private void RouterChanged()
    {
        var value = router.Paused ? 1 : 0;
        if (Interlocked.Exchange(ref paused, value) != value && value != 0) Advance();
    }
    private void OnDisconnected(PeerSession session, string reason)
    {
        if (session.Lane != "clipboard") return;
        lock (jobGate) if (ReferenceEquals(processingSession, session)) runningJob?.Cancel();
    }
    private void SetStatus(string text) { if (Status == text) return; Status = text; Changed?.Invoke(); }
    public void ShareNow()
    {
        dispatcher.VerifyAccess(); lastSent = lastApplied = ""; Advance(); debounce.Stop(); Capture();
    }
    private void Capture()
    {
        if (disposed || router.Paused || hub.Clipboards.IsEmpty) return;
        try
        {
            ClipboardSnapshot snapshot;
            if (settings.ShareImages && access.ContainsImage())
            {
                var image = access.GetImage();
                if (image == null || image.PixelWidth <= 0 || image.PixelHeight <= 0 || (long)image.PixelWidth * image.PixelHeight > ClipboardCodec.PixelLimit) { SetStatus("La imagen supera el límite de 16 megapíxeles."); return; }
                if (!image.IsFrozen) { if (!image.CanFreeze) { SetStatus("Esta imagen no puede procesarse de forma segura."); return; } image.Freeze(); }
                snapshot = new("png", Image: image);
            }
            else if (settings.ShareText && access.ContainsText())
            {
                var text = access.GetText();
                if (hub.IsPairingCodeText(text)) { SetStatus("Código de vinculación excluido del envío automático"); return; }
                snapshot = new("text", Text: text);
            }
            else return;
            var sequence = access.Sequence; Volatile.Write(ref observedSequence, sequence);
            jobs.Writer.TryWrite(new(Interlocked.Read(ref revision), sequence, snapshot, null, null));
        }
        catch (System.Runtime.InteropServices.COMException) { SetStatus("El portapapeles está ocupado. Vuelve a copiar o pulsa Compartir ahora."); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { SetStatus("No se pudo leer este formato del portapapeles."); }
    }
    private static (Guid Change, string? Format) PeekHeader(Frame frame)
    {
        var change = Guid.Empty; string? format = null;
        try
        {
            var reader = new Utf8JsonReader(frame.Payload.AsSpan(0, Math.Min(1024, frame.Payload.Length)), isFinalBlock: false, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("Content"u8)) break;
                if (reader.ValueTextEquals("Change"u8) && reader.Read() && reader.TokenType == JsonTokenType.String) _ = reader.TryGetGuid(out change);
                else if (reader.ValueTextEquals("Format"u8) && reader.Read() && reader.TokenType == JsonTokenType.String)
                    format = reader.ValueTextEquals("png"u8) ? "png" : reader.ValueTextEquals("text"u8) ? "text" : "unsupported";
            }
        }
        catch (JsonException) { }
        return (change, format);
    }
    private void OnMessage(PeerSession session, Frame frame)
    {
        if (session.Lane != "clipboard" || disposed) return;
        if (frame.Kind == PacketKind.ClipboardAck)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (disposed || router.Paused) return;
                try { var ack = frame.Read<ClipboardReceipt>(); if (ack.Change != Guid.Empty && ack.Change == lastOutgoing) SetStatus($"Portapapeles recibido por {session.Peer.Name}"); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
            })); return;
        }
        if (frame.Kind != PacketKind.Clipboard || Volatile.Read(ref paused) != 0) return;
        var header = PeekHeader(frame);
        if ((header.Change != Guid.Empty && seen.ContainsKey(header.Change)) || (header.Format != null && !Allowed(header.Format))) return;
        var next = Advance();
        jobs.Writer.TryWrite(new(next, Volatile.Read(ref observedSequence), null, session, frame));
    }
    private bool Current(Work work) => !disposed && !stop.IsCancellationRequested && Volatile.Read(ref paused) == 0 && work.Revision == Interlocked.Read(ref revision) && (work.Session == null || work.Session.Connected);
    private bool Allowed(string format) => format == "png" ? settings.ShareImages : format == "text" && settings.ShareText;
    private bool CanCommit(Work work, PreparedClipboard prepared)
    {
        if (!Current(work) || !Allowed(prepared.Payload.Format)) return false;
        var sequence = access.Sequence;
        if (sequence != 0 && work.Sequence != 0 && sequence != work.Sequence && sequence != writtenSequence) { ObserveLocalChange(); return false; }
        return true;
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var work in jobs.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if (!Current(work)) continue;
                using var job = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                lock (jobGate) { if (!Current(work)) continue; runningJob = job; processingSession = work.Session; }
                try
                {
                    var prepared = await Task.Run(() => work.Snapshot != null ? codec.Encode(work.Snapshot, hub.Identity.Id, job.Token) : codec.Decode(work.Frame!.Value, job.Token), job.Token).ConfigureAwait(false);
                    if (!Current(work) || seen.ContainsKey(prepared.Payload.Change)) continue;
                    if (work.Session == null)
                        await dispatcher.InvokeAsync(() => SendPrepared(work, prepared), DispatcherPriority.Background, job.Token).Task.ConfigureAwait(false);
                    else
                        await dispatcher.InvokeAsync(() => ApplyPreparedAsync(work, prepared, job.Token), DispatcherPriority.Background, job.Token).Task.Unwrap().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (job.IsCancellationRequested) { }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException or NotSupportedException or IOException or JsonException or System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    if (Current(work)) await dispatcher.InvokeAsync(() => { if (Current(work)) { SetStatus("No se pudo procesar el portapapeles o supera los límites permitidos."); hub.Log("Se rechazó un portapapeles no válido o fuera de límites.", true); } }, DispatcherPriority.Background, stop.Token).Task.ConfigureAwait(false);
                }
                finally { lock (jobGate) { if (ReferenceEquals(runningJob, job)) { runningJob = null; processingSession = null; } } }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Remember(Guid change)
    {
        if (seen.TryAdd(change, 0)) seenOrder.Enqueue(change);
        while (seenOrder.Count > 256) seen.TryRemove(seenOrder.Dequeue(), out _);
    }
    private static string FormatName(string format) => format == "png" ? "imagen" : "texto";
    private void SendPrepared(Work work, PreparedClipboard prepared)
    {
        if (!CanCommit(work, prepared) || prepared.Hash == lastApplied || prepared.Hash == lastSent || hub.Clipboards.IsEmpty) return;
        var count = hub.Clipboards.Values.Count(s => s.Send(prepared.Frame));
        if (count > 0) { lastSent = prepared.Hash; lastApplied = ""; lastOutgoing = prepared.Payload.Change; Remember(lastOutgoing); }
        SetStatus(count == 0 ? "El canal de portapapeles está ocupado. Reintenta compartir." : $"Enviando {FormatName(prepared.Payload.Format)} a {count} equipo(s)…");
    }
    private async Task ApplyPreparedAsync(Work work, PreparedClipboard prepared, CancellationToken ct)
    {
        var session = work.Session!;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!CanCommit(work, prepared)) return;
            try
            {
                if (prepared.Image != null) access.SetImage(prepared.Image); else access.SetText(prepared.Payload.Content);
                writtenSequence = access.Sequence; Volatile.Write(ref observedSequence, writtenSequence);
                lastApplied = prepared.Hash; lastSent = ""; lastOutgoing = Guid.Empty; Remember(prepared.Payload.Change);
                session.Send(Frame.Json(PacketKind.ClipboardAck, new ClipboardReceipt(prepared.Payload.Change)));
                foreach (var other in hub.Clipboards.Values.Where(s => s.Peer.Id != session.Peer.Id)) other.Send(prepared.Frame);
                SetStatus($"{(prepared.Image != null ? "Imagen recibida" : "Texto recibido")} de {session.Peer.Name}"); return;
            }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(100 + attempt * 100, ct); }
        }
        if (Current(work)) SetStatus("El portapapeles está ocupado. Vuelve a copiar en el otro equipo.");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Advance(); debounce.Stop(); stop.Cancel(); jobs.Writer.TryComplete();
        hub.Message -= OnMessage; hub.Disconnected -= OnDisconnected; router.Changed -= RouterChanged;
        if (source != null) { Native.RemoveClipboardFormatListener(source.Handle); source.RemoveHook(WindowMessage); }
    }
    public async ValueTask DisposeAsync() { Dispose(); await worker.ConfigureAwait(false); }
}
