using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using PyroLauncher.Core;

namespace PyroLauncher.UI;

/// <summary>An editable palette entry of a theme.</summary>
public record ThemeColorKey(string Key, string Label);

/// <summary>A launcher theme: palette, font scale and an optional background image.</summary>
public partial class LauncherTheme : ObservableObject
{
    public const double MinFontScale = 0.8, MaxFontScale = 1.5, MaxTransparency = 0.95, MinSize = 0.8, MaxSize = 1.5;

    /// <summary>Palette keys the user can edit. They match the brush keys in Theme.axaml.</summary>
    public static readonly IReadOnlyList<ThemeColorKey> Keys =
    [
        new("Bg0", "Window background"),
        new("Bg1", "Sidebar and panels"),
        new("Bg2", "Cards"),
        new("Bg3", "Controls"),
        new("Bg4", "Hover and selection"),
        new("Line", "Borders"),
        new("Line2", "Strong borders"),
        new("Fg", "Text"),
        new("Fg2", "Secondary text"),
        new("Fg3", "Muted text"),
        new("Accent", "Accent"),
        new("AccentFg", "Text on accent"),
        new("LogWarn", "Warnings"),
        new("LogError", "Errors"),
    ];

    public int Format { get; set; } = 1;
    [ObservableProperty] private string _name = "Theme";
    public double FontScale { get; set; } = 1.0;
    /// <summary>Size of the page selector on the left (icons, labels and width).</summary>
    public double SidebarScale { get; set; } = 1.0;
    /// <summary>Size of the tab menus at the top of instance, server pack and settings pages.</summary>
    public double TopMenuScale { get; set; } = 1.0;
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>File name of the background image inside the theme folder.</summary>
    public string? BackgroundImage { get; set; }
    /// <summary>0 = solid background, up to 0.95 = the image (or the desktop) shows through.</summary>
    public double BackgroundTransparency { get; set; }

    [JsonIgnore] public string Id { get; set; } = "";
    [JsonIgnore] public bool IsBuiltIn { get; set; }
    [JsonIgnore] public string? Folder { get; set; }
    [JsonIgnore] public string? BackgroundPath => Folder != null && !string.IsNullOrEmpty(BackgroundImage) ? Path.Combine(Folder, BackgroundImage) : null;
    [JsonIgnore] public bool HasBackground => BackgroundPath is { } p && File.Exists(p);
    [ObservableProperty][property: JsonIgnore] private bool _isActive;

    // Swatches for the theme picker.
    [JsonIgnore] public string SwatchBg => Color("Bg0");
    [JsonIgnore] public string SwatchCard => Color("Bg2");
    [JsonIgnore] public string SwatchText => Color("Fg");
    [JsonIgnore] public string SwatchAccent => Color("Accent");

    public string Color(string key) =>
        Colors.TryGetValue(key, out var c) ? c : ThemeManager.DefaultColors.GetValueOrDefault(key, "#FFFF00FF");

    public void SetColor(string key, string hex)
    {
        Colors[key] = hex;
        OnPropertyChanged(nameof(SwatchBg));
        OnPropertyChanged(nameof(SwatchCard));
        OnPropertyChanged(nameof(SwatchText));
        OnPropertyChanged(nameof(SwatchAccent));
    }

    /// <summary>Clamps values and fills in missing / invalid colors (themes from files may be partial or hand-edited).</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name)) Name = "Theme";
        Name = Name.Trim();
        if (Name.Length > 60) Name = Name[..60];
        FontScale = double.IsFinite(FontScale) ? Math.Clamp(FontScale, MinFontScale, MaxFontScale) : 1.0;
        SidebarScale = double.IsFinite(SidebarScale) ? Math.Clamp(SidebarScale, MinSize, MaxSize) : 1.0;
        TopMenuScale = double.IsFinite(TopMenuScale) ? Math.Clamp(TopMenuScale, MinSize, MaxSize) : 1.0;
        BackgroundTransparency = double.IsFinite(BackgroundTransparency) ? Math.Clamp(BackgroundTransparency, 0, MaxTransparency) : 0;
        var fixedColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Keys)
            fixedColors[k.Key] = Colors.TryGetValue(k.Key, out var c) && ThemeManager.NormalizeHex(c) is { } h ? h : ThemeManager.DefaultColors[k.Key];
        Colors = fixedColors;
        if (BackgroundImage != null) BackgroundImage = Path.GetFileName(BackgroundImage);
    }

    public LauncherTheme Clone(string name) => new()
    {
        Name = name,
        FontScale = FontScale,
        SidebarScale = SidebarScale,
        TopMenuScale = TopMenuScale,
        Colors = new Dictionary<string, string>(Colors, StringComparer.OrdinalIgnoreCase),
        BackgroundTransparency = BackgroundTransparency,
    };
}

