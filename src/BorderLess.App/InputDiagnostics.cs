using System.Runtime.InteropServices;

namespace BorderLess.App;

// Counters only: no key codes, typed characters or clipboard payloads are logged.
internal sealed class InputDiagnostics
{
    private long physicalMouse, physicalKey, foreignMouse, foreignKey, outgoing, incoming, modifierRepairs, lockRepairs, injectionFailures, takeovers;
    public void PhysicalMouse() => Interlocked.Increment(ref physicalMouse);
    public void PhysicalKey() => Interlocked.Increment(ref physicalKey);
    public void ForeignMouse() => Interlocked.Increment(ref foreignMouse);
    public void ForeignKey() => Interlocked.Increment(ref foreignKey);
    public void Sent() => Interlocked.Increment(ref outgoing);
    public void Received() => Interlocked.Increment(ref incoming);
    public void ModifierRepair() => Interlocked.Increment(ref modifierRepairs);
    public void LockRepair() => Interlocked.Increment(ref lockRepairs);
    public void InjectionFailure() => Interlocked.Increment(ref injectionFailures);
    public void Takeover() => Interlocked.Increment(ref takeovers);
    public string Export()
    {
        var cursor = new Native.CursorInfo { Size = (uint)Marshal.SizeOf<Native.CursorInfo>() };
        var cursorRead = Native.GetCursorInfo(ref cursor);
        return $"Entrada física observada: mouse={Interlocked.Read(ref physicalMouse)}, teclado={Interlocked.Read(ref physicalKey)}\n" +
            $"Entrada de otros programas ignorada: mouse={Interlocked.Read(ref foreignMouse)}, teclado={Interlocked.Read(ref foreignKey)}\n" +
            $"Eventos enviados={Interlocked.Read(ref outgoing)}, recibidos={Interlocked.Read(ref incoming)}\n" +
            $"Ajustes de modificadores={Interlocked.Read(ref modifierRepairs)}, bloqueos={Interlocked.Read(ref lockRepairs)}\n" +
            $"Fallos de inyección={Interlocked.Read(ref injectionFailures)}, recuperaciones por entrada local={Interlocked.Read(ref takeovers)}\n" +
            $"Windows detecta mouse={Native.GetSystemMetrics(19) != 0}, cursor visible={(cursorRead ? ((cursor.Flags & 1) != 0).ToString() : "desconocido")}\n";
    }
}
