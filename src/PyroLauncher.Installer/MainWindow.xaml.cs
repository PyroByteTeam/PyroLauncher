using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PyroLauncher.Installer;

public partial class MainWindow : Window
{
    private readonly InstalledInfo? _installed;
    private readonly Version? _payload;
    private string? _launchDir;
    private bool _busy;

    public MainWindow(bool uninstallMode)
    {
        InitializeComponent();
        _installed = InstallerCore.GetInstalled();
        _payload = InstallerCore.HasPayload ? InstallerCore.PayloadVersion() : null;
        PathBox.Text = _installed?.Path ?? InstallerCore.DefaultInstallDir;
        DataPath.Text = InstallerCore.UserDataDir;
        UninstallInfo.Text = _installed == null
            ? "PyroLauncher doesn't appear to be installed."
            : $"Version {_installed.Version.ToString(3)} in {_installed.Path}";
        BuildDataOptions();
        SetupMain();
        if (uninstallMode) ShowPage(UninstallPage);
    }

    private void SetupMain()
    {
        UninstallLink.Visibility = _installed != null ? Visibility.Visible : Visibility.Collapsed;
        LaunchButton.Visibility = _installed != null ? Visibility.Visible : Visibility.Collapsed;
        var payload = _payload?.ToString(3);

        if (!InstallerCore.HasPayload)
        {
            Heading.Text = "Setup package incomplete";
            SubHeading.Text = "This setup was built without the launcher files. Run build-installer.ps1 to create a full setup.";
            PrimaryButton.Visibility = Visibility.Collapsed;
            Options.Visibility = Visibility.Collapsed;
            return;
        }

        if (_installed == null)
        {
            Heading.Text = "Install PyroLauncher";
            SubHeading.Text = $"Version {payload} — a Minecraft launcher for modded instances.";
            PrimaryButton.Content = "Install";
            return;
        }

        // Already installed: location is fixed; offer update / repair / downgrade.
        PathBox.IsEnabled = false;
        var cmp = _payload!.CompareTo(_installed.Version);
        if (cmp > 0)
        {
            Heading.Text = "Update available";
            SubHeading.Text = $"Update PyroLauncher from {_installed.Version.ToString(3)} to {payload}. Your instances and settings are kept.";
            PrimaryButton.Content = "Update";
        }
        else if (cmp == 0)
        {
            Heading.Text = "PyroLauncher is installed";
            SubHeading.Text = $"Version {payload} is already installed. You can repair it by reinstalling the files.";
            PrimaryButton.Content = "Repair";
        }
        else
        {
            Heading.Text = "A newer version is installed";
            SubHeading.Text = $"Installed: {_installed.Version.ToString(3)}. This setup contains {payload}.";
            PrimaryButton.Content = "Downgrade";
        }
    }

    private void ShowPage(FrameworkElement page)
    {
        MainPage.Visibility = page == MainPage ? Visibility.Visible : Visibility.Collapsed;
        UninstallPage.Visibility = page == UninstallPage ? Visibility.Visible : Visibility.Collapsed;
        ProgressPage.Visibility = page == ProgressPage ? Visibility.Visible : Visibility.Collapsed;
    }

    private Progress<(string msg, double pct)> MakeProgress() => new(p =>
    {
        ProgressText.Text = p.msg;
        Bar.Value = p.pct;
    });

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var dir = PathBox.Text.Trim();
        if (dir.Length == 0) return;
        try { dir = Path.GetFullPath(dir); }
        catch { MessageBox.Show(this, "That install location isn't valid.", "PyroLauncher Setup"); return; }

