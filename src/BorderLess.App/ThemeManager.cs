using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace BorderLess.App;

public static class ThemeManager
{
    public static bool Dark { get; private set; }
    public static void Apply(bool dark)
    {
        Dark = dark;
        var colors = new Dictionary<string, string>
        {
            ["CanvasBrush"] = dark ? "#131B1A" : "#F5F6F3", ["SurfaceBrush"] = dark ? "#1B2624" : "#FFFFFF",
            ["TextBrush"] = dark ? "#EDF5F2" : "#202E2C", ["MutedBrush"] = dark ? "#AFBFB9" : "#596A66",
            ["AccentBrush"] = "#12665C", ["AccentTextBrush"] = dark ? "#78DDC8" : "#12665C",
            ["BorderBrush"] = dark ? "#40514B" : "#D9E0DB", ["SoftBrush"] = dark ? "#243D35" : "#E5F0EB",
            ["SoftTextBrush"] = dark ? "#B9E3D6" : "#355048", ["WarningBrush"] = dark ? "#F1C882" : "#805A20",
            ["WarningSurfaceBrush"] = dark ? "#3B3222" : "#F5EFE2", ["ErrorBrush"] = dark ? "#FFAA9C" : "#9B3526"
        };
        foreach (var pair in colors) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value)); brush.Freeze(); Application.Current.Resources[pair.Key] = brush; }
    }
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static void Caption(IntPtr handle) { var value = Dark ? 1 : 0; _ = DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)); }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
