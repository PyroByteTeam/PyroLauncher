using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

/// <summary>Mods / resource packs / shader packs tab: installed list + online suggestions + project page.</summary>
public partial class ContentTabViewModel : ObservableObject
{
    public Instance Instance { get; }
    public ContentType Type { get; }

    public ObservableCollection<LocalItem> Installed { get; } = [];
    public ICollectionView InstalledView { get; }
    public ObservableCollection<ProjectSummary> Suggestions { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _suggestionsEnabled;
    [ObservableProperty] private SourceFilter _sourceFilter = SourceFilter.All;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _searchWarning;
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private ProjectDetailViewModel? _detail;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private int _installedCount;
    [ObservableProperty] private int _enabledCount;

    private CancellationTokenSource? _searchCts;
    private int _page;
    private DateTime _lastScan;

    public string Title => Type.Display();
    public bool IsMods => Type == ContentType.Mod;
    public bool IsUnsupported => Type == ContentType.Mod && Instance.Config.Loader == ModLoader.Vanilla;
    public string SearchPlaceholder => SuggestionsEnabled
        ? $"Search Modrinth{(CurseForgeApi.HasKey ? " and CurseForge" : "")}…"
        : $"Filter installed {Type.Display().ToLower()}…";
    public string EmptyText => Type switch
    {
        ContentType.Mod => "No mods installed yet. Turn on suggestions to browse Modrinth and CurseForge.",
        ContentType.ResourcePack => "No resource packs yet. Turn on suggestions to browse online.",
        _ => "No shader packs yet. You'll need a shader mod like Iris or Oculus to use them.",
    };
    public string CountText => Type == ContentType.Mod
        ? $"{InstalledCount} installed · {EnabledCount} enabled"
        : $"{InstalledCount} installed";

    public ContentTabViewModel(Instance inst, ContentType type)
    {
        Instance = inst;
        Type = type;
        InstalledView = new ListCollectionView(Installed) { Filter = FilterInstalled };
        _ = RefreshAsync();
    }

    // ---------------- filters
    [ObservableProperty] private string _statusFilter = "all";
    [ObservableProperty] private SearchSort _sort = SearchSort.Relevance;
    [ObservableProperty] private string _category = AnyCategory;
    [ObservableProperty] private string _environment = "Any side";
    [ObservableProperty] private int _updatesCount;
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private string? _updateStatus;
    public const string AnyCategory = "All categories";
    public ObservableCollection<string> Categories { get; } = [AnyCategory];
    public List<SearchSort> Sorts { get; } = [SearchSort.Relevance, SearchSort.Downloads, SearchSort.Newest, SearchSort.Updated];
    public List<string> Environments { get; } = ["Any side", "Client", "Server"];
    private bool _categoriesLoaded;

    partial void OnStatusFilterChanged(string value) => InstalledView.Refresh();
    partial void OnSortChanged(SearchSort value) { if (SuggestionsEnabled) DebouncedSearch(0); }
    partial void OnCategoryChanged(string value) { if (SuggestionsEnabled) DebouncedSearch(0); }
    partial void OnEnvironmentChanged(string value) { if (SuggestionsEnabled) DebouncedSearch(0); }

    private SearchFilters CurrentFilters => new(Sort,
        Category == AnyCategory ? null : Category,
        Type == ContentType.Mod && Environment != "Any side" ? Environment.ToLowerInvariant() : null);

    private async Task LoadCategoriesAsync()
    {
        if (_categoriesLoaded) return;
        _categoriesLoaded = true;
        try
        {
            foreach (var c in await ModrinthApi.GetCategoriesAsync(Type, CancellationToken.None))
                if (c != Instance.Config.Loader.LoaderSlug()) Categories.Add(c);
        }
        catch (Exception ex) { Log.Warn($"Categories failed: {ex.Message}"); _categoriesLoaded = false; }
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        IsCheckingUpdates = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            await ContentService.IdentifyAsync(Instance, Installed.ToList(), CancellationToken.None);
            var updates = await ContentService.CheckUpdatesAsync(Instance, Type, Installed.ToList(), CancellationToken.None);
            foreach (var i in Installed) i.Update = updates.GetValueOrDefault(i);
            UpdatesCount = updates.Count;
            UpdateStatus = updates.Count == 0 ? "Everything is up to date." : $"{updates.Count} update{(updates.Count == 1 ? "" : "s")} available";
            if (updates.Count > 0) StatusFilter = "updates";
            var unlinked = Installed.Count(i => i.Ref == null);
            if (unlinked > 0) UpdateStatus += $" · {unlinked} file{(unlinked == 1 ? " isn't" : "s aren't")} on Modrinth/CurseForge";
        }
        catch (Exception ex) { UpdateStatus = $"Update check failed: {ex.Message}"; }
        finally { IsCheckingUpdates = false; }
    }

