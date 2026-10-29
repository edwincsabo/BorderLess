using System.Windows;

namespace BorderLess.App;

public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var data = ProfileLocation.Resolve(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), e.Args);
        var mutexName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..24];
        mutex = new Mutex(true, "Local\\BorderLess-" + mutexName, out var first);
        if (!first) { MessageBox.Show(UiLanguage.Text("BorderLess ya está abierto. Búscalo en el área de notificación de Windows."), "BorderLess"); Shutdown(); return; }
        var import = Array.IndexOf(e.Args, "--import-profile");
        if (import >= 0)
        {
            try { if (import + 1 >= e.Args.Length) throw new ArgumentException(); ProfileLocation.ImportPortable(e.Args[import + 1], data); Shutdown(0); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException or InvalidOperationException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { try { Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "import-error.log"), ex.Message); } catch (IOException) { } Shutdown(2); }
            return;
        }
        try { var settings = new SettingsStore(data); ThemeManager.Apply(settings.Settings.DarkMode); var window = new MainWindow(settings); MainWindow = window; window.Show(); }
        catch (Exception ex)
        {
            try { Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "startup-error.log"), ex.ToString()); } catch (IOException) { }
            MessageBox.Show(UiLanguage.Text(ex.Message), UiLanguage.Text("BorderLess no pudo iniciar"), MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e) { if (MainWindow is MainWindow window) window.SystemExit(); base.OnSessionEnding(e); }
}