        if (InstallerCore.IsLauncherRunning(dir) &&
            MessageBox.Show(this, "PyroLauncher is running. Close it and continue?", "PyroLauncher Setup", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return;

        _busy = true;
        CloseX.IsEnabled = false;
        ProgressHeading.Text = PrimaryButton.Content is "Install" ? "Installing…" : "Updating…";
        ShowPage(ProgressPage);
        try
        {
            await InstallerCore.InstallAsync(dir, DesktopShortcut.IsChecked == true, MakeProgress());
            _launchDir = dir;
            ProgressHeading.Text = "All set";
            ProgressText.Text = $"PyroLauncher {_payload?.ToString(3)} is installed in {dir}.";
            LaunchAfter.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ProgressHeading.Text = "Setup failed";
            ProgressText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            CloseX.IsEnabled = true;
            FinishButton.Visibility = Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------ data options

    private readonly List<(InstallerCore.DataCategory cat, CheckBox box)> _dataBoxes = [];

    private void BuildDataOptions()
    {
        foreach (var cat in InstallerCore.DataCategories)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var box = new CheckBox { Content = cat.Title, IsChecked = false };
            box.Click += (_, _) => SyncSelectAll();
            panel.Children.Add(box);
            panel.Children.Add(new TextBlock
            {
                Text = cat.Description,
                FontSize = 12,
                Margin = new Thickness(27, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("Fg3"),
            });
            CategoryList.Children.Add(panel);
            _dataBoxes.Add((cat, box));
        }
        SyncSelectAll();
    }

    /// <summary>"Delete everything" is ticked when all are, dashed when some are, empty when none are.</summary>
    private void SyncSelectAll()
    {
        var n = _dataBoxes.Count(b => b.box.IsChecked == true);
        SelectAll.IsChecked = n == 0 ? false : n == _dataBoxes.Count ? true : null;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        // Clicking cycles through the three states; treat "partial" as "tick everything".
        var all = _dataBoxes.Any(b => b.box.IsChecked != true);
        foreach (var (_, box) in _dataBoxes) box.IsChecked = all;
        SyncSelectAll();
    }

    private List<InstallerCore.DataCategory> SelectedData =>
        _dataBoxes.Where(b => b.box.IsChecked == true).Select(b => b.cat).ToList();

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedData;
        if (selected.Count > 0 &&
            MessageBox.Show(this, "This permanently deletes:\n\n" + string.Join("\n", selected.Select(c => "• " + c.Title)) +
                                  "\n\nfrom " + InstallerCore.UserDataDir + "\n\nContinue?",
                "PyroLauncher Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _busy = true;
        CloseX.IsEnabled = false;
        ProgressHeading.Text = "Uninstalling…";
        ShowPage(ProgressPage);
        try
        {
            await InstallerCore.UninstallAsync(selected.Select(c => c.Key).ToList(), MakeProgress());
            ProgressHeading.Text = "PyroLauncher was removed";
            var kept = InstallerCore.DataCategories.Where(c => !selected.Contains(c)).Select(c => c.Title.ToLowerInvariant()).ToList();
            ProgressText.Text = kept.Count == 0
                ? "The launcher and all of its data were deleted."
                : selected.Count == 0
                    ? $"Your data is still in {InstallerCore.UserDataDir}."
                    : $"Kept: {string.Join(", ", kept)} (in {InstallerCore.UserDataDir}).";
        }
        catch (Exception ex)
        {
            ProgressHeading.Text = "Uninstall failed";
            ProgressText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            CloseX.IsEnabled = true;
            FinishButton.Visibility = Visibility.Visible;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose install folder", InitialDirectory = Path.GetDirectoryName(PathBox.Text) };
        if (dlg.ShowDialog(this) == true)
            PathBox.Text = Path.Combine(dlg.FolderName, InstallerCore.AppName);
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_installed != null) StartLauncher(_installed.Path);
        Close();
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_launchDir != null && LaunchAfter.IsChecked == true) StartLauncher(_launchDir);
        Close();
    }

    private static void StartLauncher(string dir)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(dir, InstallerCore.ExeName)) { UseShellExecute = true, WorkingDirectory = dir }); }
        catch { }
    }

    private void ShowUninstall_Click(object sender, RoutedEventArgs e) => ShowPage(UninstallPage);
    private void Back_Click(object sender, RoutedEventArgs e) => ShowPage(MainPage);
    private void Close_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }
}
