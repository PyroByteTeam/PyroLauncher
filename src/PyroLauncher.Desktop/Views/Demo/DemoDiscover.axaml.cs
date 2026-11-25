using Avalonia.Controls;

namespace PyroLauncher.Desktop.Views.Demo;

public partial class DemoDiscover : UserControl
{
    public DemoDiscover() => InitializeComponent();

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
