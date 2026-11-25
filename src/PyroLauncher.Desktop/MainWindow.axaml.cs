using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PyroLauncher.UI;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop;

public partial class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        DataContext = new MainViewModel();
        // macOS draws its traffic-light buttons over the top-left corner: push the sidebar content down.
        if (OperatingSystem.IsMacOS()) Sidebar.Margin = new Thickness(0, 26, 0, 0);
        ThemeService.Apply(ThemeManager.Active);
    }

    /// <summary>Called by ThemeService: shows the theme's background image, or lets the desktop show through (acrylic).</summary>
    public void SetBackdrop(Bitmap? image, bool desktopShowsThrough, Color background)
    {
        BackdropImage.Source = image;
        BackdropImage.IsVisible = image != null;
        if (desktopShowsThrough)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
        }
        else
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            Background = new SolidColorBrush(background);
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
