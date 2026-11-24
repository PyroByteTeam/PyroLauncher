using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PyroLauncher.Core;

namespace PyroLauncher.Minecraft;

public class PreparedGame
{
    public List<string> Classpath { get; } = [];
    public List<string> NativeJars { get; } = [];
    public string ClientJar { get; set; } = "";
    public string AssetsRoot { get; set; } = AppPaths.Assets;
    public string AssetsIndexName { get; set; } = "legacy";
    public string GameAssetsDir { get; set; } = AppPaths.Assets;
    public string? LoggingArgument { get; set; }
}

/// <summary>Downloads libraries, client jar, assets and logging configs for a resolved version.</summary>
public static class GameInstaller
{
    private const string LibrariesBase = "https://libraries.minecraft.net/";
    private const string MavenCentral = "https://repo1.maven.org/maven2/";
    private const string ResourcesBase = "https://resources.download.minecraft.net/";

    public static async Task<PreparedGame> PrepareAsync(ResolvedVersion v, string gameDir, IProgress<TaskProgress>? progress, CancellationToken ct)
    {
        var result = new PreparedGame();
        var downloads = new List<DownloadItem>();

        foreach (var lib in v.Libraries)
        {
            if (!VersionResolver.RulesAllow(lib.Get("rules"))) continue;
            var name = lib.Str("name")!;
            var dl = lib.Obj("downloads");

            // Old style natives: "natives": { "windows": "natives-windows-${arch}" }
            if (lib.Obj("natives") is { } natives)
            {
                var cls = natives.Str("windows");
                if (cls != null)
                {
                    cls = cls.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                    var cdl = dl?.Obj("classifiers")?.Obj(cls);
                    string path; string? url; string? sha1 = null; long size = -1;
                    if (cdl != null)
                    {
                        path = Path.Combine(AppPaths.Libraries, cdl.Str("path") ?? VersionResolver.MavenPath(name + ":" + cls));
                        url = cdl.Str("url"); sha1 = cdl.Str("sha1"); size = cdl.Long("size", -1);
                    }
                    else
                    {
                        var rel = VersionResolver.MavenPath(name + ":" + cls);
                        path = Path.Combine(AppPaths.Libraries, rel);
                        url = BaseUrl(lib) + rel;
                    }
                    if (!string.IsNullOrEmpty(url)) downloads.Add(new DownloadItem(url, path, sha1, size));
                    result.NativeJars.Add(path);
                }
                // Some old natives libraries also have a main artifact.
                if (dl?.Obj("artifact") is { } nart)
                    AddArtifact(lib, name, nart, downloads, result);
                continue;
            }

            var classifier = VersionResolver.Classifier(name);
            if (classifier != null && classifier.StartsWith("natives-"))
            {
                if (!NativeClassifierMatches(classifier)) continue;
            }

            var artifact = dl?.Obj("artifact");
            AddArtifact(lib, name, artifact, downloads, result);
            if (classifier != null && classifier.StartsWith("natives-"))
                result.NativeJars.Add(result.Classpath[^1]);
        }

        // Client jar
        result.ClientJar = MojangMeta.VersionJarPath(v.JarId);
        if (v.ClientDownload != null)
            downloads.Add(new DownloadItem(v.ClientDownload.Str("url")!, result.ClientJar, v.ClientDownload.Str("sha1"), v.ClientDownload.Long("size", -1)));

        // Logging config
        if (v.LoggingClient?.Obj("file") is { } logFile)
        {
            var logPath = Path.Combine(AppPaths.Assets, "log_configs", logFile.Str("id") ?? "client.xml");
            downloads.Add(new DownloadItem(logFile.Str("url")!, logPath, logFile.Str("sha1"), logFile.Long("size", -1)));
            var arg = v.LoggingClient.Str("argument");
            if (arg != null) result.LoggingArgument = arg.Replace("${path}", logPath);
        }

        await Downloader.DownloadAllAsync(downloads, progress, "libraries", ct);

        // Like the official launcher, the client jar sits at versions/<id>/<id>.jar of the *launched* version.
        // Forge/NeoForge rely on this: their -DignoreList contains "${version_name}.jar" to keep it off the module path.
        bool usesIgnoreList = v.JvmArgs.Any(a => a.AsStr()?.Contains("ignoreList") == true);
        if (usesIgnoreList && v.Id != v.JarId && File.Exists(result.ClientJar))
        {
            var own = MojangMeta.VersionJarPath(v.Id);
            var src = new FileInfo(result.ClientJar);
            var dst = new FileInfo(own);
            if (!dst.Exists || dst.Length != src.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(own)!);
                File.Copy(src.FullName, own, true);
            }
            result.ClientJar = own;
        }

