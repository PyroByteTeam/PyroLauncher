using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PyroLauncher.UI.Controls;

/// <summary>
/// RAM selector: a slider in 512 MB steps from 512 MB up to the system's memory, quick presets, a warning when the
/// amount gets close to the total, and an optional box for typing an exact amount.
/// </summary>
public class MemoryPicker : StackPanel
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int?), typeof(MemoryPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((MemoryPicker)d).Sync()));

    public int? Value { get => (int?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly int SystemMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576);
    private const int Step = 512;
    private static readonly int MaxMb = Math.Max(4096, SystemMb / Step * Step);

    private readonly Slider _slider;
    private readonly TextBlock _label;
    private readonly TextBlock _warning;
    private readonly CheckBox _customToggle;
    private readonly StackPanel _customPanel;
    private readonly TextBox _customBox;
    private bool _syncing;

    public MemoryPicker()
    {
        Orientation = Orientation.Vertical;

        // Slider row
        var row = new DockPanel();
        _label = new TextBlock { Width = 80, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right };
        _label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        DockPanel.SetDock(_label, Dock.Right);
        row.Children.Add(_label);
        _slider = new Slider
        {
            Minimum = Step,
            Maximum = MaxMb,
            SmallChange = Step,
            LargeChange = 1024,
            TickFrequency = Step,
            IsSnapToTickEnabled = true,
            TickPlacement = TickPlacement.None,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            Value = (int)(Math.Round(e.NewValue / Step) * Step);
        };
        row.Children.Add(_slider);
        Children.Add(row);

        // Presets
        var presets = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var gb in new[] { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64 })
        {
            if (gb * 1024 > MaxMb) break;
            var b = new Button { Content = $"{gb} GB", Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(0, 0, 6, 6), Tag = gb * 1024 };
            b.Click += (s, _) => Value = (int)((Button)s!).Tag;
            presets.Children.Add(b);
        }
        Children.Add(presets);

        // Warning
        _warning = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        _warning.SetResourceReference(TextBlock.ForegroundProperty, "LogWarn");
        Children.Add(_warning);

        // Exact amount
        _customToggle = new CheckBox { Content = "Enter an exact amount", Margin = new Thickness(0, 8, 0, 0), FontSize = 12.5 };
        _customToggle.Checked += (_, _) => { _customPanel!.Visibility = Visibility.Visible; _customBox!.Text = Value?.ToString() ?? ""; };
        _customToggle.Unchecked += (_, _) =>
        {
            _customPanel!.Visibility = Visibility.Collapsed;
            if (Value is { } v && v % Step != 0) Value = Math.Max(Step, (int)Math.Round(v / (double)Step) * Step);
        };
        Children.Add(_customToggle);

        _customPanel = new StackPanel { Margin = new Thickness(27, 8, 0, 0), Visibility = Visibility.Collapsed };
        var boxRow = new StackPanel { Orientation = Orientation.Horizontal };
        _customBox = new TextBox { Width = 120 };
        _customBox.LostFocus += (_, _) => ApplyCustom();
        _customBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyCustom(); };
        boxRow.Children.Add(_customBox);
        var mb = new TextBlock { Text = "MB", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        mb.SetResourceReference(StyleProperty, "Faint");
        boxRow.Children.Add(mb);
        _customPanel.Children.Add(boxRow);
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Text = "Any amount works, but stick to steps of 512 or 1024 MB where you can: Java's garbage collector divides " +
                   "memory into equal fixed-size regions, so round amounts map cleanly onto them, are easier to compare between " +
                   "instances and avoid leaving odd slivers unused. Leave at least 2-4 GB for Windows.",
        };
        hint.SetResourceReference(StyleProperty, "Faint");
        _customPanel.Children.Add(hint);
        Children.Add(_customPanel);

        Sync();
    }

    private void ApplyCustom()
    {
        var t = _customBox.Text.Trim().ToUpperInvariant();
        bool gb = t.EndsWith("G") || t.EndsWith("GB");
        t = t.Replace("GB", "").Replace("G", "").Replace("MB", "").Replace("M", "").Trim();
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0)
        {
            _customBox.Text = Value?.ToString() ?? "";
            return;
        }
        if (gb) v *= 1024;
        Value = (int)Math.Clamp(v, 256, Math.Max(SystemMb, 1024));
    }

    private void Sync()
    {
        var value = Value ?? 2048;
        _syncing = true;
        _slider.Value = Math.Clamp(value, Step, MaxMb);
        _syncing = false;
        _label.Text = value % 1024 == 0 ? $"{value / 1024} GB" : value >= 1024 ? $"{value / 1024.0:0.##} GB" : $"{value} MB";
        if (_customBox != null && !_customBox.IsKeyboardFocused) _customBox.Text = value.ToString();

        // Unusual amounts can only come from the exact-amount box, so keep it open for them.
        if (value % Step != 0 && _customToggle is { IsChecked: false }) _customToggle.IsChecked = true;

        if (_warning != null)
        {
            var share = (double)value / Math.Max(SystemMb, 1);
            _warning.Visibility = share >= 0.75 ? Visibility.Visible : Visibility.Collapsed;
            _warning.Text = $"⚠  {value / 1024.0:0.#} GB is {share:P0} of your {SystemMb / 1024.0:0.#} GB of memory. Windows, the launcher and other apps " +
                            "need room too - with this much the game can stutter, freeze or crash. Most modpacks run well on 4-10 GB.";
        }
    }
}
