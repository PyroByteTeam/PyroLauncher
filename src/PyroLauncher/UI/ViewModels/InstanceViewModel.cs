using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

public record SectionDef(string Key, string Title, string Glyph);

/// <summary>A card in the grid-style instance menu.</summary>
public partial class SectionCard : ObservableObject
{
    public SectionDef Def { get; init; } = null!;
    public string Key => Def.Key;
    public string Title => Def.Title;
    public string Glyph => Def.Glyph;
    public bool IsContent { get; init; }
    public bool IsScreenshots => Key == "screenshots";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private IReadOnlyList<LocalItem> _icons = [];
    [ObservableProperty] private IReadOnlyList<ImageSource> _thumbs = [];
}

/// <summary>Decoded screenshot thumbnails, kept for the whole session so galleries reopen instantly.</summary>
public static class ThumbnailCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Cache = new();

    public static Task<ImageSource?> GetAsync(string path, int width = 400)
    {
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(path); } catch { return Task.FromResult<ImageSource?>(null); }
        var key = $"{path}|{mtime.Ticks}|{width}";
        if (Cache.TryGetValue(key, out var hit)) return Task.FromResult(hit);
        return Task.Run(() =>
        {
            ImageSource? img = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = width;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
            catch { }
            Cache[key] = img;
            return img;
        });
    }
}

public partial class InstanceViewModel : ObservableObject
{
    public static readonly SectionDef[] Sections =
    [
        new("mods", "Mods", ""),
        new("resourcepacks", "Resource Packs", ""),
        new("shaders", "Shaders", ""),
        new("screenshots", "Screenshots", ""),
        new("servers", "Servers", ""),
        new("logs", "Logs", ""),
        new("configs", "Configs", ""),
        new("settings", "Settings", ""),
    ];

    public Instance Instance { get; }
    private readonly Dictionary<string, object> _tabs = [];
    private readonly List<Action> _unsub = [];

    [ObservableProperty] private string _tab;
    [ObservableProperty] private object? _currentTab;

    public ObservableCollection<SectionCard> Cards { get; } = [];
    public bool IsGridMenu => SettingsService.Current.InstanceMenuStyle == InstanceMenuStyle.Grid;
    public bool IsOverview => Tab == "overview";
    public bool ShowTabs => !IsGridMenu;
    public string SectionTitle => Sections.FirstOrDefault(s => s.Key == Tab)?.Title ?? "";
    public bool ConsoleEnabled => SettingsService.Current.OpenConsoleOnLaunch;

    public InstanceViewModel(Instance inst)
    {
        Instance = inst;
        _tab = IsGridMenu ? "overview" : "mods";
        Instance.PropertyChanged += OnInstanceChanged;
        SettingsService.AppearanceChanged += OnAppearanceChanged;
        BuildCards();
        CurrentTab = GetTab(Tab);
    }

    /// <summary>Unsubscribe from long-lived events when the page is left.</summary>
    public void Detach()
    {
        Instance.PropertyChanged -= OnInstanceChanged;
        SettingsService.AppearanceChanged -= OnAppearanceChanged;
        foreach (var u in _unsub) u();
        _unsub.Clear();
        foreach (var t in _tabs.Values.OfType<ContentTabViewModel>()) t.Detach();
    }

    private void OnAppearanceChanged()
    {
        OnPropertyChanged(nameof(IsGridMenu));
        OnPropertyChanged(nameof(ShowTabs));
        OnPropertyChanged(nameof(ConsoleEnabled));
        foreach (var t in _tabs.Values.OfType<ContentTabViewModel>()) t.OnAppearanceChanged();
        if (IsGridMenu && Tab != "overview" && !_tabs.ContainsKey(Tab)) Tab = "overview";
        if (!IsGridMenu && Tab == "overview") Tab = "mods";
    }

