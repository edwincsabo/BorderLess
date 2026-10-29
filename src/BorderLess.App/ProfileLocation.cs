using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace BorderLess.App;

internal static class ProfileLocation
{
    public const string InstalledMarker = "installed-mode.txt";
    public static string Resolve(string appDirectory, string localData, string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++) if (args[i] == "--data") return Path.GetFullPath(args[i + 1]);
        return File.Exists(Path.Combine(appDirectory, InstalledMarker)) ? Path.Combine(localData, "BorderLess", "data") : Path.Combine(appDirectory, "data");
    }
    public static void ImportPortable(string sourceDirectory, string destinationDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory); var destination = Path.GetFullPath(destinationDirectory);
        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), destination.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("La carpeta de origen y destino deben ser diferentes.");
        var sourceFile = Path.Combine(source, "settings.json"); var destinationFile = Path.Combine(destination, "settings.json");
        if (File.Exists(destinationFile)) throw new InvalidOperationException("Ya existe una configuración instalada. Se conservó sin reemplazarla.");
        var info = new FileInfo(sourceFile); if (!info.Exists || info.Length > 2 * 1024 * 1024) throw new InvalidDataException("Elige la carpeta data de la versión portable de este PC.");
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(sourceFile)) ?? throw new InvalidDataException();
        if (settings.Id == Guid.Empty || string.IsNullOrWhiteSpace(settings.Name) || settings.Port is < 1 or > 65535 || settings.Peers == null || settings.Peers.Count > 12 || string.IsNullOrEmpty(settings.ProtectedCertificate)) throw new InvalidDataException("La configuración portable no es válida.");
        // Validate DPAPI for the current user before writing any destination
        // profile. Import never copies received files or edits the source.
        var pfx = SecretProtection.Unprotect(settings.ProtectedCertificate);
        try { using var cert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet); if (!cert.HasPrivateKey) throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        foreach (var peer in settings.Peers)
        {
            if (peer == null || string.IsNullOrEmpty(peer.ProtectedKey) || peer.Fingerprint == null || peer.Fingerprint.Length != 64 || !peer.Fingerprint.All(Uri.IsHexDigit)) throw new InvalidDataException("La credencial de un equipo vinculado no es válida.");
            var key = SecretProtection.Unprotect(peer.ProtectedKey);
            try { if (key.Length != 32 || peer.Id == Guid.Empty) throw new InvalidDataException(); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        if (string.IsNullOrWhiteSpace(settings.ReceiveDirectory)) settings.ReceiveDirectory = Path.Combine(source, "received");
        else if (!Path.IsPathFullyQualified(settings.ReceiveDirectory)) throw new InvalidDataException("La carpeta de recepción debe ser una ruta absoluta.");
        Directory.CreateDirectory(destination);
        var temporary = Path.Combine(destination, "import-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, destinationFile, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
