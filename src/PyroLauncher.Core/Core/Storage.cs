using System.Diagnostics;
using System.IO;
using PyroLauncher.UI;

namespace PyroLauncher.Core;

/// <summary>What a storage location holds. Each one can live in its own folder, outside the launcher home.</summary>
public enum StorageRole
{
    /// <summary>Instances and server packs ("instances", "servers").</summary>
    Instances,
    /// <summary>Shared game files, Java runtimes and caches ("meta", "java", "cache"): loader cores, libraries, assets, images.</summary>
    GameData,
}

public enum StorageChangeMode
{
    /// <summary>Move the current files to the new folder.</summary>
    Move,
    /// <summary>Start with an empty folder; the current files stay where they are.</summary>
    Empty,
    /// <summary>The new folder already has PyroLauncher data: use it as it is.</summary>
    UseExisting,
}

/// <summary>storage.json in the launcher home. Null means "inside the launcher home" (the default).
/// The Windows setup writes this file too, so keep the shape in sync with PyroLauncher.Installer/Storage.cs.</summary>
public class StorageConfig
{
    public string? InstancesDir { get; set; }
    public string? GameDataDir { get; set; }
}

/// <summary>
/// Resolves where instances and game files live, and moves them to another folder.
/// Settings, accounts, skins and logs always stay in <see cref="AppPaths.Root"/>, which is also where storage.json is.
/// </summary>
public static class StorageLocations
{
    public static string ConfigFile => Path.Combine(AppPaths.Root, "storage.json");

    private static StorageConfig? _config;
    private static StorageConfig Config => _config ??= LoadConfig();

    private static StorageConfig LoadConfig()
    {
        var cfg = JsonUtil.Load<StorageConfig>(ConfigFile) ?? new StorageConfig();
        cfg.InstancesDir = Normalize(cfg.InstancesDir);
        cfg.GameDataDir = Normalize(cfg.GameDataDir);
        return cfg;
    }

