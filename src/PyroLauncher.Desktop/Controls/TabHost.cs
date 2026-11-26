using Avalonia;
using Avalonia.Controls;

namespace PyroLauncher.Desktop.Controls;

/// <summary>
/// Shows the view for <see cref="Current"/>, keeping every view it has built alive (hidden) while the page is open.
/// Switching back to a tab is then just a visibility change: no rebuilding hundreds of mod rows, and no tearing them
/// down when leaving. Hidden views aren't measured or rendered, so they cost memory only, not time.
/// </summary>
public class TabHost : Panel
{
    public static readonly StyledProperty<object?> CurrentProperty = AvaloniaProperty.Register<TabHost, object?>(nameof(Current));

    public object? Current { get => GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }

    private readonly Dictionary<object, Control> _views = new(ReferenceEqualityComparer.Instance);
    private static readonly ViewLocator Locator = new();

    public TabHost()
    {
        // A new page model means a new set of tabs: drop the views of the old one.
        DataContextChanged += (_, _) =>
        {
            if (_views.Count == 0) return;
            _views.Clear();
            Children.Clear();
            Show(Current);
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CurrentProperty) Show(change.NewValue);
    }

    private void Show(object? vm)
    {
        if (vm != null && !_views.ContainsKey(vm))
        {
            var view = Locator.Build(vm) ?? new TextBlock();
            view.DataContext = vm;
            _views[vm] = view;
            Children.Add(view);
        }
        foreach (var (key, view) in _views) view.IsVisible = ReferenceEquals(key, vm);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size();
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Measure(availableSize);
            size = new Size(Math.Max(size.Width, c.DesiredSize.Width), Math.Max(size.Height, c.DesiredSize.Height));
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var c in Children)
            if (c.IsVisible) c.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
