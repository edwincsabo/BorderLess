using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BorderLess.App;

// Called only after SendInput fails. Normal movement performs no foreground
// process lookup, token query or additional timer work.
internal static class InputFailureInspector
{
    internal static string Format(string pc, int code, int? ownLevel, int? targetLevel, string? program)
    {
        static string Clean(string? value) => new((value ?? "").Where(c => !char.IsControl(c)).Take(40).ToArray());
        var device = Clean(pc); var name = Clean(program);
        if (ownLevel.HasValue && targetLevel.HasValue && targetLevel.Value > ownLevel.Value)
            return $"No se pudo controlar {device}: {(name.Length == 0 ? "la ventana de destino" : name)} tiene permisos superiores a BorderLess. Abre BorderLess como administrador en ese PC y vuelve a solicitar control.";
        return $"Windows rechazó la entrada en {device} (código {code}). Comprueba que esté desbloqueado y revisa los permisos de la aplicación de destino. El control remoto se liberó; esto no es una pausa manual.";
    }
    public static string Describe(string pc, int code)
    {
        int? own = null, target = null; string? name = null;
        try
        {
            using var current = Process.GetCurrentProcess(); own = Integrity(current.Handle);
            var window = GetForegroundWindow();
            if (window != IntPtr.Zero && GetWindowThreadProcessId(window, out var pid) != 0 && pid != 0)
            {
                using var process = OpenProcess(0x1000, false, pid);
                if (!process.IsInvalid) target = Integrity(process.DangerousGetHandle());
                try { using var foreground = Process.GetProcessById(checked((int)pid)); name = foreground.ProcessName; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
        return Format(pc, code, own, target, name);
    }
    private static int? Integrity(IntPtr process)
    {
        if (!OpenProcessToken(process, 8, out var token)) return null;
        using (token)
        {
            _ = GetTokenInformation(token, 25, IntPtr.Zero, 0, out var needed);
            if (needed is < 16 or > 4096) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, needed, out _)) return null;
                var sid = Marshal.ReadIntPtr(buffer); var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr buffer, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
