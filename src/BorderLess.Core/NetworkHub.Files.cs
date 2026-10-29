using System.Collections.Concurrent;

namespace BorderLess.Core;

public sealed partial class NetworkHub
{
    public ConcurrentDictionary<Guid, PeerSession> Files { get; } = new();
    public ConcurrentDictionary<Guid, bool> RemoteFilesEnabled { get; } = new();
    private readonly ConcurrentDictionary<Guid, byte> fileConnecting = new();
    private volatile bool filesEnabled;
    public bool FilesEnabled => filesEnabled;
    public Func<PeerSession, Frame, ValueTask>? FileMessage { get; set; }
    public event Action<Guid>? FileAvailabilityChanged;
    public bool CanTransferFiles(Guid id) => FilesEnabled && Inputs.TryGetValue(id, out var input) && input.Connected && input.FileRevision >= 1 && RemoteFilesEnabled.GetValueOrDefault(id);
    public void SetFilesEnabled(bool enabled)
    {
        filesEnabled = enabled;
        if (!enabled) foreach (var file in Files.Values) file.Close("Transferencia de archivos deshabilitada.");
        foreach (var input in Inputs.Values.Where(s => s.FileRevision >= 1)) input.Send(Frame.Json(PacketKind.FileAvailability, enabled));
        FileAvailabilityChanged?.Invoke(Guid.Empty);
    }
    private bool HandleFileControl(PeerSession session, Frame frame)
    {
        if (frame.Kind is not PacketKind.FileAvailability and not PacketKind.FileLaneRequest) return false;
        if (session.Lane != "input" || session.FileRevision < 1) throw new InvalidDataException("Archivos no negociados.");
        if (frame.Kind == PacketKind.FileAvailability)
        {
            var enabled = frame.Read<bool>();
            RemoteFilesEnabled[session.Peer.Id] = enabled;
            if (!enabled && Files.TryGetValue(session.Peer.Id, out var file)) file.Close("El otro equipo deshabilitó los archivos.");
            FileAvailabilityChanged?.Invoke(session.Peer.Id);
        }
        else if (CanTransferFiles(session.Peer.Id) && session.Peer.Initiator) _ = OpenFileLaneAsync(session.Peer.Id, stop.Token);
        return true;
    }
    // Only the original pairing initiator holds a routable address. A request
    // on the authenticated input lane lets either PC start a duplex file lane.
    private async Task OpenFileLaneAsync(Guid id, CancellationToken ct)
    {
        if (!fileConnecting.TryAdd(id, 0)) return;
        try
        {
            if (CanTransferFiles(id) && !Files.ContainsKey(id) && Credentials.TryGetValue(id, out var peer) && peer.Initiator)
                await ConnectLaneAsync(peer, "file", false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        { Log("No se pudo abrir el canal de archivos. Comprueba la conexión y la opción Archivos en ambos equipos.", true); }
        finally { fileConnecting.TryRemove(id, out _); }
    }
    public async Task<PeerSession> EnsureFileSessionAsync(Guid id, CancellationToken ct)
    {
        if (!CanTransferFiles(id)) throw new InvalidOperationException("Activa Archivos en ambos equipos y comprueba que estén conectados con la versión 0.1.5 o posterior.");
        if (Credentials[id].Initiator) _ = OpenFileLaneAsync(id, ct);
        else Inputs[id].Send(Frame.Json(PacketKind.FileLaneRequest, true));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!CanTransferFiles(id)) throw new IOException("Transferencia de archivos deshabilitada o equipo desconectado.");
            if (Files.TryGetValue(id, out var session) && session.Connected) return session;
            await Task.Delay(40, deadline.Token).ConfigureAwait(false);
        }
    }
}
