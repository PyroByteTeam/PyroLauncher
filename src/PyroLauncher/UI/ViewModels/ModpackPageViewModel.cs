using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

/// <summary>
/// A modpack's page: description, gallery and versions with changelogs.
/// From Discover it installs the pack as a new instance; inside a modpack instance it updates / switches the pack version.
/// </summary>
public partial class ModpackPageViewModel : ObservableObject
{
    public Instance? Instance { get; }
    public bool IsInstanceMode => Instance != null;
    public bool IsDiscoverMode => Instance == null;

    public ContentSource Source { get; }
    public string ProjectId { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string? _iconUrl;
    [ObservableProperty] private ProjectDetails? _details;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _tab = "description";

    public ModpackSummary? Listing { get; }
    public string SourceText => Source == ContentSource.Modrinth ? "Modrinth" : "CurseForge";

    public ObservableCollection<VersionItem> Versions { get; } = [];
    [ObservableProperty] private VersionItem? _selectedVersion;
    [ObservableProperty] private string? _changelogHtml;
    [ObservableProperty] private bool _loadingVersions;
    [ObservableProperty] private bool _loadingChangelog;
    [ObservableProperty] private bool _releasesOnly = true;
    private List<ProjectVersion> _allVersions = [];
    private CancellationTokenSource? _changelogCts;

    // ---- install / update
    [ObservableProperty] private string _instanceName = "";
    public List<string> Categories { get; } = [L.T("- None -"), .. SettingsService.Current.Categories];
    [ObservableProperty] private string _category;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private double _progress = -1;
    private CancellationTokenSource? _busyCts;

    public string GalleryTabText => Details == null || Details.Gallery.Count == 0 ? L.T("Gallery") : $"{L.T("Gallery")} ({Details.Gallery.Count})";
    public string CreatedText => Listing?.CreatedText ?? "-";
    public string UpdatedText => _allVersions.Count > 0 ? ModpackSummary.RelativeDate(_allVersions.Max(v => v.Date)) : Listing?.UpdatedText ?? "-";
    public string GameVersionsText
    {
        get
        {
            var all = _allVersions.SelectMany(v => v.GameVersions).Distinct()
                .OrderBy(v => System.Version.TryParse(v, out var p) ? p : new System.Version(0, 0)).ToList();
            if (all.Count == 0) return Listing?.GameVersionsText is { Length: > 0 } t ? t : "-";
            return all.Count <= 3 ? string.Join(", ", all) : $"{all[0]} - {all[^1]}";
        }
    }

    /// <summary>Discover: open a pack from search results.</summary>
    public ModpackPageViewModel(ModpackSummary s)
    {
        Listing = s;
        Source = s.Source;
        ProjectId = s.Id;
        Title = s.Title;
        Summary = s.Description;
        Author = s.Author;
        IconUrl = s.IconUrl;
        _instanceName = s.Title;
        _category = Categories[0];
        _ = LoadAsync();
    }

    /// <summary>The Modpack tab of an instance created from a pack.</summary>
    public ModpackPageViewModel(Instance inst)
    {
        Instance = inst;
        var m = inst.Config.Modpack!;
        Source = m.Source;
        ProjectId = m.ProjectId;
        Title = m.Title;
        IconUrl = m.IconUrl;
        _category = Categories[0];
        _ = LoadAsync();
    }

    partial void OnDetailsChanged(ProjectDetails? value) => OnPropertyChanged(nameof(GalleryTabText));

    private async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var d = await ContentService.GetDetailsAsync(Source, ProjectId, CancellationToken.None);
            Details = d;
            OnPropertyChanged(nameof(ServerPackUrl));
            OnPropertyChanged(nameof(HasServerPack));
            OnPropertyChanged(nameof(ServerPackTip));
            Title = d.Title;
            Summary = d.Description;
            if (!string.IsNullOrEmpty(d.Author)) Author = d.Author;
            IconUrl = d.IconUrl ?? IconUrl;
            if (IsDiscoverMode && string.IsNullOrWhiteSpace(InstanceName)) InstanceName = d.Title;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warn($"Modpack load failed: {ex.Message}");
        }
        finally { IsLoading = false; }
        await LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        LoadingVersions = true;
        try
        {
            _allVersions = await ModpackApi.GetVersionsAsync(Source, ProjectId, CancellationToken.None);
            ApplyVersionFilter();
            OnPropertyChanged(nameof(UpdatedText));
            OnPropertyChanged(nameof(GameVersionsText));
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Could not load versions: {0}", ex.Message), true); }
        finally { LoadingVersions = false; }
    }

