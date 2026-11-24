using System.Collections.ObjectModel;
using System.IO;
using PyroLauncher.Core;

namespace PyroLauncher.Instances;

public static class InstanceManager
{
    public static ObservableCollection<Instance> Instances { get; } = [];

    /// <summary>Raised when instances are added/removed or their category changes.</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    public static void LoadAll()
    {
        Instances.Clear();
        Directory.CreateDirectory(AppPaths.Instances);
        foreach (var dir in Directory.GetDirectories(AppPaths.Instances))
        {
            var cfg = JsonUtil.Load<InstanceConfig>(Path.Combine(dir, "instance.json"));
            if (cfg == null) continue;
            cfg.Overrides ??= new GameSettings();
            var inst = new Instance(dir, cfg);
            // Instances launched before the Install button existed are already installed.
            if (cfg.PreparedKey == null && cfg.LastPlayed != null) cfg.PreparedKey = inst.CurrentKey;
            Instances.Add(inst);
        }
        NotifyChanged();
    }

    public static Instance Create(string name, string mcVersion, ModLoader loader, string? loaderVersion, string? category)
    {
        var baseName = AppPaths.SanitizeFileName(name);
        var dirName = baseName;
        for (int i = 2; Directory.Exists(Path.Combine(AppPaths.Instances, dirName)); i++)
            dirName = $"{baseName} ({i})";
        var dir = Path.Combine(AppPaths.Instances, dirName);
        Directory.CreateDirectory(dir);

        var cfg = new InstanceConfig
        {
            Name = name.Trim(),
            MinecraftVersion = mcVersion,
            Loader = loader,
            LoaderVersion = loader == ModLoader.Vanilla ? null : loaderVersion,
            Category = string.IsNullOrWhiteSpace(category) ? null : category,
            Created = DateTime.Now,
        };
        var inst = new Instance(dir, cfg);
        Directory.CreateDirectory(inst.GameDir);
        foreach (var sub in new[] { "mods", "resourcepacks", "shaderpacks", "screenshots" })
            Directory.CreateDirectory(Path.Combine(inst.GameDir, sub));
        inst.Save();
        Instances.Add(inst);
        NotifyChanged();
        return inst;
    }

    public static void Delete(Instance inst)
    {
        Directory.Delete(inst.Dir, true);
        // Keep the time played in the lifetime total even after the instance is gone.
        SettingsService.Current.DeletedPlaytimeSeconds += inst.Config.PlayTimeSeconds;
        SettingsService.Save();
        Instances.Remove(inst);
        NotifyChanged();
    }

    public static void SetCategory(Instance inst, string? category)
    {
        inst.Config.Category = string.IsNullOrWhiteSpace(category) ? null : category;
        inst.Save();
        inst.Refresh();
        NotifyChanged();
    }

    public static void RenameCategory(string oldName, string newName)
    {
        var cats = SettingsService.Current.Categories;
        var idx = cats.IndexOf(oldName);
        if (idx >= 0) cats[idx] = newName;
        SettingsService.Save();
        foreach (var i in Instances.Where(i => i.Config.Category == oldName))
        {
            i.Config.Category = newName;
            i.Save();
            i.Refresh();
        }
        NotifyChanged();
    }

    public static void DeleteCategory(string name)
    {
        SettingsService.Current.Categories.Remove(name);
        SettingsService.Save();
        foreach (var i in Instances.Where(i => i.Config.Category == name))
        {
            i.Config.Category = null;
            i.Save();
            i.Refresh();
        }
        NotifyChanged();
    }

    public static void AddCategory(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || SettingsService.Current.Categories.Contains(name)) return;
        SettingsService.Current.Categories.Add(name);
        SettingsService.Save();
        NotifyChanged();
    }
}
