using System.Windows;

namespace BorderLess.App;

public partial class MainWindow
{
    private KeepAwake keepAwake = new();
    private bool powerSuspended;
    private int powerRefreshQueued;
    private void QueuePowerRefresh()
    {
        if (closing || Interlocked.Exchange(ref powerRefreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(new Action(() => { Interlocked.Exchange(ref powerRefreshQueued, 0); if (!closing) RefreshPower(); }));
    }
    private void RefreshPower()
    {
        keepAwake.Update(store.Settings.KeepAwakeWhenConnected, hub.Inputs.Values.Any(s => s.Connected), router.Paused, router.SessionLocked, powerSuspended, closing);
        UiText.Text(KeepAwakeStatus, keepAwake.Status);
    }
    private void KeepAwakePreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        store.Settings.KeepAwakeWhenConnected = KeepAwakeWhenConnected.IsChecked == true;
        store.Save(); RefreshPower();
    }
}
