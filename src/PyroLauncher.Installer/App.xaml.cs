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
        // --update: started by the launcher itself; update the existing install without asking, then relaunch it.
        var w = new MainWindow(uninstall, e.Args.Contains("--update"));
        MainWindow = w;
        w.Show();
    }
}
