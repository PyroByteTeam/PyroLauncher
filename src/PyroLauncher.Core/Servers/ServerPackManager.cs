using System.Collections.ObjectModel;
using System.IO;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.Servers;

public static class ServerPackManager
{
    public static ObservableCollection<ServerPack> Packs { get; } = [];

    /// <summary>Raised when packs are added or removed, or links to instances change.</summary>
    public static event Action? Changed;
    public static void NotifyChanged() => Changed?.Invoke();

    public static void LoadAll()
    {
        Packs.Clear();
        Directory.CreateDirectory(AppPaths.ServerPacks);
        foreach (var dir in Directory.GetDirectories(AppPaths.ServerPacks))
        {
            var cfg = JsonUtil.Load<ServerPackConfig>(Path.Combine(dir, "server.json"));
            var inst = JsonUtil.Load<InstanceConfig>(Path.Combine(dir, "instance.json"));
            if (cfg == null || inst == null) continue;
            inst.Overrides ??= new GameSettings();
            cfg.LinkedInstances ??= [];
            cfg.LaunchArgs ??= [];
            Packs.Add(new ServerPack(dir, cfg, inst));
        }
        NotifyChanged();
    }

    public static ServerPack Create(string name, string mc, ModLoader loader, string? loaderVersion, Instance? baseInstance, ModpackSource? modpack = null)
    {
        var baseName = AppPaths.SanitizeFileName(name);
        var dirName = baseName;
        for (int i = 2; Directory.Exists(Path.Combine(AppPaths.ServerPacks, dirName)); i++) dirName = $"{baseName} ({i})";
        var dir = Path.Combine(AppPaths.ServerPacks, dirName);
        Directory.CreateDirectory(Path.Combine(dir, "server", "mods"));

        var instCfg = new InstanceConfig
        {
            Name = name.Trim(),
            MinecraftVersion = mc,
            Loader = loader,
            LoaderVersion = loader == ModLoader.Vanilla ? null : loaderVersion,
            Created = DateTime.Now,
            Modpack = modpack,
        };
        var cfg = new ServerPackConfig { BaseInstanceId = baseInstance?.Id };
        if (baseInstance != null) cfg.LinkedInstances.Add(baseInstance.Id);
        var pack = new ServerPack(dir, cfg, instCfg);
        if (baseInstance != null && File.Exists(baseInstance.IconPath))
        {
            try { File.Copy(baseInstance.IconPath, pack.Instance.IconPath, true); pack.Instance.LoadIcon(); } catch { }
        }
        pack.Save();
        Packs.Add(pack);
        NotifyChanged();
        return pack;
    }

    /// <summary>Saves and stops every running server (used when the launcher exits).</summary>
    public static void StopAll()
    {
        var running = Packs.Where(p => p.IsRunning).ToList();
        foreach (var p in running) p.SendCommand("stop");
        var deadline = DateTime.Now.AddSeconds(20);
        foreach (var p in running)
            while (p.IsProcessAlive && DateTime.Now < deadline) Thread.Sleep(100);
        foreach (var p in running) p.Kill();
    }

    public static void Delete(ServerPack pack)
    {
        pack.Kill();
        Directory.Delete(pack.Dir, true);
        Packs.Remove(pack);
        NotifyChanged();
    }

    public static IEnumerable<ServerPack> LinkedTo(Instance inst) => Packs.Where(p => p.Config.LinkedInstances.Contains(inst.Id));

    public static void SetLinked(ServerPack pack, Instance inst, bool linked)
    {
        var list = pack.Config.LinkedInstances;
        if (linked == list.Contains(inst.Id)) return;
        if (linked) list.Add(inst.Id); else list.Remove(inst.Id);
        pack.Save();
        NotifyChanged();
    }

    /// <summary>Copies mods (and their Modrinth/CurseForge identity, so updates work) from an instance into a server pack.</summary>
    public static void CopyMods(Instance from, ServerPack to, IEnumerable<LocalItem> mods)
    {
        var src = ContentIndex.Load(from);
        var dest = Path.Combine(to.ServerDir, "mods");
        Directory.CreateDirectory(dest);
        ContentIndex.Update(to.Instance, idx =>
        {
            foreach (var m in mods)
            {
                var target = Path.Combine(dest, m.BaseName);
                File.Copy(m.FilePath, target, true);
                if (src.Files.TryGetValue(m.IndexKey, out var r)) idx.Files[m.IndexKey] = r;
            }
        });
    }

    /// <summary>Folders a server usually needs from the client instance besides mods.</summary>
    public static readonly string[] ConfigFolders = ["config", "defaultconfigs", "kubejs", "scripts", "global_packs", "datapacks"];

    public static void CopyConfigs(Instance from, ServerPack to)
    {
        foreach (var name in ConfigFolders)
        {
            var src = Path.Combine(from.GameDir, name);
            if (Directory.Exists(src)) CopyDir(src, Path.Combine(to.ServerDir, name));
        }
    }

    public static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, true);
        }
    }
}
