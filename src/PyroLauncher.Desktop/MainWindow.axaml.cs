using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PyroLauncher.Core;
using PyroLauncher.UI;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop;

public partial class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }

    /// <summary>The state to go back to after being minimized or hidden (Normal, Maximized or FullScreen).</summary>
    public WindowState RestoreState { get; private set; } = WindowState.Normal;
    /// <summary>The state to go back to when leaving fullscreen.</summary>
    private WindowState _beforeFullscreen = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        DataContext = new MainViewModel();
        // macOS draws its traffic-light buttons over the top-left corner: push the sidebar content down.
        if (OperatingSystem.IsMacOS()) Sidebar.Margin = new Thickness(0, 26, 0, 0);
        ThemeService.Apply(ThemeManager.Active);
        if (SettingsService.Current.StartFullscreen) WindowState = WindowState.FullScreen;
        UpdateHeader();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        var state = change.GetNewValue<WindowState>();
        var old = change.GetOldValue<WindowState>();
        if (state == WindowState.FullScreen && old is WindowState.Normal or WindowState.Maximized) _beforeFullscreen = old;
        if (state != WindowState.Minimized) RestoreState = state;
        UpdateHeader();
    }

    /// <summary>Caption buttons sit top-right on Windows and Linux; in fullscreen there are none, so the exit button takes their place.</summary>
    private void UpdateHeader()
    {
        var fullscreen = WindowState == WindowState.FullScreen;
        ExitFullscreenButton.IsVisible = fullscreen;
        HeaderRight.Margin = new Thickness(0, 0, fullscreen || OperatingSystem.IsMacOS() ? 12 : 150, 0);
    }

    public void ToggleFullscreen() =>
        WindowState = WindowState == WindowState.FullScreen ? _beforeFullscreen : WindowState.FullScreen;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.None)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void OpenLanPage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => LanStatusButton.Flyout?.Hide();

    private void ExitFullscreen_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen) ToggleFullscreen();
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
        if (WindowState == WindowState.FullScreen) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
