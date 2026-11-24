using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

public partial class PresetItemVm(PresetItem item) : ObservableObject
{
    public PresetItem Item { get; } = item;
    public string Title => Item.Title;
    public string? IconUrl => Item.IconUrl;
    public string TypeText => PresetsViewModel.TypeLabel(Item.Type);
    public string SourceText => Item.Source == ContentSource.Modrinth ? "Modrinth" : "CurseForge";
    public string Initials => new string(Title.Where(char.IsLetterOrDigit).Take(2).ToArray()) is { Length: > 0 } s ? s : "?";
}

public partial class PresetVm : ObservableObject
{
    public ContentPreset Model { get; }
    public ObservableCollection<PresetItemVm> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    [ObservableProperty] private string _name;

    public PresetVm(ContentPreset model)
    {
        Model = model;
        _name = model.Name;
        foreach (var i in model.Items.OrderBy(i => i.Type)) Items.Add(new PresetItemVm(i));
        ItemsView = new ListCollectionView(Items);
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PresetItemVm.TypeText)));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CountText));
    }

    partial void OnNameChanged(string value) => Model.Name = value;

    public string CountText
    {
        get
        {
            int m = Model.Items.Count(i => i.Type == ContentType.Mod),
                r = Model.Items.Count(i => i.Type == ContentType.ResourcePack),
                s = Model.Items.Count(i => i.Type == ContentType.ShaderPack);
            return L.F("{0} mods · {1} resource packs · {2} shaders", m, r, s);
        }
    }

    public void Add(PresetItem item)
    {
        Model.Items.Add(item);
        // Keep groups in Mods → Resource packs → Shaders order.
        var idx = Items.TakeWhile(i => i.Item.Type <= item.Type).Count();
        Items.Insert(idx, new PresetItemVm(item));
    }

    public void Remove(PresetItemVm vm)
    {
        Model.Items.Remove(vm.Item);
        Items.Remove(vm);
    }

    public bool Contains(ContentSource source, string id) => Model.Items.Any(i => i.Source == source && i.ProjectId == id);
}

/// <summary>Settings → Presets: lists of mods, resource packs and shaders that can be applied to instances.</summary>
public partial class PresetsViewModel : ObservableObject
{
    public ObservableCollection<PresetVm> Presets { get; } = [];
    [ObservableProperty] private PresetVm? _selected;

    // ---- search to add
    public List<Choice<ContentType>> Types { get; } =
    [
        new(ContentType.Mod, L.T("Mods")), new(ContentType.ResourcePack, L.T("Resource packs")), new(ContentType.ShaderPack, L.T("Shaders")),
    ];
    [ObservableProperty] private Choice<ContentType> _searchType;
    public List<Choice<ContentSource>> Platforms { get; } = [new(ContentSource.Modrinth, "Modrinth"), new(ContentSource.CurseForge, "CurseForge")];
    [ObservableProperty] private Choice<ContentSource> _platform;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private string? _searchError;
    public ObservableCollection<ProjectSummary> Results { get; } = [];
    private CancellationTokenSource? _cts;
    private readonly System.Windows.Threading.DispatcherTimer _debounce;

    public bool HasSelection => Selected != null;

    public static string TypeLabel(ContentType t) => t switch
    {
        ContentType.Mod => L.T("Mods"),
        ContentType.ResourcePack => L.T("Resource packs"),
        _ => L.T("Shaders"),
    };

