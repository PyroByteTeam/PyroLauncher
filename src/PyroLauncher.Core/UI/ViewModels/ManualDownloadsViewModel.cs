using PyroLauncher.Core;
using System.Collections.ObjectModel;
using System.IO;
using PyroLauncher.Content;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

public partial class ManualDownloadItem(ManualDownload d) : ObservableObject
{
    public ManualDownload Download { get; } = d;
    public string Title => Download.Title;
    public string FileName => Download.FileName;
    [ObservableProperty] private bool _isDone;
}

/// <summary>Lists CurseForge files that have to be downloaded by hand and helps put them in place.</summary>
public partial class ManualDownloadsViewModel : ObservableObject
{
    public Instance Instance { get; }
    public ObservableCollection<ManualDownloadItem> Items { get; } = [];
    [ObservableProperty] private string _summary = "";

    private static string DownloadsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private FileSystemWatcher? _watcher;

    public ManualDownloadsViewModel(Instance inst, IEnumerable<ManualDownload> items)
    {
        Instance = inst;
        foreach (var d in items) Items.Add(new ManualDownloadItem(d));
        Collect();
        try
        {
            // Pick files up automatically as the browser finishes downloading them.
            _watcher = new FileSystemWatcher(DownloadsDir) { EnableRaisingEvents = true };
            _watcher.Created += (_, _) => Later();
            _watcher.Renamed += (_, _) => Later();
            _watcher.Changed += (_, _) => Later();
        }
        catch { _watcher = null; }
    }

    private void Later() => Ui.Post(async () =>
    {
        await Task.Delay(800);
        Collect();
    });

    private void UpdateSummary()
    {
        var left = Items.Count(i => !i.IsDone);
        Summary = left == 0
            ? L.T("All files are in place. You can close this window.")
            : L.F("{0} of {1} files still need to be downloaded.", left, Items.Count);
    }

    /// <summary>Moves matching files from the Downloads folder (or ones already present) into the instance.</summary>
    [RelayCommand]
    private void Collect()
    {
        foreach (var item in Items.Where(i => !i.IsDone))
        {
            var dest = Path.Combine(Instance.GameDir, item.Download.Folder, item.FileName);
            if (IsComplete(dest)) { item.IsDone = true; continue; }
            var src = Path.Combine(DownloadsDir, item.FileName);
            try
            {
                // Browsers create an empty placeholder under the final name when a download starts.
                if (!IsComplete(src)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(src, dest, true);
                item.IsDone = true;
            }
            catch { /* still being written; try again later */ }
        }
        UpdateSummary();
    }

    /// <summary>True when the file exists, isn't empty and no one is still writing to it.</summary>
    private static bool IsComplete(string path)
    {
        try
        {
            if (new FileInfo(path) is not { Exists: true, Length: > 0 }) return false;
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch { return false; }
    }

    [RelayCommand]
    private void OpenAll()
    {
        foreach (var i in Items.Where(i => !i.IsDone)) Dialogs.OpenUrl(i.Download.Url);
    }

    [RelayCommand] private void Open(ManualDownloadItem i) => Dialogs.OpenUrl(i.Download.Url);

    [RelayCommand]
    private void OpenFolder()
    {
        var dir = Path.Combine(Instance.GameDir, "mods");
        Directory.CreateDirectory(dir);
        Dialogs.OpenPath(dir);
    }

    [RelayCommand]
    private void Close()
    {
        _watcher?.Dispose();
        _watcher = null;
        Dialogs.Close(this);
    }
}
