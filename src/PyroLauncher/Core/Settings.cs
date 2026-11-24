namespace PyroLauncher.Core;

/// <summary>Minecraft game settings. In launcher settings every value is set; on an instance, null means "use the global default".</summary>
public class GameSettings
{
    public string? JavaPath { get; set; }
    public int? MinMemoryMb { get; set; }
    public int? MaxMemoryMb { get; set; }
    public string? JvmArgs { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool? Fullscreen { get; set; }

    public static GameSettings Defaults() => new()
    {
        JavaPath = null,
        MinMemoryMb = 512,
        MaxMemoryMb = 4096,
        JvmArgs = "-XX:+UseG1GC -XX:+UnlockExperimentalVMOptions -XX:G1NewSizePercent=20 -XX:G1ReservePercent=20 -XX:MaxGCPauseMillis=50 -XX:G1HeapRegionSize=32M",
        WindowWidth = 854,
        WindowHeight = 480,
        Fullscreen = false,
    };

    /// <summary>Returns a fully populated copy where this object's non-null values override <paramref name="defaults"/>.</summary>
    public GameSettings Over(GameSettings defaults)
    {
        var d = Defaults();
        return new GameSettings
        {
            JavaPath = NotEmpty(JavaPath) ?? NotEmpty(defaults.JavaPath),
            MinMemoryMb = MinMemoryMb ?? defaults.MinMemoryMb ?? d.MinMemoryMb,
            MaxMemoryMb = MaxMemoryMb ?? defaults.MaxMemoryMb ?? d.MaxMemoryMb,
            JvmArgs = JvmArgs ?? defaults.JvmArgs ?? "",
            WindowWidth = WindowWidth ?? defaults.WindowWidth ?? d.WindowWidth,
            WindowHeight = WindowHeight ?? defaults.WindowHeight ?? d.WindowHeight,
            Fullscreen = Fullscreen ?? defaults.Fullscreen ?? false,
        };
    }

    private static string? NotEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public enum LaunchBehavior { KeepOpen, Minimize, Close }
public enum ContentListStyle { List, Grid }
public enum InstanceMenuStyle { TopNavigation, Grid }

public class LauncherSettings
{
    public GameSettings Game { get; set; } = GameSettings.Defaults();
    public string CurseForgeApiKey { get; set; } = "";
    public int ConcurrentDownloads { get; set; } = 12;
    public bool ShowSnapshots { get; set; }
    public List<string> Categories { get; set; } = [];
    public string? SelectedAccountId { get; set; }

    /// <summary>Unlocks offline accounts and other developer options.</summary>
    public bool DeveloperMode { get; set; }

    // ---- Appearance & behaviour
    public LaunchBehavior OnLaunch { get; set; } = LaunchBehavior.Minimize;
    /// <summary>Separate game console window on launch / crash. When off, logs are only on the Logs tab.</summary>
    public bool OpenConsoleOnLaunch { get; set; } = true;
    public ContentListStyle ContentListStyle { get; set; } = ContentListStyle.List;
    public InstanceMenuStyle InstanceMenuStyle { get; set; } = InstanceMenuStyle.TopNavigation;
    /// <summary>Order of the section cards in the grid instance menu.</summary>
    public List<string> SectionOrder { get; set; } = [];

    /// <summary>Playtime of deleted instances, so the total never goes down.</summary>
    public long DeletedPlaytimeSeconds { get; set; }
}

public static class SettingsService
{
    public static LauncherSettings Current { get; private set; } = new();

    /// <summary>Raised when an appearance/behaviour setting changes so open pages can update live.</summary>
    public static event Action? AppearanceChanged;
    public static void NotifyAppearance() => AppearanceChanged?.Invoke();

    public static void Load()
    {
        Current = JsonUtil.Load<LauncherSettings>(AppPaths.SettingsFile) ?? new LauncherSettings();
        Current.Game ??= GameSettings.Defaults();
        Downloader.Concurrency = Current.ConcurrentDownloads;
    }

    public static void Save()
    {
        Downloader.Concurrency = Current.ConcurrentDownloads;
        try { JsonUtil.Save(AppPaths.SettingsFile, Current); }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }
}
