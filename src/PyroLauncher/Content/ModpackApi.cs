using System.IO;
using System.Text.Json.Nodes;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.UI;

namespace PyroLauncher.Content;

/// <summary>A modpack in Discover search results.</summary>
public class ModpackSummary : ProjectSummary
{
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public List<string> GameVersions { get; set; } = [];
    public List<string> Loaders { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    /// <summary>Wide banner image (featured gallery image / first screenshot) for grid cards.</summary>
    public string? ImageUrl { get; set; }

    public string CreatedText => Created == default ? "-" : Created.ToString("d MMM yyyy", L.Culture);
    public string UpdatedText => Updated == default ? "-" : RelativeDate(Updated);
    public string GameVersionsText => GameVersions.Count == 0 ? "" :
        GameVersions.Count <= 3 ? string.Join(", ", GameVersions) : $"{GameVersions[0]} - {GameVersions[^1]}";
    public string LoadersText => string.Join(", ", Loaders);
    public string CategoriesText => string.Join(" · ", Categories.Take(3));
    public bool HasBanner => !string.IsNullOrEmpty(ImageUrl);
    public string? BackdropUrl => ImageUrl ?? IconUrl;

    public static string RelativeDate(DateTime d)
    {
        var span = DateTime.Now - d.ToLocalTime();
        if (span.TotalHours < 1) return L.T("just now");
        if (span.TotalDays < 1) return L.F("{0} h ago", (int)span.TotalHours);
        if (span.TotalDays < 31) return L.F("{0} d ago", (int)span.TotalDays);
        if (span.TotalDays < 365) return L.F("{0} mo ago", (int)(span.TotalDays / 30));
        return L.F("{0} y ago", (int)(span.TotalDays / 365));
    }
}

public record DiscoverQuery(string Text, SourceFilter Platform, string? GameVersion, ModLoader? Loader, string? Category, SearchSort Sort, int Page);

/// <summary>Searching, downloading, installing and updating modpacks from Modrinth and CurseForge.</summary>
public static class ModpackApi
{
    private const string Modrinth = "https://api.modrinth.com/v2";
    private const string CurseForge = "https://api.curseforge.com/v1";
    public const int CurseForgeModpackClass = 4471;
    public const int PageSize = 20;

    public static async Task<(List<ModpackSummary> results, string? warning)> SearchAsync(DiscoverQuery q, CancellationToken ct)
    {
        var tasks = new List<Task<List<ModpackSummary>>>();
        string? warning = null;
        if (q.Platform != SourceFilter.CurseForge) tasks.Add(SearchModrinthAsync(q, ct));
        if (q.Platform != SourceFilter.Modrinth)
        {
            if (!CurseForgeApi.HasKey) warning = L.T("CurseForge results are off - add a CurseForge API key in Settings.");
            else tasks.Add(SearchCurseForgeAsync(q, ct));
        }

        var lists = new List<List<ModpackSummary>>();
        foreach (var t in tasks)
        {
            try { lists.Add(await t); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Modpack search failed: {ex.Message}");
                warning = ex is HttpError { Status: System.Net.HttpStatusCode.Forbidden }
                    ? L.T("CurseForge rejected the API key (403). Check it in Settings → Launcher.")
                    : ex.Message;
            }
        }

        var merged = new List<ModpackSummary>();
        for (int i = 0; lists.Any(l => i < l.Count); i++)
            foreach (var l in lists)
                if (i < l.Count) merged.Add(l[i]);
        return (merged, warning);
    }

