using System.Windows;

namespace BorderLess.App;

public partial class MainWindow
{
    private CancellationTokenSource? latencyCancellation;
    private sealed record LatencyPeer(Guid Id, string Label)
    {
        public override string ToString() => Label;
    }

    private void RefreshLatencyControls()
    {
        if (latencyCancellation != null) return;
        var peers = hub.Inputs.Values.Where(s => s.Connected)
            .Select(s => new LatencyPeer(s.Peer.Id, store.Settings.Peers.Find(p => p.Id == s.Peer.Id)?.DisplayName ?? s.Peer.Name))
            .OrderBy(p => p.Label).ThenBy(p => p.Id).ToArray();
        if (LatencyPeerChoice.ItemsSource is not LatencyPeer[] current || !current.SequenceEqual(peers))
        {
            var selected = (LatencyPeerChoice.SelectedItem as LatencyPeer)?.Id;
            LatencyPeerChoice.ItemsSource = peers;
            LatencyPeerChoice.SelectedItem = peers.FirstOrDefault(p => p.Id == selected) ?? peers.FirstOrDefault();
        }
        LatencyPeerChoice.IsEnabled = peers.Length > 1;
        LatencyButton.IsEnabled = peers.Length > 0;
        UiText.Content(LatencyButton, "Medir durante 30 segundos");
    }

    private async void MeasureLatency(object sender, RoutedEventArgs e)
    {
        if (latencyCancellation != null) { latencyCancellation.Cancel(); return; }
        if (LatencyPeerChoice.SelectedItem is not LatencyPeer peer || !hub.Inputs.TryGetValue(peer.Id, out var session)) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        latencyCancellation = request;
        LatencyPeerChoice.IsEnabled = false;
        UiText.Content(LatencyButton, "Cancelar medición");
        UiText.Text(LatencyStatus, "Midiendo. Usa el mouse con normalidad durante 30 segundos.");
        string message;
        try
        {
            var sample = await session.MeasureLatencyAsync(request.Token);
            message = sample.Samples == 0 ? $"Sin muestras de RTT para {peer.Label}." :
                $"De {store.Settings.DisplayName} a {peer.Label} · RTT mediano {sample.MedianMs:F1} ms · pico {sample.PeakMs:F1} ms · {sample.Samples} muestras · cola local máxima observada {sample.ObservedQueuePeak}.";
            hub.Log(message);
        }
        catch (OperationCanceledException)
        {
            message = request.IsCancellationRequested ? "Medición cancelada." : "Medición interrumpida al desconectarse el equipo.";
        }
        finally { latencyCancellation = null; }
        if (closing) return;
        UiText.Text(LatencyStatus, message);
        RefreshLatencyControls();
    }
}
