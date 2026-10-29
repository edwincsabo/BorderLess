using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BorderLess.App;

internal static class CursorArtworkProbe
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Execute(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("Cursor artwork regression", failure);
    }
    private static void Execute()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
        var stock = new[] { (32512,"Arrow"), (32649,"Hand"), (32513,"Text"), (32644,"Horizontal resize"), (32645,"Vertical resize"), (32642,"Diagonal NW-SE"), (32643,"Diagonal NE-SW"), (32514,"Wait"), (32650,"App starting"), (32515,"Cross"), (32648,"Unavailable"), (32646,"Move") };
        var art = stock.Select(s => NativeCursorImages.Capture(NativeCursorImages.Stock(s.Item1)) ?? throw new Exception("Native cursor unavailable: " + s.Item2)).ToArray();
        for (var i = 0; i < art.Length; i++) Check(art[i].Frames.Length > 0 && art[i].Frames.All(f => f.IsFrozen) && art[i].HotX >= 0 && art[i].HotX < art[i].Width && art[i].HotY >= 0 && art[i].HotY < art[i].Height, "Forma nativa y punto de clic válidos: " + stock[i].Item2);
        string Digest(CursorArtwork cursor) { var data = new byte[cursor.Width * cursor.Height * 4]; cursor.Frames[0].CopyPixels(data, cursor.Width * 4, 0); return Convert.ToHexString(SHA256.HashData(data)); }
        Check(Digest(art[0]) != Digest(art[1]) && Digest(art[0]) != Digest(art[2]) && Digest(art[3]) != Digest(art[4]), "Flecha, mano, texto y ejes de redimensionamiento no usan la misma imagen");
        using var process = Process.GetCurrentProcess(); var before = GetGuiResources(process.Handle, 0);
        for (var i = 0; i < 100; i++) _ = NativeCursorImages.Capture(NativeCursorImages.Stock(stock[i % stock.Length].Item1));
        var after = GetGuiResources(process.Handle, 0);
        Check(after <= before + 4, "Capturas sucesivas liberan los objetos GDI en lugar de acumularlos");
        Check(art.All(a => a.Bytes <= 4 * 1024 * 1024), "Cada juego de frames respeta el presupuesto de memoria");
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 720, 460));
            for (var i = 0; i < art.Length; i++)
            {
                var x = i % 4 * 180; var y = i / 4 * 150;
                draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(27,38,36)), null, new Rect(x + 4, y + 35, 170, 105));
                draw.DrawRectangle(Brushes.White, null, new Rect(x + 4, y + 35, 85, 105));
                var label = new FormattedText(stock[i].Item2 + " · " + art[i].Frames.Length + " frame(s)", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Black, 1);
                draw.DrawText(label, new Point(x + 8, y + 10));
                var scale = Math.Min(2d, 60d / Math.Max(art[i].Width, art[i].Height));
                draw.DrawImage(art[i].Frames[0], new Rect(x + 15, y + 55, art[i].Width * scale, art[i].Height * scale));
                draw.DrawImage(art[i].Frames[0], new Rect(x + 105, y + 55, art[i].Width * scale, art[i].Height * scale));
            }
        }
        var bitmap = new RenderTargetBitmap(720,460,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Environment.CurrentDirectory,"artifacts","cursor-gallery-0.1.9.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using (var file = File.Create(path)) encoder.Save(file);
        Console.WriteLine($"{checks} comprobaciones de cursor. Galería: {path}");
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
}
