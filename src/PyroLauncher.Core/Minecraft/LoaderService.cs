using PyroLauncher.UI;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.Minecraft;

public static class LoaderService
{
    private static JsonObject? _forgeMeta;
    private static List<string>? _neoVersions;
    private static List<string>? _neoLegacyVersions;

    // ---------------------------------------------------------------- version lists

    public static async Task<List<string>> GetLoaderVersionsAsync(ModLoader loader, string mc, CancellationToken ct = default)
    {
        switch (loader)
        {
            case ModLoader.Fabric:
            {
                var arr = await Http.GetJsonAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mc)}", ct) as JsonArray;
                return arr?.Select(x => x.Obj("loader").Str("version")).Where(s => s != null).Cast<string>().ToList() ?? [];
            }
            case ModLoader.Forge:
            {
                _forgeMeta ??= await Http.GetJsonAsync("https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json", ct) as JsonObject;
                var list = _forgeMeta?.Get(mc) as JsonArray;
                return list?.Select(x => x.AsStr()).Where(s => s != null).Cast<string>().Reverse().ToList() ?? [];
            }
            case ModLoader.NeoForge:
            {
                if (mc == "1.20.1")
                {
                    _neoLegacyVersions ??= (await Http.GetJsonAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/forge", ct))
                        .Strings("versions").ToList();
                    return _neoLegacyVersions.Where(v => v.StartsWith("1.20.1-")).Reverse().ToList();
                }
                _neoVersions ??= (await Http.GetJsonAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", ct))
                    .Strings("versions").ToList();
                var prefix = NeoForgePrefix(mc);
                return _neoVersions.Where(v => v.StartsWith(prefix)).Reverse().ToList();
            }
            default:
                return [];
        }
    }

    /// <summary>NeoForge versions drop the leading "1." of the MC version: 1.21.1 → 21.1.x, 1.21 → 21.0.x. Newer year-based MC versions (26.1) map to 26.1.x.</summary>
    public static string NeoForgePrefix(string mc)
    {
        var parts = mc.Split('.');
        if (parts[0] == "1" && parts.Length >= 2)
            return $"{parts[1]}.{(parts.Length > 2 ? parts[2] : "0")}.";
        return mc + ".";
    }

    /// <summary>Turns a short loader version from a modpack (e.g. forge "47.2.0") into our stored form.</summary>
    public static async Task<string> NormalizeLoaderVersionAsync(ModLoader loader, string mc, string version, CancellationToken ct)
    {
        if (loader != ModLoader.Forge) return version;
        if (version.StartsWith(mc + "-")) return version;
        try
        {
            var all = await GetLoaderVersionsAsync(ModLoader.Forge, mc, ct);
            var match = all.FirstOrDefault(v => v == $"{mc}-{version}" || v.StartsWith($"{mc}-{version}-"));
            if (match != null) return match;
        }
        catch (Exception ex) { Log.Warn($"Forge version lookup failed: {ex.Message}"); }
        return $"{mc}-{version}";
    }

    // ---------------------------------------------------------------- install

    /// <summary>Ensures vanilla + loader files exist and returns the version id to launch.</summary>
    public static async Task<string> EnsureInstalledAsync(Instance inst, IProgress<TaskProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var cfg = inst.Config;
        progress?.Report(new TaskProgress(L.F("Preparing Minecraft {0}…", cfg.MinecraftVersion)));
        await MojangMeta.EnsureVersionJsonAsync(cfg.MinecraftVersion, ct);
        if (cfg.Loader == ModLoader.Vanilla) return cfg.MinecraftVersion;

        if (string.IsNullOrEmpty(cfg.LoaderVersion))
            throw new InvalidOperationException(L.F("No {0} version selected for this instance.", cfg.Loader));

        var key = $"{cfg.Loader}:{cfg.LoaderVersion}";
        if (cfg.InstalledKey == key && cfg.InstalledVersionId != null && IsInstalled(cfg.InstalledVersionId))
            return cfg.InstalledVersionId;

        string id = cfg.Loader switch
        {
            ModLoader.Fabric => await InstallFabricAsync(cfg.MinecraftVersion, cfg.LoaderVersion, progress, ct),
            ModLoader.Forge => await InstallForgeLikeAsync(cfg.MinecraftVersion,
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{cfg.LoaderVersion}/forge-{cfg.LoaderVersion}-installer.jar",
                "Forge", progress, log, ct),
            ModLoader.NeoForge => await InstallForgeLikeAsync(cfg.MinecraftVersion,
                cfg.LoaderVersion.StartsWith("1.20.1-")
                    ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{cfg.LoaderVersion}/forge-{cfg.LoaderVersion}-installer.jar"
                    : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{cfg.LoaderVersion}/neoforge-{cfg.LoaderVersion}-installer.jar",
                "NeoForge", progress, log, ct),
            _ => cfg.MinecraftVersion,
        };
        cfg.InstalledKey = key;
        cfg.InstalledVersionId = id;
        inst.Save();
        return id;
    }

