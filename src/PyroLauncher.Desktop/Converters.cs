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
        UI.L.F(parameter as string ?? "{0}", value);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
