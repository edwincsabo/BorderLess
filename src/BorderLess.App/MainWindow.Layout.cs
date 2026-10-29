using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BorderLess.App;

public partial class MainWindow
{
    private readonly WindowRefreshGate viewGate = new();
    private readonly Queue<Diagnostic> history = new();
    private Action refreshView = null!, refreshTray = null!;
    private int trayPending, metricsTicks;
    private bool devicesDirty = true, mapDirty = true, historyDirty = true;
    private Guid aliasTarget;
    private DispatcherTimer? displayDebounce;
    private void InitializePresentation()
    {
        refreshView = () =>
        {
            if (!viewGate.Consume() || closing) return;
            if (devicesDirty)
            {
                devicesDirty = false; devices.Clear();
                foreach (var peer in store.Settings.Peers.OrderBy(p => p.Edge == "left" ? 0 : p.Edge == "right" ? 1 : 2)) devices.Add(new DeviceViewModel(peer, SetEdge));
            }
            UpdateStatus();
            if (mapDirty && DevicesPage.IsVisible) { mapDirty = false; RefreshMap(); }
            if (historyDirty && ActivityPage.IsVisible) { historyDirty = false; activity.Clear(); foreach (var item in history.Reverse()) activity.Add(item); }
        };
        refreshTray = () => { Interlocked.Exchange(ref trayPending, 0); if (!closing) UpdateTrayStatus(); };
        IsVisibleChanged += (_, _) => { viewGate.SetVisible(IsVisible && WindowState != WindowState.Minimized); if (viewGate.Visible) QueueStatusRefresh(); };
        LayoutMap.EditingStarted += () => router.EmergencyReturn("Organizando equipos. El control está en este PC.");
        LayoutMap.EditingFinished += () => router.EmergencyReturn();
        LayoutMap.LayoutSaved += values =>
        {
            PlacementRules.Validate(values);
            if (values.Count != store.Settings.Peers.Count || store.Settings.Peers.Any(p => !values.ContainsKey(p.Id))) { hub.Log("Los equipos cambiaron. Vuelve a organizar la distribución.", true); RefreshDevices(); return; }
            foreach (var peer in store.Settings.Peers) peer.Edge = values[peer.Id];
            store.Save(); router.UpdateNeighbors(); foreach (var item in devices) item.RefreshEdge();
            mapDirty = true; QueueStatusRefresh(); hub.Log("Distribución de equipos guardada.");
        };
        LayoutMap.RenameRequested += OpenAlias;
        LayoutMap.ControlRequested += ControlDevice;
        displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        displayDebounce.Tick += (_, _) => { displayDebounce.Stop(); router.EmergencyReturn("Cambió la disposición de pantallas. Control local recuperado."); PublishDesktop(); };
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        hub.DesktopChanged += id => Dispatcher.BeginInvoke(new Action(() => RememberDesktop(id)));
        PublishDesktop();
    }
    private void DisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() => { if (closing) return; displayDebounce?.Stop(); displayDebounce?.Start(); }));
    private void PublishDesktop()
    {
        hub.UpdateDesktop(DesktopInventory.Capture(store.Settings.DisplayName));
        LocalName.Text = store.Settings.DisplayName; LocalMachineName.Text = store.Settings.Name;
        mapDirty = true; QueueStatusRefresh();
    }
    private void RememberDesktop(Guid id)
    {
        if (closing) return;
        var peer = store.Settings.Peers.Find(p => p.Id == id);
        if (peer != null && hub.RemoteDesktops.TryGetValue(id, out var desktop) && !DesktopSnapshot.Same(peer.LastDesktop, desktop))
        {
            peer.LastDesktop = desktop; store.Save(); router.UpdateNeighbors();
            foreach (var item in devices.Where(d => d.Id == id)) item.RefreshName();
        }
        mapDirty = true; QueueStatusRefresh();
    }
    private void RefreshMap()
    {
        var rows = new List<MapDevice> { new(store.Settings.Id, store.Settings.DisplayName, store.Settings.Name, "local", true, true, hub.LocalDesktop) };
        foreach (var p in store.Settings.Peers) rows.Add(new(p.Id, p.DisplayName, p.Name, p.Edge, false, hub.Inputs.TryGetValue(p.Id, out var s) && s.Connected, hub.RemoteDesktops.GetValueOrDefault(p.Id) ?? p.LastDesktop, s?.LayoutRevision >= 1));
        LayoutMap.SetDevices(rows);
    }
    private void QueueStatusRefresh()
    {
        viewGate.MarkDirty(); if (refreshView != null && viewGate.TrySchedule()) Dispatcher.BeginInvoke(DispatcherPriority.Background, refreshView);
    }
    private void QueueTrayRefresh()
    {
        if (refreshTray != null && Interlocked.Exchange(ref trayPending, 1) == 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, refreshTray);
    }
    private void UpdateTrayStatus()
    {
        if (tray == null) return;
        var text = router.Paused ? router.ManualPause ? "BorderLess · pausa manual" : "BorderLess · pausa por bloqueo" : "BorderLess · " + (router.ActivePeer != null ? "control remoto" : "control local");
        if (tray.Text != text) tray.Text = UiLanguage.Text(text);
        if (pauseMenu != null)
        {
            pauseMenu.Text = UiLanguage.Text(router.Paused ? "Reanudar uso compartido" : "Pausar uso compartido");
            pauseMenu.Enabled = !router.SessionLocked;
        }
    }
    private void OpenAlias(Guid id)
    {
        var peer = store.Settings.Peers.Find(p => p.Id == id);
        if (id != store.Settings.Id && peer == null) return;
        aliasTarget = id; UiText.Text(AliasMachine, "Nombre real: " + (peer?.Name ?? store.Settings.Name));
        AliasInput.Text = peer?.Alias ?? store.Settings.Alias; UiText.Text(AliasError, ""); AliasOverlay.Visibility = Visibility.Visible;
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(AliasOverlay, System.Windows.Input.KeyboardNavigationMode.Cycle);
        AliasInput.Focus(); AliasInput.SelectAll();
    }
    private void RenameLocal(object sender, RoutedEventArgs e) => OpenAlias(store.Settings.Id);
    private void RenamePeer(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is Guid id) OpenAlias(id); }
    private void ControlDevice(Guid id)
    {
        if (!hub.Inputs.TryGetValue(id, out var session)) return;
        var edge = store.Settings.Peers.Find(p => p.Id == id)?.Edge;
        var entry = PlacementRules.Opposite(edge ?? "none");
        if (entry is "top" or "bottom" && session.LayoutRevision < 1) entry = "center";
        router.Request(session, entry);
    }
    private void SaveAlias(object sender, RoutedEventArgs e)
    {
        try
        {
            store.SetAlias(aliasTarget, AliasInput.Text); AliasOverlay.Visibility = Visibility.Collapsed;
            if (aliasTarget == store.Settings.Id) PublishDesktop();
            router.UpdateNeighbors(); foreach (var item in devices) item.RefreshName();
            mapDirty = filesDirty = true; QueueStatusRefresh(); DevicesNav.Focus();
        }
        catch (ArgumentException ex) { UiText.Text(AliasError, ex.Message); }
    }
    private void ResetAlias(object sender, RoutedEventArgs e) { AliasInput.Clear(); SaveAlias(sender, e); }
    private void CloseAlias(object sender, RoutedEventArgs e) { AliasOverlay.Visibility = Visibility.Collapsed; DevicesNav.Focus(); }
}
