using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;

namespace PyroLauncher.UI.ViewModels;

public partial class NewInstanceViewModel : ObservableObject
{
    private List<McVersionInfo> _allVersions = [];
    private CancellationTokenSource? _loaderCts;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _showSnapshots = SettingsService.Current.ShowSnapshots;
    public ObservableCollection<McVersionInfo> Versions { get; } = [];
    [ObservableProperty] private McVersionInfo? _selectedVersion;
    [ObservableProperty] private ModLoader _loader = ModLoader.Vanilla;
    public ObservableCollection<string> LoaderVersions { get; } = [];
    [ObservableProperty] private string? _selectedLoaderVersion;
    [ObservableProperty] private bool _loadingLoaderVersions;
    [ObservableProperty] private string? _loaderMessage;
    [ObservableProperty] private bool _loadingVersions = true;

    public List<string> Categories { get; } = ["- None -", .. SettingsService.Current.Categories];
    [ObservableProperty] private string _category = L.T("- None -");

    public bool IsVanilla => Loader == ModLoader.Vanilla;
    public string NamePlaceholder => SelectedVersion == null ? L.T("Instance name") :
        Loader == ModLoader.Vanilla ? L.F("Minecraft {0}", SelectedVersion.Id) : L.F("{0} {1}", Loader, SelectedVersion.Id);

    public NewInstanceViewModel()
    {
        _ = LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            _allVersions = await MojangMeta.GetVersionsAsync();
            ApplyVersionFilter();
        }
        catch (Exception ex)
        {
            Dialogs.Toast(L.F("Could not load Minecraft versions: {0}", ex.Message), true);
        }
        finally { LoadingVersions = false; }
    }

    private void ApplyVersionFilter()
    {
        var prev = SelectedVersion?.Id;
        Versions.Clear();
        foreach (var v in _allVersions.Where(v => ShowSnapshots || v.IsRelease)) Versions.Add(v);
        SelectedVersion = Versions.FirstOrDefault(v => v.Id == prev) ?? Versions.FirstOrDefault(v => v.IsRelease) ?? Versions.FirstOrDefault();
    }

    private ICollectionView? _versionsView, _loaderVersionsView;
    public ICollectionView VersionsView => _versionsView ??= CreateView(Versions, o =>
        o is McVersionInfo v && (v == SelectedVersion || string.IsNullOrWhiteSpace(VersionQuery) || v.Id.Contains(VersionQuery.Trim(), StringComparison.OrdinalIgnoreCase)));
    public ICollectionView LoaderVersionsView => _loaderVersionsView ??= CreateView(LoaderVersions, o =>
        o is string s && (s == SelectedLoaderVersion || string.IsNullOrWhiteSpace(LoaderQuery) || s.Contains(LoaderQuery.Trim(), StringComparison.OrdinalIgnoreCase)));

    [ObservableProperty] private string _versionQuery = "";
    [ObservableProperty] private string _loaderQuery = "";
    partial void OnVersionQueryChanged(string value) => VersionsView.Refresh();
    partial void OnLoaderQueryChanged(string value) => LoaderVersionsView.Refresh();

    private static ICollectionView CreateView(System.Collections.IList source, Predicate<object> filter) =>
        new ListCollectionView(source) { Filter = filter };

    partial void OnShowSnapshotsChanged(bool value)
    {
        SettingsService.Current.ShowSnapshots = value;
        SettingsService.Save();
        ApplyVersionFilter();
    }

    partial void OnSelectedVersionChanged(McVersionInfo? value)
    {
        OnPropertyChanged(nameof(NamePlaceholder));
        CreateCommand.NotifyCanExecuteChanged();
        _ = LoadLoaderVersionsAsync();
    }

    partial void OnLoaderChanged(ModLoader value)
    {
        OnPropertyChanged(nameof(IsVanilla));
        OnPropertyChanged(nameof(NamePlaceholder));
        CreateCommand.NotifyCanExecuteChanged();
        _ = LoadLoaderVersionsAsync();
    }

    private async Task LoadLoaderVersionsAsync()
    {
        _loaderCts?.Cancel();
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        LoaderMessage = null;
        if (Loader == ModLoader.Vanilla || SelectedVersion == null) return;

        var cts = _loaderCts = new CancellationTokenSource();
        LoadingLoaderVersions = true;
        try
        {
            var list = await LoaderService.GetLoaderVersionsAsync(Loader, SelectedVersion.Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var v in list) LoaderVersions.Add(v);
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
            if (LoaderVersions.Count == 0) LoaderMessage = L.F("{0} is not available for Minecraft {1}.", Loader, SelectedVersion.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) LoaderMessage = L.F("Could not load {0} versions: {1}", Loader, ex.Message);
        }
        finally
        {
            if (_loaderCts == cts) LoadingLoaderVersions = false;
        }
    }

    public bool CanCreate => SelectedVersion != null && (Loader == ModLoader.Vanilla || SelectedLoaderVersion != null);

    partial void OnSelectedLoaderVersionChanged(string? value) => CreateCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        if (SelectedVersion == null) return;
        var name = string.IsNullOrWhiteSpace(Name) ? NamePlaceholder : Name.Trim();
        var inst = InstanceManager.Create(name, SelectedVersion.Id, Loader, SelectedLoaderVersion, CategoryValue);
        Dialogs.Close(this);
        Dialogs.Toast(L.F("Created \"{0}\"", inst.Name));
        MainViewModel.Instance.OpenInstance(inst);
    }

    private string? CategoryValue => Category == Categories[0] ? null : Category;

    [RelayCommand]
    private void OpenImport()
    {
        _loaderCts?.Cancel();
        Dialogs.Close(this);
        Dialogs.Show(new ImportLauncherViewModel());
    }

    [RelayCommand]
    private void Cancel()
    {
        _loaderCts?.Cancel();
        Dialogs.Close(this);
    }
}
