using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PyroLauncher.UI;
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

        AddHandler(DragDrop.DragEnterEvent, (_, e) => DropOverlay.IsVisible = e.DataTransfer.Contains(DataFormat.File), handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, (_, e) =>
        {
            // Leaving a child raises DragLeave too; hide only when the pointer is outside the page.
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= Bounds.Width || p.Y >= Bounds.Height) DropOverlay.IsVisible = false;
        }, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            DropOverlay.IsVisible = false;
            e.Handled = true;
            var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
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

    /// <summary>
    /// One context menu built on right-click instead of a menu per row: with hundreds of mods the per-row menus
    /// were a large share of the time it took to show the list.
    /// </summary>
    private void Item_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control row || row.DataContext is not PyroLauncher.Content.LocalItem item || _vm == null) return;
        e.Handled = true;
        MenuItem Item(string header, System.Windows.Input.ICommand cmd) => new() { Header = header, Command = cmd, CommandParameter = item };
        var menu = new ContextMenu
        {
            Items =
            {
                Item(L.T("Toggle on / off"), _vm.ToggleCommand),
                Item(L.T("Show in Explorer"), _vm.RevealCommand),
                new Separator(),
                Item(L.T("Delete"), _vm.DeleteCommand),
            },
        };
        menu.Closed += (_, _) => row.ContextMenu = null;
        row.ContextMenu = menu;
        menu.Open(row);
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
