using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PyroLauncher.Core;

/// <summary>
/// Mirrors another collection for display, but adds big lists a chunk at a time at background priority so a page with
/// hundreds of rows appears at once and stays responsive while the rest streams in. Later changes are applied as small
/// inserts / removes / moves instead of a full reset, so existing rows (and the scroll position) are kept.
/// </summary>
public sealed class IncrementalCollection : RangeObservableCollection<object>
{
    private readonly IEnumerable _source;
    private readonly int _chunk;
    private int _generation;
    private bool _isFilling;

    /// <summary>True while rows are still being added in the background.</summary>
    public bool IsFilling
    {
        get => _isFilling;
        private set
        {
            if (_isFilling == value) return;
            _isFilling = value;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsFilling)));
        }
    }

    public IncrementalCollection(IEnumerable source, int chunk = 20)
    {
        _source = source;
        _chunk = Math.Max(1, chunk);
        if (source is INotifyCollectionChanged ncc) ncc.CollectionChanged += OnSourceChanged;
        Sync();
    }

    public void Detach()
    {
        if (_source is INotifyCollectionChanged ncc) ncc.CollectionChanged -= OnSourceChanged;
        _generation++;
        IsFilling = false;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void Sync()
    {
        var target = _source.Cast<object>().ToList();
        var gen = ++_generation;

        var wanted = new HashSet<object>(target, ReferenceEqualityComparer.Instance);
        int kept = 0;
        foreach (var o in this) if (wanted.Contains(o)) kept++;

        // Mostly different (first fill, new filter): start over with one screenful, the rest follows.
        if (Count == 0 || kept < Count / 2)
        {
            ReplaceAll(target.Take(_chunk));
            ContinueFill(target, gen);
            return;
        }

        // Mostly the same (a mod added / removed / renamed): patch in place.
        for (int i = Count - 1; i >= 0; i--)
            if (!wanted.Contains(this[i])) RemoveAt(i);

        for (int i = 0; i < target.Count; i++)
        {
            if (i >= Count) break;
            if (ReferenceEquals(this[i], target[i])) continue;
            var j = IndexOfReference(target[i], i + 1);
            if (j > i) Move(j, i);
            else Insert(i, target[i]);
        }
        // Rows past the target can only be leftovers of an unfinished fill that are now out of order.
        while (Count > target.Count) RemoveAt(Count - 1);
        ContinueFill(target, gen);
    }

    private int IndexOfReference(object item, int start)
    {
        for (int k = start; k < Count; k++)
            if (ReferenceEquals(this[k], item)) return k;
        return -1;
    }

    /// <summary>Appends whatever of <paramref name="target"/> isn't shown yet, one chunk per background dispatcher pass.</summary>
    private void ContinueFill(List<object> target, int gen)
    {
        if (Count >= target.Count) { IsFilling = false; return; }
        IsFilling = true;
        Ui.PostBackground(() =>
        {
            if (gen != _generation) return;
            AddRange(target.Skip(Count).Take(_chunk).ToList());
            ContinueFill(target, gen);
        });
    }
}
