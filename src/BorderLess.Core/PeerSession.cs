using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;

namespace BorderLess.Core;

public sealed class PeerSession : IAsyncDisposable
{
    private readonly SslStream stream;
    private readonly TcpClient client;
    private readonly CancellationTokenSource stop = new();
    private long lastRead = Stopwatch.GetTimestamp();
    private long sent, received;
    private int closed;
    public PeerCredentials Peer { get; }
    public string Lane { get; }
    public bool ViaRelay { get; }
    public int InputRevision { get; }
    public int LayoutRevision { get; }
    public int FileRevision { get; }
    public Func<PeerSession, Frame, ValueTask>? FileMessage { get; set; }
    public bool Connected => !stop.IsCancellationRequested;
    public InputOutbox Outbox { get; } = new();
    private readonly Channel<Frame> bulk = Channel.CreateBounded<Frame>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    public double Rtt { get; private set; }
    public long Sent => Interlocked.Read(ref sent);
    public long Received => Interlocked.Read(ref received);
    public event Action<PeerSession, Frame>? Message;
    public event Action<PeerSession, string>? Closed;
    public PeerSession(PeerCredentials peer, string lane, TcpClient client, SslStream stream, bool viaRelay = false, int inputRevision = 1, int layoutRevision = 0, int fileRevision = 0) { Peer = peer; Lane = lane; ViaRelay = viaRelay; InputRevision = inputRevision; LayoutRevision = layoutRevision; FileRevision = fileRevision; this.client = client; this.stream = stream; }
    public bool Send(Frame frame)
    {
        if (!Connected) return false;
        if (Lane != "input") return bulk.Writer.TryWrite(frame);
        if (Outbox.Enqueue(frame)) return true;
        Close("La cola de acciones alcanzó su límite; se liberó el control.");
        return false;
    }
    public async ValueTask SendFileAsync(Frame frame, CancellationToken ct)
    {
        if (Lane != "file" || !Connected) throw new IOException("Canal de archivos desconectado.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        await bulk.Writer.WriteAsync(frame, linked.Token).ConfigureAwait(false);
    }
    public async Task RunAsync()
    {
        try
        {
            var read = ReaderAsync();
            var write = Lane == "input" ? InputWriterAsync() : BulkWriterAsync();
            var monitor = MonitorAsync();
            await Task.WhenAny(read, write, monitor).ConfigureAwait(false);
            // File handlers may be waiting for disk I/O. Notify their owner now
            // so it cancels that I/O before we await the reader's completion.
            if (Lane == "file") Close("Conexión de archivos interrumpida.");
            stop.Cancel(); client.Dispose();
            await Task.WhenAll(read, write, monitor).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Close("Conexión cerrada."); }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or System.Text.Json.JsonException or System.Security.Authentication.AuthenticationException)
        { Close(ex is EndOfStreamException ? "El otro equipo cerró la conexión." : "Se interrumpió la conexión."); }
        finally { Close("Conexión cerrada."); stream.Dispose(); }
    }
    private async Task ReaderAsync()
    {
        var header = new byte[5];
        while (!stop.IsCancellationRequested)
        {
            var frame = await Wire.ReadAsync(stream, stop.Token, Lane == "clipboard" ? Wire.ClipboardLimit * 2 : Lane == "file" ? 65537 : 16384, header).ConfigureAwait(false);
            Interlocked.Exchange(ref lastRead, Stopwatch.GetTimestamp());
            Interlocked.Add(ref received, frame.Payload.Length + 5);
            if (frame.Kind == PacketKind.Ping) Send(new Frame(PacketKind.Pong, frame.Payload, Stopwatch.GetTimestamp()));
            else if (frame.Kind == PacketKind.Pong)
            {
                var stamp = frame.Read<long>();
                var rtt = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                if (rtt >= 0 && rtt < 60000) Rtt = rtt;
            }
            else
            {
                if (Lane == "clipboard" && frame.Kind is not PacketKind.Clipboard and not PacketKind.ClipboardAck) throw new InvalidDataException("Canal incorrecto.");
                if (Lane == "input" && (frame.Kind is PacketKind.Clipboard or PacketKind.ClipboardAck || frame.Kind >= PacketKind.FileOffer)) throw new InvalidDataException("Canal incorrecto.");
                if (Lane == "file")
                {
                    if (frame.Kind < PacketKind.FileOffer || FileMessage == null) throw new InvalidDataException("Canal incorrecto.");
                    await FileMessage(this, frame).ConfigureAwait(false);
                }
                else Message?.Invoke(this, frame);
            }
        }
    }
    private async Task InputWriterAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        while (await Outbox.WaitForDataAsync(stop.Token).ConfigureAwait(false))
        {
            // Notifications are bounded and coalesced, but the reliable queue
            // remains ordered. Drain it completely before waiting again.
            var batch = 0;
            while (Outbox.TryTake(out var frame))
            {
                if (frame.Created != 0 && Stopwatch.GetElapsedTime(frame.Created).TotalMilliseconds > 750) throw new IOException("Entrada caducada.");
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try { await Wire.WriteAsync(stream, frame, timeout.Token).ConfigureAwait(false); }
                finally { timeout.CancelAfter(Timeout.InfiniteTimeSpan); }
                Interlocked.Add(ref sent, frame.Payload.Length + 5);
                if (++batch % 64 == 0) await Task.Yield();
            }
        }
    }
    private async Task BulkWriterAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        await foreach (var frame in bulk.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try { await Wire.WriteAsync(stream, frame, timeout.Token, true).ConfigureAwait(false); }
            finally { timeout.CancelAfter(Timeout.InfiniteTimeSpan); }
            Interlocked.Add(ref sent, frame.Payload.Length + 5);
        }
    }
    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRead)).TotalSeconds > (Lane == "input" ? 5 : 35)) throw new IOException("El otro equipo no responde.");
            Send(Frame.Json(PacketKind.Ping, Stopwatch.GetTimestamp()));
        }
    }
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stop.Cancel(); bulk.Writer.TryComplete(); client.Dispose();
        Closed?.Invoke(this, reason);
    }
    public ValueTask DisposeAsync() { Close("Desconectado por el usuario."); return ValueTask.CompletedTask; }
}
