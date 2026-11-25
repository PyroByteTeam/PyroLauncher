using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop.Views;

public partial class ContentTabView : UserControl
{
    private ContentTabViewModel? _vm;

    public ContentTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => { if (_vm != null) _vm.PropertyChanged -= OnVmChanged; };

        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
            if (files is { Count: > 0 } && DataContext is ContentTabViewModel vm) await vm.AddFilesAsync(files);
        });
    }

    private void Attach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ContentTabViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        UpdateDetailWidth();
    }

    /// <summary>The detail pane takes a bit more than half the width while a project is open.</summary>
    private void UpdateDetailWidth() =>
        Root.ColumnDefinitions[1].Width = _vm?.HasDetail == true ? new GridLength(1.25, GridUnitType.Star) : new GridLength(0);

    /// <summary>Searching with suggestions on jumps back to the top, so matching installed items are visible first.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(ContentTabViewModel.HasDetail)) UpdateDetailWidth();
        if ((e.PropertyName == nameof(ContentTabViewModel.Query) && _vm.SuggestionsEnabled)
            || e.PropertyName == nameof(ContentTabViewModel.SuggestionsEnabled))
            ListScroller.ScrollToHome();
    }
}
