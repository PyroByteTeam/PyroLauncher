using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PyroLauncher.Accounts;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.UI;

namespace PyroLauncher.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Ui.Platform = new AvaloniaPlatform();
        AppPaths.Ensure();
        Log.Info($"PyroLauncher {Http.AppVersion} starting on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            Dialogs.Toast(L.F("Unexpected error: {0}", e.Exception.Message), true);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        SettingsService.Load();
        Loc.Instance.SetLanguage(SettingsService.Current.Language);
        ThemeService.Init();
        DownloadManager.Init();
        AccountManager.Load();
        InstanceManager.LoadAll();
        Servers.ServerPackManager.LoadAll();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Closed += (_, _) =>
            {
                Views.ConsoleWindow.CloseAll();
                Servers.ServerPackManager.StopAll();
            };
            window.Opened += (_, _) => UI.ViewModels.MainViewModel.Instance.CheckForUpdatesOnStartup();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
