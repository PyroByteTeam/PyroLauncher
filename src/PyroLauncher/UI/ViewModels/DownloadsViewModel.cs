using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Core;

namespace PyroLauncher.UI.ViewModels;

public partial class DownloadsViewModel : ObservableObject
{
    public ObservableCollection<DownloadJob> Jobs => DownloadManager.Jobs;
    public ObservableCollection<HostStat> Hosts => DownloadManager.Hosts;
    public DownloadSummary Summary => DownloadManager.Summary;

    [RelayCommand] private void Pause(DownloadJob j) => j.Pause();
    [RelayCommand] private void Resume(DownloadJob j) => j.Resume();
    [RelayCommand] private void Cancel(DownloadJob j) => j.Cancel();
    [RelayCommand] private void PauseAll() => DownloadManager.PauseAll();
    [RelayCommand] private void ResumeAll() => DownloadManager.ResumeAll();
    [RelayCommand] private void ClearFinished() => DownloadManager.ClearFinished();
}
