using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PyroLauncher.Core;
using PyroLauncher.Minecraft;

namespace PyroLauncher.UI.ViewModels;

public record LaunchBehaviorOption(LaunchBehavior Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Unified launcher settings: "Launcher" section and "Minecraft" defaults applied to every instance.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private static LauncherSettings S => SettingsService.Current;
    private static GameSettings G => S.Game;
    private readonly DispatcherTimer _saveTimer;

    [ObservableProperty] private string _section = "launcher";
    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = [];
    [ObservableProperty] private string? _javaStatus;
    [ObservableProperty] private bool _installingJava;

    public List<LaunchBehaviorOption> LaunchOptions { get; } =
    [
        new(LaunchBehavior.KeepOpen, "Keep the launcher open"),
        new(LaunchBehavior.Minimize, "Minimize the launcher"),
        new(LaunchBehavior.Close, "Hide the launcher until the game closes"),
    ];

    public SettingsViewModel()
    {
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SettingsService.Save(); };
        RefreshJava();
    }

    private void Changed([CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public string DataFolder => AppPaths.Root;
    public string SystemMemory
    {
        get
        {
            var info = GC.GetGCMemoryInfo();
            return $"System memory: {info.TotalAvailableMemoryBytes / 1048576:N0} MB";
        }
    }

    // ---------------- Launcher
    public string CurseForgeApiKey { get => S.CurseForgeApiKey; set { S.CurseForgeApiKey = value; Changed(); } }
    public bool OpenConsoleOnLaunch { get => S.OpenConsoleOnLaunch; set { S.OpenConsoleOnLaunch = value; Changed(); SettingsService.NotifyAppearance(); } }
    public bool DeveloperMode
    {
        get => S.DeveloperMode;
        set { S.DeveloperMode = value; Changed(); SettingsService.NotifyAppearance(); Accounts.AccountManager.NotifyDeveloperModeChanged(); }
    }

    // ---------------- Appearance
    public List<ContentListStyle> ListStyles { get; } = [ContentListStyle.List, ContentListStyle.Grid];
    public List<InstanceMenuStyle> MenuStyles { get; } = [InstanceMenuStyle.TopNavigation, InstanceMenuStyle.Grid];
    public ContentListStyle ContentListStyle { get => S.ContentListStyle; set { S.ContentListStyle = value; Changed(); SettingsService.NotifyAppearance(); } }
    public InstanceMenuStyle InstanceMenuStyle { get => S.InstanceMenuStyle; set { S.InstanceMenuStyle = value; Changed(); SettingsService.NotifyAppearance(); } }

    public int ConcurrentDownloads { get => S.ConcurrentDownloads; set { S.ConcurrentDownloads = Math.Clamp(value, 1, 64); Changed(); } }
    public LaunchBehaviorOption OnLaunch
    {
        get => LaunchOptions.First(o => o.Value == S.OnLaunch);
        set { if (value != null) { S.OnLaunch = value.Value; Changed(); } }
    }

    // ---------------- Minecraft defaults
    public string JavaPath { get => G.JavaPath ?? ""; set { G.JavaPath = string.IsNullOrWhiteSpace(value) ? null : value; Changed(); } }
    public int? MinMemory { get => G.MinMemoryMb; set { G.MinMemoryMb = value ?? 512; Changed(); OnPropertyChanged(nameof(MinAboveMax)); } }
    public bool MinAboveMax => (G.MinMemoryMb ?? 0) > (G.MaxMemoryMb ?? int.MaxValue);
    public int? MaxMemory { get => G.MaxMemoryMb; set { G.MaxMemoryMb = value ?? 4096; Changed(); OnPropertyChanged(nameof(MinAboveMax)); } }
    public string JvmArgs { get => G.JvmArgs ?? ""; set { G.JvmArgs = value; Changed(); } }
    public int? Width { get => G.WindowWidth; set { G.WindowWidth = value ?? 854; Changed(); } }
    public int? Height { get => G.WindowHeight; set { G.WindowHeight = value ?? 480; Changed(); } }
    public bool Fullscreen { get => G.Fullscreen == true; set { G.Fullscreen = value; Changed(); } }

    [RelayCommand]
    private void ResetJvmArgs() => JvmArgs = GameSettings.Defaults().JvmArgs!;

    [RelayCommand]
    private void BrowseJava()
    {
        var dlg = new OpenFileDialog { Title = "Select javaw.exe", Filter = "Java (javaw.exe;java.exe)|javaw.exe;java.exe" };
        if (dlg.ShowDialog() == true) JavaPath = dlg.FileName;
    }

    [RelayCommand]
    private void OpenDataFolder() => Dialogs.OpenPath(AppPaths.Root);

    [RelayCommand]
    private void OpenUrl(string url) => Dialogs.OpenUrl(url);

    private void RefreshJava()
    {
        JavaRuntimes.Clear();
        foreach (var j in JavaManager.ListInstalled()) JavaRuntimes.Add(j);
    }

    [RelayCommand]
    private async Task InstallJava(string major)
    {
        if (!int.TryParse(major, out var m)) return;
        InstallingJava = true;
        var progress = new Progress<TaskProgress>(p => JavaStatus = p.Message);
        using var job = DownloadManager.Begin($"Java {m} (Adoptium)");
        try
        {
            await JavaManager.EnsureAsync(m, progress, CancellationToken.None);
            Dialogs.Toast($"Java {m} is ready");
        }
        catch (Exception ex) { Dialogs.Toast($"Java {m} install failed: {ex.Message}", true); }
        finally
        {
            InstallingJava = false;
            JavaStatus = null;
            RefreshJava();
        }
    }

    [RelayCommand]
    private async Task RemoveJava(JavaRuntime rt)
    {
        if (!await Dialogs.ConfirmAsync("Remove Java", $"Remove Java {rt.Major}? It will be downloaded again when an instance needs it.", "Remove", true)) return;
        try { JavaManager.Remove(rt.Major); }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
        RefreshJava();
    }
}
