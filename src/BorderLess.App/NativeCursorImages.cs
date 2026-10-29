using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BorderLess.App;

internal sealed record CursorArtwork(BitmapSource[] Frames, int Width, int Height, int HotX, int HotY)
{
    public long Bytes => (long)Frames.Length * Width * Height * 4;
}

// Capture the cursor artwork only when its native handle changes. We never
// change ShowCursor counters, system cursor schemes or another app's cursor.
internal static class NativeCursorImages
{
    public static IntPtr Stock(int id) => LoadCursor(IntPtr.Zero, new IntPtr(id));
    public static bool Animated(IntPtr handle) => handle == Stock(32514) || handle == Stock(32650);
    public static CursorArtwork? Capture(IntPtr handle)
    {
        var copy = CopyIcon(handle); if (copy == IntPtr.Zero) return null;
        var info = new IconInfo();
        try
        {
            if (!GetIconInfo(copy, out info)) return null;
            var bitmap = new BitmapInfo();
            if (GetObject(info.Color != IntPtr.Zero ? info.Color : info.Mask, Marshal.SizeOf<BitmapInfo>(), out bitmap) == 0) return null;
            var width = bitmap.Width; var height = info.Color == IntPtr.Zero ? bitmap.Height / 2 : bitmap.Height;
            if (width is < 1 or > 256 || height is < 1 or > 256 || info.HotX >= width || info.HotY >= height) return null;
            using var surface = new Surface(width, height);
            var pixels = new List<byte[]>(); var frames = new List<BitmapSource>(); var padding = info.Color == IntPtr.Zero ? 1 : 0;
            var limit = Animated(handle) ? 24 : 1;
            var drawingHandle = Animated(handle) ? handle : copy;
            for (var frame = 0; frame < limit; frame++)
            {
                var black = surface.Draw(drawingHandle, frame, 0); var white = surface.Draw(drawingHandle, frame, 255);
                if (black == null || white == null) break;
                var image = Compose(black, white, width, height, padding);
                if (pixels.Count > 0 && pixels[0].AsSpan().SequenceEqual(image)) break;
                if ((long)(frames.Count + 1) * (width + 2 * padding) * (height + 2 * padding) * 4 > 4 * 1024 * 1024) break;
                pixels.Add(image);
                var frozen = BitmapSource.Create(width + 2 * padding, height + 2 * padding, 96, 96, PixelFormats.Pbgra32, null, image, (width + 2 * padding) * 4); frozen.Freeze(); frames.Add(frozen);
            }
            return frames.Count == 0 ? null : new(frames.ToArray(), width + 2 * padding, height + 2 * padding, checked((int)info.HotX) + padding, checked((int)info.HotY) + padding);
        }
        finally { if (info.Color != IntPtr.Zero) DeleteObject(info.Color); if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask); DestroyIcon(copy); }
    }
    private static byte[] Compose(byte[] black, byte[] white, int width, int height, int pad)
    {
        var outputWidth = width + 2 * pad; var result = new byte[outputWidth * (height + 2 * pad) * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4; var target = ((y + pad) * outputWidth + x + pad) * 4;
            var delta = Math.Max(white[offset] - black[offset], Math.Max(white[offset + 1] - black[offset + 1], white[offset + 2] - black[offset + 2]));
            var alpha = 255 - Math.Clamp(delta, 0, 255);
            var inverse = black[offset] > white[offset] || black[offset + 1] > white[offset + 1] || black[offset + 2] > white[offset + 2];
            result[target] = inverse ? (byte)0 : (byte)Math.Min(alpha, black[offset]);
            result[target + 1] = inverse ? (byte)0 : (byte)Math.Min(alpha, black[offset + 1]);
            result[target + 2] = inverse ? (byte)0 : (byte)Math.Min(alpha, black[offset + 2]); result[target + 3] = (byte)alpha;
        }
        if (pad > 0)
        {
            var ink = (byte[])result.Clone();
            for (var y = 1; y <= height; y++) for (var x = 1; x <= width; x++)
                if (ink[(y * outputWidth + x) * 4 + 3] > 0)
                    for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                    {
                        var o = ((y + dy) * outputWidth + x + dx) * 4;
                        if (ink[o + 3] == 0) { result[o] = result[o + 1] = result[o + 2] = result[o + 3] = 255; }
                    }
        }
        return result;
    }
    private sealed class Surface : IDisposable
    {
        private readonly IntPtr dc, bitmap, old, bits;
        private readonly int width, height;
        public Surface(int width, int height)
        {
            this.width = width; this.height = height;
            var info = new DibInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            dc = CreateCompatibleDC(IntPtr.Zero); bitmap = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (dc == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero) { Dispose(); throw new System.ComponentModel.Win32Exception(); }
            old = SelectObject(dc, bitmap);
        }
        public byte[]? Draw(IntPtr icon, int frame, byte background)
        {
            var buffer = new byte[width * height * 4]; Array.Fill(buffer, background);
            for (var i = 3; i < buffer.Length; i += 4) buffer[i] = 255;
            Marshal.Copy(buffer, 0, bits, buffer.Length);
            if (!DrawIconEx(dc, 0, 0, icon, width, height, (uint)frame, IntPtr.Zero, 3)) return null;
            Marshal.Copy(bits, buffer, 0, buffer.Length); return buffer;
        }
        public void Dispose() { if (dc != IntPtr.Zero && old != IntPtr.Zero) SelectObject(dc, old); if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (dc != IntPtr.Zero) DeleteDC(dc); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint HotX, HotY; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public int Type, Width, Height, WidthBytes; public ushort Planes, Bits; public IntPtr BitsPointer; }
    [StructLayout(LayoutKind.Sequential)] private struct DibInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Colors, Important; }
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr module, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr bitmap, int size, out BitmapInfo info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref DibInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint frame, IntPtr brush, uint flags);
}
