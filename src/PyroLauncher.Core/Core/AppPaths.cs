using System.IO;

namespace PyroLauncher.Core;

/// <summary>All on-disk locations used by the launcher. Everything lives under %APPDATA%\PyroLauncher.</summary>
public static class AppPaths
{
    /// <summary>Data root. Set PYROLAUNCHER_HOME to keep everything somewhere else (e.g. a portable install).</summary>
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("PYROLAUNCHER_HOME") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : DefaultRoot();

    /// <summary>%APPDATA%\PyroLauncher on Windows, ~/Library/Application Support/PyroLauncher on macOS, $XDG_DATA_HOME (~/.local/share)/PyroLauncher on Linux.</summary>
    private static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PyroLauncher");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "PyroLauncher");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "PyroLauncher");
    }

    public static string Instances => Path.Combine(Root, "instances");
    public static string ServerPacks => Path.Combine(Root, "servers");

    /// <summary>Shared ".minecraft-like" directory: libraries, assets and version JSONs shared by all instances.</summary>
    public static string Meta => Path.Combine(Root, "meta");
    public static string Libraries => Path.Combine(Meta, "libraries");
    public static string Assets => Path.Combine(Meta, "assets");
    public static string Versions => Path.Combine(Meta, "versions");

    public static string Java => Path.Combine(Root, "java");
    public static string Cache => Path.Combine(Root, "cache");
    public static string ImageCache => Path.Combine(Cache, "images");
    public static string WebViewData => Path.Combine(Cache, "webview2");
    public static string Logs => Path.Combine(Root, "logs");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string AccountsFile => Path.Combine(Root, "accounts.dat");

    public static void Ensure()
    {
        foreach (var d in new[] { Root, Instances, Meta, Libraries, Assets, Versions, Java, Cache, ImageCache, Logs })
            Directory.CreateDirectory(d);
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(s) ? "instance" : s;
    }
}
