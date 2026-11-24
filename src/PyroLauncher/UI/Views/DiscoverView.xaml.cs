using System.Windows;
using System.Windows.Controls;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.UI.Views;

public partial class DiscoverView : UserControl
{
    private DiscoverViewModel? _vm;

    public DiscoverView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.ResultsReset -= ScrollTop;
            _vm = DataContext as DiscoverViewModel;
            if (_vm != null) _vm.ResultsReset += ScrollTop;
        };
        Unloaded += (_, _) => { if (_vm != null) _vm.ResultsReset -= ScrollTop; };
        Loaded += (_, _) => { if (_vm != null) { _vm.ResultsReset -= ScrollTop; _vm.ResultsReset += ScrollTop; } };
    }

    private void ScrollTop() => Scroller.ScrollToTop();

    /// <summary>Infinite scroll: load the next page when close to the bottom.</summary>
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm == null || e.ExtentHeight <= e.ViewportHeight) return;
        if (e.VerticalOffset + e.ViewportHeight > e.ExtentHeight - 400)
            _vm.LoadMoreCommand.Execute(null);
    }
}
