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

public class LauncherSettings
{
    public GameSettings Game { get; set; } = GameSettings.Defaults();
    public string CurseForgeApiKey { get; set; } = "";
    public string MicrosoftClientId { get; set; } = "4e4681e7-5d96-4d58-a514-ff4b8911b951";
    public int ConcurrentDownloads { get; set; } = 12;
    public LaunchBehavior OnLaunch { get; set; } = LaunchBehavior.Minimize;
    public bool ShowSnapshots { get; set; }
    public bool OpenConsoleOnLaunch { get; set; } = true;
    public List<string> Categories { get; set; } = [];
    public string? SelectedAccountId { get; set; }
}

public static class SettingsService
{
    public static LauncherSettings Current { get; private set; } = new();

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
