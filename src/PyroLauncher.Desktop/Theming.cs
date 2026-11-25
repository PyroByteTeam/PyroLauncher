using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using PyroLauncher.Core;
using PyroLauncher.UI;

namespace PyroLauncher.Desktop;

/// <summary>Size factors of the active theme: text (Value), the left sidebar (Sidebar) and the top tab menus (Tab).
/// XAML reaches them through {l:F}, {l:S}, {l:TF} and {l:TM}, so changes apply live.</summary>
public sealed class FontScale : INotifyPropertyChanged
{
    public static readonly FontScale Instance = new();
    private double _value = 1.0, _sidebar = 1.0, _tab = 1.0;

    public double Value { get => _value; set => Set(ref _value, value, nameof(Value), nameof(TabFont)); }
    public double Sidebar { get => _sidebar; set => Set(ref _sidebar, value, nameof(Sidebar)); }
    public double Tab { get => _tab; set => Set(ref _tab, value, nameof(Tab), nameof(TabFont)); }
    /// <summary>Tab text follows both the font size and the top menu size.</summary>
    public double TabFont => _value * _tab;

    private void Set(ref double field, double value, params string[] names)
    {
        if (Math.Abs(field - value) < 0.0001) return;
        field = value;
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static Binding Bind(string property, object parameter) => new(property)
    {
        Source = Instance,
        Mode = BindingMode.OneWay,
        Converter = ScaleConverter.Instance,
        ConverterParameter = parameter,
    };

    public static Binding For(double size) => Bind(nameof(Value), size);
}

/// <summary>Multiplies a base size (double) or a base thickness ("l,t,r,b" string) by the bound factor.</summary>
public sealed class ScaleConverter : IValueConverter
{
    public static readonly ScaleConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var f = value is double s ? s : 1;
        if (parameter is Thickness t) return new Thickness(Math.Round(t.Left * f), Math.Round(t.Top * f), Math.Round(t.Right * f), Math.Round(t.Bottom * f));
        return Math.Round((parameter is double d ? d : 13) * f, 1);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public abstract class ScaledExtension : MarkupExtension
{
    public string Size { get; set; } = "13";
    protected abstract string Property { get; }
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        FontScale.Bind(Property, Size.Contains(',') ? Thickness.Parse(Size) : double.Parse(Size, CultureInfo.InvariantCulture));
}

/// <summary>XAML: FontSize="{l:F 13}". Follows the theme's font size.</summary>
public sealed class FExtension : ScaledExtension
{
    public FExtension() { }
    public FExtension(string size) => Size = size;
    protected override string Property => nameof(FontScale.Value);
}

/// <summary>XAML: Width="{l:S 88}" or Margin="{l:S '0,4,0,0'}". Follows the theme's sidebar size.</summary>
public sealed class SExtension : ScaledExtension
{
    public SExtension() { }
    public SExtension(string size) => Size = size;
    protected override string Property => nameof(FontScale.Sidebar);
}

/// <summary>XAML: FontSize="{l:TF 14}". Tab text: font size × top menu size.</summary>
public sealed class TFExtension : ScaledExtension
{
    public TFExtension() { }
    public TFExtension(string size) => Size = size;
    protected override string Property => nameof(FontScale.TabFont);
}

/// <summary>XAML: Margin="{l:TM '0,8,0,11'}". Tab spacing: follows the top menu size.</summary>
public sealed class TMExtension : ScaledExtension
{
    public TMExtension() { }
    public TMExtension(string size) => Size = size;
    protected override string Property => nameof(FontScale.Tab);
}

/// <summary>"#AARRGGBB" string ↔ Color (for the color pickers) and → brush (for theme swatches).</summary>
public sealed class HexColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = Color.FromUInt32(ThemeManager.ParseArgb(value as string ?? ""));
        return typeof(IBrush).IsAssignableFrom(targetType) ? new SolidColorBrush(color) : color;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Color c ? $"#{c.ToUInt32():X8}" : BindingOperations.DoNothing;
}

/// <summary>Applies a <see cref="LauncherTheme"/> by recoloring the shared brushes from Theme.axaml in place,
/// so every {StaticResource} user updates live without restarting.</summary>
public static class ThemeService
{
    // Fluent control resources overridden in Theme.axaml → palette key they follow.
    private static readonly (string Resource, string Key)[] Derived =
    [
        ("ToolTipBackground", "Bg3"), ("ToolTipBorderBrush", "Line2"), ("ToolTipForeground", "Fg"),
        ("TextControlBackground", "Bg2"), ("TextControlBackgroundPointerOver", "Bg3"), ("TextControlBackgroundFocused", "Bg2"),
        ("TextControlBorderBrush", "Line"), ("TextControlBorderBrushPointerOver", "Line2"), ("TextControlBorderBrushFocused", "Fg3"),
        ("TextControlPlaceholderForeground", "Fg3"), ("TextControlPlaceholderForegroundFocused", "Fg3"), ("TextControlPlaceholderForegroundPointerOver", "Fg3"),
        ("ComboBoxBackground", "Bg2"), ("ComboBoxBackgroundPointerOver", "Bg3"), ("ComboBoxBorderBrush", "Line"), ("ComboBoxBorderBrushPointerOver", "Line2"),
        ("ComboBoxDropDownBackground", "Bg2"), ("ComboBoxDropDownBorderBrush", "Line2"),
        ("ListBoxItemBackgroundSelected", "Bg4"), ("ListBoxItemBackgroundPointerOver", "Bg3"),
        ("MenuFlyoutPresenterBackground", "Bg2"), ("MenuFlyoutPresenterBorderBrush", "Line2"),
        ("ToggleSwitchKnobFillOn", "AccentFg"), ("ToggleSwitchKnobFillOnPointerOver", "AccentFg"), ("ToggleSwitchKnobFillOnPressed", "AccentFg"),
        ("ToggleSwitchKnobFillOff", "Fg2"), ("ToggleSwitchKnobFillOffPointerOver", "Fg"),
        ("CheckBoxCheckGlyphForegroundChecked", "AccentFg"), ("CheckBoxCheckGlyphForegroundCheckedPointerOver", "AccentFg"),
        ("CheckBoxCheckGlyphForegroundIndeterminate", "AccentFg"),
        ("LogInfo", "Fg2"), ("LogDebug", "Fg3"), ("LogLauncher", "Fg"),
    ];

