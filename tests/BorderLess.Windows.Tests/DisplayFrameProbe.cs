using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BorderLess.App;

internal static class DisplayFrameProbe
{
    public static void Run()
    {
        var thread = new Thread(() =>
        {
            var app = new Application();
            var panel = new StackPanel { Margin = new Thickness(28) };
            var window = new Window { Title = "BorderLess · cadencia del puntero", Width = 820, Height = 500, Content = panel, FontFamily = new FontFamily("Segoe UI"), FontSize = 15 };
            panel.Children.Add(new TextBlock { Text = "Cadencia visual: temporizador y composición", FontSize = 23, Margin = new Thickness(0, 0, 0, 14) });
            panel.Children.Add(new TextBlock { Text = "Animación sintética de ocho segundos. No cambia el mouse real ni usa conexiones o portapapeles. Mide intervalos de actualización, no latencia visible entre PCs.", TextWrapping = TextWrapping.Wrap });
            var canvas = new Canvas { Height = 110, Background = Brushes.WhiteSmoke, Margin = new Thickness(0, 20, 0, 16), ClipToBounds = true };
            var oldDot = new Ellipse { Width = 16, Height = 16, Fill = Brushes.DarkOrange };
            var newDot = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Color.FromRgb(18, 102, 92)) };
            Canvas.SetTop(oldDot, 20); Canvas.SetTop(newDot, 70); canvas.Children.Add(oldDot); canvas.Children.Add(newDot);
            var button = new Button { Content = "Comparar cadencia", Height = 40, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 4, 16, 4) };
            var result = new TextBlock { Text = "Naranja: 33 ms. Verde: composición de pantalla.", Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(canvas); panel.Children.Add(button); panel.Children.Add(result);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                var oldIntervals = new List<double>(); var newIntervals = new List<double>();
                var start = Stopwatch.GetTimestamp(); long oldLast = 0, newLast = 0;
                var frames = 0;
                void Draw(Ellipse dot, List<double> gaps, ref long last)
                {
                    var now = Stopwatch.GetTimestamp(); var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    Canvas.SetLeft(dot, 20 + (Math.Sin(elapsed * 2) + 1) * Math.Max(0, canvas.ActualWidth - 60) / 2);
                    if (elapsed > 1 && last != 0) gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                    last = now;
                }
                using var pump = new DisplayFramePump(() => { frames++; Draw(newDot, newIntervals, ref newLast); });
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                timer.Tick += (_, _) => Draw(oldDot, oldIntervals, ref oldLast);
                timer.Start(); pump.Start(); pump.Start();
                await Task.Delay(8000);
                timer.Stop(); pump.Stop(); pump.Stop();
                var stoppedAt = frames; await Task.Delay(150);
                static object Stats(List<double> gaps)
                {
                    var sorted = gaps.Order().ToArray();
                    return new { Updates = sorted.Length, MedianMilliseconds = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2], P95Milliseconds = sorted.Length == 0 ? 0 : sorted[(int)Math.Floor((sorted.Length - 1) * .95)], MaximumMilliseconds = sorted.Length == 0 ? 0 : sorted[^1] };
                }
                var oldStats = Stats(oldIntervals); var newStats = Stats(newIntervals);
                var report = new { Scope = "UI scheduler cadence only; simultaneous synthetic animations; first second excluded; not photon latency or real remote pointer.", Timer33 = oldStats, Composition = newStats, StoppedCleanly = stoppedAt == frames };
                var directory = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "performance"); Directory.CreateDirectory(directory);
                File.WriteAllText(System.IO.Path.Combine(directory, "display-cadence.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                result.Text = $"Temporizador: {oldIntervals.Count} actualizaciones; mediana {oldIntervals.Order().ElementAt(oldIntervals.Count / 2):F2} ms.\nComposición: {newIntervals.Count} actualizaciones; mediana {newIntervals.Order().ElementAt(newIntervals.Count / 2):F2} ms.\nDetención correcta: {stoppedAt == frames}.";
                button.IsEnabled = true;
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
}
