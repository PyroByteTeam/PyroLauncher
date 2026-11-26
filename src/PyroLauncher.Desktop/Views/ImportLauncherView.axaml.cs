using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop.Views;

public partial class ImportLauncherView : UserControl
{
    public ImportLauncherView()
    {
        InitializeComponent();
        // A modpack file can be dropped anywhere on the dialog, not only on the drop zone of the file step.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
            if (file != null && DataContext is ImportLauncherViewModel vm) vm.SetFile(file);
        });
    }
}
