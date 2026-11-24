using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Core;

namespace PyroLauncher.UI.ViewModels;

public partial class VersionItem(ProjectVersion v) : ObservableObject
{
    public ProjectVersion Version { get; } = v;
    [ObservableProperty] private bool _isInstalled;
}

public partial class DependencyItem : ObservableObject
{
    public ContentSource Source { get; init; }
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? IconUrl { get; init; }
    public string Kind { get; init; } = "required";
    [ObservableProperty] private bool _isInstalled;
    public string KindText => Kind switch
    {
        "optional" => "Optional",
        "incompatible" => "Incompatible",
        "embedded" => "Bundled",
        _ => "Required",
    };
    public bool CanInstall => !IsInstalled && Kind is "required" or "optional";
    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
}

public partial class ProjectDetailViewModel : ObservableObject
{
    // ---------------- dependencies of the selected version
    public ObservableCollection<DependencyItem> Dependencies { get; } = [];
    [ObservableProperty] private bool _dependenciesExpanded;
    [ObservableProperty] private bool _loadingDependencies;
    [ObservableProperty] private string _dependenciesHeader = "Dependencies";
    public bool HasDependencies => Dependencies.Count > 0;
    private CancellationTokenSource? _depsCts;

    private async Task LoadDependenciesAsync(VersionItem? v)
    {
        _depsCts?.Cancel();
        Dependencies.Clear();
        OnPropertyChanged(nameof(HasDependencies));
        if (v == null || v.Version.Dependencies.Count == 0) { DependenciesHeader = "No dependencies"; return; }
        var cts = _depsCts = new CancellationTokenSource();
        LoadingDependencies = true;
        try
        {
            var deps = v.Version.Dependencies.Where(d => d.ProjectId.Length > 0 && d.ProjectId != "0").ToList();
            Dictionary<string, (string title, string? icon)> briefs = [];
            if (v.Version.Source == ContentSource.Modrinth)
                briefs = await ModrinthApi.GetProjectsBriefAsync(deps.Select(d => d.ProjectId), cts.Token);
            else if (CurseForgeApi.HasKey)
                briefs = (await CurseForgeApi.GetModsBriefAsync(deps.Select(d => long.Parse(d.ProjectId)), cts.Token))
                    .ToDictionary(k => k.Key, k => (k.Value.title, k.Value.icon));
            if (cts.IsCancellationRequested) return;

            var order = new[] { "required", "optional", "embedded", "incompatible" };
            foreach (var d in deps.OrderBy(d => Array.IndexOf(order, d.Kind)))
            {
                briefs.TryGetValue(d.ProjectId, out var b);
                Dependencies.Add(new DependencyItem
                {
                    Source = v.Version.Source,
                    ProjectId = d.ProjectId,
                    Title = string.IsNullOrEmpty(b.title) ? d.ProjectId : b.title,
                    IconUrl = b.icon,
                    Kind = d.Kind,
                    IsInstalled = _owner.FindInstalled(v.Version.Source, d.ProjectId) != null,
                });
            }
            var required = Dependencies.Count(d => d.Kind == "required");
            DependenciesHeader = $"Dependencies ({Dependencies.Count}{(required > 0 ? $" · {required} required" : "")}) — for {v.Version.VersionNumber}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DependenciesHeader = $"Dependencies unavailable: {ex.Message}"; }
        finally
        {
            if (_depsCts == cts) LoadingDependencies = false;
            OnPropertyChanged(nameof(HasDependencies));
        }
    }

    private ProjectSummary SummaryOf(DependencyItem d) => new()
    {
        Source = d.Source, Id = d.ProjectId, Title = d.Title, IconUrl = d.IconUrl,
    };

    [RelayCommand]
    private void OpenDependency(DependencyItem d) => _owner.OpenProjectCommand.Execute(SummaryOf(d));

    [RelayCommand]
    private async Task InstallDependency(DependencyItem d)
    {
        await _owner.QuickInstallCommand.ExecuteAsync(SummaryOf(d));
        d.IsInstalled = _owner.FindInstalled(d.Source, d.ProjectId) != null;
    }

    private readonly ContentTabViewModel _owner;
    private CancellationTokenSource? _changelogCts;

    public ContentSource Source { get; }
    public string? ProjectId { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string? _iconUrl;
    [ObservableProperty] private ProjectDetails? _details;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _tab = "description";
    [ObservableProperty] private LocalItem? _installed;

    public ObservableCollection<VersionItem> Versions { get; } = [];
    [ObservableProperty] private VersionItem? _selectedVersion;
    [ObservableProperty] private string? _changelogHtml;
    [ObservableProperty] private bool _loadingVersions;
    [ObservableProperty] private bool _loadingChangelog;
    [ObservableProperty] private bool _compatibleOnly = true;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string? _installStatus;

    /// <summary>True for a local file we could not match to Modrinth/CurseForge.</summary>
    public bool IsLocalOnly => ProjectId == null;
    public bool IsMod => _owner.Type == Content.ContentType.Mod;
    public string ActiveLabel => _owner.Type switch
    {
        Content.ContentType.Mod => "Enabled",
        Content.ContentType.ResourcePack => "Active in game",
        _ => "Use this shader",
    };
    public string SourceText => IsLocalOnly ? "Local file" : Source == ContentSource.Modrinth ? "Modrinth" : "CurseForge";
    public string CompatText => $"Only {_owner.Instance.Config.MinecraftVersion}{(IsMod ? " · " + _owner.Instance.LoaderText : "")}";

    public ProjectDetailViewModel(ContentTabViewModel owner, ProjectSummary p)
    {
        _owner = owner;
        Source = p.Source;
        ProjectId = p.Id;
        Title = p.Title;
        Summary = p.Description;
        Author = p.Author;
        IconUrl = p.IconUrl;
        Installed = owner.FindInstalled(p.Source, p.Id);
        _ = LoadAsync();
    }

    public ProjectDetailViewModel(ContentTabViewModel owner, LocalItem item)
    {
        _owner = owner;
        Installed = item;
        Title = item.Name;
        Summary = item.Description ?? "";
        IconUrl = item.IconUrl;
        if (item.Ref != null)
        {
            Source = item.Ref.Source;
            ProjectId = item.Ref.ProjectId;
            _ = LoadAsync();
        }
        else
        {
            Tab = "info";
        }
    }

    public System.Windows.Media.ImageSource? LocalIcon => Installed?.Icon;
    public string GalleryTabText => Details == null || Details.Gallery.Count == 0 ? "Gallery" : $"Gallery ({Details.Gallery.Count})";

    partial void OnDetailsChanged(ProjectDetails? value) => OnPropertyChanged(nameof(GalleryTabText));

    private async Task LoadAsync()
    {
        if (ProjectId == null) return;
        IsLoading = true;
        Error = null;
        try
        {
            var d = await ContentService.GetDetailsAsync(Source, ProjectId, CancellationToken.None);
            Details = d;
            Title = d.Title;
            Summary = d.Description;
            if (!string.IsNullOrEmpty(d.Author)) Author = d.Author;
            IconUrl = d.IconUrl ?? IconUrl;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warn($"Project load failed: {ex.Message}");
        }
        finally { IsLoading = false; }
        await LoadVersionsAsync();
    }

    partial void OnCompatibleOnlyChanged(bool value) => _ = LoadVersionsAsync();

    private async Task LoadVersionsAsync()
    {
        if (ProjectId == null) return;
        LoadingVersions = true;
        try
        {
            var list = await ContentService.GetVersionsAsync(Source, ProjectId, _owner.Type, _owner.Instance, CompatibleOnly, CancellationToken.None);
            Versions.Clear();
            foreach (var v in list) Versions.Add(new VersionItem(v));
            MarkInstalled();
            SelectedVersion = Versions.FirstOrDefault(v => v.IsInstalled) ?? Versions.FirstOrDefault();
        }
        catch (Exception ex) { Dialogs.Toast($"Could not load versions: {ex.Message}", true); }
        finally { LoadingVersions = false; }
    }

    private void MarkInstalled()
    {
        var vid = Installed?.Ref?.VersionId;
        foreach (var v in Versions) v.IsInstalled = vid != null && v.Version.Id == vid;
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnSelectedVersionChanged(VersionItem? value)
    {
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(CanInstall));
        _ = LoadChangelogAsync(value);
        _ = LoadDependenciesAsync(value);
    }

    partial void OnInstalledChanged(LocalItem? value)
    {
        OnPropertyChanged(nameof(LocalIcon));
        OnPropertyChanged(nameof(InstallText));
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
            if (!cts.IsCancellationRequested) ChangelogHtml = string.IsNullOrWhiteSpace(html) ? "<p class='empty'>No changelog provided.</p>" : html;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ChangelogHtml = $"<p class='empty'>Could not load changelog: {System.Net.WebUtility.HtmlEncode(ex.Message)}</p>"; }
        finally { if (_changelogCts == cts) LoadingChangelog = false; }
    }

    public string InstallText
    {
        get
        {
            if (IsInstalling) return "Installing…";
            if (SelectedVersion == null) return "Select a version";
            if (SelectedVersion.IsInstalled) return "Installed";
            return Installed != null ? "Switch to this version" : "Install";
        }
    }

    public bool CanInstall => SelectedVersion != null && !SelectedVersion.IsInstalled && !IsInstalling;

    partial void OnIsInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(CanInstall));
    }

    [RelayCommand]
    private async Task Install()
    {
        if (SelectedVersion == null || !CanInstall) return;
        IsInstalling = true;
        var progress = new Progress<TaskProgress>(p => InstallStatus = p.Message);
        try
        {
            var v = SelectedVersion.Version;
            await ContentService.InstallAsync(_owner.Instance, _owner.Type, v, Title, IconUrl, Installed, progress, CancellationToken.None);
            Dialogs.Toast($"{(Installed != null ? "Switched" : "Installed")} {Title} {v.VersionNumber}");
            await _owner.RefreshCommand.ExecuteAsync(null);
            Installed = _owner.FindInstalled(Source, ProjectId!);
            MarkInstalled();
        }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
        finally
        {
            IsInstalling = false;
            InstallStatus = null;
        }
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (Installed == null) return;
        await _owner.DeleteCommand.ExecuteAsync(Installed);
        Installed = _owner.Installed.Contains(Installed) ? Installed : null;
        MarkInstalled();
    }

    [RelayCommand]
    private void OpenWeb() => Dialogs.OpenUrl(Details?.WebUrl);

    [RelayCommand]
    private void OpenImage(GalleryImage img) => Dialogs.ShowImage(null, img.Url, img.Title);

    [RelayCommand]
    private void Back() => _owner.CloseDetailCommand.Execute(null);
}
