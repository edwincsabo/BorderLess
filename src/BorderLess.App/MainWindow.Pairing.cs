using System.Net.Sockets;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BorderLess.App;

public partial class MainWindow
{
    private CancellationTokenSource? pairingUi, discoverySearch;
    private LanPairDiscovery.Responder? pinResponder;
    private DispatcherTimer? pinTimer;
    private bool pinConnecting;
    private void InitializePinPairing()
    {
        pinTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        pinTimer.Tick += (_, _) => RefreshPinStatus();
        hub.PinStateChanged += () => Dispatcher.BeginInvoke(new Action(() => { if (!closing && PairOverlay.IsVisible) RefreshPinStatus(); }));
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopPinPairing(); };
    }
    private void StartPinPairing()
    {
        pairingUi?.Cancel(); pairingUi = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UiText.Text(PairLocalAddress, $"Dirección de este PC: {PeerCrypto.LocalAddress()} · puerto {hub.Port}");
        PairTabs.SelectedIndex = 0; pinTimer?.Start(); RefreshPinStatus(); _ = FindPeersAsync();
    }
    private void StopPinPairing()
    {
        pairingUi?.Cancel(); discoverySearch?.Cancel(); pinTimer?.Stop(); hub.RevokeShortCode();
        PinInput.Clear(); UiText.Text(PinOutput, "Sin código activo");
        PairOverlay.Visibility = Visibility.Collapsed;
        _ = StopPinResponderAsync();
    }
    private async Task StopPinResponderAsync()
    {
        var responder = pinResponder; pinResponder = null;
        if (responder != null) await responder.DisposeAsync();
    }
    private void RefreshPinStatus()
    {
        if (closing) return;
        var status = hub.ShortCodeStatus;
        if (!status.Active)
        {
            if (PinOutput.Text != "Sin código activo") UiText.Text(PinOutput, "Sin código activo");
            UiText.Text(PinExpiry, "Sin código disponible. Genera uno nuevo para vincular otro PC.");
            _ = StopPinResponderAsync();
        }
        else
        {
            var seconds = Math.Max(0, (int)(status.Expires - DateTimeOffset.UtcNow).TotalSeconds);
            UiText.Text(PinExpiry, $"Vence en {seconds / 60}:{seconds % 60:00} · {status.AttemptsRemaining} intento(s) disponible(s)" + (status.Busy ? " · verificando solicitud" : ""));
        }
    }
    private async void GeneratePin(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopPinResponderAsync();
            if (!PairOverlay.IsVisible || pairingUi?.IsCancellationRequested != false) return;
            var code = hub.CreateShortCode(); UiText.Text(PinOutput, code.Code.Insert(4, " ")); PairFeedbackBox.Visibility = Visibility.Collapsed;
            try { pinResponder = new LanPairDiscovery.Responder(hub); }
            catch (SocketException) { PairFeedbackMessage("El código está listo. La detección local no pudo iniciarse; escribe la dirección y el puerto que aparecen arriba en el otro PC."); }
            RefreshPinStatus();
        }
        catch (InvalidOperationException ex) { PairFeedbackMessage(ex.Message); }
    }
    private void RevokePin(object sender, RoutedEventArgs e) { hub.RevokeShortCode(); RefreshPinStatus(); }
    private async void SearchPeers(object sender, RoutedEventArgs e) => await FindPeersAsync();
    private async Task FindPeersAsync()
    {
        if (pairingUi?.IsCancellationRequested != false || !PairOverlay.IsVisible) return;
        discoverySearch?.Cancel(); var request = CancellationTokenSource.CreateLinkedTokenSource(pairingUi.Token); discoverySearch = request;
        SearchPeersButton.IsEnabled = false; UiText.Text(DiscoveryStatus, "Buscando durante dos segundos…");
        try
        {
            var peers = await LanPairDiscovery.SearchAsync(store.Settings.Id, request.Token);
            if (!ReferenceEquals(discoverySearch, request) || !PairOverlay.IsVisible) return;
            DiscoveredPeers.ItemsSource = peers;
            if (peers.Count > 0) DiscoveredPeers.SelectedIndex = 0;
            UiText.Text(DiscoveryStatus, peers.Count == 0 ? "No se encontraron equipos. Genera un código en el receptor y vuelve a buscar, o escribe su dirección manualmente." : $"{peers.Count} equipo(s) encontrado(s). El código comprobará la conexión al vincular.");
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { UiText.Text(DiscoveryStatus, "La búsqueda no está disponible en esta red. Puedes escribir la dirección del receptor."); }
        finally
        {
            if (ReferenceEquals(discoverySearch, request)) { discoverySearch = null; SearchPeersButton.IsEnabled = !pinConnecting; }
            request.Dispose();
        }
    }
    private void DiscoveredPeerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DiscoveredPeers.SelectedItem is DiscoveredPairPeer peer) { PinHost.Text = peer.Host; PinPort.Text = peer.Port.ToString(); }
    }
    private async void ConnectWithPin(object sender, RoutedEventArgs e)
    {
        if (pinConnecting || pairingUi?.IsCancellationRequested != false) return;
        pinConnecting = true; PinConnectButton.IsEnabled = SearchPeersButton.IsEnabled = false;
        UiText.Content(PinConnectButton, "Conectando…");
        try
        {
            if (!int.TryParse(PinPort.Text, out var port)) throw new ArgumentException("El puerto debe ser un número entre 1 y 65535.");
            var choice = DiscoveredPeers.SelectedItem as DiscoveredPairPeer;
            var selected = choice != null && choice.Host == PinHost.Text.Trim() && choice.Port == port;
            PairFeedbackMessage("Verificando el PIN y conectando. No hace falta confirmar en el otro PC.");
            var peer = await hub.PairWithShortCodeAsync(PinHost.Text, port, PinInput.Text, selected ? choice!.Id : Guid.Empty, selected ? choice!.Offer : Guid.Empty, pairingUi.Token);
            if (PairOverlay.IsVisible) { PinInput.Clear(); PairFeedbackMessage($"{peer.Name} está vinculado. Ya puedes cerrar este panel y controlarlo desde Equipos."); }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { if (PairOverlay.IsVisible) PairFeedbackMessage(ex.Message); }
        catch (OperationCanceledException) { if (PairOverlay.IsVisible) PairFeedbackMessage("Vinculación cancelada o tiempo agotado. Genera otro código en el receptor si lo necesitas."); }
        catch (AuthenticationException ex) { if (PairOverlay.IsVisible) PairFeedbackMessage(ex.Message); }
        catch (Exception ex) when (ex is IOException or SocketException or System.Text.Json.JsonException or InvalidDataException)
        { if (PairOverlay.IsVisible) PairFeedbackMessage("No se completó la vinculación. Comprueba dirección, puerto y código vigente; Ambos PCs deben estar actualizados para vincular sin confirmación adicional."); }
        finally { pinConnecting = false; UiText.Content(PinConnectButton, "Vincular con PIN"); PinConnectButton.IsEnabled = SearchPeersButton.IsEnabled = true; }
    }
}