    [RelayCommand]
    private async Task UpdateItem(LocalItem item)
    {
        if (item.Update == null) return;
        try
        {
            BusyText = $"Updating {item.Name}…";
            var v = item.Update;
            await ContentService.InstallAsync(Instance, Type, v, item.Ref?.Title ?? item.Name, item.Ref?.IconUrl, item, null, CancellationToken.None);
            item.Update = null;
            UpdatesCount = Math.Max(0, UpdatesCount - 1);
        }
        catch (Exception ex) { Dialogs.Toast($"{item.Name}: {ex.Message}", true); }
        finally { BusyText = null; }
    }

    [RelayCommand]
    private async Task UpdateAll()
    {
        var list = Installed.Where(i => i.HasUpdate).ToList();
        int ok = 0;
        foreach (var item in list)
        {
            BusyText = $"Updating {item.Name} ({ok + 1}/{list.Count})…";
            try
            {
                await ContentService.InstallAsync(Instance, Type, item.Update!, item.Ref?.Title ?? item.Name, item.Ref?.IconUrl, item, null, CancellationToken.None);
                ok++;
            }
            catch (Exception ex) { Dialogs.Toast($"{item.Name}: {ex.Message}", true); }
        }
        BusyText = null;
        Dialogs.Toast($"Updated {ok} of {list.Count}");
        UpdatesCount = 0;
        UpdateStatus = null;
        StatusFilter = "all";
        await RefreshAsync();
    }

    private bool FilterInstalled(object o)
    {
        if (o is not LocalItem i) return true;
        switch (StatusFilter)
        {
            case "enabled" when !i.IsEnabled: return false;
            case "disabled" when i.IsEnabled: return false;
            case "updates" when !i.HasUpdate: return false;
        }
        if (string.IsNullOrWhiteSpace(Query)) return true;
        return i.Name.Contains(Query, StringComparison.OrdinalIgnoreCase)
               || i.FileName.Contains(Query, StringComparison.OrdinalIgnoreCase)
               || (i.ModId?.Contains(Query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public void RefreshIfStale()
    {
        if ((DateTime.Now - _lastScan).TotalSeconds > 3) _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            _lastScan = DateTime.Now;
            var items = await Task.Run(() => LocalContentScanner.Scan(Instance, Type));
            var known = Installed.Where(i => i.HasUpdate).ToDictionary(i => i.FileName, i => i.Update);
            foreach (var old in Installed) old.PropertyChanged -= OnItemChanged;
            Installed.Clear();
            foreach (var i in items)
            {
                if (known.TryGetValue(i.FileName, out var up)) i.Update = up;
                i.PropertyChanged += OnItemChanged;
                Installed.Add(i);
            }
            UpdatesCount = items.Count(i => i.HasUpdate);
            UpdateCounts();
            _ = IdentifyAsync(items);
            if (SuggestionsEnabled) FilterSuggestions();
        }
        catch (Exception ex) { Dialogs.Toast($"Could not read {Type.Folder()}: {ex.Message}", true); }
        finally { IsLoading = false; }
    }

    private async Task IdentifyAsync(List<LocalItem> items)
    {
        try { await ContentService.IdentifyAsync(Instance, items, CancellationToken.None); }
        catch (Exception ex) { Log.Warn($"Identify failed: {ex.Message}"); }
        if (SuggestionsEnabled) FilterSuggestions();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalItem.IsEnabled))
        {
            UpdateCounts();
            if (StatusFilter != "all") InstalledView.Refresh();
        }
    }

    private void UpdateCounts()
    {
        InstalledCount = Installed.Count;
        EnabledCount = Installed.Count(i => i.IsEnabled);
        OnPropertyChanged(nameof(CountText));
    }

    partial void OnQueryChanged(string value)
    {
        InstalledView.Refresh();
        if (SuggestionsEnabled) DebouncedSearch();
    }

