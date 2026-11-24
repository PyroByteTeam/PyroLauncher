using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

public partial class InstanceViewModel : ObservableObject
{
    public Instance Instance { get; }
    private readonly Dictionary<string, object> _tabs = [];

    [ObservableProperty] private string _tab = "mods";
    [ObservableProperty] private object? _currentTab;

    public InstanceViewModel(Instance inst)
    {
        Instance = inst;
        Instance.PropertyChanged += OnInstanceChanged;
        CurrentTab = GetTab(Tab);
    }

    private void OnInstanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Instance.IsRunning) or nameof(Instance.IsBusy))
        {
            OnPropertyChanged(nameof(LaunchText));
            OnPropertyChanged(nameof(LaunchGlyph));
        }
    }

    public string LaunchText => Instance.IsBusy ? "Cancel" : Instance.IsRunning ? "Stop" : "Play";
    public string LaunchGlyph => Instance.IsBusy || Instance.IsRunning ? "" : "";

    partial void OnTabChanged(string value) => CurrentTab = GetTab(value);

    private object GetTab(string key)
    {
        if (_tabs.TryGetValue(key, out var vm))
        {
            if (vm is ContentTabViewModel c) c.RefreshIfStale();
            if (vm is ScreenshotsViewModel s) s.Load();
            return vm;
        }
        vm = key switch
        {
            "mods" => new ContentTabViewModel(Instance, ContentType.Mod),
            "resourcepacks" => new ContentTabViewModel(Instance, ContentType.ResourcePack),
            "shaders" => new ContentTabViewModel(Instance, ContentType.ShaderPack),
            "screenshots" => new ScreenshotsViewModel(Instance),
            "servers" => new ServersViewModel(Instance),
            "logs" => new LogsViewModel(Instance),
            "settings" => new InstanceSettingsViewModel(Instance),
            _ => new LogsViewModel(Instance),
        };
        _tabs[key] = vm;
        return vm;
    }

    [RelayCommand]
    private void Back()
    {
        Instance.PropertyChanged -= OnInstanceChanged;
        MainViewModel.Instance.GoHomeCommand.Execute(null);
    }

    [RelayCommand]
    private async Task Launch()
    {
        if (Instance.IsBusy || Instance.IsRunning) LaunchService.Stop(Instance);
        else await LaunchService.LaunchAsync(Instance);
    }

    [RelayCommand]
    private void OpenFolder() => Dialogs.OpenPath(Instance.GameDir);

    [RelayCommand]
    private void OpenConsole() => Views.ConsoleWindow.ShowFor(Instance);
}

public partial class LogsViewModel(Instance inst) : ObservableObject
{
    public Instance Instance { get; } = inst;

    [RelayCommand]
    private void OpenConsole() => Views.ConsoleWindow.ShowFor(Instance);

    [RelayCommand]
    private void Clear() => Instance.ClearLog();

    [RelayCommand]
    private void OpenLogsFolder()
    {
        var dir = Path.Combine(Instance.GameDir, "logs");
        Dialogs.OpenPath(Directory.Exists(dir) ? dir : Instance.GameDir);
    }

    [RelayCommand]
    private void OpenCrashReports()
    {
        var dir = Path.Combine(Instance.GameDir, "crash-reports");
        if (Directory.Exists(dir)) Dialogs.OpenPath(dir);
        else Dialogs.Toast("No crash reports yet.");
    }
}
