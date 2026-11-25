using Avalonia.Controls;

namespace PyroLauncher.Desktop.Views.Demo;

public partial class DemoInstance : UserControl
{
    public DemoInstance() => InitializeComponent();

    /// <summary>"tabs" (top navigation) or "grid" (section cards).</summary>
    public string Variant
    {
        set
        {
            Tabs.IsVisible = value != "grid";
            Sections.IsVisible = value == "grid";
        }
    }

    public bool ModsAsGrid
    {
        set
        {
            ModList.IsVisible = !value;
            ModGrid.IsVisible = value;
        }
    }
}
