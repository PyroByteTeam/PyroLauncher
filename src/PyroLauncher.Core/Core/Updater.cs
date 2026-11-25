using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using PyroLauncher.UI;

namespace PyroLauncher.Core;

public record UpdateInfo(Version Version, string Tag, string PageUrl, IReadOnlyDictionary<string, string> Assets);

public enum InstallKind { WindowsSetup, WindowsPortable, MacApp, AppImage, LinuxPackage, LinuxArchive, Unknown }

/// <summary>Checks GitHub releases for a newer launcher and replaces the running copy with it.</summary>
public static class Updater
{
    public const string Repo = "PyroByteTeam/PyroLauncher";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    public static Version Current { get; } = Version.Parse(Http.AppVersion);
    public static UpdateInfo? Available { get; private set; }
    public static event Action? AvailableChanged;

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var json = await Http.GetJsonAsync($"https://api.github.com/repos/{Repo}/releases/latest", cts.Token,
            r => r.Headers.Accept.ParseAdd("application/vnd.github+json"));
        var tag = json?["tag_name"]?.GetValue<string>() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in json?["assets"]?.AsArray() ?? [])
            if (a?["name"]?.GetValue<string>() is { } name && a["browser_download_url"]?.GetValue<string>() is { } url)
                assets[name] = url;
        var info = new UpdateInfo(Normalize(version), tag, json?["html_url"]?.GetValue<string>() ?? ReleasesPage, assets);
        Available = info.Version > Normalize(Current) ? info : null;
        AvailableChanged?.Invoke();
        return info;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static InstallKind DetectInstall()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return InstallKind.Unknown;
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        if (OperatingSystem.IsWindows())
            return string.Equals(WindowsInstallDir()?.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? InstallKind.WindowsSetup : InstallKind.WindowsPortable;
        if (OperatingSystem.IsMacOS())
            return MacBundle() != null ? InstallKind.MacApp : InstallKind.Unknown;
        if (OperatingSystem.IsLinux())
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE"))) return InstallKind.AppImage;
            if (dir.StartsWith("/opt/pyrolauncher", StringComparison.Ordinal) || dir.StartsWith("/usr/", StringComparison.Ordinal))
                return InstallKind.LinuxPackage;
            return InstallKind.LinuxArchive;
        }
        return InstallKind.Unknown;
    }

    private static string? WindowsInstallDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PyroLauncher");
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>The PyroLauncher.app folder the process runs from, if any.</summary>
    private static string? MacBundle()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        for (var d = dir; d != null; d = Path.GetDirectoryName(d))
            if (d.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    private static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>The release file this install updates from, or null when it can only be updated by hand.</summary>
    public static string? AssetName(InstallKind kind, Version v)
    {
        var ver = v.ToString(3);
        return kind switch
        {
            InstallKind.WindowsSetup => $"PyroLauncher-Setup-{ver}-win-{Arch}.exe",
            InstallKind.WindowsPortable => $"PyroLauncher-{ver}-win-{Arch}-portable.zip",
            InstallKind.MacApp => $"PyroLauncher-{ver}-osx-{Arch}.zip",
            InstallKind.AppImage => $"PyroLauncher-{ver}-linux-{Arch}.AppImage",
            InstallKind.LinuxArchive => $"PyroLauncher-{ver}-linux-{Arch}.tar.gz",
            _ => null,
        };
    }

    public static string? AutoUpdateUrl(UpdateInfo info)
    {
        var name = AssetName(DetectInstall(), info.Version);
        return name != null && info.Assets.TryGetValue(name, out var url) ? url : null;
    }

    /// <summary>
    /// Downloads the update and starts a helper that swaps the files once this process exits.
    /// The caller must shut the launcher down right after this returns.
    /// </summary>
    public static async Task PrepareAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var kind = DetectInstall();
        var url = AutoUpdateUrl(info) ?? throw new InvalidOperationException(L.T("This install can't be updated automatically."));
        var work = Path.Combine(Path.GetTempPath(), $"PyroLauncher-update-{info.Version.ToString(3)}");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var file = Path.Combine(work, AssetName(kind, info.Version)!);
        await Http.DownloadFileAsync(url, file, ct, new Progress<(long done, long total)>(p =>
        {
            if (p.total > 0) progress?.Report((double)p.done / p.total);
        }));

        var exe = Environment.ProcessPath!;
        var appDir = Path.GetDirectoryName(exe)!;
        var pid = Environment.ProcessId;

        switch (kind)
        {
            case InstallKind.WindowsSetup:
                Process.Start(new ProcessStartInfo(file, "--update") { UseShellExecute = true });
                break;

            case InstallKind.WindowsPortable:
            {
                var src = Path.Combine(work, "files");
                ZipFile.ExtractToDirectory(file, src, true);
                var script = Path.Combine(work, "update.cmd");
                File.WriteAllText(script,
                    "@echo off\r\n" +
                    $":wait\r\ntasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                    $"robocopy \"{src}\" \"{appDir}\" /E /NFL /NDL /NJH /NJS /NP >nul\r\n" +
                    $"start \"\" \"{exe}\"\r\n" +
                    $"rmdir /s /q \"{work}\"\r\n");
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false });
                break;
            }

            case InstallKind.MacApp:
            {
                var bundle = MacBundle()!;
                var src = Path.Combine(work, "files");
                ZipFile.ExtractToDirectory(file, src, true);
                var newApp = Directory.GetDirectories(src, "*.app").FirstOrDefault()
                             ?? throw new InvalidOperationException("The update has no app bundle.");
                RunShell(work, pid,
                    $"rm -rf '{bundle}'\nmv '{newApp}' '{bundle}'\nxattr -cr '{bundle}' 2>/dev/null\nopen '{bundle}'\n");
                break;
            }

            case InstallKind.AppImage:
            {
                var target = Environment.GetEnvironmentVariable("APPIMAGE")!;
                MakeExecutable(file);
                RunShell(work, pid, $"mv -f '{file}' '{target}'\nnohup '{target}' >/dev/null 2>&1 &\n");
                break;
            }

            case InstallKind.LinuxArchive:
            {
                var src = Path.Combine(work, "files");
                Directory.CreateDirectory(src);
                await using (var fs = File.OpenRead(file))
                await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    await TarFile.ExtractToDirectoryAsync(gz, src, true, ct);
                var inner = Directory.GetDirectories(src).FirstOrDefault() ?? src;
                RunShell(work, pid, $"cp -rf '{inner}'/. '{appDir}'/\nnohup '{exe}' >/dev/null 2>&1 &\n");
                break;
            }

            default:
                throw new InvalidOperationException(L.T("This install can't be updated automatically."));
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    private static void RunShell(string work, int pid, string body)
    {
        var script = Path.Combine(work, "update.sh");
        File.WriteAllText(script,
            $"#!/bin/sh\nwhile kill -0 {pid} 2>/dev/null; do sleep 0.5; done\n{body}rm -rf '{work}'\n");
        MakeExecutable(script);
        Process.Start(new ProcessStartInfo("/bin/sh", $"'{script}'") { UseShellExecute = false });
    }
}
