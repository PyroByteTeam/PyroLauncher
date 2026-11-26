using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

public partial class LauncherOption : ObservableObject
{
    public ExternalLauncher Launcher { get; init; }
    public string Name => LauncherImport.DisplayName(Launcher);
    public string Glyph { get; init; } = "";
    public string? DetectedFolder { get; init; }
    public bool IsDetected => DetectedFolder != null;
    [ObservableProperty] private bool _isSelected;
}

public partial class ImportCandidate : ObservableObject
{
    public ExternalInstance Source { get; init; } = null!;
    public string Name => Source.Name;
    public string Details => L.F("Minecraft {0} · {1}", Source.MinecraftVersion, Source.LoaderText);
    public bool CanImport => Source.Unsupported == null;
    /// <summary>An instance with the same name already exists in PyroLauncher (probably imported before).</summary>
    public bool AlreadyExists { get; init; }
    public string? Note => Source.Unsupported ?? (AlreadyExists ? L.T("An instance with this name already exists.") : null);
    [ObservableProperty] private bool _isChecked;
    /// <summary>Result after importing: null while pending, then "✓" or the error.</summary>
    [ObservableProperty] private string? _result;
    [ObservableProperty] private bool _failed;
}

/// <summary>
/// Import instances from another launcher in four steps: pick the launcher, pick its folder, choose instances, import.
/// The first step also offers a modpack file (.mrpack, .zip, .pyropack), which has a single step of its own.
/// </summary>
public partial class ImportLauncherViewModel : ObservableObject
{
    public const string StepLauncher = "launcher", StepFolder = "folder", StepInstances = "instances", StepImport = "import", StepFile = "file";

    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepNumber), nameof(CanGoBack), nameof(IsFileStep), nameof(ShowSteps))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string _step = StepLauncher;

    public bool IsFileStep => Step == StepFile;
    /// <summary>The 1-2-3-4 indicator belongs to the launcher flow; the file flow has a single step.</summary>
    public bool ShowSteps => Step != StepFile;

    // ---------- from a modpack file ----------
    [ObservableProperty] private string? _filePath;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFileCommand))]
    private ModpackInfo? _fileInfo;
    [ObservableProperty] private string _fileName = "";

    [RelayCommand]
    private void ChooseFile()
    {
        foreach (var l in Launchers) l.IsSelected = false;
        Selected = null;
        Error = null;
        Step = StepFile;
    }

    [RelayCommand]
    private async Task BrowseFile()
    {
        var file = await Ui.Platform.PickFileAsync(L.T("Import modpack"), [new FileFilter(L.T("Modpacks"), "*.mrpack", "*.zip", "*.pyropack")]);
        if (file != null) SetFile(file);
    }

    /// <summary>Also called when a file is dropped on the dialog: jumps straight to the file step.</summary>
    public void SetFile(string path)
    {
        if (IsBusy) return;
        if (Step != StepFile) ChooseFile();
        FilePath = path;
        Error = null;
        FileInfo = null;
        try
        {
            FileInfo = ModpackImporter.Inspect(path);
            FileName = FileInfo.Name;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private bool CanImportFile() => FileInfo != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanImportFile))]
    private async Task ImportFile()
    {
        if (FilePath == null || FileInfo == null) return;
        IsBusy = true;
        ImportFileCommand.NotifyCanExecuteChanged();
        Error = null;
        _cts = new CancellationTokenSource();
        var progress = new Progress<TaskProgress>(p => { Status = p.Message; Progress = p.Fraction; });
        try
        {
            var name = string.IsNullOrWhiteSpace(FileName) ? FileInfo.Name : FileName.Trim();
            var category = Category == Categories[0] ? null : Category;
            var result = await ModpackImporter.ImportAsync(FilePath, name, category, progress, _cts.Token);
            Dialogs.Close(this);
            MainViewModel.Instance.OpenInstance(result.Instance);
            if (result.SkippedFiles.Count > 0) Dialogs.Show(new ManualDownloadsViewModel(result.Instance, result.SkippedFiles));
            else
            {
                Dialogs.Toast(L.F("Imported \"{0}\"", result.Instance.Name));
                Notifications.Notify(L.T("Instance imported"), result.Instance.Name, NotifyKind.Instance);
            }
        }
        catch (OperationCanceledException) { Error = L.T("Cancelled"); }
        catch (Exception ex)
        {
            Log.Error("Import failed", ex);
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Status = null;
            Progress = -1;
            ImportFileCommand.NotifyCanExecuteChanged();
        }
    }

    public int StepNumber => Step switch { StepLauncher => 1, StepFolder => 2, StepInstances => 3, _ => 4 };

    public List<LauncherOption> Launchers { get; } =
    [
        new() { Launcher = ExternalLauncher.Modrinth, Glyph = "", DetectedFolder = LauncherImport.DetectFolder(ExternalLauncher.Modrinth) },
        new() { Launcher = ExternalLauncher.Prism, Glyph = "", DetectedFolder = LauncherImport.DetectFolder(ExternalLauncher.Prism) },
        new() { Launcher = ExternalLauncher.CurseForge, Glyph = "", DetectedFolder = LauncherImport.DetectFolder(ExternalLauncher.CurseForge) },
        new() { Launcher = ExternalLauncher.Xmcl, Glyph = "", DetectedFolder = LauncherImport.DetectFolder(ExternalLauncher.Xmcl) },
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LauncherName), nameof(FolderHint))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private LauncherOption? _selected;

    public string LauncherName => Selected?.Name ?? "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string _folder = "";

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isScanning;

    public ObservableCollection<ImportCandidate> Candidates { get; } = [];
    public int CheckedCount => Candidates.Count(c => c.IsChecked);
    public string ImportButtonText => CheckedCount == 0 ? L.T("Import") : L.F("Import ({0})", CheckedCount);

    public List<string> Categories { get; } = [L.T("- None -"), .. SettingsService.Current.Categories];
    [ObservableProperty] private string _category = L.T("- None -");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private bool _isDone;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private double _progress = -1;
    [ObservableProperty] private string? _summary;

    public bool CanGoBack => Step != StepLauncher && !IsBusy && !IsDone;

    public string FolderHint => Selected?.Launcher switch
    {
        ExternalLauncher.Modrinth => L.T("The Modrinth App data folder, the one with app.db and the profiles folder. Its location is shown in the app under Settings → Default instance options."),
        ExternalLauncher.Prism => L.T("The Prism Launcher data folder, the one with the instances folder. In Prism, click Folders → Launcher root."),
        ExternalLauncher.CurseForge => L.T("The CurseForge Instances folder. In the CurseForge app, open Settings → Minecraft to see the modding folder; Instances is inside it."),
        ExternalLauncher.Xmcl => L.T("The X Minecraft Launcher data folder, the one with the instances folder. XMCL shows it in Settings → Data folder."),
        _ => "",
    };

    [RelayCommand]
    private void ChooseLauncher(LauncherOption option)
    {
        foreach (var l in Launchers) l.IsSelected = l == option;
        Selected = option;
        Folder = option.DetectedFolder ?? "";
        Error = null;
        Step = StepFolder;
    }

    [RelayCommand]
    private async Task Browse()
    {
        var initial = Directory.Exists(Folder) ? Folder : null;
        if (await Ui.Platform.PickFolderAsync(L.F("Select the {0} folder", LauncherName), initial) is { } f) Folder = f;
    }

    private bool CanNext() => Step switch
    {
        StepFolder => Selected != null && !string.IsNullOrWhiteSpace(Folder) && !IsScanning,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task Next()
    {
        if (Selected == null) return;
        Error = null;
        IsScanning = true;
        NextCommand.NotifyCanExecuteChanged();
        try
        {
            var launcher = Selected.Launcher;
            var folder = Folder.Trim();
            var found = await Task.Run(() => LauncherImport.Scan(launcher, folder));
            var existing = new HashSet<string>(InstanceManager.Instances.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var c in Candidates) c.PropertyChanged -= OnCandidateChanged;
            Candidates.Clear();
            foreach (var x in found)
            {
                var dup = existing.Contains(x.Name);
                var c = new ImportCandidate { Source = x, AlreadyExists = dup, IsChecked = x.Unsupported == null && !dup };
                c.PropertyChanged += OnCandidateChanged;
                Candidates.Add(c);
            }
            NotifyChecked();
            Step = StepInstances;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsScanning = false;
            NextCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnCandidateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportCandidate.IsChecked)) NotifyChecked();
    }

    private void NotifyChecked()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(ImportButtonText));
        ImportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        var all = Candidates.Where(c => c.CanImport).All(c => c.IsChecked);
        foreach (var c in Candidates.Where(c => c.CanImport)) c.IsChecked = !all;
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack) return;
        Error = null;
        Step = Step == StepInstances ? StepFolder : StepLauncher;
    }

    private bool CanImport() => CheckedCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        var todo = Candidates.Where(c => c.IsChecked && c.CanImport).ToList();
        if (todo.Count == 0) return;
        foreach (var c in Candidates) { c.Result = null; c.Failed = false; }
        // Only the chosen instances stay in the list, so the results read as a report.
        foreach (var c in Candidates.Except(todo).ToList()) Candidates.Remove(c);

        Step = StepImport;
        IsBusy = true;
        ImportCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        var category = Category == Categories[0] ? null : Category;
        using var job = DownloadManager.Begin(L.F("Importing from {0}", LauncherName));
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, job.Token).Token;
        int ok = 0, failed = 0;
        Instance? last = null;
        for (int i = 0; i < todo.Count; i++)
        {
            var c = todo[i];
            if (ct.IsCancellationRequested) { c.Result = L.T("Cancelled"); continue; }
            var prefix = L.F("{0} of {1}: {2}", i + 1, todo.Count, c.Name);
            Status = prefix;
            Progress = -1;
            var progress = new Progress<TaskProgress>(p => { Status = $"{prefix} - {p.Message}"; Progress = p.Fraction; });
            try
            {
                last = await LauncherImport.ImportAsync(c.Source, category, progress, ct);
                c.Result = L.T("Imported");
                ok++;
            }
            catch (OperationCanceledException) { c.Result = L.T("Cancelled"); }
            catch (Exception ex)
            {
                Log.Error($"Importing {c.Name} from {LauncherName} failed", ex);
                c.Result = ex.Message;
                c.Failed = true;
                failed++;
            }
        }
        if (failed > 0) job.Fail();

        IsBusy = false;
        IsDone = true;
        Progress = -1;
        Status = null;
        Summary = failed == 0
            ? L.F("Imported {0} of {1} instances. Press Install on an instance to download the game files.", ok, todo.Count)
            : L.F("Imported {0} of {1} instances; {2} failed. Press Install on an instance to download the game files.", ok, todo.Count, failed);
        if (ok > 0)
        {
            Dialogs.Toast(L.F("Imported {0} instances from {1}", ok, LauncherName));
            Notifications.Notify(L.T("Import finished"), L.F("Imported {0} instances from {1}", ok, LauncherName), NotifyKind.Instance);
        }
        _lastImported = ok == 1 ? last : null;
    }

    private Instance? _lastImported;

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) { _cts?.Cancel(); return; }
        Dialogs.Close(this);
        // A single imported instance: open it right away, like importing a modpack file does.
        if (IsDone && _lastImported != null) MainViewModel.Instance.OpenInstance(_lastImported);
    }
}
