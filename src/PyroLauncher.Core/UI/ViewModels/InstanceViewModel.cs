using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    public string Title => L.T(Def.Title);
    public string Glyph => Def.Glyph;
    public bool IsContent { get; init; }
    public bool IsScreenshots => Key == "screenshots";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private IReadOnlyList<LocalItem> _icons = [];
    [ObservableProperty] private IReadOnlyList<object> _thumbs = [];
}

/// <summary>Decoded screenshot thumbnails, kept for the whole session so galleries reopen instantly.</summary>
public static class ThumbnailCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object?> Cache = new();

    public static Task<object?> GetAsync(string path, int width = 400)
    {
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(path); } catch { return Task.FromResult<object?>(null); }
        var key = $"{path}|{mtime.Ticks}|{width}";
        if (Cache.TryGetValue(key, out var hit)) return Task.FromResult(hit);
        return Task.Run(() =>
        {
            object? img = null;
            try
            {
                img = Ui.LoadImageFile(path, width);
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

    public static readonly SectionDef ModpackSection = new("modpack", "Modpack", "");

    /// <summary>Sections of this instance: modpack instances get a Modpack page first.</summary>
    public IReadOnlyList<SectionDef> InstanceSections => Instance.IsModpack ? [ModpackSection, .. Sections] : Sections;

    public Instance Instance { get; }
    private readonly Dictionary<string, object> _tabs = [];
    private readonly List<Action> _unsub = [];

    [ObservableProperty] private string _tab;
    [ObservableProperty] private object? _currentTab;

    public ObservableCollection<SectionCard> Cards { get; } = [];
    public bool IsGridMenu => SettingsService.Current.InstanceMenuStyle == InstanceMenuStyle.Grid;
    public bool IsOverview => Tab == "overview";
    public bool ShowBreadcrumb => IsGridMenu && !IsOverview;
    public bool ShowTabs => !IsGridMenu;
    public string SectionTitle => L.T(InstanceSections.FirstOrDefault(s => s.Key == Tab)?.Title ?? "");
    public bool ConsoleEnabled => SettingsService.Current.OpenConsoleOnLaunch;

    public InstanceViewModel(Instance inst)
    {
        Instance = inst;
        _tab = IsGridMenu ? "overview" : inst.IsModpack ? "modpack" : "mods";
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
        OnPropertyChanged(nameof(ShowBreadcrumb));
        OnPropertyChanged(nameof(ShowTabs));
        OnPropertyChanged(nameof(ConsoleEnabled));
        foreach (var t in _tabs.Values.OfType<ContentTabViewModel>()) t.OnAppearanceChanged();
        if (IsGridMenu && Tab != "overview" && !_tabs.ContainsKey(Tab)) Tab = "overview";
        if (!IsGridMenu && Tab == "overview") Tab = Instance.IsModpack ? "modpack" : "mods";
    }

    private void OnInstanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Instance.IsRunning) or nameof(Instance.IsBusy) or nameof(Instance.IsInstalled))
        {
            OnPropertyChanged(nameof(LaunchText));
            OnPropertyChanged(nameof(LaunchGlyph));
            OnPropertyChanged(nameof(LaunchTip));
        }
    }

    public string LaunchText => Instance.IsBusy ? L.T("Cancel") : Instance.IsRunning ? L.T("Stop") : Instance.IsInstalled ? L.T("Play") : L.T("Install");
    public string LaunchGlyph => Instance.IsBusy || Instance.IsRunning ? "" : Instance.IsInstalled ? "" : "";
    public string? LaunchTip => Instance.IsInstalled || Instance.IsBusy || Instance.IsRunning ? null : L.T("Downloads the game, mod loader and Java so the instance is ready to play");

    partial void OnTabChanged(string value)
    {
        CurrentTab = value == "overview" ? null : GetTab(value);
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(ShowBreadcrumb));
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
            "modpack" => new ModpackPageViewModel(Instance),
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

    private void BuildCards()
    {
        var order = SettingsService.Current.SectionOrder;
        var sections = InstanceSections.ToList();
        var defs = sections.OrderBy(s => order.IndexOf(s.Key) is var i && i >= 0 ? i : 100 + sections.IndexOf(s));
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
            "mods" => Instance.Config.Loader == ModLoader.Vanilla ? L.T("Vanilla instance - no mod loader") : L.F("{0} of {1} mods enabled", list.EnabledCount, n),
            "resourcepacks" => n == 0 ? L.T("No resource packs installed") : L.F("{0} of {1} resource packs active", list.EnabledCount, n),
            _ => list.ActiveShader != null ? L.F("Using {0}", list.ActiveShader) : n == 0 ? L.T("No shader packs installed") : L.F("No shader pack selected · {0} installed", n),
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
                    card.Description = files.Count == 0 ? L.T("No screenshots yet - press F2 in game") : L.F("Screenshots: {0}", files.Count);
                    var thumbs = new List<object>();
                    foreach (var f in files.Take(4))
                        if (await ThumbnailCache.GetAsync(f.FullName, 320) is { } t) thumbs.Add(t);
                    card.Thumbs = thumbs;
                    break;
                case "servers":
                    int servers = 0;
                    try { servers = ServersDat.Load(Instance.GameDir).Count; } catch { }
                    card.Description = servers == 0 ? L.T("No saved servers") : L.F("Saved servers: {0}", servers);
                    break;
                case "logs":
                    card.Description = Instance.IsRunning ? L.T("Game is running - live output") : Instance.LogLines.Count > 0 ? L.F("{0:N0} lines from the last session", Instance.LogLines.Count) : L.T("Game output appears here after launch");
                    break;
                case "configs":
                    card.Description = L.T("Edit mod config files - coming soon");
                    break;
                case "settings":
                    card.Description = L.T("Name, category, loader version, memory, Java and export");
                    break;
                case "modpack":
                    card.Description = Instance.Config.Modpack is { } mp ? L.F("{0} · version {1}", mp.Title, mp.VersionNumber ?? "?") : "";
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
        await LaunchService.PrimaryActionAsync(Instance);
    }

    [RelayCommand]
    private void OpenFolder() => Dialogs.OpenPath(Instance.GameDir);

    [RelayCommand]
    private void OpenConsole() => Ui.Platform.ShowConsole(Instance, true);
}

public partial class LogsViewModel(Instance inst) : ObservableObject
{
    public Instance Instance { get; } = inst;
    public bool ConsoleEnabled => SettingsService.Current.OpenConsoleOnLaunch;

    [RelayCommand]
    private void OpenConsole() => Ui.Platform.ShowConsole(Instance, true);

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
        else Dialogs.Toast(L.T("No crash reports yet."));
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
