using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BorderLess.App;

internal sealed record MapDevice(Guid Id, string Label, string MachineName, string Edge, bool Local, bool Online, DesktopSnapshot? Desktop, bool ScreensCurrent = true);

public sealed class TopologyMap : UserControl
{
    private MapDevice[] devices = Array.Empty<MapDevice>();
    private Dictionary<Guid, string>? draft;
    private readonly StackPanel root = new();
    private readonly Canvas surface = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly WrapPanel unassigned = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button edit = new() { Content = "Organizar equipos" };
    private readonly Button save = new() { Content = "Guardar distribución", Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button cancel = new() { Content = "Cancelar", Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("MutedBrush"), FontSize = 13, Margin = new Thickness(0, 10, 0, 10) };
    private readonly StackPanel keyboardEdit = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ComboBox deviceChoice = new() { MinWidth = 160, MaxWidth = 230, Margin = new Thickness(0, 0, 10, 0), DisplayMemberPath = "Label", SelectedValuePath = "Id" };
    private readonly ComboBox edgeChoice = new() { Width = 130, SelectedValuePath = "Tag" };
    private bool changing;
    private System.Windows.Point dragStart;
    public event Action? EditingStarted;
    public event Action? EditingFinished;
    public event Action<IReadOnlyDictionary<Guid, string>>? LayoutSaved;
    public event Action<Guid>? RenameRequested;
    public event Action<Guid>? ControlRequested;
    public bool Editing => draft != null;
    public TopologyMap()
    {
        var toolbar = new WrapPanel();
        toolbar.Children.Add(edit); toolbar.Children.Add(save); toolbar.Children.Add(cancel);
        root.Children.Add(toolbar); root.Children.Add(hint); root.Children.Add(surface); root.Children.Add(unassigned);
        keyboardEdit.Children.Add(new TextBlock { Text = "Ubicación de", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        keyboardEdit.Children.Add(deviceChoice); keyboardEdit.Children.Add(edgeChoice); root.Children.Add(keyboardEdit); Content = root;
        foreach (var edge in PlacementRules.Edges) edgeChoice.Items.Add(new ComboBoxItem { Content = PlacementRules.Label(edge), Tag = edge });
        System.Windows.Automation.AutomationProperties.SetName(deviceChoice, "Equipo para organizar");
        System.Windows.Automation.AutomationProperties.SetName(edgeChoice, "Ubicación del equipo seleccionado");
        edit.Click += (_, _) => { draft = devices.Where(d => !d.Local).ToDictionary(d => d.Id, d => d.Edge); EditingStarted?.Invoke(); RefreshEditor(); Render(); };
        cancel.Click += (_, _) => { draft = null; EditingFinished?.Invoke(); RefreshEditor(); Render(); };
        save.Click += (_, _) => { if (draft == null) return; PlacementRules.Validate(draft); var values = new Dictionary<Guid, string>(draft); draft = null; LayoutSaved?.Invoke(values); EditingFinished?.Invoke(); RefreshEditor(); Render(); };
        deviceChoice.SelectionChanged += (_, _) => RefreshSelectedEdge();
        edgeChoice.SelectionChanged += (_, _) => { if (!changing && draft != null && deviceChoice.SelectedValue is Guid id && edgeChoice.SelectedValue is string edge) Place(id, edge); };
        surface.SizeChanged += (_, _) => { if (IsVisible) Render(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Render(); };
        RefreshLanguage();
    }
    public void RefreshLanguage()
    {
        UiText.Content(edit, "Organizar equipos"); UiText.Content(save, "Guardar distribución"); UiText.Content(cancel, "Cancelar");
        if (keyboardEdit.Children[0] is TextBlock label) UiText.Text(label, "Ubicación de");
        foreach (ComboBoxItem item in edgeChoice.Items) item.Content = UiLanguage.Text(PlacementRules.Label((string)item.Tag));
        System.Windows.Automation.AutomationProperties.SetName(deviceChoice, UiLanguage.Text("Equipo para organizar"));
        System.Windows.Automation.AutomationProperties.SetName(edgeChoice, UiLanguage.Text("Ubicación del equipo seleccionado"));
        RefreshEditor(); if (IsVisible) Render();
    }
    internal void SetDevices(IEnumerable<MapDevice> items)
    {
        var incoming = items.ToArray();
        if (devices.SequenceEqual(incoming)) return;
        devices = incoming;
        if (draft != null)
        {
            var old = draft; draft = devices.Where(d => !d.Local).ToDictionary(d => d.Id, d => old.GetValueOrDefault(d.Id, d.Edge));
        }
        RefreshEditor(); if (IsVisible) Render();
    }
    public void RefreshTheme() { hint.Foreground = ThemeManager.Brush("MutedBrush"); if (IsVisible) Render(); }
    private string Edge(MapDevice d) => d.Local ? "local" : draft?.GetValueOrDefault(d.Id, "none") ?? d.Edge;
    private void RefreshEditor()
    {
        edit.Visibility = Editing ? Visibility.Collapsed : Visibility.Visible;
        save.Visibility = cancel.Visibility = keyboardEdit.Visibility = Editing ? Visibility.Visible : Visibility.Collapsed;
        edit.IsEnabled = devices.Any(d => !d.Local);
        UiText.Text(hint, Editing ? "Arrastra un equipo a un borde. También puedes elegirlo abajo y cambiar su ubicación. Guardar aplica los cambios; Cancelar los descarta." : "Pantallas informadas por cada PC. Organiza los equipos como están en tu escritorio. No modifica las pantallas de Windows.");
        var selected = deviceChoice.SelectedValue;
        changing = true; deviceChoice.ItemsSource = devices.Where(d => !d.Local).ToArray();
        if (selected is Guid id && devices.Any(d => d.Id == id)) deviceChoice.SelectedValue = id;
        else if (deviceChoice.Items.Count > 0) deviceChoice.SelectedIndex = 0;
        changing = false; RefreshSelectedEdge();
    }
    private void RefreshSelectedEdge()
    {
        changing = true;
        edgeChoice.SelectedValue = deviceChoice.SelectedValue is Guid id ? draft?.GetValueOrDefault(id, "none") ?? "none" : "none";
        changing = false;
    }
    private void Place(Guid id, string edge)
    {
        if (draft == null || !draft.ContainsKey(id)) return;
        draft = PlacementRules.Assign(draft, id, edge); RefreshSelectedEdge(); Render();
    }
    private static (int X, int Y) Cell(string edge) => edge switch { "left" => (-1, 0), "right" => (1, 0), "top" => (0, -1), "bottom" => (0, 1), _ => (0, 0) };
    private void Render()
    {
        surface.Children.Clear(); unassigned.Children.Clear();
        unassigned.MinHeight = Editing ? 36 : 0;
        if (Editing) unassigned.Children.Add(new TextBlock { Text = UiLanguage.Text("Sin borde · suelta aquí para retirar un vecino"), FontSize = 12, Foreground = ThemeManager.Brush("MutedBrush"), Margin = new Thickness(0, 8, 12, 8) });
        var shown = devices.Where(d => d.Local || Edge(d) != "none").ToArray();
        if (shown.Length == 0) { surface.Height = 0; return; }
        var cells = Editing ? new[] { (-1, -1), (1, 1) } : shown.Select(d => Cell(Edge(d))).ToArray();
        var minX = cells.Min(c => c.Item1); var maxX = cells.Max(c => c.Item1); var minY = cells.Min(c => c.Item2); var maxY = cells.Max(c => c.Item2);
        var columns = maxX - minX + 1; var width = Math.Max(520, surface.ActualWidth);
        var tileWidth = Math.Min(205, (width - (columns - 1) * 16) / columns); const double tileHeight = 152, gap = 16;
        var total = columns * tileWidth + (columns - 1) * gap; var start = Math.Max(0, (width - total) / 2);
        surface.Height = (maxY - minY + 1) * (tileHeight + gap) - gap;
        foreach (var edge in new[] { "local", "left", "right", "top", "bottom" })
        {
            var device = shown.FirstOrDefault(d => Edge(d) == edge);
            if (device == null && !Editing) continue;
            var cell = Cell(edge);
            if (cell.X < minX || cell.X > maxX || cell.Y < minY || cell.Y > maxY) continue;
            FrameworkElement tile;
            if (device != null) tile = DeviceTile(device, tileWidth, tileHeight);
            else tile = new Border { Width = tileWidth, Height = tileHeight, BorderBrush = BorderColor(), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Background = ThemeManager.Brush("CanvasBrush"), Child = new TextBlock { Text = UiLanguage.Text(PlacementRules.Label(edge)) + UiLanguage.Text("\nSuelta aquí un equipo"), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap } };
            if (Editing && edge != "local") EnableDrop(tile, edge);
            Canvas.SetLeft(tile, start + (cell.X - minX) * (tileWidth + gap)); Canvas.SetTop(tile, (cell.Y - minY) * (tileHeight + gap)); surface.Children.Add(tile);
        }
        foreach (var device in devices.Where(d => !d.Local && Edge(d) == "none"))
        {
            var chip = new Button { Content = device.Label + UiLanguage.Text(" · sin borde"), Tag = device.Id, Margin = new Thickness(0, 0, 8, 8), FontSize = 12 };
            chip.Click += (_, _) => { if (Editing) deviceChoice.SelectedValue = device.Id; };
            chip.ToolTip = UiLanguage.Text(Editing ? "Arrastra a un borde o selecciona una ubicación abajo" : "Puedes controlarlo desde su tarjeta de equipo");
            if (Editing) EnableDrag(chip, device.Id); unassigned.Children.Add(chip);
        }
        if (Editing) EnableDrop(unassigned, "none"); else unassigned.AllowDrop = false;
    }
    private FrameworkElement DeviceTile(MapDevice device, double width, double height)
    {
        var content = new StackPanel { Margin = new Thickness(12, 10, 12, 8) };
        content.Children.Add(new TextBlock { Text = device.Label, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        content.Children.Add(new TextBlock { Text = device.Local ? UiLanguage.Text("Este PC · {0}").Replace("{0}", device.MachineName) : device.MachineName, FontSize = 10, Foreground = ThemeManager.Brush("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 2, 0, 4) });
        var drawing = new Canvas { Width = width - 28, Height = 64, ClipToBounds = true };
        if (device.Desktop is { } desktop)
        {
            var left = desktop.Monitors.Min(m => m.X); var top = desktop.Monitors.Min(m => m.Y);
            var spanWidth = desktop.Monitors.Max(m => (long)m.X + m.Width) - left; var spanHeight = desktop.Monitors.Max(m => (long)m.Y + m.Height) - top;
            var scale = Math.Min((drawing.Width - 6) / spanWidth, 58d / spanHeight);
            var offsetX = (drawing.Width - spanWidth * scale) / 2; var offsetY = (64 - spanHeight * scale) / 2;
            for (var i = 0; i < desktop.Monitors.Length; i++)
            {
                var m = desktop.Monitors[i];
                var monitor = new Border { Width = Math.Max(3, m.Width * scale), Height = Math.Max(3, m.Height * scale), BorderBrush = Accent(), BorderThickness = new Thickness(m.Primary ? 2 : 1), Background = ThemeManager.Brush("SoftBrush"), CornerRadius = new CornerRadius(3), Child = new TextBlock { Text = (i + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = Accent() }, ToolTip = (UiLanguage.Instance.Code == "en" ? "Display " : "Pantalla ") + $"{i + 1}: {m.Width} × {m.Height}" + (m.ScalePercent is int factor ? $" · {factor}%" : "") + (m.Primary ? UiLanguage.Text(" · principal") : "") };
                Canvas.SetLeft(monitor, offsetX + (m.X - left) * scale); Canvas.SetTop(monitor, offsetY + (m.Y - top) * scale); drawing.Children.Add(monitor);
            }
        }
        else drawing.Children.Add(new TextBlock { Text = UiLanguage.Text(device.Online ? "Actualiza el otro PC\npara ver sus pantallas" : "Sin datos de pantalla"), FontSize = 11, Foreground = ThemeManager.Brush("MutedBrush"), TextAlignment = TextAlignment.Center, Width = drawing.Width, Margin = new Thickness(0, 16, 0, 0) });
        content.Children.Add(drawing);
        content.Children.Add(new TextBlock { Text = (device.Desktop == null ? "" : device.Desktop.Monitors.Length + (UiLanguage.Instance.Code == "en" ? " display(s) · " : " pantalla(s) · ")) + UiLanguage.Text(device.Local ? "Local" : device.Online && device.ScreensCurrent ? "Conectado" : "Últimos datos conocidos"), FontSize = 10, Foreground = device.Online || device.Local ? Accent() : ThemeManager.Brush("MutedBrush"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        var tile = new Button { Width = width, Height = height, Padding = new Thickness(0), Content = content, BorderBrush = device.Local ? Accent() : BorderColor(), BorderThickness = new Thickness(device.Local ? 2 : 1), Background = ThemeManager.Brush("SurfaceBrush"), ToolTip = device.Label + "\n" + device.MachineName + UiLanguage.Text(Editing ? "\nArrastra para cambiar su ubicación" : device.Local || !device.Online ? "\nPulsa para cambiar el alias" : "\nPulsa para controlar. Menú contextual para editar alias.") };
        System.Windows.Automation.AutomationProperties.SetName(tile, UiLanguage.Text("Equipo {0}").Replace("{0}", device.Label));
        tile.Click += (_, _) => { if (Editing && !device.Local) deviceChoice.SelectedValue = device.Id; else if (!Editing) { if (device.Online && !device.Local) ControlRequested?.Invoke(device.Id); else RenameRequested?.Invoke(device.Id); } };
        if (!Editing)
        {
            var menu = new ContextMenu(); var rename = new MenuItem { Header = UiLanguage.Text("Editar alias") };
            rename.Click += (_, _) => RenameRequested?.Invoke(device.Id); menu.Items.Add(rename);
            if (!device.Local) { var control = new MenuItem { Header = UiLanguage.Text("Controlar"), IsEnabled = device.Online }; control.Click += (_, _) => ControlRequested?.Invoke(device.Id); menu.Items.Insert(0, control); }
            tile.ContextMenu = menu;
        }
        if (Editing && !device.Local) EnableDrag(tile, device.Id);
        return tile;
    }
    private void EnableDrag(FrameworkElement element, Guid id)
    {
        element.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(element);
        element.PreviewMouseMove += (_, e) =>
        {
            if (!Editing || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(element);
            if (Math.Abs(now.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            DragDrop.DoDragDrop(element, new DataObject("BorderLess.DeviceId", id.ToString("D")), DragDropEffects.Move); e.Handled = true;
        };
    }
    private void EnableDrop(UIElement element, string edge)
    {
        element.AllowDrop = true;
        element.DragOver -= OverDevice; element.DragOver += OverDevice;
        // Unassigned panel is reused; replace its handler instead of adding one each render.
        if (element == unassigned) { unassigned.Drop -= DropUnassigned; unassigned.Drop += DropUnassigned; return; }
        element.Drop += (_, e) => ApplyDrop(e, edge);
    }
    private void OverDevice(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("BorderLess.DeviceId") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; }
    private void DropUnassigned(object sender, DragEventArgs e) => ApplyDrop(e, "none");
    private void ApplyDrop(DragEventArgs e, string edge) { if (e.Data.GetData("BorderLess.DeviceId") is string value && Guid.TryParse(value, out var id)) Place(id, edge); e.Handled = true; }
    private static Brush Accent() => (Brush)System.Windows.Application.Current.FindResource("AccentTextBrush");
    private static Brush BorderColor() => (Brush)System.Windows.Application.Current.FindResource("BorderBrush");
}
