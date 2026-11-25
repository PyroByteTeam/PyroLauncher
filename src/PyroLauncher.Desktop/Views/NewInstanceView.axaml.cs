using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop.Views;

public partial class NewInstanceView : UserControl
{
    public NewInstanceView()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        DropZone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var file = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
            if (file != null && DataContext is NewInstanceViewModel vm) vm.SetImportFile(file);
        });
    }
}
