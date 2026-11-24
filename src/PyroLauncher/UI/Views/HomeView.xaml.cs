using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PyroLauncher.Instances;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.UI.Views;

public partial class HomeView : UserControl
{
    private Point _downPos;
    private bool _pressed;
    private bool _dragged;

    public HomeView() => InitializeComponent();

    private HomeViewModel Vm => (HomeViewModel)DataContext;

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Ignore presses on the play button.
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        _downPos = e.GetPosition(this);
        _pressed = true;
        _dragged = false;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downPos;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
        _dragged = true;
        _pressed = false;
        if (sender is FrameworkElement fe && fe.DataContext is Instance inst)
            DragDrop.DoDragDrop(fe, new DataObject(typeof(Instance), inst), DragDropEffects.Move);
    }

    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed && !_dragged && sender is FrameworkElement { DataContext: Instance inst })
            Vm.OpenCommand.Execute(inst);
        _pressed = false;
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase) return true;
            if (d is Border { Name: "card" }) return false;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Group_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Instance)) ? DragDropEffects.Move : DragDropEffects.None;
        if (sender is Border b && e.Effects != DragDropEffects.None)
            b.BorderBrush = (Brush)FindResource("Line2");
        e.Handled = true;
    }

    private void Group_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = Brushes.Transparent;
    }

    private void Group_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = Brushes.Transparent;
        if (e.Data.GetData(typeof(Instance)) is Instance inst && sender is FrameworkElement { DataContext: CategoryGroup g })
            Vm.MoveInstance(inst, g.Key);
    }
}
