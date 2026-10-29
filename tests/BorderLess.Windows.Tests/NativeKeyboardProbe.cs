using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BorderLess.App;
using BorderLess.Core;
using System.Runtime.InteropServices;

internal static class NativeKeyboardProbe
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static void Run()
    {
        var thread = new Thread(() =>
        {
            var app = new Application();
            var panel = new StackPanel { Margin = new Thickness(28) };
            var window = new Window { Title = "BorderLess · prueba nativa de teclado", Width = 680, Height = 460, Content = panel, FontFamily = new FontFamily("Segoe UI"), FontSize = 15 };
            panel.Children.Add(new TextBlock { Text = "Prueba local de Shift y Bloq Mayús", FontSize = 23, Margin = new Thickness(0, 0, 0, 16) });
            panel.Children.Add(new TextBlock { Text = "Solo se escribe en el campo de esta ventana. No hay conexiones de red ni lectura del portapapeles. El estado inicial de Bloq Mayús se conserva en cada lote.", TextWrapping = TextWrapping.Wrap });
            var entry = new TextBox { Height = 44, Margin = new Thickness(0, 20, 0, 16), IsReadOnly = true };
            var button = new Button { Content = "Iniciar prueba local", Height = 40, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 4, 16, 4) };
            var result = new TextBlock { Text = "Esperando inicio.", Margin = new Thickness(0, 20, 0, 0), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(entry); panel.Children.Add(button); panel.Children.Add(result);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false; entry.IsReadOnly = false;
                var results = new List<string>();
                try
                {
                    entry.Focus(); System.Windows.Input.Keyboard.Focus(entry);
                    await Task.Delay(150);
                    var handle = new WindowInteropHelper(window).Handle;
                    var initialCaps = (Native.GetKeyState(0x14) & 1) != 0;
                    async Task<string> Type(bool caps, int modifier)
                    {
                        if (GetForegroundWindow() != handle || !entry.IsKeyboardFocusWithin) throw new InvalidOperationException("Se detuvo porque la ventana de prueba perdió el foco.");
                        if (new[] { 0x10, 0x11, 0x12 }.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0)) throw new InvalidOperationException("Suelta Shift, Ctrl y Alt antes de repetir.");
                        entry.Clear();
                        var batch = new List<Native.Input>();
                        void CapsPulse() { batch.Add(Native.Key(0x14, 0x3a, false)); batch.Add(Native.Key(0x14, 0x3a, true)); }
                        if (caps != initialCaps) CapsPulse();
                        if (modifier != 0) batch.Add(Native.Key(modifier, KeyboardPacket.CanonicalData(modifier), false));
                        batch.Add(Native.Key(65, 30, false)); batch.Add(Native.Key(65, 30, true));
                        if (modifier != 0) batch.Add(Native.Key(modifier, KeyboardPacket.CanonicalData(modifier), true));
                        if (caps != initialCaps) CapsPulse();
                        Native.Inject(batch.ToArray());
                        await Task.Delay(180);
                        return entry.Text;
                    }
                    var lower = await Type(false, 0);
                    if (lower.Length == 0 || lower.ToUpperInvariant() == lower) throw new InvalidOperationException("La tecla base no produjo una letra con mayúscula en esta distribución.");
                    var upper = lower.ToUpperInvariant();
                    foreach (var scenario in new[] { (Name: "Shift izquierdo", Caps: false, Modifier: 0xa0), (Name: "Shift derecho", Caps: false, Modifier: 0xa1), (Name: "Bloq Mayús", Caps: true, Modifier: 0) })
                    {
                        var actual = await Type(scenario.Caps, scenario.Modifier);
                        var ok = actual == upper;
                        var message = $"{scenario.Name}: {(ok ? "correcto" : "falló")}";
                        results.Add(message); Console.WriteLine((ok ? "PASS " : "FAIL ") + message);
                    }
                    var restored = ((Native.GetKeyState(0x14) & 1) != 0) == initialCaps;
                    results.Add("Estado inicial de Bloq Mayús: " + (restored ? "conservado" : "no coincide"));
                    Console.WriteLine((restored ? "PASS " : "FAIL ") + "Restauración del estado de Bloq Mayús");
                }
                catch (Exception ex) { results.Add(ex.Message); Console.WriteLine("FAIL " + ex.Message); }
                finally { entry.IsReadOnly = true; result.Text = string.Join("\n", results); button.IsEnabled = true; }
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
}
