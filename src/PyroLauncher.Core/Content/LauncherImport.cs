using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;
using PyroLauncher.UI;

namespace PyroLauncher.Content;

public enum ExternalLauncher { Modrinth, Prism, CurseForge, Xmcl }

/// <summary>An instance found in another launcher's folder.</summary>
public record ExternalInstance(
    string Name,
    string GameDir,
    string MinecraftVersion,
    ModLoader Loader,
    string? LoaderVersion,
    string? IconPath = null,
    long PlayTimeSeconds = 0,
    DateTime? LastPlayed = null,
    int? MaxMemoryMb = null,
    int? MinMemoryMb = null,
    /// <summary>Set when the instance can't be imported (for example a Quilt instance); shown instead of the checkbox.</summary>
    string? Unsupported = null,
    /// <summary>Files in the game folder that belong to the other launcher and are not copied.</summary>
    IReadOnlySet<string>? SkipFiles = null)
{
    public string LoaderText => Loader == ModLoader.Vanilla ? L.T("Vanilla") : $"{Loader} {LoaderVersion}".Trim();
}

/// <summary>Finds and imports instances from Modrinth App, Prism Launcher, the CurseForge app and X Minecraft Launcher.</summary>
public static class LauncherImport
{
    public static string DisplayName(ExternalLauncher l) => l switch
    {
        ExternalLauncher.Modrinth => "Modrinth App",
        ExternalLauncher.Prism => "Prism Launcher",
        ExternalLauncher.CurseForge => "CurseForge",
        _ => "X Minecraft Launcher",
    };

