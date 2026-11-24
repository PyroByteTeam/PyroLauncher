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

public partial class ProjectDetailViewModel : ObservableObject
{
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
