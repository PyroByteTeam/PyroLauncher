using Avalonia.Controls;
using Avalonia.Input;

namespace PyroLauncher.Desktop.Views;

public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();

    private void Drag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window w && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) w.BeginMoveDrag(e);
    }
}
