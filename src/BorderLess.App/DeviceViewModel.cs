using System.ComponentModel;
using System.Windows.Media;

namespace BorderLess.App;

public sealed class DeviceViewModel : INotifyPropertyChanged
{
    private readonly SavedPeer peer;
    private readonly Action<SavedPeer, string> edgeChanged;
    public Guid Id => peer.Id;
    public string Name => peer.Name;
    public string DisplayName => peer.DisplayName;
    public bool Online { get; private set; }
    public string Status { get; private set; } = "Sin conexión";
    public string Detail { get; private set; } = "";
    private static readonly Brush Ready = CreateBrush(18, 102, 92), Offline = CreateBrush(89, 106, 102);
    private static Brush CreateBrush(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    public Brush StatusColor => System.Windows.Application.Current == null ? Online ? Ready : Offline : ThemeManager.Brush(Online ? "AccentTextBrush" : "MutedBrush");
    public string Edge { get => peer.Edge; set { if (value == peer.Edge || !PlacementRules.Edges.Contains(value)) return; edgeChanged(peer, value); OnChanged(nameof(Edge)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public DeviceViewModel(SavedPeer peer, Action<SavedPeer, string> edgeChanged) { this.peer = peer; this.edgeChanged = edgeChanged; }
    public void Update(NetworkHub hub, Guid? active, Guid? controlling, bool paused)
    {
        var online = hub.Inputs.TryGetValue(Id, out var session) && session.Connected;
        var status = !online ? "Sin conexión · vínculo guardado" : paused ? "Conectado · entrada pausada" : active == Id ? "Control activo en este equipo remoto" : controlling == Id ? "Este equipo está controlando tu PC" : "Conectado · listo para controlar";
        var detail = online ? $"TLS cifrado  ·  {(session!.Rtt > 0 ? $"RTT {session.Rtt:F0} ms" : "midiendo red")}  ·  {(hub.Clipboards.ContainsKey(Id) ? "portapapeles conectado" : "portapapeles reconectando")}" : peer.Initiator ? (peer.RelayRoom == "" ? $"Conexión directa · {peer.Host}:{peer.Port}" : "Conexión mediante relay") : "Se reconectará cuando el otro PC vuelva a estar disponible";
        if (online != Online) { Online = online; OnChanged(nameof(Online)); OnChanged(nameof(StatusColor)); }
        if (Status != status) { Status = status; OnChanged(nameof(Status)); }
        if (Detail != detail) { Detail = detail; OnChanged(nameof(Detail)); }
    }
    public void RefreshEdge() => OnChanged(nameof(Edge));
    public void RefreshName() { OnChanged(nameof(DisplayName)); OnChanged(nameof(Name)); }
    private void OnChanged(string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
