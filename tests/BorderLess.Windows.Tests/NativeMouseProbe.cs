using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BorderLess.App;

internal static class NativeMouseProbe
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private static Native.Input LegacyMove(int x, int y)
    {
        var b = Native.Bounds;
        return new Native.Input { Data = new Native.InputUnion { Mouse = new Native.MouseInput { X = (int)((long)(x - b.Left) * 65535 / Math.Max(1, b.Width - 1)), Y = (int)((long)(y - b.Top) * 65535 / Math.Max(1, b.Height - 1)), Flags = 0xc001, Extra = Native.Marker } } };
    }
    private static Native.Input CenteredMove(int x, int y)
    {
        return Native.MoveTo(x, y);
    }
    public static void Run()
    {
        var thread = new Thread(() =>
        {
            var app = new Application();
            var panel = new StackPanel { Margin = new Thickness(28) };
            var window = new Window { Title = "BorderLess · medición nativa de coordenadas", Width = 830, Height = 590, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel, FontFamily = new FontFamily("Segoe UI"), FontSize = 15 };
            panel.Children.Add(new TextBlock { Text = "Medición de posición del puntero", FontSize = 23, Margin = new Thickness(0, 0, 0, 16) });
            panel.Children.Add(new TextBlock { Text = "La prueba mueve el mouse a puntos controlados de este monitor, incluida la posición de estacionamiento, y restaura su posición. No hace clics, no escribe, no utiliza red ni portapapeles. Durante dos segundos evita mover el mouse.", TextWrapping = TextWrapping.Wrap });
            var button = new Button { Content = "Medir redondeo de coordenadas", Height = 40, Margin = new Thickness(0, 16, 0, 18), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 4, 16, 4) };
            var result = new TextBlock { Text = "Esperando medición.", FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(button); panel.Children.Add(result);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                Native.GetPhysicalCursorPos(out var original);
                var rows = new List<object>(); var log = new List<string>(); var errorsOld = 0; var errorsCenter = 0;
                var handle = new WindowInteropHelper(window).Handle;
                try
                {
                    var basePoint = window.PointToScreen(new System.Windows.Point(110, 220));
                    var monitor = Native.MonitorAt(original);
                    var points = new[] { 0, 1, 2, 4, 7, 10, 15, 21 }.Select(offset => (Kind: "Interior", X: (int)basePoint.X + offset, Y: (int)basePoint.Y + offset)).Append((Kind: "Ancla", X: monitor.Left + (monitor.Right - monitor.Left) / 2, Y: monitor.Top + 4)).ToArray();
                    foreach (var point in points)
                    {
                        if (GetForegroundWindow() != handle) throw new InvalidOperationException("La prueba perdió el foco y se detuvo.");
                        var px = point.X; var py = point.Y;
                        Native.Inject(LegacyMove(px, py)); await Task.Delay(50);
                        Native.GetPhysicalCursorPos(out var oldPoint);
                        Native.InjectOne(CenteredMove(px, py)); await Task.Delay(50);
                        Native.GetPhysicalCursorPos(out var newPoint);
                        var oldBad = oldPoint.X != px || oldPoint.Y != py;
                        var newBad = newPoint.X != px || newPoint.Y != py;
                        if (oldBad) errorsOld++; if (newBad) errorsCenter++;
                        log.Add($"{point.Kind}({px},{py}) anterior({oldPoint.X},{oldPoint.Y}) centro({newPoint.X},{newPoint.Y})");
                        rows.Add(new { point.Kind, Requested = new { X = px, Y = py }, Previous = new { X = oldPoint.X, Y = oldPoint.Y }, Centered = new { X = newPoint.X, Y = newPoint.Y } });
                    }
                    log.Add($"\nDesajustes anteriores: {errorsOld}/{points.Length}; con centro de píxel: {errorsCenter}/{points.Length}.");
                    var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts"); Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "native-mouse-roundtrip.json"), JsonSerializer.Serialize(new { Bounds = Native.Bounds.ToString(), PreviousErrors = errorsOld, CenteredErrors = errorsCenter, Measurements = rows }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { log.Add(ex.Message); }
                finally
                {
                    if (GetForegroundWindow() == handle) Native.Inject(CenteredMove(original.X, original.Y));
                    result.Text = string.Join("\n", log); button.IsEnabled = true;
                }
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
}
