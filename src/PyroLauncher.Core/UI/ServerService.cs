using System.IO;
using PyroLauncher.Core;
using PyroLauncher.Servers;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.UI;

/// <summary>Install / start / stop flow of server packs, shared by the Servers page, the server page and instance buttons.</summary>
public static class ServerService
{
    public static async Task<bool> InstallAsync(ServerPack pack)
    {
        if (pack.IsBusy || pack.IsRunning) return false;
        pack.IsBusy = true;
        var cts = new CancellationTokenSource();
        var progress = new Progress<TaskProgress>(p => { pack.Status = p.Message; pack.Progress = p.Fraction; });
        var job = DownloadManager.Begin(L.F("{0} - installing server", pack.Name), cts);
        try
        {
            await ServerInstaller.InstallAsync(pack, progress, cts.Token);
            return true;
        }
        catch (OperationCanceledException) { job.Fail(); return false; }
        catch (Exception ex)
        {
            job.Fail();
            Log.Error($"Installing server {pack.Name} failed", ex);
            pack.Instance.AppendLog("[PyroLauncher] Install failed: " + ex.Message);
            Dialogs.Toast(L.F("Server install failed: {0}", ex.Message), true);
            return false;
        }
        finally
        {
            job.Dispose();
            pack.IsBusy = false;
            pack.Status = null;
            pack.Progress = -1;
        }
    }

    /// <summary>Asks for memory and the EULA the first time. Returns false when the user cancels.</summary>
    public static async Task<bool> SetupAsync(ServerPack pack)
    {
        var vm = new ServerSetupViewModel(pack);
        Dialogs.Show(vm);
        return await vm.Result.Task;
    }

    public static async Task StartAsync(ServerPack pack)
    {
        if (pack.IsRunning || pack.IsBusy) return;
        if (!pack.IsInstalled && !await InstallAsync(pack)) return;
        if (pack.NeedsSetup && !await SetupAsync(pack)) return;
        WriteEula(pack.ServerDir);
        try
        {
            pack.IsBusy = true;
            pack.Status = L.T("Preparing Java…");
            var java = await ServerInstaller.FindJavaAsync(pack, null, CancellationToken.None);
            pack.IsBusy = false;
            pack.Status = null;
            pack.Start(java);
        }
        catch (Exception ex)
        {
            pack.IsBusy = false;
            pack.Status = null;
            Log.Error($"Starting server {pack.Name} failed", ex);
            Dialogs.Toast(L.F("Couldn't start the server: {0}", ex.Message), true);
        }
    }

    public static async Task ToggleAsync(ServerPack pack)
    {
        if (pack.IsRunning) await pack.StopAsync();
        else await StartAsync(pack);
    }

    public static void WriteEula(string serverDir)
    {
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(Path.Combine(serverDir, "eula.txt"),
            $"#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).\n#{DateTime.Now:R}\neula=true\n");
    }

    public static void Open(ServerPack pack, string? tab = null) => MainViewModel.Instance.OpenServerPack(pack, tab);
}
