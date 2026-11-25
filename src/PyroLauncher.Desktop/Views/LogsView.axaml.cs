using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PyroLauncher.Desktop.Views;

public partial class LogsView : UserControl
{
    public LogsView() => InitializeComponent();

    private void Copy_Click(object? sender, RoutedEventArgs e) => Viewer.CopySelected();
}
