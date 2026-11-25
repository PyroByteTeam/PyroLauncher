using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PyroLauncher.Instances;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop.Views;

public partial class HomeView : UserControl
{
    private static readonly DataFormat<string> Format = DataFormat.CreateStringApplicationFormat("pyro.instance");
    private Point _down;
    private bool _pressed;
    private bool _dragging;
    private Instance? _dragged;

    public HomeView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private HomeViewModel Vm => (HomeViewModel)DataContext!;

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return; // play button
        _down = e.GetPosition(this);
        _pressed = true;
        _dragging = false;
    }

    private async void Card_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || _dragging || sender is not Control { DataContext: Instance inst }) return;
        var d = e.GetPosition(this) - _down;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
        _dragging = true;
        _pressed = false;
        _dragged = inst;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Format, inst.Id));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        _dragged = null;
        ClearHighlights();
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed && !_dragging && e.InitialPressMouseButton == MouseButton.Left && sender is Control { DataContext: Instance inst })
            Vm.OpenCommand.Execute(inst);
        _pressed = false;
    }

    private static Border? GroupBorder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("group"));

    private void ClearHighlights()
    {
        foreach (var b in this.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("dropTarget")))
            b.Classes.Remove("dropTarget");
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = e.DataTransfer.Contains(Format) ? GroupBorder(e.Source) : null;
        e.DragEffects = target != null ? DragDropEffects.Move : DragDropEffects.None;
        ClearHighlights();
        target?.Classes.Add("dropTarget");
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => ClearHighlights();

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ClearHighlights();
        if (_dragged != null && GroupBorder(e.Source) is { DataContext: CategoryGroup g })
            Vm.MoveInstance(_dragged, g.Key);
    }
}
