using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BorderLess.App;

internal static class Native
{
    public const nuint Marker = 0x424C5353;
    public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] public struct MouseHook { public Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyHook { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct CursorInfo { public uint Size, Flags; public IntPtr Handle; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern short GetKeyState(int key);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)] private static extern uint SendInputOne(uint count, ref Input input, int size);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    public static (int Left, int Top, int Width, int Height) Bounds => (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
    public static void Inject(params Input[] input)
    {
        var accepted = SendInput((uint)input.Length, input, Marshal.SizeOf<Input>());
        if (accepted != input.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows aceptó {accepted} de {input.Length} eventos de entrada. Comprueba los permisos de la ventana de destino.");
    }
    public static void InjectOne(Input input)
    {
        if (SendInputOne(1, ref input, Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows no aceptó el evento de entrada.");
    }
    public static Input MoveTo(int x, int y, (int Left, int Top, int Width, int Height)? bounds = null)
    {
        var b = bounds ?? Bounds;
        return new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = NormalizePixel(x, b.Left, b.Width), Y = NormalizePixel(y, b.Top, b.Height), Flags = 0x8001 | 0x4000, Extra = Marker } } };
    }
    public static int NormalizePixel(int pixel, int origin, int extent)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        var relative = Math.Clamp((long)pixel - origin, 0, extent - 1L);
        // Windows maps a RANGE of normalized coordinates to each physical pixel.
        // Target its center; edge scaling by 65535/(extent-1) can land one pixel
        // above the requested parking point (observed Y=3 for requested Y=4).
        return (int)Math.Clamp((relative * 2 + 1) * 65536 / (extent * 2L), 0, 65535);
    }
    public static Input MouseButton(int button, bool up) => new() { Data = new InputUnion { Mouse = new MouseInput { Flags = button switch { 1 => up ? 0x4u : 0x2u, 2 => up ? 0x10u : 0x8u, 3 => up ? 0x40u : 0x20u, _ => up ? 0x100u : 0x80u }, Data = button >= 4 ? (uint)(button - 3) : 0, Extra = Marker } } };
    public static Input Wheel(int delta, bool horizontal) => new() { Data = new InputUnion { Mouse = new MouseInput { Flags = horizontal ? 0x1000u : 0x800u, Data = unchecked((uint)delta), Extra = Marker } } };
    public static Input Key(int key, int data, bool up)
    {
        data &= KeyboardPacket.NativeMask;
        var virtualKey = KeyboardPacket.IsStateKey(key) || (data & 0xffff) == 0;
        return new Input { Type = 1, Data = new InputUnion { Key = new KeyInput { Key = (ushort)(virtualKey ? key : 0), Scan = (ushort)(data & 0xffff), Flags = (virtualKey ? 0u : 8u) | ((data & 0x10000) != 0 ? 1u : 0u) | (up ? 2u : 0u), Extra = Marker } } };
    }
    public static Rect MonitorAt(Point point)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromPoint(point, 2), ref info)) return info.Monitor;
        var b = Bounds; return new Rect { Left = b.Left, Top = b.Top, Right = b.Left + b.Width, Bottom = b.Top + b.Height };
    }
    public static Point Constrain(Point point)
    {
        var rect = MonitorAt(point);
        return new Point(Math.Clamp(point.X, rect.Left, rect.Right - 1), Math.Clamp(point.Y, rect.Top, rect.Bottom - 1));
    }
}

internal sealed class InputCapture : IDisposable
{
    private readonly Native.HookProc mouseCallback, keyCallback;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private readonly InputRouter router;
    private IntPtr mouse, keyboard;
    private uint threadId;
    private Exception? failure;
    public InputCapture(InputRouter router)
    {
        this.router = router;
        router.RequirePointerParkingAcknowledgement = true;
        mouseCallback = Mouse; keyCallback = Key;
        thread = new Thread(Run) { IsBackground = true, Name = "BorderLess.Input", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Windows no inició la captura de entrada.");
        if (failure != null) throw failure;
    }
    private void Run()
    {
        threadId = Native.GetCurrentThreadId();
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        try
        {
            var module = Native.GetModuleHandle(null);
            mouse = Native.SetWindowsHookEx(14, mouseCallback, module, 0);
            keyboard = Native.SetWindowsHookEx(13, keyCallback, module, 0);
            if (mouse == IntPtr.Zero || keyboard == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudieron instalar los hooks de entrada.");
            ready.Set();
            while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
        }
        catch (Exception ex) { failure = ex; ready.Set(); }
        finally { if (mouse != IntPtr.Zero) Native.UnhookWindowsHookEx(mouse); if (keyboard != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboard); }
    }
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var value = Marshal.PtrToStructure<Native.MouseHook>(data);
            if (value.Extra == Native.Marker)
            {
                if (message.ToInt32() == 0x200) router.AcknowledgePointerParking(value.Point);
            }
            else if ((value.Flags & 1) == 0)
            {
                router.Diagnostics.PhysicalMouse();
                try { if (router.CaptureMouse(message.ToInt32(), value)) return new IntPtr(1); }
                catch { router.EmergencyReturn("Se detuvo la captura para recuperar el control local."); }
            }
            else router.Diagnostics.ForeignMouse();
        }
        return Native.CallNextHookEx(mouse, code, message, data);
    }
    private IntPtr Key(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var value = Marshal.PtrToStructure<Native.KeyHook>(data);
            if ((value.Flags & 0x10) == 0 && value.Extra != Native.Marker)
            {
                router.Diagnostics.PhysicalKey();
                try
                {
                    if (router.CaptureKey(value, message.ToInt32() is 0x101 or 0x105)) return new IntPtr(1);
                }
                catch { router.EmergencyReturn("Se detuvo la captura para recuperar el control local."); }
            }
            else if (value.Extra != Native.Marker) router.Diagnostics.ForeignKey();
        }
        return Native.CallNextHookEx(keyboard, code, message, data);
    }
    public void Dispose() { Native.PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); thread.Join(1500); ready.Dispose(); }
}
