using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BorderLess.App;

// Updated only by session/preference events on the UI dispatcher, never by input packets.
internal sealed class KeepAwake : IDisposable
{
    private readonly Func<IDisposable> acquire;
    private IDisposable? request;
    private bool requested, disposed;
    public string Status { get; private set; } = "Desactivado";
    public bool Active => request != null;
    public KeepAwake(Func<IDisposable>? acquire = null) => this.acquire = acquire ?? WindowsPowerRequest.Acquire;
    public void Update(bool enabled, bool connected, bool paused, bool locked, bool suspended, bool closing)
    {
        var want = enabled && connected && !paused && !locked && !suspended && !closing && !disposed;
        if (!want) { request?.Dispose(); request = null; requested = false; }
        else if (!requested)
        {
            requested = true;
            try { request = acquire(); }
            catch (Win32Exception) { request = null; }
        }
        Status = closing || disposed ? "Desactivado" : !enabled ? "Desactivado" : locked ? "Inactivo · Windows bloqueado" : suspended ? "Inactivo · equipo suspendido" : paused ? "Inactivo · uso compartido en pausa" : !connected ? "En espera · conecta otro equipo" : Active ? "Activo · pantalla y equipo en uso" : "No disponible · Windows rechazó la solicitud de energía";
    }
    public void Dispose() { if (disposed) return; disposed = true; request?.Dispose(); request = null; requested = false; Status = "Desactivado"; }
}

internal sealed class WindowsPowerRequest : IDisposable
{
    private readonly SafeFileHandle handle;
    private bool system, display;
    private WindowsPowerRequest(SafeFileHandle handle) => this.handle = handle;
    public static IDisposable Acquire()
    {
        var reason = Marshal.StringToHGlobalUni(UiLanguage.Text("BorderLess mantiene una conexión activa con otro equipo."));
        try
        {
            var context = new ReasonContext { Version = 0, Flags = 1, Reason = new ReasonUnion { Simple = reason } };
            var handle = PowerCreateRequest(ref context);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            var request = new WindowsPowerRequest(handle);
            try
            {
                if (!PowerSetRequest(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                request.system = true;
                if (!PowerSetRequest(handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                request.display = true;
                return request;
            }
            catch { request.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(reason); }
    }
    public void Dispose()
    {
        if (handle.IsClosed) return;
        if (display) PowerClearRequest(handle, 0);
        if (system) PowerClearRequest(handle, 1);
        display = system = false; handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct DetailedReason { public IntPtr Module; public uint Id, Count; public IntPtr Strings; }
    [StructLayout(LayoutKind.Explicit)] private struct ReasonUnion { [FieldOffset(0)] public IntPtr Simple; [FieldOffset(0)] private DetailedReason detailed; }
    [StructLayout(LayoutKind.Sequential)] private struct ReasonContext { public uint Version, Flags; public ReasonUnion Reason; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerSetRequest(SafeFileHandle handle, int type);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerClearRequest(SafeFileHandle handle, int type);
}
