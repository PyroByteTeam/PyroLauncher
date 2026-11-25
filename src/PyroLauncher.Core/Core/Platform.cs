using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using PyroLauncher.Instances;

namespace PyroLauncher.Core;

public record FileFilter(string Name, params string[] Patterns);

public enum WindowAction { Minimize, Hide, Restore, Quit }

/// <summary>Everything the shared code needs from the UI framework. The desktop app provides the implementation.</summary>
public interface IPlatformUi
{
    bool CheckAccess();
    void Post(Action action);

    /// <summary>Decodes an image into the UI framework's bitmap type (thread-safe; returns null on failure).</summary>
    object? DecodeImage(byte[] data, int decodeWidth);
    object? LoadImageFile(string path, int decodeWidth);

    /// <summary>Raw pixels (BGRA, 4 bytes per pixel, rows top to bottom) of a PNG/JPEG, or null.</summary>
    (int Width, int Height, byte[] Bgra)? DecodePixels(byte[] data);

    /// <summary>A region of an image, scaled up with nearest-neighbour (for pixel art like capes).</summary>
    object? CropImage(byte[] data, int x, int y, int width, int height, int scale);

    Task<string?> PickFileAsync(string title, IReadOnlyList<FileFilter> filters);
    Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FileFilter> filters);
    Task<string?> PickFolderAsync(string title, string? initialDirectory);

    Task SetClipboardTextAsync(string text);
    Task<bool> SetClipboardImageAsync(string path);

    void ShowConsole(Instance instance, bool activate);
    void CloseConsoles();
    void MainWindow(WindowAction action);
}

/// <summary>Access point for the platform layer plus UI-thread helpers.</summary>
public static class Ui
{
    public static IPlatformUi Platform { get; set; } = null!;

    public static bool CheckAccess() => Platform == null || Platform.CheckAccess();

    /// <summary>Runs on the UI thread: inline when already there, otherwise queued.</summary>
    public static void Run(Action action)
    {
        if (CheckAccess()) action();
        else Platform.Post(action);
    }

    /// <summary>Always queues (also from the UI thread), like Dispatcher.BeginInvoke.</summary>
    public static void Post(Action action)
    {
        if (Platform == null) action();
        else Platform.Post(action);
    }

    public static object? DecodeImage(byte[] data, int decodeWidth) => Platform?.DecodeImage(data, decodeWidth);
    public static object? LoadImageFile(string path, int decodeWidth) => Platform?.LoadImageFile(path, decodeWidth);

    public static void OpenPath(string path) => Shell(path);
    public static void OpenUrl(string url) => Shell(url);

    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
            else Shell(Path.GetDirectoryName(path) ?? path);
        }
        catch { }
    }

    private static void Shell(string target)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", [target]);
        else Process.Start("xdg-open", [target]);
    }
}

public enum DispatcherPriority { Normal, Background, ApplicationIdle }

/// <summary>Timer whose Tick runs on the UI thread (replacement for WPF's DispatcherTimer).</summary>
public sealed class DispatcherTimer
{
    private Timer? _timer;
    private int _busy;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
    public event EventHandler? Tick;
    public bool IsEnabled => _timer != null;

    public DispatcherTimer() { }
    public DispatcherTimer(DispatcherPriority _) { }

    public void Start()
    {
        Stop();
        _timer = new Timer(_ =>
        {
            // Skip a tick if the previous one is still queued, so a busy UI isn't flooded.
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            Ui.Post(() =>
            {
                Interlocked.Exchange(ref _busy, 0);
                if (_timer != null) Tick?.Invoke(this, EventArgs.Empty);
            });
        }, null, Interval, Interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

/// <summary>Header row emitted by a grouped <see cref="ListCollectionView"/>.</summary>
public sealed record GroupHeader(string Name);

public abstract class GroupDescription
{
    public abstract string GroupOf(object item);
}

public sealed class PropertyGroupDescription(string property) : GroupDescription
{
    public override string GroupOf(object item) =>
        item.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item)?.ToString() ?? "";
}

public interface ICollectionView : IEnumerable, INotifyCollectionChanged
{
    void Refresh();
    int Count { get; }
    bool IsEmpty { get; }
    List<GroupDescription> GroupDescriptions { get; }
}

/// <summary>
/// Filtered (and optionally grouped) live view over an observable list. Grouping is flattened:
/// a <see cref="GroupHeader"/> precedes each group, so any items control can show it with a second template.
/// </summary>
public sealed class ListCollectionView : ICollectionView, IList, INotifyPropertyChanged
{
    private readonly IList _source;
    private List<object> _items = [];
    private Predicate<object>? _filter;

    public List<GroupDescription> GroupDescriptions { get; } = [];
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ListCollectionView(IList source)
    {
        _source = source;
        if (source is INotifyCollectionChanged ncc) ncc.CollectionChanged += OnSourceChanged;
        Refresh();
    }

    private void OnSourceChanged(object? s, NotifyCollectionChangedEventArgs e) => Refresh();

    /// <summary>Stops following the source (call when the owner goes away).</summary>
    public void DetachFromSourceCollection()
    {
        if (_source is INotifyCollectionChanged ncc) ncc.CollectionChanged -= OnSourceChanged;
    }

    public Predicate<object>? Filter
    {
        get => _filter;
        set { _filter = value; Refresh(); }
    }

    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;

    public void Refresh()
    {
        var visible = _source.Cast<object>().Where(o => _filter == null || _filter(o)).ToList();
        if (GroupDescriptions.Count > 0)
        {
            var g = GroupDescriptions[0];
            var flat = new List<object>();
            foreach (var grp in visible.GroupBy(g.GroupOf))
            {
                flat.Add(new GroupHeader(grp.Key));
                flat.AddRange(grp);
            }
            visible = flat;
        }
        _items = visible;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
    }

    public IEnumerator GetEnumerator() => _items.GetEnumerator();

    // Read-only IList so UI frameworks can index it and follow Reset notifications.
    public object? this[int index] { get => _items[index]; set => throw new NotSupportedException(); }
    public bool IsReadOnly => true;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public bool Contains(object? value) => value != null && _items.Contains(value);
    public int IndexOf(object? value) => value == null ? -1 : _items.IndexOf(value);
    public void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
