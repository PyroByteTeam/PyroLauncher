using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;
using PyroLauncher.UI;

namespace PyroLauncher.Servers;

/// <summary>Downloads the server jar / loader for a server pack and works out how to start it.</summary>
public static class ServerInstaller
{
    public static async Task InstallAsync(ServerPack pack, IProgress<TaskProgress>? progress, CancellationToken ct)
    {
        var cfg = pack.Instance.Config;
        var dir = pack.ServerDir;
        Directory.CreateDirectory(dir);

        progress?.Report(new TaskProgress(L.F("Preparing Minecraft {0}…", cfg.MinecraftVersion)));
        await MojangMeta.EnsureVersionJsonAsync(cfg.MinecraftVersion, ct);
        var vanilla = VersionResolver.Resolve(cfg.MinecraftVersion);
        pack.Config.JavaMajor = vanilla.JavaMajor;
        var java = JavaManager.ConsoleJava(await JavaManager.EnsureAsync(vanilla.JavaMajor, progress, ct));

        List<string> args;
        switch (cfg.Loader)
        {
            case ModLoader.Vanilla:
            {
                var json = JsonUtil.ParseFile(MojangMeta.VersionJsonPath(cfg.MinecraftVersion));
                var server = json.Obj("downloads")?.Obj("server")
                             ?? throw new InvalidOperationException(L.F("Minecraft {0} has no server download.", cfg.MinecraftVersion));
                progress?.Report(new TaskProgress(L.T("Downloading the server…")));
                await Downloader.DownloadAllAsync([new DownloadItem(server.Str("url")!, Path.Combine(dir, "server.jar"),
                    server.Str("sha1"), server.Long("size", -1))], progress, "server", ct);
                args = ["-jar", "server.jar", "nogui"];
                break;
            }
            case ModLoader.Fabric:
            {
                progress?.Report(new TaskProgress(L.F("Installing Fabric {0}…", cfg.LoaderVersion ?? "")));
                var installers = await Http.GetJsonAsync("https://meta.fabricmc.net/v2/versions/installer", ct) as JsonArray;
                var installer = installers?.FirstOrDefault(x => x?["stable"]?.GetValue<bool>() == true)?.Str("version")
                                ?? installers?.FirstOrDefault()?.Str("version") ?? "1.0.1";
                var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(cfg.MinecraftVersion)}/{Uri.EscapeDataString(cfg.LoaderVersion!)}/{installer}/server/jar";
                await Http.DownloadFileAsync(url, Path.Combine(dir, "fabric-server-launch.jar"), ct);
                args = ["-jar", "fabric-server-launch.jar", "nogui"];
                break;
            }
            case ModLoader.Forge or ModLoader.NeoForge:
                args = await InstallForgeLikeAsync(pack, java, progress, ct);
                break;
            default:
                throw new InvalidOperationException();
        }

