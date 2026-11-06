using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using BorderLess.App;
using BorderLess.Core;

internal static class AddressReconnectTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "address-test-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(directory);
        var local = store.LoadIdentity();
        using var localCert = local.Certificate;
        var remoteId = Guid.NewGuid();
        using var remoteCert = NodeIdentity.CreateCertificate(remoteId);
        var remote = new NodeIdentity(remoteId, "Receptor de prueba", remoteCert);
        await using var client = new NetworkHub(local);
        await using var host = new NetworkHub(remote);
        var wrongId = Guid.NewGuid();
        using var wrongCert = NodeIdentity.CreateCertificate(wrongId);
        await using var wrongHost = new NetworkHub(new NodeIdentity(wrongId, "Otro equipo", wrongCert));
        host.Start(0); wrongHost.Start(0);
        var key = RandomNumberGenerator.GetBytes(32);
        var old = new PeerCredentials(remoteId, remote.Name, remote.Fingerprint, key, "old.invalid", 45161, true);
        client.Credentials[remoteId] = old;
        var receiverCredential = new PeerCredentials(local.Id, local.Name, local.Fingerprint, key, "", 0, false);
        host.Credentials[local.Id] = receiverCredential;
        store.SavePeer(old);
        store.Settings.Peers[0].Alias = "Alias de prueba"; store.Settings.Peers[0].Edge = "left"; store.Save();
        var encryptedKey = store.Settings.Peers[0].ProtectedKey;
        async Task ExpectFailure(int port, Action<PeerCredentials> commit)
        {
            try { await client.ReconnectAtAsync(remoteId, "127.0.0.1", port, commit, CancellationToken.None); }
            catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException) { return; }
            throw new Exception("Se esperaba rechazo del candidato.");
        }
        var saves = 0;
        await ExpectFailure(wrongHost.Port, _ => saves++);
        check(saves == 0 && client.Credentials[remoteId] == old, "Certificado de otro equipo se rechaza antes de guardar la dirección");
        host.Credentials[local.Id] = receiverCredential with { Key = RandomNumberGenerator.GetBytes(32) };
        await ExpectFailure(host.Port, _ => saves++);
        check(saves == 0 && client.Credentials[remoteId] == old, "Clave del vínculo incorrecta tampoco autoriza el cambio");
        host.Credentials[local.Id] = receiverCredential;
        await ExpectFailure(host.Port, _ => throw new IOException("Simulación de fallo de escritura"));
        check(client.Credentials[remoteId] == old && !client.Inputs.ContainsKey(remoteId), "Fallo de persistencia conserva la dirección anterior sin activar el canal nuevo");
        await client.ReconnectAtAsync(remoteId, "127.0.0.1", host.Port, store.SaveAddress, CancellationToken.None);
        var saved = new SettingsStore(directory).Settings.Peers.Single();
        check(saved.Host == "127.0.0.1" && saved.Port == host.Port && saved.Alias == "Alias de prueba" && saved.Edge == "left" && saved.ProtectedKey == encryptedKey, "Dirección verificada sobrevive al reinicio conservando alias, borde y clave");
        check(client.Inputs[remoteId].Connected && client.Clipboards[remoteId].Connected, "Reconectar sin PIN restablece entrada y portapapeles");
        var hold = new EdgeHold();
        long At(int ms) => Stopwatch.Frequency * ms / 1000;
        check(!hold.Ready("right", At(0), false) && !hold.Ready("right", At(80), false) && hold.Ready("right", At(160), false), "El cruce normal conserva los 160 ms");
        hold.Reset();
        check(!hold.Ready("left", At(0), true) && !hold.Ready("left", At(79), true) && hold.Ready("left", At(80), true), "El modo rápido espera los 80 ms completos");
        check(!hold.Ready("top", At(81), true) && !hold.Ready("", At(160), true) && !hold.Ready("top", At(161), true), "Salir del borde o cambiar de lado reinicia la espera");
    }
}
