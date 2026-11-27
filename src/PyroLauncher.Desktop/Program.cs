using Avalonia;

namespace PyroLauncher.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        WaitForPreviousInstance(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>"--wait-for &lt;pid&gt;": a restart (e.g. after moving storage folders) waits until the old process has released its files.</summary>
    private static void WaitForPreviousInstance(string[] args)
    {
        var i = Array.IndexOf(args, "--wait-for");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.WaitForExit(20_000);
        }
        catch { /* already gone */ }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
