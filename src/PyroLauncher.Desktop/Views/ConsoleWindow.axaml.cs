using Avalonia.Controls;
using Avalonia.Interactivity;
using PyroLauncher.Instances;
using PyroLauncher.UI;

namespace PyroLauncher.Desktop.Views;

/// <summary>Live game console, one window per instance.</summary>
public partial class ConsoleWindow : Window
{
    private static readonly Dictionary<Instance, ConsoleWindow> Open = [];
    private readonly Instance _inst = null!;

    public ConsoleWindow() => InitializeComponent();

    private ConsoleWindow(Instance inst) : this()
    {
        _inst = inst;
        DataContext = inst;
        Title = $"{inst.Name} - {L.T("Console")}";
        Closed += (_, _) => Open.Remove(_inst);
    }

    public static void ShowFor(Instance inst, bool activate = true)
    {
        if (!Open.TryGetValue(inst, out var w))
        {
            w = new ConsoleWindow(inst);
            Open[inst] = w;
            w.Show();
        }
        else if (activate)
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        }
    }

    public static void CloseAll()
    {
        foreach (var w in Open.Values.ToList()) w.Close();
    }

    private void Copy_Click(object? sender, RoutedEventArgs e) => Viewer.CopySelected();
    private void Clear_Click(object? sender, RoutedEventArgs e) => _inst.ClearLog();
    private void Kill_Click(object? sender, RoutedEventArgs e) => LaunchService.Stop(_inst);

    private void Folder_Click(object? sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(_inst.GameDir, "logs");
        Dialogs.OpenPath(Directory.Exists(dir) ? dir : _inst.GameDir);
    }
}
