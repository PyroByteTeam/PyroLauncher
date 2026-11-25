using PyroLauncher.UI;
using System.IO;
using System.Formats.Tar;
using System.IO.Compression;
using PyroLauncher.Core;

namespace PyroLauncher.Minecraft;

public record JavaRuntime(int Major, string Path, string Name);

/// <summary>Manages Eclipse Temurin (Adoptium) runtimes downloaded into the launcher's own folder.</summary>
public static class JavaManager
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>Maps the Java version Minecraft asks for to an Adoptium LTS feature release.</summary>
    public static int MapMajor(int required) => required switch
    {
        <= 8 => 8,
        <= 17 => 17,
        <= 21 => 21,
        _ => required,
    };

    public static string RuntimeDir(int major) => Path.Combine(AppPaths.Java, major.ToString());

    /// <summary>Adoptium's name for this operating system.</summary>
    public static string OsName => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";

    /// <summary>Preferred executable names: javaw.exe on Windows (no console window), java elsewhere.</summary>
    private static string[] Executables => OperatingSystem.IsWindows() ? ["javaw.exe", "java.exe"] : ["java"];

    public static FileFilter ExecutableFilter => OperatingSystem.IsWindows()
        ? new FileFilter("Java", "javaw.exe", "java.exe")
        : new FileFilter("Java", "java");

    /// <summary>bin folders inside an extracted runtime (macOS JDKs keep it under Contents/Home).</summary>
    private static IEnumerable<string> BinDirs(string root)
    {
        yield return Path.Combine(root, "bin");
        yield return Path.Combine(root, "Contents", "Home", "bin");
        if (!Directory.Exists(root)) yield break;
        foreach (var sub in Directory.GetDirectories(root))
        {
            yield return Path.Combine(sub, "bin");
            yield return Path.Combine(sub, "Contents", "Home", "bin");
        }
    }

    public static string? FindInstalled(int major)
    {
        var dir = RuntimeDir(major);
        if (!Directory.Exists(dir)) return null;
        foreach (var exe in Executables)
            foreach (var bin in BinDirs(dir))
            {
                var p = Path.Combine(bin, exe);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    public static List<JavaRuntime> ListInstalled()
    {
        var list = new List<JavaRuntime>();
        if (!Directory.Exists(AppPaths.Java)) return list;
        foreach (var d in Directory.GetDirectories(AppPaths.Java))
        {
            if (!int.TryParse(Path.GetFileName(d), out var major)) continue;
            var exe = FindInstalled(major);
            if (exe == null) continue;
            var home = Path.GetDirectoryName(Path.GetDirectoryName(exe))!;
            if (Path.GetFileName(home) == "Home") home = Path.GetDirectoryName(Path.GetDirectoryName(home))!; // macOS bundle
            var name = Path.GetFileName(home) is { Length: > 0 } n ? n : $"Java {major}";
            list.Add(new JavaRuntime(major, exe, name));
        }
        return list.OrderBy(j => j.Major).ToList();
    }

    /// <summary>Console java.exe next to a javaw.exe (useful for installers whose output we want).</summary>
    public static string ConsoleJava(string javaw)
    {
        if (!OperatingSystem.IsWindows()) return javaw;
        var java = Path.Combine(Path.GetDirectoryName(javaw)!, "java.exe");
        return File.Exists(java) ? java : javaw;
    }

    public static async Task<string> EnsureAsync(int requiredMajor, IProgress<TaskProgress>? progress, CancellationToken ct)
    {
        var major = MapMajor(requiredMajor);
        var existing = FindInstalled(major);
        if (existing != null) return existing;

        await Lock.WaitAsync(ct);
        try
        {
            existing = FindInstalled(major);
            if (existing != null) return existing;
            return await InstallAsync(major, progress, ct);
        }
        finally { Lock.Release(); }
    }

    private static async Task<string> InstallAsync(int major, IProgress<TaskProgress>? progress, CancellationToken ct)
    {
        var arch = VersionResolver.ArchName switch { "arm64" => "aarch64", "x86" => "x32", _ => "x64" };
        progress?.Report(new TaskProgress(L.F("Looking up Java {0} (Adoptium)…", major)));

        System.Text.Json.Nodes.JsonNode? pkg = null;
        foreach (var image in new[] { "jre", "jdk" })
        {
            var url = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture={arch}&image_type={image}&os={OsName}&vendor=eclipse";
            try
            {
                var arr = await Http.GetJsonAsync(url, ct) as System.Text.Json.Nodes.JsonArray;
                pkg = arr?.FirstOrDefault()?.Obj("binary")?.Obj("package");
                if (pkg != null) break;
            }
            catch (HttpError e) when (e.Status == System.Net.HttpStatusCode.NotFound) { }
        }
        if (pkg == null) throw new InvalidOperationException(L.F("Adoptium has no Java {0} build for {1}/{2}.", major, OsName, arch));

        var link = pkg.Str("link")!;
        var fileName = pkg.Str("name") ?? $"java{major}.zip";
        var checksum = pkg.Str("checksum");
        var zipPath = Path.Combine(AppPaths.Cache, "java", fileName);

        var dlProgress = new Progress<(long done, long total)>(p =>
            progress?.Report(new TaskProgress(L.F("Downloading Java {0}… {1} MB", major, p.done / 1048576), p.total > 0 ? (double)p.done / p.total : -1)));
        progress?.Report(new TaskProgress(L.F("Downloading Java {0}…", major), 0));
        var job = DownloadManager.Current;
        job?.AddFiles(1);
        try { await Http.DownloadFileAsync(link, zipPath, ct, dlProgress); }
        finally { job?.FileDone(); }

        if (checksum != null && !(await Task.Run(() => Hashing.Sha256File(zipPath), ct)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zipPath);
            throw new IOException(L.T("Downloaded Java archive failed checksum verification."));
        }

        progress?.Report(new TaskProgress(L.F("Extracting Java {0}…", major)));
        var target = RuntimeDir(major);
        var tmp = target + ".tmp";
        await Task.Run(() =>
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            if (zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(zipPath, tmp);
            else
            {
                // macOS / Linux builds are .tar.gz; TarFile keeps the executable bits and symlinks.
                using var fs = File.OpenRead(zipPath);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, tmp, overwriteFiles: true);
            }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(tmp, target);
        }, ct);
        try { File.Delete(zipPath); } catch { }

        return FindInstalled(major) ?? throw new IOException(L.F("Java {0} was extracted but the java executable was not found.", major));
    }

    public static void Remove(int major)
    {
        var dir = RuntimeDir(major);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
