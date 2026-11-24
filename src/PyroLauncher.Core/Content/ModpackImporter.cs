using PyroLauncher.UI;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;

namespace PyroLauncher.Content;

public record ModpackInfo(string Format, string Name, string MinecraftVersion, ModLoader Loader, string? LoaderVersion, string? Version);

/// <summary>A CurseForge file whose author disabled third-party downloads; the user has to fetch it in a browser.</summary>
public record ManualDownload(string Title, string FileName, string Url, string Folder);

public record ImportResult(Instance Instance, List<ManualDownload> SkippedFiles);

/// <summary>What installing a pack into a game directory produced.</summary>
public record PackFilesResult(List<string> Files, List<ManualDownload> Skipped);

public static class ModpackImporter
{
    /// <summary>Files a pack update never overwrites when they already exist (the player's own settings).</summary>
    private static readonly HashSet<string> KeepOnUpdate = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat",
    };

    public static ModpackInfo Inspect(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.GetEntry(ModpackExporter.PyroManifest) is { } pyro)
        {
            var j = Read(pyro);
            var loader = Enum.TryParse<ModLoader>(j.Str("loader"), true, out var l) ? l : ModLoader.Vanilla;
            return new ModpackInfo("Pyro", j.Str("name") ?? Path.GetFileNameWithoutExtension(path), j.Str("minecraft") ?? throw new InvalidDataException(L.T("Pack has no Minecraft version")),
                loader, j.Str("loaderVersion"), j.Str("version"));
        }
        if (zip.GetEntry("modrinth.index.json") is { } mr)
        {
            var j = Read(mr);
            var deps = j.Obj("dependencies");
            var mc = deps.Str("minecraft") ?? throw new InvalidDataException(L.T("mrpack has no minecraft dependency"));
            var (loader, ver) =
                deps.Str("fabric-loader") is { } f ? (ModLoader.Fabric, f) :
                deps.Str("neoforge") is { } n ? (ModLoader.NeoForge, n) :
                deps.Str("forge") is { } fo ? (ModLoader.Forge, fo) :
                deps.Str("quilt-loader") is not null ? throw new NotSupportedException(UI.L.T("Quilt modpacks are not supported.")) :
                (ModLoader.Vanilla, (string?)null);
            return new ModpackInfo("Modrinth", j.Str("name") ?? Path.GetFileNameWithoutExtension(path), mc, loader, ver, j.Str("versionId"));
        }
        if (zip.GetEntry("manifest.json") is { } cf)
        {
            var j = Read(cf);
            var mcNode = j.Obj("minecraft");
            var mc = mcNode.Str("version") ?? throw new InvalidDataException(L.T("manifest.json has no minecraft version"));
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
                _ => throw new NotSupportedException(UI.L.F("Mod loader '{0}' is not supported.", kind)),
            };
            return new ModpackInfo("CurseForge", j.Str("name") ?? Path.GetFileNameWithoutExtension(path), mc, loader, ver, j.Str("version"));
        }
        throw new InvalidDataException(UI.L.T("This file is not a Modrinth (.mrpack) or CurseForge (.zip) modpack."));
    }

    public static async Task<ImportResult> ImportAsync(string path, string name, string? category, IProgress<TaskProgress> progress, CancellationToken ct,
        ModpackSource? source = null)
    {
        var info = Inspect(path);
        using var job = DownloadManager.Begin(UI.L.F("Importing {0}", name));
        ct = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Token).Token;
        var loaderVersion = info.LoaderVersion == null ? null
            : await LoaderService.NormalizeLoaderVersionAsync(info.Loader, info.MinecraftVersion, info.LoaderVersion, ct);

        var inst = InstanceManager.Create(name, info.MinecraftVersion, info.Loader, loaderVersion, category);
        // The instance shows up right away; keep it busy so it can't be played (or deleted) until the pack is fully installed.
        var instCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = instCts.Token;
        inst.IsBusy = true;
        inst.LaunchCts = instCts;
        inst.Status = UI.L.T("Installing modpack…");
        var outer = progress;
        progress = new Progress<TaskProgress>(p => { outer.Report(p); inst.Status = p.Message; inst.Progress = p.Fraction; });
        try
        {
            var result = await InstallFilesAsync(path, info, inst, false, progress, ct);
            source ??= await IdentifyAsync(path, ct);
            if (source != null)
            {
                source.Files = result.Files;
                source.VersionNumber ??= info.Version;
                inst.Config.Modpack = source;
                inst.Save();
                await TrySetIconAsync(inst, source.IconUrl);
            }
            return new ImportResult(inst, result.Skipped);
        }
        catch
        {
            job.Fail();
            try { InstanceManager.Delete(inst); } catch { }
            throw;
        }
        finally
        {
            inst.IsBusy = false;
            inst.LaunchCts = null;
            inst.Status = null;
            inst.Progress = -1;
        }
    }

    /// <summary>Replaces the instance's pack files with another version of the same pack. Worlds, screenshots and files the player added stay.</summary>
    public static async Task<List<ManualDownload>> UpdateAsync(Instance inst, string path, ProjectVersion version, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        var pack = inst.Config.Modpack ?? throw new InvalidOperationException(L.T("Not a modpack instance"));
        var info = Inspect(path);
        using var job = DownloadManager.Begin(UI.L.F("Updating {0}", inst.Name));
        ct = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Token).Token;
        try
        {
            var loaderVersion = info.LoaderVersion == null ? null
                : await LoaderService.NormalizeLoaderVersionAsync(info.Loader, info.MinecraftVersion, info.LoaderVersion, ct);

            progress.Report(new TaskProgress(UI.L.T("Removing files of the old version…")));
            foreach (var rel in pack.Files)
            {
                if (KeepOnUpdate.Contains(Path.GetFileName(rel))) continue;
                try
                {
                    var full = SafeCombine(inst.GameDir, rel);
                    if (File.Exists(full)) File.Delete(full);
                    // Disabled copies too.
                    if (File.Exists(full + ".disabled")) File.Delete(full + ".disabled");
                }
                catch (Exception ex) { Log.Warn($"Could not remove {rel}: {ex.Message}"); }
            }

            var result = await InstallFilesAsync(path, info, inst, true, progress, ct);
            var cfg = inst.Config;
            cfg.MinecraftVersion = info.MinecraftVersion;
            cfg.Loader = info.Loader;
            cfg.LoaderVersion = info.Loader == ModLoader.Vanilla ? null : loaderVersion;
            pack.Files = result.Files;
            pack.VersionId = version.Id;
            pack.VersionNumber = version.VersionNumber;
            inst.Save();
            inst.Refresh();
            return result.Skipped;
        }
        catch { job.Fail(); throw; }
    }

    private static Task<PackFilesResult> InstallFilesAsync(string path, ModpackInfo info, Instance inst, bool update, IProgress<TaskProgress> progress, CancellationToken ct) =>
        info.Format switch
        {
            "Modrinth" => InstallModrinthAsync(path, inst, update, progress, ct),
            "Pyro" => InstallPyroAsync(path, inst, update, progress, ct),
            _ => InstallCurseForgeAsync(path, inst, update, progress, ct),
        };

    /// <summary>Pyro packs bundle every file under minecraft/ plus the instance's game settings.</summary>
    private static async Task<PackFilesResult> InstallPyroAsync(string path, Instance inst, bool update, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        progress.Report(new TaskProgress(UI.L.T("Extracting files…")));
        var files = await Task.Run(() => ExtractOverrides(path, inst.GameDir, ["minecraft/"], update), ct);
        using var zip = ZipFile.OpenRead(path);
        var j = Read(zip.GetEntry(ModpackExporter.PyroManifest)!);
        if (!update && j.Get("overrides") is JsonObject o)
        {
            try { inst.Config.Overrides = o.Deserialize<GameSettings>(JsonUtil.Options) ?? new GameSettings(); inst.Save(); }
            catch (Exception ex) { Log.Warn($"Pack settings ignored: {ex.Message}"); }
        }
        if (!update && zip.GetEntry("icon.png") is { } icon)
        {
            icon.ExtractToFile(inst.IconPath, true);
            inst.LoadIcon();
        }
        return new PackFilesResult(files, []);
    }

    private static async Task<PackFilesResult> InstallModrinthAsync(string path, Instance inst, bool update, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        JsonNode index;
        using (var zip = ZipFile.OpenRead(path))
            index = Read(zip.GetEntry("modrinth.index.json")!);

        var items = new List<DownloadItem>();
        var files = new List<string>();
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
            files.Add(Norm(rel));
        }
        await Downloader.DownloadAllAsync(items, progress, UI.L.T("modpack files"), ct);

        progress.Report(new TaskProgress(UI.L.T("Extracting overrides…")));
        files.AddRange(await Task.Run(() => ExtractOverrides(path, inst.GameDir, ["overrides/", "client-overrides/"], update), ct));
        return new PackFilesResult(files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), []);
    }

    private static async Task<PackFilesResult> InstallCurseForgeAsync(string path, Instance inst, bool update, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        JsonNode manifest;
        using (var zip = ZipFile.OpenRead(path))
            manifest = Read(zip.GetEntry("manifest.json")!);

        var skipped = new List<ManualDownload>();
        var files = new List<string>();
        var fileIds = manifest.Arr("files").Select(f => f.Long("fileID")).Where(id => id > 0).ToList();
        if (fileIds.Count > 0)
        {
            if (!CurseForgeApi.HasKey)
                throw new InvalidOperationException(UI.L.T("Importing CurseForge modpacks requires a CurseForge API key (Settings → Launcher)."));

            progress.Report(new TaskProgress(UI.L.T("Resolving CurseForge files…")));
            var resolved = await CurseForgeApi.GetFilesAsync(fileIds, ct);
            var mods = await CurseForgeApi.GetModsBriefAsync(resolved.Select(f => long.Parse(f.ProjectId)), ct);

            var items = new List<DownloadItem>();
            var refs = new List<(string key, ContentRef r)>();
            foreach (var f in resolved)
            {
                mods.TryGetValue(f.ProjectId, out var m);
                var folder = m.classId switch { 12 => "resourcepacks", 6552 => "shaderpacks", _ => "mods" };
                files.Add($"{folder}/{f.FileName}");
                if (string.IsNullOrEmpty(f.Url))
                {
                    var page = m.url is { Length: > 0 } u ? $"{u.TrimEnd('/')}/download/{f.Id}" : $"https://www.curseforge.com/api/v1/mods/{f.ProjectId}/files/{f.Id}/download";
                    skipped.Add(new ManualDownload(m.title ?? f.FileName, f.FileName, page, folder));
                    continue;
                }
                items.Add(new DownloadItem(f.Url, Path.Combine(inst.GameDir, folder, f.FileName), f.Sha1));
                refs.Add(($"{folder}/{f.FileName}", new ContentRef
                {
                    Source = ContentSource.CurseForge, ProjectId = f.ProjectId, VersionId = f.Id,
                    Title = m.title, IconUrl = m.icon, VersionNumber = f.Name,
                }));
            }
            await Downloader.DownloadAllAsync(items, progress, UI.L.T("mods"), ct);
            ContentIndex.Update(inst, idx => { foreach (var (k, r) in refs) idx.Files[k] = r; });
        }

        progress.Report(new TaskProgress(UI.L.T("Extracting overrides…")));
        var overrides = (manifest.Str("overrides") ?? "overrides").TrimEnd('/') + "/";
        files.AddRange(await Task.Run(() => ExtractOverrides(path, inst.GameDir, [overrides], update), ct));
        return new PackFilesResult(files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }

    /// <returns>Relative paths of the extracted files.</returns>
    private static List<string> ExtractOverrides(string zipPath, string gameDir, string[] prefixes, bool update)
    {
        var written = new List<string>();
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
                if (update && File.Exists(dest) && KeepOnUpdate.Contains(Path.GetFileName(dest))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
                written.Add(Norm(rel));
            }
        }
        return written;
    }

    /// <summary>Finds the online project a pack file belongs to (by hash), so imported packs get a modpack page and updates.</summary>
    public static async Task<ModpackSource?> IdentifyAsync(string path, CancellationToken ct)
    {
        try
        {
            if (path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            {
                var sha1 = Hashing.Sha1File(path);
                var hits = await ModrinthApi.IdentifyAsync([sha1], ct);
                if (hits.TryGetValue(sha1, out var v))
                {
                    var brief = await ModrinthApi.GetProjectsBriefAsync([v.ProjectId], ct);
                    brief.TryGetValue(v.ProjectId, out var b);
                    return new ModpackSource
                    {
                        Source = ContentSource.Modrinth, ProjectId = v.ProjectId, VersionId = v.Id, VersionNumber = v.VersionNumber,
                        Title = b.title ?? "", IconUrl = b.icon,
                    };
                }
            }
            else if (CurseForgeApi.HasKey)
            {
                var fp = Hashing.CurseForgeFingerprint(path);
                var hits = await CurseForgeApi.IdentifyAsync([fp], ct);
                if (hits.TryGetValue(fp, out var v))
                {
                    var brief = await CurseForgeApi.GetModsBriefAsync([long.Parse(v.ProjectId)], ct);
                    brief.TryGetValue(v.ProjectId, out var b);
                    if (b.classId is not 4471) return null;
                    return new ModpackSource
                    {
                        Source = ContentSource.CurseForge, ProjectId = v.ProjectId, VersionId = v.Id, VersionNumber = v.VersionNumber,
                        Title = b.title ?? "", IconUrl = b.icon,
                    };
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Modpack identify failed: {ex.Message}"); }
        return null;
    }

    public static async Task TrySetIconAsync(Instance inst, string? url)
    {
        if (string.IsNullOrEmpty(url) || File.Exists(inst.IconPath)) return;
        try
        {
            var bytes = await Http.GetBytesAsync(url, CancellationToken.None);
            await File.WriteAllBytesAsync(inst.IconPath, bytes);
            inst.LoadIcon();
        }
        catch (Exception ex) { Log.Warn($"Modpack icon download failed: {ex.Message}"); }
    }

    private static string Norm(string rel) => rel.Replace('\\', '/').TrimStart('/');

    /// <summary>Prevents zip-slip: the resulting path must stay inside <paramref name="root"/>.</summary>
    private static string SafeCombine(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.F("Modpack contains an unsafe path: {0}", rel));
        return full;
    }

    private static JsonNode Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        return JsonNode.Parse(s) ?? throw new InvalidDataException(L.F("{0} is empty", e.FullName));
    }
}
