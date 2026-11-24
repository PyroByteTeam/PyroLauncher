using System.Windows;
using System.Windows.Controls;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.UI.Views;

public partial class ContentTabView : UserControl
{
    public ContentTabView() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is ContentTabViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await vm.AddFilesAsync(files);
    }
}
