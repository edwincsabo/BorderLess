using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace BorderLess.App;

public sealed class AppSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = Environment.MachineName;
    public string Alias { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => string.IsNullOrEmpty(Alias) ? Name : Alias;
    public string ProtectedCertificate { get; set; } = "";
    public int Port { get; set; } = 45161;
    public bool ShareText { get; set; } = true;
    public bool ShareImages { get; set; } = true;
    public bool AllowFiles { get; set; }
    public bool DarkMode { get; set; }
    public bool PauseOnLock { get; set; } = true;
    public bool KeepAwakeWhenConnected { get; set; }
    public string Language { get; set; } = "es";
    public string ReceiveDirectory { get; set; } = "";
    public bool AllowControl { get; set; } = true;
    public bool PointerFallback { get; set; } = true;
    public bool AutoEdges { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public string RelayHost { get; set; } = "";
    public int RelayPort { get; set; } = 45162;
    public List<SavedPeer> Peers { get; set; } = new();
}
public sealed class SavedPeer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool Initiator { get; set; }
    public string RelayRoom { get; set; } = "";
    public string Edge { get; set; } = "none";
    public string Alias { get; set; } = "";
    public DesktopSnapshot? LastDesktop { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => !string.IsNullOrEmpty(Alias) ? Alias : !string.IsNullOrWhiteSpace(LastDesktop?.Label) ? LastDesktop.Label : Name;
    public PeerCredentials Credentials() => new(Id, Name, Fingerprint, SecretProtection.Unprotect(ProtectedKey), Host, Port, Initiator, RelayRoom);
}
public sealed class SettingsStore
{
    public string DirectoryPath { get; }
    public AppSettings Settings { get; }
    public string ReceiveDirectory => string.IsNullOrWhiteSpace(Settings.ReceiveDirectory) ? Path.Combine(DirectoryPath, "received") : Path.IsPathFullyQualified(Settings.ReceiveDirectory) ? Path.GetFullPath(Settings.ReceiveDirectory) : throw new InvalidDataException("La carpeta de recepción debe ser una ruta absoluta.");
    private readonly object gate = new();
    public SettingsStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "settings.json");
        try
        {
            Settings = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new JsonException() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { throw new InvalidDataException("No se pudieron leer las preferencias. Se conservó el archivo original en: " + path, ex); }
        if (Settings.Id == Guid.Empty || Settings.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(Settings.Name) || Settings.Peers == null) throw new InvalidDataException("La configuración no es válida. El archivo original se conservó.");
        Settings.Alias ??= "";
        foreach (var peer in Settings.Peers)
        {
            peer.Alias ??= "";
            if (!PlacementRules.Edges.Contains(peer.Edge)) peer.Edge = "none";
            if (peer.LastDesktop != null) { try { peer.LastDesktop.Validate(); } catch (InvalidDataException) { peer.LastDesktop = null; } }
        }
        if (string.IsNullOrEmpty(Settings.ProtectedCertificate))
        {
            using var certificate = NodeIdentity.CreateCertificate(Settings.Id);
            Settings.ProtectedCertificate = SecretProtection.Protect(certificate.Export(X509ContentType.Pfx));
            Save();
        }
    }
    public NodeIdentity LoadIdentity() => new(Settings.Id, Settings.Name, X509CertificateLoader.LoadPkcs12(SecretProtection.Unprotect(Settings.ProtectedCertificate), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet));
    public void Save()
    {
        lock (gate)
        {
            var path = Path.Combine(DirectoryPath, "settings.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
    }
    public void SavePeer(PeerCredentials peer)
    {
        lock (gate)
        {
            var previous = Settings.Peers.Find(p => p.Id == peer.Id);
            var edge = previous?.Edge ?? (Settings.Peers.All(p => p.Edge != "right") ? "right" : "none");
            Settings.Peers.RemoveAll(p => p.Id == peer.Id);
            Settings.Peers.Add(new SavedPeer { Id = peer.Id, Name = peer.Name, Fingerprint = peer.Fingerprint, ProtectedKey = SecretProtection.Protect(peer.Key), Host = peer.Host, Port = peer.Port, Initiator = peer.Initiator, RelayRoom = peer.RelayRoom, Edge = edge, Alias = previous?.Alias ?? "", LastDesktop = previous?.LastDesktop });
            Save();
        }
    }
    public void SetAlias(Guid id, string alias)
    {
        alias = alias.Trim();
        if (alias.Length > 40 || alias.Any(char.IsControl)) throw new ArgumentException("Usa hasta 40 caracteres y evita saltos de línea.");
        lock (gate)
        {
            if (id == Settings.Id) Settings.Alias = alias;
            else { var peer = Settings.Peers.Find(p => p.Id == id) ?? throw new ArgumentException("El equipo ya no está vinculado."); peer.Alias = alias; }
            Save();
        }
    }
}

internal static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string Protect(byte[] bytes) => Convert.ToBase64String(Transform(bytes, true));
    public static byte[] Unprotect(string text) => Transform(Convert.FromBase64String(text), false);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect ? CryptProtectData(ref input, "BorderLess", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows no pudo proteger o abrir las credenciales de este usuario.");
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