    partial void OnReleasesOnlyChanged(bool value) => ApplyVersionFilter();

    private void ApplyVersionFilter()
    {
        var keep = SelectedVersion?.Version.Id;
        var installed = Instance?.Config.Modpack?.VersionId;
        Versions.Clear();
        foreach (var v in _allVersions.OrderByDescending(v => v.Date))
            if (!ReleasesOnly || v.ReleaseType == "release" || v.Id == installed)
                Versions.Add(new VersionItem(v) { IsInstalled = v.Id == installed });
        SelectedVersion = Versions.FirstOrDefault(v => v.Version.Id == keep)
                          ?? (IsInstanceMode ? Latest ?? Versions.FirstOrDefault(v => v.IsInstalled) : Versions.FirstOrDefault());
        OnPropertyChanged(nameof(LatestText));
        OnPropertyChanged(nameof(HasUpdate));
    }

    /// <summary>Newest release (or newest version when there are no releases).</summary>
    private VersionItem? Latest => Versions.FirstOrDefault(v => v.Version.ReleaseType == "release") ?? Versions.FirstOrDefault();
    public bool HasUpdate => IsInstanceMode && Latest is { IsInstalled: false } l && InstalledDate is { } d && l.Version.Date > d;
    private DateTime? InstalledDate => _allVersions.FirstOrDefault(v => v.Id == Instance?.Config.Modpack?.VersionId)?.Date;
    public string InstalledVersionText => Instance?.Config.Modpack?.VersionNumber ?? "-";
    public string LatestText => HasUpdate ? L.F("Update available: {0}", Latest!.Version.VersionNumber) : L.T("You have the latest version");

