using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.UI.Views;

public partial class InstanceView : UserControl
{
    private Point _down;
    private bool _pressed;

    public InstanceView() => InitializeComponent();

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = !IsInButton(e.OriginalSource as DependencyObject);
        _down = e.GetPosition(this);
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _down;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
        _pressed = false;
        if (sender is FrameworkElement { DataContext: SectionCard card } fe)
        {
            fe.Opacity = 0.5;
            DragDrop.DoDragDrop(fe, new DataObject(typeof(SectionCard), card), DragDropEffects.Move);
            fe.Opacity = 1;
        }
    }

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SectionCard)) ? DragDropEffects.Move : DragDropEffects.None;
        if (sender is Border b && e.Effects == DragDropEffects.Move) b.BorderBrush = (Brush)FindResource("Fg");
        e.Handled = true;
    }

    private void Card_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.ClearValue(Border.BorderBrushProperty);
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.ClearValue(Border.BorderBrushProperty);
        if (e.Data.GetData(typeof(SectionCard)) is SectionCard dragged && sender is FrameworkElement { DataContext: SectionCard target }
            && DataContext is InstanceViewModel vm)
            vm.MoveCard(dragged, target);
    }

    private static bool IsInButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
