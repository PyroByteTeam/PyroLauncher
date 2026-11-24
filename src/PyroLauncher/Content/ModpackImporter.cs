using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;

namespace PyroLauncher.Content;

public record ModpackInfo(string Format, string Name, string MinecraftVersion, ModLoader Loader, string? LoaderVersion, string? Version);

public record ImportResult(Instance Instance, List<string> SkippedFiles);

public static class ModpackImporter
{
    public static ModpackInfo Inspect(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.GetEntry("modrinth.index.json") is { } mr)
        {
            var j = Read(mr);
            var deps = j.Obj("dependencies");
            var mc = deps.Str("minecraft") ?? throw new InvalidDataException("mrpack has no minecraft dependency");
            var (loader, ver) =
                deps.Str("fabric-loader") is { } f ? (ModLoader.Fabric, f) :
                deps.Str("neoforge") is { } n ? (ModLoader.NeoForge, n) :
                deps.Str("forge") is { } fo ? (ModLoader.Forge, fo) :
                deps.Str("quilt-loader") is not null ? throw new NotSupportedException("Quilt modpacks are not supported.") :
                (ModLoader.Vanilla, (string?)null);
            return new ModpackInfo("Modrinth", j.Str("name") ?? Path.GetFileNameWithoutExtension(path), mc, loader, ver, j.Str("versionId"));
        }
        if (zip.GetEntry("manifest.json") is { } cf)
        {
            var j = Read(cf);
            var mcNode = j.Obj("minecraft");
            var mc = mcNode.Str("version") ?? throw new InvalidDataException("manifest.json has no minecraft version");
            var primary = mcNode.Arr("modLoaders").FirstOrDefault(l => l.Bool("primary")) ?? mcNode.Arr("modLoaders").FirstOrDefault();
            var id = primary.Str("id") ?? "";
            var dash = id.IndexOf('-');
            var kind = dash > 0 ? id[..dash] : id;
            var ver = dash > 0 ? id[(dash + 1)..] : null;
            var loader = kind.ToLowerInvariant() switch
            {
                "forge" => ModLoader.Forge,
                "fabric" => ModLoader.Fabric,
                "neoforge" => ModLoader.NeoForge,
                "" => ModLoader.Vanilla,
                _ => throw new NotSupportedException($"Mod loader '{kind}' is not supported."),
            };
            return new ModpackInfo("CurseForge", j.Str("name") ?? Path.GetFileNameWithoutExtension(path), mc, loader, ver, j.Str("version"));
        }
        throw new InvalidDataException("This file is not a Modrinth (.mrpack) or CurseForge (.zip) modpack.");
    }

    public static async Task<ImportResult> ImportAsync(string path, string name, string? category, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        var info = Inspect(path);
        using var job = DownloadManager.Begin($"Importing {name}");
        var loaderVersion = info.LoaderVersion == null ? null
            : await LoaderService.NormalizeLoaderVersionAsync(info.Loader, info.MinecraftVersion, info.LoaderVersion, ct);

        var inst = InstanceManager.Create(name, info.MinecraftVersion, info.Loader, loaderVersion, category);
        try
        {
            var skipped = info.Format == "Modrinth"
                ? await ImportModrinthAsync(path, inst, progress, ct)
                : await ImportCurseForgeAsync(path, inst, progress, ct);
            return new ImportResult(inst, skipped);
        }
        catch
        {
            job.Fail();
            try { InstanceManager.Delete(inst); } catch { }
            throw;
        }
    }

    private static async Task<List<string>> ImportModrinthAsync(string path, Instance inst, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        JsonNode index;
        using (var zip = ZipFile.OpenRead(path))
            index = Read(zip.GetEntry("modrinth.index.json")!);

        var items = new List<DownloadItem>();
        var refs = new Dictionary<string, string>(); // indexKey -> sha1
        foreach (var f in index.Arr("files"))
        {
            if (f.Obj("env")?.Str("client") == "unsupported") continue;
            var rel = f.Str("path");
            var url = f.Arr("downloads").FirstOrDefault().AsStr();
            if (rel == null || url == null) continue;
            var dest = SafeCombine(inst.GameDir, rel);
            var sha1 = f.Obj("hashes").Str("sha1");
            items.Add(new DownloadItem(url, dest, sha1, f.Long("fileSize", -1),
                f.Arr("downloads").Skip(1).Select(x => x.AsStr()).Where(x => x != null).Cast<string>().ToArray()));
        }
        await Downloader.DownloadAllAsync(items, progress, "modpack files", ct);

        progress.Report(new TaskProgress("Extracting overrides…"));
        await Task.Run(() => ExtractOverrides(path, inst.GameDir, ["overrides/", "client-overrides/"]), ct);
        return [];
    }

    private static async Task<List<string>> ImportCurseForgeAsync(string path, Instance inst, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        JsonNode manifest;
        using (var zip = ZipFile.OpenRead(path))
            manifest = Read(zip.GetEntry("manifest.json")!);

        var skipped = new List<string>();
        var fileIds = manifest.Arr("files").Select(f => f.Long("fileID")).Where(id => id > 0).ToList();
        if (fileIds.Count > 0)
        {
            if (!CurseForgeApi.HasKey)
                throw new InvalidOperationException("Importing CurseForge modpacks requires a CurseForge API key (Settings → Launcher).");

            progress.Report(new TaskProgress("Resolving CurseForge files…"));
            var files = await CurseForgeApi.GetFilesAsync(fileIds, ct);
            var mods = await CurseForgeApi.GetModsBriefAsync(files.Select(f => long.Parse(f.ProjectId)), ct);

            var items = new List<DownloadItem>();
            var refs = new List<(string key, ContentRef r)>();
            foreach (var f in files)
            {
                mods.TryGetValue(f.ProjectId, out var m);
                var folder = m.classId switch { 12 => "resourcepacks", 6552 => "shaderpacks", _ => "mods" };
                if (string.IsNullOrEmpty(f.Url))
                {
                    skipped.Add($"{m.title ?? f.FileName} — {m.url ?? "https://www.curseforge.com"}");
                    continue;
                }
                items.Add(new DownloadItem(f.Url, Path.Combine(inst.GameDir, folder, f.FileName), f.Sha1));
                refs.Add(($"{folder}/{f.FileName}", new ContentRef
                {
                    Source = ContentSource.CurseForge, ProjectId = f.ProjectId, VersionId = f.Id,
                    Title = m.title, IconUrl = m.icon, VersionNumber = f.Name,
                }));
            }
            await Downloader.DownloadAllAsync(items, progress, "mods", ct);
            ContentIndex.Update(inst, idx => { foreach (var (k, r) in refs) idx.Files[k] = r; });
        }

        progress.Report(new TaskProgress("Extracting overrides…"));
        var overrides = (manifest.Str("overrides") ?? "overrides").TrimEnd('/') + "/";
        await Task.Run(() => ExtractOverrides(path, inst.GameDir, [overrides]), ct);
        return skipped;
    }

    private static void ExtractOverrides(string zipPath, string gameDir, string[] prefixes)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var prefix in prefixes)
        {
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = e.FullName[prefix.Length..];
                if (rel.Length == 0) continue;
                var dest = SafeCombine(gameDir, rel);
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
            }
        }
    }

    /// <summary>Prevents zip-slip: the resulting path must stay inside <paramref name="root"/>.</summary>
    private static string SafeCombine(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Modpack contains an unsafe path: {rel}");
        return full;
    }

    private static JsonNode Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        return JsonNode.Parse(s) ?? throw new InvalidDataException($"{e.FullName} is empty");
    }
}