    partial void OnSelectedVersionChanged(VersionItem? value)
    {
        OnPropertyChanged(nameof(ServerPackUrl));
        OnPropertyChanged(nameof(HasServerPack));
        OnPropertyChanged(nameof(ServerPackTip));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanAct));
        _ = LoadChangelogAsync(value);
    }

    private async Task LoadChangelogAsync(VersionItem? v)
    {
        _changelogCts?.Cancel();
        if (v == null) { ChangelogHtml = null; return; }
        var cts = _changelogCts = new CancellationTokenSource();
        LoadingChangelog = true;
        try
        {
            var html = await ContentService.GetChangelogAsync(v.Version, cts.Token);
            if (!cts.IsCancellationRequested)
                ChangelogHtml = string.IsNullOrWhiteSpace(html) ? $"<p class='empty'>{L.T("No changelog provided.")}</p>" : html;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ChangelogHtml = $"<p class='empty'>{System.Net.WebUtility.HtmlEncode(L.F("Could not load changelog: {0}", ex.Message))}</p>"; }
        finally { if (_changelogCts == cts) LoadingChangelog = false; }
    }

    public string ActionText
    {
        get
        {
            if (IsBusy) return IsInstanceMode ? L.T("Updating…") : L.T("Installing…");
            if (SelectedVersion == null) return L.T("Select a version");
            if (IsDiscoverMode) return L.T("Install modpack");
            if (SelectedVersion.IsInstalled) return L.T("Installed");
            return InstalledDate is { } d && SelectedVersion.Version.Date < d ? L.T("Downgrade to this version") : L.T("Update to this version");
        }
    }

    public bool CanAct => SelectedVersion != null && !SelectedVersion.IsInstalled && !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanAct));
    }

    [RelayCommand]
    private async Task Act()
    {
        if (!CanAct || SelectedVersion == null) return;
        if (IsDiscoverMode) await InstallAsync(SelectedVersion.Version);
        else await UpdateAsync(SelectedVersion.Version);
    }

    [RelayCommand]
    private async Task UpdateToLatest()
    {
        if (Latest is { } l) { SelectedVersion = l; await Act(); }
    }

    private async Task InstallAsync(ProjectVersion v)
    {
        if (Details == null) return;
        IsBusy = true;
        _busyCts = new CancellationTokenSource();
        var progress = new Progress<TaskProgress>(p => { Status = p.Message; Progress = p.Fraction; });
        try
        {
            var name = string.IsNullOrWhiteSpace(InstanceName) ? Details.Title : InstanceName.Trim();
            var cat = Category == Categories[0] ? null : Category;
            var result = await ModpackApi.InstallAsync(Details, v, name, cat, progress, _busyCts.Token);
            MainViewModel.Instance.OpenInstance(result.Instance);
            if (result.SkippedFiles.Count > 0) Dialogs.Show(new ManualDownloadsViewModel(result.Instance, result.SkippedFiles));
            else Dialogs.Toast(L.F("Created \"{0}\"", result.Instance.Name));
        }
        catch (OperationCanceledException) { Dialogs.Toast(L.T("Cancelled")); }
        catch (Exception ex)
        {
            Log.Error("Modpack install failed", ex);
            Dialogs.Toast(L.F("Install failed: {0}", ex.Message), true);
        }
        finally
        {
            IsBusy = false;
            Status = null;
            Progress = -1;
        }
    }

    private async Task UpdateAsync(ProjectVersion v)
    {
        var inst = Instance!;
        if (inst.IsRunning || inst.IsBusy) { Dialogs.Toast(L.T("Stop the instance before changing the modpack version."), true); return; }
        if (!await Dialogs.ConfirmAsync(L.T("Change modpack version"),
                L.F("Switch \"{0}\" to {1}? Mods, configs and other files from the pack are replaced. Worlds, screenshots, your options and anything you added yourself are kept.", inst.Name, v.VersionNumber),
                L.T("Switch version")))
            return;
        IsBusy = true;
        inst.IsBusy = true;
        _busyCts = new CancellationTokenSource();
        inst.LaunchCts = _busyCts;
        var progress = new Progress<TaskProgress>(p => { Status = p.Message; Progress = p.Fraction; inst.Status = p.Message; inst.Progress = p.Fraction; });
        try
        {
            var skipped = await ModpackApi.UpdateAsync(inst, v, progress, _busyCts.Token);
            ApplyVersionFilter();
            OnPropertyChanged(nameof(InstalledVersionText));
            InstanceManager.NotifyChanged();
            if (skipped.Count > 0) Dialogs.Show(new ManualDownloadsViewModel(inst, skipped));
            else Dialogs.Toast(L.F("{0} is now on {1}", inst.Name, v.VersionNumber));
        }
        catch (OperationCanceledException) { Dialogs.Toast(L.T("Cancelled")); }
        catch (Exception ex)
        {
            Log.Error("Modpack update failed", ex);
            Dialogs.Toast(L.F("Update failed: {0}", ex.Message), true);
        }
        finally
        {
            IsBusy = false;
            inst.IsBusy = false;
            inst.LaunchCts = null;
            inst.Status = null;
            inst.Progress = -1;
            Status = null;
            Progress = -1;
        }
    }

    /// <summary>
    /// Where to get the server pack of the selected version: CurseForge links its server file to each pack file;
    /// on Modrinth authors attach it as an extra file of the version.
    /// </summary>
    public string? ServerPackUrl
    {
        get
        {
            if (SelectedVersion?.Version is not { } v) return null;
            if (v.Source == ContentSource.CurseForge)
            {
                if (v.ServerPackFileId <= 0) return null;
                var web = Details?.WebUrl ?? Listing?.WebUrl;
                return string.IsNullOrEmpty(web) ? null : $"{web.TrimEnd('/')}/files/{v.ServerPackFileId}";
            }
            return v.ExtraFiles.FirstOrDefault(f => (f.Title ?? "").Contains("server", StringComparison.OrdinalIgnoreCase))?.Url;
        }
    }

    public bool HasServerPack => ServerPackUrl != null;
    public string ServerPackTip => HasServerPack
        ? L.T("Download the server version of this modpack (opens in your browser)")
        : L.T("The author didn't publish a server pack for this version");

    [RelayCommand]
    private async Task ServerPack()
    {
        if (ServerPackUrl is not { } url || SelectedVersion == null) return;
        if (!await Dialogs.ConfirmAsync(L.T("Download server pack"),
                L.F("Open the server pack download for {0} {1} in your browser?\n\n{2}", Title, SelectedVersion.Version.VersionNumber, url),
                L.T("Open in browser")))
            return;
        Dialogs.OpenUrl(url);
    }

    [RelayCommand] private void CancelBusy() => _busyCts?.Cancel();
    [RelayCommand] private void OpenWeb() => Dialogs.OpenUrl(Details?.WebUrl ?? Listing?.WebUrl);
    [RelayCommand] private void OpenImage(GalleryImage img) => Dialogs.ShowGallery(Details?.Gallery.Select(g => new GalleryEntry(null, g.Url, g.Title)).ToList() ?? [new GalleryEntry(null, img.Url, img.Title)], Details?.Gallery.IndexOf(img) ?? 0);
    [RelayCommand] private void Back() => MainViewModel.Instance.ShowDiscover();
}
