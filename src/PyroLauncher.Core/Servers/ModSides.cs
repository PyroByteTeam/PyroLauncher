using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PyroLauncher.Content;
using PyroLauncher.Core;

namespace PyroLauncher.Servers;

public enum ModSide { Unknown, Client, Server, Both }

/// <summary>
/// Works out whether mods are needed on the client, the server or both: from the mod's own metadata
/// (fabric.mod.json "environment") and from Modrinth / CurseForge project data. Results are cached on disk.
/// </summary>
public static class ModSides
{
    private static readonly string CachePath = Path.Combine(AppPaths.Cache, "mod-sides.json");
    private static Dictionary<string, ModSide>? _cache;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static async Task<Dictionary<LocalItem, ModSide>> ResolveAsync(IReadOnlyList<LocalItem> mods, CancellationToken ct = default)
    {
        await Lock.WaitAsync(ct);
        try
        {
            _cache ??= JsonUtil.Load<Dictionary<string, ModSide>>(CachePath) ?? [];
            var result = new Dictionary<LocalItem, ModSide>();
            var missingMr = new List<string>();
            var missingCf = new List<long>();

            foreach (var m in mods)
            {
                var side = FromMetadata(m.FilePath);
                if (side == ModSide.Unknown && m.Ref is { } r && _cache.TryGetValue(Key(r), out var cached)) side = cached;
                result[m] = side;
                if (side != ModSide.Unknown || m.Ref is not { } re) continue;
                if (re.Source == ContentSource.Modrinth) missingMr.Add(re.ProjectId);
                else if (long.TryParse(re.ProjectId, out var id)) missingCf.Add(id);
            }

            if (missingMr.Count > 0)
            {
                try
                {
                    foreach (var chunk in missingMr.Distinct().Chunk(200))
                    {
                        var ids = new JsonArray(chunk.Select(i => (JsonNode)i).ToArray()).ToJsonString();
                        var arr = await Http.GetJsonAsync($"https://api.modrinth.com/v2/projects?ids={Uri.EscapeDataString(ids)}", ct) as JsonArray;
                        foreach (var p in arr ?? [])
                            if (p.Str("id") is { } id) _cache[$"mr:{id}"] = Sides(p.Str("client_side"), p.Str("server_side"));
                    }
                }
                catch (Exception ex) { Log.Warn($"Modrinth side lookup failed: {ex.Message}"); }
            }

            if (missingCf.Count > 0 && CurseForgeApi.HasKey)
            {
                try
                {
                    var body = new JsonObject { ["modIds"] = new JsonArray(missingCf.Distinct().Select(i => (JsonNode)i).ToArray()) };
                    var data = (await Http.PostJsonAsync("https://api.curseforge.com/v1/mods", body, ct, CurseForgeApi.Auth)).Arr("data");
                    foreach (var m in data)
                    {
                        var tags = m.Arr("latestFiles").SelectMany(f => f.Strings("gameVersions")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        bool c = tags.Contains("Client"), s = tags.Contains("Server");
                        _cache[$"cf:{m.Long("id")}"] = c && s ? ModSide.Both : c ? ModSide.Client : s ? ModSide.Server : ModSide.Unknown;
                    }
                }
                catch (Exception ex) { Log.Warn($"CurseForge side lookup failed: {ex.Message}"); }
            }

            foreach (var m in mods)
                if (result[m] == ModSide.Unknown && m.Ref is { } r && _cache.TryGetValue(Key(r), out var side)) result[m] = side;
            try { JsonUtil.Save(CachePath, _cache); } catch { }
            return result;
        }
        finally { Lock.Release(); }
    }

    private static string Key(ContentRef r) => (r.Source == ContentSource.Modrinth ? "mr:" : "cf:") + r.ProjectId;

    private static ModSide Sides(string? client, string? server) =>
        server == "unsupported" ? ModSide.Client : client == "unsupported" ? ModSide.Server : ModSide.Both;

    private static ModSide FromMetadata(string jar)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jar);
            if (zip.GetEntry("fabric.mod.json") is { } e)
            {
                using var s = e.Open();
                return JsonNode.Parse(s).Str("environment") switch { "client" => ModSide.Client, "server" => ModSide.Server, _ => ModSide.Unknown };
            }
        }
        catch { }
        return ModSide.Unknown;
    }
}
