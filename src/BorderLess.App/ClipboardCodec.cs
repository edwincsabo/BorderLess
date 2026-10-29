using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BorderLess.App;

internal interface IClipboardAccess
{
    uint Sequence { get; }
    bool ContainsImage();
    bool ContainsText();
    BitmapSource? GetImage();
    string GetText();
    void SetImage(BitmapSource image);
    void SetText(string text);
}
internal sealed class WindowsClipboardAccess : IClipboardAccess
{
    public uint Sequence => Native.GetClipboardSequenceNumber();
    public bool ContainsImage() => Clipboard.ContainsImage();
    public bool ContainsText() => Clipboard.ContainsText(TextDataFormat.UnicodeText);
    public BitmapSource? GetImage() => Clipboard.GetImage();
    public string GetText() => Clipboard.GetText(TextDataFormat.UnicodeText);
    public void SetImage(BitmapSource image) => Clipboard.SetImage(image);
    public void SetText(string text) => Clipboard.SetText(text, TextDataFormat.UnicodeText);
}
internal sealed record ClipboardSnapshot(string Format, string? Text = null, BitmapSource? Image = null);
internal sealed record PreparedClipboard(ClipboardPayload Payload, string Hash, Frame Frame, BitmapSource? Image);
internal interface IClipboardCodec
{
    PreparedClipboard Encode(ClipboardSnapshot snapshot, Guid origin, CancellationToken ct);
    PreparedClipboard Decode(Frame frame, CancellationToken ct);
}
internal sealed class ClipboardCodec : IClipboardCodec
{
    public const long PixelLimit = 16_000_000;
    public PreparedClipboard Encode(ClipboardSnapshot snapshot, Guid origin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string content;
        if (snapshot.Format == "png")
        {
            var image = snapshot.Image ?? throw new InvalidDataException();
            if (!image.IsFrozen || image.PixelWidth <= 0 || image.PixelHeight <= 0 || (long)image.PixelWidth * image.PixelHeight > PixelLimit) throw new InvalidDataException("La imagen supera el límite o no se puede procesar fuera de la interfaz.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new LimitedPngStream(ct); encoder.Save(stream);
            ct.ThrowIfCancellationRequested();
            content = Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
        }
        else if (snapshot.Format == "text")
        {
            content = snapshot.Text ?? throw new InvalidDataException();
            if (Encoding.UTF8.GetByteCount(content) > 1024 * 1024) throw new InvalidDataException("El texto supera el límite de 1 MB.");
        }
        else throw new InvalidDataException("Formato no admitido.");
        var payload = new ClipboardPayload(origin, Guid.NewGuid(), snapshot.Format, content);
        var hash = Hash(payload.Format, payload.Content, ct);
        var frame = Frame.Json(PacketKind.Clipboard, payload);
        ct.ThrowIfCancellationRequested();
        return new(payload, hash, frame, null);
    }
    public PreparedClipboard Decode(Frame frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = frame.Read<ClipboardPayload>();
        if (payload.Change == Guid.Empty || payload.Origin == Guid.Empty || payload.Content == null) throw new InvalidDataException();
        BitmapSource? image = null;
        if (payload.Format == "png")
        {
            if (payload.Content.Length > (Wire.ClipboardLimit + 2) / 3 * 4) throw new InvalidDataException();
            var png = Convert.FromBase64String(payload.Content);
            if (png.Length is < 33 or > Wire.ClipboardLimit || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException();
            var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)); var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
            if (width == 0 || height == 0 || (ulong)width * height > PixelLimit) throw new InvalidDataException();
            using var stream = new MemoryStream(png, writable: false);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            if (bitmap.PixelWidth != width || bitmap.PixelHeight != height) throw new InvalidDataException();
            image = bitmap;
        }
        else if (payload.Format != "text" || Encoding.UTF8.GetByteCount(payload.Content) > 1024 * 1024) throw new InvalidDataException();
        var hash = Hash(payload.Format, payload.Content, ct);
        ct.ThrowIfCancellationRequested();
        return new(payload, hash, frame, image);
    }
    internal static string Hash(string format, string content, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(format + ":"));
        var encoder = Encoding.UTF8.GetEncoder(); var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            var offset = 0;
            do
            {
                ct.ThrowIfCancellationRequested();
                encoder.Convert(content.AsSpan(offset), buffer, true, out var chars, out var bytes, out var complete);
                hash.AppendData(buffer, 0, bytes); offset += chars;
                if (complete) break;
            } while (true);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    private sealed class LimitedPngStream(CancellationToken ct) : MemoryStream
    {
        private void Check(long end) { ct.ThrowIfCancellationRequested(); if (end > Wire.ClipboardLimit) throw new InvalidDataException("La imagen supera el límite de 8 MB."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(Position + count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(Position + buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(Position + 1); base.WriteByte(value); }
        public override void SetLength(long value) { Check(value); base.SetLength(value); }
    }
}
