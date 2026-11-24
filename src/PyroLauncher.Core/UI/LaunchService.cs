using PyroLauncher.Accounts;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;
using PyroLauncher.UI.ViewModels;

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
            Dialogs.Toast(L.T("Add an account before playing."), true);
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

        if (SettingsService.Current.OpenConsoleOnLaunch) Ui.Platform.ShowConsole(inst, false);

        var job = DownloadManager.Begin($"{inst.Name} - game files", cts);
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
            if (behavior == LaunchBehavior.Minimize) Ui.Platform.MainWindow(WindowAction.Minimize);
            else if (behavior == LaunchBehavior.Close) Ui.Platform.MainWindow(WindowAction.Hide);

            proc.Exited += (_, _) => Ui.Post(() =>
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

                if (behavior != LaunchBehavior.KeepOpen) Ui.Platform.MainWindow(WindowAction.Restore);

                bool killed = Killed.Remove(inst);
                if (code != 0 && !killed)
                {
                    Dialogs.Toast(L.F("{0} crashed (exit code {1}). See the logs for details.", inst.Name, code), true);
                    if (SettingsService.Current.OpenConsoleOnLaunch) Ui.Platform.ShowConsole(inst, true);
                    else MainViewModel.Instance.OpenInstance(inst, "logs");
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
            Dialogs.Toast(L.F("Launch failed: {0}", ex.Message), true);
        }
        finally
        {
            inst.IsBusy = false;
            inst.Progress = -1;
            inst.LaunchCts = null;
        }
    }

    /// <summary>Downloads the game, loader and Java without launching. Used by the Install button.</summary>
    public static async Task InstallAsync(Instance inst)
    {
        if (inst.IsRunning || inst.IsBusy) return;
        inst.IsBusy = true;
        inst.Progress = -1;
        var cts = new CancellationTokenSource();
        inst.LaunchCts = cts;
        var progress = new Progress<TaskProgress>(p =>
        {
            inst.Status = p.Message;
            inst.Progress = p.Fraction;
        });
        var job = DownloadManager.Begin(L.F("{0} - installing", inst.Name), cts);
        try
        {
            inst.AppendLog($"[PyroLauncher] Installing {inst.Name} ({inst.VersionText})");
            await GameLauncher.InstallAsync(inst, progress, cts.Token);
            inst.AppendLog("[PyroLauncher] Install finished");
            Dialogs.Toast(L.F("{0} is installed and ready to play", inst.Name));
        }
        catch (OperationCanceledException)
        {
            inst.AppendLog("[PyroLauncher] Install cancelled");
        }
        catch (Exception ex)
        {
            job.Fail();
            Log.Error($"Install of {inst.Name} failed", ex);
            inst.AppendLog("[PyroLauncher] Install failed: " + ex);
            Dialogs.Toast(L.F("Install failed: {0}", ex.Message), true);
        }
        finally
        {
            job.Dispose();
            inst.IsBusy = false;
            inst.Status = null;
            inst.Progress = -1;
            inst.LaunchCts = null;
        }
    }

    /// <summary>Play when installed, otherwise Install; Stop/Cancel while running or busy.</summary>
    public static Task PrimaryActionAsync(Instance inst)
    {
        if (inst.IsBusy || inst.IsRunning) { Stop(inst); return Task.CompletedTask; }
        return inst.IsInstalled ? LaunchAsync(inst) : InstallAsync(inst);
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
