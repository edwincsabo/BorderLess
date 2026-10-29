namespace BorderLess.Core;

public sealed record DisplayMonitor(string Id, int X, int Y, int Width, int Height, bool Primary, int? ScalePercent = null);
public sealed record DesktopSnapshot(DisplayMonitor[] Monitors, string Label = "")
{
    public void Validate()
    {
        if (Monitors == null || Monitors.Length is < 1 or > 16 || Label == null || Label.Length > 40 || Label.Any(char.IsControl)) throw new InvalidDataException("Información de pantallas no válida.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in Monitors)
            if (m == null || string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > 80 || m.Id.Any(char.IsControl) || !ids.Add(m.Id) || m.Width is < 1 or > 65536 || m.Height is < 1 or > 65536 || m.X is < -1000000 or > 1000000 || m.Y is < -1000000 or > 1000000 || m.ScalePercent is < 50 or > 500) throw new InvalidDataException("Geometría de monitor no válida.");
    }
    public static bool Same(DesktopSnapshot? a, DesktopSnapshot? b) => ReferenceEquals(a, b) || (a != null && b != null && a.Label == b.Label && a.Monitors.SequenceEqual(b.Monitors));
}
