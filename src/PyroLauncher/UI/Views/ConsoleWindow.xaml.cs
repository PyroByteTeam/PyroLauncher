using System.IO;
using System.Windows;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.Views;

/// <summary>Live game console, one window per instance.</summary>
public partial class ConsoleWindow : Window
{
    private static readonly Dictionary<Instance, ConsoleWindow> Open = [];
    private readonly Instance _inst;

    private ConsoleWindow(Instance inst)
    {
        _inst = inst;
        InitializeComponent();
        DataContext = inst;
        Title = $"{inst.Name} - Console";
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            MaxButton.ToolTip = WindowState == WindowState.Maximized ? L.T("Restore") : L.T("Maximize");
        };
        Closed += (_, _) => Open.Remove(_inst);
    }

    public static void ShowFor(Instance inst, bool activate = true)
    {
        if (!Open.TryGetValue(inst, out var w))
        {
            w = new ConsoleWindow(inst) { Owner = null };
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

    private void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => Viewer.Filter = FilterBox.Text;
    private void Copy_Click(object sender, RoutedEventArgs e) => Viewer.CopySelected();
    private void Clear_Click(object sender, RoutedEventArgs e) => _inst.ClearLog();
    private void Kill_Click(object sender, RoutedEventArgs e) => LaunchService.Stop(_inst);

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(_inst.GameDir, "logs");
        Dialogs.OpenPath(Directory.Exists(dir) ? dir : _inst.GameDir);
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
