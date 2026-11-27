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
    /// <summary>Launcher data from an earlier install exists: the data folder is fixed here and changed in the launcher (Settings → Storage).</summary>
    private readonly bool _dataLocked;
    private string? _launchDir;
    private bool _busy;
    private readonly List<(InstallerCore.DataCategory cat, CheckBox box, TextBlock desc)> _dataBoxes = [];

    public MainWindow(bool uninstallMode, bool autoUpdate = false)
    {
        Loc.SetLanguage(Loc.SystemLanguage());
        InitializeComponent();
        _installed = InstallerCore.GetInstalled();
        _payload = InstallerCore.HasPayload ? InstallerCore.PayloadVersion() : null;
        _dataLocked = _installed != null || Storage.HasExistingData();
        PathBox.Text = _installed?.Path ?? InstallerCore.DefaultInstallDir;
        DataBox.Text = Storage.InstancesDir;
        BuildLanguageBar();
        BuildDataOptions();
        ApplyTexts();
        Loc.Changed += ApplyTexts;
        if (uninstallMode) ShowPage(UninstallPage);
        else if (autoUpdate && _installed != null && InstallerCore.HasPayload) Loaded += async (_, _) => await AutoUpdateAsync(_installed.Path);
    }

    // ---- Language ----

    private void BuildLanguageBar()
    {
        foreach (var lang in Loc.Languages)
        {
            var b = new Button
            {
                Content = lang.Name, Tag = lang.Code, Style = (Style)FindResource("Ghost"),
                Padding = new Thickness(8, 4, 8, 4), FontSize = 12, Margin = new Thickness(2, 0, 0, 0), Focusable = false,
            };
            b.Click += (_, _) => Loc.SetLanguage(lang.Code);
            LanguageBar.Children.Add(b);
        }
    }

    /// <summary>Texts built in code. XAML texts ({local:T}) update on their own.</summary>
    private void ApplyTexts()
    {
        foreach (Button b in LanguageBar.Children)
        {
            var active = (string)b.Tag == Loc.Language;
            b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            b.Foreground = (System.Windows.Media.Brush)FindResource(active ? "Fg" : "Fg3");
        }

        UninstallInfo.Text = _installed == null
            ? Loc.T("PyroLauncher doesn't appear to be installed.")
            : Loc.F("Version {0} in {1}", _installed.Version.ToString(3), _installed.Path);
        var folders = string.Join(", ", Storage.DataFolders());
        UninstallExplain.Text = Loc.F("The program is always removed. Tick what else to delete from {0} - anything left unticked is kept, so a reinstall picks up where you left off.", folders);
        foreach (var (cat, box, desc) in _dataBoxes)
        {
            box.Content = Loc.T(cat.Title);
            desc.Text = Loc.T(cat.Description);
        }
        DataHint.Text = _dataLocked
            ? Loc.T("Your existing instances and settings are kept. To move instances and game files to another folder, use Settings → Storage in the launcher.")
            : Loc.F("Instances, game files, Java and caches - the large part. Settings and accounts always stay in {0}.", InstallerCore.UserDataDir);
        if (!_busy && ProgressPage.Visibility != Visibility.Visible) SetupMain();
    }

    // ---- Main page ----

    private async Task AutoUpdateAsync(string dir)
    {
        _busy = true;
        CloseX.IsEnabled = false;
        ProgressHeading.Text = Loc.T("Updating…");
        ShowPage(ProgressPage);
        try
        {
            await InstallerCore.InstallAsync(dir, InstallerCore.HasDesktopShortcut, MakeProgress());
            StartLauncher(dir);
            Close();
        }
        catch (Exception ex)
        {
            ProgressHeading.Text = Loc.T("Update failed");
            ProgressText.Text = ex.Message;
            _busy = false;
            CloseX.IsEnabled = true;
            FinishButton.Visibility = Visibility.Visible;
        }
    }

    private void SetupMain()
    {
        UninstallLink.Visibility = _installed != null ? Visibility.Visible : Visibility.Collapsed;
        LaunchButton.Visibility = _installed != null ? Visibility.Visible : Visibility.Collapsed;
        DataBox.IsEnabled = BrowseData.IsEnabled = !_dataLocked;
        ResetData.Visibility = _dataLocked ? Visibility.Collapsed : Visibility.Visible;
        var payload = _payload?.ToString(3);

        if (!InstallerCore.HasPayload)
        {
            Heading.Text = Loc.T("Setup package incomplete");
            SubHeading.Text = Loc.T("This setup was built without the launcher files. Run build.ps1 to create a full setup.");
            PrimaryButton.Visibility = Visibility.Collapsed;
            Options.Visibility = Visibility.Collapsed;
            return;
        }

        if (_installed == null)
        {
            Heading.Text = Loc.T("Install PyroLauncher");
            SubHeading.Text = Loc.F("Version {0} - a Minecraft launcher for modded instances.", payload);
            PrimaryButton.Content = Loc.T("Install");
            return;
        }

        // Already installed: locations are fixed; offer update / repair / downgrade.
        PathBox.IsEnabled = BrowseInstall.IsEnabled = false;
        var cmp = _payload!.CompareTo(_installed.Version);
        if (cmp > 0)
        {
            Heading.Text = Loc.T("Update available");
            SubHeading.Text = Loc.F("Update PyroLauncher from {0} to {1}. Your instances and settings are kept.", _installed.Version.ToString(3), payload);
            PrimaryButton.Content = Loc.T("Update");
        }
        else if (cmp == 0)
        {
            Heading.Text = Loc.T("PyroLauncher is installed");
            SubHeading.Text = Loc.F("Version {0} is already installed. You can repair it by reinstalling the files.", payload);
            PrimaryButton.Content = Loc.T("Repair");
        }
        else
        {
            Heading.Text = Loc.T("A newer version is installed");
            SubHeading.Text = Loc.F("Installed: {0}. This setup contains {1}.", _installed.Version.ToString(3), payload);
            PrimaryButton.Content = Loc.T("Downgrade");
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

    private MessageBoxResult Ask(string text, MessageBoxButton buttons, MessageBoxImage icon = MessageBoxImage.None) =>
        MessageBox.Show(this, text, Loc.T("PyroLauncher Setup"), buttons, icon);

    /// <summary>Lists what a non-empty folder holds and asks to delete it. False: the user said no.</summary>
    private bool ConfirmDeletion(string folder, IReadOnlyList<string> entries)
    {
        var (files, bytes) = Storage.Measure(entries);
        var names = string.Join("\n", entries.Take(6).Select(e => "• " + Path.GetFileName(e)));
        if (entries.Count > 6) names += "\n" + Loc.F("…and {0} more", entries.Count - 6);
        var count = Loc.Language == "ru"
            ? $"{files} {RuPlural(files, "файл", "файла", "файлов")}"
            : files == 1 ? "1 file" : $"{files} files";
        return Ask(Loc.F("{0} isn't empty. It contains {1} ({2}):\n\n{3}\n\nEverything in it will be permanently deleted. This can't be undone.\n\nDelete these files and continue?",
                folder, count, Storage.FormatBytes(bytes), names),
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static string RuPlural(long n, string one, string few, string many)
    {
        n = Math.Abs(n);
        if (n % 10 == 1 && n % 100 != 11) return one;
        if (n % 10 is >= 2 and <= 4 && (n % 100 < 10 || n % 100 >= 20)) return few;
        return many;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var fresh = _installed == null;
        var dir = PathBox.Text.Trim();
        if (dir.Length == 0) return;
        try { dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch { Ask(Loc.T("That install location isn't valid."), MessageBoxButton.OK); return; }

        InstallerCore.DataChoice? data = null;
        if (fresh)
        {
            if (Storage.ValidateFolder(dir) is { } err) { Ask(err, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (Storage.IsInside(InstallerCore.UserDataDir, dir) || Storage.IsInside(dir, InstallerCore.UserDataDir))
            {
                Ask(Loc.T("The install folder must be separate from the launcher's data folder."), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_dataLocked)
            {
                var dataDir = DataBox.Text.Trim();
                if (dataDir.Length == 0) dataDir = InstallerCore.UserDataDir;
                try { dataDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDir))); }
                catch { Ask(Loc.T("That data folder isn't valid."), MessageBoxButton.OK); return; }
                if (Storage.ValidateDataFolder(dataDir, dir) is { } derr) { Ask(derr, MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                // Warn about the install folder first, then the data folder.
                if (Storage.NonEmptyDir(dir) && !ConfirmDeletion(dir, Directory.EnumerateFileSystemEntries(dir).ToList())) return;

                var delete = new List<string>();
                if (!Storage.SamePath(dataDir, InstallerCore.UserDataDir))
                {
                    var foreign = Storage.ForeignEntries(dataDir);
                    if (Storage.HasPyroData(dataDir))
                    {
                        // Data kept from an earlier install: offer to use it as it is.
                        var r = Ask(Loc.F("{0} already has PyroLauncher instances or game files.\n\nUse them? Choose No to delete them and start fresh.", dataDir),
                            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                        if (r == MessageBoxResult.Cancel) return;
                        if (r == MessageBoxResult.No)
                            foreach (var s in Storage.AllSubfolders)
                            {
                                var p = Path.Combine(dataDir, s);
                                if (Directory.Exists(p)) delete.Add(p);
                            }
                    }
                    delete.AddRange(foreign);
                    if (delete.Count > 0 && !ConfirmDeletion(dataDir, delete)) return;
                }
                data = new InstallerCore.DataChoice(dataDir, delete);
            }
            else if (Storage.NonEmptyDir(dir) && !ConfirmDeletion(dir, Directory.EnumerateFileSystemEntries(dir).ToList())) return;
        }

        if (InstallerCore.IsLauncherRunning(dir) &&
            Ask(Loc.T("PyroLauncher is running. Close it and continue?"), MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return;

        _busy = true;
        CloseX.IsEnabled = false;
        ProgressHeading.Text = fresh ? Loc.T("Installing…") : Loc.T("Updating…");
        ShowPage(ProgressPage);
        try
        {
            await InstallerCore.InstallAsync(dir, DesktopShortcut.IsChecked == true, MakeProgress(), data);
            _launchDir = dir;
            ProgressHeading.Text = Loc.T("All set");
            ProgressText.Text = Loc.F("PyroLauncher {0} is installed in {1}.", _payload?.ToString(3), dir);
            if (data != null && !Storage.SamePath(data.Dir, InstallerCore.UserDataDir))
                ProgressText.Text += "\n" + Loc.F("Instances and game files are stored in {0}.", data.Dir);
            LaunchAfter.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ProgressHeading.Text = Loc.T("Setup failed");
            ProgressText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            CloseX.IsEnabled = true;
            FinishButton.Visibility = Visibility.Visible;
        }
    }

    // ---- Uninstall page ----

    private void BuildDataOptions()
    {
        foreach (var cat in InstallerCore.DataCategories)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var box = new CheckBox { IsChecked = false };
            box.Click += (_, _) => SyncSelectAll();
            panel.Children.Add(box);
            var desc = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(27, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("Fg3"),
            };
            panel.Children.Add(desc);
            CategoryList.Children.Add(panel);
            _dataBoxes.Add((cat, box, desc));
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
        foreach (var (_, box, _) in _dataBoxes) box.IsChecked = all;
        SyncSelectAll();
    }

    private List<InstallerCore.DataCategory> SelectedData =>
        _dataBoxes.Where(b => b.box.IsChecked == true).Select(b => b.cat).ToList();

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedData;
        var folders = string.Join("\n", Storage.DataFolders());
        if (selected.Count > 0 &&
            Ask(Loc.F("This permanently deletes:\n\n{0}\n\nfrom\n{1}\n\nContinue?", string.Join("\n", selected.Select(c => "• " + Loc.T(c.Title))), folders),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _busy = true;
        CloseX.IsEnabled = false;
        ProgressHeading.Text = Loc.T("Uninstalling…");
        ShowPage(ProgressPage);
        try
        {
            await InstallerCore.UninstallAsync(selected.Select(c => c.Key).ToList(), MakeProgress());
            ProgressHeading.Text = Loc.T("PyroLauncher was removed");
            var kept = InstallerCore.DataCategories.Where(c => !selected.Contains(c)).Select(c => Loc.T(c.Title).ToLower(Loc.Culture)).ToList();
            ProgressText.Text = kept.Count == 0
                ? Loc.T("The launcher and all of its data were deleted.")
                : selected.Count == 0
                    ? Loc.F("Your data is still in {0}.", folders.Replace("\n", ", "))
                    : Loc.F("Kept: {0} (in {1}).", string.Join(", ", kept), folders.Replace("\n", ", "));
        }
        catch (Exception ex)
        {
            ProgressHeading.Text = Loc.T("Uninstall failed");
            ProgressText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            CloseX.IsEnabled = true;
            FinishButton.Visibility = Visibility.Visible;
        }
    }

    // ---- Buttons ----

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Choose install folder"), InitialDirectory = Path.GetDirectoryName(PathBox.Text) };
        if (dlg.ShowDialog(this) == true)
            PathBox.Text = Path.Combine(dlg.FolderName, InstallerCore.AppName);
    }

    private void BrowseData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Choose the data folder for instances and game files") };
        try { if (Directory.Exists(DataBox.Text)) dlg.InitialDirectory = DataBox.Text; } catch { }
        if (dlg.ShowDialog(this) != true) return;
        // A folder that already has other things in it gets a PyroLauncher subfolder; an empty or PyroLauncher folder is used as it is.
        var picked = dlg.FolderName;
        var useAsIs = !Storage.NonEmptyDir(picked) || Storage.HasPyroData(picked) || Path.GetFileName(picked).Contains(InstallerCore.AppName, StringComparison.OrdinalIgnoreCase);
        DataBox.Text = useAsIs ? picked : Path.Combine(picked, InstallerCore.AppName);
    }

    private void ResetData_Click(object sender, RoutedEventArgs e) => DataBox.Text = InstallerCore.UserDataDir;

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