    private static string? Normalize(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)));
            return SamePath(full, AppPaths.Root) ? null : full;
        }
        catch { return null; }
    }

    /// <summary>The folder that holds this role's subfolders.</summary>
    public static string Get(StorageRole role) => (role == StorageRole.Instances ? Config.InstancesDir : Config.GameDataDir) ?? AppPaths.Root;

    public static bool IsDefault(StorageRole role) => SamePath(Get(role), AppPaths.Root);

    /// <summary>Folder names a role owns inside its storage folder.</summary>
    public static string[] Subfolders(StorageRole role) => role == StorageRole.Instances
        ? ["instances", "servers"]
        : ["meta", "java", "cache"];

    /// <summary>Custom folders that couldn't be reached at start-up (drive unplugged, folder deleted). The default is used for them this session.</summary>
    public static List<(StorageRole Role, string Dir)> Unavailable { get; } = [];

    /// <summary>Called once at start-up. A custom folder whose drive is missing would make every path fail, so this session falls back to
    /// the default for it without touching storage.json; the folder is used again once it's back.</summary>
    public static void EnsureReachable()
    {
        Unavailable.Clear();
        foreach (var role in new[] { StorageRole.Instances, StorageRole.GameData })
        {
            var dir = role == StorageRole.Instances ? Config.InstancesDir : Config.GameDataDir;
            if (dir == null) continue;
            var ok = false;
            try { ok = Directory.Exists(Path.GetPathRoot(dir)) && Directory.CreateDirectory(dir).Exists; } catch { }
            if (ok) continue;
            Log.Warn($"Storage folder for {role} is unavailable: {dir}. Using the default for this session.");
            Unavailable.Add((role, dir));
            if (role == StorageRole.Instances) Config.InstancesDir = null; else Config.GameDataDir = null;
        }
    }

    private static void Save(StorageRole role, string dir)
    {
        var cfg = LoadConfig();
        var value = SamePath(dir, AppPaths.Root) ? null : dir;
        if (role == StorageRole.Instances) cfg.InstancesDir = value; else cfg.GameDataDir = value;
        JsonUtil.Save(ConfigFile, cfg);
        _config = cfg;
    }

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> or somewhere inside it.</summary>
    public static bool IsInside(string child, string parent)
    {
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child)) + Path.DirectorySeparatorChar;
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Folders whose contents must never be deleted by a storage change: picking one of them (or a folder that contains one) is refused.</summary>
    private static IEnumerable<string> ProtectedFolders()
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
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "Downloads");
        if (!OperatingSystem.IsWindows())
            foreach (var p in new[] { "/bin", "/boot", "/etc", "/lib", "/opt", "/sbin", "/usr", "/var", "/Applications", "/System", "/Library" })
                yield return p;
        if (Path.GetDirectoryName(Environment.ProcessPath ?? "") is { Length: > 0 } app) yield return app;
        yield return Path.GetTempPath();
    }

    /// <summary>Why <paramref name="dir"/> can't hold <paramref name="role"/>, or null when it can.</summary>
    public static string? Validate(StorageRole role, string dir)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch { return L.T("That folder path isn't valid."); }

        if (Path.GetPathRoot(full) is { } root && SamePath(full, root))
            return L.T("Pick a folder, not the root of a drive.");
        if (SamePath(full, AppPaths.Root)) return null;

        // The new folder must not contain the launcher home or the other storage folder (their files would be "foreign" and deleted),
        // and must not be inside one of this role's own subfolders.
        if (IsInside(AppPaths.Root, full)) return L.T("That folder contains the launcher's own data folder. Pick an empty folder or a new one.");
        var other = Get(role == StorageRole.Instances ? StorageRole.GameData : StorageRole.Instances);
        if (!SamePath(other, full) && IsInside(other, full)) return L.T("That folder contains another PyroLauncher storage folder. Pick an empty folder or a new one.");
        var current = Get(role);
        foreach (var sub in Subfolders(role))
            if (IsInside(full, Path.Combine(current, sub)))
                return L.T("The new folder can't be inside the current one.");
        if (!SamePath(other, full) && IsInside(full, AppPaths.Root) && !SamePath(full, AppPaths.Root))
            foreach (var sub in Subfolders(StorageRole.Instances).Concat(Subfolders(StorageRole.GameData)))
                if (IsInside(full, Path.Combine(AppPaths.Root, sub)))
                    return L.T("The new folder can't be inside the launcher's own data folders.");

        foreach (var p in ProtectedFolders())
        {
            try { if (IsInside(p, full)) return L.F("{0} is a system or personal folder. Make a new folder for PyroLauncher instead.", full); }
            catch { }
        }

        try
        {
            if (File.Exists(full)) return L.T("A file with that name already exists.");
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Entries in <paramref name="dir"/> that would be deleted to make room for <paramref name="role"/>.
    /// Inside the launcher home or the other storage folder only this role's own subfolders count; anywhere else, everything does.
    /// </summary>
    public static List<string> ConflictingEntries(StorageRole role, string dir)
    {
        var list = new List<string>();
        if (!Directory.Exists(dir)) return list;
        var other = Get(role == StorageRole.Instances ? StorageRole.GameData : StorageRole.Instances);
        if (SamePath(dir, AppPaths.Root) || SamePath(dir, other))
        {
            foreach (var sub in Subfolders(role))
            {
                var p = Path.Combine(dir, sub);
                if (Directory.Exists(p) && Directory.EnumerateFileSystemEntries(p).Any()) list.Add(p);
            }
            return list;
        }
        list.AddRange(Directory.EnumerateFileSystemEntries(dir));
        return list;
    }

    /// <summary>Counts files and folders below the entries (for the warning text).</summary>
    public static (int Files, long Bytes) Measure(IEnumerable<string> entries)
    {
        int files = 0; long bytes = 0;
        foreach (var e in entries)
        {
            try
            {
                if (File.Exists(e)) { files++; bytes += new FileInfo(e).Length; continue; }
                foreach (var f in new DirectoryInfo(e).EnumerateFiles("*", Enumerate))
                {
                    files++;
                    bytes += f.Length;
                }
            }
            catch { }
        }
        return (files, bytes);
    }

    internal static readonly EnumerationOptions Enumerate = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>True when every entry in <paramref name="dir"/> is one of this role's subfolders, i.e. it was a PyroLauncher storage folder before
    /// (data kept after an uninstall, or a folder used earlier). Such a folder can be used as it is.</summary>
    public static bool HasExistingData(StorageRole role, string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var subs = Subfolders(role);
            var hasData = false;
            foreach (var e in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(e);
                if (subs.Contains(name, StringComparer.OrdinalIgnoreCase) && Directory.Exists(e)) { hasData |= Directory.EnumerateFileSystemEntries(e).Any(); continue; }
                // Other PyroLauncher folders (the other role, launcher home files) may share the folder.
                if (Subfolders(role == StorageRole.Instances ? StorageRole.GameData : StorageRole.Instances).Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (SamePath(dir, AppPaths.Root)) continue;
                return false;
            }
            return hasData;
        }
        catch { return false; }
    }

    /// <summary>
    /// Switches <paramref name="role"/> to <paramref name="target"/> and saves storage.json. The launcher must restart afterwards (paths are read once at start-up).
    /// <see cref="StorageChangeMode.Move"/> and <see cref="StorageChangeMode.Empty"/> first delete <see cref="ConflictingEntries"/> in the target;
    /// <see cref="StorageChangeMode.UseExisting"/> keeps them. On failure or cancel nothing is switched and moved files are put back.
    /// </summary>
    public static async Task ChangeAsync(StorageRole role, string target, StorageChangeMode mode, IProgress<(string Message, double Fraction)> progress, CancellationToken ct)
    {
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (Validate(role, target) is { } err) throw new InvalidOperationException(err);
        var source = Get(role);
        if (SamePath(source, target)) return;

        await Task.Run(() =>
        {
            progress.Report((L.T("Preparing the folder…"), 0));
            Directory.CreateDirectory(target);
            if (mode != StorageChangeMode.UseExisting)
                foreach (var e in ConflictingEntries(role, target))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(e)) DeleteDirectory(e); else File.Delete(e);
                }

            if (mode == StorageChangeMode.Move) MoveRole(role, source, target, progress, ct);
            Save(role, target);
            progress.Report((L.T("Done"), 1));
        }, ct);
    }

    private static void MoveRole(StorageRole role, string source, string target, IProgress<(string Message, double Fraction)> progress, CancellationToken ct)
    {
        var subs = Subfolders(role).Where(s => Directory.Exists(Path.Combine(source, s))).ToList();
        var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);

        if (sameVolume)
        {
            // A rename on the same drive is instant; put back what was moved if a later folder fails (e.g. a locked file).
            var moved = new List<string>();
            try
            {
                for (var i = 0; i < subs.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report((L.F("Moving {0}…", subs[i]), (double)i / Math.Max(1, subs.Count)));
                    // WebView2 keeps its profile open while the launcher runs; it's a cache and is recreated, so leave it behind.
                    if (subs[i] == "cache") TryDeleteDirectory(Path.Combine(source, "cache", "webview2"));
                    Directory.Move(Path.Combine(source, subs[i]), Path.Combine(target, subs[i]));
                    moved.Add(subs[i]);
                }
                return;
            }
            catch
            {
                foreach (var s in moved)
                    try { Directory.Move(Path.Combine(target, s), Path.Combine(source, s)); } catch { }
                throw;
            }
        }

        // Another drive: copy everything first, switch, then delete the originals. A failed copy leaves the originals untouched.
        var files = new List<(string Src, string Dst, long Size)>();
        foreach (var s in subs)
        {
            var srcDir = Path.Combine(source, s);
            foreach (var f in new DirectoryInfo(srcDir).EnumerateFiles("*", Enumerate))
            {
                var rel = Path.GetRelativePath(srcDir, f.FullName);
                if (s == "cache" && rel.StartsWith("webview2", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add((f.FullName, Path.Combine(target, s, rel), f.Length));
            }
        }

        var total = Math.Max(1, files.Sum(f => f.Size));
        long done = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var s in subs) Directory.CreateDirectory(Path.Combine(target, s));
            var buffer = new byte[1 << 20];
            foreach (var (src, dst, size) in files)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int n;
                    while ((n = i.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        o.Write(buffer, 0, n);
                        done += n;
                        if (sw.ElapsedMilliseconds > 100)
                        {
                            sw.Restart();
                            progress.Report((L.F("Copying {0} of {1}", DownloadManager.FormatBytes(done), DownloadManager.FormatBytes(total)), 0.95 * done / total));
                        }
                    }
                }
                try { File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src)); } catch { }
            }
        }
        catch
        {
            foreach (var s in subs) TryDeleteDirectory(Path.Combine(target, s));
            throw;
        }

        progress.Report((L.T("Removing the old copy…"), 0.97));
        foreach (var s in subs) TryDeleteDirectory(Path.Combine(source, s));
    }

    private static void DeleteDirectory(string dir)
    {
        // Read-only files (e.g. git objects in a modpack) block Directory.Delete.
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Enumerate))
            try { if (f.IsReadOnly) f.IsReadOnly = false; } catch { }
        Directory.Delete(dir, true);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) DeleteDirectory(dir); } catch { }
    }
}

