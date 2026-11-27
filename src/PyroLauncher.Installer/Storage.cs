using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PyroLauncher.Installer;

/// <summary>
/// The setup's side of the launcher's storage.json (PyroLauncher.Core/Core/Storage.cs): where instances and game files live.
/// Settings and accounts always stay in <see cref="InstallerCore.UserDataDir"/>.
/// </summary>
public static class Storage
{
    public static string ConfigFile => Path.Combine(InstallerCore.UserDataDir, "storage.json");

    public static readonly string[] InstancesSubfolders = ["instances", "servers"];
    public static readonly string[] GameDataSubfolders = ["meta", "java", "cache"];
    public static string[] AllSubfolders => [.. InstancesSubfolders, .. GameDataSubfolders];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public class Config
    {
        public string? InstancesDir { get; set; }
        public string? GameDataDir { get; set; }
    }

    public static Config Load()
    {
        try { return File.Exists(ConfigFile) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigFile), Json) ?? new() : new(); }
        catch { return new(); }
    }

    public static string InstancesDir => Load().InstancesDir is { Length: > 0 } d ? d : InstallerCore.UserDataDir;
    public static string GameDataDir => Load().GameDataDir is { Length: > 0 } d ? d : InstallerCore.UserDataDir;

    /// <summary>Points instances and game files to <paramref name="dir"/> (the launcher's data folder means "default").</summary>
    public static void Save(string dir)
    {
        var value = SamePath(dir, InstallerCore.UserDataDir) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var cfg = new Config { InstancesDir = value, GameDataDir = value };
        if (value == null && !File.Exists(ConfigFile)) return;
        Directory.CreateDirectory(InstallerCore.UserDataDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg, Json));
    }

    /// <summary>Launcher data from an earlier install is still there (kept on uninstall): the setup then leaves storage alone.</summary>
    public static bool HasExistingData() =>
        File.Exists(ConfigFile) || AllSubfolders.Any(s => NonEmptyDir(Path.Combine(InstallerCore.UserDataDir, s)));

    /// <summary>Every folder that may hold instances or game files (custom locations and the default), without duplicates.</summary>
    public static IEnumerable<string> DataFolders() =>
        new[] { InstancesDir, GameDataDir, InstallerCore.UserDataDir }.DistinctBy(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)).ToLowerInvariant());

    public static bool NonEmptyDir(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsInside(string child, string parent)
    {
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child)) + '\\';
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + '\\';
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Folders the setup must never empty: picking one of them, or a folder that contains one, is refused.</summary>
    private static IEnumerable<string> Protected()
    {
        foreach (var sf in new[]
                 {
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.Programs,
                 })
        {
            var p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(p)) yield return p;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        yield return Path.GetTempPath();
    }

    /// <summary>Common checks for the install folder and the data folder. Returns an error text or null.</summary>
    public static string? ValidateFolder(string full)
    {
        if (Path.GetPathRoot(full) is { } root && SamePath(full, root)) return Loc.T("Pick a folder, not the root of a drive.");
        foreach (var p in Protected())
            if (IsInside(p, full)) return Loc.F("{0} is a system or personal folder. Make a new folder for PyroLauncher instead.", full);
        if (File.Exists(full)) return Loc.T("A file with that name already exists.");
        return null;
    }

    /// <summary>Validates a data folder and lists what's in it that would be deleted (empty list: nothing to delete).</summary>
    public static string? ValidateDataFolder(string full, string installDir)
    {
        if (SamePath(full, InstallerCore.UserDataDir)) return null;
        if (ValidateFolder(full) is { } err) return err;
        if (IsInside(full, installDir) || IsInside(installDir, full)) return Loc.T("The data folder must be separate from the install folder.");
        if (IsInside(InstallerCore.UserDataDir, full)) return Loc.T("That folder contains the launcher's own data folder. Pick an empty folder or a new one.");
        return null;
    }

    /// <summary>Entries in <paramref name="dir"/> that are not PyroLauncher storage folders.</summary>
    public static List<string> ForeignEntries(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return [];
            if (SamePath(dir, InstallerCore.UserDataDir)) return [];
            return Directory.EnumerateFileSystemEntries(dir)
                .Where(e => !(Directory.Exists(e) && AllSubfolders.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase))).ToList();
        }
        catch { return []; }
    }

    /// <summary>The folder has PyroLauncher storage subfolders with something in them (e.g. data kept from an earlier install).</summary>
    public static bool HasPyroData(string dir) => AllSubfolders.Any(s => NonEmptyDir(Path.Combine(dir, s)));

    public static (int Files, long Bytes) Measure(IEnumerable<string> entries)
    {
        int files = 0; long bytes = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var e in entries)
        {
            try
            {
                if (File.Exists(e)) { files++; bytes += new FileInfo(e).Length; continue; }
                foreach (var f in new DirectoryInfo(e).EnumerateFiles("*", opts)) { files++; bytes += f.Length; }
            }
            catch { }
        }
        return (files, bytes);
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => Loc.F("{0:0.00} GB", b / (double)(1L << 30)),
        >= 1L << 20 => Loc.F("{0:0.0} MB", b / (double)(1L << 20)),
        >= 1L << 10 => Loc.F("{0:0} KB", b / 1024.0),
        _ => $"{b} B",
    };
}
