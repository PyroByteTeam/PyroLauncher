using System.Text.Json.Nodes;
using Markdig;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.Content;

public static class ModrinthApi
{
    private const string Base = "https://api.modrinth.com/v2";
    private static readonly MarkdownPipeline Md = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static string MarkdownToHtml(string? md) => string.IsNullOrEmpty(md) ? "" : Markdown.ToHtml(md, Md);

    public static async Task<List<ProjectSummary>> SearchAsync(string query, ContentType type, string mc, ModLoader loader, int offset, CancellationToken ct,
        SearchFilters? filters = null)
    {
        filters ??= new SearchFilters();
        var facets = new JsonArray { new JsonArray($"project_type:{type.ModrinthType()}") };
        if (!string.IsNullOrEmpty(mc)) facets.Add(new JsonArray($"versions:{mc}"));
        if (type == ContentType.Mod && loader != ModLoader.Vanilla)
            facets.Add(new JsonArray($"categories:{loader.LoaderSlug()}"));
        if (!string.IsNullOrEmpty(filters.Category))
            facets.Add(new JsonArray($"categories:{filters.Category}"));
        if (filters.Environment is "client" or "server")
            facets.Add(new JsonArray($"{filters.Environment}_side:required", $"{filters.Environment}_side:optional"));

        var index = filters.Sort switch
        {
            SearchSort.Downloads => "downloads",
            SearchSort.Newest => "newest",
            SearchSort.Updated => "updated",
            _ => string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance",
        };
        var url = $"{Base}/search?query={Uri.EscapeDataString(query)}&limit=20&offset={offset}" +
                  $"&index={index}" +
                  $"&facets={Uri.EscapeDataString(facets.ToJsonString())}";
        var res = await Http.GetJsonAsync(url, ct);
        return res.Arr("hits").Select(h => new ProjectSummary
        {
            Source = ContentSource.Modrinth,
            Id = h.Str("project_id")!,
            Slug = h.Str("slug") ?? "",
            Title = h.Str("title") ?? "",
            Description = h.Str("description") ?? "",
            Author = h.Str("author") ?? "",
            IconUrl = h.Str("icon_url"),
            Downloads = h.Long("downloads"),
            WebUrl = $"https://modrinth.com/{type.ModrinthType()}/{h.Str("slug")}",
        }).ToList();
    }

    public static async Task<ProjectDetails> GetProjectAsync(string id, CancellationToken ct)
    {
        var p = await Http.GetJsonAsync($"{Base}/project/{Uri.EscapeDataString(id)}", ct);
        var d = new ProjectDetails
        {
            Source = ContentSource.Modrinth,
            Id = p.Str("id")!,
            Slug = p.Str("slug") ?? "",
            Title = p.Str("title") ?? "",
            Description = p.Str("description") ?? "",
            IconUrl = p.Str("icon_url"),
            Downloads = p.Long("downloads"),
            BodyHtml = MarkdownToHtml(p.Str("body")),
            Gallery = p.Arr("gallery")
                .OrderByDescending(g => g.Bool("featured"))
                .Select(g => new GalleryImage(g.Str("raw_url") ?? g.Str("url")!, g.Str("title"), g.Str("url")))
                .ToList(),
            Side = SideText(p.Str("client_side"), p.Str("server_side")),
            Loaders = p.Strings("loaders").Select(Pretty).ToList(),
            License = p.Obj("license")?.Str("name") is { Length: > 0 } ln ? ln : p.Obj("license")?.Str("id") ?? "Unknown",
            LicenseUrl = p.Obj("license")?.Str("url"),
            Categories = p.Strings("categories").ToList(),
        };
        d.WebUrl = $"https://modrinth.com/{p.Str("project_type") ?? "mod"}/{d.Slug}";
        try
        {
            var team = await Http.GetJsonAsync($"{Base}/project/{Uri.EscapeDataString(id)}/members", ct) as JsonArray;
            var owner = team?.FirstOrDefault(m => m.Str("role") == "Owner") ?? team?.FirstOrDefault();
            d.Author = owner?.Obj("user").Str("username") ?? "";
        }
        catch { /* not critical */ }
        // Custom SPDX refs look like "LicenseRef-Polyform-Shield-1.0.0" - make them readable.
        if (d.License.StartsWith("LicenseRef-", StringComparison.OrdinalIgnoreCase))
            d.License = d.License["LicenseRef-".Length..].Replace('-', ' ');
        return d;
    }

    private static string SideText(string? client, string? server)
    {
        bool c = client is "required" or "optional";
        bool s = server is "required" or "optional";
        if (c && s) return client == "required" && server == "required" ? "Client & Server (required on both)" : "Client & Server";
        if (c) return "Client";
        if (s) return "Server";
        return "Unknown";
    }

