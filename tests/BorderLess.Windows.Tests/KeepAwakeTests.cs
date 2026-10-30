using System.ComponentModel;
using BorderLess.App;

internal static class KeepAwakeTests
{
    internal sealed class Lease : IDisposable { public int Releases; public void Dispose() => Releases++; }
    public static void Run(Action<bool, string> check)
    {
        var leases = new List<Lease>();
        var manager = new KeepAwake(() => { var lease = new Lease(); leases.Add(lease); return lease; });
        void Update(bool enabled = true, bool connected = true, bool paused = false, bool locked = false, bool suspended = false, bool closing = false) => manager.Update(enabled, connected, paused, locked, suspended, closing);
        check(!new AppSettings().KeepAwakeWhenConnected, "Mantener activo viene desactivado por defecto");
        Update(enabled: false); Update(connected: false); check(leases.Count == 0, "Preferencia apagada o vínculo guardado sin conexión no solicita energía");
        Update(); for (var i = 0; i < 100; i++) Update();
        check(manager.Active && leases.Count == 1, "Conexión activa adquiere una sola solicitud sin repetir trabajo");
        Update(paused: true); check(!manager.Active && leases[0].Releases == 1, "Pausar libera la solicitud inmediatamente");
        Update(); Update(locked: true); check(!manager.Active && leases[^1].Releases == 1, "Bloquear libera energía aunque los servicios de datos sigan activos");
        Update(); Update(suspended: true); check(!manager.Active && leases[^1].Releases == 1, "Suspender manualmente libera la solicitud");
        Update(); Update(connected: false); check(!manager.Active && leases[^1].Releases == 1, "Desconectar el último equipo libera la solicitud");
        Update(); Update(enabled: false); check(!manager.Active && leases[^1].Releases == 1, "Desmarcar la preferencia libera energía");
        Update(); Update(closing: true); check(!manager.Active && leases[^1].Releases == 1, "Salir libera la solicitud antes del cierre de red");
        Update(); manager.Dispose(); manager.Dispose(); Update(); check(!manager.Active && leases.All(l => l.Releases == 1), "Cierre es idempotente y no permite adquirir otra solicitud");
        var attempts = 0;
        using var failure = new KeepAwake(() => { attempts++; throw new Win32Exception(5); });
        failure.Update(true, true, false, false, false, false); failure.Update(true, true, false, false, false, false);
        check(!failure.Active && attempts == 1 && failure.Status.StartsWith("No disponible"), "Rechazo de Windows muestra estado sin bucle de reintentos");
        failure.Update(false, true, false, false, false, false); failure.Update(true, true, false, false, false, false);
        check(attempts == 2, "Reactivar permite un nuevo intento tras rechazo");
        using (WindowsPowerRequest.Acquire()) check(true, "Windows acepta los tipos nativos de solicitud de pantalla y sistema");
    }
}
