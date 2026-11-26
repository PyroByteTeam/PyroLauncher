using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DispatcherPriority = Avalonia.Threading.DispatcherPriority;
using Avalonia.VisualTree;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.UI;

namespace PyroLauncher.Desktop.Controls;

/// <summary>
/// Color-coded, virtualized log list with filtering and editor-like text selection:
/// drag across lines to select, double-click selects a word, triple-click a line, Shift+click extends.
/// </summary>
public class LogViewer : UserControl
{
    public static readonly StyledProperty<ObservableCollection<LogLine>?> LinesProperty =
        AvaloniaProperty.Register<LogViewer, ObservableCollection<LogLine>?>(nameof(Lines));
    public static readonly StyledProperty<string?> FilterProperty = AvaloniaProperty.Register<LogViewer, string?>(nameof(Filter));
    public static readonly StyledProperty<bool> ErrorsOnlyProperty = AvaloniaProperty.Register<LogViewer, bool>(nameof(ErrorsOnly));
    public static readonly StyledProperty<bool> AutoScrollProperty = AvaloniaProperty.Register<LogViewer, bool>(nameof(AutoScroll), true);

    public ObservableCollection<LogLine>? Lines { get => GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public string? Filter { get => GetValue(FilterProperty); set => SetValue(FilterProperty, value); }
    public bool ErrorsOnly { get => GetValue(ErrorsOnlyProperty); set => SetValue(ErrorsOnlyProperty, value); }
    public bool AutoScroll { get => GetValue(AutoScrollProperty); set => SetValue(AutoScrollProperty, value); }

    private readonly ListBox _list;
    private readonly RangeObservableCollection<LogLine> _shown = [];
    private bool _scrollQueued;
    private const int MaxShown = 10000;
    private INotifyCollectionChanged? _source;

    // Selection: positions are (index into _shown, character offset). Anchor is where the drag started.
    private readonly record struct Pos(int Line, int Ch) : IComparable<Pos>
    {
        public int CompareTo(Pos o) => Line != o.Line ? Line.CompareTo(o.Line) : Ch.CompareTo(o.Ch);
    }
    private Pos _anchor, _caret;
    private bool _dragging;
    private Point _lastPointer;
    private readonly Avalonia.Threading.DispatcherTimer _dragScroll;
    private ScrollViewer? _scroller;

    private bool HasSelection => _anchor.CompareTo(_caret) != 0;
    private (Pos Start, Pos End) Range => _anchor.CompareTo(_caret) <= 0 ? (_anchor, _caret) : (_caret, _anchor);

    private static readonly IValueConverter LevelBrush = new FuncValueConverter<LogLevel, IBrush>(l => l switch
    {
        LogLevel.Warn => Res("LogWarn"),
        LogLevel.Error => Res("LogError"),
        LogLevel.Debug => Res("LogDebug"),
        LogLevel.Launcher => Res("LogLauncher"),
        _ => Res("LogInfo"),
    });

    private static readonly System.Text.RegularExpressions.Regex UrlRx = new(@"https?://[^\s""'<>\]\)]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Plain text, or text with underlined links when the line contains URLs. Inline text always equals the line text, so character offsets stay valid.</summary>
    private static void FillLine(TextBlock t, string text)
    {
        var matches = UrlRx.Matches(text);
        if (matches.Count == 0)
        {
            t.Inlines = null;
            t.Text = text;
            return;
        }
        var inlines = new InlineCollection();
        int pos = 0;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));
            var url = m.Value.TrimEnd('.', ',', ';');
            inlines.Add(new Run(url) { TextDecorations = TextDecorations.Underline, Foreground = Res("Fg") });
            if (url.Length < m.Value.Length) inlines.Add(new Run(m.Value[url.Length..]));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
        t.Text = null;
        t.Inlines = inlines;
    }

    /// <summary>The link under a character offset, if any.</summary>
    private static string? UrlAt(string text, int ch)
    {
        foreach (System.Text.RegularExpressions.Match m in UrlRx.Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', ';');
            if (ch >= m.Index && ch < m.Index + url.Length) return url;
        }
        return null;
    }

    private static IBrush Res(string key) =>
        Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.White;