    partial void OnSuggestionsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchPlaceholder));
        if (value) _ = LoadCategoriesAsync();
        if (value) DebouncedSearch(0);
        else
        {
            _searchCts?.Cancel();
            Suggestions.Clear();
            SearchWarning = null;
        }
    }

    partial void OnSourceFilterChanged(SourceFilter value)
    {
        if (SuggestionsEnabled) DebouncedSearch(0);
    }

    private async void DebouncedSearch(int delayMs = 400)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            _page = 0;
            await SearchAsync(cts.Token, false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task SearchAsync(CancellationToken ct, bool append)
    {
        if (IsUnsupported) return;
        IsSearching = true;
        try
        {
            var (results, warning) = await ContentService.SearchAsync(Query.Trim(), Type, Instance, SourceFilter, _page, ct, CurrentFilters);
            if (ct.IsCancellationRequested) return;
            if (!append) Suggestions.Clear();
            var installedIds = InstalledProjectIds();
            foreach (var r in results)
                if (!installedIds.Contains(r.Id) && Suggestions.All(s => s.Id != r.Id || s.Source != r.Source))
                    Suggestions.Add(r);
            SearchWarning = warning;
            CanLoadMore = results.Count >= 10;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SearchWarning = ex.Message; }
        finally
        {
            if (!ct.IsCancellationRequested) IsSearching = false;
        }
    }

    private HashSet<string> InstalledProjectIds() =>
        Installed.Where(i => i.Ref != null).Select(i => i.Ref!.ProjectId).ToHashSet();

    private void FilterSuggestions()
    {
        var ids = InstalledProjectIds();
        foreach (var s in Suggestions.Where(s => ids.Contains(s.Id)).ToList()) Suggestions.Remove(s);
    }

    [RelayCommand]
    private async Task LoadMore()
    {
        _page++;
        var cts = _searchCts ??= new CancellationTokenSource();
        await SearchAsync(cts.Token, true);
    }

    [RelayCommand]
    private void OpenFolder() => Dialogs.OpenPath(Path.Combine(Instance.GameDir, Type.Folder()));

    [RelayCommand]
    private async Task AddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Add {Type.Display().ToLower()}",
            Multiselect = true,
            Filter = Type == ContentType.Mod ? "Mods (*.jar)|*.jar" : "Packs (*.zip)|*.zip",
        };
        if (dlg.ShowDialog() != true) return;
        await AddFilesAsync(dlg.FileNames);
    }

    public async Task AddFilesAsync(IEnumerable<string> files)
    {
        var folder = Path.Combine(Instance.GameDir, Type.Folder());
        Directory.CreateDirectory(folder);
        int n = 0;
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (Type == ContentType.Mod ? ext != ".jar" : ext != ".zip") continue;
            try { File.Copy(f, Path.Combine(folder, Path.GetFileName(f)), true); n++; }
            catch (Exception ex) { Dialogs.Toast($"{Path.GetFileName(f)}: {ex.Message}", true); }
        }
        if (n > 0) Dialogs.Toast($"Added {n} file{(n == 1 ? "" : "s")}");
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task Delete(LocalItem item)
    {
        if (!await Dialogs.ConfirmAsync("Remove file", $"Delete \"{item.Name}\" ({item.FileName}) from this instance?", "Delete", true)) return;
        try
        {
            ContentService.Delete(Instance, item);
            item.PropertyChanged -= OnItemChanged;
            Installed.Remove(item);
            UpdateCounts();
            if (Detail?.Installed == item) Detail = null;
        }
        catch (Exception ex) { Dialogs.Toast($"Delete failed: {ex.Message}", true); }
    }

    [RelayCommand]
    private void Reveal(LocalItem item) => Dialogs.RevealInExplorer(item.FilePath);

    [RelayCommand]
    private void OpenItem(LocalItem item) => Detail = new ProjectDetailViewModel(this, item);

    [RelayCommand]
    private void OpenProject(ProjectSummary p) => Detail = new ProjectDetailViewModel(this, p);

    [RelayCommand]
    private async Task QuickInstall(ProjectSummary p)
    {
        try
        {
            BusyText = $"Installing {p.Title}…";
            var versions = await ContentService.GetVersionsAsync(p.Source, p.Id, Type, Instance, true, CancellationToken.None);
            var v = versions.FirstOrDefault(x => x.ReleaseType == "release") ?? versions.FirstOrDefault();
            if (v == null)
            {
                Dialogs.Toast($"{p.Title} has no version for {Instance.Config.MinecraftVersion} {Instance.LoaderText}.", true);
                return;
            }
            await ContentService.InstallAsync(Instance, Type, v, p.Title, p.IconUrl, null, null, CancellationToken.None);
            Dialogs.Toast($"Installed {p.Title} {v.VersionNumber}");
            Suggestions.Remove(p);
            await RefreshAsync();
        }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
        finally { BusyText = null; }
    }

    [RelayCommand]
    private void CloseDetail() => Detail = null;

    public LocalItem? FindInstalled(ContentSource source, string projectId) =>
        Installed.FirstOrDefault(i => i.Ref != null && i.Ref.Source == source && i.Ref.ProjectId == projectId);
}
