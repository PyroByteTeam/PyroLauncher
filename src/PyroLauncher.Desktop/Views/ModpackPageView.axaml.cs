using Avalonia;
using Avalonia.Controls;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop.Views;

public partial class ModpackPageView : UserControl
{
    public ModpackPageView()
    {
        InitializeComponent();
        // On Discover the page provides its own margins; inside an instance the instance page does.
        DataContextChanged += (_, _) =>
            Root.Margin = DataContext is ModpackPageViewModel { IsDiscoverMode: true } ? new Thickness(36, 0, 36, 0) : default;
    }
}
