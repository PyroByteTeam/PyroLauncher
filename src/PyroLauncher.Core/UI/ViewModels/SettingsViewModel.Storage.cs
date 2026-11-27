using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

/// <summary>Settings → Storage: where instances and game files live, moving them, and disk usage.</summary>
public partial class SettingsViewModel
{
    public string InstancesFolder => StorageLocations.Get(StorageRole.Instances);
    public string GameDataFolder => StorageLocations.Get(StorageRole.GameData);
    public bool InstancesIsCustom => !StorageLocations.IsDefault(StorageRole.Instances);
    public bool GameDataIsCustom => !StorageLocations.IsDefault(StorageRole.GameData);

    /// <summary>Shown when a custom folder was missing at start-up (e.g. an unplugged drive).</summary>
    public string? StorageWarning => StorageLocations.Unavailable.Count == 0 ? null : string.Join("\n", StorageLocations.Unavailable.Select(u =>
        L.F("{0} isn't available, so the default folder is used until it's back: {1}", RoleName(u.Role), u.Dir)));

    [ObservableProperty] private bool _isMeasuring;
    [ObservableProperty] private StorageReport? _usage;
    [ObservableProperty] private bool _isMigrating;
    [ObservableProperty] private string _migrationText = "";
    [ObservableProperty] private double _migrationProgress;
    private CancellationTokenSource? _migrationCts;
    private CancellationTokenSource? _measureCts;

    public bool ShowStorageBreakdown
    {
        get => S.ShowStorageBreakdown;
        set { S.ShowStorageBreakdown = value; Changed(); }
    }

    public string UsageTotalText => Usage == null ? "…" : DownloadManager.FormatBytes(Usage.Total);
    public string InstancesSizeText => Usage == null ? "…" : DownloadManager.FormatBytes(Usage.InstancesTotal);
    public string GameDataSizeText => Usage == null ? "…" : DownloadManager.FormatBytes(Usage.GameDataTotal);
    public string HomeSizeText => Usage == null ? "…" : DownloadManager.FormatBytes(Usage.HomeTotal);
    public bool HasInstanceUsage => Usage is { Instances.Count: > 0 };

    partial void OnUsageChanged(StorageReport? value)
    {
        OnPropertyChanged(nameof(UsageTotalText));
        OnPropertyChanged(nameof(InstancesSizeText));
        OnPropertyChanged(nameof(GameDataSizeText));
        OnPropertyChanged(nameof(HomeSizeText));
        OnPropertyChanged(nameof(HasInstanceUsage));
    }

    partial void OnSectionChanged(string value)
    {
        if (value == "storage") _ = RefreshUsage();
    }

    [RelayCommand]
    private async Task RefreshUsage()
    {
        _measureCts?.Cancel();
        var cts = _measureCts = new CancellationTokenSource();
        IsMeasuring = true;
        try
        {
            var report = await StorageUsage.MeasureAsync(cts.Token);
            if (!cts.IsCancellationRequested) Usage = report;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Measuring storage failed", ex); }
        finally { if (_measureCts == cts) IsMeasuring = false; }
    }

    private static string RoleName(StorageRole role) => role == StorageRole.Instances ? L.T("Instances folder") : L.T("Game files and cache folder");

    [RelayCommand] private void OpenInstancesFolder() => Dialogs.OpenPath(InstancesFolder);
    [RelayCommand] private void OpenGameDataFolder() => Dialogs.OpenPath(GameDataFolder);

    [RelayCommand] private Task ChangeInstancesFolder() => ChangeFolder(StorageRole.Instances, null);
    [RelayCommand] private Task ChangeGameDataFolder() => ChangeFolder(StorageRole.GameData, null);
    [RelayCommand] private Task ResetInstancesFolder() => ChangeFolder(StorageRole.Instances, AppPaths.Root);
    [RelayCommand] private Task ResetGameDataFolder() => ChangeFolder(StorageRole.GameData, AppPaths.Root);

    [RelayCommand] private void CancelMigration() => _migrationCts?.Cancel();

    /// <summary>Why storage can't be changed right now (files in use), or null.</summary>
    private static string? BusyReason()
    {
        if (InstanceManager.Instances.Any(i => i.IsRunning || i.IsBusy)) return L.T("Close running games and wait for installs to finish first.");
        if (Servers.ServerPackManager.Packs.Any(p => p.IsRunning || p.IsBusy)) return L.T("Stop running servers first.");
        if (DownloadManager.Jobs.Any(j => j.IsActive)) return L.T("Wait for downloads to finish first.");
        return null;
    }

    private async Task ChangeFolder(StorageRole role, string? target)
    {
        if (IsMigrating) return;
        var current = StorageLocations.Get(role);
        target ??= await Ui.Platform.PickFolderAsync(
            role == StorageRole.Instances ? L.T("Choose the instances folder") : L.T("Choose the game files and cache folder"), current);
        if (string.IsNullOrWhiteSpace(target)) return;
        try { target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)); }
        catch { Dialogs.Toast(L.T("That folder path isn't valid."), true); return; }
        if (StorageLocations.SamePath(target, current)) return;

