using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;

namespace PyroLauncher.UI.ViewModels;

public partial class NewInstanceViewModel : ObservableObject
{
    private List<McVersionInfo> _allVersions = [];
    private CancellationTokenSource? _loaderCts;
    private CancellationTokenSource? _importCts;

    [ObservableProperty] private string _mode = "create";

    // ---- create
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

    // ---- import
    [ObservableProperty] private string? _importPath;
    [ObservableProperty] private ModpackInfo? _importInfo;
    [ObservableProperty] private string _importName = "";
    [ObservableProperty] private string? _importError;

    // ---- progress
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private double _progress = -1;

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

    // ---- search inside the dropdowns (the selected item always stays visible so the selection is kept)
    private System.ComponentModel.ICollectionView? _versionsView, _loaderVersionsView;
    public System.ComponentModel.ICollectionView VersionsView => _versionsView ??= CreateView(Versions, o =>
        o is McVersionInfo v && (v == SelectedVersion || string.IsNullOrWhiteSpace(VersionQuery) || v.Id.Contains(VersionQuery.Trim(), StringComparison.OrdinalIgnoreCase)));
    public System.ComponentModel.ICollectionView LoaderVersionsView => _loaderVersionsView ??= CreateView(LoaderVersions, o =>
        o is string s && (s == SelectedLoaderVersion || string.IsNullOrWhiteSpace(LoaderQuery) || s.Contains(LoaderQuery.Trim(), StringComparison.OrdinalIgnoreCase)));

    [ObservableProperty] private string _versionQuery = "";
    [ObservableProperty] private string _loaderQuery = "";
    partial void OnVersionQueryChanged(string value) => VersionsView.Refresh();
    partial void OnLoaderQueryChanged(string value) => LoaderVersionsView.Refresh();

    private static System.ComponentModel.ICollectionView CreateView(System.Collections.IList source, Predicate<object> filter) =>
        new System.Windows.Data.ListCollectionView(source) { Filter = filter };

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
    private void BrowseImport()
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Import modpack"),
            Filter = "Modpacks (*.mrpack;*.zip;*.pyropack)|*.mrpack;*.zip;*.pyropack|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        SetImportFile(dlg.FileName);
    }

    public void SetImportFile(string path)
    {
        ImportPath = path;
        ImportError = null;
        ImportInfo = null;
        try
        {
            ImportInfo = ModpackImporter.Inspect(path);
            ImportName = ImportInfo.Name;
        }
        catch (Exception ex) { ImportError = ex.Message; }
        ImportCommand.NotifyCanExecuteChanged();
    }

    public bool CanImport => ImportInfo != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        if (ImportPath == null || ImportInfo == null) return;
        IsBusy = true;
        ImportCommand.NotifyCanExecuteChanged();
        _importCts = new CancellationTokenSource();
        var progress = new Progress<TaskProgress>(p => { Status = p.Message; Progress = p.Fraction; });
        try
        {
            var name = string.IsNullOrWhiteSpace(ImportName) ? ImportInfo.Name : ImportName.Trim();
            var result = await ModpackImporter.ImportAsync(ImportPath, name, CategoryValue, progress, _importCts.Token);
            Dialogs.Close(this);
            MainViewModel.Instance.OpenInstance(result.Instance);
            if (result.SkippedFiles.Count > 0)
            {
                Dialogs.Show(new ManualDownloadsViewModel(result.Instance, result.SkippedFiles));
            }
            else Dialogs.Toast(L.F("Imported \"{0}\"", result.Instance.Name));
        }
        catch (OperationCanceledException) { Status = L.T("Cancelled"); }
        catch (Exception ex)
        {
            Log.Error("Import failed", ex);
            ImportError = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = -1;
            ImportCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) { _importCts?.Cancel(); return; }
        _loaderCts?.Cancel();
        Dialogs.Close(this);
    }
}