/// <summary>One coloured part of a storage usage bar.</summary>
public record UsageSegment(string Key, string Label, long Bytes, string Color)
{
    public string SizeText => DownloadManager.FormatBytes(Bytes);
    public double Fraction { get; init; }
    public string PercentText => Fraction >= 0.995 ? "100%" : Fraction < 0.001 && Bytes > 0 ? "<0.1%" : $"{Fraction * 100:0.#}%";
}

/// <summary>Disk space taken by a single instance or server pack.</summary>
public record InstanceUsage(string Name, string Kind, long Bytes, double Fraction, string Color)
{
    public string SizeText => DownloadManager.FormatBytes(Bytes);
}

/// <summary>Free space on a drive that holds launcher data.</summary>
public record DriveUsage(string Name, long Total, long Free, long Launcher)
{
    public double UsedFraction => Total <= 0 ? 0 : (double)(Total - Free) / Total;
    public double LauncherFraction => Total <= 0 ? 0 : (double)Launcher / Total;
    public string Text => L.F("{0} free of {1}", DownloadManager.FormatBytes(Free), DownloadManager.FormatBytes(Total));
}

public record StorageReport(
    IReadOnlyList<UsageSegment> Segments,
    long Total,
    long InstancesTotal,
    long GameDataTotal,
    long HomeTotal,
    IReadOnlyList<InstanceUsage> Instances,
    IReadOnlyList<DriveUsage> Drives);

