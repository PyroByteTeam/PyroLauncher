using System.IO;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public ThemesViewModel Themes { get; } = new();
    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = [];
    [ObservableProperty] private string? _javaStatus;
    [ObservableProperty] private bool _installingJava;

    public List<LaunchBehaviorOption> LaunchOptions { get; } =
    [
        new(LaunchBehavior.KeepOpen, L.T("Keep the launcher open")),
        new(LaunchBehavior.Minimize, L.T("Minimize the launcher")),
        new(LaunchBehavior.Close, L.T("Hide the launcher until the game closes")),
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
            return L.F("System memory: {0:N0} MB", info.TotalAvailableMemoryBytes / 1048576);
        }
    }

    public string CurseForgeApiKey { get => S.CurseForgeApiKey; set { S.CurseForgeApiKey = value; Changed(); } }
    public bool OpenConsoleOnLaunch { get => S.OpenConsoleOnLaunch; set { S.OpenConsoleOnLaunch = value; Changed(); SettingsService.NotifyAppearance(); } }
    /// <summary>Used when the launcher starts; F11 switches fullscreen at any time.</summary>
    public bool StartFullscreen { get => S.StartFullscreen; set { S.StartFullscreen = value; Changed(); } }
    public bool DeveloperMode
    {
        get => S.DeveloperMode;
        set { S.DeveloperMode = value; Changed(); SettingsService.NotifyAppearance(); Accounts.AccountManager.NotifyDeveloperModeChanged(); }
    }

    public List<ContentListStyle> ListStyles { get; } = [ContentListStyle.List, ContentListStyle.Grid];
    public List<InstanceMenuStyle> MenuStyles { get; } = [InstanceMenuStyle.TopNavigation, InstanceMenuStyle.Grid];
    public ContentListStyle ContentListStyle { get => S.ContentListStyle; set { S.ContentListStyle = value; Changed(); SettingsService.NotifyAppearance(); } }
    public InstanceMenuStyle InstanceMenuStyle { get => S.InstanceMenuStyle; set { S.InstanceMenuStyle = value; Changed(); SettingsService.NotifyAppearance(); } }
    public ContentListStyle DiscoverStyle { get => S.DiscoverStyle; set { S.DiscoverStyle = value; Changed(); SettingsService.NotifyAppearance(); } }
    public ContentListStyle ServerListStyle { get => S.ServerListStyle; set { S.ServerListStyle = value; Changed(); SettingsService.NotifyAppearance(); } }

    public List<Choice<string>> LanguageOptions { get; } =
        [new("auto", L.T("Automatic (system)")), .. Loc.Languages.Select(l => new Choice<string>(l.Code, l.Name))];

    public Choice<string> Language
    {
        get => LanguageOptions.FirstOrDefault(o => o.Value == S.Language) ?? LanguageOptions[0];
        set
        {
            if (value == null || value.Value == S.Language) return;
            S.Language = value.Value;
            SettingsService.Save();
            Loc.Instance.SetLanguage(value.Value);
            MainViewModel.Instance.OnLanguageChanged();
        }
    }

    public string AppVersion => Http.AppVersion;
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _isCheckingUpdates;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        IsCheckingUpdates = true;
        UpdateStatus = L.T("Checking…");
        try
        {
            await Updater.CheckAsync();
            if (Updater.Available is { } u)
            {
                UpdateStatus = L.F("Version {0} is available.", u.Version.ToString(3));
                await MainViewModel.Instance.UpdateLauncher();
            }
            else UpdateStatus = L.T("You have the latest version.");
        }
        catch (Exception ex) { UpdateStatus = L.F("Couldn't check for updates: {0}", ex.Message); }
        finally { IsCheckingUpdates = false; }
    }
    public string RuntimeText => $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

    public int ConcurrentDownloads { get => S.ConcurrentDownloads; set { S.ConcurrentDownloads = Math.Clamp(value, 1, 64); Changed(); } }
    public LaunchBehaviorOption OnLaunch
    {
        get => LaunchOptions.First(o => o.Value == S.OnLaunch);
        set { if (value != null) { S.OnLaunch = value.Value; Changed(); } }
    }

    public string JavaPath { get => G.JavaPath ?? ""; set { G.JavaPath = string.IsNullOrWhiteSpace(value) ? null : value; Changed(); } }
    public int? MinMemory { get => G.MinMemoryMb; set { G.MinMemoryMb = value ?? 512; Changed(); OnPropertyChanged(nameof(MinAboveMax)); } }
    public bool MinAboveMax => (G.MinMemoryMb ?? 0) > (G.MaxMemoryMb ?? int.MaxValue);
    public int? MaxMemory { get => G.MaxMemoryMb; set { G.MaxMemoryMb = value ?? 4096; Changed(); OnPropertyChanged(nameof(MinAboveMax)); } }
    public string JvmArgs { get => G.JvmArgs ?? ""; set { G.JvmArgs = value; Changed(); } }
    public int? Width { get => G.WindowWidth; set { G.WindowWidth = value ?? 854; Changed(); } }
    public int? Height { get => G.WindowHeight; set { G.WindowHeight = value ?? 480; Changed(); } }
    public bool Fullscreen { get => G.Fullscreen == true; set { G.Fullscreen = value; Changed(); } }
    public string ServerPropertiesDefaults { get => S.ServerPropertiesDefaults; set { S.ServerPropertiesDefaults = value ?? ""; Changed(); } }

    [RelayCommand]
    private void ResetJvmArgs() => JvmArgs = GameSettings.Defaults().JvmArgs!;

    [RelayCommand]
    private async Task BrowseJava()
    {
        if (await Ui.Platform.PickFileAsync(L.T("Select the Java executable"), [JavaManager.ExecutableFilter]) is { } f) JavaPath = f;
    }

    /// <summary>Opens the runtime's install folder (the one containing bin/).</summary>
    [RelayCommand]
    private void OpenJavaFolder(JavaRuntime rt)
    {
        var dir = File.Exists(rt.Path) ? Path.GetDirectoryName(Path.GetDirectoryName(rt.Path)) : rt.Path;
        Dialogs.OpenPath(dir ?? rt.Path);
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
            Dialogs.Toast(L.F("Java {0} is ready", m));
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Java {0} install failed: {1}", m, ex.Message), true); }
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
        if (!await Dialogs.ConfirmAsync(L.T("Remove Java"), L.F("Remove Java {0}? It will be downloaded again when an instance needs it.", rt.Major), L.T("Remove"), true)) return;
        try { JavaManager.Remove(rt.Major); }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
        RefreshJava();
    }
}