    public PresetsViewModel()
    {
        _searchType = Types[0];
        _platform = Platforms[0];
        foreach (var p in PresetService.All) Presets.Add(new PresetVm(p));
        Selected = Presets.FirstOrDefault();
        _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = SearchAsync(); };
    }

    partial void OnSelectedChanged(PresetVm? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        MarkAdded();
        if (value != null && Results.Count == 0) _ = SearchAsync();
        RebuildApply();
    }

    // ---- apply to an instance
    /// <summary>"edit" shows the preset's contents and search; "apply" resolves it against an instance.</summary>
    [ObservableProperty] private string _mode = "edit";
    public ObservableCollection<Instance> Instances => InstanceManager.Instances;
    [ObservableProperty] private Instance? _targetInstance;
    [ObservableProperty] private ApplyPresetViewModel? _apply;
    public bool IsEditMode => Mode == "edit";
    public bool IsApplyMode => Mode == "apply";
    public bool HasInstances => Instances.Count > 0;

    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsApplyMode));
        RebuildApply();
    }

    partial void OnTargetInstanceChanged(Instance? value) => RebuildApply();

    private void RebuildApply()
    {
        if (Apply is { IsInstalling: true }) return; // don't interrupt a running install
        Apply?.Cancel();
        TargetInstance ??= InstanceManager.Instances.FirstOrDefault();
        Apply = IsApplyMode && Selected != null && TargetInstance != null && Selected.Model.Items.Count > 0
            ? new ApplyPresetViewModel(TargetInstance, Selected.Model)
            : null;
    }

    /// <summary>Opens the apply view for an instance (from the instance's settings).</summary>
    public void StartApply(Instance inst, ContentPreset? preset)
    {
        if (preset != null && Presets.FirstOrDefault(p => p.Model == preset) is { } vm) Selected = vm;
        Selected ??= Presets.FirstOrDefault();
        TargetInstance = inst;
        if (Mode == "apply") RebuildApply(); else Mode = "apply";
    }

    [RelayCommand] private void ShowEdit() => Mode = "edit";
    [RelayCommand] private void ShowApply() => Mode = "apply";
    [RelayCommand] private void RefreshApply() => RebuildApply();

    partial void OnQueryChanged(string value) { _debounce.Stop(); _debounce.Start(); }
    partial void OnSearchTypeChanged(Choice<ContentType> value) => _ = SearchAsync();
    partial void OnPlatformChanged(Choice<ContentSource> value) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Searching = true;
        SearchError = null;
        try
        {
            var type = SearchType.Value;
            var list = Platform.Value == ContentSource.Modrinth
                ? await ModrinthApi.SearchAsync(Query.Trim(), type, "", ModLoader.Vanilla, 0, cts.Token)
                : CurseForgeApi.HasKey
                    ? await CurseForgeApi.SearchAsync(Query.Trim(), type, "", ModLoader.Vanilla, 0, cts.Token)
                    : throw new InvalidOperationException(L.T("CurseForge search needs an API key (Settings → Launcher)."));
            if (cts.IsCancellationRequested) return;
            Results.Clear();
            foreach (var r in list) Results.Add(r);
            MarkAdded();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) SearchError = ex.Message; }
        finally { if (_cts == cts) Searching = false; }
    }

    /// <summary>Ticks results that are already in the selected preset.</summary>
    private void MarkAdded()
    {
        foreach (var r in Results) r.IsSelected = Selected?.Contains(r.Source, r.Id) == true;
    }

    [RelayCommand]
    private async Task New()
    {
        var name = await Dialogs.PromptAsync(L.T("New preset"), L.T("Preset name"), "", L.T("Create"));
        if (name == null) return;
        var model = new ContentPreset { Name = name };
        PresetService.All.Add(model);
        PresetService.Save();
        var vm = new PresetVm(model);
        Presets.Add(vm);
        Selected = vm;
    }

    [RelayCommand]
    private async Task NewFromInstance()
    {
        if (InstanceManager.Instances.Count == 0) { Dialogs.Toast(L.T("There are no instances yet."), true); return; }
        var names = InstanceManager.Instances.Select(i => i.Name).ToList();
        var choice = await Dialogs.ChooseAsync(L.T("Preset from instance"), L.T("Copy the mods, resource packs and shaders of an instance into a new preset."), names);
        var inst = InstanceManager.Instances.FirstOrDefault(i => i.Name == choice);
        if (inst == null) return;

        var model = new ContentPreset { Name = inst.Name };
        foreach (var type in new[] { ContentType.Mod, ContentType.ResourcePack, ContentType.ShaderPack })
        {
            var list = ContentStore.Get(inst, type);
            await list.RefreshAsync();
            foreach (var i in list.Items)
                if (i.Ref is { } r && !model.Items.Any(x => x.Source == r.Source && x.ProjectId == r.ProjectId))
                    model.Items.Add(new PresetItem { Type = type, Source = r.Source, ProjectId = r.ProjectId, Title = r.Title ?? i.Name, IconUrl = r.IconUrl ?? i.IconUrl });
        }
        PresetService.All.Add(model);
        PresetService.Save();
        var vm = new PresetVm(model);
        Presets.Add(vm);
        Selected = vm;
        Dialogs.Toast(L.F("Created \"{0}\" with {1} items. Files that aren't on Modrinth or CurseForge were skipped.", model.Name, model.Items.Count));
    }

    [RelayCommand]
    private async Task Rename()
    {
        if (Selected == null) return;
        var name = await Dialogs.PromptAsync(L.T("Rename preset"), L.T("Preset name"), Selected.Name);
        if (name == null) return;
        Selected.Name = name;
        PresetService.Save();
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected == null) return;
        var model = new ContentPreset
        {
            Name = L.F("{0} (copy)", Selected.Name),
            Items = Selected.Model.Items.Select(i => new PresetItem
            {
                Type = i.Type, Source = i.Source, ProjectId = i.ProjectId, Title = i.Title, IconUrl = i.IconUrl, Author = i.Author,
            }).ToList(),
        };
        PresetService.All.Add(model);
        PresetService.Save();
        var vm = new PresetVm(model);
        Presets.Add(vm);
        Selected = vm;
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected == null) return;
        if (!await Dialogs.ConfirmAsync(L.T("Delete preset"), L.F("Delete the preset \"{0}\"? Instances are not affected.", Selected.Name), L.T("Delete"), true))
            return;
        PresetService.All.Remove(Selected.Model);
        PresetService.Save();
        Presets.Remove(Selected);
        Selected = Presets.FirstOrDefault();
    }

    [RelayCommand]
    private void Toggle(ProjectSummary p)
    {
        if (Selected == null) return;
        var existing = Selected.Items.FirstOrDefault(i => i.Item.Source == p.Source && i.Item.ProjectId == p.Id);
        if (existing != null) Selected.Remove(existing);
        else Selected.Add(new PresetItem
        {
            Type = SearchType.Value, Source = p.Source, ProjectId = p.Id, Title = p.Title, IconUrl = p.IconUrl, Author = p.Author,
        });
        PresetService.Save();
        MarkAdded();
    }

    [RelayCommand]
    private void RemoveItem(PresetItemVm item)
    {
        Selected?.Remove(item);
        PresetService.Save();
        MarkAdded();
    }
}

