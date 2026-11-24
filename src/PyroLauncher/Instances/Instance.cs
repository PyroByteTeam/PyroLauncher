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

    /// <summary>Key ("loader:version") the cached <see cref="InstalledVersionId"/> was produced for.</summary>
    public string? InstalledKey { get; set; }
    public string? InstalledVersionId { get; set; }
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
        ? $"{Config.MinecraftVersion} · Vanilla"
        : $"{Config.MinecraftVersion} · {LoaderText} {ShortLoaderVersion}";

    public string ShortLoaderVersion
    {
        get
        {
            var v = Config.LoaderVersion ?? "";
            // Forge versions are stored as "1.20.1-47.2.0" – strip the MC prefix for display.
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
        ? $"{TotalPlaySeconds / 60} min played"
        : $"{HoursPlayed:0.#} h played";
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
            if (Config.LastPlayed is not { } lp) return "Never played";
            var span = DateTime.Now - lp;
            if (span.TotalMinutes < 1) return "Played just now";
            if (span.TotalHours < 1) return $"Played {(int)span.TotalMinutes} min ago";
            if (span.TotalDays < 1) return $"Played {(int)span.TotalHours} h ago";
            if (span.TotalDays < 30) return $"Played {(int)span.TotalDays} d ago";
            return $"Played {lp:d MMM yyyy}";
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
            bmp.DecodePixelWidth = 128;
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