    private static async Task<List<ModpackSummary>> SearchModrinthAsync(DiscoverQuery q, CancellationToken ct)
    {
        var facets = new JsonArray { new JsonArray("project_type:modpack") };
        if (!string.IsNullOrEmpty(q.GameVersion)) facets.Add(new JsonArray($"versions:{q.GameVersion}"));
        if (q.Loader is { } l and not ModLoader.Vanilla) facets.Add(new JsonArray($"categories:{l.LoaderSlug()}"));
        if (!string.IsNullOrEmpty(q.Category)) facets.Add(new JsonArray($"categories:{q.Category}"));
        var index = q.Sort switch
        {
            SearchSort.Downloads => "downloads",
            SearchSort.Newest => "newest",
            SearchSort.Updated => "updated",
            _ => string.IsNullOrWhiteSpace(q.Text) ? "downloads" : "relevance",
        };
        var url = $"{Modrinth}/search?query={Uri.EscapeDataString(q.Text)}&limit={PageSize}&offset={q.Page * PageSize}&index={index}" +
                  $"&facets={Uri.EscapeDataString(facets.ToJsonString())}";
        var res = await Http.GetJsonAsync(url, ct);
        string[] loaderSlugs = ["fabric", "forge", "neoforge", "quilt"];
        var list = res.Arr("hits").Select(h =>
        {
            var cats = h.Strings("categories").ToList();
            return new ModpackSummary
            {
                Source = ContentSource.Modrinth,
                Id = h.Str("project_id")!,
                Slug = h.Str("slug") ?? "",
                Title = h.Str("title") ?? "",
                Description = h.Str("description") ?? "",
                Author = h.Str("author") ?? "",
                IconUrl = h.Str("icon_url") is { Length: > 0 } i ? i : null,
                Downloads = h.Long("downloads"),
                WebUrl = $"https://modrinth.com/modpack/{h.Str("slug")}",
                Created = ParseDate(h.Str("date_created")),
                Updated = ParseDate(h.Str("date_modified")),
                GameVersions = SortVersions(h.Strings("versions").Where(v => v.Length > 0 && char.IsDigit(v[0]) && !v.Contains('w') && !v.Contains('-'))),
                Loaders = cats.Where(c => loaderSlugs.Contains(c)).Select(ModrinthApi.Pretty).ToList(),
                Categories = h.Strings("display_categories").Where(c => !loaderSlugs.Contains(c)).Select(ModrinthApi.Pretty).ToList(),
                ImageUrl = h.Str("featured_gallery") is { Length: > 0 } g ? g : h.Arr("gallery").FirstOrDefault().AsStr(),
            };
        }).ToList();
        await UpgradeGalleryAsync(list, ct);
        return list;
    }

