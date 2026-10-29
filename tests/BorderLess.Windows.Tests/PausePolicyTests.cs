using System.IO;
using BorderLess.App;
using BorderLess.Core;

internal static class PausePolicyTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        async Task Until(Func<bool> test, string text) { var end = DateTime.UtcNow.AddSeconds(6); while (!test() && DateTime.UtcNow < end) await Task.Delay(20); check(test(), text); }
        var policy = new SharingPauseState(true);
        check(!policy.Paused && !policy.Locked, "Inicio no está pausado y conserva política de bloqueo activada");
        policy.SetLocked(true); check(policy.Paused && policy.Automatic && !policy.Manual, "Bloqueo activa una causa automática independiente");
        policy.SetManual(false); check(policy.Paused, "Reanudar manualmente no borra una causa de bloqueo activa");
        policy.SetLocked(false); check(!policy.Paused, "Desbloquear reanuda una pausa exclusivamente automática");
        policy.SetManual(true); policy.SetLocked(true); policy.SetLocked(false); check(policy.Paused && policy.Manual, "Desbloquear conserva una pausa manual previa");
        policy.SetManual(false); policy.SetLocked(true); policy.SetPolicy(false); check(policy.Locked && !policy.Paused, "Desactivar política no convierte una sesión bloqueada en escritorio accesible");
        policy.SetManual(true); check(policy.Paused, "Pausa manual funciona también con pausa por bloqueo desactivada");
        policy.SetManual(false); policy.SetPolicy(true); check(policy.Paused, "Activar política durante bloqueo aplica la pausa");

        var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
        using var ac = NodeIdentity.CreateCertificate(aId); using var bc = NodeIdentity.CreateCertificate(bId);
        await using var a = new NetworkHub(new(aId, "Origen de prueba", ac)); await using var b = new NetworkHub(new(bId, "Destino de prueba", bc));
        var ad = new FakeDesktop(); var bd = new FakeDesktop(); var settings = new AppSettings { AllowFiles = true, AutoEdges = false };
        var ar = new InputRouter(a, new AppSettings { AutoEdges = false }, ad); var br = new InputRouter(b, settings, bd);
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "lock-policy-" + Guid.NewGuid().ToString("N"));
        await using var files = new FileTransferService(b, Path.Combine(folder, "received"));
        br.Changed += () => files.SetEnabled(settings.AllowFiles && !br.Paused); files.SetEnabled(true);
        a.Start(0); b.Start(0); await a.PairAsync(b.CreateInvitation() with { Host = "127.0.0.1" });
        ar.Request(a.Inputs[bId]); await Until(() => ar.ActivePeer == bId && br.ControlledBy == aId, "Se prepara control antes del bloqueo simulado");
        ar.CaptureKey(new Native.KeyHook { Key = 65, Scan = 30 }, false); ar.CaptureMouse(0x201, new Native.MouseHook());
        await Until(() => !bd.HeldKeys.IsEmpty && !bd.HeldButtons.IsEmpty, "Tecla y botón remotos estaban retenidos antes de bloquear");
        br.SetSessionLocked(true);
        await Until(() => ar.ActivePeer == null && br.ControlledBy == null && bd.HeldKeys.IsEmpty && bd.HeldButtons.IsEmpty, "Bloquear libera el control y toda entrada retenida en ambos extremos");
        check(br.Paused && !br.ManualPause && !files.Enabled, "Política activada pausa datos y archivos sin marcar pausa manual");
        await Until(() => a.Clipboards.IsEmpty && b.Clipboards.IsEmpty, "Pausa por bloqueo cierra el canal de portapapeles");
        br.SetSessionLocked(false); check(!br.Paused && files.Enabled, "Desbloqueo reactiva servicios permitidos automáticamente");
        await a.ConnectAsync(bId); await Until(() => a.Clipboards.ContainsKey(bId) && b.Clipboards.ContainsKey(aId), "Desbloqueo permite recuperar portapapeles sin vincular otra vez");
        ar.CaptureKey(new Native.KeyHook { Key = 65, Scan = 30 }, true);
        settings.PauseOnLock = false; br.SetPauseOnLock(false); var clipboard = b.Clipboards[aId];
        ar.Request(a.Inputs[bId]); await Until(() => br.ControlledBy == aId, "Se prepara control con pausa al bloquear desactivada");
        br.SetSessionLocked(true);
        await Until(() => ar.ActivePeer == null && br.ControlledBy == null, "Con opción desactivada se libera igualmente el control remoto al bloquear");
        check(!br.Paused && files.Enabled && b.Clipboards.TryGetValue(aId, out var retained) && ReferenceEquals(retained, clipboard) && clipboard.Connected, "Opción desactivada conserva servicios y la misma sesión de portapapeles");
        ar.Request(a.Inputs[bId]); await Until(() => ar.ActivePeer == null && ar.Status.Contains("Windows está bloqueado", StringComparison.Ordinal), "PC bloqueado rechaza nuevo control aunque sus datos sigan activos");
        check(br.SessionLocked && !br.CaptureMouse(0x200, new Native.MouseHook()) && !br.CaptureKey(new Native.KeyHook { Key = 65, Scan = 30 }, false), "Captura no crea control desde una sesión bloqueada");
        br.SetSessionLocked(false); br.Pause(true); br.SetSessionLocked(true); br.SetSessionLocked(false);
        check(br.Paused && br.ManualPause && !files.Enabled, "Pausa manual sobrevive a bloquear y desbloquear con política desactivada");
        br.Pause(false); check(!br.Paused && files.Enabled, "Reanudar manualmente devuelve servicios sin recrear el vínculo");
        var store = new SettingsStore(folder); check(store.Settings.PauseOnLock, "Perfiles nuevos activan por defecto la pausa al bloquear");
        store.Settings.PauseOnLock = false; store.Save(); var reloaded = new SettingsStore(folder);
        check(!reloaded.Settings.PauseOnLock && reloaded.Settings.Id == store.Settings.Id, "Preferencia desactivada persiste sin cambiar identidad");
    }
}