    // ---------- default locations ----------

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Per-user data folder: %APPDATA% on Windows, Application Support on macOS, XDG data home on Linux.</summary>
    private static string DataHome =>
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) :
        OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support") :
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".local", "share");

    private static string ConfigHome =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? DataHome :
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config");

    /// <summary>Where each launcher keeps its data by default, most likely first.</summary>
    public static IEnumerable<string> Candidates(ExternalLauncher l) => l switch
    {
        ExternalLauncher.Modrinth =>
        [
            Path.Combine(DataHome, "ModrinthApp"),
            Path.Combine(DataHome, "com.modrinth.theseus"),
        ],
        ExternalLauncher.Prism =>
        [
            Path.Combine(DataHome, "PrismLauncher"),
            Path.Combine(Home, ".var", "app", "org.prismlauncher.PrismLauncher", "data", "PrismLauncher"),
        ],
        ExternalLauncher.CurseForge =>
        [
            Path.Combine(Home, "curseforge", "minecraft", "Instances"),
            Path.Combine(Home, "Documents", "curseforge", "minecraft", "Instances"),
        ],
        _ =>
        [
            Path.Combine(ConfigHome, "xmcl"),
            Path.Combine(DataHome, "xmcl"),
        ],
    };

    /// <summary>The first default location that exists, or null.</summary>
    public static string? DetectFolder(ExternalLauncher l) => Candidates(l).FirstOrDefault(Directory.Exists);

    // ---------- scanning ----------

    /// <summary>Lists the instances in <paramref name="folder"/>. Throws with a readable message when the folder isn't one the launcher uses.</summary>
    public static List<ExternalInstance> Scan(ExternalLauncher l, string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(L.T("This folder doesn't exist."));
        var list = l switch
        {
            ExternalLauncher.Modrinth => ScanModrinth(folder),
            ExternalLauncher.Prism => ScanPrism(folder),
            ExternalLauncher.CurseForge => ScanCurseForge(folder),
            _ => ScanXmcl(folder),
        };
        return list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Folders below <paramref name="folder"/> (or the folder itself) that contain <paramref name="marker"/>.</summary>
    private static List<string> InstanceDirs(string folder, string marker, params string[] containers)
    {
        if (File.Exists(Path.Combine(folder, marker))) return [folder];
        foreach (var c in containers.Append(""))
        {
            var dir = c.Length == 0 ? folder : Path.Combine(folder, c);
            if (!Directory.Exists(dir)) continue;
            var found = SafeDirs(dir).Where(d => File.Exists(Path.Combine(d, marker))).ToList();
            if (found.Count > 0) return found;
        }
        return [];
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception ex) { Log.Warn($"Can't list {dir}: {ex.Message}"); return []; }
    }

    private static Exception NothingFound(ExternalLauncher l) =>
        new InvalidDataException(L.F("No {0} instances were found in this folder.", DisplayName(l)));

    private static (ModLoader loader, string? unsupported) LoaderFromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "vanilla" or "minecraft" => (ModLoader.Vanilla, null),
        "fabric" or "fabricloader" or "fabric-loader" => (ModLoader.Fabric, null),
        "forge" => (ModLoader.Forge, null),
        "neoforge" or "neoforged" => (ModLoader.NeoForge, null),
        "quilt" or "quiltloader" or "quilt-loader" => (ModLoader.Vanilla, L.T("Quilt instances are not supported.")),
        var other => (ModLoader.Vanilla, L.F("Mod loader '{0}' is not supported.", other)),
    };

    private static string? ExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return File.Exists(path) ? path : null; }
        catch { return null; }
    }

    // Prism Launcher (and MultiMC): instances/<dir>/instance.cfg + mmc-pack.json, game files in .minecraft/
    private static List<ExternalInstance> ScanPrism(string folder)
    {
        var instancesDir = "instances";
        var cfgPath = Path.Combine(folder, "prismlauncher.cfg");
        if (!File.Exists(cfgPath)) cfgPath = Path.Combine(folder, "multimc.cfg");
        if (File.Exists(cfgPath) && ReadIni(cfgPath).TryGetValue("InstanceDir", out var custom) && custom.Length > 0) instancesDir = custom;
        var instancesRoot = Path.IsPathRooted(instancesDir) ? instancesDir : Path.Combine(folder, instancesDir);

        var dirs = InstanceDirs(folder, "instance.cfg", instancesRoot);
        if (dirs.Count == 0) throw NothingFound(ExternalLauncher.Prism);
        // Icons live next to the instances folder: <root>/icons/<iconKey>.png
        var iconsDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dirs[0]).TrimEnd(Path.DirectorySeparatorChar)) is { } p
            ? Path.GetDirectoryName(p) ?? folder : folder, "icons");

        var result = new List<ExternalInstance>();
        foreach (var dir in dirs)
        {
            try
            {
                var cfg = ReadIni(Path.Combine(dir, "instance.cfg"));
                var name = cfg.GetValueOrDefault("name") is { Length: > 0 } n ? n : Path.GetFileName(dir);
                string? mc = null, loaderVer = null;
                var loader = ModLoader.Vanilla;
                string? unsupported = null;
                var pack = JsonUtil.ParseFile(Path.Combine(dir, "mmc-pack.json"));
                foreach (var c in pack.Arr("components"))
                {
                    var ver = c.Str("version");
                    switch (c.Str("uid"))
                    {
                        case "net.minecraft": mc = ver; break;
                        case "net.fabricmc.fabric-loader": loader = ModLoader.Fabric; loaderVer = ver; break;
                        case "net.minecraftforge": loader = ModLoader.Forge; loaderVer = ver; break;
                        case "net.neoforged": loader = ModLoader.NeoForge; loaderVer = ver; break;
                        case "org.quiltmc.quilt-loader": unsupported = L.T("Quilt instances are not supported."); break;
                        case "com.mumfrey.liteloader": unsupported = L.F("Mod loader '{0}' is not supported.", "LiteLoader"); break;
                    }
                }
                var game = new[] { ".minecraft", "minecraft" }.Select(s => Path.Combine(dir, s)).FirstOrDefault(Directory.Exists)
                           ?? Path.Combine(dir, ".minecraft");
                string? icon = null;
                if (cfg.GetValueOrDefault("iconKey") is { Length: > 0 } key && Directory.Exists(iconsDir))
                    icon = Directory.EnumerateFiles(iconsDir, key + ".*").FirstOrDefault();

                int? Mem(string k) => cfg.GetValueOrDefault("OverrideMemory") == "true" && int.TryParse(cfg.GetValueOrDefault(k), out var v) ? v : null;
                long.TryParse(cfg.GetValueOrDefault("totalTimePlayed"), out var played);
                DateTime? last = long.TryParse(cfg.GetValueOrDefault("lastLaunchTime"), out var ms) && ms > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : null;

                result.Add(new ExternalInstance(name, game, mc ?? "", loader, loaderVer, icon, played, last, Mem("MaxMemAlloc"), Mem("MinMemAlloc"),
                    unsupported ?? (mc == null ? L.T("The Minecraft version could not be read.") : null)));
            }
            catch (Exception ex) { Log.Warn($"Skipping Prism instance {dir}: {ex.Message}"); }
        }
        return result;
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '[' or '#' or ';') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            d[line[..eq].Trim()] = value;
        }
        return d;
    }

    // CurseForge app: Instances/<name>/minecraftinstance.json, game files in the same folder
    private static List<ExternalInstance> ScanCurseForge(string folder)
    {
        var dirs = InstanceDirs(folder, "minecraftinstance.json", "Instances", Path.Combine("minecraft", "Instances"));
        if (dirs.Count == 0) throw NothingFound(ExternalLauncher.CurseForge);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "minecraftinstance.json" };
        var result = new List<ExternalInstance>();
        foreach (var dir in dirs)
        {
            try
            {
                var j = JsonUtil.ParseFile(Path.Combine(dir, "minecraftinstance.json"));
                var name = j.Str("name") is { Length: > 0 } n ? n : Path.GetFileName(dir);
                var mc = j.Str("gameVersion") ?? j.Obj("baseModLoader").Str("minecraftVersion");
                // baseModLoader.name: "forge-47.2.0", "neoforge-21.1.65", "fabric-0.15.7-1.20.1"
                var loaderName = j.Obj("baseModLoader").Str("name");
                var (loader, unsupported) = (ModLoader.Vanilla, (string?)null);
                string? loaderVer = null;
                if (!string.IsNullOrEmpty(loaderName))
                {
                    var parts = loaderName.Split('-');
                    (loader, unsupported) = LoaderFromName(parts[0]);
                    loaderVer = parts.Length > 1 ? parts[1] : null;
                }
                DateTime? last = DateTime.TryParse(j.Str("lastPlayed"), out var lp) && lp.Year > 2000 ? lp : null;
                var icon = ExistingFile(j.Str("profileImagePath"));
                result.Add(new ExternalInstance(name, dir, mc ?? "", loader, loaderVer, icon, 0, last, null, null,
                    unsupported ?? (mc == null ? L.T("The Minecraft version could not be read.") : null), skip));
            }
            catch (Exception ex) { Log.Warn($"Skipping CurseForge instance {dir}: {ex.Message}"); }
        }
        return result;
    }

    // X Minecraft Launcher: instances/<name>/instance.json, game files in the same folder
    private static List<ExternalInstance> ScanXmcl(string folder)
    {
        // XMCL can move its data to another folder; the path is then stored in a "root" file in the app folder.
        var rootFile = Path.Combine(folder, "root");
        if (File.Exists(rootFile) && File.ReadAllText(rootFile).Trim() is { Length: > 0 } root && Directory.Exists(root)
            && !Directory.Exists(Path.Combine(folder, "instances")))
            folder = root;

        var dirs = InstanceDirs(folder, "instance.json", "instances");
        if (dirs.Count == 0) throw NothingFound(ExternalLauncher.Xmcl);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "instance.json", "server.json" };
        var result = new List<ExternalInstance>();
        foreach (var dir in dirs)
        {
            try
            {
                var j = JsonUtil.ParseFile(Path.Combine(dir, "instance.json"));
                var rt = j.Obj("runtime");
                var mc = rt.Str("minecraft");
                var name = j.Str("name") is { Length: > 0 } n ? n : Path.GetFileName(dir);
                var (loader, ver, unsupported) =
                    rt.Str("fabricLoader") is { Length: > 0 } f ? (ModLoader.Fabric, f, (string?)null) :
                    rt.Str("neoForged") is { Length: > 0 } nf ? (ModLoader.NeoForge, nf, null) :
                    rt.Str("forge") is { Length: > 0 } fo ? (ModLoader.Forge, fo, null) :
                    rt.Str("quiltLoader") is { Length: > 0 } ? (ModLoader.Vanilla, null, L.T("Quilt instances are not supported.")) :
                    (ModLoader.Vanilla, (string?)null, (string?)null);
                var icon = XmclIcon(j.Str("icon"));
                var lastMs = j.Long("lastPlayedDate");
                int? Mem(string k) => j.Long(k) is > 0 and < int.MaxValue and var v ? (int)v : null;
                result.Add(new ExternalInstance(name, dir, mc ?? "", loader, ver, icon, 0,
                    lastMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastMs).LocalDateTime : null, Mem("maxMemory"), Mem("minMemory"),
                    unsupported ?? (string.IsNullOrEmpty(mc) ? L.T("The Minecraft version could not be read.") : null), skip));
            }
            catch (Exception ex) { Log.Warn($"Skipping XMCL instance {dir}: {ex.Message}"); }
        }
        return result;
    }

    /// <summary>XMCL stores icons as "image://&lt;path&gt;", a file:// URL or a "http://launcher/media?path=..." URL.</summary>
    private static string? XmclIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        try
        {
            if (icon.StartsWith("image://", StringComparison.OrdinalIgnoreCase)) return ExistingFile(Uri.UnescapeDataString(icon["image://".Length..]));
            if (Uri.TryCreate(icon, UriKind.Absolute, out var u))
            {
                if (u.IsFile) return ExistingFile(u.LocalPath);
                var q = System.Web.HttpUtility.ParseQueryString(u.Query)["path"];
                if (q != null) return ExistingFile(q);
            }
            return ExistingFile(icon);
        }
        catch { return null; }
    }

    // Modrinth App: profiles/<path>/, metadata in app.db (SQLite); old versions kept a profile.json per profile
    private static List<ExternalInstance> ScanModrinth(string folder)
    {
        // Accept the profiles folder too.
        if (!File.Exists(Path.Combine(folder, "app.db")) && Path.GetFileName(folder.TrimEnd('/', '\\')).Equals("profiles", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(folder.TrimEnd('/', '\\')) is { } parent)
            folder = parent;

        var profiles = Path.Combine(folder, "profiles");
        var db = Path.Combine(folder, "app.db");
        var result = new List<ExternalInstance>();
        if (File.Exists(db) && Directory.Exists(profiles))
            result = ReadModrinthDb(db, profiles);
        if (result.Count == 0)
        {
            foreach (var dir in InstanceDirs(folder, "profile.json", "profiles"))
            {
                try
                {
                    var meta = JsonUtil.ParseFile(Path.Combine(dir, "profile.json")).Obj("metadata");
                    var (loader, unsupported) = LoaderFromName(meta.Str("loader"));
                    var mc = meta.Str("game_version");
                    result.Add(new ExternalInstance(meta.Str("name") ?? Path.GetFileName(dir), dir, mc ?? "", loader,
                        meta.Obj("loader_version").Str("id"), ExistingFile(meta.Str("icon")), Unsupported: unsupported,
                        SkipFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "profile.json" }));
                }
                catch (Exception ex) { Log.Warn($"Skipping Modrinth profile {dir}: {ex.Message}"); }
            }
        }
        if (result.Count == 0) throw NothingFound(ExternalLauncher.Modrinth);
        return result;
    }

    private static List<ExternalInstance> ReadModrinthDb(string dbPath, string profilesDir)
    {
        var result = new List<ExternalInstance>();
        // Read a copy: the app keeps the database open while it runs.
        var tmp = Path.Combine(Path.GetTempPath(), $"pyro-modrinth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var copy = Path.Combine(tmp, "app.db");
            File.Copy(dbPath, copy);
            foreach (var ext in new[] { "-wal", "-shm" })
                if (File.Exists(dbPath + ext)) File.Copy(dbPath + ext, copy + ext);

            using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString());
            con.Open();
            HashSet<string> Columns(string table)
            {
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var info = con.CreateCommand();
                info.CommandText = $"PRAGMA table_info({table})";
                using var r = info.ExecuteReader();
                while (r.Read()) cols.Add(r.GetString(1));
                return cols;
            }

            using var cmd = con.CreateCommand();
            var profiles = Columns("profiles");
            var instances = Columns("instances");
            var sets = Columns("instance_content_sets");
            if (profiles.Contains("path") && profiles.Contains("game_version"))
            {
                // 0.8 - 0.10: one "profiles" row per instance
                string Col(string c) => profiles.Contains(c) ? c : "NULL";
                cmd.CommandText = $"SELECT path, {Col("name")}, {Col("icon_path")}, game_version, {Col("mod_loader")}, {Col("mod_loader_version")}, " +
                                  $"{Col("override_mc_memory_max")}, {Col("recent_time_played")}, {Col("submitted_time_played")}, {Col("last_played")} FROM profiles";
            }
            else if (instances.Contains("path") && sets.Contains("game_version") && instances.Contains("applied_content_set_id"))
            {
                // Newer versions: "instances" plus content sets that hold the game version and loader
                string I(string c) => instances.Contains(c) ? "i." + c : "NULL";
                string Sc(string c) => sets.Contains(c) ? "s." + c : "NULL";
                cmd.CommandText = $"SELECT i.path, {I("name")}, {I("icon_path")}, s.game_version, {Sc("loader")}, {Sc("loader_version")}, NULL, " +
                                  $"{I("recent_time_played")}, {I("submitted_time_played")}, {I("last_played")} " +
                                  "FROM instances i LEFT JOIN instance_content_sets s ON s.id = i.applied_content_set_id";
            }
            else
            {
                Log.Warn("Modrinth App database has an unknown layout");
                return result;
            }
            using var rd = cmd.ExecuteReader();
            string? S(int i) => rd.IsDBNull(i) ? null : Convert.ToString(rd.GetValue(i));
            long N(int i) => rd.IsDBNull(i) ? 0 : Convert.ToInt64(rd.GetValue(i));
            while (rd.Read())
            {
                var path = S(0);
                if (string.IsNullOrEmpty(path)) continue;
                var dir = Path.Combine(profilesDir, path);
                if (!Directory.Exists(dir)) continue;
                var (loader, unsupported) = LoaderFromName(S(4));
                var mem = N(6);
                var last = N(9);
                result.Add(new ExternalInstance(S(1) ?? path, dir, S(3) ?? "", loader, S(5), ExistingFile(S(2)),
                    N(7) + N(8), last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last).LocalDateTime : null,
                    mem is > 0 and < int.MaxValue ? (int)mem : null, null, unsupported));
            }
        }
        catch (Exception ex) { Log.Warn($"Reading Modrinth App database failed: {ex.Message}"); }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tmp, true); } catch { }
        }
        return result;
    }

    // ---------- importing ----------

    /// <summary>Caches and leftovers of the other launcher that the new instance doesn't need.</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "natives", ".fabric", ".cache", ".mixin.out", ".quilt", "libraries", "versions", "assets",
    };

    /// <summary>Creates a PyroLauncher instance with the same version and loader and copies the game folder into it.</summary>
    public static async Task<Instance> ImportAsync(ExternalInstance x, string? category, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        if (x.Unsupported != null) throw new NotSupportedException(x.Unsupported);
        var loaderVersion = x.LoaderVersion == null || x.Loader == ModLoader.Vanilla ? null
            : await LoaderService.NormalizeLoaderVersionAsync(x.Loader, x.MinecraftVersion, x.LoaderVersion, ct);

        var inst = InstanceManager.Create(x.Name, x.MinecraftVersion, x.Loader, loaderVersion, category);
        var instCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inst.IsBusy = true;
        inst.LaunchCts = instCts;
        inst.Status = L.T("Copying files…");
        try
        {
            var cfg = inst.Config;
            cfg.PlayTimeSeconds = x.PlayTimeSeconds;
            cfg.LastPlayed = x.LastPlayed;
            if (x.MaxMemoryMb is { } max) cfg.Overrides.MaxMemoryMb = max;
            if (x.MinMemoryMb is { } min) cfg.Overrides.MinMemoryMb = min;
            inst.Save();

            if (x.IconPath != null)
            {
                try { File.Copy(x.IconPath, inst.IconPath, true); inst.LoadIcon(); }
                catch (Exception ex) { Log.Warn($"Icon of {x.Name} not copied: {ex.Message}"); }
            }

            if (Directory.Exists(x.GameDir))
            {
                var report = new Progress<TaskProgress>(p => { progress.Report(p); inst.Status = p.Message; inst.Progress = p.Fraction; });
                await Task.Run(() => CopyGameDir(x, inst.GameDir, report, instCts.Token), instCts.Token);
            }
            inst.Refresh();
            return inst;
        }
        catch
        {
            try { InstanceManager.Delete(inst); } catch (Exception ex) { Log.Warn($"Cleanup of {inst.Dir} failed: {ex.Message}"); }
            throw;
        }
        finally
        {
            inst.IsBusy = false;
            inst.LaunchCts = null;
            inst.Status = null;
            inst.Progress = -1;
        }
    }

    private static void CopyGameDir(ExternalInstance x, string dest, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        var src = Path.GetFullPath(x.GameDir);
        var files = new List<(string rel, long size)>();
        var stack = new Stack<string>([src]);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var info = new DirectoryInfo(d);
                    // Don't follow links (Prism can link shared folders), and don't copy the other launcher's caches.
                    if (info.LinkTarget != null) continue;
                    if (dir == src && SkipDirs.Contains(info.Name)) continue;
                    stack.Push(d);
                }
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var rel = Path.GetRelativePath(src, f);
                    if (dir == src && x.SkipFiles?.Contains(rel) == true) continue;
                    files.Add((rel, new FileInfo(f).Length));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Log.Warn($"Skipped {dir}: {ex.Message}"); }
        }

        var total = Math.Max(1, files.Sum(f => f.size));
        long done = 0;
        var lastReport = DateTime.MinValue;
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (rel, size) = files[i];
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { File.Copy(Path.Combine(src, rel), target, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Could not copy {rel}: {ex.Message}"); }
            done += size;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100 || i == files.Count - 1)
            {
                lastReport = DateTime.UtcNow;
                progress.Report(new TaskProgress(L.F("Copying files… {0} of {1}", i + 1, files.Count), (double)done / total));
            }
        }
    }
}
