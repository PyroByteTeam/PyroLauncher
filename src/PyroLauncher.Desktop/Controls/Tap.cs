using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using PyroLauncher.Content;

namespace PyroLauncher.Desktop.Controls;

/// <summary>
/// Click / double-click commands on any element (e.g. a row Border):
/// <c>c:Tap.Command="{Binding OpenCommand}" c:Tap.Parameter="{Binding}"</c>. Clicks on buttons, toggles and inputs inside are ignored.
/// </summary>
public class Tap : AvaloniaObject
{
    public static readonly AttachedProperty<ICommand?> CommandProperty = AvaloniaProperty.RegisterAttached<Tap, Control, ICommand?>("Command");
    public static readonly AttachedProperty<ICommand?> DoubleCommandProperty = AvaloniaProperty.RegisterAttached<Tap, Control, ICommand?>("DoubleCommand");
    public static readonly AttachedProperty<object?> ParameterProperty = AvaloniaProperty.RegisterAttached<Tap, Control, object?>("Parameter");

    public static ICommand? GetCommand(Control c) => c.GetValue(CommandProperty);
    public static void SetCommand(Control c, ICommand? v) => c.SetValue(CommandProperty, v);
    public static ICommand? GetDoubleCommand(Control c) => c.GetValue(DoubleCommandProperty);
    public static void SetDoubleCommand(Control c, ICommand? v) => c.SetValue(DoubleCommandProperty, v);
    public static object? GetParameter(Control c) => c.GetValue(ParameterProperty);
    public static void SetParameter(Control c, object? v) => c.SetValue(ParameterProperty, v);

    static Tap()
    {
        CommandProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.Tapped -= OnTapped;
            if (e.NewValue != null) c.Tapped += OnTapped;
        });
        DoubleCommandProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.DoubleTapped -= OnDoubleTapped;
            if (e.NewValue != null) c.DoubleTapped += OnDoubleTapped;
        });
    }

    private static bool FromInteractive(object? source, Control owner) =>
        (source as Visual)?.GetSelfAndVisualAncestors().TakeWhile(v => v != owner)
            .Any(v => v is Button or ToggleButton or TextBox or ComboBox or Slider or ToggleSwitch) == true;

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c || FromInteractive(e.Source, c)) return;
        var cmd = GetCommand(c);
        var p = GetParameter(c) ?? c.DataContext;
        if (cmd?.CanExecute(p) == true) cmd.Execute(p);
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c || FromInteractive(e.Source, c)) return;
        var cmd = GetDoubleCommand(c);
        var p = GetParameter(c) ?? c.DataContext;
        if (cmd?.CanExecute(p) == true) cmd.Execute(p);
    }
}

/// <summary>Enum value lists for combo boxes.</summary>
public static class EnumValues
{
    public static SourceFilter[] SourceFilters { get; } = Enum.GetValues<SourceFilter>();
}
