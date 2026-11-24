using PyroLauncher.UI;
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
        ContentType.Mod => L.T("Mods"),
        ContentType.ResourcePack => L.T("Resource Packs"),
        _ => L.T("Shader Packs"),
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

public partial class ProjectSummary : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
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

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isSelected;
    public string Initials => new string(Title.Where(char.IsLetterOrDigit).Take(2).ToArray()) is { Length: > 0 } s ? s : "?";
    public string SourceText => Source == ContentSource.Modrinth ? "Modrinth" : "CurseForge";
    public string DownloadsText => Downloads switch
    {
        >= 1_000_000 => L.F("{0:0.#}M", Downloads / 1_000_000.0),
        >= 1_000 => L.F("{0:0.#}K", Downloads / 1_000.0),
        _ => Downloads.ToString(),
    };
}

public record GalleryImage(string Url, string? Title, string? ThumbUrl);

public class ProjectDetails : ProjectSummary
{
    public string BodyHtml { get; set; } = "";
    public List<GalleryImage> Gallery { get; set; } = [];
    public string Side { get; set; } = L.T("Unknown");
    public List<string> Loaders { get; set; } = [];
    public string License { get; set; } = L.T("Unknown");
    public string? LicenseUrl { get; set; }
    public List<string> Categories { get; set; } = [];

    public string LoadersText => Loaders.Count == 0 ? "-" : string.Join(", ", Loaders);
    public string CategoriesText => Categories.Count == 0 ? "-" : string.Join(", ", Categories.Select(ModrinthApi.Pretty));
}

/// <summary>Kind: required, optional, incompatible or embedded.</summary>
public record Dependency(string ProjectId, string? VersionId, bool Required, string Kind = "required");

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
    /// <summary>Non-primary files of the version (Modrinth), e.g. a separate server pack.</summary>
    public List<GalleryImage> ExtraFiles { get; set; } = [];
    /// <summary>CurseForge: id of the server pack file that belongs to this modpack file (0 = none).</summary>
    public long ServerPackFileId { get; set; }

    public string DateText => Date == default ? "" : Date.ToString("d MMM yyyy", UI.L.Culture);
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