        if (StorageLocations.Validate(role, target) is { } err) { Dialogs.Toast(err, true); return; }
        if (BusyReason() is { } busy) { Dialogs.Toast(busy, true); return; }

        var conflicts = StorageLocations.ConflictingEntries(role, target);
        var hasCurrent = StorageLocations.Subfolders(role).Any(s =>
        {
            var p = Path.Combine(current, s);
            return Directory.Exists(p) && Directory.EnumerateFileSystemEntries(p).Any();
        });

        StorageChangeMode mode;
        if (conflicts.Count > 0 && StorageLocations.HasExistingData(role, target))
        {
            // The folder already holds PyroLauncher data (kept after an uninstall, or used before): offer to pick it up as it is.
            var use = L.T("Use the files in that folder");
            var replace = L.T("Delete them and move my current files there");
            var choice = await Dialogs.ChooseAsync(L.T("This folder already has PyroLauncher data"),
                L.F("{0} already contains PyroLauncher files. Use them as they are, or delete them and move your current files there?", target),
                hasCurrent ? [use, replace] : [use]);
            if (choice == null) return;
            if (choice == use) mode = StorageChangeMode.UseExisting;
            else
            {
                if (!await ConfirmDeletion(target, conflicts)) return;
                mode = StorageChangeMode.Move;
            }
        }
        else
        {
            if (conflicts.Count > 0 && !await ConfirmDeletion(target, conflicts)) return;
            if (hasCurrent)
            {
                var move = L.T("Move my files there");
                var empty = L.T("Start with an empty folder");
                var choice = await Dialogs.ChooseAsync(L.T("Move your files?"),
                    L.F("Move everything from {0} to {1}? The launcher restarts afterwards and works with the files in their new place.\n\nIf you start with an empty folder, your current files stay where they are and are not used.", current, target),
                    [move, empty]);
                if (choice == null) return;
                mode = choice == move ? StorageChangeMode.Move : StorageChangeMode.Empty;
            }
            else mode = StorageChangeMode.Empty;
        }

        // Re-check: something may have started while the dialogs were open.
        if (BusyReason() is { } busy2) { Dialogs.Toast(busy2, true); return; }

        IsMigrating = true;
        MigrationProgress = 0;
        MigrationText = L.T("Preparing the folder…");
        _migrationCts = new CancellationTokenSource();
        var progress = new Progress<(string Message, double Fraction)>(p => { MigrationText = p.Message; MigrationProgress = p.Fraction; });
        try
        {
            await StorageLocations.ChangeAsync(role, target, mode, progress, _migrationCts.Token);
            Log.Info($"Storage for {role} changed to {target} ({mode})");
            MigrationText = L.T("Restarting the launcher…");
            RestartLauncher();
        }
        catch (OperationCanceledException)
        {
            Dialogs.Toast(L.T("Cancelled. Nothing was changed."));
        }
        catch (Exception ex)
        {
            Log.Error("Changing storage folder failed", ex);
            await Dialogs.ConfirmAsync(L.T("Couldn't change the folder"),
                L.F("{0}\n\nThe launcher keeps using {1}.", ex.Message, StorageLocations.Get(role)), L.T("OK"));
        }
        finally
        {
            IsMigrating = false;
            _migrationCts = null;
            OnPropertyChanged(nameof(InstancesFolder));
            OnPropertyChanged(nameof(GameDataFolder));
            OnPropertyChanged(nameof(InstancesIsCustom));
            OnPropertyChanged(nameof(GameDataIsCustom));
        }
    }

    private static Task<bool> ConfirmDeletion(string target, List<string> entries)
    {
        var (files, bytes) = StorageLocations.Measure(entries);
        var names = string.Join("\n", entries.Take(6).Select(e => "• " + Path.GetFileName(e)));
        if (entries.Count > 6) names += "\n" + L.F("…and {0} more", entries.Count - 6);
        return Dialogs.ConfirmAsync(L.T("The folder isn't empty"),
            L.F("{0} already contains {1} ({2}):\n\n{3}\n\nThey will be permanently deleted to make room for PyroLauncher. This can't be undone.",
                target, FileCount(files), DownloadManager.FormatBytes(bytes), names),
            L.T("Delete and continue"), true);
    }

    private static string FileCount(int n) =>
        L.IsRu ? $"{n} {L.Plural(n, "файл", "файла", "файлов")}" : n == 1 ? "1 file" : $"{n} files";

    /// <summary>Starts a new launcher process (which waits for this one to exit) and quits.</summary>
    private static void RestartLauncher()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
                psi.ArgumentList.Add("--wait-for");
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(psi);
            }
        }
        catch (Exception ex) { Log.Error("Restarting the launcher failed", ex); }
        Ui.Platform.MainWindow(WindowAction.Quit);
    }
}