    /// <summary>Search hits only carry 350px gallery thumbnails; swap in the full-size featured image (one batch request).</summary>
    private static async Task UpgradeGalleryAsync(List<ModpackSummary> list, CancellationToken ct)
    {
        var ids = list.Where(m => m.ImageUrl != null).Select(m => m.Id).ToList();
        if (ids.Count == 0) return;
        try
        {
            var arr = new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
            var res = await Http.GetJsonAsync($"{Modrinth}/projects?ids={Uri.EscapeDataString(arr.ToJsonString())}", ct) as JsonArray;
            var byId = (res ?? []).ToDictionary(p => p.Str("id") ?? "", p => p);
            foreach (var m in list)
            {
                if (!byId.TryGetValue(m.Id, out var p)) continue;
                var g = p.Arr("gallery").OrderByDescending(x => x.Bool("featured")).FirstOrDefault();
                if (g.Str("url") is { Length: > 0 } full) m.ImageUrl = full;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Gallery upgrade failed: {ex.Message}"); }
    }

    private static async Task<List<ModpackSummary>> SearchCurseForgeAsync(DiscoverQuery q, CancellationToken ct)
    {
        var sortField = q.Sort switch
        {
            SearchSort.Downloads => 6,
            SearchSort.Newest => 11,
            SearchSort.Updated => 3,
            _ => string.IsNullOrWhiteSpace(q.Text) ? 2 : 2,
        };
        var url = $"{CurseForge}/mods/search?gameId=432&classId={CurseForgeModpackClass}&searchFilter={Uri.EscapeDataString(q.Text)}" +
                  $"&sortField={sortField}&sortOrder=desc&pageSize={PageSize}&index={q.Page * PageSize}";
        if (!string.IsNullOrEmpty(q.GameVersion)) url += $"&gameVersion={Uri.EscapeDataString(q.GameVersion)}";
        if (q.Loader is { } l && CurseForgeApi.LoaderType(l) is > 0 and var lt) url += $"&modLoaderType={lt}";
        if (!string.IsNullOrEmpty(q.Category))
        {
            var cat = await CurseForgeApi.MatchCategoryAsync(CurseForgeModpackClass, q.Category, ct);
            if (cat == null) return [];
            url += $"&categoryId={cat}";
        }
        var res = await Http.GetJsonAsync(url, ct, CurseForgeApi.Auth);
        return res.Arr("data").Select(m =>
        {
            var idx = m.Arr("latestFilesIndexes");
            var loaders = idx.Select(i => i.Int("modLoader") switch { 1 => "Forge", 4 => "Fabric", 5 => "Quilt", 6 => "NeoForge", _ => null })
                .Where(x => x != null).Cast<string>().Distinct().ToList();
            return new ModpackSummary
            {
                Source = ContentSource.CurseForge,
                Id = m.Long("id").ToString(),
                Slug = m.Str("slug") ?? "",
                Title = m.Str("name") ?? "",
                Description = m.Str("summary") ?? "",
                Author = m.Arr("authors").FirstOrDefault().Str("name") ?? "",
                IconUrl = m.Obj("logo")?.Str("thumbnailUrl"),
                Downloads = m.Long("downloadCount"),
                WebUrl = m.Obj("links")?.Str("websiteUrl"),
                Created = ParseDate(m.Str("dateCreated")),
                Updated = ParseDate(m.Str("dateReleased") ?? m.Str("dateModified")),
                GameVersions = SortVersions(idx.Select(i => i.Str("gameVersion") ?? "").Where(v => v.Length > 0).Distinct()),
                Loaders = loaders,
                Categories = m.Arr("categories").Select(c => c.Str("name") ?? "").Where(c => c.Length > 0).ToList(),
                ImageUrl = m.Arr("screenshots").FirstOrDefault().Str("url"),
            };
        }).ToList();
    }

    private static DateTime ParseDate(string? s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : default;

    private static List<string> SortVersions(IEnumerable<string> versions) =>
        versions.Distinct().OrderBy(v => Version.TryParse(v, out var p) ? p : new Version(0, 0)).ToList();

    private static List<string>? _categories;

    /// <summary>Modrinth modpack category slugs (also matched to CurseForge categories by name).</summary>
    public static async Task<List<string>> GetCategoriesAsync(CancellationToken ct)
    {
        if (_categories != null) return _categories;
        var arr = await Http.GetJsonAsync($"{Modrinth}/tag/category", ct) as JsonArray ?? [];
        _categories = arr.Where(c => c.Str("project_type") == "modpack" && c.Str("header") == "categories")
            .Select(c => c.Str("name") ?? "").Where(n => n.Length > 0).Distinct().OrderBy(n => n).ToList();
        return _categories;
    }

    public static Task<List<ProjectVersion>> GetVersionsAsync(ContentSource source, string id, CancellationToken ct) =>
        source == ContentSource.Modrinth
            ? ModrinthApi.GetVersionsAsync(id, ContentType.ResourcePack, null, ModLoader.Vanilla, ct)
            : CurseForgeApi.GetVersionsAsync(id, ContentType.ResourcePack, null, ModLoader.Vanilla, ct);

    /// <summary>Downloads the pack file (.mrpack / CurseForge zip) into the cache.</summary>
    public static async Task<string> DownloadPackAsync(ProjectVersion v, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(v.Url))
            throw new InvalidOperationException(L.T("The author of this modpack doesn't allow downloads from other launchers. Open it on the website to download it."));
        var ext = v.Source == ContentSource.Modrinth ? ".mrpack" : ".zip";
        var dir = Path.Combine(AppPaths.Cache, "modpacks");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{v.Source}-{v.ProjectId}-{v.Id}{ext}");
        progress.Report(new TaskProgress(L.F("Downloading {0}…", v.FileName)));
        await Downloader.DownloadOneAsync(new DownloadItem(v.Url, path, v.Sha1), ct);
        return path;
    }

    /// <summary>Creates a new instance from a pack version.</summary>
    public static async Task<ImportResult> InstallAsync(ProjectDetails p, ProjectVersion v, string name, string? category,
        IProgress<TaskProgress> progress, CancellationToken ct)
    {
        string path;
        using (var job = DownloadManager.Begin(L.F("Downloading {0}", name)))
        {
            try { path = await DownloadPackAsync(v, progress, CancellationTokenSource.CreateLinkedTokenSource(ct, job.Token).Token); }
            catch { job.Fail(); throw; }
        }
        var source = new ModpackSource
        {
            Source = p.Source, ProjectId = p.Id, VersionId = v.Id, VersionNumber = v.VersionNumber,
            Title = p.Title, IconUrl = p.IconUrl,
        };
        return await ModpackImporter.ImportAsync(path, name, category, progress, ct, source);
    }

    /// <summary>Switches a modpack instance to another version of its pack.</summary>
    public static async Task<List<ManualDownload>> UpdateAsync(Instance inst, ProjectVersion v, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        string path;
        using (var job = DownloadManager.Begin(L.F("Downloading {0}", inst.Name)))
        {
            try { path = await DownloadPackAsync(v, progress, CancellationTokenSource.CreateLinkedTokenSource(ct, job.Token).Token); }
            catch { job.Fail(); throw; }
        }
        return await ModpackImporter.UpdateAsync(inst, path, v, progress, ct);
    }
}
