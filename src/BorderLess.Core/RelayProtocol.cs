using System.Buffers.Binary;
using System.Text.Json;

namespace BorderLess.Core;

// Routing metadata only. TLS, peer certificate verification and authentication
// run end-to-end inside the tunnel. A relay never receives clipboard/input plaintext.
public static class RelayProtocol
{
    public sealed record Route(string Role, string Room);
    public static async Task JoinAsync(Stream stream, string role, string room, CancellationToken ct, Action? registered = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Route(role, room));
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct);
        var ready = new byte[1]; await stream.ReadExactlyAsync(ready, ct);
        if (role == "host" && ready[0] == 2) { registered?.Invoke(); await stream.ReadExactlyAsync(ready, ct); }
        if (ready[0] != 1) throw new IOException("El relay no tiene una ruta disponible.");
    }
    public static async Task<Route> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 10 or > 512) throw new InvalidDataException();
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        var route = JsonSerializer.Deserialize<Route>(bytes) ?? throw new InvalidDataException();
        if (route.Role is not "host" and not "join" || route.Room == null || route.Room.Length != 48 || !route.Room.All(Uri.IsHexDigit)) throw new InvalidDataException();
        return route;
    }
}
