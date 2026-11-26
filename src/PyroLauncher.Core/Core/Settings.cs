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

    /// <summary>UI language: "auto", "en" or "ru".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>True only on a fresh install (no settings file existed) until the welcome screens are finished.
    /// Updates and repairs keep the settings file, so they never show onboarding.</summary>
    public bool OnboardingPending { get; set; }

    /// <summary>Id of the active theme (see ThemeManager).</summary>
    public string ThemeId { get; set; } = "builtin:dark";

    /// <summary>Unlocks offline accounts and other developer options.</summary>
    public bool DeveloperMode { get; set; }

    public LaunchBehavior OnLaunch { get; set; } = LaunchBehavior.Minimize;
    /// <summary>Separate game console window on launch / crash. When off, logs are only on the Logs tab.</summary>
    public bool OpenConsoleOnLaunch { get; set; } = true;
    /// <summary>Open the launcher window in fullscreen instead of a normal window.</summary>
    public bool StartFullscreen { get; set; }
    public ContentListStyle ContentListStyle { get; set; } = ContentListStyle.List;
    public InstanceMenuStyle InstanceMenuStyle { get; set; } = InstanceMenuStyle.TopNavigation;
    public ContentListStyle DiscoverStyle { get; set; } = ContentListStyle.List;
    public ContentListStyle ServerListStyle { get; set; } = ContentListStyle.List;

    /// <summary>Content presets (Settings → Presets).</summary>
    public List<Content.ContentPreset> Presets { get; set; } = [];

    public string? ExportFolder { get; set; }
    public string ExportFormat { get; set; } = "Modrinth";
    public string? ExportAuthor { get; set; }
    /// <summary>Order of the section cards in the grid instance menu.</summary>
    public List<string> SectionOrder { get; set; } = [];

    /// <summary>Playtime of deleted instances, so the total never goes down.</summary>
    public long DeletedPlaytimeSeconds { get; set; }
    /// <summary>"key=value" lines applied to the server.properties of every new server pack.</summary>
    public string ServerPropertiesDefaults { get; set; } = "";

    /// <summary>LAN page (internet play that looks like LAN to Minecraft).</summary>
    public LanSettings Lan { get; set; } = new();
}

public class LanBan
{
    public string Ip { get; set; } = "";
    /// <summary>Name the player had when banned (just a reminder; the ban is by IP).</summary>
    public string? Name { get; set; }
    public DateTimeOffset BannedAt { get; set; }
}

public class LanSettings
{
    /// <summary>Off by default: no lobby port is opened and nothing is contacted until the user turns LAN on.</summary>
    public bool Enabled { get; set; }
    public int Port { get; set; } = 25570;
    /// <summary>Ask the router to forward the lobby port (UPnP) while hosting.</summary>
    public bool UseUpnp { get; set; } = true;
    /// <summary>Put this computer's IPv6 addresses in invite codes.</summary>
    public bool UseIPv6 { get; set; } = true;
    /// <summary>Also accept players through UDP hole punching (no port forwarding needed on either side).</summary>
    public bool HolePunching { get; set; }
    /// <summary>Host identity key (PKCS#8, DPAPI-protected, base64). Its fingerprint goes into invite codes.</summary>
    public string? ProtectedHostKey { get; set; }
    public string? LobbyName { get; set; }
    public int MaxPlayers { get; set; } = 8;
    /// <summary>Lobby password from the last hosted lobby, DPAPI-protected (base64), so the same lobby can be hosted again.</summary>
    public string? ProtectedPassword { get; set; }
    public string? LastJoinCode { get; set; }
    /// <summary>The IP disclaimer was agreed to once; later it can be skipped without the countdown.</summary>
    public bool DisclaimerAccepted { get; set; }
    public List<LanBan> Bans { get; set; } = [];
}

public static class SettingsService
{
    public static LauncherSettings Current { get; private set; } = new();

    /// <summary>Raised when an appearance/behaviour setting changes so open pages can update live.</summary>
    public static event Action? AppearanceChanged;
    public static void NotifyAppearance() => AppearanceChanged?.Invoke();

    public static void Load()
    {
        var fresh = !File.Exists(AppPaths.SettingsFile);
        Current = JsonUtil.Load<LauncherSettings>(AppPaths.SettingsFile) ?? new LauncherSettings();
        if (fresh) Current.OnboardingPending = true;
        Current.Game ??= GameSettings.Defaults();
        Current.Presets ??= [];
        Current.Lan ??= new LanSettings();
        Current.Lan.Bans ??= [];
        Downloader.Concurrency = Current.ConcurrentDownloads;
    }

    public static void Save()
    {
        Downloader.Concurrency = Current.ConcurrentDownloads;
        try { JsonUtil.Save(AppPaths.SettingsFile, Current); }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }
}
