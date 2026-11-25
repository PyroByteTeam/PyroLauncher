using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.Desktop.Views;

public partial class InstanceView : UserControl
{
    private const string Format = "pyro/section";
    private Point _down;
    private bool _pressed;
    private bool _onGrip;
    private bool _dragging;
    private SectionCard? _dragged;

    public InstanceView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearHighlights());
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static bool IsIn(object? source, Func<Visual, bool> test) =>
        (source as Visual)?.GetSelfAndVisualAncestors().TakeWhile(v => v is not Border { Classes: var c } || !c.Contains("sectionCard")).Any(test) == true;

    // Cards open on click anywhere; only the grip in the corner starts a drag.
    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = !IsIn(e.Source, v => v is Button);
        _onGrip = IsIn(e.Source, v => v is Border b && b.Classes.Contains("grip"));
        _dragging = false;
        _down = e.GetPosition(this);
    }

    private async void Card_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || !_onGrip || _dragging || sender is not Control { DataContext: SectionCard card }) return;
        var d = e.GetPosition(this) - _down;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
        _dragging = true;
        _pressed = false;
        _dragged = card;
        var data = new DataObject();
        data.Set(Format, card.Key);
        if (sender is Control c) c.Opacity = 0.5;
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        if (sender is Control c2) c2.Opacity = 1;
        _dragged = null;
        ClearHighlights();
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed && !_onGrip && !_dragging && sender is Control { DataContext: SectionCard card } && DataContext is InstanceViewModel vm)
            vm.OpenSectionCommand.Execute(card.Key);
        _pressed = false;
    }

    private static Border? CardBorder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("sectionCard"));

    private void ClearHighlights()
    {
        foreach (var b in this.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("dropTarget")))
            b.Classes.Remove("dropTarget");
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = e.Data.Contains(Format) ? CardBorder(e.Source) : null;
        e.DragEffects = target != null ? DragDropEffects.Move : DragDropEffects.None;
        ClearHighlights();
        target?.Classes.Add("dropTarget");
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ClearHighlights();
        if (_dragged != null && CardBorder(e.Source) is { DataContext: SectionCard target } && DataContext is InstanceViewModel vm)
            vm.MoveCard(_dragged, target);
    }
}