    private void OnInstanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Instance.IsRunning) or nameof(Instance.IsBusy))
        {
            OnPropertyChanged(nameof(LaunchText));
            OnPropertyChanged(nameof(LaunchGlyph));
        }
    }

    public string LaunchText => Instance.IsBusy ? "Cancel" : Instance.IsRunning ? "Stop" : "Play";
    public string LaunchGlyph => Instance.IsBusy || Instance.IsRunning ? "" : "";

    partial void OnTabChanged(string value)
    {
        CurrentTab = value == "overview" ? null : GetTab(value);
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(SectionTitle));
        if (value == "overview") RefreshCards();
    }

    private object GetTab(string key)
    {
        if (_tabs.TryGetValue(key, out var vm))
        {
            if (vm is ContentTabViewModel c) c.RefreshIfStale();
            if (vm is ScreenshotsViewModel s) s.Load();
            return vm;
        }
        vm = key switch
        {
            "mods" => new ContentTabViewModel(Instance, ContentType.Mod),
            "resourcepacks" => new ContentTabViewModel(Instance, ContentType.ResourcePack),
            "shaders" => new ContentTabViewModel(Instance, ContentType.ShaderPack),
            "screenshots" => new ScreenshotsViewModel(Instance),
            "servers" => new ServersViewModel(Instance),
            "logs" => new LogsViewModel(Instance),
            "configs" => new ConfigsViewModel(Instance),
            "settings" => new InstanceSettingsViewModel(Instance),
            _ => new LogsViewModel(Instance),
        };
        _tabs[key] = vm;
        return vm;
    }

    // ------------------------------------------------------------------ grid menu

    private void BuildCards()
    {
        var order = SettingsService.Current.SectionOrder;
        var defs = Sections.OrderBy(s => order.IndexOf(s.Key) is var i && i >= 0 ? i : 100 + Array.IndexOf(Sections, s));
        foreach (var d in defs)
        {
            var card = new SectionCard { Def = d, IsContent = d.Key is "mods" or "resourcepacks" or "shaders" };
            Cards.Add(card);
            if (card.IsContent)
            {
                var list = ContentStore.Get(Instance, KeyToType(d.Key));
                void Update() => UpdateContentCard(card, list);
                PropertyChangedEventHandler onProp = (_, e) => { if (e.PropertyName is nameof(ContentList.EnabledCount) or nameof(ContentList.ActiveShader)) Update(); };
                Action onRefresh = Update;
                list.Refreshed += onRefresh;
                list.PropertyChanged += onProp;
                _unsub.Add(() => { list.Refreshed -= onRefresh; list.PropertyChanged -= onProp; });
                Update();
            }
        }
        RefreshCards();
    }

    private static ContentType KeyToType(string key) => key switch
    {
        "mods" => ContentType.Mod,
        "resourcepacks" => ContentType.ResourcePack,
        _ => ContentType.ShaderPack,
    };

    private void UpdateContentCard(SectionCard card, ContentList list)
    {
        var n = list.Items.Count;
        card.Description = card.Key switch
        {
            "mods" => Instance.Config.Loader == ModLoader.Vanilla ? "Vanilla instance — no mod loader" : $"{list.EnabledCount} of {n} mods enabled",
            "resourcepacks" => n == 0 ? "No resource packs installed" : $"{list.EnabledCount} of {n} resource packs active",
            _ => list.ActiveShader != null ? $"Using {list.ActiveShader}" : n == 0 ? "No shader packs installed" : $"No shader pack selected · {n} installed",
        };
        card.Icons = list.Items.Take(50).ToList();
    }

    private async void RefreshCards()
    {
        foreach (var card in Cards)
        {
            switch (card.Key)
            {
                case "screenshots":
                    var dir = Path.Combine(Instance.GameDir, "screenshots");
                    var files = Directory.Exists(dir)
                        ? new DirectoryInfo(dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTime).ToList()
                        : [];
                    card.Description = files.Count == 0 ? "No screenshots yet — press F2 in game" : $"{files.Count} screenshot{(files.Count == 1 ? "" : "s")}";
                    var thumbs = new List<ImageSource>();
                    foreach (var f in files.Take(4))
                        if (await ThumbnailCache.GetAsync(f.FullName, 320) is { } t) thumbs.Add(t);
                    card.Thumbs = thumbs;
                    break;
                case "servers":
                    int servers = 0;
                    try { servers = ServersDat.Load(Instance.GameDir).Count; } catch { }
                    card.Description = servers == 0 ? "No saved servers" : $"{servers} saved server{(servers == 1 ? "" : "s")}";
                    break;
                case "logs":
                    card.Description = Instance.IsRunning ? "Game is running — live output" : Instance.LogLines.Count > 0 ? $"{Instance.LogLines.Count:N0} lines from the last session" : "Game output appears here after launch";
                    break;
                case "configs":
                    card.Description = "Edit mod config files — coming soon";
                    break;
                case "settings":
                    card.Description = "Name, category, loader version, memory and Java";
                    break;
            }
        }
    }

    /// <summary>Drag & drop reordering of cards; the order is remembered for every instance.</summary>
    public void MoveCard(SectionCard card, SectionCard target)
    {
        var from = Cards.IndexOf(card);
        var to = Cards.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Cards.Move(from, to);
        SettingsService.Current.SectionOrder = Cards.Select(c => c.Key).ToList();
        SettingsService.Save();
    }

    [RelayCommand]
    private void OpenSection(string key) => Tab = key;

    [RelayCommand]
    private void ShowOverview() => Tab = "overview";

    [RelayCommand]
    private void Back()
    {
        Detach();
        MainViewModel.Instance.GoHomeCommand.Execute(null);
    }

    [RelayCommand]
    private async Task Launch()
    {
        if (Instance.IsBusy || Instance.IsRunning) LaunchService.Stop(Instance);
        else await LaunchService.LaunchAsync(Instance);
    }

    [RelayCommand]
    private void OpenFolder() => Dialogs.OpenPath(Instance.GameDir);

    [RelayCommand]
    private void OpenConsole() => Views.ConsoleWindow.ShowFor(Instance);
}

public partial class LogsViewModel(Instance inst) : ObservableObject
{
    public Instance Instance { get; } = inst;
    public bool ConsoleEnabled => SettingsService.Current.OpenConsoleOnLaunch;

    [RelayCommand]
    private void OpenConsole() => Views.ConsoleWindow.ShowFor(Instance);

    [RelayCommand]
    private void Clear() => Instance.ClearLog();

    [RelayCommand]
    private void OpenLogsFolder()
    {
        var dir = Path.Combine(Instance.GameDir, "logs");
        Dialogs.OpenPath(Directory.Exists(dir) ? dir : Instance.GameDir);
    }

    [RelayCommand]
    private void OpenCrashReports()
    {
        var dir = Path.Combine(Instance.GameDir, "crash-reports");
        if (Directory.Exists(dir)) Dialogs.OpenPath(dir);
        else Dialogs.Toast("No crash reports yet.");
    }
}

/// <summary>Placeholder for the upcoming config editor.</summary>
public partial class ConfigsViewModel(Instance inst) : ObservableObject
{
    public Instance Instance { get; } = inst;

    [RelayCommand]
    private void OpenConfigFolder()
    {
        var dir = Path.Combine(Instance.GameDir, "config");
        Directory.CreateDirectory(dir);
        Dialogs.OpenPath(dir);
    }
}