    public static string Pretty(string loader) => loader switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        "iris" => "Iris",
        "optifine" => "OptiFine",
        "canvas" => "Canvas",
        "minecraft" => "Minecraft",
        "liteloader" => "LiteLoader",
        "modloader" => "ModLoader",
        "rift" => "Rift",
        _ => loader.Length > 0 ? char.ToUpper(loader[0]) + loader[1..] : loader,
    };

    public static async Task<List<ProjectVersion>> GetVersionsAsync(string projectId, ContentType type, string? mc, ModLoader loader, CancellationToken ct)
    {
        var url = $"{Base}/project/{Uri.EscapeDataString(projectId)}/version";
        var q = new List<string>();
        if (mc != null) q.Add("game_versions=" + Uri.EscapeDataString(new JsonArray(mc).ToJsonString()));
        if (type == ContentType.Mod && loader != ModLoader.Vanilla && mc != null)
            q.Add("loaders=" + Uri.EscapeDataString(new JsonArray(loader.LoaderSlug()).ToJsonString()));
        if (q.Count > 0) url += "?" + string.Join("&", q);
        var arr = await Http.GetJsonAsync(url, ct) as JsonArray ?? [];
        return arr.Select(ParseVersion).ToList();
    }

    public static async Task<ProjectVersion> GetVersionAsync(string versionId, CancellationToken ct) =>
        ParseVersion(await Http.GetJsonAsync($"{Base}/version/{Uri.EscapeDataString(versionId)}", ct));

    public static ProjectVersion ParseVersion(JsonNode? v)
    {
        var file = v.Arr("files").FirstOrDefault(f => f.Bool("primary")) ?? v.Arr("files").FirstOrDefault();
        return new ProjectVersion
        {
            Source = ContentSource.Modrinth,
            ProjectId = v.Str("project_id") ?? "",
            Id = v.Str("id") ?? "",
            Name = v.Str("name") ?? "",
            VersionNumber = v.Str("version_number") ?? "",
            Date = DateTime.TryParse(v.Str("date_published"), out var d) ? d : default,
            ReleaseType = v.Str("version_type") ?? "release",
            ChangelogHtml = MarkdownToHtml(v.Str("changelog")),
            FileName = file.Str("filename") ?? "",
            Url = file.Str("url"),
            Sha1 = file.Obj("hashes").Str("sha1"),
            GameVersions = v.Strings("game_versions").ToList(),
            Loaders = v.Strings("loaders").Select(Pretty).ToList(),
            Dependencies = v.Arr("dependencies")
                .Where(dep => dep.Str("project_id") != null)
                .Select(dep => new Dependency(dep.Str("project_id")!, dep.Str("version_id"), dep.Str("dependency_type") == "required", dep.Str("dependency_type") ?? "required"))
                .ToList(),
            ExtraFiles = v.Arr("files").Where(f => f != file && f.Str("url") != null)
                .Select(f => new GalleryImage(f.Str("url")!, f.Str("filename"), null)).ToList(),
        };
    }

    /// <summary>sha1 → (version) for files known to Modrinth.</summary>
    public static async Task<Dictionary<string, ProjectVersion>> IdentifyAsync(IEnumerable<string> sha1s, CancellationToken ct)
    {
        var list = sha1s.ToList();
        var result = new Dictionary<string, ProjectVersion>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0) return result;
        var res = await Http.PostJsonAsync($"{Base}/version_files", new JsonObject
        {
            ["hashes"] = new JsonArray(list.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
            ["algorithm"] = "sha1",
        }, ct) as JsonObject;
        if (res == null) return result;
        foreach (var kv in res) result[kv.Key] = ParseVersion(kv.Value);
        return result;
    }

    private static List<(string name, string type)>? _categories;

    /// <summary>Category slugs for a project type (e.g. "optimization", "utility" for mods).</summary>
    public static async Task<List<string>> GetCategoriesAsync(ContentType type, CancellationToken ct)
    {
        _categories ??= (await Http.GetJsonAsync($"{Base}/tag/category", ct) as JsonArray ?? [])
            .Select(c => (c.Str("name") ?? "", c.Str("project_type") ?? "")).ToList();
        return _categories.Where(c => c.type == type.ModrinthType() && c.name.Length > 0)
            .Select(c => c.name).Distinct().OrderBy(n => n).ToList();
    }

    /// <summary>sha1 → newest version compatible with the given loaders / game version.</summary>
    public static async Task<Dictionary<string, ProjectVersion>> GetLatestVersionsAsync(IEnumerable<string> sha1s, IEnumerable<string>? loaders, string mc, CancellationToken ct)
    {
        var list = sha1s.ToList();
        var result = new Dictionary<string, ProjectVersion>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0) return result;
        var body = new JsonObject
        {
            ["hashes"] = new JsonArray(list.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
            ["algorithm"] = "sha1",
            ["game_versions"] = new JsonArray(mc),
        };
        if (loaders != null) body["loaders"] = new JsonArray(loaders.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
        if (await Http.PostJsonAsync($"{Base}/version_files/update", body, ct) is JsonObject res)
            foreach (var kv in res) result[kv.Key] = ParseVersion(kv.Value);
        return result;
    }

    public static async Task<Dictionary<string, (string title, string? icon)>> GetProjectsBriefAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var result = new Dictionary<string, (string, string?)>();
        if (list.Count == 0) return result;
        var arr = new JsonArray(list.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        var res = await Http.GetJsonAsync($"{Base}/projects?ids={Uri.EscapeDataString(arr.ToJsonString())}", ct) as JsonArray;
        foreach (var p in res ?? [])
            result[p.Str("id")!] = (p.Str("title") ?? "", p.Str("icon_url"));
        return result;
    }
}
