using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PyroLauncher.UI;

/// <summary>true / non-null / non-empty → Visible. Parameter "invert" flips it.</summary>
public class VisibleIfConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool v = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            double d => d > 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is string p && p == "invert") v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Compares value.ToString() with the parameter. Two-way for RadioButton groups bound to enums/strings.</summary>
public class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true) return Binding.DoNothing;
        if (targetType.IsEnum) return Enum.Parse(targetType, parameter!.ToString()!);
        return parameter!;
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

/// <summary>
/// Memory (MB) text box: values up to 64 are read as gigabytes (4 → 4096), everything is snapped to the nearest power of two.
/// </summary>
public class MemoryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString() ?? "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string ?? "").Trim().ToUpperInvariant().Replace("MB", "").Replace("M", "");
        bool gb = s.EndsWith("GB") || s.EndsWith('G');
        s = s.Replace("GB", "").Replace("G", "").Trim();
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) return null;
        if (gb || v <= 64) v *= 1024;
        return Snap(v);
    }

    public static int Snap(double mb)
    {
        mb = Math.Clamp(mb, 256, 1 << 20);
        var exp = Math.Round(Math.Log2(mb));
        return (int)Math.Pow(2, exp);
    }
}

/// <summary>MB → "= 4 GB" hint.</summary>
public class MbToGbConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int mb ? (mb >= 1024 ? $"= {mb / 1024.0:0.##} GB" : $"= {mb} MB") : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Nullable int ↔ string for text boxes.</summary>
public class NullableIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString() ?? "";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        int.TryParse(value as string, out var i) ? i : null;
}
