using System.Windows.Controls;

namespace PyroLauncher.UI.Views;

public partial class ImageViewerView : UserControl
{
    public ImageViewerView()
    {
        InitializeComponent();
        // Take keyboard focus so the arrow keys switch images right away.
        Loaded += (_, _) => Focus();
    }
}
