using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace BorderLess.Core;

public sealed record NodeIdentity(Guid Id, string Name, X509Certificate2 Certificate)
{
    public string Fingerprint => Certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public static X509Certificate2 CreateCertificate(Guid id)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=BorderLess-{id:N}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
        // Schannel cannot use EphemeralKeySet. The imported user key is temporary
        // (PersistKeySet is not used) and is removed when the certificate is disposed.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
}

public sealed record PeerCredentials(Guid Id, string Name, string Fingerprint, byte[] Key, string Host, int Port, bool Initiator, string RelayRoom = "");
public sealed record Invitation(int Version, Guid Id, string Name, string Host, int Port, string Fingerprint, string Secret, DateTimeOffset Expires, string RelayRoom = "")
{
    public string Encode() => "BL1." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Invitation Parse(string input)
    {
        if (input.Length > 4096 || !input.Trim().StartsWith("BL1.", StringComparison.Ordinal)) throw new FormatException("El código debe comenzar por BL1. Copia el código completo del otro equipo.");
        var value = input.Trim()[4..].Replace('-', '+').Replace('_', '/');
        value = value.PadRight((value.Length + 3) / 4 * 4, '=');
        Invitation invite;
        try { invite = JsonSerializer.Deserialize<Invitation>(Convert.FromBase64String(value)) ?? throw new FormatException(); }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new FormatException("El código está incompleto o no es válido."); }
        if (invite.Version != 1 || invite.Id == Guid.Empty || string.IsNullOrWhiteSpace(invite.Name) || invite.Name.Length > 80 || invite.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(invite.Host) || invite.Host.Length > 253 || invite.Fingerprint == null || invite.Fingerprint.Length != 64 || !invite.Fingerprint.All(Uri.IsHexDigit) || invite.Secret == null || invite.RelayRoom == null) throw new FormatException("El código no contiene una dirección o identidad válida.");
        try { if (Convert.FromBase64String(invite.Secret).Length != 32) throw new FormatException(); }
        catch (FormatException) { throw new FormatException("La clave del código no es válida."); }
        if (invite.Expires <= DateTimeOffset.UtcNow || invite.Expires > DateTimeOffset.UtcNow.AddMinutes(15)) throw new FormatException("El código venció. Genera uno nuevo en el otro equipo.");
        if (invite.RelayRoom.Length > 100) throw new FormatException("La ruta de internet no es válida.");
        return invite;
    }
}

public enum InputKind : byte { Move = 1, ButtonDown, ButtonUp, Wheel, HorizontalWheel, KeyDown, KeyUp }
public readonly record struct InputEvent(InputKind Kind, long Sequence, Guid Epoch, long X, long Y, int Code = 0, int Data = 0);
public enum PacketKind : byte { Input = 1, RequestControl, GrantControl, DenyControl, ReleaseControl, Boundary, Ping, Pong, Clipboard, ClipboardAck, DesktopInfo, FileAvailability, FileLaneRequest, FileOffer, FileReply, FileChunk, FileFinish, FileReceipt, FileAbort }
public sealed record ControlRequest(Guid Epoch, string Entry, double Y, KeyboardSnapshot? Keyboard = null, double X = .5);
public sealed record ControlReply(Guid Epoch, string Reason = "");
public sealed record BoundaryNotice(Guid Epoch, string Side, double Y, double X = .5);
public sealed record ClipboardPayload(Guid Origin, Guid Change, string Format, string Content);
public sealed record ClipboardReceipt(Guid Change);
public sealed record Diagnostic(DateTimeOffset Time, string Message, bool Error = false);

public static class PeerCrypto
{
    public static byte[] Proof(byte[] key, string nonce, Guid id, string fingerprint, string lane, bool pairing) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"BorderLess/1|{nonce}|{id:D}|{fingerprint}|{lane}|{pairing}"));
    public static byte[] Derive(byte[] secret, Guid host, Guid client, string fingerprint) => HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"BorderLess/1/pair|{host:D}|{client:D}|{fingerprint}"));
    public static string LocalAddress()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType is not System.Net.NetworkInformation.NetworkInterfaceType.Loopback and not System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                .OrderBy(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ? 0 : 1)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(n => n.Address).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString() ?? "127.0.0.1";
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return "127.0.0.1"; }
    }
}
