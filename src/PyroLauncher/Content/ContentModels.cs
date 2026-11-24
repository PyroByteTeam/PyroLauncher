using PyroLauncher.Instances;

namespace PyroLauncher.Content;

public enum ContentSource { Modrinth, CurseForge }

public enum ContentType { Mod, ResourcePack, ShaderPack }

public static class ContentTypeExt
{
    public static string Folder(this ContentType t) => t switch
    {
        ContentType.Mod => "mods",
        ContentType.ResourcePack => "resourcepacks",
        _ => "shaderpacks",
    };

    public static string Display(this ContentType t) => t switch
    {
        ContentType.Mod => "Mods",
        ContentType.ResourcePack => "Resource Packs",
        _ => "Shader Packs",
    };

    public static string ModrinthType(this ContentType t) => t switch
    {
        ContentType.Mod => "mod",
        ContentType.ResourcePack => "resourcepack",
        _ => "shader",
    };

    public static int CurseForgeClass(this ContentType t) => t switch
    {
        ContentType.Mod => 6,
        ContentType.ResourcePack => 12,
        _ => 6552,
    };

    public static string LoaderSlug(this ModLoader l) => l switch
    {
        ModLoader.Fabric => "fabric",
        ModLoader.Forge => "forge",
        ModLoader.NeoForge => "neoforge",
        _ => "minecraft",
    };
}

public enum SearchSort { Relevance, Downloads, Newest, Updated }

/// <summary>Extra search filters. Category is a Modrinth category slug; Environment is "client"/"server"/null.</summary>
public record SearchFilters(SearchSort Sort = SearchSort.Relevance, string? Category = null, string? Environment = null);

public class ProjectSummary
{
    public ContentSource Source { get; set; }
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string? IconUrl { get; set; }
    public long Downloads { get; set; }
    public string? WebUrl { get; set; }

    public string SourceText => Source == ContentSource.Modrinth ? "Modrinth" : "CurseForge";
    public string DownloadsText => Downloads switch
    {
        >= 1_000_000 => $"{Downloads / 1_000_000.0:0.#}M",
        >= 1_000 => $"{Downloads / 1_000.0:0.#}K",
        _ => Downloads.ToString(),
    };
}

public record GalleryImage(string Url, string? Title, string? ThumbUrl);

public class ProjectDetails : ProjectSummary
{
    public string BodyHtml { get; set; } = "";
    public List<GalleryImage> Gallery { get; set; } = [];
    public string Side { get; set; } = "Unknown";
    public List<string> Loaders { get; set; } = [];
    public string License { get; set; } = "Unknown";
    public string? LicenseUrl { get; set; }
    public List<string> Categories { get; set; } = [];

    public string LoadersText => Loaders.Count == 0 ? "—" : string.Join(", ", Loaders);
}

public record Dependency(string ProjectId, string? VersionId, bool Required);

public class ProjectVersion
{
    public ContentSource Source { get; set; }
    public string ProjectId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public DateTime Date { get; set; }
    public string ReleaseType { get; set; } = "release";
    public string? ChangelogHtml { get; set; }
    public string FileName { get; set; } = "";
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public List<string> GameVersions { get; set; } = [];
    public List<string> Loaders { get; set; } = [];
    public List<Dependency> Dependencies { get; set; } = [];

    public string DateText => Date == default ? "" : Date.ToString("d MMM yyyy");
    public string GameVersionsText => GameVersions.Count <= 4 ? string.Join(", ", GameVersions) : $"{string.Join(", ", GameVersions.Take(3))} +{GameVersions.Count - 3}";
    public string LoadersText => string.Join(", ", Loaders);
}

/// <summary>Remembers which online project/version a local file came from.</summary>
public class ContentRef
{
    public ContentSource Source { get; set; }
    public string ProjectId { get; set; } = "";
    public string? VersionId { get; set; }
    public string? Title { get; set; }
    public string? IconUrl { get; set; }
    public string? VersionNumber { get; set; }
}
