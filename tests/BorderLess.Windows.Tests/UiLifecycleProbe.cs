using System.Reflection;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BorderLess.App;
using BorderLess.Core;

internal static class UiLifecycleProbe
{
    public static void Run(bool previewPin = false, bool previewPause = false, bool previewImprovements = false, bool previewLanguage = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BorderLess;component/SharedStyles.xaml", UriKind.Relative) });
            app.DispatcherUnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; app.Shutdown(1); };
            var path = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-lifecycle-" + Guid.NewGuid().ToString("N"));
            var store = new SettingsStore(path); store.Settings.Port = 45176; store.Settings.AutoEdges = false; store.Settings.AllowFiles = true; store.Settings.DarkMode = true; store.Settings.ShareText = store.Settings.ShareImages = false; store.Save();
            ThemeManager.Apply(true); var window = new MainWindow(store); app.MainWindow = window;
            var checks = 0;
            void Check(bool value, string text) { if (!value) throw new Exception(text); checks++; Console.WriteLine("PASS " + text); }
            async Task Until(Func<bool> predicate) { var end = DateTime.UtcNow.AddSeconds(10); while (!predicate() && DateTime.UtcNow < end) await Task.Delay(20); if (!predicate()) throw new TimeoutException("UI test state timed out"); }
            async Task Preview(string stage)
            {
                var pinStage = stage is "code" or "approval" or "paired";
                if (!(stage == "language" && previewLanguage) && (pinStage ? !previewPin : !previewImprovements)) return;
                var marker = System.IO.Path.Combine(path, "continue-" + stage);
                System.IO.File.WriteAllText(System.IO.Path.Combine(path, "preview-stage"), stage);
                Console.WriteLine("PREVIEW " + stage + " | " + path);
                var end = DateTime.UtcNow.AddSeconds(90);
                while (!System.IO.File.Exists(marker) && DateTime.UtcNow < end) await Task.Delay(100);
                if (!System.IO.File.Exists(marker)) throw new TimeoutException("Preview step timed out");
            }
            async Task PausePreview(string stage)
            {
                if (!previewPause) return;
                var marker = System.IO.Path.Combine(path, "continue-pause-" + stage);
                System.IO.File.WriteAllText(System.IO.Path.Combine(path, "preview-stage"), stage);
                Console.WriteLine("PAUSE PREVIEW " + stage + " | " + path);
                var end = DateTime.UtcNow.AddSeconds(90);
                while (!System.IO.File.Exists(marker) && DateTime.UtcNow < end) await Task.Delay(100);
                if (!System.IO.File.Exists(marker)) throw new TimeoutException("Pause preview timed out");
            }
            var started = false;
            window.ContentRendered += async (_, _) =>
            {
                if (started) return; started = true;
                try
                {
                    ((Button)window.FindName("FilesNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var recipient = (ComboBox)window.FindName("FileRecipient");
                    recipient.ItemsSource = new[] { new TestChoice(Guid.NewGuid(), "Destino de prueba") }; recipient.SelectedIndex = 0; window.UpdateLayout();
                    Check(Descendants<TextBlock>(recipient).Any(t => t.Text == "Destino de prueba"), "Selector muestra el alias y no la representación interna del equipo");
                    var view = new FileTransferSnapshot(Guid.NewGuid(), Guid.NewGuid(), "Archivo generado para UI.bin", 100, 42, true, TransferState.Receiving, "Recepción simulada para validar plantilla", "");
                    var transferList = (ItemsControl)window.FindName("TransferList");
                    var updateRows = typeof(MainWindow).GetMethod("UpdateTransferRows", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    updateRows.Invoke(window, new object[] { new[] { view } });
                    window.UpdateLayout();
                    var bar = Descendants<ProgressBar>(window).Single();
                    Check(bar.Value == 42, "Plantilla real de progreso carga un modelo de solo lectura sin excepción");
                    var waitingRow = view with { Id = Guid.NewGuid(), State = TransferState.Waiting, Bytes = 0, Name = "Solicitud sintética pendiente.bin" };
                    updateRows.Invoke(window, new object[] { new[] { view, waitingRow } }); window.UpdateLayout();
                    var rows = (IList)transferList.ItemsSource; var firstRow = rows[0]; var secondRow = rows[1];
                    var propertyChanges = new List<string>(); var collectionChanges = 0;
                    ((INotifyCollectionChanged)rows).CollectionChanged += (_, _) => collectionChanges++;
                    ((INotifyPropertyChanged)firstRow!).PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName ?? "");
                    var accept = Descendants<Button>(transferList).Single(b => Equals(b.Content, "Aceptar archivo") && b.IsVisible); accept.Focus();
                    var focusBefore = System.Windows.Input.FocusManager.GetFocusedElement(window);
                    updateRows.Invoke(window, new object[] { new[] { view with { Bytes = 64 }, waitingRow } }); window.UpdateLayout();
                    Check(ReferenceEquals(rows, transferList.ItemsSource) && ReferenceEquals(rows[0], firstRow) && ReferenceEquals(rows[1], secondRow) && collectionChanges == 0, "Progreso conserva colección y filas sin reiniciar la lista");
                    Check(propertyChanges.Order().SequenceEqual(new[] { "Percent", "Size" }) && ReferenceEquals(focusBefore, accept) && ReferenceEquals(focusBefore, System.Windows.Input.FocusManager.GetFocusedElement(window)), "Solo cambian porcentaje y tamaño; la solicitud conserva foco");
                    propertyChanges.Clear(); updateRows.Invoke(window, new object[] { new[] { view with { Bytes = 64 }, waitingRow } });
                    Check(propertyChanges.Count == 0 && collectionChanges == 0, "Snapshot idéntico no provoca notificaciones ni cambios de colección");
                    ((Border)window.FindName("NoFiles")).Visibility = Visibility.Collapsed;
                    if (Environment.GetEnvironmentVariable("BORDERLESS_SCREENSHOTS") is string capturePath && !string.IsNullOrWhiteSpace(capturePath))
                    {
                        System.IO.Directory.CreateDirectory(capturePath);
                        var folderLabel = (TextBlock)window.FindName("ReceiveFolder"); var originalLabel = folderLabel.Text;
                        folderLabel.Text = @"C:\Users\Usuario\Downloads\BorderLess";
                        foreach (var dark in new[] { true, false })
                        {
                            ThemeManager.Apply(dark); window.UpdateLayout();
                            var frame = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); frame.Render(window);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                            using var output = System.IO.File.Create(System.IO.Path.Combine(capturePath, dark ? "archivos-oscuro.png" : "archivos-claro.png")); encoder.Save(output);
                        }
                        folderLabel.Text = originalLabel; ThemeManager.Apply(true);
                    }
                    await Preview("files");
                    updateRows.Invoke(window, new object[] { new[] { view with { Bytes = 100, State = TransferState.Completed }, waitingRow } });
                    Check(ReferenceEquals(rows[0], firstRow), "Completar una transferencia conserva la identidad de su fila");
                    Check(((SolidColorBrush)window.Foreground).Color == ((SolidColorBrush)ThemeManager.Brush("TextBrush")).Color, "Ventana hereda texto claro del modo oscuro");
                    var theme = (ComboBox)window.FindName("ThemeChoice"); theme.SelectedIndex = 0; window.UpdateLayout();
                    Check(!ThemeManager.Dark && !new SettingsStore(path).Settings.DarkMode, "Cambiar a Claro se aplica y persiste");
                    theme.SelectedIndex = 1; window.UpdateLayout();
                    Check(ThemeManager.Dark && new SettingsStore(path).Settings.DarkMode, "Cambiar a Oscuro se aplica y persiste");
                    var hub = (NetworkHub)typeof(MainWindow).GetField("hub", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    var files = (FileTransferService)typeof(MainWindow).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    var saveFolder = typeof(MainWindow).GetMethod("SaveReceiveFolderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var chosenFolder = System.IO.Path.Combine(path, "chosen-received");
                    await (Task)saveFolder.Invoke(window, new object[] { chosenFolder, false })!;
                    Check(files.ReceiveDirectory == chosenFolder && new SettingsStore(path).ReceiveDirectory == chosenFolder && ((TextBlock)window.FindName("ReceiveFolder")).Text == chosenFolder, "Cambiar carpeta actualiza servicio, pantalla y preferencia persistente");
                    await (Task)saveFolder.Invoke(window, new object[] { System.IO.Path.Combine(path, "received"), true })!;
                    Check(store.Settings.ReceiveDirectory == "" && files.ReceiveDirectory == System.IO.Path.Combine(path, "received"), "Usar predeterminada restaura el destino sin renombrar el perfil");
                    var powerField = typeof(MainWindow).GetField("keepAwake", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    ((KeepAwake)powerField.GetValue(window)!).Dispose();
                    var power = new KeepAwake(() => new KeepAwakeTests.Lease()); powerField.SetValue(window, power);
                    var powerOption = (CheckBox)window.FindName("KeepAwakeWhenConnected");
                    Check(powerOption.IsChecked == false && !power.Active, "Mantener activo está apagado en una ventana nueva");
                    powerOption.IsChecked = true;
                    Check(new SettingsStore(path).Settings.KeepAwakeWhenConnected && !power.Active, "Opción se guarda pero espera una conexión real");
                    typeof(MainWindow).GetMethod("OpenPairing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                    var tabs = (TabControl)window.FindName("PairTabs"); tabs.SelectedIndex = 1;
                    ((Button)window.FindName("GeneratePinButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => hub.ShortCodeStatus.Active);
                    var code = ((TextBox)window.FindName("PinOutput")).Text.Replace(" ", "", StringComparison.Ordinal);
                    Check(code.Length == 8 && code.All(char.IsAsciiDigit) && tabs.Items.Count == 3, "Panel presenta PIN legible de ocho dígitos y conserva Avanzado");
                    await Preview("code");
                    var partnerId = Guid.NewGuid(); using var partnerCert = NodeIdentity.CreateCertificate(partnerId);
                    await using var partner = new NetworkHub(new(partnerId, "EMISOR DE PRUEBA LOCAL", partnerCert));
                    var pair = partner.PairWithShortCodeAsync("127.0.0.1", hub.Port, code);
                    await pair.WaitAsync(TimeSpan.FromSeconds(12));
                    Check(window.FindName("PinApprovalBox") == null, "PIN correcto vincula sin panel ni botón de confirmación adicional");
                    Check(new SettingsStore(path).Settings.Peers.Any(p => p.Id == partnerId) && hub.Inputs.ContainsKey(partnerId), "Permitir desde la pantalla persiste el vínculo y conecta entrada");
                    await Until(() => power.Active);
                    Check(((TextBlock)window.FindName("KeepAwakeStatus")).Text.StartsWith("Activo"), "Conexión TLS activa actualiza la solicitud y el estado visible");
                    await Until(() => ((TextBox)window.FindName("PinOutput")).Text == "Sin código activo");
                    Check(!hub.ShortCodeStatus.Active, "La pantalla retira el código después de usarlo");
                    await Preview("paired");
                    partner.Start(0); var reverseCode = partner.CreateShortCode();
                    tabs.SelectedIndex = 0;
                    ((TextBox)window.FindName("PinHost")).Text = "127.0.0.1";
                    ((TextBox)window.FindName("PinPort")).Text = partner.Port.ToString();
                    ((TextBox)window.FindName("PinInput")).Text = reverseCode.Code.Insert(4, " ");
                    var priorSession = hub.Inputs[partnerId];
                    ((Button)window.FindName("PinConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => hub.Inputs.TryGetValue(partnerId, out var s) && s != priorSession && s.Peer.Initiator && s.Connected);
                    Check(new SettingsStore(path).Settings.Peers.Single(p => p.Id == partnerId).Initiator && partner.Credentials[hub.Identity.Id].Initiator == false, "Conectar desde la pantalla admite IP manual y renueva el vínculo en sentido inverso");
                    ((Button)window.FindName("GeneratePinButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => hub.ShortCodeStatus.Active);
                    typeof(MainWindow).GetMethod("ClosePairing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                    Check(!hub.ShortCodeStatus.Active && ((Grid)window.FindName("PairOverlay")).Visibility == Visibility.Collapsed, "Cerrar el panel revoca el código y oculta la vinculación");
                    Check(files.Enabled && hub.Listening, "Ventana de prueba tiene servicio y recepción activos");
                    window.Close(); await Task.Delay(100);
                    Check(!window.IsVisible && hub.Listening && files.Enabled, "Cerrar con X oculta la ventana y conserva los servicios activos");
                    Check(power.Active, "Mantener activo continúa en la bandeja con un equipo conectado");
                    window.Show(); window.Activate();
                    Check(window.IsVisible, "Ventana oculta se restaura conservando la instancia");
                    var pause = (Button)window.FindName("PauseButton"); pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!files.Enabled && store.Settings.AllowFiles, "Pausa suspende archivos sin borrar la preferencia habilitada");
                    await Until(() => !power.Active); Check(!power.Active, "Pausa real de la interfaz libera energía");
                    pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(files.Enabled, "Reanudar restablece la preferencia de archivos");
                    await Until(() => power.Active);
                    var lockOption = (CheckBox)window.FindName("PauseWhenLocked");
                    var route = (InputRouter)typeof(MainWindow).GetField("router", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    var applyLock = typeof(MainWindow).GetMethod("ApplySessionLock", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    Check(lockOption.IsChecked == true, "Preferencias muestra pausa al bloquear activada por defecto");
                    hub.CreateShortCode(); applyLock.Invoke(window, new object[] { true }); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(route.Paused && !files.Enabled && !hub.ShortCodeStatus.Active && ((Border)window.FindName("PauseNotice")).Visibility == Visibility.Visible && ((TextBlock)window.FindName("PauseNoticeText")).Text.Contains("bloqueo"), "Bloqueo simulado muestra motivo, pausa archivos y revoca el PIN");
                    applyLock.Invoke(window, new object[] { false }); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(!route.Paused && files.Enabled && ((Border)window.FindName("PauseNotice")).Visibility == Visibility.Collapsed, "Desbloqueo retira el aviso y reanuda automáticamente");
                    lockOption.IsChecked = false;
                    Check(!new SettingsStore(path).Settings.PauseOnLock, "Cambiar la opción desde preferencias se guarda");
                    applyLock.Invoke(window, new object[] { true }); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(!route.Paused && files.Enabled && route.SessionLocked, "Preferencia desactivada mantiene archivos activos durante bloqueo simulado");
                    Check(!power.Active, "Bloqueo libera energía aunque la pausa automática esté desactivada");
                    applyLock.Invoke(window, new object[] { false }); pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    applyLock.Invoke(window, new object[] { true }); applyLock.Invoke(window, new object[] { false }); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(route.Paused && route.ManualPause && ((TextBlock)window.FindName("PauseNoticeText")).Text.Contains("manualmente"), "Aviso diferencia pausa manual y esta sobrevive al desbloqueo");
                    ((Button)window.FindName("SettingsNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await PausePreview("manual");
                    pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await PausePreview("active");
                    if (Environment.GetEnvironmentVariable("BORDERLESS_SCREENSHOTS") is string powerCapturePath && !string.IsNullOrWhiteSpace(powerCapturePath))
                    {
                        powerOption.BringIntoView(); window.UpdateLayout();
                        var card = (Border)((FrameworkElement)powerOption.Parent).Parent;
                        var drawing = new DrawingVisual();
                        using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                        var frame = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32); frame.Render(drawing);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                        using var output = System.IO.File.Create(System.IO.Path.Combine(powerCapturePath, "keep-awake.png")); encoder.Save(output);
                    }
                    var language = (ComboBox)window.FindName("LanguageChoice"); language.SelectedValue = "en"; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(Equals(((Button)window.FindName("DevicesNav")).Content, "Devices") && Equals(((Button)window.FindName("SettingsNav")).Content, "Preferences"), "Cambiar a inglés actualiza la navegación sin reiniciar");
                    Check(new SettingsStore(path).Settings.Language == "en" && ((TextBlock)window.FindName("LocalName")).Text == store.Settings.DisplayName, "Idioma persiste sin cambiar el nombre del equipo");
                    Check(UiLanguage.Text("Controlando Preferencias") == "Controlling Preferencias" && UiLanguage.Text("No se pudo controlar TORRE: GCC tiene permisos superiores a BorderLess. Abre BorderLess como administrador en ese PC y vuelve a solicitar control.").Contains("GCC has higher privileges"), "Estados dinámicos se traducen y mantienen nombres originales");
                    Check(Equals(((Button)window.FindName("PauseButton")).Content, "Pause sharing") && Equals(((Button)window.FindName("PinConnectButton")).Content, "Pair with PIN"), "Botones dinámicos siguen traducidos tras cambios de estado");
                    Check(Equals(powerOption.Content, "Keep awake while another device is connected"), "Nueva preferencia se traduce al inglés sin reiniciar");
                    await Preview("language");
                    language.SelectedValue = "es"; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(Equals(((Button)window.FindName("DevicesNav")).Content, "Equipos") && new SettingsStore(path).Settings.Language == "es", "Volver a español restaura los textos y guarda la preferencia");
                    if (previewImprovements)
                    {
                        ((Button)window.FindName("DevicesNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        typeof(InputRouter).GetProperty("Status")!.SetValue(route, InputFailureInspector.Format("TORRE DE PRUEBA", 5, 8192, 12288, "GCC"));
                        typeof(MainWindow).GetMethod("QueueStatusRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Preview("permissions");
                    }
                    hub.Inputs[partnerId].Close("Fin de la conexión de prueba");
                    await Until(() => !power.Active); Check(!power.Active, "Desconexión del último canal de entrada libera energía desde el evento de red");
                    window.SystemExit();
                    Check(!power.Active, "Salir deja liberado el objeto de energía");
                    Console.WriteLine($"{checks} comprobaciones de UI y bandeja completadas.");
                }
                catch (Exception ex) { failure = ex; window.SystemExit(); }
            };
            window.Show(); app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("UI lifecycle regression", failure);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var next in Descendants<T>(child)) yield return next; }
    }
    private sealed record TestChoice(Guid Id, string Label);
}
