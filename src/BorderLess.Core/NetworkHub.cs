using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BorderLess.Core;

public sealed partial class NetworkHub : IAsyncDisposable
{
    private sealed record Challenge(int Version, string Nonce, int InputRevision = 1, int LayoutRevision = 0, int FileRevision = 0, int PinRevision = 0, Guid PinOffer = default);
    private sealed record Hello(Guid Id, string Name, string Fingerprint, string Lane, bool Pairing, string Proof, int InputRevision = 1, int LayoutRevision = 0, DesktopSnapshot? Desktop = null, int FileRevision = 0, bool FilesEnabled = false, Guid PinOffer = default);
    private sealed record Welcome(bool Accepted, Guid Id, string Name, string Error = "", int InputRevision = 1, int LayoutRevision = 0, DesktopSnapshot? Desktop = null, int FileRevision = 0, bool FilesEnabled = false);
    public NodeIdentity Identity { get; }
    public ConcurrentDictionary<Guid, PeerCredentials> Credentials { get; } = new();
    public ConcurrentDictionary<Guid, PeerSession> Inputs { get; } = new();
    public ConcurrentDictionary<Guid, PeerSession> Clipboards { get; } = new();
    public ConcurrentDictionary<Guid, DesktopSnapshot> RemoteDesktops { get; } = new();
    private DesktopSnapshot? localDesktop;
    public DesktopSnapshot? LocalDesktop => Volatile.Read(ref localDesktop);
    public event Action<Guid>? DesktopChanged;
    private readonly ConcurrentDictionary<Guid, byte> connecting = new();
    private readonly SemaphoreSlim incoming = new(12, 12);
    private readonly CancellationTokenSource stop = new();
    private TcpListener? listener;
    private Invitation? invitation;
    private string relayHost = "", relayRoom = "";
    private int relayPort;
    private int relayWaiting;
    private volatile bool clipboardPaused;
    private CancellationTokenSource? relayStop;
    public int Port { get; private set; }
    public bool Listening => listener != null;
    public bool RelayEnabled => !string.IsNullOrEmpty(relayRoom);
    public int RelayWaiting => Volatile.Read(ref relayWaiting);
    public event Action<PeerCredentials>? Paired;
    public event Action<PeerSession>? Connected;
    public event Action<PeerSession, string>? Disconnected;
    public event Action<PeerSession, Frame>? Message;
    public event Action<Diagnostic>? Activity;
    public event Action<Exception>? Faulted;
    public NetworkHub(NodeIdentity identity) => Identity = identity;
    public void UpdateDesktop(DesktopSnapshot desktop)
    {
        desktop.Validate();
        var snapshot = desktop with { Monitors = desktop.Monitors.ToArray() };
        if (DesktopSnapshot.Same(LocalDesktop, snapshot)) return;
        Volatile.Write(ref localDesktop, snapshot);
        foreach (var session in Inputs.Values.Where(s => s.LayoutRevision >= 1)) session.Send(Frame.Json(PacketKind.DesktopInfo, snapshot));
    }
    public void Log(string message, bool error = false) => Activity?.Invoke(new(DateTimeOffset.Now, message, error));
    public void Start(int port = 45161)
    {
        if (listener != null) return;
        var candidate = new TcpListener(IPAddress.IPv6Any, port);
        candidate.Server.DualMode = true;
        candidate.Start(16);
        listener = candidate;
        Port = ((IPEndPoint)candidate.LocalEndpoint).Port;
        _ = AcceptLoopAsync(candidate);
        _ = ReconnectLoopAsync();
        Log($"Disponible para conexiones en el puerto {Port}.");
    }
    public Invitation CreateInvitation(bool viaRelay = false)
    {
        if (!Listening) throw new InvalidOperationException("Activa la recepción antes de generar un código.");
        if (viaRelay && string.IsNullOrEmpty(relayRoom)) throw new InvalidOperationException("Configura y activa el relay en Preferencias.");
        invitation = new(1, Identity.Id, Identity.Name, viaRelay ? relayHost : PeerCrypto.LocalAddress(), viaRelay ? relayPort : Port, Identity.Fingerprint, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddMinutes(10), viaRelay ? relayRoom : "");
        Log("Código de vinculación generado. Vence en 10 minutos.");
        return invitation;
    }
    public void RevokeInvitation() => invitation = null;
    private async Task AcceptLoopAsync(TcpListener current)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await current.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                if (!incoming.Wait(0)) { client.Dispose(); continue; }
                _ = AcceptGuardedAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task AcceptGuardedAsync(TcpClient client, bool viaRelay = false)
    {
        try { await AcceptClientAsync(client, viaRelay).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException or FormatException or System.Text.Json.JsonException or ObjectDisposedException)
        { client.Dispose(); Faulted?.Invoke(ex); Log("Se rechazó una conexión que no pudo autenticarse.", true); }
        finally { incoming.Release(); }
    }
    private static void Tune(TcpClient client) { client.NoDelay = true; client.SendBufferSize = 16384; client.ReceiveBufferSize = 16384; }
    private async Task AcceptClientAsync(TcpClient client, bool viaRelay)
    {
        Tune(client);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null);
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = Identity.Certificate, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token).ConfigureAwait(false);
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pinStatus = ShortCodeStatus;
            await Wire.WriteJsonAsync(stream, new Challenge(1, nonce, 2, LocalDesktop == null ? 0 : 1, 1, 1, pinStatus.Active ? pinStatus.Id : Guid.Empty), deadline.Token).ConfigureAwait(false);
            var hello = await Wire.ReadJsonAsync<Hello>(stream, deadline.Token).ConfigureAwait(false);
            if (hello.Id == Guid.Empty || hello.Id == Identity.Id || hello.Name == null || hello.Name.Length is < 1 or > 80 || hello.Name.Any(char.IsControl) || hello.Fingerprint == null || hello.Proof == null || hello.InputRevision is < 1 or > 2 || hello.FileRevision is < 0 or > 1 || hello.Lane is not "input" and not "clipboard" and not "file" and not "pair-pin") throw new AuthenticationException();
            var certificateFingerprint = stream.RemoteCertificate?.GetCertHashString(HashAlgorithmName.SHA256);
            if (hello.LayoutRevision is < 0 or > 1) throw new AuthenticationException();
            hello.Desktop?.Validate();
            if (!string.Equals(certificateFingerprint, hello.Fingerprint, StringComparison.Ordinal)) throw new AuthenticationException();
            if (hello.Lane == "pair-pin")
            {
                if (viaRelay || !hello.Pairing) throw new AuthenticationException("Usa el código avanzado para relay.");
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                await AcceptPinPairingAsync(client, stream, hello, nonce).ConfigureAwait(false);
                stream.Dispose(); client.Dispose(); return;
            }
            byte[] key;
            PeerCredentials peer;
            if (hello.Pairing)
            {
                var active = invitation;
                if (hello.Lane != "input" || active == null || active.Expires <= DateTimeOffset.UtcNow) throw new AuthenticationException();
                key = Convert.FromBase64String(active.Secret);
                peer = new(hello.Id, hello.Name, hello.Fingerprint, PeerCrypto.Derive(key, Identity.Id, hello.Id, hello.Fingerprint), "", 0, false);
            }
            else
            {
                if (!Credentials.TryGetValue(hello.Id, out peer!) || peer.Fingerprint != hello.Fingerprint) throw new AuthenticationException();
                key = peer.Key;
            }
            var proof = Convert.FromBase64String(hello.Proof);
            if (!CryptographicOperations.FixedTimeEquals(PeerCrypto.Proof(key, nonce, hello.Id, hello.Fingerprint, hello.Lane, hello.Pairing), proof)) throw new AuthenticationException();
            if (hello.Lane == "clipboard" && clipboardPaused) { await Wire.WriteJsonAsync(stream, new Welcome(false, Identity.Id, Identity.Name, "Portapapeles pausado"), deadline.Token); stream.Dispose(); client.Dispose(); return; }
            if (hello.Lane == "file" && (!FilesEnabled || hello.FileRevision != 1 || !hello.FilesEnabled || !CanTransferFiles(peer.Id))) throw new AuthenticationException("Transferencia deshabilitada.");
            if (hello.Pairing)
            {
                if (!Credentials.ContainsKey(peer.Id) && Credentials.Count >= 12) throw new AuthenticationException("Límite de equipos alcanzado.");
                Credentials[peer.Id] = peer;
                Paired?.Invoke(peer);
            }
            var layoutRevision = Math.Min(hello.LayoutRevision, LocalDesktop == null ? 0 : 1);
            await Wire.WriteJsonAsync(stream, new Welcome(true, Identity.Id, Identity.Name, InputRevision: hello.InputRevision, LayoutRevision: layoutRevision, Desktop: layoutRevision > 0 ? LocalDesktop : null, FileRevision: hello.FileRevision, FilesEnabled: FilesEnabled), deadline.Token).ConfigureAwait(false);
            Attach(peer, hello.Lane, client, stream, viaRelay, hello.InputRevision, layoutRevision, hello.Desktop, hello.FileRevision, hello.FilesEnabled);
        }
        catch { stream.Dispose(); throw; }
    }
    public async Task<PeerCredentials> PairAsync(Invitation invite, string? hostOverride = null, CancellationToken ct = default)
    {
        if (invite.Id == Identity.Id) throw new InvalidOperationException("Este código pertenece a este equipo. Pégalo en el otro PC.");
        var peer = new PeerCredentials(invite.Id, invite.Name, invite.Fingerprint, Convert.FromBase64String(invite.Secret), string.IsNullOrWhiteSpace(hostOverride) ? invite.Host : hostOverride.Trim(), invite.Port, true, invite.RelayRoom);
        var established = await ConnectLaneAsync(peer, "input", true, ct).ConfigureAwait(false);
        Credentials[established.Id] = established;
        Paired?.Invoke(established);
        try { await ConnectLaneAsync(established, "clipboard", false, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException) { Log("Entrada conectada. Se reintentará el canal de portapapeles.", true); }
        return established;
    }
    public async Task ConnectAsync(Guid id, CancellationToken ct = default)
    {
        if (!Credentials.TryGetValue(id, out var peer) || !peer.Initiator) return;
        if (!connecting.TryAdd(id, 0)) return;
        try
        {
            if (!Inputs.ContainsKey(id)) await ConnectLaneAsync(peer, "input", false, ct).ConfigureAwait(false);
            if (!clipboardPaused && !Clipboards.ContainsKey(id)) await ConnectLaneAsync(peer, "clipboard", false, ct).ConfigureAwait(false);
        }
        finally { connecting.TryRemove(id, out _); }
    }
    private async Task<PeerCredentials> ConnectLaneAsync(PeerCredentials peer, string lane, bool pairing, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var client = new TcpClient();
        SslStream? stream = null;
        try
        {
            await client.ConnectAsync(peer.Host, peer.Port, deadline.Token).ConfigureAwait(false);
            Tune(client);
            if (!string.IsNullOrEmpty(peer.RelayRoom)) await RelayProtocol.JoinAsync(client.GetStream(), "join", peer.RelayRoom, deadline.Token).ConfigureAwait(false);
            stream = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null && cert.GetCertHashString(HashAlgorithmName.SHA256) == peer.Fingerprint);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "BorderLess", ClientCertificates = new X509CertificateCollection { Identity.Certificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => Identity.Certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token).ConfigureAwait(false);
            var challenge = await Wire.ReadJsonAsync<Challenge>(stream, deadline.Token).ConfigureAwait(false);
            if (challenge.Version != 1 || challenge.Nonce == null || challenge.Nonce.Length != 44) throw new AuthenticationException("Versión de protocolo incompatible.");
            if (challenge.InputRevision is < 1 or > 2) throw new AuthenticationException("Revisión de entrada incompatible.");
            var proof = Convert.ToBase64String(PeerCrypto.Proof(peer.Key, challenge.Nonce, Identity.Id, Identity.Fingerprint, lane, pairing));
            if (challenge.LayoutRevision is < 0 or > 1) throw new AuthenticationException();
            if (challenge.FileRevision is < 0 or > 1) throw new AuthenticationException();
            var layoutRevision = Math.Min(challenge.LayoutRevision, LocalDesktop == null ? 0 : 1);
            await Wire.WriteJsonAsync(stream, new Hello(Identity.Id, Identity.Name, Identity.Fingerprint, lane, pairing, proof, challenge.InputRevision, layoutRevision, layoutRevision > 0 ? LocalDesktop : null, challenge.FileRevision, FilesEnabled), deadline.Token).ConfigureAwait(false);
            var welcome = await Wire.ReadJsonAsync<Welcome>(stream, deadline.Token).ConfigureAwait(false);
            if (!welcome.Accepted || welcome.Id != peer.Id || string.IsNullOrWhiteSpace(welcome.Name) || welcome.Name.Length > 80 || welcome.InputRevision is < 1 or > 2 || welcome.InputRevision > challenge.InputRevision) throw new AuthenticationException("El otro equipo rechazó la vinculación.");
            var established = peer with { Name = welcome.Name, Key = pairing ? PeerCrypto.Derive(peer.Key, peer.Id, Identity.Id, Identity.Fingerprint) : peer.Key };
            if (welcome.LayoutRevision is < 0 or > 1 || welcome.LayoutRevision > layoutRevision) throw new AuthenticationException();
            welcome.Desktop?.Validate();
            if (welcome.FileRevision is < 0 or > 1 || welcome.FileRevision > challenge.FileRevision) throw new AuthenticationException();
            if (lane == "file" && (!FilesEnabled || !welcome.FilesEnabled || welcome.FileRevision != 1 || !CanTransferFiles(peer.Id))) throw new AuthenticationException("Transferencia deshabilitada.");
            Attach(established, lane, client, stream, !string.IsNullOrEmpty(peer.RelayRoom), welcome.InputRevision, welcome.LayoutRevision, welcome.Desktop, welcome.FileRevision, welcome.FilesEnabled);
            return established;
        }
        catch { stream?.Dispose(); client.Dispose(); throw; }
    }
    private void Attach(PeerCredentials peer, string lane, TcpClient client, SslStream stream, bool viaRelay = false, int inputRevision = 1, int layoutRevision = 0, DesktopSnapshot? desktop = null, int fileRevision = 0, bool remoteFilesEnabled = false)
    {
        var collection = lane == "input" ? Inputs : lane == "file" ? Files : Clipboards;
        if (collection.TryRemove(peer.Id, out var old)) old.Close("Conexión sustituida por una nueva sesión autenticada.");
        var session = new PeerSession(peer, lane, client, stream, viaRelay, inputRevision, layoutRevision, fileRevision);
        collection[peer.Id] = session;
        if (lane == "input") { RemoteFilesEnabled[peer.Id] = fileRevision > 0 && remoteFilesEnabled; FileAvailabilityChanged?.Invoke(peer.Id); }
        if (lane == "file") session.FileMessage = (s, f) => FileMessage == null ? ValueTask.FromException(new InvalidDataException("Recepción de archivos no disponible.")) : FileMessage(s, f);
        if (lane == "input" && layoutRevision > 0 && desktop != null) RemoteDesktops[peer.Id] = desktop;
        else if (lane == "input") RemoteDesktops.TryRemove(peer.Id, out _);
        session.Message += (s, frame) =>
        {
            if (HandleFileControl(s, frame)) return;
            if (frame.Kind == PacketKind.DesktopInfo)
            {
                if (s.LayoutRevision < 1) throw new InvalidDataException("Información de pantallas no negociada.");
                var changed = frame.Read<DesktopSnapshot>(); changed.Validate();
                if (!DesktopSnapshot.Same(RemoteDesktops.GetValueOrDefault(s.Peer.Id), changed)) { RemoteDesktops[s.Peer.Id] = changed; DesktopChanged?.Invoke(s.Peer.Id); }
            }
            else Message?.Invoke(s, frame);
        };
        session.Closed += (s, reason) =>
        {
            var removed = ((ICollection<KeyValuePair<Guid, PeerSession>>)collection).Remove(new(peer.Id, s));
            Disconnected?.Invoke(s, reason);
            if (lane == "input" && removed) { RemoteFilesEnabled.TryRemove(peer.Id, out _); if (Files.TryRemove(peer.Id, out var file)) file.Close("Canal de entrada desconectado."); if (Clipboards.TryRemove(peer.Id, out var clipboard)) clipboard.Close("Canal de entrada desconectado."); Log($"{peer.Name}: {reason}"); }
        };
        if (lane == "input") Log($"{peer.Name}: conexión cifrada establecida.");
        Connected?.Invoke(session);
        _ = RunSessionAsync(session);
    }
    private async Task RunSessionAsync(PeerSession session)
    {
        try { await session.RunAsync().ConfigureAwait(false); }
        catch (Exception) { session.Close("La sesión se cerró por un mensaje no válido."); }
    }
    private async Task ReconnectLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                foreach (var peer in Credentials.Values.Where(p => p.Initiator))
                {
                    if (stop.IsCancellationRequested) return;
                    if (Inputs.ContainsKey(peer.Id) && Clipboards.ContainsKey(peer.Id)) continue;
                    try { await ConnectAsync(peer.Id, stop.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException or ObjectDisposedException) { }
                }
        }
        catch (OperationCanceledException) { }
    }
    public void Forget(Guid id)
    {
        Credentials.TryRemove(id, out _);
        RemoteDesktops.TryRemove(id, out _);
        if (Inputs.TryRemove(id, out var input)) input.Close("Equipo desvinculado.");
        if (Clipboards.TryRemove(id, out var clip)) clip.Close("Equipo desvinculado.");
        if (Files.TryRemove(id, out var file)) file.Close("Equipo desvinculado.");
        RemoteFilesEnabled.TryRemove(id, out _);
        Log("Se revocó la identidad del equipo.");
    }
    public void EnableRelay(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || port is < 1 or > 65535) throw new ArgumentException("Indica dirección y puerto del relay.");
        relayStop?.Cancel();
        relayHost = host.Trim(); relayPort = port;
        relayRoom = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        relayStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        // Three waiting tunnels permit an input lane, clipboard lane, and reconnection.
        for (var i = 0; i < 3; i++) _ = RelayHostLoopAsync(relayStop.Token);
        Log("Relay activado. Genera un código de internet para vincular.");
    }
    private async Task RelayHostLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = new TcpClient();
            var registered = false;
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct); connect.CancelAfter(10000);
                await client.ConnectAsync(relayHost, relayPort, connect.Token).ConfigureAwait(false);
                Tune(client);
                await RelayProtocol.JoinAsync(client.GetStream(), "host", relayRoom, ct, () => { registered = true; Interlocked.Increment(ref relayWaiting); }).ConfigureAwait(false);
                await incoming.WaitAsync(ct).ConfigureAwait(false);
                _ = AcceptGuardedAsync(client, true);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            { client.Dispose(); try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { } }
            finally { if (registered) Interlocked.Decrement(ref relayWaiting); }
        }
    }
    public void DisableRelay()
    {
        relayStop?.Cancel(); relayRoom = "";
        foreach (var session in Inputs.Values.Concat(Clipboards.Values).Concat(Files.Values).Where(s => s.ViaRelay && !s.Peer.Initiator)) session.Close("Relay desactivado por el receptor.");
        Log("Relay desactivado.");
    }
    public void SetClipboardPaused(bool paused)
    {
        clipboardPaused = paused;
        if (paused) foreach (var session in Clipboards.Values) session.Close("Portapapeles pausado.");
    }
    public ValueTask DisposeAsync()
    {
        stop.Cancel(); relayStop?.Cancel(); listener?.Stop(); listener = null;
        RevokeShortCode();
        foreach (var session in Inputs.Values.Concat(Clipboards.Values).Concat(Files.Values)) session.Close("Aplicación cerrada.");
        return ValueTask.CompletedTask;
    }
}
