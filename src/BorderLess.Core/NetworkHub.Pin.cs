using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace BorderLess.Core;



public sealed partial class NetworkHub
{
    private sealed record PinGreeting(Guid Offer, Guid Id, string Name, string Fingerprint, int InputRevision, int LayoutRevision, int FileRevision, bool FilesEnabled, DesktopSnapshot? Desktop);
    private sealed record PinContext(string Protocol, string Nonce, PinGreeting Receiver, Hello Sender);
    private sealed record PinDecision(bool Accepted);
    private readonly PinInvitationWindow shortCodes = new();
    public event Action? PinStateChanged;
    public PinInvitationStatus ShortCodeStatus => shortCodes.Status;
    public PinInvitation CreateShortCode()
    {
        if (!Listening) throw new InvalidOperationException("La recepción no está activa.");
        RevokeInvitation(); var code = shortCodes.Create(); PinStateChanged?.Invoke(); return code;
    }
    public void RevokeShortCode() { shortCodes.Revoke(); PinStateChanged?.Invoke(); }
    public bool IsPairingCodeText(string text) => text.StartsWith("BL1.", StringComparison.Ordinal) || shortCodes.IsCodeText(text.Trim());
    private static byte[] Context(string nonce, PinGreeting greeting, Hello hello) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new PinContext("BorderLess-PIN/1/JPAKE-NIST3072-SHA256", nonce, greeting, hello)));
    private void SavePinPeer(PeerCredentials peer)
    {
        Credentials[peer.Id] = peer;
        // Re-pairing can reverse the initiator or update its address/key. Do
        // not retain sessions whose peer metadata belongs to the previous pair.
        if (Inputs.TryRemove(peer.Id, out var input)) input.Close("Vinculación renovada con autorización local.");
        if (Clipboards.TryRemove(peer.Id, out var clipboard)) clipboard.Close("Vinculación renovada.");
        if (Files.TryRemove(peer.Id, out var file)) file.Close("Vinculación renovada.");
        Paired?.Invoke(peer);
    }
    private async Task AcceptPinPairingAsync(TcpClient client, SslStream stream, Hello hello, string nonce)
    {
        using var lease = shortCodes.Begin(hello.PinOffer);
        PinStateChanged?.Invoke();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, lease.Token);
        var remaining = lease.Invitation.Expires - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new AuthenticationException();
        deadline.CancelAfter(remaining < TimeSpan.FromSeconds(100) ? remaining : TimeSpan.FromSeconds(100));
        byte[]? key = null;
        try
        {
            if (!Credentials.ContainsKey(hello.Id) && Credentials.Count >= 12) throw new AuthenticationException("Límite de equipos alcanzado.");
            if (Credentials.TryGetValue(hello.Id, out var known) && known.Fingerprint != hello.Fingerprint) throw new AuthenticationException("Identidad guardada diferente.");
            var layout = Math.Min(hello.LayoutRevision, LocalDesktop == null ? 0 : 1);
            var greeting = new PinGreeting(lease.Invitation.Id, Identity.Id, Identity.Name, Identity.Fingerprint, hello.InputRevision, layout, hello.FileRevision, FilesEnabled, layout > 0 ? LocalDesktop : null);
            await Wire.WriteJsonAsync(stream, greeting, deadline.Token).ConfigureAwait(false);
            key = await PinPake.ExchangeAsync(stream, lease.Invitation.Code, Context(nonce, greeting, hello), true, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            shortCodes.Consume(lease); PinStateChanged?.Invoke();
            // The long-term key is derived from fresh PAKE randomness, not PIN.
            var peer = new PeerCredentials(hello.Id, hello.Name, hello.Fingerprint, key, "", 0, false);
            SavePinPeer(peer); key = null;
            await Wire.WriteJsonAsync(stream, new PinDecision(true), deadline.Token).ConfigureAwait(false);
            await WatchPinConnectionAsync(stream, deadline.Token).ConfigureAwait(false); // authenticated client commit
            Log("Vinculación por PIN completada sin confirmación adicional.");
        }
        finally
        {
            deadline.Cancel();
            if (key != null) CryptographicOperations.ZeroMemory(key);
            PinStateChanged?.Invoke();
        }
    }
    private static async Task WatchPinConnectionAsync(SslStream stream, CancellationToken ct)
    {
        var commit = await Wire.ReadJsonAsync<PinDecision>(stream, ct).ConfigureAwait(false);
        if (!commit.Accepted) throw new AuthenticationException("Vinculación cancelada.");
    }
    public Task<PeerCredentials> PairWithShortCodeAsync(string host, int port, string code, Guid expectedId = default, Guid expectedOffer = default, CancellationToken ct = default)
    {
        var pin = PinPake.Normalize(code);
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsControl) || port is < 1 or > 65535) throw new ArgumentException("Indica una dirección de equipo y un puerto válido.");
        return Task.Run(() => PairPinCoreAsync(host.Trim(), port, pin, expectedId, expectedOffer, ct), ct);
    }
    private async Task<PeerCredentials> PairPinCoreAsync(string host, int port, string pin, Guid expectedId, Guid expectedOffer, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(105));
        using var client = new TcpClient(); byte[]? key = null;
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); connect.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(host, port, connect.Token).ConfigureAwait(false); Tune(client);
            // Trust is established by PAKE, with both actual TLS certificate
            // fingerprints bound into its participant IDs and key derivation.
            using var stream = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "BorderLess", ClientCertificates = new X509CertificateCollection { Identity.Certificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => Identity.Certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, connect.Token).ConfigureAwait(false);
            var challenge = await Wire.ReadJsonAsync<Challenge>(stream, connect.Token).ConfigureAwait(false);
            if (challenge.Version != 1 || challenge.PinRevision != 1) throw new AuthenticationException("Actualiza ambos PCs a BorderLess 0.1.6 para usar 8 dígitos.");
            if (challenge.Nonce?.Length != 44 || challenge.PinOffer == Guid.Empty || challenge.InputRevision is < 1 or > 2 || challenge.LayoutRevision is < 0 or > 1 || challenge.FileRevision is < 0 or > 1 || (expectedOffer != Guid.Empty && challenge.PinOffer != expectedOffer)) throw new AuthenticationException("Genera un código nuevo en el receptor y vuelve a buscarlo.");
            var layout = Math.Min(challenge.LayoutRevision, LocalDesktop == null ? 0 : 1);
            var hello = new Hello(Identity.Id, Identity.Name, Identity.Fingerprint, "pair-pin", true, "", challenge.InputRevision, layout, layout > 0 ? LocalDesktop : null, challenge.FileRevision, FilesEnabled, challenge.PinOffer);
            await Wire.WriteJsonAsync(stream, hello, connect.Token).ConfigureAwait(false);
            var greeting = await Wire.ReadJsonAsync<PinGreeting>(stream, connect.Token).ConfigureAwait(false);
            if (greeting.Id == Guid.Empty || greeting.Id == Identity.Id || (expectedId != Guid.Empty && expectedId != greeting.Id) || greeting.Offer != challenge.PinOffer || string.IsNullOrWhiteSpace(greeting.Name) || greeting.Name.Length > 80 || greeting.Name.Any(char.IsControl) || greeting.Fingerprint != stream.RemoteCertificate?.GetCertHashString(HashAlgorithmName.SHA256) || greeting.InputRevision != hello.InputRevision || greeting.LayoutRevision != layout || greeting.FileRevision != hello.FileRevision) throw new AuthenticationException("El equipo o su identidad no coincide.");
            greeting.Desktop?.Validate();
            if (Credentials.TryGetValue(greeting.Id, out var known) && known.Fingerprint != greeting.Fingerprint) throw new AuthenticationException("Identidad guardada diferente.");
            key = await PinPake.ExchangeAsync(stream, pin, Context(challenge.Nonce, greeting, hello), false, deadline.Token).ConfigureAwait(false);
            var decision = await Wire.ReadJsonAsync<PinDecision>(stream, deadline.Token).ConfigureAwait(false);
            if (!decision.Accepted) throw new AuthenticationException("El otro PC rechazó la solicitud. No se guardó el vínculo.");
            var peer = new PeerCredentials(greeting.Id, greeting.Name, greeting.Fingerprint, key, host, port, true);
            SavePinPeer(peer); key = null;
            await Wire.WriteJsonAsync(stream, new PinDecision(true), deadline.Token).ConfigureAwait(false);
            await ConnectAsync(peer.Id, deadline.Token).ConfigureAwait(false);
            return peer;
        }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
    }
}
