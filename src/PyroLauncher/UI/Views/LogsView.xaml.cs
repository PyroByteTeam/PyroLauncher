using System.Windows;
using System.Windows.Controls;

namespace PyroLauncher.UI.Views;

public partial class LogsView : UserControl
{
    public LogsView() => InitializeComponent();

    private void Filter_Changed(object sender, TextChangedEventArgs e) => Viewer.Filter = FilterBox.Text;
    private void Copy_Click(object sender, RoutedEventArgs e) => Viewer.CopySelected();
}
