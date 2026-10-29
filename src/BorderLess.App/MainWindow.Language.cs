using System.ComponentModel;
using System.Windows.Controls;

namespace BorderLess.App;

public partial class MainWindow
{
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || LanguageChoice.SelectedValue is not string code) return;
        store.Settings.Language = code == "en" ? "en" : "es"; store.Save(); UiLanguage.Instance.Use(store.Settings.Language);
    }
    private void LanguageUpdated(object? sender, PropertyChangedEventArgs e)
    {
        if (closing) return;
        LayoutMap.RefreshLanguage(); filesDirty = mapDirty = true; QueueStatusRefresh(); QueueTrayRefresh();
    }
}
