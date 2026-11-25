using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        // macOS draws its traffic-light buttons over the top-left corner: push the sidebar content down.
        if (OperatingSystem.IsMacOS()) Sidebar.Margin = new Thickness(0, 26, 0, 0);
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
