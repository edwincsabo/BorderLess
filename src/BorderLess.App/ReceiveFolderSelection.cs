namespace BorderLess.App;

internal static class ReceiveFolderSelection
{
    public static string ValidateWritable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("La carpeta de recepción debe ser una ruta absoluta.");
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        var probe = Path.Combine(full, ".borderless-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) stream.WriteByte(0);
        return full;
    }
}
