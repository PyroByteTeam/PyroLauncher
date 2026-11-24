using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

/// <summary>A file or folder of the instance in the export tree. Folder checkboxes are three-state.</summary>
public partial class ExportNode : ObservableObject
{
    public string Name { get; init; } = "";
    public string RelPath { get; init; } = "";
    public bool IsDir { get; init; }
    public long Size { get; set; }
    public ExportNode? Parent { get; init; }
    public ObservableCollection<ExportNode> Children { get; } = [];

    [ObservableProperty] private bool? _isChecked = true;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isExpanded;
    private bool _syncing;

    public string Glyph => IsDir ? "" : "";
    public string SizeText => DownloadManager.FormatBytes(Size);

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_syncing) return;
        // A click on a partial folder ticks everything.
        if (value == null) { IsChecked = true; return; }
        foreach (var c in Children) c.SetFromParent(value.Value);
        Parent?.SyncFromChildren();
    }

    private void SetFromParent(bool value)
    {
        _syncing = true;
        IsChecked = value;
        _syncing = false;
        foreach (var c in Children) c.SetFromParent(value);
    }

    private void SyncFromChildren()
    {
        var all = Children.All(c => c.IsChecked == true);
        var none = Children.All(c => c.IsChecked == false);
        _syncing = true;
        IsChecked = all ? true : none ? false : null;
        _syncing = false;
        Parent?.SyncFromChildren();
    }

    public IEnumerable<ExportNode> SelectedFiles()
    {
        if (IsChecked == false) yield break;
        if (!IsDir) { yield return this; yield break; }
        foreach (var c in Children)
            foreach (var f in c.SelectedFiles()) yield return f;
    }

    /// <summary>Shows nodes matching the search and the folders that lead to them.</summary>
    public bool ApplyFilter(string q)
    {
        bool self = q.Length == 0 || Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        bool child = false;
        foreach (var c in Children) child |= c.ApplyFilter(self ? "" : q);
        IsVisible = self || child;
        if (q.Length > 0 && child) IsExpanded = true;
        return IsVisible;
    }
}

public partial class ExportViewModel : ObservableObject
{
    public Instance Instance { get; }
    public ObservableCollection<ExportNode> Roots { get; } = [];

    public List<Choice<ExportFormat>> Formats { get; } =
    [
        new(ExportFormat.Modrinth, "Modrinth (.mrpack)"),
        new(ExportFormat.CurseForge, "CurseForge (.zip)"),
        new(ExportFormat.Pyro, "PyroLauncher (.pyropack)"),
    ];
    [ObservableProperty] private Choice<ExportFormat> _format;
    [ObservableProperty] private string _packName;
    [ObservableProperty] private string _packVersion = "1.0.0";
    [ObservableProperty] private string _author = SettingsService.Current.ExportAuthor ?? "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private double _progress = -1;
    [ObservableProperty] private string _selectionText = "";
    private CancellationTokenSource? _cts;

    public string FormatHint => Format.Value switch
    {
        ExportFormat.Modrinth => L.T("Mods, resource packs and shaders found on Modrinth are linked and downloaded on import; everything else is bundled. Works in the Modrinth App, Prism and other launchers."),
        ExportFormat.CurseForge => CurseForgeApi.HasKey
            ? L.T("Files found on CurseForge are linked; everything else is bundled in overrides. Works in the CurseForge app and most launchers.")
            : L.T("Without a CurseForge API key every file is bundled in overrides (the pack still works, it's just larger)."),
        _ => L.T("Bundles every selected file together with the instance's game settings and icon. Import it in PyroLauncher."),
    };

    public ExportViewModel(Instance inst)
    {
        Instance = inst;
        _packName = inst.Name;
        _format = Formats.FirstOrDefault(f => f.Value.ToString() == SettingsService.Current.ExportFormat) ?? Formats[0];
        _outputFolder = SettingsService.Current.ExportFolder is { Length: > 0 } f && Directory.Exists(f)
            ? f
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (inst.Config.Modpack?.VersionNumber is { } v) _packVersion = v;
        BuildTree();
    }

