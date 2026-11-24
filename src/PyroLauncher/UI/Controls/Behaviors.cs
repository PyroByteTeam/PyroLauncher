using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PyroLauncher.UI.Controls;

public static class Behaviors
{
    /// <summary>Focuses the element every time it becomes visible (e.g. a search box inside a dropdown).</summary>
    public static readonly DependencyProperty FocusWhenVisibleProperty = DependencyProperty.RegisterAttached(
        "FocusWhenVisible", typeof(bool), typeof(Behaviors), new PropertyMetadata(false, OnChanged));

    public static bool GetFocusWhenVisible(DependencyObject d) => (bool)d.GetValue(FocusWhenVisibleProperty);
    public static void SetFocusWhenVisible(DependencyObject d, bool v) => d.SetValue(FocusWhenVisibleProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.IsVisibleChanged -= OnVisible;
        if (e.NewValue is true) el.IsVisibleChanged += OnVisible;
    }

    private static void OnVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not UIElement el) return;
        el.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            el.Focus();
            Keyboard.Focus(el);
        });
    }
}