    private static bool IsInstalled(string id)
    {
        var dir = Path.Combine(AppPaths.Versions, id);
        return File.Exists(Path.Combine(dir, id + ".json")) && File.Exists(Path.Combine(dir, ".pyro-installed"));
    }

    private static void MarkInstalled(string id) =>
        File.WriteAllText(Path.Combine(AppPaths.Versions, id, ".pyro-installed"), DateTime.Now.ToString("O"));

    private static async Task<string> InstallFabricAsync(string mc, string loader, IProgress<TaskProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new TaskProgress(L.F("Installing Fabric {0}…", loader)));
        var profile = await Http.GetJsonAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loader)}/profile/json", ct)
            ?? throw new InvalidOperationException(L.T("Fabric meta returned no profile."));
        var id = profile.Str("id") ?? $"fabric-loader-{loader}-{mc}";
        JsonUtil.WriteNode(MojangMeta.VersionJsonPath(id), profile);
        MarkInstalled(id);
        return id;
    }

    private static async Task<string> InstallForgeLikeAsync(string mc, string installerUrl, string label,
        IProgress<TaskProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        // The installer needs vanilla present and a launcher_profiles.json in the target dir.
        var vanilla = VersionResolver.Resolve(mc);
        progress?.Report(new TaskProgress(L.F("Preparing Minecraft {0} for {1}…", mc, label)));
        var java = await JavaManager.EnsureAsync(vanilla.JavaMajor, progress, ct);
        if (vanilla.ClientDownload != null)
        {
            await Downloader.DownloadAllAsync([new DownloadItem(vanilla.ClientDownload.Str("url")!, MojangMeta.VersionJarPath(mc),
                vanilla.ClientDownload.Str("sha1"), vanilla.ClientDownload.Long("size", -1))], progress, "client", ct);
        }

        var installer = Path.Combine(AppPaths.Cache, "installers", Path.GetFileName(new Uri(installerUrl).LocalPath));
        progress?.Report(new TaskProgress(L.F("Downloading {0} installer…", label)));
        if (!File.Exists(installer)) await Downloader.DownloadOneAsync(new DownloadItem(installerUrl, installer), ct);

        JsonNode profile;
        using (var zip = ZipFile.OpenRead(installer))
        {
            var entry = zip.GetEntry("install_profile.json") ?? throw new InvalidDataException(L.T("Installer has no install_profile.json"));
            using var s = entry.Open();
            profile = JsonNode.Parse(s)!;
        }

        if (profile.Obj("versionInfo") is { } versionInfo)
            return await InstallLegacyForgeAsync(mc, installer, profile, versionInfo, ct);

        var id = profile.Str("version") ?? throw new InvalidDataException(L.T("install_profile.json has no version id"));
        if (IsInstalled(id)) return id;

        var profiles = Path.Combine(AppPaths.Meta, "launcher_profiles.json");
        if (!File.Exists(profiles)) await File.WriteAllTextAsync(profiles, "{\"profiles\":{},\"selectedProfile\":null}", ct);

        progress?.Report(new TaskProgress(L.F("Running {0} installer (this can take a few minutes)…", label)));
        var psi = new ProcessStartInfo(JavaManager.ConsoleJava(java))
        {
            WorkingDirectory = Path.GetDirectoryName(installer)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(AppPaths.Meta);

        using var proc = new Process { StartInfo = psi };
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (line == null) return;
            log?.Invoke("[installer] " + line);
            lock (tail) { tail.Enqueue(line); if (tail.Count > 15) tail.Dequeue(); }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using (ct.Register(() => { try { proc.Kill(true); } catch { } }))
            await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0 || !File.Exists(MojangMeta.VersionJsonPath(id)))
        {
            string msg;
            lock (tail) msg = string.Join("\n", tail);
            throw new InvalidOperationException(L.F("{0} installer failed (exit code {1}).\n{2}", label, proc.ExitCode, msg));
        }
        MarkInstalled(id);
        return id;
    }

    /// <summary>Old (≤1.12) Forge installers: version JSON is embedded, universal jar is extracted manually.</summary>
    private static Task<string> InstallLegacyForgeAsync(string mc, string installer, JsonNode profile, JsonObject versionInfo, CancellationToken ct)
    {
        var id = versionInfo.Str("id") ?? throw new InvalidDataException(L.T("Legacy installer versionInfo has no id"));
        var json = (JsonObject)versionInfo.DeepClone();
        if (json.Str("inheritsFrom") == null) json["inheritsFrom"] = mc;
        json.Remove("jar");
        JsonUtil.WriteNode(MojangMeta.VersionJsonPath(id), json);

        var install = profile.Obj("install");
        var filePath = install.Str("filePath");
        var mavenPath = install.Str("path");
        if (filePath != null && mavenPath != null)
        {
            var dest = Path.Combine(AppPaths.Libraries, VersionResolver.MavenPath(mavenPath));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var zip = ZipFile.OpenRead(installer);
            zip.GetEntry(filePath)?.ExtractToFile(dest, true);
        }
        MarkInstalled(id);
        return Task.FromResult(id);
    }
}
