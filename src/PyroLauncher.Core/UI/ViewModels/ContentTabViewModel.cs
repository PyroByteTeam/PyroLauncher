using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

/// <summary>
/// Mods / resource packs / shader packs: a list (or icon grid) on the left and, when something is selected,
/// its details on the right so you can switch between items without leaving the page.
/// </summary>
public partial class ContentTabViewModel : ObservableObject
{
    public Instance Instance { get; }
    public ContentType Type { get; }
    public ContentList List { get; }

    public ObservableCollection<LocalItem> Installed => List.Items;
    public ICollectionView InstalledView { get; }
    public ObservableCollection<ProjectSummary> Suggestions { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _suggestionsEnabled;
    [ObservableProperty] private SourceFilter _sourceFilter = SourceFilter.All;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _searchWarning;
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private ProjectDetailViewModel? _detail;
    [ObservableProperty] private string? _busyText;

    private CancellationTokenSource? _searchCts;
    private int _page;
    private object? _selected;

    public string Title => Type.Display();
    public string DropHint => L.F("Drop {0} here to add them", Type.Display().ToLower());
    public bool IsMods => Type == ContentType.Mod;
    public bool IsGrid => SettingsService.Current.ContentListStyle == ContentListStyle.Grid;
    public bool IsUnsupported => Type == ContentType.Mod && Instance.Config.Loader == ModLoader.Vanilla;
    public bool HasDetail => Detail != null;
    public string ToggleTooltip => Type switch
    {
        ContentType.Mod => L.T("Load this mod on launch"),
        ContentType.ResourcePack => L.T("Active in game"),
        _ => L.T("Use this shader pack (only one at a time)"),
    };
    public string SearchPlaceholder => SuggestionsEnabled
        ? (CurseForgeApi.HasKey ? L.T("Search Modrinth and CurseForge…") : L.T("Search Modrinth…"))
        : L.F("Filter installed {0}…", Type.Display().ToLower());
    public string EmptyText => Type switch
    {
        ContentType.Mod => L.T("No mods installed yet. Turn on suggestions to browse Modrinth and CurseForge."),
        ContentType.ResourcePack => L.T("No resource packs yet. Turn on suggestions to browse online."),
        _ => L.T("No shader packs yet. You'll need a shader mod like Iris or Oculus to use them."),
    };
    public string CountText => Type switch
    {
        ContentType.Mod => L.F("{0} installed · {1} enabled", Installed.Count, List.EnabledCount),
        ContentType.ResourcePack => L.F("{0} installed · {1} active", Installed.Count, List.EnabledCount),
        _ => $"{Installed.Count} installed · {(List.ActiveShader != null ? "using " + List.ActiveShader : L.T("shaders off"))}",
    };

    public ContentTabViewModel(Instance inst, ContentType type)
    {
        Instance = inst;
        Type = type;
        List = ContentStore.Get(inst, type);
        InstalledView = new ListCollectionView(List.Items) { Filter = FilterInstalled };
        List.PropertyChanged += OnListPropertyChanged;
        List.Items.CollectionChanged += OnItemsChanged;
        List.Refreshed += OnListRefreshed;
        WatchChecks();

        // Restore the suggestions toggle as it was left for this instance.
        Instance.Config.SuggestionsOn ??= [];
        if (Instance.Config.SuggestionsOn.Contains(Type.ToString()))
        {
            _suggestionsEnabled = true;
            _ = LoadCategoriesAsync();
            DebouncedSearch(0);
        }
    }

    private void OnListPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContentList.EnabledCount) or nameof(ContentList.ActiveShader)) OnPropertyChanged(nameof(CountText));
    }

    private void OnItemsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CountText));
        WatchChecks();
    }

    private readonly HashSet<LocalItem> _watched = [];
    [ObservableProperty] private int _checkedCount;
    public bool HasChecked => CheckedCount > 0;
    public int CheckedUpdates => Installed.Count(i => i.IsChecked && i.HasUpdate);
    partial void OnCheckedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(CheckedUpdates));
    }

    private void WatchChecks()
    {
        foreach (var gone in _watched.Where(w => !Installed.Contains(w)).ToList())
        {
            gone.PropertyChanged -= OnItemCheckChanged;
            _watched.Remove(gone);
        }
        foreach (var i in Installed)
            if (_watched.Add(i)) i.PropertyChanged += OnItemCheckChanged;
        CheckedCount = Installed.Count(i => i.IsChecked);
    }

    private void OnItemCheckChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalItem.IsChecked)) CheckedCount = Installed.Count(i => i.IsChecked);
        else if (e.PropertyName is nameof(LocalItem.HasUpdate)) OnPropertyChanged(nameof(CheckedUpdates));
    }

    private List<LocalItem> Checked => Installed.Where(i => i.IsChecked).ToList();

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var i in InstalledView.Cast<LocalItem>()) i.IsChecked = true;
    }

    [RelayCommand]
    private void ClearChecked()
    {
        foreach (var i in Installed) i.IsChecked = false;
    }

    [RelayCommand]
    private void BulkEnable(string on)
    {
        var value = on == "true";
        var items = Checked;
        if (Type == ContentType.ShaderPack && value)
        {
            // Only one shader pack can be active.
            if (items.Count > 0) items[^1].IsEnabled = true;
            return;
        }
        foreach (var i in items) i.IsEnabled = value;
    }

    [RelayCommand]
    private async Task BulkUpdate()
    {
        var list = Checked.Where(i => i.HasUpdate).ToList();
        if (list.Count == 0) { Dialogs.Toast(L.T("None of the selected items has a known update - run Check for updates first.")); return; }
        int ok = 0;
        foreach (var item in list)
        {
            BusyText = L.F("Updating {0} ({1}/{2})…", item.Name, ok + 1, list.Count);
            try
            {
                await ContentService.InstallAsync(Instance, Type, item.Update!, item.Ref?.Title ?? item.Name, item.Ref?.IconUrl, item, null, CancellationToken.None);
                ok++;
            }
            catch (Exception ex) { Dialogs.Toast(L.F("{0}: {1}", item.Name, ex.Message), true); }
        }
        BusyText = null;
        Dialogs.Toast(L.F("Updated {0} of {1}", ok, list.Count));
        await List.RefreshAsync();
        UpdatesCount = Installed.Count(i => i.HasUpdate);
    }

    [RelayCommand]
    private async Task BulkDelete()
    {
        var items = Checked;
        if (items.Count == 0) return;
        var names = string.Join("\n", items.Take(12).Select(i => "• " + i.Name)) + (items.Count > 12 ? L.F("\n…and {0} more", items.Count - 12) : "");
        if (!await Dialogs.ConfirmAsync(L.F("Delete selected ({0})", items.Count),
                L.F("These files will be deleted from the instance:\n\n{0}", names), L.T("Delete"), true)) return;
        int failed = 0;
        foreach (var i in items)
        {
            try
            {
                if (i.IsEnabled && Type != ContentType.Mod) i.IsEnabled = false;
                ContentService.Delete(Instance, i);
                if (Detail?.Installed == i) CloseDetail();
            }
            catch { failed++; }
        }
        if (failed > 0) Dialogs.Toast(L.F("{0} file(s) could not be deleted (in use?).", failed), true);
        await List.RefreshAsync();
    }

    /// <summary>Stops listening to the shared list (the list itself stays cached for fast reopening).</summary>
    public void Detach()
    {
        List.PropertyChanged -= OnListPropertyChanged;
        List.Items.CollectionChanged -= OnItemsChanged;
        List.Refreshed -= OnListRefreshed;
        foreach (var w in _watched) w.PropertyChanged -= OnItemCheckChanged;
        _watched.Clear();
        (InstalledView as ListCollectionView)?.DetachFromSourceCollection();
    }

    /// <summary>Called by the instance page when appearance settings change.</summary>
    public void OnAppearanceChanged() => OnPropertyChanged(nameof(IsGrid));

    private void OnListRefreshed()
    {
        UpdatesCount = Installed.Count(i => i.HasUpdate);
        if (StatusFilter != "all") InstalledView.Refresh();
        if (SuggestionsEnabled) FilterSuggestions();
        // Keep the details pane pointing at the (possibly re-created) installed item.
        if (Detail?.Installed is { } d && !Installed.Contains(d))
            Detail.Installed = Detail.ProjectId != null ? FindInstalled(Detail.Source, Detail.ProjectId) : null;
    }

    public void RefreshIfStale() => _ = List.RefreshAsync();

    [RelayCommand]
    private Task Refresh() => List.RefreshAsync();

    [ObservableProperty] private string _statusFilter = "all";
    [ObservableProperty] private SearchSort _sort = SearchSort.Relevance;
    [ObservableProperty] private string _category = AnyCategory;
    [ObservableProperty] private string _environment = L.T("Any side");
    [ObservableProperty] private int _updatesCount;
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private string? _updateStatus;
    public const string AnyCategory = "All categories";
    public ObservableCollection<string> Categories { get; } = [AnyCategory];
    public List<SearchSort> Sorts { get; } = [SearchSort.Relevance, SearchSort.Downloads, SearchSort.Newest, SearchSort.Updated];
    public List<string> Environments { get; } = ["Any side", "Client", "Server"];
    public string EnabledFilterText => Type == ContentType.Mod ? L.T("Enabled") : L.T("Active");
    public string DisabledFilterText => Type == ContentType.Mod ? L.T("Disabled") : L.T("Inactive");
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

    [RelayCommand]
    private async Task CheckUpdates()
    {
        IsCheckingUpdates = true;
        UpdateStatus = L.T("Checking for updates…");
        try
        {
            await ContentService.IdentifyAsync(Instance, Installed.ToList(), CancellationToken.None);
            var updates = await ContentService.CheckUpdatesAsync(Instance, Type, Installed.ToList(), CancellationToken.None);
            foreach (var i in Installed) i.Update = updates.GetValueOrDefault(i);
            UpdatesCount = updates.Count;
            UpdateStatus = updates.Count == 0 ? L.T("Everything is up to date.") : L.F("Updates available: {0}", updates.Count);
            if (updates.Count > 0) StatusFilter = "updates";
            var unlinked = Installed.Count(i => i.Ref == null);
            if (unlinked > 0) UpdateStatus += " · " + L.F("Not on Modrinth or CurseForge: {0}", unlinked);
        }
        catch (Exception ex) { UpdateStatus = L.F("Update check failed: {0}", ex.Message); }
        finally { IsCheckingUpdates = false; }
    }

    [RelayCommand]
    private async Task UpdateItem(LocalItem item)
    {
        if (item.Update == null) return;
        try
        {
            BusyText = L.F("Updating {0}…", item.Name);
            await ContentService.InstallAsync(Instance, Type, item.Update, item.Ref?.Title ?? item.Name, item.Ref?.IconUrl, item, null, CancellationToken.None);
            UpdatesCount = Math.Max(0, UpdatesCount - 1);
            await List.RefreshAsync();
        }
        catch (Exception ex) { Dialogs.Toast(L.F("{0}: {1}", item.Name, ex.Message), true); }
        finally { BusyText = null; }
    }

    [RelayCommand]
    private async Task UpdateAll()
    {
        var list = Installed.Where(i => i.HasUpdate).ToList();
        int ok = 0;
        foreach (var item in list)
        {
            BusyText = L.F("Updating {0} ({1}/{2})…", item.Name, ok + 1, list.Count);
            try
            {
                await ContentService.InstallAsync(Instance, Type, item.Update!, item.Ref?.Title ?? item.Name, item.Ref?.IconUrl, item, null, CancellationToken.None);
                ok++;
            }
            catch (Exception ex) { Dialogs.Toast(L.F("{0}: {1}", item.Name, ex.Message), true); }
        }
        BusyText = null;
        Dialogs.Toast(L.F("Updated {0} of {1}", ok, list.Count));
        UpdatesCount = 0;
        UpdateStatus = null;
        StatusFilter = "all";
        await List.RefreshAsync();
    }

    partial void OnQueryChanged(string value)
    {
        InstalledView.Refresh();
        if (SuggestionsEnabled) DebouncedSearch();
    }

    partial void OnSuggestionsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchPlaceholder));
        var saved = Instance.Config.SuggestionsOn;
        saved.Remove(Type.ToString());
        if (value) saved.Add(Type.ToString());
        Instance.Save();
        if (value) { _ = LoadCategoriesAsync(); DebouncedSearch(0); }
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
        finally { if (!ct.IsCancellationRequested) IsSearching = false; }
    }

    private HashSet<string> InstalledProjectIds() => Installed.Where(i => i.Ref != null).Select(i => i.Ref!.ProjectId).ToHashSet();

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
    private void OpenFolder() => Dialogs.OpenPath(List.Folder);

    [RelayCommand]
    private async Task AddFiles()
    {
        var filter = Type == ContentType.Mod ? new FileFilter(L.T("Mods"), "*.jar") : new FileFilter(L.T("Packs"), "*.zip");
        var files = await Ui.Platform.PickFilesAsync(L.F("Add {0}", Type.Display().ToLower()), [filter]);
        if (files.Count == 0) return;
        await AddFilesAsync(files);
    }

    public async Task AddFilesAsync(IEnumerable<string> files)
    {
        Directory.CreateDirectory(List.Folder);
        int n = 0, skipped = 0;
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (Type == ContentType.Mod ? ext != ".jar" : ext != ".zip") { skipped++; continue; }
            try { File.Copy(f, Path.Combine(List.Folder, Path.GetFileName(f)), true); n++; }
            catch (Exception ex) { Dialogs.Toast(L.F("{0}: {1}", Path.GetFileName(f), ex.Message), true); }
        }
        if (n > 0) Dialogs.Toast(L.F("Added files: {0}", n));
        if (skipped > 0) Dialogs.Toast(L.F("Skipped {0} file(s): expected {1}", skipped, Type == ContentType.Mod ? ".jar" : ".zip"), true);
        await List.RefreshAsync();
    }

    [RelayCommand]
    private async Task Delete(LocalItem item)
    {
        if (!await Dialogs.ConfirmAsync(L.T("Remove file"), L.F("Delete \"{0}\" ({1}) from this instance?", item.Name, item.FileName), L.T("Delete"), true)) return;
        try
        {
            if (item.IsEnabled && Type != ContentType.Mod) item.IsEnabled = false;
            ContentService.Delete(Instance, item);
            if (Detail?.Installed == item) CloseDetail();
            await List.RefreshAsync();
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Delete failed: {0}", ex.Message), true); }
    }

    [RelayCommand]
    private void Reveal(LocalItem item) => Dialogs.RevealInExplorer(item.FilePath);

    [RelayCommand]
    private void Toggle(LocalItem item) => item.IsEnabled = !item.IsEnabled;

    private void Select(object? item)
    {
        if (_selected is LocalItem a) a.IsSelected = false;
        if (_selected is ProjectSummary b) b.IsSelected = false;
        _selected = item;
        if (item is LocalItem c) c.IsSelected = true;
        if (item is ProjectSummary d) d.IsSelected = true;
    }

    [RelayCommand]
    private void OpenItem(LocalItem item)
    {
        Select(item);
        Detail = new ProjectDetailViewModel(this, item);
    }

    [RelayCommand]
    private void OpenProject(ProjectSummary p)
    {
        Select(p);
        Detail = new ProjectDetailViewModel(this, p);
    }

    [RelayCommand]
    private async Task QuickInstall(ProjectSummary p)
    {
        try
        {
            BusyText = L.F("Installing {0}…", p.Title);
            var versions = await ContentService.GetVersionsAsync(p.Source, p.Id, Type, Instance, true, CancellationToken.None);
            var v = versions.FirstOrDefault(x => x.ReleaseType == "release") ?? versions.FirstOrDefault();
            if (v == null)
            {
                Dialogs.Toast(L.F("{0} has no version for {1} {2}.", p.Title, Instance.Config.MinecraftVersion, Instance.LoaderText), true);
                return;
            }
            await ContentService.InstallAsync(Instance, Type, v, p.Title, p.IconUrl, null, null, CancellationToken.None);
            Dialogs.Toast(L.F("Installed {0} {1}", p.Title, v.VersionNumber));
            Suggestions.Remove(p);
            await List.RefreshAsync();
        }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
        finally { BusyText = null; }
    }

    [RelayCommand]
    private void CloseDetail()
    {
        Select(null);
        Detail = null;
    }

    partial void OnDetailChanged(ProjectDetailViewModel? value) => OnPropertyChanged(nameof(HasDetail));

    public LocalItem? FindInstalled(ContentSource source, string projectId) =>
        Installed.FirstOrDefault(i => i.Ref != null && i.Ref.Source == source && i.Ref.ProjectId == projectId);
}
