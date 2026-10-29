using System.Diagnostics;

namespace BorderLess.App;

internal sealed partial class InputRouter
{
    private readonly object gate = new();
    private readonly NetworkHub hub;
    private readonly AppSettings settings;
    private readonly IInputDesktop desktop;
    private readonly SharingPauseState sharingPause;
    private readonly InputState state = new();
    private readonly Dictionary<int, int> keys = new();
    private readonly HashSet<int> buttons = new();
    private readonly Dictionary<int, int> physicalKeys = new();
    private Guid? leftNeighbor, rightNeighbor, topNeighbor, bottomNeighbor;
    private Dictionary<Guid, string> peerLabels = new();
    private string returnEdge = "left";
    private PeerSession? outgoing, incoming, pending;
    private Guid outgoingEpoch, pendingEpoch;
    private Native.Point anchor;
    private Native.Point targetPosition;
    private int parkingX, parkingY, parkingAcknowledged;
    private int parkedActualX, parkedActualY;
    private bool parkingPending;
    private long parkingSince;
    private long sequence, x, y, edgeSince, lastEdge, pendingSince;
    private string edgeSide = "";
    public bool Paused { get; private set; }
    public bool SessionLocked { get { lock (gate) return sharingPause.Locked; } }
    public bool ManualPause { get { lock (gate) return sharingPause.Manual; } }
    public string PauseDescription { get { lock (gate) return sharingPause.Description; } }
    public string PauseHint { get { lock (gate) return sharingPause.Hint; } }
    public bool RequirePointerParkingAcknowledgement { get; set; }
    public InputDiagnostics Diagnostics { get; } = new();
    public string Status { get; private set; } = "El control está en este equipo";
    public Guid? ActivePeer { get { lock (gate) return outgoing?.Peer.Id; } }
    public Guid? ControlledBy { get { lock (gate) return incoming?.Peer.Id; } }
    public event Action? Changed;
    public void UpdateNeighbors()
    {
        lock (gate)
        {
            leftNeighbor = settings.Peers.FirstOrDefault(p => p.Edge == "left")?.Id; rightNeighbor = settings.Peers.FirstOrDefault(p => p.Edge == "right")?.Id;
            topNeighbor = settings.Peers.FirstOrDefault(p => p.Edge == "top")?.Id; bottomNeighbor = settings.Peers.FirstOrDefault(p => p.Edge == "bottom")?.Id;
            peerLabels = settings.Peers.ToDictionary(p => p.Id, p => p.DisplayName);
            if (outgoing != null) Status = $"Controlando {PeerLabel(outgoing)}";
            else if (incoming != null) Status = $"{PeerLabel(incoming)} controla este equipo";
        }
    }
    private string PeerLabel(PeerSession session) => peerLabels.GetValueOrDefault(session.Peer.Id, session.Peer.Name);
    public InputRouter(NetworkHub hub, AppSettings settings, IInputDesktop? desktop = null)
    {
        this.hub = hub; this.settings = settings; this.desktop = desktop ?? new WindowsInputDesktop();
        sharingPause = new(settings.PauseOnLock);
        lockState = this.desktop.LockState;
        hub.Message += Receive;
        hub.Disconnected += (session, _) => { lock (gate) { if (session == outgoing || session == incoming || session == pending) ReturnLocked("Conexión interrumpida. Control local recuperado."); } };
    }
    public void Pause(bool pause)
    {
        lock (gate) { sharingPause.SetManual(pause); ReconcilePauseLocked(); }
    }
    public void SetPauseOnLock(bool enabled)
    {
        lock (gate) { sharingPause.SetPolicy(enabled); ReconcilePauseLocked(status: sharingPause.Locked ? "Windows bloqueado. La app mantiene sus servicios; control remoto liberado." : null); }
    }
    public void SetSessionLocked(bool locked)
    {
        lock (gate)
        {
            if (sharingPause.Locked == locked) return;
            sharingPause.SetLocked(locked);
            if (locked) { physicalKeys.Clear(); swallowEmergencyRelease = false; }
            ReconcilePauseLocked(locked, locked ? "Windows bloqueado. La app mantiene sus servicios; control remoto liberado." : "Windows desbloqueado. El control está en este equipo.");
        }
    }
    private void ReconcilePauseLocked(bool releaseControl = false, string? status = null)
    {
        var before = Paused; Paused = sharingPause.Paused;
        if (releaseControl || (Paused && !before)) ReturnLocked(Paused ? sharingPause.Description : status ?? "El control está en este equipo");
        if (before != Paused) hub.SetClipboardPaused(Paused);
        if (Paused) Status = sharingPause.Description;
        else if (before || status != null) Status = status ?? "El control está en este equipo";
        Changed?.Invoke();
    }
    public void Request(PeerSession session, string entry = "center", double height = .5, double width = .5)
    {
        lock (gate)
        {
            if (Paused || sharingPause.Locked || !session.Connected || pending != null) return;
            if (entry is "top" or "bottom" && session.LayoutRevision < 1) { Status = "Actualiza ambos PCs para utilizar bordes superior e inferior."; Changed?.Invoke(); return; }
            ReturnLocked("El control está en este equipo");
            SeedPhysicalModifiers();
            pending = session; pendingEpoch = Guid.NewGuid(); pendingSince = Stopwatch.GetTimestamp();
            session.Send(Frame.Json(PacketKind.RequestControl, new ControlRequest(pendingEpoch, entry, Math.Clamp(height, 0, 1), session.InputRevision >= 2 ? CurrentKeyboard() : null, Math.Clamp(width, 0, 1))));
            Status = $"Solicitando control de {PeerLabel(session)}…"; Changed?.Invoke();
        }
    }
    public void Tick()
    {
        lock (gate)
        {
            if (pending != null && Stopwatch.GetElapsedTime(pendingSince).TotalSeconds > 3) ReturnLocked("El otro equipo no concedió el control. Reintenta.");
            if (parkingPending && Volatile.Read(ref parkingAcknowledged) == 0 && Stopwatch.GetElapsedTime(parkingSince).TotalMilliseconds > 500) ReturnLocked("Windows no confirmó la posición del mouse. Control local recuperado.");
        }
    }
    public void EmergencyReturn(string message = "El control está en este equipo") { lock (gate) ReturnLocked(message); }
    private void ReturnLocked(string message, string remoteReason = "")
    {
        var wasOutgoing = outgoing != null;
        if (outgoing != null) outgoing.Send(Frame.Json(PacketKind.ReleaseControl, new ControlReply(outgoingEpoch, remoteReason)));
        if (pending != null) pending.Send(Frame.Json(PacketKind.ReleaseControl, new ControlReply(pendingEpoch, remoteReason)));
        if (incoming != null) incoming.Send(Frame.Json(PacketKind.ReleaseControl, new ControlReply(state.Epoch, remoteReason)));
        outgoing = incoming = pending = null; outgoingEpoch = pendingEpoch = Guid.Empty; state.End();
        parkingPending = false;
        ReleaseInjected();
        if (wasOutgoing)
            foreach (var key in physicalKeys.Where(k => k.Key is 0x10 or 0x11 or 0x12 or 0xa0 or 0xa1 or 0xa2 or 0xa3 or 0xa4 or 0xa5).ToArray())
                try { desktop.Inject(Native.Key(key.Key, key.Value, false)); } catch (System.ComponentModel.Win32Exception) { }
        Status = message; lastEdge = Stopwatch.GetTimestamp(); Changed?.Invoke();
    }
    private void ReleaseInjected()
    {
        foreach (var key in keys.ToArray()) { try { desktop.Inject(Native.Key(key.Key, key.Value, true)); } catch { } }
        foreach (var button in buttons.ToArray()) { try { desktop.Inject(Native.MouseButton(button, true)); } catch { } }
        keys.Clear(); buttons.Clear();
    }
    public bool CaptureMouse(int message, Native.MouseHook value)
    {
        lock (gate)
        {
            if (Paused || sharingPause.Locked) return false;
            if (incoming != null)
            {
                // A stationary touchpad notification or an echo at the last injected
                // point is not evidence of an intentional physical takeover.
                if (message == 0x200 && Math.Abs(value.Point.X - targetPosition.X) + Math.Abs(value.Point.Y - targetPosition.Y) < 3) return false;
                Diagnostics.Takeover(); ReturnLocked("Se recuperó el control con el mouse local."); return false;
            }
            if (outgoing == null)
            {
                if (message == 0x200 && settings.AutoEdges && pending == null) CheckEdge(value.Point);
                return false;
            }
            var kind = InputKind.Move;
            var code = 0; var data = 0;
            if (message == 0x200 && parkingPending)
            {
                if (Volatile.Read(ref parkingAcknowledged) == 0)
                {
                    if (Stopwatch.GetElapsedTime(parkingSince).TotalMilliseconds > 500) { ReturnLocked("Windows no confirmó la posición del mouse. Control local recuperado."); return false; }
                    return true;
                }
                anchor = new Native.Point(Volatile.Read(ref parkedActualX), Volatile.Read(ref parkedActualY));
                parkingPending = false;
            }
            switch (message)
            {
                case 0x200:
                    var dx = value.Point.X - anchor.X; var dy = value.Point.Y - anchor.Y;
                    if (dx == 0 && dy == 0) return true;
                    x += dx; y += dy; break;
                case 0x201: kind = InputKind.ButtonDown; code = 1; break;
                case 0x202: kind = InputKind.ButtonUp; code = 1; break;
                case 0x204: kind = InputKind.ButtonDown; code = 2; break;
                case 0x205: kind = InputKind.ButtonUp; code = 2; break;
                case 0x207: kind = InputKind.ButtonDown; code = 3; break;
                case 0x208: kind = InputKind.ButtonUp; code = 3; break;
                case 0x20b: kind = InputKind.ButtonDown; code = 3 + (int)(value.Data >> 16); break;
                case 0x20c: kind = InputKind.ButtonUp; code = 3 + (int)(value.Data >> 16); break;
                case 0x20a: kind = InputKind.Wheel; data = unchecked((short)(value.Data >> 16)); break;
                case 0x20e: kind = InputKind.HorizontalWheel; data = unchecked((short)(value.Data >> 16)); break;
                default: return true;
            }
            if (!outgoing.Send(Frame.FromInput(new(kind, ++sequence, outgoingEpoch, x, y, code, data)))) ReturnLocked("La conexión no está disponible.");
            else Diagnostics.Sent();
            return true;
        }
    }
    public bool CaptureKey(Native.KeyHook value, bool up)
    {
        lock (gate)
        {
            return CaptureKeyboardLocked(value, up);
        }
    }
    private void CheckEdge(Native.Point point)
    {
        if (desktop.IsKeyDown(1) || desktop.IsKeyDown(2) || desktop.IsKeyDown(4)) return;
        if (Stopwatch.GetElapsedTime(lastEdge).TotalSeconds < 1) return;
        var bounds = desktop.Bounds;
        var side = point.X <= bounds.Left ? "left" : point.X >= bounds.Left + bounds.Width - 1 ? "right" : point.Y <= bounds.Top ? "top" : point.Y >= bounds.Top + bounds.Height - 1 ? "bottom" : "";
        if (side == "") { edgeSide = ""; return; }
        if (edgeSide != side) { edgeSide = side; edgeSince = Stopwatch.GetTimestamp(); return; }
        if (Stopwatch.GetElapsedTime(edgeSince).TotalMilliseconds < 160) return;
        var peerId = side switch { "left" => leftNeighbor, "right" => rightNeighbor, "top" => topNeighbor, _ => bottomNeighbor };
        if (peerId is Guid id && hub.Inputs.TryGetValue(id, out var session))
        { lastEdge = Stopwatch.GetTimestamp(); Request(session, PlacementRules.Opposite(side), (double)(point.Y - bounds.Top) / Math.Max(1, bounds.Height), (double)(point.X - bounds.Left) / Math.Max(1, bounds.Width)); }
    }
    private void Receive(PeerSession session, Frame frame)
    {
        if (session.Lane != "input") return;
        lock (gate)
        {
            switch (frame.Kind)
            {
                case PacketKind.RequestControl:
                    var request = frame.Read<ControlRequest>();
                    if (sharingPause.Locked) { session.Send(Frame.Json(PacketKind.DenyControl, new ControlReply(request.Epoch, "Windows está bloqueado. Desbloquea ese PC para controlarlo."))); return; }
                    if (Paused || !settings.AllowControl || incoming != null || outgoing != null || pending != null || request.Epoch == Guid.Empty || !double.IsFinite(request.Y) || !double.IsFinite(request.X) || request.Entry is not "center" and not "left" and not "right" and not "top" and not "bottom" || (request.Entry is "top" or "bottom" && session.LayoutRevision < 1))
                    { session.Send(Frame.Json(PacketKind.DenyControl, new ControlReply(request.Epoch, "El equipo está pausado, ocupado o no permite control."))); return; }
                    incoming = session; state.Begin(request.Epoch); ReleaseInjected();
                    returnEdge = request.Entry == "center" ? "left" : request.Entry;
                    var bounds = desktop.Bounds;
                    var px = request.Entry == "left" ? bounds.Left + 3 : request.Entry == "right" ? bounds.Left + bounds.Width - 4 : bounds.Left + (int)(Math.Clamp(request.X, .01, .99) * (bounds.Width - 1));
                    var py = request.Entry == "top" ? bounds.Top + 3 : request.Entry == "bottom" ? bounds.Top + bounds.Height - 4 : bounds.Top + (int)(Math.Clamp(request.Y, .01, .99) * (bounds.Height - 1));
                    targetPosition = desktop.ConstrainPosition(px, py);
                    var initial = new List<Native.Input> { Native.MoveTo(targetPosition.X, targetPosition.Y, bounds) };
                    if (request.Keyboard is { } keyboard) SynchronizeKeyboard(initial, keyboard);
                    try { desktop.Inject(initial.ToArray()); }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        var reason = RecordInjectionFailure(ex); ReturnLocked(reason, reason);
                        session.Send(Frame.Json(PacketKind.DenyControl, new ControlReply(request.Epoch, reason))); return;
                    }
                    session.Send(Frame.Json(PacketKind.GrantControl, new ControlReply(request.Epoch)));
                    Status = $"{PeerLabel(session)} controla este equipo"; Changed?.Invoke();
                    break;
                case PacketKind.GrantControl:
                    var grant = frame.Read<ControlReply>();
                    if (session != pending || grant.Epoch != pendingEpoch) { session.Send(Frame.Json(PacketKind.ReleaseControl, grant)); return; }
                    outgoing = session; outgoingEpoch = grant.Epoch; pending = null; sequence = x = y = 0;
                    var park = desktop.ParkingPosition; anchor = desktop.ConstrainPosition(park.X, park.Y);
                    Volatile.Write(ref parkingX, anchor.X); Volatile.Write(ref parkingY, anchor.Y); Volatile.Write(ref parkingAcknowledged, 0);
                    parkingSince = Stopwatch.GetTimestamp(); parkingPending = RequirePointerParkingAcknowledgement;
                    try { desktop.SetPosition(anchor.X, anchor.Y); }
                    catch (System.ComponentModel.Win32Exception ex) { var reason = RecordInjectionFailure(ex); ReturnLocked(reason, reason); return; }
                    foreach (var key in physicalKeys.ToArray())
                    {
                        if (KeyboardPacket.ToggleBit(key.Key) != 0) continue;
                        try { desktop.Inject(Native.Key(key.Key, key.Value, true)); } catch (System.ComponentModel.Win32Exception) { }
                        var keyData = outgoing.InputRevision >= 2 ? KeyboardPacket.Pack(key.Value, CurrentKeyboard()) : key.Value;
                        outgoing.Send(Frame.FromInput(new(InputKind.KeyDown, ++sequence, outgoingEpoch, 0, 0, key.Key, keyData)));
                    }
                    Status = $"Controlando {PeerLabel(session)}"; Changed?.Invoke();
                    break;
                case PacketKind.DenyControl:
                    var denied = frame.Read<ControlReply>();
                    if (session == pending && denied.Epoch == pendingEpoch) ReturnLocked(denied.Reason);
                    break;
                case PacketKind.ReleaseControl:
                    var release = frame.Read<ControlReply>();
                    if ((session == incoming && release.Epoch == state.Epoch) || (session == outgoing && release.Epoch == outgoingEpoch) || (session == pending && release.Epoch == pendingEpoch))
                    {
                        if (release.Reason == null || release.Reason.Length > 480 || release.Reason.Any(char.IsControl)) throw new InvalidDataException("Motivo de liberación inválido.");
                        var reason = string.IsNullOrWhiteSpace(release.Reason) ? "El control está en este equipo" : release.Reason;
                        ReturnLocked(reason);
                        if (release.Reason.Length > 0) hub.Log(reason, true);
                    }
                    break;
                case PacketKind.Boundary:
                    var boundary = frame.Read<BoundaryNotice>();
                    if (session != outgoing || boundary.Epoch != outgoingEpoch) return;
                    ReturnLocked("El control está en este equipo");
                    var local = desktop.Bounds;
                    if (!double.IsFinite(boundary.X) || !double.IsFinite(boundary.Y)) throw new InvalidDataException();
                    var backX = boundary.Side == "left" ? local.Left + local.Width - 5 : boundary.Side == "right" ? local.Left + 4 : local.Left + (int)(Math.Clamp(boundary.X, 0, 1) * (local.Width - 1));
                    var backY = boundary.Side == "top" ? local.Top + local.Height - 5 : boundary.Side == "bottom" ? local.Top + 4 : local.Top + (int)(Math.Clamp(boundary.Y, 0, 1) * (local.Height - 1));
                    desktop.SetPosition(backX, backY);
                    break;
                case PacketKind.Input:
                    if (Paused || incoming != session || !settings.AllowControl) return;
                    var input = frame.ReadInput();
                    if (!state.TryAdvance(input, out var dx, out var dy)) return;
                    Diagnostics.Received();
                    Apply(session, input, dx, dy);
                    break;
            }
        }
    }
    private void Apply(PeerSession session, InputEvent input, int dx, int dy)
    {
        var b = desktop.Bounds;
        // Keep the intended position independently of the asynchronous OS queue.
        // Reading GetCursorPos for every frame can observe the previous injection.
        var point = targetPosition;
        var targetX = point.X + dx; var targetY = point.Y + dy;
        if (input.Kind == InputKind.Move && buttons.Count == 0 && ((returnEdge == "left" && targetX < b.Left - 2) || (returnEdge == "right" && targetX > b.Left + b.Width + 1) || (returnEdge == "top" && targetY < b.Top - 2) || (returnEdge == "bottom" && targetY > b.Top + b.Height + 1)))
        {
            session.Send(Frame.Json(PacketKind.Boundary, new BoundaryNotice(state.Epoch, returnEdge, (double)(point.Y - b.Top) / Math.Max(1, b.Height), (double)(point.X - b.Left) / Math.Max(1, b.Width))));
            incoming = null; state.End(); ReleaseInjected(); Status = "El control está en este equipo"; Changed?.Invoke(); return;
        }
        // The dominant path needs neither List<Input> nor a params array.
        if (input.Kind == InputKind.Move)
        {
            if (dx == 0 && dy == 0) return;
            targetPosition = desktop.ConstrainPosition(Math.Clamp(targetX, b.Left, b.Left + b.Width - 1), Math.Clamp(targetY, b.Top, b.Top + b.Height - 1));
            try { desktop.InjectOne(Native.MoveTo(targetPosition.X, targetPosition.Y, b)); }
            catch (System.ComponentModel.Win32Exception ex) { var reason = RecordInjectionFailure(ex); ReturnLocked(reason, reason); }
            return;
        }
        var inputs = new List<Native.Input>();
        if (dx != 0 || dy != 0)
        {
            targetPosition = desktop.ConstrainPosition(Math.Clamp(targetX, b.Left, b.Left + b.Width - 1), Math.Clamp(targetY, b.Top, b.Top + b.Height - 1));
            inputs.Add(Native.MoveTo(targetPosition.X, targetPosition.Y, b));
        }
        switch (input.Kind)
        {
            case InputKind.ButtonDown:
                if (input.Code is < 1 or > 5) throw new InvalidDataException();
                if (buttons.Add(input.Code)) inputs.Add(Native.MouseButton(input.Code, false)); break;
            case InputKind.ButtonUp:
                if (buttons.Remove(input.Code)) inputs.Add(Native.MouseButton(input.Code, true)); break;
            case InputKind.Wheel: case InputKind.HorizontalWheel:
                if (input.Data is < -12000 or > 12000) throw new InvalidDataException();
                inputs.Add(Native.Wheel(input.Data, input.Kind == InputKind.HorizontalWheel)); break;
            case InputKind.KeyDown:
            case InputKind.KeyUp:
                ApplyKeyboard(inputs, input, session.InputRevision); break;
        }
        if (inputs.Count > 0)
        {
            try { desktop.Inject(inputs.ToArray()); }
            catch (System.ComponentModel.Win32Exception ex) { var reason = RecordInjectionFailure(ex); ReturnLocked(reason, reason); }
        }
    }
    public void AcknowledgePointerParking(Native.Point point)
    {
        // Called in the hook while SendInput may still be running on a different
        // thread. Taking gate here would create a hook/network-thread deadlock.
        if (Math.Abs(point.X - Volatile.Read(ref parkingX)) <= 2 && Math.Abs(point.Y - Volatile.Read(ref parkingY)) <= 2)
        {
            Volatile.Write(ref parkedActualX, point.X); Volatile.Write(ref parkedActualY, point.Y);
            Volatile.Write(ref parkingAcknowledged, 1);
        }
    }
    private string RecordInjectionFailure(System.ComponentModel.Win32Exception ex)
    {
        Diagnostics.InjectionFailure();
        var reason = desktop.DescribeInjectionFailure(hub.Identity.Name, ex.NativeErrorCode);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 480 || reason.Any(char.IsControl)) reason = InputFailureInspector.Format(hub.Identity.Name, ex.NativeErrorCode, null, null, null);
        hub.Log(reason, true); return reason;
    }
}
