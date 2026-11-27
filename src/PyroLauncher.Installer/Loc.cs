using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace PyroLauncher.Installer;

public record LanguageOption(string Code, string Name);

/// <summary>
/// Setup translations, same scheme as the launcher: keys are the English texts, so anything untranslated shows in English.
/// Add a language by dropping Lang/&lt;code&gt;.json next to ru.json and listing it in <see cref="Languages"/>.
/// </summary>
public static class Loc
{
    public static readonly LanguageOption[] Languages = [new("en", "EN"), new("ru", "RU")];

    private static Dictionary<string, string> _map = [];
    public static string Language { get; private set; } = "en";

    /// <summary>Raised after the language changes; XAML texts update by themselves, code-built ones listen to this.</summary>
    public static event Action? Changed;

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "ru" ? "ru-RU" : "en-US");

    public static string T(string key) => _map.TryGetValue(key, out var v) && v.Length > 0 ? v : key;
    public static string F(string format, params object?[] args) => string.Format(T(format), args);

    /// <summary>The Windows display language when it's one we have, else English.</summary>
    public static string SystemLanguage()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    public static void SetLanguage(string code)
    {
        Language = Languages.Any(l => l.Code == code) ? code : "en";
        _map = Language == "en" ? [] : LoadMap(Language);
        Changed?.Invoke();
    }

    private static Dictionary<string, string> LoadMap(string lang)
    {
        try
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream($"Lang.{lang}.json");
            return s == null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
        }
        catch { return []; }
    }
}

/// <summary>A translated text that updates when the language changes.</summary>
public sealed class LocText : INotifyPropertyChanged
{
    private readonly string _key;
    public LocText(string key)
    {
        _key = key;
        Loc.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }
    public string Value => Loc.T(_key);
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>XAML: Text="{local:T 'Install location'}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider sp) =>
        new Binding(nameof(LocText.Value)) { Source = new LocText(key), Mode = BindingMode.OneWay }.ProvideValue(sp);
}
