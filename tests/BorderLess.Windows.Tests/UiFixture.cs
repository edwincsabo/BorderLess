using System.IO;
using System.Security.Cryptography;
using BorderLess.App;
using BorderLess.Core;

internal static class UiFixture
{
    public static async Task RunAsync(string profile)
    {
        var store = new SettingsStore(profile);
        store.Settings.Name = "VALIDACION-LOCAL"; store.Settings.Alias = "Torre de prueba"; store.Settings.Port = 45174; store.Settings.AutoEdges = false;
        store.Settings.AllowFiles = true;
        var local = store.LoadIdentity(); using var localCertificate = local.Certificate;
        var peerId = Guid.NewGuid(); using var peerCertificate = NodeIdentity.CreateCertificate(peerId);
        var identity = new NodeIdentity(peerId, "SIMULADOR", peerCertificate);
        var key = RandomNumberGenerator.GetBytes(32);
        await using var peer = new NetworkHub(identity);
        await using var files = new FileTransferService(peer, Path.Combine(profile, "fixture-received"));
        files.SetEnabled(true); files.Offered += item => _ = files.AcceptAsync(item.Id);
        peer.UpdateDesktop(new DesktopSnapshot(new[] { new DisplayMonitor("SIMULADA-1", -1920, 0, 1920, 1080, false, 100), new DisplayMonitor("SIMULADA-2", 0, 0, 2560, 1440, true, 125) }, "Prueba · monitores simulados"));
        peer.Credentials[local.Id] = new(local.Id, store.Settings.Name, local.Fingerprint, key, "", 0, false);
        store.Settings.Peers.Clear();
        store.SavePeer(new(peerId, identity.Name, identity.Fingerprint, key, "127.0.0.1", 45175, true));
        store.Save(); peer.Start(45175);
        Console.WriteLine("Fixture local listo; no captura ni inyecta periféricos.");
        var marker = Path.Combine(profile, "stop-fixture");
        var offered = false;
        while (!File.Exists(marker))
        {
            var trigger = Path.Combine(profile, "send-fixture");
            if (!offered && File.Exists(trigger) && peer.CanTransferFiles(local.Id))
            {
                offered = true;
                var generated = Path.Combine(profile, "generated-ui-fixture.txt");
                await File.WriteAllTextAsync(generated, "Archivo generado solo para validar la interfaz de BorderLess.\n");
                _ = files.SendAsync(local.Id, generated);
            }
            await Task.Delay(500);
        }
    }
}
