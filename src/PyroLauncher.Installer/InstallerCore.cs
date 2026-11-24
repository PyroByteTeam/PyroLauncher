using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;

namespace PyroLauncher.Installer;

public record InstalledInfo(Version Version, string Path);

/// <summary>Per-user install/update/uninstall of PyroLauncher. No admin rights required.</summary>
public static class InstallerCore
{
    public const string AppName = "PyroLauncher";
    public const string Publisher = "PyroByte";
    public const string ExeName = "PyroLauncher.exe";
    public const string SetupName = "PyroLauncher.Setup.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PyroLauncher";

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string UserDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    private static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppName}.lnk");

    private static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

    // ------------------------------------------------------------------ state

    public static bool HasPayload => typeof(InstallerCore).Assembly.GetManifestResourceInfo("payload.zip") != null;

    public static Version? PayloadVersion()
    {
        using var zip = OpenPayload();
        if (zip?.GetEntry("version.txt") is not { } e) return null;
        using var r = new StreamReader(e.Open());
        return Version.TryParse(r.ReadToEnd().Trim(), out var v) ? v : null;
    }

    private static ZipArchive? OpenPayload()
    {
        var s = typeof(InstallerCore).Assembly.GetManifestResourceStream("payload.zip");
        return s == null ? null : new ZipArchive(s, ZipArchiveMode.Read);
    }

    public static InstalledInfo? GetInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (key == null) return null;
        var path = key.GetValue("InstallLocation") as string;
        var ver = key.GetValue("DisplayVersion") as string;
        if (path == null || !File.Exists(Path.Combine(path, ExeName))) return null;
        return new InstalledInfo(Version.TryParse(ver, out var v) ? v : new Version(0, 0), path);
    }

    // ------------------------------------------------------------------ install / update

    public static async Task InstallAsync(string dir, bool desktopShortcut, IProgress<(string msg, double pct)> progress)
    {
        await Task.Run(() =>
        {
            progress.Report(("Closing running launcher…", 0.02));
            CloseRunning(dir);

            Directory.CreateDirectory(dir);
            using var zip = OpenPayload() ?? throw new InvalidOperationException("This setup has no launcher payload. Build it with build-installer.ps1.");

            // Remove files from a previous version (keep nothing: user data lives in %APPDATA%).
            foreach (var f in Directory.GetFiles(dir))
                if (!Path.GetFileName(f).Equals(SetupName, StringComparison.OrdinalIgnoreCase))
                    TryDelete(f);
            foreach (var d in Directory.GetDirectories(dir)) TryDeleteDir(d);

            var entries = zip.Entries.Where(e => e.Name.Length > 0 && e.FullName != "version.txt").ToList();
            var root = Path.GetFullPath(dir).TrimEnd('\\') + '\\';
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
                progress.Report(($"Installing {e.Name}", 0.05 + 0.8 * (i + 1) / entries.Count));
            }

            progress.Report(("Registering…", 0.9));
            // Keep a copy of the setup for maintenance (update/uninstall from Apps & Features).
            var self = Environment.ProcessPath!;
            var setupCopy = Path.Combine(dir, SetupName);
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(setupCopy), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, setupCopy, true);

            var exe = Path.Combine(dir, ExeName);
            CreateShortcut(StartMenuShortcut, exe, dir);
            if (desktopShortcut) CreateShortcut(DesktopShortcut, exe, dir);
            else TryDelete(DesktopShortcut);

            var version = PayloadVersion()?.ToString(3) ?? "1.0.0";
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", Publisher);
            key.SetValue("InstallLocation", dir);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", $"\"{setupCopy}\" --uninstall");
            key.SetValue("ModifyPath", $"\"{setupCopy}\"");
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("EstimatedSize", (int)(DirSize(dir) / 1024), RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            progress.Report(("Done", 1));
        });
    }

    // ------------------------------------------------------------------ uninstall

    /// <summary>A group of user data that can optionally be removed on uninstall. Paths are relative to <see cref="UserDataDir"/>.</summary>
    public record DataCategory(string Key, string Title, string Description, string[] Paths);

    /// <summary>Everything the uninstaller can remove. Add an entry here to offer another option.</summary>
    public static readonly DataCategory[] DataCategories =
    [
        new("instances", "Instances",
            "Worlds, mods, resource packs, shader packs, screenshots and play time of every instance.",
            ["instances"]),
        new("settings", "Settings and accounts",
            "Launcher settings, categories, layout choices and saved Microsoft accounts.",
            ["settings.json", "settings.json.tmp", "accounts.dat"]),
        new("downloads", "Downloaded files",
            "Shared game libraries, Java runtimes, caches and logs. They are downloaded again when needed.",
            ["meta", "java", "cache", "logs"]),
    ];

    public static async Task UninstallAsync(IReadOnlyCollection<string> removeData, IProgress<(string msg, double pct)> progress)
    {
        var info = GetInstalled();
        await Task.Run(() =>
        {
            progress.Report(("Closing running launcher…", 0.1));
            if (info != null) CloseRunning(info.Path);

            progress.Report(("Removing shortcuts…", 0.3));
            TryDelete(StartMenuShortcut);
            TryDelete(DesktopShortcut);

            progress.Report(("Removing files…", 0.5));
            if (info != null)
            {
                foreach (var f in Directory.GetFiles(info.Path))
                    if (!IsSelf(f)) TryDelete(f);
                foreach (var d in Directory.GetDirectories(info.Path)) TryDeleteDir(d);
            }

            if (removeData.Count > 0 && DataCategories.All(c => removeData.Contains(c.Key)))
            {
                // Everything selected: remove the whole data folder, including anything not covered by a category.
                progress.Report(("Removing all launcher data…", 0.75));
                TryDeleteDir(UserDataDir);
            }
            else
            {
                foreach (var cat in DataCategories.Where(c => removeData.Contains(c.Key)))
                {
                    progress.Report(($"Removing {cat.Title.ToLowerInvariant()}…", 0.75));
                    foreach (var rel in cat.Paths)
                    {
                        var p = Path.Combine(UserDataDir, rel);
                        if (Directory.Exists(p)) TryDeleteDir(p);
                        else TryDelete(p);
                    }
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            progress.Report(("Done", 1));
        });

        // The running setup may live inside the install folder; remove it (and the folder) once we exit.
        if (info != null) ScheduleFolderDeletion(info.Path);
    }

    /// <summary>When started from the install folder, copy to %TEMP% and relaunch so the folder can be deleted.</summary>
    public static bool RelaunchFromTempIfNeeded(string[] args)
    {
        var info = GetInstalled();
        var self = Environment.ProcessPath!;
        if (info == null || !Path.GetFullPath(self).StartsWith(Path.GetFullPath(info.Path), StringComparison.OrdinalIgnoreCase)) return false;
        var temp = Path.Combine(Path.GetTempPath(), $"PyroLauncher.Setup.{Guid.NewGuid():N}.exe");
        File.Copy(self, temp, true);
        var psi = new ProcessStartInfo(temp) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--from-temp");
        Process.Start(psi);
        return true;
    }

    private static void ScheduleFolderDeletion(string dir)
    {
        var cmd = $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{dir}\"";
        if (Environment.ProcessPath is { } self && self.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            cmd += $" & del /f /q \"{self}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
    }

    // ------------------------------------------------------------------ helpers

    public static bool IsLauncherRunning(string dir) => FindRunning(dir).Any();

    private static IEnumerable<Process> FindRunning(string dir)
    {
        var exe = Path.GetFullPath(Path.Combine(dir, ExeName));
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path != null && string.Equals(Path.GetFullPath(path), exe, StringComparison.OrdinalIgnoreCase)) yield return p;
        }
    }

    private static void CloseRunning(string dir)
    {
        foreach (var p in FindRunning(dir))
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(5000)) p.Kill(true);
            }
            catch { }
        }
    }

    private static bool IsSelf(string path) =>
        Environment.ProcessPath is { } self && string.Equals(Path.GetFullPath(self), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private static void CreateShortcut(string lnkPath, string target, string workDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = workDir;
            lnk.IconLocation = target + ",0";
            lnk.Description = "Minecraft launcher for modded instances";
            lnk.Save();
        }
        catch { /* shortcuts are a convenience */ }
    }

    private static long DirSize(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
