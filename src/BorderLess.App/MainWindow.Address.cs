using System.Net.Sockets;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BorderLess.App;

public partial class MainWindow
{
    private Guid addressPeer;
    private CancellationTokenSource? addressRequest;

    private void OpenAddress(object sender, RoutedEventArgs e)
    {
        if (addressRequest != null || ((Button)sender).Tag is not Guid id) return;
        var peer = store.Settings.Peers.Find(p => p.Id == id);
        if (peer == null || !peer.Initiator || !string.IsNullOrEmpty(peer.RelayRoom)) return;
        router.EmergencyReturn();
        addressPeer = id;
        AddressPeerName.Text = peer.DisplayName;
        AddressHost.Text = peer.Host;
        AddressPort.Text = peer.Port.ToString();
        UiText.Text(AddressFeedback, "");
        AddressOverlay.Visibility = Visibility.Visible;
        KeyboardNavigation.SetTabNavigation(AddressOverlay, KeyboardNavigationMode.Cycle);
        AddressHost.Focus(); AddressHost.SelectAll();
    }

    private void CloseAddress(object sender, RoutedEventArgs e)
    {
        addressRequest?.Cancel();
        AddressOverlay.Visibility = Visibility.Collapsed;
        DevicesNav.Focus();
    }

    private async void VerifyAddress(object sender, RoutedEventArgs e)
    {
        if (addressRequest != null) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        addressRequest = request;
        VerifyAddressButton.IsEnabled = AddressHost.IsEnabled = AddressPort.IsEnabled = false;
        UiText.Text(AddressFeedback, "Verificando la conexión con el equipo vinculado…");
        try
        {
            if (!int.TryParse(AddressPort.Text, out var port)) throw new ArgumentException("Indica una IP o nombre de equipo y un puerto válido.");
            await hub.ReconnectAtAsync(addressPeer, AddressHost.Text, port, store.SaveAddress, request.Token);
            if (closing) return;
            AddressOverlay.Visibility = Visibility.Collapsed;
            hub.Log("Dirección verificada y guardada para la reconexión.");
            RefreshDevices(); DevicesNav.Focus();
        }
        catch (OperationCanceledException) { if (!request.IsCancellationRequested) UiText.Text(AddressFeedback, "El equipo no respondió a tiempo. Revisa la dirección y el puerto."); }
        catch (Exception ex) when (ex is AuthenticationException or InvalidDataException or System.Text.Json.JsonException or FormatException)
        { UiText.Text(AddressFeedback, "No se pudo verificar la identidad del equipo en esa dirección."); }
        catch (SocketException) { UiText.Text(AddressFeedback, "No se pudo conectar. Revisa la dirección y el puerto del receptor."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { UiText.Text(AddressFeedback, "No se pudo completar la conexión o guardar la dirección."); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { UiText.Text(AddressFeedback, ex.Message); }
        finally { addressRequest = null; VerifyAddressButton.IsEnabled = AddressHost.IsEnabled = AddressPort.IsEnabled = true; }
    }
}
