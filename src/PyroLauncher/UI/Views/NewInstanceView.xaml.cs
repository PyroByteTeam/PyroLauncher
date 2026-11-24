using System.Windows;
using System.Windows.Controls;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.UI.Views;

public partial class NewInstanceView : UserControl
{
    public NewInstanceView() => InitializeComponent();

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not NewInstanceViewModel vm || vm.IsBusy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            vm.SetImportFile(files[0]);
    }
}
