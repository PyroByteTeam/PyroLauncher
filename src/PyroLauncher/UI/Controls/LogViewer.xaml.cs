using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.Controls;

/// <summary>Color-coded, virtualized log list with multi-select copy and filtering.</summary>
public partial class LogViewer : UserControl
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines), typeof(ObservableCollection<LogLine>), typeof(LogViewer), new PropertyMetadata(null, (d, _) => ((LogViewer)d).Rebind()));

    public static readonly DependencyProperty FilterProperty = DependencyProperty.Register(
        nameof(Filter), typeof(string), typeof(LogViewer), new PropertyMetadata("", (d, _) => ((LogViewer)d).ApplyFilter()));

    public static readonly DependencyProperty ErrorsOnlyProperty = DependencyProperty.Register(
        nameof(ErrorsOnly), typeof(bool), typeof(LogViewer), new PropertyMetadata(false, (d, _) => ((LogViewer)d).ApplyFilter()));

    public static readonly DependencyProperty AutoScrollProperty = DependencyProperty.Register(
        nameof(AutoScroll), typeof(bool), typeof(LogViewer), new PropertyMetadata(true));

    public ObservableCollection<LogLine>? Lines { get => (ObservableCollection<LogLine>?)GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public string Filter { get => (string)GetValue(FilterProperty); set => SetValue(FilterProperty, value); }
    public bool ErrorsOnly { get => (bool)GetValue(ErrorsOnlyProperty); set => SetValue(ErrorsOnlyProperty, value); }
    public bool AutoScroll { get => (bool)GetValue(AutoScrollProperty); set => SetValue(AutoScrollProperty, value); }

    private ListCollectionView? _view;
    private INotifyCollectionChanged? _source;

    public LogViewer()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => { Rebind(); ScrollToEnd(); };
    }

    private void Detach()
    {
        if (_source != null) _source.CollectionChanged -= OnChanged;
        _source = null;
    }

    private void Rebind()
    {
        Detach();
        if (Lines == null) { List.ItemsSource = null; return; }
        _view = new ListCollectionView(Lines) { Filter = Matches };
        List.ItemsSource = _view;
        _source = Lines;
        _source.CollectionChanged += OnChanged;
    }

    private bool Matches(object o)
    {
        if (o is not LogLine l) return true;
        if (ErrorsOnly && l.Level is not (LogLevel.Error or LogLevel.Warn)) return false;
        return string.IsNullOrEmpty(Filter) || l.Text.Contains(Filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter() => _view?.Refresh();

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && AutoScroll) ScrollToEnd();
    }

    public void ScrollToEnd()
    {
        if (List.Items.Count > 0) List.ScrollIntoView(List.Items[^1]);
    }

    public string AllText() => Lines == null ? "" : string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    public string SelectedText() =>
        string.Join(Environment.NewLine, List.SelectedItems.Cast<LogLine>()
            .OrderBy(l => Lines?.IndexOf(l) ?? 0).Select(l => l.Text));

    private static void SetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Dialogs.Toast("Copied to clipboard"); }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }

    public void CopyAll() => SetClipboard(AllText());
    public void CopySelected() => SetClipboard(List.SelectedItems.Count > 0 ? SelectedText() : AllText());

    private void Copy_Executed(object sender, ExecutedRoutedEventArgs e) => CopySelected();
    private void SelectAll_Executed(object sender, ExecutedRoutedEventArgs e) => List.SelectAll();
    private void CopySelected_Click(object sender, RoutedEventArgs e) => CopySelected();
    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyAll();
    private void SelectAllMenu_Click(object sender, RoutedEventArgs e) => List.SelectAll();
}
