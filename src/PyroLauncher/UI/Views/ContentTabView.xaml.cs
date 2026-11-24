using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.UI.Views;

public partial class ContentTabView : UserControl
{
    private ContentTabViewModel? _vm;

    public ContentTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ContentTabViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        };
        Unloaded += (_, _) => { if (_vm != null) _vm.PropertyChanged -= OnVmChanged; };
        Loaded += (_, _) => { if (_vm != null) { _vm.PropertyChanged -= OnVmChanged; _vm.PropertyChanged += OnVmChanged; } };
    }

    /// <summary>Searching with suggestions on jumps back to the top, so matching installed items are visible first.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if ((e.PropertyName == nameof(ContentTabViewModel.Query) && _vm.SuggestionsEnabled)
            || e.PropertyName == nameof(ContentTabViewModel.SuggestionsEnabled))
            ListScroller.ScrollToTop();
    }

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
