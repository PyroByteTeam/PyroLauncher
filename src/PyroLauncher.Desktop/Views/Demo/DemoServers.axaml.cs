using Avalonia.Controls;

namespace PyroLauncher.Desktop.Views.Demo;

public partial class DemoServers : UserControl
{
    public DemoServers() => InitializeComponent();

    /// <summary>"list" shows rows; anything else the card layout.</summary>
    public string Variant
    {
        set
        {
            Rows.IsVisible = value == "list";
            Cards.IsVisible = value != "list";
        }
    }
}
