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

        AddHandler(DragDrop.DragEnterEvent, (_, e) => DropOverlay.IsVisible = e.Data.Contains(DataFormats.Files), handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, (_, e) =>
        {
            // Leaving a child raises DragLeave too; hide only when the pointer is outside the page.
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= Bounds.Width || p.Y >= Bounds.Height) DropOverlay.IsVisible = false;
        }, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            DropOverlay.IsVisible = false;
            e.Handled = true;
            var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
            if (files is { Count: > 0 } && DataContext is ContentTabViewModel vm) await vm.AddFilesAsync(files);
        }, handledEventsToo: true);
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
