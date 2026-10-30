using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BorderLess.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly NetworkHub hub;
    private readonly InputRouter router;
    private readonly FileTransferService files;
    private readonly ObservableCollection<DeviceViewModel> devices = new();
    private readonly ObservableCollection<Diagnostic> activity = new();
    private readonly DispatcherTimer timer;
    private readonly CancellationTokenSource lifetime = new();
    private InputCapture? capture;
    private ClipboardSync? clipboard;
    private RemotePointer? remotePointer;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripItem? pauseMenu;
    private HwndSource? source;
    private bool initialized, closing;
    public MainWindow(SettingsStore store)
    {
        UiLanguage.Instance.Use(store.Settings.Language);
        InitializeComponent();
        this.store = store;
        hub = new NetworkHub(store.LoadIdentity());
        foreach (var peer in store.Settings.Peers) hub.Credentials[peer.Id] = peer.Credentials();
        router = new InputRouter(hub, store.Settings);
        files = new FileTransferService(hub, store.ReceiveDirectory);
        router.Changed += () => { SyncFilesEnabled(); QueueTrayRefresh(); QueueStatusRefresh(); QueuePowerRefresh(); };
        hub.Activity += diagnostic => Dispatcher.BeginInvoke(new Action(() => AddActivity(diagnostic)));
        hub.Paired += peer => Dispatcher.Invoke(() => { store.SavePeer(peer); RememberDesktop(peer.Id); RefreshDevices(); PairFeedbackMessage($"{peer.Name} está vinculado. La conexión ya está cifrada."); });
        hub.Connected += session => Dispatcher.BeginInvoke(new Action(() => { RememberDesktop(session.Peer.Id); QueueTrayRefresh(); QueueStatusRefresh(); RefreshPower(); }));
        hub.Disconnected += (_, _) => Dispatcher.BeginInvoke(new Action(() => { mapDirty = true; QueueTrayRefresh(); QueueStatusRefresh(); if (!closing) RefreshPower(); }));
        PeerList.ItemsSource = devices; ActivityList.ItemsSource = activity;
        LocalName.Text = store.Settings.DisplayName;
        AllowControl.IsChecked = store.Settings.AllowControl; AutoEdges.IsChecked = store.Settings.AutoEdges;
        ShareText.IsChecked = store.Settings.ShareText; ShareImages.IsChecked = store.Settings.ShareImages;
        PointerFallback.IsChecked = store.Settings.PointerFallback;
        PauseWhenLocked.IsChecked = store.Settings.PauseOnLock;
        KeepAwakeWhenConnected.IsChecked = store.Settings.KeepAwakeWhenConnected;
        RelayHost.Text = store.Settings.RelayHost; RelayPort.Text = store.Settings.RelayPort.ToString();
        InitializePresentation();
        InitializeFiles();
        InitializePinPairing();
        ThemeChoice.SelectedIndex = store.Settings.DarkMode ? 1 : 0;
        LanguageChoice.SelectedValue = UiLanguage.Instance.Code;
        UiLanguage.Instance.PropertyChanged += LanguageUpdated;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { router.RefreshLocalKeyboardState(); router.Tick(); if (++metricsTicks % 2 == 0 && viewGate.Visible && DevicesPage.IsVisible) QueueStatusRefresh(); }, Dispatcher);
        initialized = true;
        RefreshPower();
        RefreshDevices();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && tray != null) Hide(); viewGate.SetVisible(IsVisible && WindowState != WindowState.Minimized); if (viewGate.Visible) QueueStatusRefresh(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && PairOverlay.Visibility == Visibility.Visible) { ClosePairing(this, new RoutedEventArgs()); e.Handled = true; } };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && AliasOverlay.Visibility == Visibility.Visible) { CloseAlias(this, new RoutedEventArgs()); e.Handled = true; } };
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerChanged;
    }
    private void WindowReady(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        ThemeManager.Caption(handle);
        source = HwndSource.FromHwnd(handle); source?.AddHook(WindowMessage);
        if (!Native.RegisterHotKey(handle, 100, 0x4003, 0x7b)) hub.Log("El atajo global ya está reservado. El hook también reconoce Ctrl+Alt+F12.", true);
        try
        {
            capture = new InputCapture(router);
            remotePointer = new RemotePointer(router, store.Settings);
            clipboard = new ClipboardSync(handle, hub, store.Settings, router, Dispatcher);
            clipboard.Changed += QueueStatusRefresh;
            hub.Start(store.Settings.Port);
            UiText.Text(ListeningStatus, "Disponible · recepción activa");
            UiText.Text(LocalAddress, $"Red local · {PeerCrypto.LocalAddress()}:{hub.Port}");
        }
        catch (Exception ex) when (ex is SocketException or Win32Exception or InvalidOperationException or TimeoutException)
        { UiText.Text(ListeningStatus, "No se pudo iniciar la recepción"); hub.Log(ex.Message, true); UiText.Text(ControlStatus, ex.Message); }
        trayIcon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/BorderLess;component/Assets/BorderLess.ico")).Stream);
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "BorderLess · control local", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir BorderLess", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow)));
        menu.Items.Add("Volver a este equipo", null, (_, _) => router.EmergencyReturn());
        pauseMenu = menu.Items.Add("Pausar uso compartido", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => TogglePause(this, new RoutedEventArgs()))));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Salir", null, (_, _) => Dispatcher.BeginInvoke(new Action(ExitApp)));
        menu.Opening += (_, _) => { menu.Items[0].Text = UiLanguage.Text("Abrir BorderLess"); menu.Items[1].Text = UiLanguage.Text("Volver a este equipo"); menu.Items[4].Text = UiLanguage.Text("Salir"); UpdateTrayStatus(); };
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow));
        tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFiles));
        hub.Log("BorderLess iniciado. Ctrl+Alt+F12 devuelve el control local.");
    }
    private IntPtr WindowMessage(IntPtr h, int message, IntPtr wp, IntPtr lp, ref bool handled)
    { if (message == 0x312 && wp.ToInt32() == 100) { router.EmergencyReturn("Control local recuperado con Ctrl+Alt+F12."); handled = true; } return IntPtr.Zero; }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) Dispatcher.BeginInvoke(new Action(() => ApplySessionLock(true)));
        else if (e.Reason == SessionSwitchReason.SessionUnlock) Dispatcher.BeginInvoke(new Action(() => ApplySessionLock(false)));
        else if (e.Reason == SessionSwitchReason.SessionLogoff) Dispatcher.BeginInvoke(new Action(() => { StopPinPairing(); router.Pause(true); hub.Log("Cierre de sesión: uso compartido pausado."); }));
    }
    private void ApplySessionLock(bool locked)
    {
        if (closing) return;
        if (locked) StopPinPairing();
        router.SetSessionLocked(locked);
        RefreshPower();
        hub.Log(locked ? store.Settings.PauseOnLock ? "Windows bloqueado: pausa automática del uso compartido; se reanudará al desbloquear salvo pausa manual." : "Windows bloqueado: servicios activos por preferencia; control liberado y PIN revocado." : router.Paused ? "Windows desbloqueado: se conserva la pausa manual." : "Windows desbloqueado: uso compartido activo.");
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume) Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closing) return;
            powerSuspended = e.Mode == PowerModes.Suspend;
            if (powerSuspended) { StopPinPairing(); router.EmergencyReturn("Suspensión: control local recuperado."); }
            RefreshPower();
        }));
    }
    private void AddActivity(Diagnostic diagnostic)
    {
        history.Enqueue(diagnostic); while (history.Count > 150) history.Dequeue();
        if (viewGate.Visible && ActivityPage.IsVisible) { activity.Insert(0, diagnostic); while (activity.Count > 150) activity.RemoveAt(activity.Count - 1); }
        else historyDirty = true;
    }
    private void RefreshDevices()
    {
        router.UpdateNeighbors();
        devicesDirty = mapDirty = true; QueueStatusRefresh();
    }
    private void SetEdge(SavedPeer peer, string edge)
    {
        foreach (var other in store.Settings.Peers.Where(p => p.Id != peer.Id && edge != "none" && p.Edge == edge)) other.Edge = "none";
        peer.Edge = edge; store.Save(); router.UpdateNeighbors(); foreach (var device in devices) device.RefreshEdge(); mapDirty = true; QueueStatusRefresh();
        hub.Log($"Se actualizó el borde de acceso a {peer.Name}.");
    }
    private void UpdateStatus()
    {
        if (closing || !viewGate.Visible) return;
        var pendingFiles = files.Snapshot().Count(t => t.Incoming && t.State == TransferState.Waiting);
        var fileLabel = pendingFiles == 0 ? "Archivos" : $"Archivos · {pendingFiles} pendiente(s)";
        if (!Equals(FilesNav.Content, fileLabel)) UiText.Content(FilesNav, fileLabel);
        foreach (var device in devices) device.Update(hub, router.ActivePeer, router.ControlledBy, router.Paused);
        EmptyState.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UiText.Text(PeerCount, $"{hub.Inputs.Count} de {devices.Count} conectado(s)");
        UiText.Text(ControlStatus, router.Paused ? router.PauseDescription : router.Status);
        UiText.Content(PauseButton, router.Paused ? "Reanudar uso compartido" : "Pausar uso compartido");
        PauseButton.IsEnabled = !router.SessionLocked;
        UiText.Tip(PauseButton, router.Paused ? router.PauseHint : "Suspende control remoto, portapapeles y archivos en este PC sin cerrar BorderLess. Cancela las transferencias en curso.");
        PauseNotice.Visibility = router.Paused ? Visibility.Visible : Visibility.Collapsed;
        UiText.Text(PauseNoticeText, router.PauseDescription + " " + router.PauseHint);
        UiText.Text(ClipboardStatus, router.Paused ? "Pausado · no se envía ni recibe contenido" : clipboard?.Status ?? "Portapapeles no iniciado");
        UiText.Text(NetworkStatus, hub.Inputs.Count == 0 ? "TLS · esperando equipos" : $"TLS · {hub.Inputs.Count} equipo(s) · entrada y portapapeles separados");
        if (hub.RelayEnabled) UiText.Text(RelayStatus, hub.RelayWaiting > 0 ? "Relay disponible · esperando conexiones de tus equipos" : "Conectando con relay… Comprueba servidor, puerto y accesibilidad.");
        if (FilesPage.IsVisible) RefreshFileView();
    }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        var page = (string)((Button)sender).Tag;
        DevicesPage.Visibility = page == "devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "activity" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = page == "files" ? Visibility.Visible : Visibility.Collapsed;
        MainScroll.ScrollToTop();
        QueueStatusRefresh();
        foreach (var button in new[] { DevicesNav, FilesNav, SettingsNav, ActivityNav })
        { var selected = (string)button.Tag == page; button.SetResourceReference(BackgroundProperty, selected ? "SoftBrush" : "SurfaceBrush"); button.SetResourceReference(ForegroundProperty, selected ? "AccentTextBrush" : "TextBrush"); }
    }
    private void OpenPairing(object sender, RoutedEventArgs e)
    {
        router.EmergencyReturn(); PairOverlay.Visibility = Visibility.Visible; PairFeedbackBox.Visibility = Visibility.Collapsed; StartPinPairing(); PinInput.Focus();
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(PairOverlay, System.Windows.Input.KeyboardNavigationMode.Cycle);
    }
    private void ClosePairing(object sender, RoutedEventArgs e) { StopPinPairing(); DevicesNav.Focus(); }
    private void PairTabChanged(object sender, SelectionChangedEventArgs e)
    { if (initialized && ReferenceEquals(e.Source, PairTabs)) PairFeedbackBox.Visibility = Visibility.Collapsed; }
    private void PairFeedbackMessage(string message) { UiText.Text(PairFeedback, message); PairFeedbackBox.Visibility = Visibility.Visible; }
    private void GenerateInvite(object sender, RoutedEventArgs e)
    {
        try
        {
            hub.RevokeShortCode(); RefreshPinStatus();
            var invite = hub.CreateInvitation(InviteViaRelay.IsChecked == true);
            InviteOutput.Text = invite.Encode(); CopyCodeButton.IsEnabled = true;
            UiText.Text(InviteExpiry, $"Válido hasta las {invite.Expires.ToLocalTime():HH:mm}. El código concede acceso a este equipo.");
            PairFeedbackBox.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException) { PairFeedbackMessage(ex.Message); }
    }
    private void CopyInvite(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(InviteOutput.Text); PairFeedbackMessage("Código copiado. Pégalo solo en tu otro equipo de confianza."); }
        catch (System.Runtime.InteropServices.COMException) { PairFeedbackMessage("El portapapeles está ocupado. Selecciona el código y cópialo de nuevo."); }
    }
    private void RevokeInvite(object sender, RoutedEventArgs e)
    { hub.RevokeInvitation(); InviteOutput.Text = ""; CopyCodeButton.IsEnabled = false; UiText.Text(InviteExpiry, "Código revocado. Los equipos ya vinculados conservan su acceso."); }
    private async void ConnectPeer(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false; UiText.Content(ConnectButton, "Conectando…");
        try
        {
            var invite = Invitation.Parse(InviteInput.Text);
            var peer = await hub.PairAsync(invite, HostOverride.Text, lifetime.Token);
            PairFeedbackMessage($"Conectado a {peer.Name}. Cierra este panel y pulsa Controlar para empezar.");
            InviteInput.Clear(); hub.Log("Vinculación completada. El código no se guardó en el registro.");
        }
        catch (FormatException ex) { PairFeedbackMessage(ex.Message); }
        catch (InvalidOperationException ex) { PairFeedbackMessage(ex.Message); }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or OperationCanceledException or System.Text.Json.JsonException)
        { PairFeedbackMessage("No se pudo conectar. Comprueba que BorderLess esté abierto en el receptor, que el código siga vigente y que Windows permita la conexión en su firewall. También puedes indicar la IP de VPN."); hub.Log("No se completó la vinculación. Revisa red, identidad y código vigente.", true); }
        finally { ConnectButton.IsEnabled = true; UiText.Content(ConnectButton, "Vincular y conectar"); }
    }
    private void ControlPeer(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is Guid id) ControlDevice(id);
    }
    private void ForgetPeer(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Guid id) return;
        var name = store.Settings.Peers.Find(p => p.Id == id)?.Name ?? UiLanguage.Text("este equipo");
        if (MessageBox.Show(UiLanguage.Text("¿Desvincular {0}? Se cerrará su conexión y necesitará un código nuevo para volver a acceder.").Replace("{0}", name), UiLanguage.Text("Revocar acceso"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        hub.Forget(id); store.Settings.Peers.RemoveAll(p => p.Id == id); store.Save(); RefreshDevices();
    }
    private void ReturnLocal(object sender, RoutedEventArgs e) => router.EmergencyReturn();
    private void TogglePause(object sender, RoutedEventArgs e) { router.Pause(!router.Paused); QueueTrayRefresh(); QueueStatusRefresh(); }
    private void ShareClipboard(object sender, RoutedEventArgs e)
    { if (hub.Clipboards.IsEmpty) { hub.Log("Vincula y conecta otro equipo antes de compartir el portapapeles."); UiText.Text(ClipboardStatus, "Conecta otro equipo para compartir"); } else clipboard?.ShareNow(); }
    private void PreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        store.Settings.AllowControl = AllowControl.IsChecked == true; store.Settings.AutoEdges = AutoEdges.IsChecked == true;
        store.Settings.ShareText = ShareText.IsChecked == true; store.Settings.ShareImages = ShareImages.IsChecked == true;
        store.Settings.PointerFallback = PointerFallback.IsChecked == true;
        store.Settings.PauseOnLock = PauseWhenLocked.IsChecked == true; router.SetPauseOnLock(store.Settings.PauseOnLock);
        remotePointer?.RefreshSettings();
        if (!store.Settings.AllowControl) router.EmergencyReturn();
        store.Save(); hub.Log("Preferencias guardadas.");
    }
    private void EnableRelay(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(RelayPort.Text, out var port)) throw new ArgumentException("El puerto debe ser un número entre 1 y 65535.");
            hub.EnableRelay(RelayHost.Text, port); store.Settings.RelayHost = RelayHost.Text.Trim(); store.Settings.RelayPort = port; store.Save();
            UiText.Text(RelayStatus, "Relay activado · genera un código de internet en Añadir equipo");
        }
        catch (ArgumentException ex) { UiText.Text(RelayStatus, ex.Message); }
    }
    private void DisableRelay(object sender, RoutedEventArgs e) { hub.DisableRelay(); UiText.Text(RelayStatus, "Relay desactivado"); }
    private void ExportDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = UiLanguage.Text("Texto (*.txt)|*.txt"), FileName = $"BorderLess-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = new StringBuilder(UiLanguage.Text("BorderLess 0.1.11 | Diagnóstico\nNo incluye teclas, claves, códigos ni contenido del portapapeles o archivos.\n"));
            text.AppendLine(UiLanguage.Text("Estado actual: ") + UiLanguage.Text(router.Status));
            text.AppendLine(UiLanguage.Text($"Pausa: activa={router.Paused}; manual={router.ManualPause}; Windows bloqueado={router.SessionLocked}; pausar al bloquear={store.Settings.PauseOnLock}"));
            foreach (var line in router.Diagnostics.Export().Split('\n')) if (line.Length > 0) text.AppendLine(UiLanguage.Text(line));
            foreach (var session in hub.Inputs.Values) text.AppendLine(UiLanguage.Text($"Conexión: {session.Peer.Name}; entrada revisión {session.InputRevision}; RTT {session.Rtt:F0} ms; cola {session.Outbox.Count}"));
            foreach (var item in history) text.AppendLine($"{item.Time:O} | {UiLanguage.Text(item.Message)}");
            File.WriteAllText(dialog.FileName, text.ToString()); hub.Log("Diagnóstico exportado.");
        }
        catch (IOException) { MessageBox.Show(UiLanguage.Text("No se pudo guardar el diagnóstico en esa ubicación."), "BorderLess"); }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) { e.Cancel = true; return; }
        if (!exitRequested && tray != null)
        {
            e.Cancel = true; Hide();
            return;
        }
        e.Cancel = true; closing = true; StopPinPairing(); lifetime.Cancel(); timer.Stop(); router.EmergencyReturn();
        keepAwake.Dispose();
        SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.DisplaySettingsChanged -= DisplaysChanged; displayDebounce?.Stop();
        remotePointer?.Dispose(); clipboard?.Dispose(); capture?.Dispose();
        UiLanguage.Instance.PropertyChanged -= LanguageUpdated;
        var handle = new WindowInteropHelper(this).Handle; Native.UnregisterHotKey(handle, 100); source?.RemoveHook(WindowMessage);
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray.ContextMenuStrip?.Dispose(); } trayIcon?.Dispose();
        if (clipboard != null) await clipboard.DisposeAsync();
        await files.DisposeAsync(); await hub.DisposeAsync();
        hub.Identity.Certificate.Dispose(); Closing -= WindowClosing; System.Windows.Application.Current.Shutdown();
    }
}
