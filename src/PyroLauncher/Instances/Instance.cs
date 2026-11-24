using PyroLauncher.UI;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PyroLauncher.Core;

namespace PyroLauncher.Instances;

public enum ModLoader { Vanilla, Fabric, Forge, NeoForge }

public class InstanceConfig
{
    public string Name { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public ModLoader Loader { get; set; }
    public string? LoaderVersion { get; set; }
    public string? Category { get; set; }
    public long PlayTimeSeconds { get; set; }
    public DateTime? LastPlayed { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public GameSettings Overrides { get; set; } = new();
    /// <summary>Content tabs ("Mod", "ResourcePack", "ShaderPack") where online suggestions were left on.</summary>
    public List<string> SuggestionsOn { get; set; } = [];

    /// <summary>Key ("loader:version") the cached <see cref="InstalledVersionId"/> was produced for.</summary>
    public string? InstalledKey { get; set; }
    public string? InstalledVersionId { get; set; }

    /// <summary>"mc|loader|loaderVersion" of the last complete install (game files + Java). Differs from the current key → Install button.</summary>
    public string? PreparedKey { get; set; }

    /// <summary>Set for instances created from a Modrinth / CurseForge modpack.</summary>
    public ModpackSource? Modpack { get; set; }
}

public partial class Instance : ObservableObject
{
    public string Id { get; }
    public string Dir { get; }
    public string GameDir => Path.Combine(Dir, "minecraft");
    public string NativesDir => Path.Combine(Dir, "natives");
    public string ConfigPath => Path.Combine(Dir, "instance.json");
    public string IconPath => Path.Combine(Dir, "icon.png");
    public InstanceConfig Config { get; }

    public ObservableCollection<LogLine> LogLines { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private double _progress = -1;
    [ObservableProperty] private ImageSource? _icon;

    public CancellationTokenSource? LaunchCts { get; set; }
    public System.Diagnostics.Process? Process { get; set; }

    public Instance(string dir, InstanceConfig config)
    {
        Dir = dir;
        Id = Path.GetFileName(dir);
        Config = config;
        LoadIcon();
    }

    public string CurrentKey => $"{Config.MinecraftVersion}|{Config.Loader}|{Config.LoaderVersion}";
    /// <summary>False for fresh instances and after the game or loader version changed - the UI then offers Install instead of Play.</summary>
    public bool IsInstalled => Config.PreparedKey == CurrentKey;
    public bool IsModpack => Config.Modpack != null;

    public void MarkInstalled()
    {
        if (Config.PreparedKey == CurrentKey) return;
        Config.PreparedKey = CurrentKey;
        Save();
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) OnPropertyChanged(nameof(IsInstalled));
        else d.BeginInvoke(() => OnPropertyChanged(nameof(IsInstalled)));
    }

    public string Name => Config.Name;
    public string Initial => string.IsNullOrEmpty(Config.Name) ? "?" : Config.Name[..1].ToUpperInvariant();
    public string? Category => Config.Category;

    public string LoaderText => Config.Loader switch
    {
        ModLoader.Vanilla => "Vanilla",
        ModLoader.NeoForge => "NeoForge",
        _ => Config.Loader.ToString(),
    };

    public string VersionText => Config.Loader == ModLoader.Vanilla
        ? L.F("{0} · Vanilla", Config.MinecraftVersion)
        : L.F("{0} · {1} {2}", Config.MinecraftVersion, LoaderText, ShortLoaderVersion);

    public string ShortLoaderVersion
    {
        get
        {
            var v = Config.LoaderVersion ?? "";
            // Forge versions are stored as "1.20.1-47.2.0" - strip the MC prefix for display.
            if (Config.Loader == ModLoader.Forge && v.StartsWith(Config.MinecraftVersion + "-"))
                v = v[(Config.MinecraftVersion.Length + 1)..];
            return v;
        }
    }

    /// <summary>Set while the game runs so playtime can tick live.</summary>
    public DateTime? SessionStart { get; set; }

    public long TotalPlaySeconds => Config.PlayTimeSeconds +
        (SessionStart is { } s ? (long)(DateTime.Now - s).TotalSeconds : 0);

    public double HoursPlayed => TotalPlaySeconds / 3600.0;
    public string PlayTimeText => TotalPlaySeconds < 3600
        ? L.F("{0} min played", TotalPlaySeconds / 60)
        : L.F("{0:0.#} h played", HoursPlayed);
    public string PlayTimeClock => FormatClock(TotalPlaySeconds);

    public static string FormatClock(long s) => $"{s / 3600}h {s / 60 % 60:00}m {s % 60:00}s";

    public void TickPlaytime()
    {
        OnPropertyChanged(nameof(PlayTimeText));
        OnPropertyChanged(nameof(HoursPlayed));
        OnPropertyChanged(nameof(PlayTimeClock));
    }

    public string LastPlayedText
    {
        get
        {
            if (Config.LastPlayed is not { } lp) return L.T("Never played");
            var span = DateTime.Now - lp;
            if (span.TotalMinutes < 1) return L.T("Played just now");
            if (span.TotalHours < 1) return L.F("Played {0} min ago", (int)span.TotalMinutes);
            if (span.TotalDays < 1) return L.F("Played {0} h ago", (int)span.TotalHours);
            if (span.TotalDays < 30) return L.F("Played {0} d ago", (int)span.TotalDays);
            return L.F("Played {0}", lp.ToString("d MMM yyyy", L.Culture));
        }
    }

    public void Save()
    {
        try { JsonUtil.Save(ConfigPath, Config); }
        catch (Exception ex) { Log.Error($"Saving instance {Id} failed", ex); }
    }

    /// <summary>Raise change notifications for all config-derived properties.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(LoaderText));
        OnPropertyChanged(nameof(ShortLoaderVersion));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsModpack));
        TickPlaytime();
        OnPropertyChanged(nameof(LastPlayedText));
    }

    public void LoadIcon()
    {
        try
        {
            if (!File.Exists(IconPath)) { Icon = null; return; }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 256;
            bmp.UriSource = new Uri(IconPath);
            bmp.EndInit();
            bmp.Freeze();
            Icon = bmp;
        }
        catch { Icon = null; }
    }

    private readonly GameLogParser _parser = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogLine> _pendingLog = new();
    private int _flushScheduled;

    public void ClearLog()
    {
        lock (_parser) _parser.Reset();
        while (_pendingLog.TryDequeue(out _)) { }
        LogLines.Clear();
    }

    /// <summary>Thread-safe: parses a raw output line and queues the result for the UI thread (batched).</summary>
    public void AppendLog(string raw)
    {
        List<LogLine> lines;
        lock (_parser) lines = _parser.Feed(raw).ToList();
        if (lines.Count == 0) return;
        foreach (var l in lines) _pendingLog.Enqueue(l);

        var d = Application.Current?.Dispatcher;
        if (d == null) { FlushLog(); return; }
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            d.BeginInvoke(FlushLog, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushLog()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        while (_pendingLog.TryDequeue(out var l)) LogLines.Add(l);
        while (LogLines.Count > 10000) LogLines.RemoveAt(0);
    }
}

/// <summary>Where a modpack instance came from, so the pack page can show it and update it.</summary>
public class ModpackSource
{
    public Content.ContentSource Source { get; set; }
    public string ProjectId { get; set; } = "";
    public string? VersionId { get; set; }
    public string? VersionNumber { get; set; }
    public string Title { get; set; } = "";
    public string? IconUrl { get; set; }
    /// <summary>Files (relative to the game dir) the pack installed - replaced or removed on update; anything else the user added is kept.</summary>
    public List<string> Files { get; set; } = [];
}
