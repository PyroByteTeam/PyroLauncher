using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.Content;

/// <summary>
/// Live list of one instance's mods / resource packs / shader packs. Shared by every page that shows it (so going back is
/// instant), refreshed automatically when files are added, removed or renamed in the folder.
/// </summary>
public partial class ContentList : ObservableObject
{
    public Instance Instance { get; }
    public ContentType Type { get; }
    public ObservableCollection<LocalItem> Items { get; } = [];
    public string Folder => Path.Combine(Instance.GameDir, Type.Folder());

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private string? _activeShader;

    /// <summary>Raised on the UI thread after every refresh.</summary>
    public event Action? Refreshed;

    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;
    private readonly HashSet<string> _identifyTried = new(StringComparer.OrdinalIgnoreCase);
    private Task? _running;
    private bool _again;

    internal ContentList(Instance inst, ContentType type)
    {
        Instance = inst;
        Type = type;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshAsync(); };
        StartWatching();
        _ = RefreshAsync();
    }

    private void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            _watcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onChange = (_, e) => ScheduleRefresh(e.Name);
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Changed += onChange;
            _watcher.Renamed += (_, e) => ScheduleRefresh(e.Name);
        }
        catch (Exception ex) { Log.Warn($"Folder watch failed for {Folder}: {ex.Message}"); }
    }

    private void ScheduleRefresh(string? name)
    {
        if (name != null && (name.EndsWith(".part") || name.EndsWith(".tmp"))) return;
        Application.Current?.Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
    }

    /// <summary>Rescans the folder; unchanged files keep their existing objects (selection, update info, icons).</summary>
    public async Task RefreshAsync()
    {
        if (_running != null) { _again = true; await _running; return; }
        _running = RefreshCoreAsync();
        try { await _running; }
        finally { _running = null; }
        if (_again) { _again = false; await RefreshAsync(); }
    }

    private async Task RefreshCoreAsync()
    {
        IsLoading = Items.Count == 0;
        try
        {
            var scanned = await Task.Run(() => LocalContentScanner.Scan(Instance, Type));
            var old = Items.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);
            var merged = new List<LocalItem>();
            foreach (var s in scanned)
            {
                if (old.TryGetValue(s.FilePath, out var o) && o.Size == s.Size && o.Modified == s.Modified)
                {
                    if (s.Ref != null && !ReferenceEquals(o.Ref, s.Ref)) { o.Ref = s.Ref; o.IconUrl ??= s.IconUrl; }
                    merged.Add(o);
                }
                else merged.Add(s);
            }

            if (!merged.SequenceEqual(Items))
            {
                foreach (var i in Items) i.PropertyChanged -= OnItemChanged;
                Items.Clear();
                foreach (var i in merged)
                {
                    i.PropertyChanged += OnItemChanged;
                    Items.Add(i);
                }
            }
            ApplyActivation();
            UpdateCounts();
            Refreshed?.Invoke();
            _ = IdentifyNewAsync();
        }
        catch (Exception ex) { Log.Warn($"Scan of {Folder} failed: {ex.Message}"); }
        finally { IsLoading = false; }
    }

    private async Task IdentifyNewAsync()
    {
        var fresh = Items.Where(i => i.Ref == null && !i.IsDirectory && _identifyTried.Add(i.FilePath)).ToList();
        if (fresh.Count == 0) return;
        try { await ContentService.IdentifyAsync(Instance, fresh, CancellationToken.None); }
        catch (Exception ex) { Log.Warn($"Identify failed: {ex.Message}"); }
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalItem.IsEnabled)) UpdateCounts();
    }

    private void UpdateCounts()
    {
        EnabledCount = Items.Count(i => i.IsEnabled);
        ActiveShader = Type == ContentType.ShaderPack ? Items.FirstOrDefault(i => i.IsEnabled)?.Name : null;
    }

    // ------------------------------------------------------------------ resource pack / shader activation

    private void ApplyActivation()
    {
        if (Type == ContentType.Mod) return;
        HashSet<string> active;
        if (Type == ContentType.ResourcePack)
            active = GameOptions.ReadResourcePacks(Instance.GameDir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        else
            active = GameOptions.ReadShader(Instance.GameDir) is { } s ? [s] : [];

        foreach (var i in Items)
        {
            i.ActivationHandler ??= HandleActivation;
            i.SetActiveSilently(active.Contains(Path.GetFileName(i.FilePath)));
        }
    }

    private bool HandleActivation(LocalItem item, bool on)
    {
        try
        {
            if (Instance.IsRunning)
                UI.Dialogs.Toast("The game is running — the change applies the next time it starts.");

            if (Type == ContentType.ResourcePack)
            {
                // Keep the current order and append newly enabled packs on top (highest priority).
                var current = GameOptions.ReadResourcePacks(Instance.GameDir);
                var name = Path.GetFileName(item.FilePath);
                current.RemoveAll(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (on) current.Add(name);
                GameOptions.WriteResourcePacks(Instance.GameDir, current);
            }
            else
            {
                var mods = ContentStore.Get(Instance, ContentType.Mod).Items;
                var loader = GameOptions.DetectShaderLoader(mods);
                GameOptions.WriteShader(Instance.GameDir, on ? Path.GetFileName(item.FilePath) : null, loader);
                foreach (var other in Items.Where(i => i != item)) other.SetActiveSilently(false);
                if (on && loader == ShaderLoader.None)
                    UI.Dialogs.Toast("No shader mod found — install Iris (Fabric/NeoForge), Oculus (Forge) or OptiFine to use shader packs.", true);
            }
            Application.Current?.Dispatcher.BeginInvoke(UpdateCounts);
            return true;
        }
        catch (Exception ex)
        {
            UI.Dialogs.Toast($"Could not update game options: {ex.Message}", true);
            return false;
        }
    }
}

public static class ContentStore
{
    private static readonly Dictionary<(string dir, ContentType type), ContentList> Lists = [];

    /// <summary>Gets the shared live list for an instance's content folder (created and scanned on first use).</summary>
    public static ContentList Get(Instance inst, ContentType type)
    {
        var key = (inst.Dir.ToLowerInvariant(), type);
        if (!Lists.TryGetValue(key, out var list))
        {
            list = new ContentList(inst, type);
            Lists[key] = list;
        }
        return list;
    }
}
