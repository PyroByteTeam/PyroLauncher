using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;

namespace PyroLauncher.UI.ViewModels;

public partial class InstanceSettingsViewModel : ObservableObject
{
    public Instance Instance { get; }
    private static GameSettings G => SettingsService.Current.Game;

    [ObservableProperty] private string _name;
    public List<string> Categories { get; } = ["— None —", .. SettingsService.Current.Categories];
    [ObservableProperty] private string _category;

    public ObservableCollection<string> LoaderVersions { get; } = [];
    [ObservableProperty] private string? _loaderVersion;
    public bool HasLoader => Instance.Config.Loader != ModLoader.Vanilla;

    [ObservableProperty] private bool _useJava;
    [ObservableProperty] private string _javaPath;
    [ObservableProperty] private bool _useMemory;
    [ObservableProperty] private int? _minMemory;
    [ObservableProperty] private int? _maxMemory;
    [ObservableProperty] private bool _useJvmArgs;
    [ObservableProperty] private string _jvmArgs;
    [ObservableProperty] private bool _useResolution;
    [ObservableProperty] private int? _width;
    [ObservableProperty] private int? _height;
    [ObservableProperty] private bool _useFullscreen;
    [ObservableProperty] private bool _fullscreen;
    [ObservableProperty] private bool _isDirty;

    public string DefaultJava => string.IsNullOrWhiteSpace(G.JavaPath) ? "Default: automatic (Adoptium)" : $"Default: {G.JavaPath}";
    public string DefaultMemory => $"Default: {G.MinMemoryMb} – {G.MaxMemoryMb} MB";
    public string DefaultJvm => string.IsNullOrWhiteSpace(G.JvmArgs) ? "Default: none" : $"Default: {G.JvmArgs}";
    public string DefaultResolution => $"Default: {G.WindowWidth} × {G.WindowHeight}";
    public string DefaultFullscreen => $"Default: {(G.Fullscreen == true ? "on" : "off")}";
    public string LoaderLabel => $"{Instance.LoaderText} version";

    public InstanceSettingsViewModel(Instance inst)
    {
        Instance = inst;
        var c = inst.Config;
        var o = c.Overrides;
        _name = c.Name;
        _category = c.Category ?? Categories[0];
        _loaderVersion = c.LoaderVersion;
        _useJava = !string.IsNullOrEmpty(o.JavaPath);
        _javaPath = o.JavaPath ?? "";
        _useMemory = o.MinMemoryMb != null || o.MaxMemoryMb != null;
        _minMemory = o.MinMemoryMb ?? G.MinMemoryMb;
        _maxMemory = o.MaxMemoryMb ?? G.MaxMemoryMb;
        _useJvmArgs = o.JvmArgs != null;
        _jvmArgs = o.JvmArgs ?? G.JvmArgs ?? "";
        _useResolution = o.WindowWidth != null || o.WindowHeight != null;
        _width = o.WindowWidth ?? G.WindowWidth;
        _height = o.WindowHeight ?? G.WindowHeight;
        _useFullscreen = o.Fullscreen != null;
        _fullscreen = o.Fullscreen ?? G.Fullscreen ?? false;
        if (c.LoaderVersion != null) LoaderVersions.Add(c.LoaderVersion);
        if (HasLoader) _ = LoadLoaderVersionsAsync();
    }

    private async Task LoadLoaderVersionsAsync()
    {
        try
        {
            var list = await LoaderService.GetLoaderVersionsAsync(Instance.Config.Loader, Instance.Config.MinecraftVersion);
            var current = LoaderVersion;
            LoaderVersions.Clear();
            foreach (var v in list) LoaderVersions.Add(v);
            if (current != null && !LoaderVersions.Contains(current)) LoaderVersions.Insert(0, current);
            LoaderVersion = current;
            IsDirty = false;
        }
        catch { /* keep current */ }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(IsDirty)) IsDirty = true;
    }

    [RelayCommand]
    private void Save()
    {
        if (Instance.IsRunning || Instance.IsBusy)
        {
            Dialogs.Toast("Settings will apply on the next launch.");
        }
        var c = Instance.Config;
        var o = c.Overrides;
        if (!string.IsNullOrWhiteSpace(Name)) c.Name = Name.Trim();
        var cat = Category == Categories[0] ? null : Category;
        bool catChanged = c.Category != cat;
        c.Category = cat;
        if (HasLoader && LoaderVersion != null && LoaderVersion != c.LoaderVersion) c.LoaderVersion = LoaderVersion;

        o.JavaPath = UseJava && !string.IsNullOrWhiteSpace(JavaPath) ? JavaPath.Trim() : null;
        o.MinMemoryMb = UseMemory ? MinMemory : null;
        o.MaxMemoryMb = UseMemory ? MaxMemory : null;
        o.JvmArgs = UseJvmArgs ? JvmArgs : null;
        o.WindowWidth = UseResolution ? Width : null;
        o.WindowHeight = UseResolution ? Height : null;
        o.Fullscreen = UseFullscreen ? Fullscreen : null;

        if (o.MinMemoryMb > o.MaxMemoryMb) { Dialogs.Toast("Minimum memory can't be larger than maximum.", true); return; }

        Instance.Save();
        Instance.Refresh();
        InstanceManager.NotifyChanged();
        IsDirty = false;
        Dialogs.Toast("Instance settings saved");
    }

    [RelayCommand]
    private void BrowseJava()
    {
        var dlg = new OpenFileDialog { Title = "Select javaw.exe", Filter = "Java (javaw.exe;java.exe)|javaw.exe;java.exe" };
        if (dlg.ShowDialog() == true) { JavaPath = dlg.FileName; UseJava = true; }
    }

    [RelayCommand]
    private void ChangeIcon()
    {
        var dlg = new OpenFileDialog { Title = "Choose icon", Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.Copy(dlg.FileName, Instance.IconPath, true);
            Instance.LoadIcon();
        }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }

    [RelayCommand]
    private void RemoveIcon()
    {
        try { File.Delete(Instance.IconPath); Instance.LoadIcon(); } catch { }
    }

    [RelayCommand]
    private void OpenFolder() => Dialogs.OpenPath(Instance.Dir);

    [RelayCommand]
    private async Task Delete()
    {
        if (Instance.IsRunning || Instance.IsBusy) { Dialogs.Toast("Stop the instance before deleting it.", true); return; }
        if (!await Dialogs.ConfirmAsync("Delete instance",
                $"Permanently delete \"{Instance.Name}\" including its worlds, mods and screenshots? This cannot be undone.", "Delete", true))
            return;
        try
        {
            InstanceManager.Delete(Instance);
            MainViewModel.Instance.GoHomeCommand.Execute(null);
        }
        catch (Exception ex) { Dialogs.Toast($"Delete failed: {ex.Message}", true); }
    }
}