/// <summary>Measures how much disk space the launcher uses.</summary>
public static class StorageUsage
{
    // Distinct hues that read on both dark and light themes.
    public const string InstancesColor = "#FF5B8DEF";
    public const string ServersColor = "#FF8E6CEF";
    public const string MetaColor = "#FF3FB98A";
    public const string JavaColor = "#FFF0A23B";
    public const string CacheColor = "#FFE3647A";
    public const string HomeColor = "#FF8A8F98";

    public static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long sum = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", StorageLocations.Enumerate))
                try { sum += f.Length; } catch { }
        }
        catch { }
        return sum;
    }

    public static Task<StorageReport> MeasureAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var inst = StorageLocations.Get(StorageRole.Instances);
        var data = StorageLocations.Get(StorageRole.GameData);

        var instances = new List<(string Name, string Kind, long Bytes, string Color)>();
        foreach (var (dir, kind, color) in new[] { (AppPaths.Instances, L.T("Instance"), InstancesColor), (AppPaths.ServerPacks, L.T("Server pack"), ServersColor) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var d in Directory.GetDirectories(dir))
            {
                ct.ThrowIfCancellationRequested();
                instances.Add((DisplayName(d), kind, DirSize(d), color));
            }
        }
        long instancesBytes = instances.Where(i => i.Color == InstancesColor).Sum(i => i.Bytes);
        long serversBytes = instances.Where(i => i.Color == ServersColor).Sum(i => i.Bytes);
        // Loose files directly in the instances / servers folders.
        instancesBytes += LooseFiles(AppPaths.Instances);
        serversBytes += LooseFiles(AppPaths.ServerPacks);

        ct.ThrowIfCancellationRequested();
        var meta = DirSize(AppPaths.Meta);
        var java = DirSize(AppPaths.Java);
        var cache = DirSize(AppPaths.Cache);

        // Launcher home: everything in the data root that isn't one of the storage subfolders kept there.
        long home = 0;
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (StorageLocations.SamePath(inst, AppPaths.Root)) owned.UnionWith(StorageLocations.Subfolders(StorageRole.Instances));
        if (StorageLocations.SamePath(data, AppPaths.Root)) owned.UnionWith(StorageLocations.Subfolders(StorageRole.GameData));
        if (Directory.Exists(AppPaths.Root))
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.Root)) try { home += new FileInfo(f).Length; } catch { }
            foreach (var d in Directory.EnumerateDirectories(AppPaths.Root))
                if (!owned.Contains(Path.GetFileName(d))) home += DirSize(d);
        }

        var raw = new List<UsageSegment>
        {
            new("instances", L.T("Instances"), instancesBytes, InstancesColor),
            new("servers", L.T("Server packs"), serversBytes, ServersColor),
            new("meta", L.T("Game files"), meta, MetaColor),
            new("java", L.T("Java runtimes"), java, JavaColor),
            new("cache", L.T("Cache"), cache, CacheColor),
            new("home", L.T("Settings, logs and skins"), home, HomeColor),
        };
        var total = raw.Sum(s => s.Bytes);
        var segments = raw.Where(s => s.Bytes > 0).Select(s => s with { Fraction = total == 0 ? 0 : (double)s.Bytes / total }).ToList();

        var biggest = Math.Max(1, instances.Count == 0 ? 1 : instances.Max(i => i.Bytes));
        var perInstance = instances.OrderByDescending(i => i.Bytes)
            .Select(i => new InstanceUsage(i.Name, i.Kind, i.Bytes, (double)i.Bytes / biggest, i.Color)).ToList();

        // Free space per drive used by any of the folders.
        var drives = new List<DriveUsage>();
        var byDrive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, long bytes)
        {
            var d = DriveOf(path);
            if (d == null) return;
            byDrive[d] = byDrive.GetValueOrDefault(d) + bytes;
        }
        Add(inst, instancesBytes + serversBytes);
        Add(data, meta + java + cache);
        Add(AppPaths.Root, home);
        foreach (var (name, bytes) in byDrive)
        {
            try
            {
                var di = new DriveInfo(name);
                if (di.IsReady) drives.Add(new DriveUsage(DriveLabel(di), di.TotalSize, di.AvailableFreeSpace, bytes));
            }
            catch { }
        }

        return new StorageReport(segments, total, instancesBytes + serversBytes, meta + java + cache, home, perInstance, drives);
    }, ct);

    private static long LooseFiles(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long sum = 0;
        foreach (var f in Directory.EnumerateFiles(dir)) try { sum += new FileInfo(f).Length; } catch { }
        return sum;
    }

    private static string DisplayName(string dir)
    {
        foreach (var file in new[] { "instance.json", "server.json", "pack.json" })
        {
            var p = Path.Combine(dir, file);
            if (!File.Exists(p)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        && prop.Value.GetString() is { Length: > 0 } n)
                        return n;
            }
            catch { }
        }
        return Path.GetFileName(dir);
    }

    /// <summary>The mount point / drive root that contains <paramref name="path"/>.</summary>
    private static string? DriveOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows()) return Path.GetPathRoot(full);
            // Longest mount point that contains the path.
            return DriveInfo.GetDrives().Select(d => d.Name).Where(m => StorageLocations.IsInside(full, m))
                .OrderByDescending(m => m.Length).FirstOrDefault() ?? "/";
        }
        catch { return null; }
    }

    private static string DriveLabel(DriveInfo di)
    {
        string label = "";
        try { label = di.VolumeLabel; } catch { }
        var name = di.Name.TrimEnd('\\', '/');
        if (name.Length == 0) name = "/";
        return string.IsNullOrWhiteSpace(label) ? name : $"{label} ({name})";
    }
}