    private void BuildTree()
    {
        Roots.Clear();
        var root = new DirectoryInfo(Instance.GameDir);
        if (!root.Exists) return;
        foreach (var n in Children(root, "", null)) Roots.Add(n);
        foreach (var r in Roots)
            if (ModpackExporter.DefaultExcluded.Contains(r.Name) || r.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                r.IsChecked = false;
        foreach (var r in Roots) Hook(r);
        UpdateSelection();
    }

    private void Hook(ExportNode n)
    {
        n.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ExportNode.IsChecked)) UpdateSelectionSoon(); };
        foreach (var c in n.Children) Hook(c);
    }

    private static IEnumerable<ExportNode> Children(DirectoryInfo dir, string rel, ExportNode? parent)
    {
        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos().OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { yield break; }
        foreach (var e in entries)
        {
            var path = rel.Length == 0 ? e.Name : $"{rel}/{e.Name}";
            if (e is DirectoryInfo d)
            {
                var node = new ExportNode { Name = e.Name, RelPath = path, IsDir = true, Parent = parent };
                foreach (var c in Children(d, path, node)) node.Children.Add(c);
                node.Size = node.Children.Sum(c => c.Size);
                yield return node;
            }
            else if (e is FileInfo f)
            {
                yield return new ExportNode { Name = e.Name, RelPath = path, Parent = parent, Size = f.Length };
            }
        }
    }

    private bool _selectionPending;
    private void UpdateSelectionSoon()
    {
        if (_selectionPending) return;
        _selectionPending = true;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => { _selectionPending = false; UpdateSelection(); },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateSelection()
    {
        var files = Roots.SelectMany(r => r.SelectedFiles()).ToList();
        SelectionText = L.F("{0} files selected · {1}", files.Count, DownloadManager.FormatBytes(files.Sum(f => f.Size)));
    }

    partial void OnSearchChanged(string value)
    {
        var q = value.Trim();
        foreach (var r in Roots) r.ApplyFilter(q);
    }

    partial void OnFormatChanged(Choice<ExportFormat> value) => OnPropertyChanged(nameof(FormatHint));

    [RelayCommand]
    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Choose where to save the modpack"), InitialDirectory = OutputFolder };
        if (dlg.ShowDialog() == true) OutputFolder = dlg.FolderName;
    }

    [RelayCommand] private void SelectAll() { foreach (var r in Roots) r.IsChecked = true; }
    [RelayCommand] private void SelectNone() { foreach (var r in Roots) r.IsChecked = false; }

    [RelayCommand]
    private void ResetDefaults()
    {
        foreach (var r in Roots)
            r.IsChecked = !(ModpackExporter.DefaultExcluded.Contains(r.Name) || r.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private async Task Export()
    {
        if (IsBusy) return;
        var files = Roots.SelectMany(r => r.SelectedFiles()).Select(f => f.RelPath).ToList();
        if (files.Count == 0) { Dialogs.Toast(L.T("Select at least one file to export."), true); return; }
        if (string.IsNullOrWhiteSpace(OutputFolder)) { Dialogs.Toast(L.T("Choose a folder to save the modpack to."), true); return; }

        var name = string.IsNullOrWhiteSpace(PackName) ? Instance.Name : PackName.Trim();
        var fileName = AppPaths.SanitizeFileName($"{name} {PackVersion}".Trim()) + ModpackExporter.Extension(Format.Value);
        var output = Path.Combine(OutputFolder, fileName);
        if (File.Exists(output) && !await Dialogs.ConfirmAsync(L.T("Replace file"), L.F("{0} already exists. Replace it?", fileName), L.T("Replace")))
            return;

        var s = SettingsService.Current;
        s.ExportFolder = OutputFolder;
        s.ExportFormat = Format.Value.ToString();
        s.ExportAuthor = Author;
        SettingsService.Save();

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<TaskProgress>(p => { Status = p.Message; Progress = p.Fraction; });
        try
        {
            var o = new ExportOptions(Format.Value, name, PackVersion.Trim(), Author.Trim(), Summary.Trim(), files, output);
            var result = await ModpackExporter.ExportAsync(Instance, o, progress, _cts.Token);
            Dialogs.Close(this);
            Dialogs.Toast(Format.Value == ExportFormat.Pyro
                ? L.F("Exported {0}",Path.GetFileName(result.Path))
                : L.F("Exported {0}: {1} linked, {2} bundled files", Path.GetFileName(result.Path), result.Linked, result.Bundled));
            Dialogs.RevealInExplorer(result.Path);
        }
        catch (OperationCanceledException) { Status = L.T("Cancelled"); }
        catch (Exception ex)
        {
            Log.Error("Export failed", ex);
            Dialogs.Toast(L.F("Export failed: {0}", ex.Message), true);
        }
        finally
        {
            IsBusy = false;
            Progress = -1;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) { _cts?.Cancel(); return; }
        Dialogs.Close(this);
    }
}
