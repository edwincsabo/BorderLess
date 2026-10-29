using System.Runtime.InteropServices;

namespace BorderLess.App;

internal static class DesktopInventory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorEx { public uint Size; public Native.Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Native.Rect rectangle, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorEx info);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
    public static DesktopSnapshot Capture(string label)
    {
        var monitors = new List<DisplayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr dc, ref Native.Rect rect, IntPtr data) =>
        {
            var info = new MonitorEx { Size = (uint)Marshal.SizeOf<MonitorEx>(), Device = "" };
            if (GetMonitorInfo(handle, ref info))
            {
                int? scale = GetScaleFactorForMonitor(handle, out var factor) == 0 && factor is >= 50 and <= 500 ? factor : null;
                monitors.Add(new(info.Device, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0, scale));
            }
            return monitors.Count < 16;
        }, IntPtr.Zero);
        if (monitors.Count == 0)
        {
            var b = Native.Bounds;
            monitors.Add(new("Escritorio Windows", b.Left, b.Top, b.Width, b.Height, true));
        }
        var snapshot = new DesktopSnapshot(monitors.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray(), label.Length > 40 ? label[..40] : label);
        snapshot.Validate(); return snapshot;
    }
}
