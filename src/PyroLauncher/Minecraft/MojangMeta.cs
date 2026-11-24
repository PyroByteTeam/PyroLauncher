using System.IO;
using System.Text.Json.Nodes;
using PyroLauncher.Core;

namespace PyroLauncher.Minecraft;

public record McVersionInfo(string Id, string Type, string Url, string? Sha1, DateTime ReleaseTime)
{
    public bool IsRelease => Type == "release";
    public override string ToString() => Id;
}

public static class MojangMeta
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static List<McVersionInfo>? _cache;
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static string ManifestCache => Path.Combine(AppPaths.Meta, "version_manifest_v2.json");

    public static async Task<List<McVersionInfo>> GetVersionsAsync(CancellationToken ct = default)
    {
        await Lock.WaitAsync(ct);
        try
        {
            if (_cache != null) return _cache;
            JsonNode? root;
            try
            {
                var text = await Http.GetStringAsync(ManifestUrl, ct);
                Directory.CreateDirectory(AppPaths.Meta);
                await File.WriteAllTextAsync(ManifestCache, text, ct);
                root = JsonNode.Parse(text);
            }
            catch (Exception ex) when (File.Exists(ManifestCache))
            {
                Log.Warn($"Version manifest offline, using cache: {ex.Message}");
                root = JsonUtil.ParseFile(ManifestCache);
            }
            _cache = root.Arr("versions").Select(v => new McVersionInfo(
                v.Str("id")!, v.Str("type") ?? "release", v.Str("url")!, v.Str("sha1"),
                DateTime.TryParse(v.Str("releaseTime"), out var d) ? d : DateTime.MinValue)).ToList();
            return _cache;
        }
        finally { Lock.Release(); }
    }

    public static string VersionJsonPath(string id) => Path.Combine(AppPaths.Versions, id, id + ".json");
    public static string VersionJarPath(string id) => Path.Combine(AppPaths.Versions, id, id + ".jar");

    /// <summary>Ensures the vanilla version JSON for <paramref name="id"/> is present locally.</summary>
    public static async Task<string> EnsureVersionJsonAsync(string id, CancellationToken ct)
    {
        var path = VersionJsonPath(id);
        if (File.Exists(path)) return path;
        var versions = await GetVersionsAsync(ct);
        var info = versions.FirstOrDefault(v => v.Id == id)
                   ?? throw new InvalidOperationException($"Minecraft version {id} was not found in the version manifest.");
        await Downloader.DownloadOneAsync(new DownloadItem(info.Url, path, info.Sha1), ct);
        return path;
    }
}
