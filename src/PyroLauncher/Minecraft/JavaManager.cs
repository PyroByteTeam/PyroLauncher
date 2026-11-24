using PyroLauncher.UI;
using System.IO;
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

    public static string? FindInstalled(int major)
    {
        var dir = RuntimeDir(major);
        if (!Directory.Exists(dir)) return null;
        foreach (var exe in new[] { "javaw.exe", "java.exe" })
        {
            var direct = Path.Combine(dir, "bin", exe);
            if (File.Exists(direct)) return direct;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var p = Path.Combine(sub, "bin", exe);
                if (File.Exists(p)) return p;
            }
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
            var name = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(exe))!) ?? $"Java {major}";
            list.Add(new JavaRuntime(major, exe, name));
        }
        return list.OrderBy(j => j.Major).ToList();
    }

    /// <summary>Console java.exe next to a javaw.exe (useful for installers whose output we want).</summary>
    public static string ConsoleJava(string javaw)
    {
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
            var url = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture={arch}&image_type={image}&os=windows&vendor=eclipse";
            try
            {
                var arr = await Http.GetJsonAsync(url, ct) as System.Text.Json.Nodes.JsonArray;
                pkg = arr?.FirstOrDefault()?.Obj("binary")?.Obj("package");
                if (pkg != null) break;
            }
            catch (HttpError e) when (e.Status == System.Net.HttpStatusCode.NotFound) { }
        }
        if (pkg == null) throw new InvalidOperationException(L.F("Adoptium has no Java {0} build for windows/{1}.", major, arch));

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

        if (checksum != null && !Hashing.Sha256File(zipPath).Equals(checksum, StringComparison.OrdinalIgnoreCase))
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
            ZipFile.ExtractToDirectory(zipPath, tmp);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(tmp, target);
        }, ct);
        try { File.Delete(zipPath); } catch { }

        return FindInstalled(major) ?? throw new IOException(L.F("Java {0} was extracted but javaw.exe was not found.", major));
    }

    public static void Remove(int major)
    {
        var dir = RuntimeDir(major);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
