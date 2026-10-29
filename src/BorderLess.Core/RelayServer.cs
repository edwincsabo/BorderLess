using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BorderLess.Core;

public sealed class RelayServer : IAsyncDisposable
{
    private sealed record Waiting(TcpClient Client, TaskCompletionSource<TcpClient> Joined);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Waiting>> rooms = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim capacity = new(128, 128);
    private TcpListener? listener;
    public int Port { get; private set; }
    public void Start(int port, IPAddress? bind = null)
    {
        listener = new TcpListener(bind ?? IPAddress.IPv6Any, port);
        if (listener.Server.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = true;
        listener.Start(32); Port = ((IPEndPoint)listener.LocalEndpoint).Port; _ = AcceptLoop();
    }
    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(stop.Token);
                if (!capacity.Wait(0)) { client.Dispose(); continue; }
                _ = Handle(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task Handle(TcpClient client)
    {
        var transferred = false;
        try
        {
            client.NoDelay = true;
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); headerTimeout.CancelAfter(10000);
            var route = await RelayProtocol.ReadAsync(client.GetStream(), headerTimeout.Token);
            if (route.Role == "host")
            {
                var waiting = new Waiting(client, new(TaskCreationOptions.RunContinuationsAsynchronously));
                ConcurrentQueue<Waiting> queue;
                lock (rooms)
                {
                    if (rooms.Count >= 64 && !rooms.ContainsKey(route.Room)) throw new IOException();
                    queue = rooms.GetOrAdd(route.Room, _ => new());
                    if (queue.Count >= 8) throw new IOException();
                    queue.Enqueue(waiting);
                }
                await client.GetStream().WriteAsync(new byte[] { 2 }, stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(55));
                try
                {
                    var guest = await waiting.Joined.Task.WaitAsync(timeout.Token);
                    await Bridge(client, guest, stop.Token);
                }
                catch { waiting.Joined.TrySetCanceled(); throw; }
                finally
                {
                    lock (rooms)
                    {
                        while (queue.TryPeek(out var stale) && stale.Joined.Task.IsCompleted) queue.TryDequeue(out _);
                        if (queue.IsEmpty) ((ICollection<KeyValuePair<string, ConcurrentQueue<Waiting>>>)rooms).Remove(new(route.Room, queue));
                    }
                }
            }
            else
            {
                if (!rooms.TryGetValue(route.Room, out var queue)) throw new IOException();
                while (queue.TryDequeue(out var waiting))
                {
                    if (!waiting.Joined.TrySetResult(client)) continue;
                    transferred = true; return;
                }
                throw new IOException();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException or ObjectDisposedException) { }
        finally { if (!transferred) client.Dispose(); capacity.Release(); }
    }
    private static async Task Bridge(TcpClient host, TcpClient guest, CancellationToken ct)
    {
        using (guest)
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var a = host.GetStream(); var b = guest.GetStream();
            await a.WriteAsync(new byte[] { 1 }, ct); await b.WriteAsync(new byte[] { 1 }, ct);
            var ab = Pump(a, b, cancel.Token); var ba = Pump(b, a, cancel.Token);
            await Task.WhenAny(ab, ba); cancel.Cancel(); host.Dispose(); guest.Dispose();
            try { await Task.WhenAll(ab, ba); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    private static async Task Pump(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = new byte[16384];
        while (true) { var read = await input.ReadAsync(buffer, ct); if (read == 0) return; await output.WriteAsync(buffer.AsMemory(0, read), ct); }
    }
    public ValueTask DisposeAsync() { stop.Cancel(); listener?.Stop(); foreach (var queue in rooms.Values) foreach (var waiting in queue) { waiting.Joined.TrySetCanceled(); waiting.Client.Dispose(); } return ValueTask.CompletedTask; }
}
