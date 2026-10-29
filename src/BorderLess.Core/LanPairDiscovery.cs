using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace BorderLess.Core;

public sealed record DiscoveredPairPeer(Guid Id, string Name, string Host, int Port, Guid Offer, DateTimeOffset Expires)
{
    public string Label => $"{Name} · {Host}";
    public override string ToString() => Label;
}

// Discovery is an untrusted address hint only; PAKE authenticates the selected
// endpoint. There is no PIN, key, certificate or account in these datagrams.
public static class LanPairDiscovery
{
    public const int Port = 45163;
    private const string Protocol = "BorderLess-Discover-PIN/1";
    private sealed record Query(string Protocol, string Nonce);
    private sealed record Reply(string Protocol, string Nonce, Guid Id, string Name, int Port, Guid Offer, DateTimeOffset Expires);
    private static byte[] Encode<T>(T data) => JsonSerializer.SerializeToUtf8Bytes(data);
    public static IReadOnlyList<IPEndPoint> BroadcastTargets()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211).Take(8))
                foreach (var ip in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    var bytes = ip.Address.GetAddressBytes(); var mask = ip.IPv4Mask.GetAddressBytes();
                    if (mask.Length == 4 && bytes[0] != 127) addresses.Add(new IPAddress(bytes.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
                }
        }
        catch (NetworkInformationException) { }
        return addresses.Take(9).Select(a => new IPEndPoint(a, Port)).ToArray();
    }
    public static async Task<IReadOnlyList<DiscoveredPairPeer>> SearchAsync(Guid localId, CancellationToken ct, IReadOnlyList<IPEndPoint>? targets = null)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0)); socket.EnableBroadcast = true; socket.Ttl = 1;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)); var query = Encode(new Query(Protocol, nonce));
        var found = new Dictionary<Guid, DiscoveredPairPeer>();
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(ct); duration.CancelAfter(TimeSpan.FromSeconds(2));
        var receive = Task.Run(async () =>
        {
            try
            {
                while (!duration.IsCancellationRequested)
                {
                    var packet = await socket.ReceiveAsync(duration.Token).ConfigureAwait(false);
                    if (packet.Buffer.Length is < 2 or > 768) continue;
                    Reply? value;
                    try { value = JsonSerializer.Deserialize<Reply>(packet.Buffer); } catch (JsonException) { continue; }
                    if (value == null || value.Protocol != Protocol || value.Nonce != nonce || value.Id == Guid.Empty || value.Id == localId || value.Offer == Guid.Empty || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 80 || value.Name.Any(char.IsControl) || value.Port is < 1 or > 65535 || value.Expires <= DateTimeOffset.UtcNow || value.Expires > DateTimeOffset.UtcNow.AddMinutes(3) || found.Count >= 16) continue;
                    found.TryAdd(value.Id, new(value.Id, value.Name, packet.RemoteEndPoint.Address.ToString(), value.Port, value.Offer, value.Expires));
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        try
        {
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var endpoint in (targets ?? BroadcastTargets()).Take(16))
                { try { await socket.SendAsync(query, endpoint, duration.Token).ConfigureAwait(false); } catch (SocketException) { } }
                await Task.Delay(450, duration.Token).ConfigureAwait(false);
            }
            await receive.ConfigureAwait(false);
        }
        finally { duration.Cancel(); await receive.ConfigureAwait(false); }
        ct.ThrowIfCancellationRequested();
        return found.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public sealed class Responder : IAsyncDisposable
    {
        private readonly UdpClient socket;
        private readonly NetworkHub hub;
        private readonly CancellationTokenSource stop = new();
        private readonly Task work;
        public int LocalPort => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        public Responder(NetworkHub hub, int port = Port)
        {
            this.hub = hub;
            socket = new UdpClient(new IPEndPoint(IPAddress.Any, port)); socket.Ttl = 1;
            work = RunAsync();
        }
        private async Task RunAsync()
        {
            var burst = 0; var window = DateTimeOffset.UtcNow;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var packet = await socket.ReceiveAsync(stop.Token).ConfigureAwait(false);
                    var status = hub.ShortCodeStatus;
                    if (!status.Active || packet.Buffer.Length is < 2 or > 192) continue;
                    if (DateTimeOffset.UtcNow - window >= TimeSpan.FromSeconds(1)) { window = DateTimeOffset.UtcNow; burst = 0; }
                    if (++burst > 12) continue;
                    Query? query;
                    try { query = JsonSerializer.Deserialize<Query>(packet.Buffer); } catch (JsonException) { continue; }
                    if (query == null || query.Protocol != Protocol || query.Nonce?.Length != 32 || !query.Nonce.All(Uri.IsHexDigit)) continue;
                    var label = hub.LocalDesktop?.Label;
                    var reply = new Reply(Protocol, query.Nonce, hub.Identity.Id, string.IsNullOrWhiteSpace(label) ? hub.Identity.Name : label, hub.Port, status.Id, status.Expires);
                    var encoded = Encode(reply); if (encoded.Length > 768) continue;
                    try { await socket.SendAsync(encoded, packet.RemoteEndPoint, stop.Token).ConfigureAwait(false); } catch (SocketException) { }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); socket.Dispose(); await work.ConfigureAwait(false); stop.Dispose(); }
    }
}
