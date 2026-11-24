using System.IO;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.Content;

public enum SourceFilter { All, Modrinth, CurseForge }

public static class ContentService
{
    public static async Task<(List<ProjectSummary> results, string? warning)> SearchAsync(
        string query, ContentType type, Instance inst, SourceFilter filter, int page, CancellationToken ct, SearchFilters? filters = null)
    {
        filters ??= new SearchFilters();
        var mc = inst.Config.MinecraftVersion;
        var loader = inst.Config.Loader;
        var tasks = new List<Task<List<ProjectSummary>>>();
        string? warning = null;

        if (filter != SourceFilter.CurseForge)
            tasks.Add(ModrinthApi.SearchAsync(query, type, mc, loader, page * 20, ct, filters));
        if (filter != SourceFilter.Modrinth)
        {
            if (!CurseForgeApi.HasKey) warning = "CurseForge results are off — add a CurseForge API key in Settings.";
            else if (filters.Environment != null) warning = "CurseForge can't filter by client/server side, so only Modrinth results are shown.";
            else
            {
                int? cat = null;
                bool skip = false;
                if (!string.IsNullOrEmpty(filters.Category))
                {
                    try { cat = await CurseForgeApi.MatchCategoryAsync(type, filters.Category, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"CF categories: {ex.Message}"); }
                    if (cat == null) { skip = true; warning = $"CurseForge has no \"{filters.Category}\" category, so only Modrinth results are shown."; }
                }
                if (!skip) tasks.Add(CurseForgeApi.SearchAsync(query, type, mc, loader, page * 20, ct, filters, cat));
            }
        }

        var lists = new List<List<ProjectSummary>>();
        foreach (var t in tasks)
        {
            try { lists.Add(await t); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Search failed: {ex.Message}");
                warning = ex.Message;
            }
        }

        // Interleave results so both sources are visible, skipping duplicates by title.
        var merged = new List<ProjectSummary>();
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; lists.Any(l => i < l.Count); i++)
            foreach (var l in lists)
                if (i < l.Count && titles.Add(l[i].Title)) merged.Add(l[i]);
        return (merged, warning);
    }

    public static Task<ProjectDetails> GetDetailsAsync(ContentSource source, string id, CancellationToken ct) =>
        source == ContentSource.Modrinth ? ModrinthApi.GetProjectAsync(id, ct) : CurseForgeApi.GetProjectAsync(id, ct);

    public static Task<List<ProjectVersion>> GetVersionsAsync(ContentSource source, string id, ContentType type, Instance inst, bool compatibleOnly, CancellationToken ct)
    {
        var mc = compatibleOnly ? inst.Config.MinecraftVersion : null;
        return source == ContentSource.Modrinth
            ? ModrinthApi.GetVersionsAsync(id, type, mc, inst.Config.Loader, ct)
            : CurseForgeApi.GetVersionsAsync(id, type, mc, inst.Config.Loader, ct);
    }

    public static async Task<string> GetChangelogAsync(ProjectVersion v, CancellationToken ct)
    {
        if (v.ChangelogHtml != null) return v.ChangelogHtml;
        if (v.Source == ContentSource.CurseForge)
            v.ChangelogHtml = await CurseForgeApi.GetChangelogAsync(v.ProjectId, v.Id, ct);
        return v.ChangelogHtml ?? "";
    }

    /// <summary>
    /// Downloads <paramref name="version"/> into the instance. If <paramref name="replace"/> is given that file is removed first
    /// (version switch). Required dependencies are installed too when missing.
    /// </summary>
    public static async Task<string> InstallAsync(Instance inst, ContentType type, ProjectVersion version, string? title, string? iconUrl,
        LocalItem? replace, IProgress<TaskProgress>? progress, CancellationToken ct, int depth = 0)
    {
        using var job = depth == 0 ? DownloadManager.Begin($"{title ?? version.FileName} {version.VersionNumber}") : null;
        if (string.IsNullOrEmpty(version.Url))
            throw new InvalidOperationException(
                $"The author of \"{title ?? version.FileName}\" disabled third-party downloads. Download it manually from CurseForge and drop it into the {type.Folder()} folder.");

        var folder = Path.Combine(inst.GameDir, type.Folder());
        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder, version.FileName);
        progress?.Report(new TaskProgress($"Downloading {version.FileName}…"));

        bool wasDisabled = replace != null && !replace.IsEnabled;
        await Downloader.DownloadOneAsync(new DownloadItem(version.Url, dest, version.Sha1), ct);

        if (replace != null && !string.Equals(replace.FilePath, dest, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (replace.IsDirectory) Directory.Delete(replace.FilePath, true);
                else File.Delete(replace.FilePath);
            }
            catch (Exception ex) { Log.Warn($"Could not remove old file: {ex.Message}"); }
            ContentIndex.Update(inst, idx => idx.Files.Remove(replace.IndexKey));
        }
        if (wasDisabled) File.Move(dest, dest + LocalItem.DisabledSuffix, true);

        ContentIndex.Update(inst, idx => idx.Files[$"{type.Folder()}/{version.FileName}"] = new ContentRef
        {
            Source = version.Source,
            ProjectId = version.ProjectId,
            VersionId = version.Id,
            Title = title,
            IconUrl = iconUrl,
            VersionNumber = version.VersionNumber,
        });

        if (type == ContentType.Mod && depth < 3)
            await InstallDependenciesAsync(inst, version, progress, ct, depth);
        return dest;
    }

    private static async Task InstallDependenciesAsync(Instance inst, ProjectVersion version, IProgress<TaskProgress>? progress, CancellationToken ct, int depth)
    {
        var required = version.Dependencies.Where(d => d.Required).ToList();
        if (required.Count == 0) return;
        var index = ContentIndex.Load(inst);
        var installedProjects = index.Files.Values.Select(r => r.ProjectId).ToHashSet();

        foreach (var dep in required)
        {
            if (installedProjects.Contains(dep.ProjectId)) continue;
            try
            {
                ProjectVersion? v;
                if (dep.VersionId != null && version.Source == ContentSource.Modrinth)
                    v = await ModrinthApi.GetVersionAsync(dep.VersionId, ct);
                else
                    v = (await GetVersionsAsync(version.Source, dep.ProjectId, ContentType.Mod, inst, true, ct)).FirstOrDefault();
                if (v == null) continue;
                var details = await GetDetailsAsync(version.Source, dep.ProjectId, ct);
                progress?.Report(new TaskProgress($"Installing dependency {details.Title}…"));
                await InstallAsync(inst, ContentType.Mod, v, details.Title, details.IconUrl, null, progress, ct, depth + 1);
            }
            catch (Exception ex)
            {
                Log.Warn($"Dependency {dep.ProjectId} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Matches unknown local files to Modrinth / CurseForge projects by hash and records them in the content index.</summary>
    public static async Task IdentifyAsync(Instance inst, IList<LocalItem> items, CancellationToken ct)
    {
        var unknown = items.Where(i => i.Ref == null && !i.IsDirectory).ToList();
        if (unknown.Count == 0) return;

        var hashes = await Task.Run(() => unknown.ToDictionary(i => i, i => { try { return Hashing.Sha1File(i.FilePath); } catch { return ""; } }), ct);
        var found = new Dictionary<LocalItem, ContentRef>();

        try
        {
            var mr = await ModrinthApi.IdentifyAsync(hashes.Values.Where(h => h.Length > 0), ct);
            var projects = await ModrinthApi.GetProjectsBriefAsync(mr.Values.Select(v => v.ProjectId), ct);
            foreach (var (item, hash) in hashes)
            {
                if (!mr.TryGetValue(hash, out var v)) continue;
                projects.TryGetValue(v.ProjectId, out var p);
                found[item] = new ContentRef
                {
                    Source = ContentSource.Modrinth, ProjectId = v.ProjectId, VersionId = v.Id,
                    Title = p.title, IconUrl = p.icon, VersionNumber = v.VersionNumber,
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Modrinth identify failed: {ex.Message}"); }

        var rest = unknown.Where(i => !found.ContainsKey(i)).ToList();
        if (rest.Count > 0 && CurseForgeApi.HasKey)
        {
            try
            {
                var fps = await Task.Run(() => rest.ToDictionary(i => i, i => Hashing.CurseForgeFingerprint(i.FilePath)), ct);
                var cf = await CurseForgeApi.IdentifyAsync(fps.Values, ct);
                var mods = cf.Count > 0 ? await CurseForgeApi.GetModsBriefAsync(cf.Values.Select(v => long.Parse(v.ProjectId)), ct) : [];
                foreach (var (item, fp) in fps)
                {
                    if (!cf.TryGetValue(fp, out var v)) continue;
                    mods.TryGetValue(v.ProjectId, out var m);
                    found[item] = new ContentRef
                    {
                        Source = ContentSource.CurseForge, ProjectId = v.ProjectId, VersionId = v.Id,
                        Title = m.title, IconUrl = m.icon, VersionNumber = v.Name,
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"CurseForge identify failed: {ex.Message}"); }
        }

        if (found.Count == 0) return;
        ContentIndex.Update(inst, idx =>
        {
            foreach (var (item, r) in found) idx.Files[item.IndexKey] = r;
        });
        foreach (var (item, r) in found)
        {
            item.Ref = r;
            if (item.Icon == null) item.IconUrl = r.IconUrl;
            if (item.Type != ContentType.Mod && r.Title != null) item.Name = r.Title;
        }
    }

    /// <summary>Finds newer compatible versions for installed files that are linked to Modrinth or CurseForge.</summary>
    public static async Task<Dictionary<LocalItem, ProjectVersion>> CheckUpdatesAsync(Instance inst, ContentType type, IList<LocalItem> items, CancellationToken ct)
    {
        var mc = inst.Config.MinecraftVersion;
        var loader = inst.Config.Loader;
        var updates = new Dictionary<LocalItem, ProjectVersion>();

        // Modrinth: bulk lookup by file hash (also works for files we identified by hash).
        var mrItems = items.Where(i => !i.IsDirectory && i.Ref?.Source == ContentSource.Modrinth).ToList();
        if (mrItems.Count > 0)
        {
            var hashes = await Task.Run(() => mrItems.ToDictionary(i => i, i => { try { return Hashing.Sha1File(i.FilePath); } catch { return ""; } }), ct);
            IEnumerable<string>? loaders = type switch
            {
                ContentType.Mod => [loader.LoaderSlug()],
                ContentType.ResourcePack => ["minecraft"],
                _ => null,
            };
            var latest = await ModrinthApi.GetLatestVersionsAsync(hashes.Values.Where(h => h.Length > 0), loaders, mc, ct);
            foreach (var (item, hash) in hashes)
                if (latest.TryGetValue(hash, out var v) && v.Sha1 != null && !v.Sha1.Equals(hash, StringComparison.OrdinalIgnoreCase)
                    && v.Id != item.Ref!.VersionId)
                    updates[item] = v;
        }

        // CurseForge: compare file ids with the mods' latest file index for this game version / loader.
        var cfItems = items.Where(i => i.Ref?.Source == ContentSource.CurseForge && long.TryParse(i.Ref.VersionId, out _)).ToList();
        if (cfItems.Count > 0 && CurseForgeApi.HasKey)
        {
            var latest = await CurseForgeApi.GetLatestFileIdsAsync(cfItems.Select(i => long.Parse(i.Ref!.ProjectId)), type, mc, loader, ct);
            var newer = cfItems.Where(i => latest.TryGetValue(i.Ref!.ProjectId, out var f) && f > long.Parse(i.Ref.VersionId!)).ToList();
            if (newer.Count > 0)
            {
                var files = (await CurseForgeApi.GetFilesAsync(newer.Select(i => latest[i.Ref!.ProjectId]), ct)).ToDictionary(f => f.Id);
                foreach (var i in newer)
                    if (files.TryGetValue(latest[i.Ref!.ProjectId].ToString(), out var f))
                    {
                        f.ProjectId = i.Ref.ProjectId;
                        updates[i] = f;
                    }
            }
        }
        return updates;
    }

    public static void Delete(Instance inst, LocalItem item)
    {
        if (item.IsDirectory) Directory.Delete(item.FilePath, true);
        else File.Delete(item.FilePath);
        ContentIndex.Update(inst, idx => idx.Files.Remove(item.IndexKey));
    }
}
