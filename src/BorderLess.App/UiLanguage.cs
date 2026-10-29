using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace BorderLess.App;

public sealed class UiLanguage : INotifyPropertyChanged
{
    public static UiLanguage Instance { get; } = new();
    private readonly Dictionary<string, string> english;
    private readonly (Regex Match, string Replacement)[] patterns;
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    public string Code { get; private set; } = "es";
    public int Version { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private UiLanguage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BorderLess.App.Localization.en.json") ?? throw new InvalidOperationException("Translation catalog missing.");
        english = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? throw new InvalidDataException();
        patterns = english.Where(p => Regex.IsMatch(p.Key, @"\{\d+\}") && Regex.Replace(p.Key, @"\{\d+\}", "").Length > 2)
            .OrderByDescending(p => Regex.Replace(p.Key, @"\{\d+\}", "").Length)
            .Select(p => (BuildPattern(p.Key), p.Value)).ToArray();
    }
    private static Regex BuildPattern(string key)
    {
        var text = "\\A"; var start = 0;
        foreach (Match part in Regex.Matches(key, @"\{(\d+)\}")) { text += Regex.Escape(key[start..part.Index]) + "(?<p" + part.Groups[1].Value + ">.*?)"; start = part.Index + part.Length; }
        return new Regex(text + Regex.Escape(key[start..]) + "\\z", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
    public void Use(string? language)
    {
        var value = language == "en" ? "en" : "es";
        if (Code == value) return;
        Code = value; cache.Clear(); Version++;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(value); CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(value);
        PropertyChanged?.Invoke(this, new(nameof(Version)));
    }
    public static string Text(string? raw) => Instance.Translate(raw ?? "");
    private string Translate(string raw)
    {
        if (Code == "es" || raw.Length == 0) return raw;
        if (english.TryGetValue(raw, out var exact)) return exact;
        if (cache.TryGetValue(raw, out var cached)) return cached;
        var result = raw;
        foreach (var prefix in new[] { "Uso compartido pausado manualmente.", "Uso compartido pausado por bloqueo de Windows.", "Uso compartido pausado manualmente y por bloqueo de Windows." })
            if (raw.StartsWith(prefix + " ", StringComparison.Ordinal)) return english[prefix] + " " + Translate(raw[(prefix.Length + 1)..]);
        foreach (var pattern in patterns)
        {
            var match = pattern.Match.Match(raw); if (!match.Success) continue;
            result = Regex.Replace(pattern.Replacement, @"\{(\d+)\}", m =>
            {
                var value = match.Groups["p" + m.Groups[1].Value].Value;
                return value is "midiendo red" or "portapapeles conectado" or "portapapeles reconectando" or "texto" or "imagen" or "Imagen recibida" or "Texto recibido" or " · verificando solicitud" ? Translate(value) : value;
            }); break;
        }
        if (cache.Count >= 2048) cache.Clear(); cache[raw] = result; return result;
    }
}

public sealed class LocalizedValueConverter : IMultiValueConverter
{
    public static LocalizedValueConverter Instance { get; } = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => UiLanguage.Text(values.Length == 0 || values[0] == DependencyProperty.UnsetValue ? "" : values[0]?.ToString());
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class TrExtension : MarkupExtension
{
    public string Text { get; set; }
    public TrExtension(string text) => Text = text;
    public override object ProvideValue(IServiceProvider provider) => UiText.Binding(new Binding { Source = Text }).ProvideValue(provider);
}
public sealed class LiveTextExtension : MarkupExtension
{
    public string Path { get; set; } = ".";
    public override object ProvideValue(IServiceProvider provider) => UiText.Binding(new Binding(Path)).ProvideValue(provider);
}
public static class UiText
{
    private static readonly DependencyProperty RawText = DependencyProperty.RegisterAttached("RawText", typeof(string), typeof(UiText), new PropertyMetadata(""));
    private static readonly DependencyProperty RawContent = DependencyProperty.RegisterAttached("RawContent", typeof(string), typeof(UiText), new PropertyMetadata(""));
    private static readonly DependencyProperty RawTip = DependencyProperty.RegisterAttached("RawTip", typeof(string), typeof(UiText), new PropertyMetadata(""));
    public static MultiBinding Binding(Binding raw)
    {
        var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = LocalizedValueConverter.Instance };
        binding.Bindings.Add(raw); binding.Bindings.Add(new Binding(nameof(UiLanguage.Version)) { Source = UiLanguage.Instance }); return binding;
    }
    private static void Set(DependencyObject target, DependencyProperty property, DependencyProperty source, string text)
    {
        if (target.ReadLocalValue(source) == DependencyProperty.UnsetValue) BindingOperations.SetBinding(target, property, Binding(new Binding { Source = target, Path = new PropertyPath(source) }));
        target.SetValue(source, text);
    }
    public static void Text(TextBlock target, string text) => Set(target, TextBlock.TextProperty, RawText, text);
    public static void Text(TextBox target, string text) => Set(target, TextBox.TextProperty, RawText, text);
    public static void Content(ContentControl target, string text) => Set(target, ContentControl.ContentProperty, RawContent, text);
    public static void Tip(FrameworkElement target, string text) => Set(target, FrameworkElement.ToolTipProperty, RawTip, text);
}
