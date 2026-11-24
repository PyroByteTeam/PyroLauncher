using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.UI;

namespace PyroLauncher.Content;

public enum ExportFormat { Modrinth, CurseForge, Pyro }

public record ExportOptions(ExportFormat Format, string Name, string Version, string Author, string Summary, IReadOnlyCollection<string> Files, string OutputPath);

public record ExportResult(string Path, int Linked, int Bundled);

/// <summary>
/// Writes an instance as a modpack. Modrinth / CurseForge exports reference mods that exist on that platform
/// (so they're downloaded on import) and bundle everything else in overrides/. Pyro exports bundle every file.
/// </summary>
public static class ModpackExporter
{
    /// <summary>Marker file inside a Pyro pack.</summary>
    public const string PyroManifest = "pyro.instance.json";
    public const string PyroExtension = ".pyropack";

    /// <summary>Top-level entries unticked by default: worlds, logs, caches and launcher-generated files.</summary>
    public static readonly HashSet<string> DefaultExcluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "screenshots", "logs", "crash-reports", "backups", ".fabric", ".mixin.out", "natives", "downloads",
        "usercache.json", "usernamecache.json", "launcher_profiles.json", "command_history.txt", ".cache", "debug",
        "patchouli_data.json", "realms_persistence.json", "telemetry", "local", "journeymap",
    };

    public static string Extension(ExportFormat f) => f switch
    {
        ExportFormat.Modrinth => ".mrpack",
        ExportFormat.CurseForge => ".zip",
        _ => PyroExtension,
    };

    public static async Task<ExportResult> ExportAsync(Instance inst, ExportOptions o, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        var files = o.Files.Select(f => f.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var linked = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase); // relPath → index entry

        if (o.Format == ExportFormat.Modrinth) await LinkModrinthAsync(inst, files, linked, progress, ct);
        else if (o.Format == ExportFormat.CurseForge) await LinkCurseForgeAsync(inst, files, linked, progress, ct);

        progress.Report(new TaskProgress(L.T("Writing the pack…"), 0));
        Directory.CreateDirectory(Path.GetDirectoryName(o.OutputPath)!);
        var tmp = o.OutputPath + ".tmp";
        await Task.Run(() =>
        {
            using (var fs = new FileStream(tmp, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var prefix = o.Format == ExportFormat.Pyro ? "minecraft/" : "overrides/";
                var bundle = files.Where(f => !linked.ContainsKey(f)).ToList();
                for (int i = 0; i < bundle.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var full = Path.Combine(inst.GameDir, bundle[i].Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(full)) continue;
                    try { zip.CreateEntryFromFile(full, prefix + bundle[i], CompressionLevel.Optimal); }
                    catch (IOException ex) { Log.Warn($"Export skipped {bundle[i]}: {ex.Message}"); }
                    if (i % 20 == 0) progress.Report(new TaskProgress(L.F("Writing the pack… ({0}/{1})", i, bundle.Count), (double)i / bundle.Count));
                }
                WriteManifest(zip, inst, o, linked);
                if (o.Format == ExportFormat.Pyro && File.Exists(inst.IconPath)) zip.CreateEntryFromFile(inst.IconPath, "icon.png");
            }
            File.Move(tmp, o.OutputPath, true);
        }, ct);
        return new ExportResult(o.OutputPath, linked.Count, files.Count - linked.Count);
    }

    private static readonly string[] Linkable = ["mods/", "resourcepacks/", "shaderpacks/"];
    private static bool IsLinkable(string rel) => Linkable.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && rel.Count(c => c == '/') == 1;

    private static async Task LinkModrinthAsync(Instance inst, List<string> files, Dictionary<string, JsonNode> linked, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        progress.Report(new TaskProgress(L.T("Looking up files on Modrinth…")));
        var candidates = files.Where(IsLinkable).ToList();
        var hashes = await Task.Run(() => candidates.ToDictionary(f => f, f => Hashing.Sha1File(Full(inst, f))), ct);
        Dictionary<string, ProjectVersion> found;
        try { found = await ModrinthApi.IdentifyAsync(hashes.Values, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Modrinth lookup failed, bundling everything: {ex.Message}");
            return;
        }
        foreach (var (rel, sha1) in hashes)
        {
            if (!found.TryGetValue(sha1, out var v)) continue;
            // The version must contain this exact file with a download URL.
            var full = Full(inst, rel);
            var url = v.Sha1?.Equals(sha1, StringComparison.OrdinalIgnoreCase) == true ? v.Url : null;
            if (url == null) continue;
            linked[rel] = new JsonObject
            {
                ["path"] = rel,
                ["hashes"] = new JsonObject { ["sha1"] = sha1, ["sha512"] = Hashing.Sha512File(full) },
                ["env"] = new JsonObject { ["client"] = "required", ["server"] = rel.StartsWith("mods/") ? "required" : "unsupported" },
                ["downloads"] = new JsonArray(url),
                ["fileSize"] = new FileInfo(full).Length,
            };
        }
    }

    private static async Task LinkCurseForgeAsync(Instance inst, List<string> files, Dictionary<string, JsonNode> linked, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        if (!CurseForgeApi.HasKey) return; // everything gets bundled
        progress.Report(new TaskProgress(L.T("Looking up files on CurseForge…")));
        var candidates = files.Where(IsLinkable).ToList();
        var prints = await Task.Run(() => candidates.ToDictionary(f => f, f => Hashing.CurseForgeFingerprint(Full(inst, f))), ct);
        Dictionary<uint, ProjectVersion> found;
        try { found = await CurseForgeApi.IdentifyAsync(prints.Values.Distinct(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"CurseForge lookup failed, bundling everything: {ex.Message}");
            return;
        }
        foreach (var (rel, fp) in prints)
            if (found.TryGetValue(fp, out var v))
                linked[rel] = new JsonObject { ["projectID"] = long.Parse(v.ProjectId), ["fileID"] = long.Parse(v.Id), ["required"] = true };
    }

    private static string Full(Instance inst, string rel) => Path.Combine(inst.GameDir, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteManifest(ZipArchive zip, Instance inst, ExportOptions o, Dictionary<string, JsonNode> linked)
    {
        var cfg = inst.Config;
        JsonNode manifest;
        string entryName;
        switch (o.Format)
        {
            case ExportFormat.Modrinth:
            {
                var deps = new JsonObject { ["minecraft"] = cfg.MinecraftVersion };
                if (cfg.LoaderVersion != null)
                {
                    var key = cfg.Loader switch { ModLoader.Fabric => "fabric-loader", ModLoader.Forge => "forge", ModLoader.NeoForge => "neoforge", _ => null };
                    if (key != null) deps[key] = inst.ShortLoaderVersion;
                }
                manifest = new JsonObject
                {
                    ["formatVersion"] = 1,
                    ["game"] = "minecraft",
                    ["versionId"] = o.Version,
                    ["name"] = o.Name,
                    ["summary"] = o.Summary,
                    ["files"] = new JsonArray(linked.Values.Select(v => v.DeepClone()).ToArray()),
                    ["dependencies"] = deps,
                };
                entryName = "modrinth.index.json";
                break;
            }
            case ExportFormat.CurseForge:
            {
                var loaders = new JsonArray();
                if (cfg.LoaderVersion != null && cfg.Loader != ModLoader.Vanilla)
                    loaders.Add(new JsonObject { ["id"] = $"{cfg.Loader.LoaderSlug()}-{inst.ShortLoaderVersion}", ["primary"] = true });
                manifest = new JsonObject
                {
                    ["minecraft"] = new JsonObject { ["version"] = cfg.MinecraftVersion, ["modLoaders"] = loaders },
                    ["manifestType"] = "minecraftModpack",
                    ["manifestVersion"] = 1,
                    ["name"] = o.Name,
                    ["version"] = o.Version,
                    ["author"] = o.Author,
                    ["files"] = new JsonArray(linked.Values.Select(v => v.DeepClone()).ToArray()),
                    ["overrides"] = "overrides",
                };
                entryName = "manifest.json";
                break;
            }
            default:
            {
                // Pyro: the instance settings travel with the pack (but not play time, accounts or install state).
                manifest = new JsonObject
                {
                    ["format"] = 1,
                    ["name"] = o.Name,
                    ["version"] = o.Version,
                    ["author"] = o.Author,
                    ["summary"] = o.Summary,
                    ["minecraft"] = cfg.MinecraftVersion,
                    ["loader"] = cfg.Loader.ToString(),
                    ["loaderVersion"] = cfg.LoaderVersion,
                    ["overrides"] = JsonSerializer.SerializeToNode(cfg.Overrides, JsonUtil.Options),
                    ["launcher"] = $"PyroLauncher {Http.AppVersion}",
                };
                entryName = PyroManifest;
                break;
            }
        }
        var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var s = e.Open();
        using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
        manifest.WriteTo(w);
    }
}
