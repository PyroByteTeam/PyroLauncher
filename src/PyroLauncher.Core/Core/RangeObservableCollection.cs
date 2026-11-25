using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PyroLauncher.Core;

/// <summary>
/// ObservableCollection with batch operations that raise one change notification instead of one per item.
/// Used by logs, where thousands of lines can arrive at once and per-item events freeze the UI.
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;
        CheckReentrancy();
        var start = Count;
        foreach (var i in items) Items.Add(i);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, (IList)items.ToList(), start));
    }

    /// <summary>Removes the first <paramref name="count"/> items.</summary>
    public void RemoveFirst(int count)
    {
        count = Math.Min(count, Count);
        if (count <= 0) return;
        CheckReentrancy();
        var removed = new List<T>(count);
        for (int i = 0; i < count; i++) removed.Add(Items[i]);
        if (Items is List<T> list) list.RemoveRange(0, count);
        else for (int i = 0; i < count; i++) Items.RemoveAt(0);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, 0));
    }

    /// <summary>Replaces the whole content with a single Reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void RaiseCountChanged()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }
}
