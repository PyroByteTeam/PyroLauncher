using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.Content;

/// <summary>A project (mod, resource pack or shader) saved in a preset.</summary>
public class PresetItem
{
    public ContentType Type { get; set; }
    public ContentSource Source { get; set; }
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? IconUrl { get; set; }
    public string? Author { get; set; }
}

/// <summary>A reusable list of mods / resource packs / shaders that can be applied to any instance.</summary>
public class ContentPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<PresetItem> Items { get; set; } = [];
    public override string ToString() => Name;
}

public enum PresetItemState { Checking, Available, AlreadyInstalled, Unavailable, Failed }

/// <summary>Finds, for each preset item, the newest version that fits an instance.</summary>
public static class PresetService
{
    public static List<ContentPreset> All => SettingsService.Current.Presets;

    public static void Save() => SettingsService.Save();

    /// <summary>True when the instance already has this project (matched by the content index).</summary>
    public static bool IsInstalled(Instance inst, PresetItem item)
    {
        if (ContentStore.Get(inst, item.Type).Items.Any(i => i.Ref is { } r && r.Source == item.Source && r.ProjectId == item.ProjectId))
            return true;
        var idx = ContentIndex.Load(inst);
        return idx.Files.Any(kv => kv.Key.StartsWith(item.Type.Folder() + "/", StringComparison.OrdinalIgnoreCase)
                                   && kv.Value.Source == item.Source && kv.Value.ProjectId == item.ProjectId);
    }

    /// <summary>Newest version compatible with the instance's Minecraft version (and loader for mods), or null.</summary>
    public static async Task<ProjectVersion?> ResolveAsync(Instance inst, PresetItem item, CancellationToken ct)
    {
        var versions = await ContentService.GetVersionsAsync(item.Source, item.ProjectId, item.Type, inst, true, ct);
        return versions
            .Where(v => item.Type != ContentType.Mod || inst.Config.Loader == ModLoader.Vanilla
                        || v.Loaders.Count == 0 || v.Loaders.Any(l => l.Equals(inst.Config.Loader.ToString(), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(v => v.ReleaseType == "release")
            .ThenByDescending(v => v.Date)
            .FirstOrDefault();
    }
}