    private static Bitmap? _background;
    private static string? _backgroundPath;

    public static void Init()
    {
        ThemeManager.Applied += t => Apply(t);
        ThemeManager.Load();
        Apply(ThemeManager.Active);
    }

    private static Color C(LauncherTheme t, string key) => Color.FromUInt32(ThemeManager.ParseArgb(t.Color(key)));

    private static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * amount), (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));

    private static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * c.A), c.R, c.G, c.B);

    private static void SetBrush(string key, Color color)
    {
        if (Application.Current!.TryGetResource(key, null, out var v) && v is SolidColorBrush b) b.Color = color;
    }

    public static void Apply(LauncherTheme t)
    {
        var app = Application.Current;
        if (app == null) return;

        foreach (var k in LauncherTheme.Keys) SetBrush(k.Key, C(t, k.Key));
        foreach (var (res, key) in Derived) SetBrush(res, C(t, key));
        var bg0 = C(t, "Bg0");
        var accent = C(t, "Accent");
        SetBrush("AccentHover", Mix(accent, bg0, 0.16));
        SetBrush("ListBoxItemBackgroundSelectedPointerOver", Mix(C(t, "Bg4"), C(t, "Fg"), 0.04));

        // Fluent's own controls (sliders, toggles, scrollbars, focus rings) take their colors from the palette.
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent
            && fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var p))
        {
            Color fg = C(t, "Fg"), fg2 = C(t, "Fg2"), fg3 = C(t, "Fg3");
            p.Accent = accent;
            p.AltHigh = p.AltLow = p.AltMedium = p.AltMediumHigh = p.AltMediumLow = p.RegionColor = bg0;
            p.BaseHigh = fg; p.BaseMediumHigh = Mix(fg, fg2, 0.5); p.BaseMedium = fg2; p.BaseMediumLow = fg3; p.BaseLow = C(t, "Line");
            p.ChromeLow = p.ChromeMediumLow = C(t, "Bg2"); p.ChromeMedium = C(t, "Bg3"); p.ChromeHigh = C(t, "Line2");
            p.ChromeDisabledHigh = C(t, "Line"); p.ChromeDisabledLow = p.ChromeGray = fg3; p.ChromeAltLow = p.ChromeBlackLow = Mix(fg, fg2, 0.5);
            p.ListLow = C(t, "Bg3"); p.ListMedium = C(t, "Bg4");
        }

        FontScale.Instance.Value = t.FontScale;
        FontScale.Instance.Sidebar = t.SidebarScale;
        FontScale.Instance.Tab = t.TopMenuScale;
        ApplyBackground(t);
    }

    private static void ApplyBackground(LauncherTheme t)
    {
        var path = t.HasBackground ? t.BackgroundPath : null;
        if (path != _backgroundPath)
        {
            var old = _background;
            _background = null;
            _backgroundPath = path;
            if (path != null)
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    _background = Bitmap.DecodeToWidth(fs, 2560, BitmapInterpolationMode.HighQuality);
                }
                catch (Exception ex) { Log.Error($"Loading theme background {path} failed", ex); }
            }
            old?.Dispose();
        }

        var transparency = t.BackgroundTransparency;
        var bg0 = C(t, "Bg0");
        SetBrush("WindowTint", WithAlpha(bg0, 1 - transparency));
        SetBrush("SidebarBg", WithAlpha(C(t, "Bg1"), 1 - transparency * 0.8));

        if (MainWindow.Current is { } w)
            w.SetBackdrop(_background, transparency > 0 && _background == null, bg0);
    }
}
