using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace BorderLess.App;

public partial class MainWindow
{
    private System.Drawing.Icon? trayIcon;
    private bool exitRequested;
    private volatile bool filesDirty = true;
    private bool choosingFile;
    private bool choosingReceiveFolder;
    private sealed record FilePeerChoice(Guid Id, string Label) { public override string ToString() => Label; }
    private readonly ObservableCollection<TransferRow> transferRows = new();
    private readonly Dictionary<Guid, TransferRow> transferById = new();
    private sealed class TransferRow : INotifyPropertyChanged
    {
        public FileTransferSnapshot Item { get; private set; }
        public string Peer { get; private set; }
        public TransferRow(FileTransferSnapshot item, string peer) { Item = item; Peer = peer; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public Guid Id => Item.Id;
        public override string ToString() => Title;
        public string Title => Item.Name;
        public string Direction => (Item.Incoming ? "De " : "Para ") + Peer;
        public string Detail => Item.Detail;
        public string Size => $"{FormatBytes(Item.Bytes)} / {FormatBytes(Item.Length)}";
        public double Percent => Item.Length == 0 ? Item.State == TransferState.Completed ? 100 : 0 : 100d * Item.Bytes / Item.Length;
        public Visibility Acceptance => Item.Incoming && Item.State == TransferState.Waiting ? Visibility.Visible : Visibility.Collapsed;
        public Visibility Cancellation => Item.State is TransferState.Sending or TransferState.Receiving || !Item.Incoming && Item.State == TransferState.Waiting ? Visibility.Visible : Visibility.Collapsed;
        public void Update(FileTransferSnapshot item, string peer)
        {
            if (item.Id != Id) throw new ArgumentException("A transfer row retains its identity.");
            if (Item == item && Peer == peer) return;
            var title = Title; var direction = Direction; var detail = Detail; var size = Size; var percent = Percent; var acceptance = Acceptance; var cancellation = Cancellation;
            Item = item; Peer = peer;
            if (Title != title) Changed(nameof(Title));
            if (Direction != direction) Changed(nameof(Direction));
            if (Detail != detail) Changed(nameof(Detail));
            if (Size != size) Changed(nameof(Size));
            if (Percent != percent) Changed(nameof(Percent));
            if (Acceptance != acceptance) Changed(nameof(Acceptance));
            if (Cancellation != cancellation) Changed(nameof(Cancellation));
        }
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    }
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:F1} KiB" : $"{bytes / (1024d * 1024):F1} MiB";
    private void InitializeFiles()
    {
        AllowFiles.IsChecked = store.Settings.AllowFiles;
        TransferList.ItemsSource = transferRows;
        ReceiveFolder.Text = files.ReceiveDirectory;
        files.Changed += () => { filesDirty = true; QueueStatusRefresh(); };
        files.Offered += item => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closing) return;
            if ((!IsVisible || !FilesPage.IsVisible) && tray != null) { tray.BalloonTipTitle = UiLanguage.Text("Archivo pendiente de aceptación"); tray.BalloonTipText = UiLanguage.Text("Abre Archivos en BorderLess para aceptar o rechazar la solicitud."); tray.ShowBalloonTip(5000); }
        }));
        hub.FileAvailabilityChanged += _ => { filesDirty = true; QueueStatusRefresh(); };
        hub.Connected += _ => { filesDirty = true; QueueStatusRefresh(); };
        hub.Disconnected += (_, _) => { filesDirty = true; QueueStatusRefresh(); };
        SyncFilesEnabled();
    }
    private void SyncFilesEnabled()
    {
        var enable = store.Settings.AllowFiles && !router.Paused && !closing;
        if (files.Enabled != enable) files.SetEnabled(enable);
    }
    private void FilesPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        store.Settings.AllowFiles = AllowFiles.IsChecked == true; store.Save(); SyncFilesEnabled();
        filesDirty = true; QueueStatusRefresh();
    }
    private void RefreshFileView()
    {
        var rows = store.Settings.Peers.Select(p => new FilePeerChoice(p.Id, p.DisplayName + UiLanguage.Text(hub.CanTransferFiles(p.Id) ? " · disponible" : hub.Inputs.TryGetValue(p.Id, out var s) && s.FileRevision == 0 ? " · requiere actualización" : " · no disponible"))).ToArray();
        var current = FileRecipient.ItemsSource as FilePeerChoice[];
        if (current == null || !current.SequenceEqual(rows))
        {
            var selected = (FileRecipient.SelectedItem as FilePeerChoice)?.Id;
            FileRecipient.ItemsSource = rows;
            FileRecipient.SelectedItem = rows.FirstOrDefault(p => p.Id == selected) ?? rows.FirstOrDefault(p => hub.CanTransferFiles(p.Id)) ?? rows.FirstOrDefault();
        }
        SendFileButton.IsEnabled = !choosingFile && files.Enabled && FileRecipient.SelectedItem is FilePeerChoice peer && hub.CanTransferFiles(peer.Id);
        UiText.Text(FilesStatus, router.Paused ? "En pausa · se cancelaron las transferencias activas" : files.Enabled ? "Activado · cada archivo requiere aceptación en el receptor" : "Desactivado · sin conexión ni transferencia de archivos");
        if (!filesDirty) return;
        filesDirty = false;
        var transfers = files.Snapshot();
        UpdateTransferRows(transfers);
        NoFiles.Visibility = transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateTransferRows(IReadOnlyList<FileTransferSnapshot> snapshots)
    {
        var ids = snapshots.Select(s => s.Id).ToHashSet();
        for (var i = transferRows.Count - 1; i >= 0; i--)
            if (!ids.Contains(transferRows[i].Id)) { transferById.Remove(transferRows[i].Id); transferRows.RemoveAt(i); }
        for (var i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i]; var label = store.Settings.Peers.Find(p => p.Id == snapshot.PeerId)?.DisplayName ?? "Equipo vinculado";
            if (!transferById.TryGetValue(snapshot.Id, out var row)) { row = new(snapshot, label); transferById.Add(snapshot.Id, row); transferRows.Insert(i, row); }
            else
            {
                row.Update(snapshot, label);
                if (!ReferenceEquals(transferRows[i], row)) transferRows.Move(transferRows.IndexOf(row), i);
            }
        }
    }
    private void FileRecipientChanged(object sender, SelectionChangedEventArgs e) { if (initialized) QueueStatusRefresh(); }
    private async void SendFile(object sender, RoutedEventArgs e)
    {
        if (FileRecipient.SelectedItem is not FilePeerChoice peer) return;
        router.EmergencyReturn();
        var dialog = new OpenFileDialog { Title = UiLanguage.Text("Elige un archivo para enviar"), CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        choosingFile = true; UiText.Text(FileFeedback, ""); QueueStatusRefresh();
        try { await files.SendAsync(peer.Id, dialog.FileName, lifetime.Token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { UiText.Text(FileFeedback, ex is OperationCanceledException ? "Envío cancelado." : ex is InvalidOperationException or InvalidDataException ? ex.Message : "No se pudo abrir el archivo o conectar. Comprueba los permisos y la conexión."); }
        finally { choosingFile = false; filesDirty = true; QueueStatusRefresh(); }
    }
    private async void AcceptFile(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is Guid id) await files.AcceptAsync(id); }
    private async void RejectFile(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is Guid id) await files.RejectAsync(id); }
    private async void CancelFile(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is Guid id) await files.CancelAsync(id); }
    private void OpenReceiveFolder(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(files.ReceiveDirectory); Process.Start(new ProcessStartInfo { FileName = files.ReceiveDirectory, UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { UiText.Text(FileFeedback, "No se pudo abrir la carpeta de recepción."); }
    }
    private async void ChooseReceiveFolder(object sender, RoutedEventArgs e)
    {
        if (choosingReceiveFolder) return;
        router.EmergencyReturn();
        var dialog = new OpenFolderDialog { Title = UiLanguage.Text("Elegir carpeta de archivos recibidos"), InitialDirectory = Directory.Exists(files.ReceiveDirectory) ? files.ReceiveDirectory : store.DirectoryPath, Multiselect = false };
        if (dialog.ShowDialog(this) == true) await SaveReceiveFolderAsync(dialog.FolderName, false);
    }
    private async void ResetReceiveFolder(object sender, RoutedEventArgs e) => await SaveReceiveFolderAsync(Path.Combine(store.DirectoryPath, "received"), true);
    private async Task SaveReceiveFolderAsync(string path, bool useDefault)
    {
        if (choosingReceiveFolder) return;
        choosingReceiveFolder = true;
        ReceiveFolderActions.IsEnabled = false;
        try
        {
            var selected = await Task.Run(() => ReceiveFolderSelection.ValidateWritable(path));
            if (closing) return;
            var previous = store.Settings.ReceiveDirectory;
            store.Settings.ReceiveDirectory = useDefault ? "" : selected;
            try { store.Save(); } catch { store.Settings.ReceiveDirectory = previous; throw; }
            files.SetReceiveDirectory(selected); ReceiveFolder.Text = selected;
            ReceiveFolderFeedback.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentTextBrush");
            UiText.Text(ReceiveFolderFeedback, "Carpeta guardada. Se usará al aceptar nuevos archivos; los existentes no se mueven.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { ReceiveFolderFeedback.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "ErrorBrush"); UiText.Text(ReceiveFolderFeedback, "No se pudo usar esa carpeta. Comprueba que exista y tengas permiso de escritura."); }
        finally { choosingReceiveFolder = false; ReceiveFolderActions.IsEnabled = true; }
    }
    private void ShowFiles() { ShowWindow(); Navigate(FilesNav, new RoutedEventArgs()); }
    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        store.Settings.DarkMode = ThemeChoice.SelectedIndex == 1; store.Save(); ThemeManager.Apply(store.Settings.DarkMode);
        ThemeManager.Caption(new WindowInteropHelper(this).Handle);
        devicesDirty = mapDirty = true; LayoutMap.RefreshTheme(); QueueStatusRefresh();
    }
    private void ExitApp() { exitRequested = true; Close(); }
    private void ExitFromSettings(object sender, RoutedEventArgs e) => ExitApp();
    public void SystemExit() { exitRequested = true; Close(); }
}
