using System.Windows;

namespace PyroLauncher.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool uninstall = e.Args.Contains("--uninstall");
        // Uninstalling from inside the install folder: hand over to a temp copy so the folder can be removed.
        if (uninstall && !e.Args.Contains("--from-temp") && InstallerCore.RelaunchFromTempIfNeeded(e.Args))
        {
            Shutdown();
            return;
        }
        var w = new MainWindow(uninstall);
        MainWindow = w;
        w.Show();
    }
}