    public LogViewer()
    {
        _list = new ListBox
        {
            ItemsSource = _shown,
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Ibeam),
            FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!,
            ItemTemplate = new FuncDataTemplate<LogLine>((_, _) =>
            {
                var t = new SelectableTextBlock { TextWrapping = TextWrapping.NoWrap, IsHitTestVisible = false, Focusable = false };
                t.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding(nameof(LogLine.Level)) { Converter = LevelBrush });
                t.DataContextChanged += (_, _) => { FillLine(t, (t.DataContext as LogLine)?.Text ?? ""); ApplySelection(t); };
                t.AttachedToVisualTree += (_, _) => ApplySelection(t);
                return t;
            }),
        };
        _list.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x))
        {
            Setters = { new Avalonia.Styling.Setter(PaddingProperty, new Thickness(8, 1)), new Avalonia.Styling.Setter(MinHeightProperty, 0d),
                        new Avalonia.Styling.Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(0)) },
        });
        _list.Bind(FontSizeProperty, FontScale.For(13));
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _list.ContainerIndexChanged += (_, e) => ApplySelection(e.Container);
        _list.ContainerPrepared += (_, e) => ApplySelection(e.Container);

        _list.ContextMenu = new ContextMenu
        {
            Items =
            {
                MenuItem(L.T("Copy"), CopySelected, "Ctrl+C"),
                MenuItem(L.T("Copy all"), CopyAll),
                MenuItem(L.T("Select all"), SelectAll, "Ctrl+A"),
            },
        };

        // Tunnel handlers so the ListBox never turns clicks into item selection.
        _list.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerCaptureLostEvent, (_, _) => StopDrag(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        Focusable = true;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _dragScroll = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => DragAutoScroll());

        Content = new Border
        {
            Background = Res("Bg1"),
            BorderBrush = Res("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            Child = _list,
        };
        DetachedFromVisualTree += (_, _) => { Detach(); StopDrag(); };
        AttachedToVisualTree += (_, _) => { Rebind(); ScrollToEnd(); };
    }

    private static MenuItem MenuItem(string header, Action a, string? gesture = null)
    {
        var m = new MenuItem { Header = header };
        if (gesture != null) m.InputGesture = KeyGesture.Parse(gesture);
        m.Click += (_, _) => a();
        return m;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LinesProperty) Rebind();
        else if (change.Property == FilterProperty || change.Property == ErrorsOnlyProperty) Rebuild();
    }

    private void Detach()
    {
        if (_source != null) _source.CollectionChanged -= OnChanged;
        _source = null;
    }

    private void Rebind()
    {
        Detach();
        if (Lines != null)
        {
            _source = Lines;
            _source.CollectionChanged += OnChanged;
        }
        Rebuild();
    }

    private bool Matches(LogLine l)
    {
        if (ErrorsOnly && l.Level is not (LogLevel.Error or LogLevel.Warn)) return false;
        return string.IsNullOrEmpty(Filter) || l.Text.Contains(Filter, StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild()
    {
        _anchor = _caret = default;
        _shown.ReplaceAll(Lines == null ? [] : Lines.Where(Matches).TakeLast(MaxShown));
        QueueScrollToEnd();
    }

    // New lines are appended in one batch; anything else (clear) rebuilds. Trimming of the source is ignored - we trim ourselves.
    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            var added = e.NewItems.Cast<LogLine>().Where(Matches).ToList();
            if (added.Count == 0) return;
            _shown.AddRange(added);
            if (_shown.Count > MaxShown)
            {
                var removed = _shown.Count - MaxShown + MaxShown / 10;
                _shown.RemoveFirst(removed);
                ShiftSelection(removed);
            }
            // Don't yank the view away while the user is selecting or has text selected.
            if (AutoScroll && !_dragging && !HasSelection) QueueScrollToEnd();
        }
        else if (e.Action != NotifyCollectionChangedAction.Remove) Rebuild();
    }

    private void ShiftSelection(int removed)
    {
        if (!HasSelection) { _anchor = _caret = default; return; }
        static Pos Shift(Pos p, int n) => p.Line - n < 0 ? new Pos(0, 0) : p with { Line = p.Line - n };
        _anchor = Shift(_anchor, removed);
        _caret = Shift(_caret, removed);
        RefreshSelection();
    }

    /// <summary>Scrolls once after the current burst of lines instead of forcing a layout pass per line.</summary>
    private void QueueScrollToEnd()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() => { _scrollQueued = false; ScrollToEnd(); }, DispatcherPriority.Background);
    }

    public void ScrollToEnd()
    {
        if (_shown.Count > 0) _list.ScrollIntoView(_shown.Count - 1);
    }

    // ---------- selection rendering ----------

    private static SelectableTextBlock? TextOf(Control? container) =>
        container as SelectableTextBlock ?? container?.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();

    private void ApplySelection(Control container)
    {
        if (TextOf(container) is { } t) ApplySelection(t, _list.IndexFromContainer(container));
    }

    private void ApplySelection(SelectableTextBlock t)
    {
        if (t.FindAncestorOfType<ListBoxItem>() is { } item) ApplySelection(t, _list.IndexFromContainer(item));
    }

    private void ApplySelection(SelectableTextBlock t, int index)
    {
        int s = 0, e = 0;
        if (index >= 0 && HasSelection)
        {
            var (start, end) = Range;
            if (index >= start.Line && index <= end.Line)
            {
                var len = (t.DataContext as LogLine)?.Text.Length ?? 0;
                s = index == start.Line ? Math.Min(start.Ch, len) : 0;
                e = index == end.Line ? Math.Min(end.Ch, len) : len;
            }
        }
        if (t.SelectionStart != s || t.SelectionEnd != e)
        {
            t.SelectionStart = s;
            t.SelectionEnd = e;
        }
    }

    private void RefreshSelection()
    {
        foreach (var c in _list.GetRealizedContainers()) ApplySelection(c);
    }

    // ---------- pointer / keyboard ----------

    private ScrollViewer? Scroller => _scroller ??= _list.FindDescendantOfType<ScrollViewer>();

    /// <summary>Line and character under a point in list coordinates; points outside the rows clamp to the nearest row.</summary>
    private Pos? HitTest(Point p)
    {
        Control? best = null;
        int bestIndex = -1;
        double bestDist = double.MaxValue;
        foreach (var c in _list.GetRealizedContainers())
        {
            if (!c.IsVisible || c.TranslatePoint(default, _list) is not { } top) continue;
            var dist = p.Y < top.Y ? top.Y - p.Y : p.Y > top.Y + c.Bounds.Height ? p.Y - top.Y - c.Bounds.Height : 0;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = c;
                bestIndex = _list.IndexFromContainer(c);
                if (dist == 0) break;
            }
        }
        if (best == null || bestIndex < 0 || bestIndex >= _shown.Count || TextOf(best) is not { } t) return null;

        var len = _shown[bestIndex].Text.Length;
        if (bestDist > 0 && _list.TranslatePoint(p, best) is { } local)
        {
            // Above the first visible row → its start, below the last → its end.
            if (local.Y < 0) return new Pos(bestIndex, 0);
            if (local.Y > best.Bounds.Height) return new Pos(bestIndex, len);
        }
        if (_list.TranslatePoint(p, t) is not { } pt) return new Pos(bestIndex, 0);
        var y = Math.Clamp(pt.Y, 0, Math.Max(0, t.Bounds.Height - 1));
        var hit = t.TextLayout.HitTestPoint(new Point(pt.X, y));
        return new Pos(bestIndex, Math.Clamp(hit.TextPosition, 0, len));
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_list).Properties;
        e.Handled = true;
        Focus(NavigationMethod.Pointer);
        var pos = e.GetPosition(_list);
        if (HitTest(pos) is not { } hit)
        {
            if (props.IsLeftButtonPressed) ClearSelection();
            return;
        }
        if (props.IsRightButtonPressed)
        {
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        var text = _shown[hit.Line].Text;
        if (e.ClickCount == 2)
        {
            int s = hit.Ch, en = hit.Ch;
            while (s > 0 && !char.IsWhiteSpace(text[s - 1])) s--;
            while (en < text.Length && !char.IsWhiteSpace(text[en])) en++;
            _anchor = new Pos(hit.Line, s);
            _caret = new Pos(hit.Line, en);
        }
        else if (e.ClickCount >= 3)
        {
            _anchor = new Pos(hit.Line, 0);
            _caret = new Pos(hit.Line, text.Length);
        }
        else
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) || !HasSelection) _anchor = hit;
            _caret = hit;
            _dragging = true;
            _lastPointer = pos;
            e.Pointer.Capture(_list);
            _dragScroll.Start();
        }
        RefreshSelection();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(_list);
        if (_dragging)
        {
            _lastPointer = pos;
            if (HitTest(pos) is { } hit && hit.CompareTo(_caret) != 0)
            {
                _caret = hit;
                RefreshSelection();
            }
            return;
        }
        var overLink = HitTest(pos) is { } h && UrlAt(_shown[h.Line].Text, h.Ch) != null && IsOverText(pos);
        _list.Cursor = new Cursor(overLink ? StandardCursorType.Hand : StandardCursorType.Ibeam);
    }

    private bool IsOverText(Point p)
    {
        foreach (var c in _list.GetRealizedContainers())
            if (c.TranslatePoint(default, _list) is { } top && p.Y >= top.Y && p.Y < top.Y + c.Bounds.Height)
                return TextOf(c) is { } t && _list.TranslatePoint(p, t) is { } pt && pt.X >= 0 && pt.X <= t.TextLayout.WidthIncludingTrailingWhitespace;
        return false;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || e.InitialPressMouseButton != MouseButton.Left) return;
        e.Handled = true;
        var pos = e.GetPosition(_list);
        StopDrag();
        e.Pointer.Capture(null);
        // A plain click (no drag) on a link opens it.
        if (!HasSelection && HitTest(pos) is { } hit && IsOverText(pos) && UrlAt(_shown[hit.Line].Text, hit.Ch) is { } url)
            Ui.OpenUrl(url);
    }

    private void StopDrag()
    {
        _dragging = false;
        _dragScroll.Stop();
    }

    /// <summary>While dragging past the top or bottom edge, keep scrolling and extending the selection.</summary>
    private void DragAutoScroll()
    {
        if (!_dragging || Scroller is not { } sv) return;
        var h = _list.Bounds.Height;
        double dy = _lastPointer.Y < 0 ? _lastPointer.Y : _lastPointer.Y > h ? _lastPointer.Y - h : 0;
        if (dy == 0) return;
        var step = Math.Sign(dy) * Math.Min(200, 4 + Math.Abs(dy) / 2);
        sv.Offset = sv.Offset.WithY(Math.Clamp(sv.Offset.Y + step, 0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
        Dispatcher.UIThread.Post(() =>
        {
            if (_dragging && HitTest(_lastPointer) is { } hit) { _caret = hit; RefreshSelection(); }
        }, DispatcherPriority.Background);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key is Key.C or Key.Insert) { CopySelected(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.Escape && HasSelection) { ClearSelection(); e.Handled = true; return; }
        if (Scroller is not { } sv) return;
        var line = FontSize * 1.4;
        double? y = e.Key switch
        {
            Key.Up => sv.Offset.Y - line,
            Key.Down => sv.Offset.Y + line,
            Key.PageUp => sv.Offset.Y - sv.Viewport.Height,
            Key.PageDown => sv.Offset.Y + sv.Viewport.Height,
            Key.Home when ctrl => 0,
            Key.End when ctrl => sv.Extent.Height,
            _ => null,
        };
        if (y is { } v)
        {
            sv.Offset = sv.Offset.WithY(Math.Clamp(v, 0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
            e.Handled = true;
        }
    }

    public void SelectAll()
    {
        if (_shown.Count == 0) return;
        _anchor = new Pos(0, 0);
        _caret = new Pos(_shown.Count - 1, _shown[^1].Text.Length);
        RefreshSelection();
    }

    public void ClearSelection()
    {
        _anchor = _caret = default;
        RefreshSelection();
    }

    // ---------- clipboard ----------

    public string AllText() => Lines == null ? "" : string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    public string SelectedText()
    {
        if (!HasSelection || _shown.Count == 0) return "";
        var (start, end) = Range;
        var sb = new StringBuilder();
        for (int i = start.Line; i <= Math.Min(end.Line, _shown.Count - 1); i++)
        {
            var text = _shown[i].Text;
            var s = i == start.Line ? Math.Min(start.Ch, text.Length) : 0;
            var en = i == end.Line ? Math.Min(end.Ch, text.Length) : text.Length;
            if (i > start.Line) sb.Append(Environment.NewLine);
            sb.Append(text, s, Math.Max(0, en - s));
        }
        return sb.ToString();
    }

    private static async void SetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { await Ui.Platform.SetClipboardTextAsync(text); Dialogs.Toast(L.T("Copied to clipboard")); }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }

    public void CopyAll() => SetClipboard(AllText());

    /// <summary>Copies the highlighted text, or the whole log when nothing is highlighted.</summary>
    public void CopySelected() => SetClipboard(HasSelection ? SelectedText() : AllText());
}
