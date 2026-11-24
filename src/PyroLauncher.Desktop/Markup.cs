using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using PyroLauncher.UI;

namespace PyroLauncher.Desktop;

/// <summary>XAML: Text="{l:T 'Some text'}". Updates live when the language changes.</summary>
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public TExtension() { }
    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(Loc.Language))
    {
        Source = Loc.Instance,
        Mode = BindingMode.OneWay,
        Converter = TranslateConverter.Instance,
        ConverterParameter = Key,
    };
}

public sealed class TranslateConverter : IValueConverter
{
    public static readonly TranslateConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Loc.Instance[parameter as string ?? ""];
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>XAML: Text="{i:I E80F}". A Segoe Fluent code point, drawn with the bundled Lucide font.</summary>
public sealed class IExtension : MarkupExtension
{
    public string Code { get; set; } = "";
    public IExtension() { }
    public IExtension(string code) => Code = code;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Icons.Convert(((char)System.Convert.ToInt32(Code, 16)).ToString());
}
