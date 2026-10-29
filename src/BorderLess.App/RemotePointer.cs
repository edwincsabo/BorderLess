using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Diagnostics;
using Microsoft.Win32;

namespace BorderLess.App;

internal static class PointerVisibility
{
    public static bool NeedsFallback(bool receiving, bool enabled, bool cursorKnown, uint flags) => receiving && enabled && cursorKnown && (flags & 1) == 0;
}

// Draws only our remote pointer. It never changes global cursor schemes, display
// counters, Mouse Keys, device drivers, or a corporate security policy.
internal sealed class RemotePointer : IDisposable
{
    private readonly Window window;
    private readonly DispatcherTimer visibilityTimer;
    private readonly DisplayFramePump frames;
    private readonly Dispatcher dispatcher;
    private readonly Action refreshAction;
    private readonly InputRouter router;
    private readonly AppSettings settings;
    private IntPtr handle;
    private int refreshPending;
    private bool disposed, positioned;
    private int lastX, lastY;
    private readonly Image nativeImage = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path backup = new() { Data = Geometry.Parse("M 2,2 L 2,25 L 8,19 L 13,30 L 18,28 L 13,17 L 23,17 Z"), Fill = Brushes.Black, Stroke = Brushes.White, StrokeThickness = 1.7, Stretch = Stretch.None };
    private readonly Dictionary<IntPtr, CursorArtwork?> artwork = new();
    private readonly Queue<IntPtr> cacheOrder = new();
    private long cachedBytes;
    private IntPtr lastCursor;
    private int lastFrame = -1, lastWidth, lastHeight;
    private double lastScale;
    private long animationStart = Stopwatch.GetTimestamp();
    public RemotePointer(InputRouter router, AppSettings settings)
    {
        this.router = router; this.settings = settings;
        dispatcher = System.Windows.Application.Current.Dispatcher;
        window = new Window
        {
            Title = "BorderLess · puntero auxiliar", Width = 26, Height = 34,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            Focusable = false, IsHitTestVisible = false,
            Content = new Grid { Children = { nativeImage, backup } }
        };
        window.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle;
            var style = Native.GetWindowLongPtr(handle, -20).ToInt64();
            Native.SetWindowLongPtr(handle, -20, new IntPtr(style | 0x20 | 0x80 | 0x08000000));
        };
        frames = new DisplayFramePump(Update);
        visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        visibilityTimer.Tick += (_, _) => Update();
        refreshAction = () => { Interlocked.Exchange(ref refreshPending, 0); if (!disposed) Update(); };
        router.Changed += RefreshSettings;
        SystemEvents.UserPreferenceChanged += CursorPreferencesChanged;
        // No cursor polling while this machine is not receiving remote control.
        RefreshSettings();
    }
    public void RefreshSettings()
    {
        if (disposed || Interlocked.Exchange(ref refreshPending, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Input, refreshAction);
    }
    private void CursorPreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (disposed) return;
        dispatcher.BeginInvoke(new Action(() => { if (disposed) return; artwork.Clear(); cacheOrder.Clear(); cachedBytes = 0; lastCursor = IntPtr.Zero; lastFrame = -1; RefreshSettings(); }));
    }
    private void Update()
    {
        if (disposed) return;
        if (router.ControlledBy == null || !settings.PointerFallback)
        {
            frames.Stop(); visibilityTimer.Stop(); positioned = false;
            if (window.IsVisible) window.Hide();
            return;
        }
        var info = new Native.CursorInfo { Size = (uint)Marshal.SizeOf<Native.CursorInfo>() };
        var known = Native.GetCursorInfo(ref info);
        if (!PointerVisibility.NeedsFallback(true, true, known, info.Flags))
        {
            frames.Stop(); positioned = false;
            if (window.IsVisible) window.Hide();
            visibilityTimer.Start(); return;
        }
        visibilityTimer.Stop();
        var cursor = info.Handle == IntPtr.Zero ? NativeCursorImages.Stock(32512) : info.Handle;
        if (!artwork.TryGetValue(cursor, out var art))
        {
            try { art = NativeCursorImages.Capture(cursor); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException or ArgumentException) { art = null; }
            while (cacheOrder.Count > 0 && (cacheOrder.Count >= 24 || cachedBytes + (art?.Bytes ?? 0) > 4 * 1024 * 1024))
            { var key = cacheOrder.Dequeue(); cachedBytes -= artwork[key]?.Bytes ?? 0; artwork.Remove(key); }
            artwork[cursor] = art; cacheOrder.Enqueue(cursor); cachedBytes += art?.Bytes ?? 0;
        }
        if (cursor != lastCursor) { lastCursor = cursor; lastFrame = -1; animationStart = Stopwatch.GetTimestamp(); }
        var width = art?.Width ?? 26; var height = art?.Height ?? 34;
        if (art != null)
        {
            var frame = (int)(Stopwatch.GetElapsedTime(animationStart).TotalMilliseconds / 60) % art.Frames.Length;
            if (frame != lastFrame) { nativeImage.Source = art.Frames[frame]; lastFrame = frame; }
            nativeImage.Visibility = Visibility.Visible; backup.Visibility = Visibility.Collapsed;
        }
        else { nativeImage.Visibility = Visibility.Collapsed; backup.Visibility = Visibility.Visible; }
        if (!window.IsVisible) window.Show();
        var dpi = VisualTreeHelper.GetDpi(window);
        var sizeChanged = lastWidth != width || lastHeight != height || lastScale != dpi.DpiScaleX;
        if (sizeChanged) { window.Width = width / dpi.DpiScaleX; window.Height = height / dpi.DpiScaleY; }
        var x = info.Position.X - (art?.HotX ?? 2); var y = info.Position.Y - (art?.HotY ?? 2);
        if (!positioned || lastX != x || lastY != y || sizeChanged)
        {
            Native.SetWindowPos(handle, new IntPtr(-1), x, y, width, height, 0x0010);
            lastX = x; lastY = y; lastWidth = width; lastHeight = height; lastScale = dpi.DpiScaleX; positioned = true;
        }
        frames.Start();
    }
    public void Dispose() { disposed = true; router.Changed -= RefreshSettings; SystemEvents.UserPreferenceChanged -= CursorPreferencesChanged; frames.Dispose(); visibilityTimer.Stop(); artwork.Clear(); window.Close(); }
}
