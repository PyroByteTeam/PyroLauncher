using Avalonia;
using Avalonia.Controls;

namespace PyroLauncher.Desktop.Views.Demo;

/// <summary>Non-interactive demo of the launcher used by onboarding. <see cref="Page"/> picks what it shows, e.g.
/// "home", "instance-tabs", "instance-grid", "content-grid", "discover-grid", "discover-list", "servers-cards",
/// "servers-list", "profile", "presets", "downloads", "settings".</summary>
public partial class DemoWindow : UserControl
{
    public static readonly StyledProperty<string?> PageProperty = AvaloniaProperty.Register<DemoWindow, string?>(nameof(Page));

    public string? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public DemoWindow() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) Show(Page ?? "home");
    }

    private void Show(string page)
    {
        var dash = page.IndexOf('-');
        var (area, variant) = dash < 0 ? (page, "") : (page[..dash], page[(dash + 1)..]);
        Host.Content = area switch
        {
            "instance" => new DemoInstance { Variant = variant },
            "content" => new DemoInstance { Variant = "tabs", ModsAsGrid = variant == "grid" },
            "discover" => new DemoDiscover { Variant = variant },
            "servers" => new DemoServers { Variant = variant },
            "profile" => new DemoProfile(),
            "presets" => new DemoPresets(),
            "downloads" => new DemoDownloads(),
            "settings" => new DemoSettings(),
            _ => new DemoHome(),
        };
        var nav = area switch
        {
            "servers" => NavServers,
            "discover" => NavDiscover,
            "profile" => NavProfile,
            "presets" => NavPresets,
            "downloads" => NavDownloads,
            "settings" => NavSettings,
            _ => NavHome,
        };
        nav.IsChecked = true;
    }
}
