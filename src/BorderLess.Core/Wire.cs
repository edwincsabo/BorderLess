using System.Buffers.Binary;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace BorderLess.Core;

public readonly record struct Frame(PacketKind Kind, byte[] Payload, long Created = 0)
{
    public static Frame Json<T>(PacketKind kind, T data) => new(kind, JsonSerializer.SerializeToUtf8Bytes(data), Stopwatch.GetTimestamp());
    public T Read<T>() => JsonSerializer.Deserialize<T>(Payload) ?? throw new InvalidDataException("Mensaje vacío.");
    public static Frame FromInput(InputEvent input)
    {
        var bytes = new byte[49];
        bytes[0] = (byte)input.Kind;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1), input.Sequence);
        input.Epoch.TryWriteBytes(bytes.AsSpan(9, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(25), input.X);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(33), input.Y);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(41), input.Code);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(45), input.Data);
        return new Frame(PacketKind.Input, bytes, Stopwatch.GetTimestamp());
    }
    public InputEvent ReadInput()
    {
        if (Kind != PacketKind.Input || Payload.Length != 49 || Payload[0] is < 1 or > 7) throw new InvalidDataException("Entrada no válida.");
        return new((InputKind)Payload[0], BinaryPrimitives.ReadInt64LittleEndian(Payload.AsSpan(1)), new Guid(Payload.AsSpan(9, 16)), BinaryPrimitives.ReadInt64LittleEndian(Payload.AsSpan(25)), BinaryPrimitives.ReadInt64LittleEndian(Payload.AsSpan(33)), BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(41)), BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(45)));
    }
}

public static class Wire
{
    public const int ClipboardLimit = 8 * 1024 * 1024;
    public static async ValueTask WriteAsync(Stream stream, Frame frame, CancellationToken cancellationToken, bool bulk = false)
    {
        if (!bulk)
        {
            var length = checked(frame.Payload.Length + 5);
            var packet = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(packet, frame.Payload.Length + 1);
                packet[4] = (byte)frame.Kind;
                frame.Payload.CopyTo(packet.AsSpan(5));
                await stream.WriteAsync(packet.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(packet, clearArray: true); }
            return;
        }
        var header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Payload.Length + 1);
        header[4] = (byte)frame.Kind;
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (bulk)
        {
            for (var offset = 0; offset < frame.Payload.Length; offset += 16384)
            {
                await stream.WriteAsync(frame.Payload.AsMemory(offset, Math.Min(16384, frame.Payload.Length - offset)), cancellationToken).ConfigureAwait(false);
                await Task.Delay(12, cancellationToken).ConfigureAwait(false);
            }
        }
    }
    public static async ValueTask<Frame> ReadAsync(Stream stream, CancellationToken cancellationToken, int max = 16384, byte[]? headerBuffer = null)
    {
        var header = headerBuffer ?? new byte[5];
        if (header.Length != 5) throw new ArgumentException("La cabecera debe tener cinco bytes.", nameof(headerBuffer));
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1 || length > max || !Enum.IsDefined((PacketKind)header[4])) throw new InvalidDataException("Mensaje fuera de los límites del protocolo.");
        var bytes = new byte[length - 1];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return new Frame((PacketKind)header[4], bytes);
    }
    public static async Task WriteJsonAsync<T>(Stream stream, T message, CancellationToken ct) => await WriteAsync(stream, Frame.Json(PacketKind.Ping, message), ct);
    public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct) => (await ReadAsync(stream, ct, 8192)).Read<T>();
}

// A move is a replaceable cumulative snapshot. Actions flush the prior snapshot,
// preserving move/click ordering without putting network waits inside the hook.
public sealed class InputOutbox
{
    private readonly object gate = new();
    private readonly Queue<Frame> reliable = new();
    private Frame? move;
    private readonly Channel<bool> available = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    public int Count { get { lock (gate) return reliable.Count + (move is null ? 0 : 1); } }
    public long Coalesced { get; private set; }
    public bool Enqueue(Frame frame)
    {
        lock (gate)
        {
            var wasEmpty = reliable.Count == 0 && move == null;
            if (frame.Kind == PacketKind.Input && frame.Payload.Length == 49 && frame.Payload[0] == (byte)InputKind.Move)
            {
                if (move != null) Coalesced++;
                move = frame;
                if (wasEmpty) available.Writer.TryWrite(true);
                return true;
            }
            if (reliable.Count >= 510) return false;
            if (move is { } pending) { reliable.Enqueue(pending); move = null; }
            reliable.Enqueue(frame);
            if (wasEmpty) available.Writer.TryWrite(true);
            return true;
        }
    }
    public ValueTask<bool> WaitForDataAsync(CancellationToken cancellationToken) => available.Reader.ReadAsync(cancellationToken);
    public bool TryTake(out Frame frame)
    {
        lock (gate)
        {
            if (reliable.TryDequeue(out frame)) return true;
            if (move is { } pending) { frame = pending; move = null; return true; }
            frame = default;
            return false;
        }
    }
}

public sealed class InputState
{
    public Guid Epoch { get; private set; }
    public long Sequence { get; private set; }
    public long X { get; private set; }
    public long Y { get; private set; }
    public void Begin(Guid epoch) { Epoch = epoch; Sequence = X = Y = 0; }
    public void End() => Begin(Guid.Empty);
    public bool TryAdvance(InputEvent input, out int dx, out int dy)
    {
        dx = dy = 0;
        if (Epoch == Guid.Empty || input.Epoch != Epoch || input.Sequence <= Sequence) return false;
        var x = input.X - X; var y = input.Y - Y;
        if (Math.Abs(input.X) > 1_000_000_000L || Math.Abs(input.Y) > 1_000_000_000L || x is < -50000 or > 50000 || y is < -50000 or > 50000) throw new InvalidDataException("Movimiento fuera de límites.");
        dx = (int)x; dy = (int)y; X = input.X; Y = input.Y; Sequence = input.Sequence;
        return true;
    }
}