// ======================================================================= applying a preset

public partial class PresetApplyRow(PresetItem item) : ObservableObject
{
    public PresetItem Item { get; } = item;
    public string Title => Item.Title;
    public string? IconUrl => Item.IconUrl;
    public string TypeText => PresetsViewModel.TypeLabel(Item.Type);
    public string Initials => new string(Title.Where(char.IsLetterOrDigit).Take(2).ToArray()) is { Length: > 0 } s ? s : "?";
    public ProjectVersion? Version { get; set; }

    [ObservableProperty] private PresetItemState _state = PresetItemState.Checking;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _statusText = L.T("Checking…");
    [ObservableProperty] private bool _done;

    public bool CanSelect => State is PresetItemState.Available or PresetItemState.AlreadyInstalled && !Done;
    partial void OnStateChanged(PresetItemState value) => OnPropertyChanged(nameof(CanSelect));
    partial void OnDoneChanged(bool value) => OnPropertyChanged(nameof(CanSelect));
}

/// <summary>Dialog that resolves a preset against an instance and installs the chosen items.</summary>
public partial class ApplyPresetViewModel : ObservableObject
{
    public Instance Instance { get; }
    public ContentPreset Preset { get; }
    public ObservableCollection<PresetApplyRow> Rows { get; } = [];
    public ICollectionView RowsView { get; }
    [ObservableProperty] private string _summary = L.T("Looking for compatible versions…");
    [ObservableProperty] private bool _isResolving = true;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private double _progress = -1;
    private readonly CancellationTokenSource _cts = new();

    public string TargetText => $"{Instance.Config.MinecraftVersion} · {Instance.LoaderText}";
    public int SelectedCount => Rows.Count(r => r.IsChecked && r.CanSelect);
    public string InstallText => SelectedCount == 0 ? L.T("Nothing selected") : L.F("Install {0} selected", SelectedCount);

