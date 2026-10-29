using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace BorderLess.Core;

public enum TransferState { Waiting, Sending, Receiving, Completed, Rejected, Cancelled, Failed }
public sealed record FileTransferSnapshot(Guid Id, Guid PeerId, string Name, long Length, long Bytes, bool Incoming, TransferState State, string Detail, string SavedPath);
public sealed record FileOffer(Guid Id, string Name, long Length);
public sealed record FileReply(Guid Id, bool Accepted, string Error = "");
public sealed record FileFinish(Guid Id, long Length, string Sha256);
public sealed record FileReceipt(Guid Id, bool Success, string Error = "");
public sealed record FileAbort(Guid Id, string Reason = "Cancelado");

// File I/O and hashing run only on the file lane. Input never waits on this
// service. At most eight 64 KiB frames are queued; Wire paces bulk writes.
public sealed class FileTransferService : IAsyncDisposable
{
    public const long MaxFileSize = 1024L * 1024 * 1024;
    public const int ChunkDataSize = 65536 - 24;
    private sealed class Operation
    {
        public required FileTransferSnapshot View;
        public required PeerSession Session;
        public readonly CancellationTokenSource Stop = new();
        public readonly SemaphoreSlim Io = new(1, 1);
        public readonly TaskCompletionSource<FileReply> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<FileReceipt> Receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileStream? Stream;
        public IncrementalHash? Hash;
        public string Partial = "", Directory = "";
        public long Offset, LastNotice;
    }
    private readonly NetworkHub hub;
    private string receiveRoot;
    private readonly object gate = new();
    private readonly List<Operation> operations = new();
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private Task? senderTask;
    private bool enabled;
    public bool Enabled { get { lock (gate) return enabled; } }
    public string ReceiveDirectory { get { lock (gate) return receiveRoot; } }
    public void SetReceiveDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new ArgumentException("La carpeta de recepción debe ser una ruta absoluta.");
        var normalized = Path.GetFullPath(directory);
        lock (gate) receiveRoot = normalized;
        Changed?.Invoke();
    }
    public event Action? Changed;
    public event Action<FileTransferSnapshot>? Offered;
    public FileTransferService(NetworkHub hub, string receiveDirectory)
    {
        this.hub = hub; receiveRoot = Path.GetFullPath(receiveDirectory);
        hub.FileMessage = ReceiveAsync; hub.Disconnected += Disconnected;
    }
    public IReadOnlyList<FileTransferSnapshot> Snapshot() { lock (gate) return operations.Select(o => o.View).Reverse().ToArray(); }
    public void SetEnabled(bool value)
    {
        lock (gate) enabled = value && !stop.IsCancellationRequested;
        hub.SetFilesEnabled(Enabled);
        if (!Enabled) foreach (var op in Active()) _ = CancelOperationAsync(op, TransferState.Cancelled, "Transferencia deshabilitada.", false);
        Changed?.Invoke();
    }
    public static void ValidateOffer(FileOffer offer)
    {
        var name = offer.Name;
        if (offer.Id == Guid.Empty || offer.Length is < 0 or > MaxFileSize || string.IsNullOrWhiteSpace(name) || name.Length > 200 || name != Path.GetFileName(name) || name.EndsWith('.') || name.EndsWith(' ') || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) throw new InvalidDataException("Nombre o tamaño de archivo no permitido (máximo 1 GiB).");
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (char.IsDigit(stem[3]) || stem[3] is '¹' or '²' or '³'))) throw new InvalidDataException("Nombre reservado por Windows.");
    }
    public Task SendAsync(Guid peerId, string path, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!enabled) throw new InvalidOperationException("Activa la transferencia de archivos primero.");
            if (!sending.Wait(0)) throw new InvalidOperationException("Espera a que termine el envío actual o cancélalo.");
            senderTask = Task.Run(() => SendCoreAsync(peerId, path, ct));
            return senderTask;
        }
    }
    private async Task SendCoreAsync(Guid peerId, string path, CancellationToken ct)
    {
        Operation? op = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        try
        {
            // FileShare.Read denies new writers while the snapshot is being sent.
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkDataSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var offer = new FileOffer(Guid.NewGuid(), Path.GetFileName(path), source.Length); ValidateOffer(offer);
            var session = await hub.EnsureFileSessionAsync(peerId, linked.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (!enabled) throw new OperationCanceledException();
                op = new() { Session = session, View = new(offer.Id, peerId, offer.Name, offer.Length, 0, false, TransferState.Waiting, "Esperando aceptación en el otro equipo…", "") };
                Add(op);
            }
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, op.Stop.Token);
            Changed?.Invoke();
            await session.SendFileAsync(Frame.Json(PacketKind.FileOffer, offer), transfer.Token).ConfigureAwait(false);
            var reply = await op.Reply.Task.WaitAsync(TimeSpan.FromMinutes(2), transfer.Token).ConfigureAwait(false);
            if (!reply.Accepted) { Update(op, TransferState.Rejected, "El otro equipo rechazó el archivo."); return; }
            Update(op, TransferState.Sending, "Enviando · límite aproximado de 1 MiB/s");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[ChunkDataSize]; long offset = 0;
            while (offset < offer.Length)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offer.Length - offset)), transfer.Token).ConfigureAwait(false);
                if (count == 0) throw new IOException("El archivo cambió durante el envío.");
                hash.AppendData(buffer, 0, count);
                var payload = new byte[count + 24]; offer.Id.TryWriteBytes(payload);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), offset); buffer.AsSpan(0, count).CopyTo(payload.AsSpan(24));
                await session.SendFileAsync(new(PacketKind.FileChunk, payload), transfer.Token).ConfigureAwait(false);
                offset += count; Progress(op, offset);
            }
            await session.SendFileAsync(Frame.Json(PacketKind.FileFinish, new FileFinish(offer.Id, offset, Convert.ToHexString(hash.GetHashAndReset()))), transfer.Token).ConfigureAwait(false);
            var receipt = await op.Receipt.Task.WaitAsync(TimeSpan.FromSeconds(40), transfer.Token).ConfigureAwait(false);
            if (!receipt.Success) throw new IOException("El receptor no pudo verificar o guardar el archivo.");
            Update(op, TransferState.Completed, "Recibido y verificado con SHA-256.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or InvalidOperationException or System.Threading.Channels.ChannelClosedException)
        {
            if (op == null) throw;
            await CancelOperationAsync(op, ex is OperationCanceledException ? TransferState.Cancelled : TransferState.Failed, ex is TimeoutException ? "Se agotó el tiempo de espera." : ex is OperationCanceledException ? "Transferencia cancelada." : "No se pudo completar el envío.", true).ConfigureAwait(false);
        }
        finally { sending.Release(); }
    }
    private void Add(Operation op)
    {
        operations.Add(op);
        while (operations.Count > 100) { var index = operations.FindIndex(o => Terminal(o.View.State)); if (index < 0) break; operations.RemoveAt(index); }
    }
    private static bool Terminal(TransferState state) => state is TransferState.Completed or TransferState.Rejected or TransferState.Cancelled or TransferState.Failed;
    private Operation[] Active() { lock (gate) return operations.Where(o => !Terminal(o.View.State)).ToArray(); }
    private void Update(Operation op, TransferState state, string detail, string? saved = null)
    {
        lock (gate)
        {
            if (Terminal(op.View.State)) return;
            op.View = op.View with { State = state, Detail = detail, SavedPath = saved ?? op.View.SavedPath, Bytes = state == TransferState.Completed ? op.View.Length : op.View.Bytes };
        }
        Changed?.Invoke();
    }
    private void Progress(Operation op, long bytes)
    {
        bool notice;
        lock (gate)
        {
            if (Terminal(op.View.State)) return;
            op.View = op.View with { Bytes = bytes };
            notice = Stopwatch.GetElapsedTime(op.LastNotice).TotalMilliseconds >= 250;
            if (notice) op.LastNotice = Stopwatch.GetTimestamp();
        }
        if (notice) Changed?.Invoke();
    }
    private async ValueTask ReceiveAsync(PeerSession session, Frame frame)
    {
        if (!Enabled) throw new InvalidDataException("Archivos deshabilitados.");
        if (frame.Kind == PacketKind.FileOffer)
        {
            var offer = frame.Read<FileOffer>(); ValidateOffer(offer);
            Operation op;
            lock (gate)
            {
                if (!enabled) throw new InvalidDataException("Archivos deshabilitados.");
                if (operations.Any(o => o.View.Id == offer.Id)) throw new InvalidDataException("Oferta repetida.");
                if (operations.Any(o => o.View.Incoming && !Terminal(o.View.State)))
                { session.Send(Frame.Json(PacketKind.FileReply, new FileReply(offer.Id, false, "Hay otra recepción en curso."))); return; }
                op = new() { Session = session, View = new(offer.Id, session.Peer.Id, offer.Name, offer.Length, 0, true, TransferState.Waiting, "Pendiente de tu aceptación", "") };
                Add(op);
            }
            Changed?.Invoke(); Offered?.Invoke(op.View); _ = ExpireOfferAsync(op); return;
        }
        Guid id = frame.Kind switch
        {
            PacketKind.FileChunk when frame.Payload.Length is > 24 and <= 65536 => new Guid(frame.Payload.AsSpan(0, 16)),
            PacketKind.FileReply => frame.Read<FileReply>().Id,
            PacketKind.FileFinish => frame.Read<FileFinish>().Id,
            PacketKind.FileReceipt => frame.Read<FileReceipt>().Id,
            PacketKind.FileAbort => frame.Read<FileAbort>().Id,
            _ => throw new InvalidDataException("Mensaje de archivos no válido.")
        };
        Operation? operation;
        lock (gate) operation = operations.Find(o => o.View.Id == id && ReferenceEquals(o.Session, session));
        if (operation == null) throw new InvalidDataException("Transferencia desconocida.");
        if (Terminal(operation.View.State)) return; // Already queued chunks after a cancellation are discarded.
        if (frame.Kind == PacketKind.FileAbort) { await CancelOperationAsync(operation, TransferState.Cancelled, "Cancelado por el otro equipo.", false).ConfigureAwait(false); return; }
        if (!operation.View.Incoming)
        {
            if (frame.Kind == PacketKind.FileReply && operation.View.State == TransferState.Waiting) operation.Reply.TrySetResult(frame.Read<FileReply>());
            else if (frame.Kind == PacketKind.FileReceipt && operation.View.State == TransferState.Sending) operation.Receipt.TrySetResult(frame.Read<FileReceipt>());
            else throw new InvalidDataException("Respuesta fuera de orden.");
            return;
        }
        await operation.Io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (operation.Stop.IsCancellationRequested) { operation.Io.Release(); return; }
            if (operation.View.State != TransferState.Receiving || operation.Stream == null || operation.Hash == null) throw new InvalidDataException("Archivo enviado sin aceptación.");
            if (frame.Kind == PacketKind.FileChunk)
            {
                var offset = BinaryPrimitives.ReadInt64LittleEndian(frame.Payload.AsSpan(16)); var count = frame.Payload.Length - 24;
                if (offset != operation.Offset || offset + count > operation.View.Length) throw new InvalidDataException("Bloque fuera de orden o tamaño declarado excedido.");
                await operation.Stream.WriteAsync(frame.Payload.AsMemory(24), operation.Stop.Token).ConfigureAwait(false);
                operation.Hash.AppendData(frame.Payload, 24, count); operation.Offset += count; Progress(operation, operation.Offset);
            }
            else if (frame.Kind == PacketKind.FileFinish)
            {
                var finish = frame.Read<FileFinish>();
                if (finish.Length != operation.View.Length || operation.Offset != finish.Length || finish.Sha256 == null || finish.Sha256.Length != 64 || !string.Equals(finish.Sha256, Convert.ToHexString(operation.Hash.GetHashAndReset()), StringComparison.Ordinal)) throw new InvalidDataException("La verificación SHA-256 falló.");
                await operation.Stream.FlushAsync(operation.Stop.Token).ConfigureAwait(false); await operation.Stream.DisposeAsync().ConfigureAwait(false); operation.Stream = null;
                operation.Stop.Token.ThrowIfCancellationRequested();
                var final = Path.Combine(operation.Directory, operation.View.Name);
                File.Move(operation.Partial, final, false); operation.Partial = "";
                Update(operation, TransferState.Completed, "Guardado y verificado con SHA-256.", final);
                await session.SendFileAsync(Frame.Json(PacketKind.FileReceipt, new FileReceipt(id, true)), stop.Token).ConfigureAwait(false);
                operation.Hash.Dispose(); operation.Hash = null;
            }
            else throw new InvalidDataException("Mensaje fuera de orden.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or System.Threading.Channels.ChannelClosedException)
        {
            // Release before cleanup, which takes this same I/O gate.
            operation.Io.Release();
            await CancelOperationAsync(operation, ex is OperationCanceledException ? TransferState.Cancelled : TransferState.Failed, "No se pudo guardar o verificar el archivo.", true).ConfigureAwait(false);
            return;
        }
        operation.Io.Release();
    }
    public Task AcceptAsync(Guid id) => Task.Run(() => AcceptCoreAsync(id));
    private async Task AcceptCoreAsync(Guid id)
    {
        Operation? op; lock (gate) op = operations.Find(o => o.View.Id == id && o.View.Incoming && o.View.State == TransferState.Waiting);
        if (op == null) return;
        await op.Io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Enabled || op.Stop.IsCancellationRequested || op.View.State != TransferState.Waiting) { op.Io.Release(); return; }
            // Pin the selected root at acceptance. Changing preferences never
            // redirects a partial file that has already started receiving.
            string root; lock (gate) root = receiveRoot;
            Directory.CreateDirectory(root);
            op.Directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(op.Directory);
            op.Partial = Path.Combine(op.Directory, "receiving.partial");
            op.Stream = new(op.Partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkDataSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            op.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Update(op, TransferState.Receiving, "Recibiendo · se verificará antes de finalizar");
            await op.Session.SendFileAsync(Frame.Json(PacketKind.FileReply, new FileReply(id, true)), op.Stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Threading.Channels.ChannelClosedException)
        { op.Io.Release(); await CancelOperationAsync(op, TransferState.Failed, "No se pudo preparar la carpeta de recepción.", true).ConfigureAwait(false); return; }
        op.Io.Release();
    }
    private async Task ExpireOfferAsync(Operation op)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), op.Stop.Token).ConfigureAwait(false); if (op.View.State == TransferState.Waiting) await CancelOperationAsync(op, TransferState.Cancelled, "Solicitud vencida.", true).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
    public Task RejectAsync(Guid id) => CancelByIdAsync(id, TransferState.Rejected, "Archivo rechazado.");
    public Task CancelAsync(Guid id) => CancelByIdAsync(id, TransferState.Cancelled, "Transferencia cancelada.");
    private Task CancelByIdAsync(Guid id, TransferState state, string reason)
    { Operation? op; lock (gate) op = operations.Find(o => o.View.Id == id); return op == null ? Task.CompletedTask : CancelOperationAsync(op, state, reason, true); }
    private async Task CancelOperationAsync(Operation op, TransferState state, string reason, bool notify)
    {
        lock (gate) { if (Terminal(op.View.State)) return; op.Stop.Cancel(); }
        await op.Io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (op.Stream != null) { await op.Stream.DisposeAsync().ConfigureAwait(false); op.Stream = null; }
            op.Hash?.Dispose(); op.Hash = null;
            if (op.Partial.Length > 0) { try { File.Delete(op.Partial); if (op.Directory.Length > 0) Directory.Delete(op.Directory, false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } op.Partial = ""; }
            Update(op, state, reason);
            if (notify && op.Session.Connected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await op.Session.SendFileAsync(state == TransferState.Rejected && op.View.Incoming ? Frame.Json(PacketKind.FileReply, new FileReply(op.View.Id, false)) : Frame.Json(PacketKind.FileAbort, new FileAbort(op.View.Id)), timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Threading.Channels.ChannelClosedException) { op.Session.Close("Transferencia cancelada."); }
            }
        }
        finally { op.Io.Release(); }
    }
    private void Disconnected(PeerSession session, string reason)
    { if (session.Lane == "file") foreach (var op in Active().Where(o => ReferenceEquals(o.Session, session))) _ = CancelOperationAsync(op, TransferState.Failed, "Conexión de archivos interrumpida.", false); }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); SetEnabled(false); hub.Disconnected -= Disconnected;
        foreach (var op in Active()) await CancelOperationAsync(op, TransferState.Cancelled, "Aplicación cerrada.", false).ConfigureAwait(false);
        if (senderTask != null) { try { await senderTask.ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException) { } }
        hub.FileMessage = null;
    }
}
