using System.Windows;
using PyroLauncher.Accounts;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;
using PyroLauncher.UI.ViewModels;
using PyroLauncher.UI.Views;

namespace PyroLauncher.UI;

public static class LaunchService
{
    private static readonly HashSet<Instance> Killed = [];

    public static async Task LaunchAsync(Instance inst)
    {
        if (inst.IsRunning || inst.IsBusy) return;
        var account = AccountManager.Selected;
        if (account == null)
        {
            Dialogs.Toast("Add an account before playing.", true);
            MainViewModel.Instance.Nav = "profile";
            return;
        }

        inst.IsBusy = true;
        inst.ClearLog();
        inst.Progress = -1;
        var cts = new CancellationTokenSource();
        inst.LaunchCts = cts;
        var progress = new Progress<TaskProgress>(p =>
        {
            inst.Status = p.Message;
            inst.Progress = p.Fraction;
        });

        if (SettingsService.Current.OpenConsoleOnLaunch) ConsoleWindow.ShowFor(inst, activate: false);

        var job = DownloadManager.Begin($"{inst.Name} — game files", cts);
        try
        {
            System.Diagnostics.Process proc;
            try { proc = await GameLauncher.LaunchAsync(inst, account, progress, cts.Token); }
            catch { job.Fail(); throw; }
            finally { job.Dispose(); }

            inst.Process = proc;
            inst.IsRunning = true;
            inst.Status = null;
            var started = DateTime.Now;
            inst.SessionStart = started;
            inst.Config.LastPlayed = started;
            inst.Save();
            inst.Refresh();

            var behavior = SettingsService.Current.OnLaunch;
            var window = Application.Current.MainWindow;
            if (behavior == LaunchBehavior.Minimize) window.WindowState = WindowState.Minimized;
            else if (behavior == LaunchBehavior.Close) window.Hide();

            proc.Exited += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                int code = 0;
                try { code = proc.ExitCode; } catch { }
                inst.IsRunning = false;
                inst.Process = null;
                inst.Status = null;
                inst.SessionStart = null;
                inst.Config.PlayTimeSeconds += (long)(DateTime.Now - started).TotalSeconds;
                inst.Config.LastPlayed = DateTime.Now;
                inst.Save();
                inst.Refresh();
                inst.AppendLog($"[PyroLauncher] Game exited with code {code}");

                if (behavior == LaunchBehavior.Close) { window.Show(); window.Activate(); }
                else if (behavior == LaunchBehavior.Minimize && window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;

                bool killed = Killed.Remove(inst);
                if (code != 0 && !killed)
                {
                    Dialogs.Toast($"{inst.Name} crashed (exit code {code}). See the console for details.", true);
                    ConsoleWindow.ShowFor(inst);
                }
            });
        }
        catch (OperationCanceledException)
        {
            inst.Status = null;
            inst.AppendLog("[PyroLauncher] Launch cancelled");
        }
        catch (Exception ex)
        {
            Log.Error($"Launch of {inst.Name} failed", ex);
            inst.AppendLog("[PyroLauncher] Launch failed: " + ex);
            inst.Status = null;
            Dialogs.Toast($"Launch failed: {ex.Message}", true);
        }
        finally
        {
            inst.IsBusy = false;
            inst.Progress = -1;
            inst.LaunchCts = null;
        }
    }

    public static void Stop(Instance inst)
    {
        if (inst.IsBusy) inst.LaunchCts?.Cancel();
        if (inst.IsRunning)
        {
            Killed.Add(inst);
            try { inst.Process?.Kill(true); }
            catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
        }
    }
}
