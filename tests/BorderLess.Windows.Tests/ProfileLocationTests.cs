using System.IO;
using System.Security.Cryptography;
using BorderLess.App;

internal static class ProfileLocationTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "profile-install-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app"); var local = Path.Combine(root, "local"); Directory.CreateDirectory(app);
        check(ProfileLocation.Resolve(app, local, Array.Empty<string>()) == Path.Combine(app, "data"), "Portable mantiene perfil data junto al ejecutable");
        File.WriteAllText(Path.Combine(app, ProfileLocation.InstalledMarker), "1");
        check(ProfileLocation.Resolve(app, local, Array.Empty<string>()) == Path.Combine(local, "BorderLess", "data"), "Instalado separa el perfil de los binarios del programa");
        check(ProfileLocation.Resolve(app, local, new[] { "--data", root }) == root, "Perfil explícito de prueba tiene prioridad sobre modo instalado");
        var source = new SettingsStore(Path.Combine(root, "portable-data")); source.Settings.Alias = "Alias que se conserva"; source.Settings.Language = "en";
        var peerKey = RandomNumberGenerator.GetBytes(32);
        source.Settings.Peers.Add(new SavedPeer { Id = Guid.NewGuid(), Name = "Generated test peer", Alias = "Alias del equipo", Fingerprint = new string('A', 64), ProtectedKey = SecretProtection.Protect(peerKey), Host = "127.0.0.1", Port = 45161 }); source.Save();
        var received = Path.Combine(source.DirectoryPath, "received"); Directory.CreateDirectory(received); var originalFile = Path.Combine(received, "existing.txt"); File.WriteAllText(originalFile, "Generated fixture");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(source.DirectoryPath, "settings.json")));
        var destination = Path.Combine(root, "installed-data"); ProfileLocation.ImportPortable(source.DirectoryPath, destination); var imported = new SettingsStore(destination);
        using var before = source.LoadIdentity().Certificate; using var after = imported.LoadIdentity().Certificate;
        check(before.GetCertHashString() == after.GetCertHashString() && imported.Settings.Id == source.Settings.Id && imported.Settings.Alias == source.Settings.Alias && imported.Settings.Language == "en", "Importar conserva certificado, identidad, alias e idioma del mismo usuario");
        check(imported.Settings.Peers.Single().Id == source.Settings.Peers.Single().Id && imported.Settings.Peers.Single().Alias == "Alias del equipo" && SecretProtection.Unprotect(imported.Settings.Peers.Single().ProtectedKey).SequenceEqual(peerKey), "Importar conserva vínculos y claves protegidas del mismo PC");
        check(imported.ReceiveDirectory == received && File.Exists(originalFile) && !Directory.Exists(Path.Combine(destination, "received")), "Importar no copia ni borra archivos recibidos y conserva su ubicación");
        check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(source.DirectoryPath, "settings.json")))), "Importar no modifica la configuración original portable");
        try { ProfileLocation.ImportPortable(source.DirectoryPath, destination); throw new Exception("Existing profile overwritten"); } catch (InvalidOperationException) { check(true, "Importación no reemplaza un perfil instalado existente"); }
        var invalid = new SettingsStore(Path.Combine(root, "invalid-source")); invalid.Settings.ProtectedCertificate = "Invalid DPAPI fixture"; invalid.Save(); var invalidDestination = Path.Combine(root, "invalid-destination");
        try { ProfileLocation.ImportPortable(invalid.DirectoryPath, invalidDestination); throw new Exception("Invalid identity imported"); } catch (Exception ex) when (ex is CryptographicException or FormatException) { check(!File.Exists(Path.Combine(invalidDestination, "settings.json")), "Identidad inválida se rechaza sin escribir un perfil de destino"); }
        var chosen = ReceiveFolderSelection.ValidateWritable(Path.Combine(root, "chosen"));
        check(Directory.Exists(chosen) && !Directory.EnumerateFiles(chosen).Any(), "Validar carpeta comprueba escritura sin dejar archivos de prueba");
        imported.Settings.ReceiveDirectory = chosen; imported.Save(); var reloaded = new SettingsStore(destination);
        check(reloaded.ReceiveDirectory == chosen && reloaded.Settings.Id == imported.Settings.Id, "Ubicación elegida sobrevive a reinicio sin cambiar identidad");
        imported.Settings.ReceiveDirectory = ""; imported.Save(); check(new SettingsStore(destination).ReceiveDirectory == Path.Combine(destination, "received"), "Restablecer predeterminada usa la carpeta del perfil actual");
    }
}
