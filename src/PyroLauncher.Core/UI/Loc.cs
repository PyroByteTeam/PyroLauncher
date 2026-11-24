using PyroLauncher.Core;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PyroLauncher.UI;

public record LanguageOption(string Code, string Name);

/// <summary>
/// UI translations. Keys are the English texts themselves, so untranslated strings simply show in English.
/// Dictionaries live in Lang/&lt;code&gt;.json (embedded resources).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public static readonly LanguageOption[] Languages = [new("en", "English"), new("ru", "Русский")];

    private Dictionary<string, string> _map = [];
    public string Language { get; private set; } = "en";
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changes so code-built texts can be rebuilt.</summary>
    public static event Action? Changed;

    public string this[string key] => _map.TryGetValue(key, out var v) && v.Length > 0 ? v : key;

    /// <summary>"auto" (or null) follows the Windows display language.</summary>
    public static string Resolve(string? code) =>
        string.IsNullOrEmpty(code) || code == "auto"
            ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en")
            : code;

    public void SetLanguage(string? code)
    {
        var lang = Resolve(code);
        _map = lang == "en" ? [] : LoadMap(lang);
        Language = lang;
        var culture = new CultureInfo(lang == "ru" ? "ru-RU" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        Changed?.Invoke();
    }

    private static Dictionary<string, string> LoadMap(string lang)
    {
        try
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream($"Lang.{lang}.json");
            if (s == null) return [];
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Loading language {lang} failed: {ex.Message}");
            return [];
        }
    }
}

/// <summary>Short helpers for code: L.T("text"), L.F("{0} files", n).</summary>
public static class L
{
    public static string T(string text) => Loc.Instance[text];
    public static string F(string format, params object?[] args) => string.Format(Loc.Instance[format], args);
    public static bool IsRu => Loc.Instance.Language == "ru";
    /// <summary>Culture for dates and numbers in the UI language.</summary>
    public static System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo(IsRu ? "ru-RU" : "en-GB");

    /// <summary>Russian plural forms: one (1, 21), few (2-4), many (5-20, 0).</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n);
        if (n % 10 == 1 && n % 100 != 11) return one;
        if (n % 10 is >= 2 and <= 4 && (n % 100 < 10 || n % 100 >= 20)) return few;
        return many;
    }
}

