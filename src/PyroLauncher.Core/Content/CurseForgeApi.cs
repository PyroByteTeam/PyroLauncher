using PyroLauncher.UI;
using System.Text.Json.Nodes;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.Content;

public static class CurseForgeApi
{
    private const string Base = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;
    private static readonly string[] LoaderNames = ["Forge", "Fabric", "NeoForge", "Quilt", "LiteLoader"];

    public static bool HasKey => !string.IsNullOrWhiteSpace(SettingsService.Current.CurseForgeApiKey);

    internal static void Auth(System.Net.Http.HttpRequestMessage req)
    {
        if (!HasKey) throw new InvalidOperationException(L.T("CurseForge needs an API key. Add one in Settings → Launcher."));
        req.Headers.Add("x-api-key", SettingsService.Current.CurseForgeApiKey.Trim());
        req.Headers.Accept.ParseAdd("application/json");
    }

    public static int LoaderType(ModLoader l) => l switch
    {
        ModLoader.Forge => 1,
        ModLoader.Fabric => 4,
        ModLoader.NeoForge => 6,
        _ => 0,
    };

    private static readonly Dictionary<int, Dictionary<string, int>> CategoryCache = [];

    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Maps a Modrinth category slug to a CurseForge category id by normalized name, or null if CF has no equivalent.</summary>
    public static Task<int?> MatchCategoryAsync(ContentType type, string slug, CancellationToken ct) => MatchCategoryAsync(type.CurseForgeClass(), slug, ct);

    public static async Task<int?> MatchCategoryAsync(int cls, string slug, CancellationToken ct)
    {
        if (!CategoryCache.TryGetValue(cls, out var map))
        {
            var res = await Http.GetJsonAsync($"{Base}/categories?gameId={MinecraftGameId}&classId={cls}", ct, Auth);
            map = [];
            foreach (var c in res.Arr("data"))
                map.TryAdd(Norm(c.Str("name") ?? ""), c.Int("id"));
            CategoryCache[cls] = map;
        }
        return map.TryGetValue(Norm(slug), out var id) ? id : null;
    }

    public static async Task<List<ProjectSummary>> SearchAsync(string query, ContentType type, string mc, ModLoader loader, int offset, CancellationToken ct,
        SearchFilters? filters = null, int? categoryId = null)
    {
        var sortField = filters?.Sort switch
        {
            SearchSort.Downloads => 6,
            SearchSort.Newest => 11,
            SearchSort.Updated => 3,
            _ => 2,
        };
        var url = $"{Base}/mods/search?gameId={MinecraftGameId}&classId={type.CurseForgeClass()}" +
                  $"&searchFilter={Uri.EscapeDataString(query)}" + (string.IsNullOrEmpty(mc) ? "" : $"&gameVersion={Uri.EscapeDataString(mc)}") +
                  $"&sortField={sortField}&sortOrder=desc&pageSize=20&index={offset}";
        if (categoryId != null) url += $"&categoryId={categoryId}";
        if (type == ContentType.Mod && LoaderType(loader) is > 0 and var lt) url += $"&modLoaderType={lt}";
        var res = await Http.GetJsonAsync(url, ct, Auth);
        return res.Arr("data").Select(ParseSummary).ToList();
    }