/// <summary>Built-in and user themes (stored in &lt;data&gt;/themes/&lt;id&gt;/theme.json).</summary>
public static class ThemeManager
{
    public const string FileExtension = ".pyrotheme";
    private const string ThemeJson = "theme.json";
    private const long MaxImageBytes = 30 * 1024 * 1024;
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    public static readonly IReadOnlyDictionary<string, string> DefaultColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Bg0"] = "#FF080808", ["Bg1"] = "#FF0F0F0F", ["Bg2"] = "#FF151515", ["Bg3"] = "#FF1D1D1D", ["Bg4"] = "#FF262626",
        ["Line"] = "#FF262626", ["Line2"] = "#FF404040",
        ["Fg"] = "#FFF4F4F4", ["Fg2"] = "#FFC4C4C4", ["Fg3"] = "#FF959595",
        ["Accent"] = "#FFFFFFFF", ["AccentFg"] = "#FF000000",
        ["LogWarn"] = "#FFE8C468", ["LogError"] = "#FFFF6B6B",
    };

    public static ObservableCollection<LauncherTheme> Themes { get; } = [];
    public static LauncherTheme Active { get; private set; } = null!;

    /// <summary>Raised whenever the active theme or one of its values changes; the desktop app applies it.</summary>
    public static event Action<LauncherTheme>? Applied;

    private static string Dir => Path.Combine(AppPaths.Root, "themes");

    private static LauncherTheme BuiltIn(string id, string name, Dictionary<string, string> overrides)
    {
        var colors = new Dictionary<string, string>(DefaultColors, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in overrides) colors[k] = v;
        return new LauncherTheme { Id = id, Name = name, Colors = colors, IsBuiltIn = true };
    }

    private static IEnumerable<LauncherTheme> BuiltIns() =>
    [
        BuiltIn("builtin:dark", "Pyro Dark", []),
        BuiltIn("builtin:ember", "Ember", new()
        {
            ["Bg0"] = "#FF0C0907", ["Bg1"] = "#FF130E0B", ["Bg2"] = "#FF1A1410", ["Bg3"] = "#FF241B16", ["Bg4"] = "#FF30241D",
            ["Line"] = "#FF2E231C", ["Line2"] = "#FF4A3A2F", ["Fg"] = "#FFF8F1EA", ["Fg2"] = "#FFD2C3B6", ["Fg3"] = "#FFA08E80",
            ["Accent"] = "#FFFF8A3D", ["AccentFg"] = "#FF1A0D04",
        }),
        BuiltIn("builtin:ocean", "Ocean", new()
        {
            ["Bg0"] = "#FF070B12", ["Bg1"] = "#FF0B111B", ["Bg2"] = "#FF111926", ["Bg3"] = "#FF172233", ["Bg4"] = "#FF1F2D42",
            ["Line"] = "#FF1C2A3D", ["Line2"] = "#FF2F4461", ["Fg"] = "#FFEEF4FB", ["Fg2"] = "#FFBCCADB", ["Fg3"] = "#FF8B9DB3",
            ["Accent"] = "#FF5AB0FF", ["AccentFg"] = "#FF04121F",
        }),
        BuiltIn("builtin:forest", "Forest", new()
        {
            ["Bg0"] = "#FF070B08", ["Bg1"] = "#FF0C120E", ["Bg2"] = "#FF121A14", ["Bg3"] = "#FF19241C", ["Bg4"] = "#FF213026",
            ["Line"] = "#FF1D2A21", ["Line2"] = "#FF2F4535", ["Fg"] = "#FFEFF6F0", ["Fg2"] = "#FFBFD0C3", ["Fg3"] = "#FF8EA394",
            ["Accent"] = "#FF6BD68A", ["AccentFg"] = "#FF04140A",
        }),
    ];

    public static void Load()
    {
        Themes.Clear();
        foreach (var t in BuiltIns()) Themes.Add(t);
        if (Directory.Exists(Dir))
        {
            foreach (var folder in Directory.GetDirectories(Dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var t = JsonUtil.Load<LauncherTheme>(Path.Combine(folder, ThemeJson));
                if (t == null) continue;
                t.Id = Path.GetFileName(folder);
                t.Folder = folder;
                t.Normalize();
                Themes.Add(t);
            }
        }
        var wanted = SettingsService.Current.ThemeId;
        SetActive(Themes.FirstOrDefault(t => t.Id == wanted) ?? Themes[0]);
    }

    private static void SetActive(LauncherTheme t)
    {
        foreach (var x in Themes) x.IsActive = x == t;
        Active = t;
    }

    /// <summary>Makes <paramref name="theme"/> the active theme and remembers the choice.</summary>
    public static void Switch(LauncherTheme theme)
    {
        SetActive(theme);
        SettingsService.Current.ThemeId = theme.Id;
        SettingsService.Save();
        Applied?.Invoke(theme);
    }

    /// <summary>Re-applies the active theme after one of its values changed (live preview).</summary>
    public static void Refresh() => Applied?.Invoke(Active);

    public static void Save(LauncherTheme theme)
    {
        if (theme.IsBuiltIn || theme.Folder == null) return;
        try { JsonUtil.Save(Path.Combine(theme.Folder, ThemeJson), theme); }
        catch (Exception ex) { Log.Error($"Saving theme {theme.Name} failed", ex); }
    }

    private static string NewFolder(string name)
    {
        Directory.CreateDirectory(Dir);
        var baseId = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (baseId.Length == 0) baseId = "theme";
        if (baseId.StartsWith("builtin", StringComparison.Ordinal)) baseId = "theme-" + baseId;
        var id = baseId;
        for (var i = 2; Directory.Exists(Path.Combine(Dir, id)); i++) id = $"{baseId}-{i}";
        var folder = Path.Combine(Dir, id);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string UniqueName(string name)
    {
        var n = name.Trim();
        if (Themes.All(t => !t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) return n;
        for (var i = 2; ; i++)
            if (Themes.All(t => !t.Name.Equals($"{n} ({i})", StringComparison.OrdinalIgnoreCase))) return $"{n} ({i})";
    }

    private static LauncherTheme Add(LauncherTheme theme, string folder)
    {
        theme.Id = Path.GetFileName(folder);
        theme.Folder = folder;
        theme.Normalize();
        Save(theme);
        Themes.Add(theme);
        return theme;
    }

    /// <summary>Creates a new user theme as a copy of <paramref name="source"/> (including its background image).</summary>
    public static LauncherTheme Create(string name, LauncherTheme source)
    {
        var theme = source.Clone(UniqueName(name));
        var folder = NewFolder(theme.Name);
        if (source.HasBackground)
        {
            var file = "background" + Path.GetExtension(source.BackgroundPath!).ToLowerInvariant();
            File.Copy(source.BackgroundPath!, Path.Combine(folder, file), true);
            theme.BackgroundImage = file;
        }
        return Add(theme, folder);
    }

    public static void Rename(LauncherTheme theme, string name)
    {
        if (theme.IsBuiltIn) return;
        theme.Name = UniqueName(name);
        Save(theme);
    }

    public static void Delete(LauncherTheme theme)
    {
        if (theme.IsBuiltIn) return;
        if (theme == Active) Switch(Themes[0]);
        Themes.Remove(theme);
        try { if (theme.Folder != null && Directory.Exists(theme.Folder)) Directory.Delete(theme.Folder, true); }
        catch (Exception ex) { Log.Error($"Deleting theme folder {theme.Folder} failed", ex); }
    }

    /// <summary>Copies <paramref name="imagePath"/> into the theme folder and uses it as the background; null removes it.</summary>
    public static void SetBackground(LauncherTheme theme, string? imagePath)
    {
        if (theme.IsBuiltIn || theme.Folder == null) return;
        string? file = null;
        if (imagePath != null)
        {
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            if (!ImageExtensions.Contains(ext)) throw new InvalidOperationException(L.T("Unsupported image format."));
            if (new FileInfo(imagePath).Length > MaxImageBytes) throw new InvalidOperationException(L.T("The image is too large (30 MB max)."));
            // A new name each time, so the old file isn't locked by a bitmap that is still on screen.
            file = $"background-{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
            File.Copy(imagePath, Path.Combine(theme.Folder, file), true);
        }
        var old = theme.BackgroundPath;
        theme.BackgroundImage = file;
        Save(theme);
        if (old != null) try { File.Delete(old); } catch { /* still in use; harmless leftover */ }
    }

    /// <summary>Writes a .pyrotheme file (a zip with theme.json and the background image).</summary>
    public static void Export(LauncherTheme theme, string destination)
    {
        var copy = theme.Clone(theme.Name);
        var tmp = destination + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            if (theme.HasBackground)
            {
                copy.BackgroundImage = "background" + Path.GetExtension(theme.BackgroundPath!).ToLowerInvariant();
                zip.CreateEntryFromFile(theme.BackgroundPath!, copy.BackgroundImage);
            }
            var entry = zip.CreateEntry(ThemeJson);
            using var s = entry.Open();
            JsonSerializer.Serialize(s, copy, JsonUtil.Options);
        }
        File.Move(tmp, destination, true);
    }

    /// <summary>Imports a .pyrotheme / .zip file or a bare theme .json as a new user theme.</summary>
    public static LauncherTheme Import(string path)
    {
        LauncherTheme? theme;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            theme = JsonSerializer.Deserialize<LauncherTheme>(File.ReadAllText(path), JsonUtil.Options);
            if (theme == null) throw new InvalidDataException(L.T("This is not a theme file."));
            theme.BackgroundImage = null;
            theme.Name = UniqueName(theme.Name);
            theme.Normalize();
            return Add(theme, NewFolder(theme.Name));
        }

        using var zip = ZipFile.OpenRead(path);
        var json = zip.GetEntry(ThemeJson) ?? throw new InvalidDataException(L.T("This is not a theme file."));
        using (var s = json.Open()) theme = JsonSerializer.Deserialize<LauncherTheme>(s, JsonUtil.Options);
        if (theme == null) throw new InvalidDataException(L.T("This is not a theme file."));
        theme.Name = UniqueName(theme.Name);
        theme.Normalize();
        var folder = NewFolder(theme.Name);
        var image = theme.BackgroundImage;
        theme.BackgroundImage = null;
        // Only the referenced image is extracted, by its bare file name and with an allowed extension.
        if (image != null && ImageExtensions.Contains(Path.GetExtension(image).ToLowerInvariant())
            && zip.GetEntry(image) is { } entry && entry.Length <= MaxImageBytes)
        {
            entry.ExtractToFile(Path.Combine(folder, image), true);
            theme.BackgroundImage = image;
        }
        return Add(theme, folder);
    }

    private static readonly Regex HexRx = new("^#?([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

    /// <summary>"#RRGGBB" or "#AARRGGBB" → "#AARRGGBB" (upper case), or null when invalid.</summary>
    public static string? NormalizeHex(string? s)
    {
        if (s == null) return null;
        var m = HexRx.Match(s.Trim());
        if (!m.Success) return null;
        var h = m.Groups[1].Value.ToUpperInvariant();
        return "#" + (h.Length == 6 ? "FF" + h : h);
    }

    public static uint ParseArgb(string hex) =>
        uint.Parse((NormalizeHex(hex) ?? "#FFFF00FF")[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
