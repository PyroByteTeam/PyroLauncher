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
    [RelayCommand] private void CancelAll() => DownloadManager.CancelAll();
    [RelayCommand] private void ClearFinished() => DownloadManager.ClearFinished();

    [RelayCommand]
    private void CopyHost(HostStat h)
    {
        try { System.Windows.Clipboard.SetText(h.Host); Dialogs.Toast(L.F("Copied {0}", h.Host)); }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }

    [RelayCommand]
    private async Task ClearHosts()
    {
        if (Hosts.Count == 0) return;
        if (!await Dialogs.ConfirmAsync(L.T("Clear connections"),
                L.F("Are you sure? The list of {0} contacted domains and their statistics will be cleared.", Hosts.Count), L.T("Clear"), true))
            return;
        DownloadManager.ClearHosts();
    }

    [RelayCommand]
    private void CopyAllHosts()
    {
        if (Hosts.Count == 0) return;
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Hosts.Select(x => x.Host))); Dialogs.Toast(L.F("Copied {0} domains", Hosts.Count)); }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }
}