    /// <summary>CF thumbnails of animated logos are flattened to a still frame, so animated logos use the original file.</summary>
    internal static string? LogoUrl(JsonNode? logo)
    {
        var url = logo.Str("url");
        if (url != null && (url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))) return url;
        return logo.Str("thumbnailUrl") ?? url;
    }

    private static ProjectSummary ParseSummary(JsonNode? m) => new()
    {
        Source = ContentSource.CurseForge,
        Id = m.Long("id").ToString(),
        Slug = m.Str("slug") ?? "",
        Title = m.Str("name") ?? "",
        Description = m.Str("summary") ?? "",
        Author = m.Arr("authors").FirstOrDefault().Str("name") ?? "",
        IconUrl = LogoUrl(m.Obj("logo")),
        Downloads = m.Long("downloadCount"),
        WebUrl = m.Obj("links")?.Str("websiteUrl"),
    };

    public static async Task<ProjectDetails> GetProjectAsync(string id, CancellationToken ct)
    {
        var m = (await Http.GetJsonAsync($"{Base}/mods/{id}", ct, Auth)).Obj("data");
        var s = ParseSummary(m);
        string body = "";
        try { body = (await Http.GetJsonAsync($"{Base}/mods/{id}/description", ct, Auth)).Str("data") ?? ""; }
        catch (Exception ex) { Log.Warn($"CF description failed: {ex.Message}"); }

        // Side and loaders come from the "game versions" tags of the latest files.
        var tags = m.Arr("latestFiles").SelectMany(f => f.Strings("gameVersions")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var idx in m.Arr("latestFilesIndexes"))
        {
            var lt = idx.Int("modLoader");
            var name = lt switch { 1 => "Forge", 4 => "Fabric", 5 => "Quilt", 6 => "NeoForge", 3 => "LiteLoader", _ => null };
            if (name != null) tags.Add(name);
        }
        bool client = tags.Contains("Client"), server = tags.Contains("Server");
        var side = client && server ? "Client & Server" : client ? "Client" : server ? "Server" : "Unknown";

        return new ProjectDetails
        {
            Source = s.Source,
            Id = s.Id,
            Slug = s.Slug,
            Title = s.Title,
            Description = s.Description,
            Author = s.Author,
            IconUrl = s.IconUrl,
            Downloads = s.Downloads,
            WebUrl = s.WebUrl,
            BodyHtml = body,
            Gallery = m.Arr("screenshots").Select(x => new GalleryImage(x.Str("url")!, x.Str("title"), x.Str("thumbnailUrl"))).ToList(),
            Side = side,
            Loaders = LoaderNames.Where(tags.Contains).ToList(),
            License = "Not provided by CurseForge",
            Categories = m.Arr("categories").Select(c => c.Str("name") ?? "").Where(c => c.Length > 0).ToList(),
        };
    }

    public static async Task<List<ProjectVersion>> GetVersionsAsync(string projectId, ContentType type, string? mc, ModLoader loader, CancellationToken ct)
    {
        var url = $"{Base}/mods/{projectId}/files?pageSize=50";
        if (mc != null)
        {
            url += $"&gameVersion={Uri.EscapeDataString(mc)}";
            if (type == ContentType.Mod && LoaderType(loader) is > 0 and var lt) url += $"&modLoaderType={lt}";
        }
        var res = await Http.GetJsonAsync(url, ct, Auth);
        return res.Arr("data").Select(f => ParseFile(f, projectId)).OrderByDescending(v => v.Date).ToList();
    }

    public static ProjectVersion ParseFile(JsonNode? f, string? projectId = null)
    {
        var gv = f.Strings("gameVersions").ToList();
        return new ProjectVersion
        {
            Source = ContentSource.CurseForge,
            ProjectId = projectId ?? f.Long("modId").ToString(),
            Id = f.Long("id").ToString(),
            Name = f.Str("displayName") ?? "",
            VersionNumber = f.Str("displayName") ?? "",
            Date = DateTime.TryParse(f.Str("fileDate"), out var d) ? d : default,
            ReleaseType = f.Int("releaseType") switch { 2 => "beta", 3 => "alpha", _ => "release" },
            ChangelogHtml = null,
            FileName = f.Str("fileName") ?? "",
            Url = f.Str("downloadUrl"),
            Sha1 = f.Arr("hashes").FirstOrDefault(h => h.Int("algo") == 1).Str("value"),
            ServerPackFileId = f.Long("serverPackFileId"),
            GameVersions = gv.Where(x => x.Length > 0 && char.IsDigit(x[0])).ToList(),
            Loaders = gv.Where(x => LoaderNames.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList(),
            Dependencies = f.Arr("dependencies")
                .Select(dep => new Dependency(dep.Long("modId").ToString(), null, dep.Int("relationType") == 3,
                    dep.Int("relationType") switch { 1 or 6 => "embedded", 2 or 4 => "optional", 5 => "incompatible", _ => "required" }))
                .ToList(),
        };
    }

    public static async Task<string> GetChangelogAsync(string projectId, string fileId, CancellationToken ct) =>
        (await Http.GetJsonAsync($"{Base}/mods/{projectId}/files/{fileId}/changelog", ct, Auth)).Str("data") ?? "";

    public static async Task<List<ProjectVersion>> GetFilesAsync(IEnumerable<long> fileIds, CancellationToken ct)
    {
        var ids = new JsonArray(fileIds.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        var res = await Http.PostJsonAsync($"{Base}/mods/files", new JsonObject { ["fileIds"] = ids }, ct, Auth);
        return res.Arr("data").Select(f => ParseFile(f)).ToList();
    }

    /// <summary>modId → (title, icon, classId)</summary>
    public static async Task<Dictionary<string, (string title, string? icon, int classId, string? url)>> GetModsBriefAsync(IEnumerable<long> modIds, CancellationToken ct)
    {
        var ids = new JsonArray(modIds.Distinct().Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        var res = await Http.PostJsonAsync($"{Base}/mods", new JsonObject { ["modIds"] = ids }, ct, Auth);
        var dict = new Dictionary<string, (string, string?, int, string?)>();
        foreach (var m in res.Arr("data"))
            dict[m.Long("id").ToString()] = (m.Str("name") ?? "", LogoUrl(m.Obj("logo")), m.Int("classId"), m.Obj("links")?.Str("websiteUrl"));
        return dict;
    }

    /// <summary>modId → newest file id for the game version (and loader, for mods), from the mods' latestFilesIndexes.</summary>
    public static async Task<Dictionary<string, long>> GetLatestFileIdsAsync(IEnumerable<long> modIds, ContentType type, string mc, ModLoader loader, CancellationToken ct)
    {
        var ids = new JsonArray(modIds.Distinct().Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        var res = await Http.PostJsonAsync($"{Base}/mods", new JsonObject { ["modIds"] = ids }, ct, Auth);
        var lt = LoaderType(loader);
        var dict = new Dictionary<string, long>();
        foreach (var m in res.Arr("data"))
        {
            var best = m.Arr("latestFilesIndexes")
                .Where(i => i.Str("gameVersion") == mc && (type != ContentType.Mod || lt == 0 || i.Int("modLoader") == lt))
                .Select(i => i.Long("fileId"))
                .DefaultIfEmpty(0).Max();
            if (best > 0) dict[m.Long("id").ToString()] = best;
        }
        return dict;
    }

    /// <summary>fingerprint → matched file.</summary>
    public static async Task<Dictionary<uint, ProjectVersion>> IdentifyAsync(IEnumerable<uint> fingerprints, CancellationToken ct)
    {
        var arr = new JsonArray(fingerprints.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        var res = await Http.PostJsonAsync($"{Base}/fingerprints/{MinecraftGameId}", new JsonObject { ["fingerprints"] = arr }, ct, Auth);
        var dict = new Dictionary<uint, ProjectVersion>();
        foreach (var m in res.Obj("data").Arr("exactMatches"))
        {
            var file = m.Obj("file");
            var fp = (uint)file.Long("fileFingerprint");
            dict[fp] = ParseFile(file, m.Long("id").ToString());
        }
        return dict;
    }
}