        pack.Config.LaunchArgs = args;
        pack.Config.InstalledKey = pack.CurrentKey;
        ServerProperties.CreateDefault(pack.PropertiesPath, SettingsService.Current.ServerPropertiesDefaults, pack.Name);
        pack.Save();
        Ui.Run(pack.Refresh);
    }

    private static async Task<List<string>> InstallForgeLikeAsync(ServerPack pack, string java, IProgress<TaskProgress>? progress, CancellationToken ct)
    {
        var cfg = pack.Instance.Config;
        var label = cfg.Loader == ModLoader.Forge ? "Forge" : "NeoForge";
        var url = LoaderService.InstallerUrl(cfg.Loader, cfg.LoaderVersion!);
        var installer = Path.Combine(AppPaths.Cache, "installers", Path.GetFileName(new Uri(url).LocalPath));
        progress?.Report(new TaskProgress(L.F("Downloading {0} installer…", label)));
        if (!File.Exists(installer)) await Downloader.DownloadOneAsync(new DownloadItem(url, installer), ct);

        progress?.Report(new TaskProgress(L.F("Running {0} installer (this can take a few minutes)…", label)));
        var psi = new ProcessStartInfo(java)
        {
            WorkingDirectory = pack.ServerDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("--installServer");
        psi.ArgumentList.Add(pack.ServerDir);
        using var proc = new Process { StartInfo = psi };
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (line == null) return;
            pack.Instance.AppendLog("[installer] " + line);
            lock (tail) { tail.Enqueue(line); if (tail.Count > 15) tail.Dequeue(); }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using (ct.Register(() => { try { proc.Kill(true); } catch { } }))
            await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            string msg;
            lock (tail) msg = string.Join("\n", tail);
            throw new InvalidOperationException(L.F("{0} installer failed (exit code {1}).\n{2}", label, proc.ExitCode, msg));
        }
        foreach (var junk in Directory.GetFiles(pack.ServerDir, "*installer*.jar.log")) File.Delete(junk);
        return DetectForgeLaunch(pack.ServerDir) ?? throw new InvalidOperationException(L.F("Couldn't find how to start the {0} server.", label));
    }

    /// <summary>Modern Forge/NeoForge start through an @args file; old Forge through a single jar.</summary>
    public static List<string>? DetectForgeLaunch(string dir)
    {
        var os = OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt";
        var libs = Path.Combine(dir, "libraries");
        if (Directory.Exists(libs))
        {
            var argsFile = Directory.GetFiles(libs, os, SearchOption.AllDirectories)
                .FirstOrDefault(f => f.Contains("minecraftforge") || f.Contains("neoforged"));
            if (argsFile != null)
            {
                var list = new List<string>();
                if (File.Exists(Path.Combine(dir, "user_jvm_args.txt"))) list.Add("@user_jvm_args.txt");
                list.Add("@" + Path.GetRelativePath(dir, argsFile).Replace('\\', '/'));
                list.Add("nogui");
                return list;
            }
        }
        var jar = Directory.GetFiles(dir, "*.jar")
            .Select(Path.GetFileName)
            .FirstOrDefault(n => n != null && (n.StartsWith("forge", StringComparison.OrdinalIgnoreCase) || n.StartsWith("neoforge", StringComparison.OrdinalIgnoreCase))
                                 && !n.Contains("installer", StringComparison.OrdinalIgnoreCase));
        return jar != null ? ["-jar", jar, "nogui"] : null;
    }

    /// <summary>Unpacks a downloaded server pack zip into the server folder, dropping a single wrapping folder if there is one.</summary>
    public static void ExtractServerPack(string zipPath, string serverDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        var tops = files.Select(e => e.FullName.Split('/')[0]).Distinct().ToList();
        var strip = tops.Count == 1 && files.All(e => e.FullName.Contains('/')) ? tops[0] + "/" : "";
        var root = Path.GetFullPath(serverDir) + Path.DirectorySeparatorChar;
        foreach (var e in files)
        {
            var rel = e.FullName[strip.Length..];
            if (rel.Length == 0) continue;
            var dest = Path.GetFullPath(Path.Combine(serverDir, rel));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, true);
        }
    }

    public static async Task<string> FindJavaAsync(ServerPack pack, IProgress<TaskProgress>? progress, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(pack.Config.JavaPath) && File.Exists(pack.Config.JavaPath)
            ? pack.Config.JavaPath
            : JavaManager.ConsoleJava(await JavaManager.EnsureAsync(pack.Config.JavaMajor, progress, ct));

    /// <summary>The start arguments the installer would pick for this server, or null if they can't be worked out.</summary>
    public static List<string>? DefaultLaunchArgs(ServerPack pack) => pack.Instance.Config.Loader switch
    {
        ModLoader.Vanilla => ["-jar", "server.jar", "nogui"],
        ModLoader.Fabric => ["-jar", "fabric-server-launch.jar", "nogui"],
        _ => DetectForgeLaunch(pack.ServerDir),
    };
}
