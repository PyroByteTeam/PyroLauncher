using Avalonia.Controls;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop.Views;

public partial class DiscoverView : UserControl
{
    private DiscoverViewModel? _vm;

    public DiscoverView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => { if (_vm != null) _vm.ResultsReset -= ScrollTop; };
        // Infinite scroll: load the next page when close to the bottom.
        Scroller.ScrollChanged += (_, _) =>
        {
            if (_vm == null || Scroller.Extent.Height <= Scroller.Viewport.Height) return;
            if (Scroller.Offset.Y + Scroller.Viewport.Height > Scroller.Extent.Height - 400) _vm.LoadMoreCommand.Execute(null);
        };
    }

    private void Attach()
    {
        if (_vm != null) _vm.ResultsReset -= ScrollTop;
        _vm = DataContext as DiscoverViewModel;
        if (_vm != null) _vm.ResultsReset += ScrollTop;
    }

    private void ScrollTop() => Scroller.ScrollToHome();
}
