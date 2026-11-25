using System.Collections;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace PyroLauncher.Desktop;

/// <summary>true / non-null / non-empty → true (for IsVisible). Parameter "invert" flips it.</summary>
public class VisibleIfConverter : IValueConverter
{
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        int i => i != 0,
        long l => l != 0,
        double d => d > 0,
        ICollection c => c.Count > 0,
        _ => true,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = Truthy(value);
        return parameter is "invert" ? !v : v;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Compares value.ToString() with the parameter. Two-way for RadioButton groups bound to enums/strings.</summary>
public class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true) return BindingOperations.DoNothing;
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t.IsEnum) return Enum.Parse(t, parameter!.ToString()!);
        return parameter;
    }
}

/// <summary>Progress fraction → 0..100; negative → 0.</summary>
public class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && d >= 0 ? d * 100 : 0.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class IndeterminateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double d && d < 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Nullable int ↔ string for text boxes.</summary>
public class NullableIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString() ?? "";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        int.TryParse(value as string, out var i) ? i : null;
}

/// <summary>Segoe glyph text from the view models → bundled icon font.</summary>
public class IconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Icons.Convert(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats the value with a translated pattern, e.g. ConverterParameter="by {0}".</summary>
public class FormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        UI.L.F((parameter as string ?? "{0}").Replace("#", "{0}"), value); // "#" stands in for "{0}" where XAML would read a leading brace as markup
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>false → dimmed (0.3), true → 1.</summary>
public class BoolToOpacity : IValueConverter
{
    public static readonly BoolToOpacity Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is false ? 0.3 : 1.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0 → faint (0.25), anything else → 1.</summary>
public class CountToOpacity : IValueConverter
{
    public static readonly CountToOpacity Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is 0 ? 0.25 : 1.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Icon, tooltip and dimming for a preset row's state (Checking / Available / AlreadyInstalled / Unavailable / Failed).</summary>
public static class PresetStateConverters
{
    public static readonly IValueConverter Glyph = new FuncValueConverter<Content.PresetItemState, string>(s => Icons.Convert(s switch
    {
        Content.PresetItemState.Available => "\uE896",
        Content.PresetItemState.AlreadyInstalled => "\uE73E",
        Content.PresetItemState.Unavailable => "\uE711",
        Content.PresetItemState.Failed => "\uE783",
        _ => "\uE895",
    }));

    public static readonly IValueConverter Tip = new FuncValueConverter<Content.PresetItemState, string>(s => s switch
    {
        Content.PresetItemState.Available => UI.L.T("Can be installed"),
        Content.PresetItemState.AlreadyInstalled => UI.L.T("Already in this instance"),
        Content.PresetItemState.Unavailable => UI.L.T("Not available for this version"),
        Content.PresetItemState.Failed => UI.L.T("Something went wrong"),
        _ => UI.L.T("Checking…"),
    });

    public static readonly IValueConverter Opacity = new FuncValueConverter<Content.PresetItemState, double>(s => s == Content.PresetItemState.Unavailable ? 0.55 : 1);
}
