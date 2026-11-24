using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Minecraft;

namespace PyroLauncher.UI.ViewModels;

/// <summary>A value with a translated label for combo boxes.</summary>
public record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Discover page: search Modrinth and CurseForge modpacks and install them as instances.</summary>
public partial class DiscoverViewModel : ObservableObject
{
    public ObservableCollection<ModpackSummary> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private SourceFilter _platform = SourceFilter.All;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private string? _error;

    public ObservableCollection<Choice<string?>> GameVersions { get; } = [new(null, L.T("Any version"))];
    [ObservableProperty] private Choice<string?> _gameVersion;

    public List<Choice<ModLoader?>> Loaders { get; } =
    [
        new(null, L.T("Any loader")), new(ModLoader.Fabric, "Fabric"), new(ModLoader.Forge, "Forge"), new(ModLoader.NeoForge, "NeoForge"),
    ];
    [ObservableProperty] private Choice<ModLoader?> _loader;

    public ObservableCollection<Choice<string?>> Categories { get; } = [new(null, L.T("All categories"))];
    [ObservableProperty] private Choice<string?> _category;

    public List<Choice<SearchSort>> Sorts { get; } =
    [
        new(SearchSort.Relevance, L.T("Relevance")), new(SearchSort.Downloads, L.T("Most downloads")),
        new(SearchSort.Newest, L.T("Newest")), new(SearchSort.Updated, L.T("Recently updated")),
    ];
    [ObservableProperty] private Choice<SearchSort> _sort;

    public bool IsGrid => SettingsService.Current.DiscoverStyle == ContentListStyle.Grid;
    public bool IsEmpty => !IsLoading && Results.Count == 0 && Error == null;

    private int _page;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _debounce;

    public DiscoverViewModel()
    {
        _gameVersion = GameVersions[0];
        _loader = Loaders[0];
        _category = Categories[0];
        _sort = Sorts[0];
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = SearchAsync(false); };
        SettingsService.AppearanceChanged += () => OnPropertyChanged(nameof(IsGrid));
        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        _ = LoadFiltersAsync();
        _ = SearchAsync(false);
    }

    private async Task LoadFiltersAsync()
    {
        try
        {
            foreach (var v in (await MojangMeta.GetVersionsAsync()).Where(v => v.IsRelease))
                GameVersions.Add(new(v.Id, v.Id));
        }
        catch (Exception ex) { Log.Warn($"Discover versions: {ex.Message}"); }
        try
        {
            foreach (var c in await ModpackApi.GetCategoriesAsync(CancellationToken.None))
                Categories.Add(new(c, L.T(ModrinthApi.Pretty(c))));
        }
        catch (Exception ex) { Log.Warn($"Discover categories: {ex.Message}"); }
    }

    partial void OnQueryChanged(string value) { _debounce.Stop(); _debounce.Start(); }
    partial void OnPlatformChanged(SourceFilter value) => _ = SearchAsync(false);
    partial void OnGameVersionChanged(Choice<string?> value) => _ = SearchAsync(false);
    partial void OnLoaderChanged(Choice<ModLoader?> value) => _ = SearchAsync(false);
    partial void OnCategoryChanged(Choice<string?> value) => _ = SearchAsync(false);
    partial void OnSortChanged(Choice<SearchSort> value) => _ = SearchAsync(false);
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>Raised when a new search replaces the results, so the view scrolls back to the top.</summary>
    public event Action? ResultsReset;

    private async Task SearchAsync(bool more)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (more) { _page++; IsLoadingMore = true; }
        else { _page = 0; IsLoading = true; }
        Error = null;
        try
        {
            var q = new DiscoverQuery(Query.Trim(), Platform, GameVersion?.Value, Loader?.Value, Category?.Value, Sort?.Value ?? SearchSort.Relevance, _page);
            var (results, warning) = await ModpackApi.SearchAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!more) { Results.Clear(); ResultsReset?.Invoke(); }
            var seen = Results.Select(r => r.Source + r.Id).ToHashSet();
            foreach (var r in results) if (seen.Add(r.Source + r.Id)) Results.Add(r);
            Warning = warning;
            CanLoadMore = results.Count >= ModpackApi.PageSize / 2;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Error = L.F("Search failed: {0}", ex.Message);
        }
        finally
        {
            if (_cts == cts) { IsLoading = false; IsLoadingMore = false; }
        }
    }

    [RelayCommand]
    private async Task LoadMore()
    {
        if (!CanLoadMore || IsLoading || IsLoadingMore) return;
        await SearchAsync(true);
    }

    [RelayCommand] private void SetPlatform(string p) => Platform = Enum.Parse<SourceFilter>(p);
    [RelayCommand] private Task Refresh() => SearchAsync(false);

    [RelayCommand]
    private void ClearFilters()
    {
        Query = "";
        GameVersion = GameVersions[0];
        Loader = Loaders[0];
        Category = Categories[0];
        Sort = Sorts[0];
        Platform = SourceFilter.All;
    }

    [RelayCommand]
    private void Open(ModpackSummary s) => MainViewModel.Instance.OpenModpack(new ModpackPageViewModel(s));
}