    public ApplyPresetViewModel(Instance inst, ContentPreset preset)
    {
        Instance = inst;
        Preset = preset;
        foreach (var i in preset.Items.OrderBy(i => i.Type).ThenBy(i => i.Title))
        {
            var row = new PresetApplyRow(i);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PresetApplyRow.IsChecked) or nameof(PresetApplyRow.State) or nameof(PresetApplyRow.Done))
                {
                    OnPropertyChanged(nameof(SelectedCount));
                    OnPropertyChanged(nameof(InstallText));
                    InstallCommand.NotifyCanExecuteChanged();
                }
            };
            Rows.Add(row);
        }
        RowsView = new ListCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PresetApplyRow.TypeText)));
        _ = ResolveAsync();
    }

    private async Task ResolveAsync()
    {
        using var sem = new SemaphoreSlim(6);
        await Task.WhenAll(Rows.Select(async row =>
        {
            await sem.WaitAsync(_cts.Token);
            try
            {
                if (row.Item.Type == ContentType.Mod && Instance.Config.Loader == ModLoader.Vanilla)
                {
                    row.State = PresetItemState.Unavailable;
                    row.StatusText = L.T("Vanilla instance - mods need a mod loader");
                    return;
                }
                if (row.Item.Source == ContentSource.CurseForge && !CurseForgeApi.HasKey)
                {
                    row.State = PresetItemState.Failed;
                    row.StatusText = L.T("Needs a CurseForge API key");
                    return;
                }
                var installed = PresetService.IsInstalled(Instance, row.Item);
                var v = await PresetService.ResolveAsync(Instance, row.Item, _cts.Token);
                row.Version = v;
                if (installed)
                {
                    row.State = PresetItemState.AlreadyInstalled;
                    row.StatusText = L.T("Already in this instance");
                    row.IsChecked = false;
                }
                else if (v == null)
                {
                    row.State = PresetItemState.Unavailable;
                    row.StatusText = L.F("No version for {0}", TargetText);
                }
                else
                {
                    row.State = PresetItemState.Available;
                    row.StatusText = v.VersionNumber;
                    row.IsChecked = true;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                row.State = PresetItemState.Failed;
                row.StatusText = ex.Message;
            }
            finally { sem.Release(); }
        }));
        IsResolving = false;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int ok = Rows.Count(r => r.State == PresetItemState.Available),
            have = Rows.Count(r => r.State == PresetItemState.AlreadyInstalled),
            missing = Rows.Count(r => r.State is PresetItemState.Unavailable or PresetItemState.Failed);
        Summary = L.F("{0} can be installed · {1} already in the instance · {2} not available for {3}", ok, have, missing, TargetText);
    }

    [RelayCommand] private void SelectAll() { foreach (var r in Rows.Where(r => r.State == PresetItemState.Available && !r.Done)) r.IsChecked = true; }
    [RelayCommand] private void SelectNone() { foreach (var r in Rows) r.IsChecked = false; }

    private bool CanInstall() => !IsInstalling && !IsResolving && SelectedCount > 0;
    partial void OnIsResolvingChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();
    partial void OnIsInstallingChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        var todo = Rows.Where(r => r.IsChecked && r.CanSelect && r.Version != null).ToList();
        IsInstalling = true;
        int done = 0, failed = 0;
        using var job = DownloadManager.Begin(L.F("Preset \"{0}\" → {1}", Preset.Name, Instance.Name), _cts);
        try
        {
            foreach (var row in todo)
            {
                Status = L.F("Installing {0}… ({1}/{2})", row.Title, done + failed + 1, todo.Count);
                Progress = (double)(done + failed) / todo.Count;
                try
                {
                    // Already-installed items that the user ticked are reinstalled at the resolved version.
                    LocalItem? replace = row.State == PresetItemState.AlreadyInstalled
                        ? ContentStore.Get(Instance, row.Item.Type).Items.FirstOrDefault(i => i.Ref is { } r && r.Source == row.Item.Source && r.ProjectId == row.Item.ProjectId)
                        : null;
                    await ContentService.InstallAsync(Instance, row.Item.Type, row.Version!, row.Title, row.IconUrl, replace, null, _cts.Token, 1);
                    row.Done = true;
                    row.IsChecked = false;
                    row.StatusText = L.F("Installed {0}", row.Version!.VersionNumber);
                    done++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    row.StatusText = ex.Message;
                    row.State = PresetItemState.Failed;
                    failed++;
                }
            }
            Dialogs.Toast(failed == 0 ? L.F("Installed {0} items from \"{1}\"", done, Preset.Name)
                                      : L.F("Installed {0} items, {1} failed", done, failed), failed > 0);
            foreach (var t in new[] { ContentType.Mod, ContentType.ResourcePack, ContentType.ShaderPack })
                _ = ContentStore.Get(Instance, t).RefreshAsync();
        }
        catch (OperationCanceledException) { Dialogs.Toast(L.T("Cancelled")); }
        finally
        {
            IsInstalling = false;
            Status = null;
            Progress = -1;
        }
    }

    public void Cancel() => _cts.Cancel();

    [RelayCommand] private void CancelInstall() => _cts.Cancel();

    [RelayCommand] private void OpenInstance() => MainViewModel.Instance.OpenInstance(Instance);
}
