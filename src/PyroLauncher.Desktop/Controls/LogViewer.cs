using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.UI;

namespace PyroLauncher.Desktop.Controls;

/// <summary>Color-coded, virtualized log list with multi-select copy and filtering.</summary>
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
    private readonly ObservableCollection<LogLine> _shown = [];
    private INotifyCollectionChanged? _source;

    private static readonly IValueConverter LevelBrush = new FuncValueConverter<LogLevel, IBrush>(l => l switch
    {
        LogLevel.Warn => Res("LogWarn"),
        LogLevel.Error => Res("LogError"),
        LogLevel.Debug => Res("LogDebug"),
        LogLevel.Launcher => Res("LogLauncher"),
        _ => Res("LogInfo"),
    });

    private static readonly System.Text.RegularExpressions.Regex UrlRx = new(@"https?://[^\s""'<>\]\)]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Plain text, or text with clickable links when the line contains URLs.</summary>
    private static void FillLine(TextBlock t, string text)
    {
        var matches = UrlRx.Matches(text);
        if (matches.Count == 0)
        {
            t.Inlines = null;
            t.Text = text;
            return;
        }
        var inlines = new Avalonia.Controls.Documents.InlineCollection();
        int pos = 0;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (m.Index > pos) inlines.Add(new Avalonia.Controls.Documents.Run(text[pos..m.Index]));
            var url = m.Value.TrimEnd('.', ',', ';');
            var link = new TextBlock
            {
                Text = url,
                TextDecorations = TextDecorations.Underline,
                Cursor = new Cursor(StandardCursorType.Hand),
                Foreground = Res("Fg"),
            };
            ToolTip.SetTip(link, L.T("Open in browser"));
            link.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(link).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                Ui.OpenUrl(url);
            };
            inlines.Add(new Avalonia.Controls.Documents.InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom });
            if (url.Length < m.Value.Length) inlines.Add(new Avalonia.Controls.Documents.Run(m.Value[url.Length..]));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Avalonia.Controls.Documents.Run(text[pos..]));
        t.Text = null;
        t.Inlines = inlines;
    }

    private static IBrush Res(string key) =>
        Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.White;

    public LogViewer()
    {
        _list = new ListBox
        {
            ItemsSource = _shown,
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!,
            ItemTemplate = new FuncDataTemplate<LogLine>((_, _) =>
            {
                var t = new TextBlock { TextWrapping = TextWrapping.NoWrap };
                t.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding(nameof(LogLine.Level)) { Converter = LevelBrush });
                t.DataContextChanged += (_, _) => FillLine(t, (t.DataContext as LogLine)?.Text ?? "");
                return t;
            }),
        };
        _list.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x))
        {
            Setters = { new Avalonia.Styling.Setter(PaddingProperty, new Thickness(8, 1)), new Avalonia.Styling.Setter(MinHeightProperty, 0d),
                        new Avalonia.Styling.Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(0)) },
        });
        _list.Bind(FontSizeProperty, FontScale.For(13));
        ScrollViewer.SetHorizontalScrollBarVisibility(_list,Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var menu = new ContextMenu
        {
            Items =
            {
                MenuItem(L.T("Copy selected"), CopySelected),
                MenuItem(L.T("Copy all"), CopyAll),
                MenuItem(L.T("Select all"), () => _list.SelectAll()),
            },
        };
        _list.ContextMenu = menu;
        _list.KeyDown += (_, e) =>
        {
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (ctrl && e.Key == Key.C) { CopySelected(); e.Handled = true; }
            else if (ctrl && e.Key == Key.A) { _list.SelectAll(); e.Handled = true; }
        };

        Content = new Border
        {
            Background = Res("Bg1"),
            BorderBrush = Res("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            Child = _list,
        };
        DetachedFromVisualTree += (_, _) => Detach();
        AttachedToVisualTree += (_, _) => { Rebind(); ScrollToEnd(); };
    }

    private static MenuItem MenuItem(string header, Action a)
    {
        var m = new MenuItem { Header = header };
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
        _shown.Clear();
        if (Lines == null) return;
        foreach (var l in Lines) if (Matches(l)) _shown.Add(l);
        ScrollToEnd();
    }

    // New lines are appended incrementally; anything else (clear, trimming) rebuilds.
    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (LogLine l in e.NewItems) if (Matches(l)) _shown.Add(l);
            while (_shown.Count > 10000) _shown.RemoveAt(0);
            if (AutoScroll) ScrollToEnd();
        }
        else if (e.Action != NotifyCollectionChangedAction.Remove) Rebuild();
    }

    public void ScrollToEnd()
    {
        if (_shown.Count > 0) _list.ScrollIntoView(_shown.Count - 1);
    }

    public string AllText() => Lines == null ? "" : string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    private string SelectedText() =>
        string.Join(Environment.NewLine, (_list.SelectedItems?.Cast<LogLine>() ?? []).OrderBy(l => _shown.IndexOf(l)).Select(l => l.Text));

    private static async void SetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { await Ui.Platform.SetClipboardTextAsync(text); Dialogs.Toast(L.T("Copied to clipboard")); }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }

    public void CopyAll() => SetClipboard(AllText());
    public void CopySelected() => SetClipboard(_list.SelectedItems?.Count > 0 ? SelectedText() : AllText());
}