        // Assets
        if (v.AssetIndex != null)
            await PrepareAssetsAsync(v, gameDir, result, progress, ct);

        result.Classpath.Add(result.ClientJar);
        return result;
    }

    private static string BaseUrl(JsonObject lib)
    {
        var url = lib.Str("url");
        if (string.IsNullOrWhiteSpace(url)) return LibrariesBase;
        url = url.Replace("http://", "https://")
                 .Replace("files.minecraftforge.net/maven", "maven.minecraftforge.net");
        return url.EndsWith('/') ? url : url + "/";
    }

    private static void AddArtifact(JsonObject lib, string name, JsonObject? artifact, List<DownloadItem> downloads, PreparedGame result)
    {
        string rel = artifact?.Str("path") is { Length: > 0 } p ? p : VersionResolver.MavenPath(name);
        var path = Path.Combine(AppPaths.Libraries, rel.Replace('/', Path.DirectorySeparatorChar));
        if (artifact != null)
        {
            var url = artifact.Str("url");
            if (!string.IsNullOrEmpty(url))
                downloads.Add(new DownloadItem(url, path, artifact.Str("sha1"), artifact.Long("size", -1)));
            // empty url => file is generated by an installer (Forge); nothing to download
        }
        else
        {
            downloads.Add(new DownloadItem(BaseUrl(lib) + rel, path, null, -1, [MavenCentral + rel, LibrariesBase + rel]));
        }
        if (!result.Classpath.Contains(path, StringComparer.OrdinalIgnoreCase))
            result.Classpath.Add(path);
    }

    private static bool NativeClassifierMatches(string classifier)
    {
        if (!classifier.Contains("windows")) return false;
        var arch = VersionResolver.ArchName;
        if (classifier.EndsWith("arm64")) return arch == "arm64";
        if (classifier.EndsWith("x86")) return arch == "x86";
        return arch == "x64"; // plain "natives-windows" is the x64 build
    }

    private static async Task PrepareAssetsAsync(ResolvedVersion v, string gameDir, PreparedGame result, IProgress<TaskProgress>? progress, CancellationToken ct)
    {
        var idx = v.AssetIndex!;
        var id = idx.Str("id") ?? v.Assets ?? "legacy";
        result.AssetsIndexName = id;
        var indexPath = Path.Combine(AppPaths.Assets, "indexes", id + ".json");
        await Downloader.DownloadAllAsync([new DownloadItem(idx.Str("url")!, indexPath, idx.Str("sha1"), idx.Long("size", -1))], null, "asset index", ct);

        var index = JsonUtil.ParseFile(indexPath)!;
        var isVirtual = index.Bool("virtual");
        var mapToResources = index.Bool("map_to_resources");
        var objects = index.Obj("objects") ?? new JsonObject();
        var items = new List<DownloadItem>();
        var mapping = new List<(string name, string objPath)>();
        foreach (var kv in objects)
        {
            var hash = kv.Value.Str("hash");
            if (hash == null) continue;
            var sub = hash[..2];
            var objPath = Path.Combine(AppPaths.Assets, "objects", sub, hash);
            items.Add(new DownloadItem($"{ResourcesBase}{sub}/{hash}", objPath, hash, kv.Value.Long("size", -1)));
            mapping.Add((kv.Key, objPath));
        }
        await Downloader.DownloadAllAsync(items, progress, "assets", ct);

        if (isVirtual || mapToResources)
        {
            var target = mapToResources ? Path.Combine(gameDir, "resources") : Path.Combine(AppPaths.Assets, "virtual", id);
            progress?.Report(new TaskProgress("Copying legacy assets…"));
            await Task.Run(() =>
            {
                foreach (var (name, objPath) in mapping)
                {
                    var dest = Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(dest)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(objPath, dest, true);
                }
            }, ct);
            result.GameAssetsDir = target;
        }
    }

    public static void ExtractNatives(PreparedGame game, string nativesDir)
    {
        try { if (Directory.Exists(nativesDir)) Directory.Delete(nativesDir, true); }
        catch (Exception ex) { Log.Warn($"Could not clean natives dir: {ex.Message}"); }
        Directory.CreateDirectory(nativesDir);
        foreach (var jar in game.NativeJars.Distinct())
        {
            if (!File.Exists(jar)) continue;
            using var zip = ZipFile.OpenRead(jar);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(e.Name)) continue;
                if (!e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(nativesDir, e.Name);
                try { e.ExtractToFile(dest, true); }
                catch (IOException) { /* in use, ignore */ }
            }
        }
    }
}
