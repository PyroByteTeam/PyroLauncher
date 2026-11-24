using System.Windows;
using System.Windows.Threading;
using PyroLauncher.Accounts;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.UI;

namespace PyroLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.Ensure();
        Log.Info($"PyroLauncher {Http.AppVersion} starting");

        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        SettingsService.Load();
        UI.Loc.Instance.SetLanguage(SettingsService.Current.Language);
        DownloadManager.Init();
        AccountManager.Load();
        InstanceManager.LoadAll();

        var window = new MainWindow();
        MainWindow = window;
        window.Closed += (_, _) => UI.Views.ConsoleWindow.CloseAll();
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        Dialogs.Toast(L.F("Unexpected error: {0}", e.Exception.Message), true);
        e.Handled = true;
    }
}
