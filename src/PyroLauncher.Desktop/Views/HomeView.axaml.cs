using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    // Drag ghost: a snapshot of the card that follows the pointer, while the card itself stays faded in place.
    private Control? _ghost;
    private Control? _source;
    private Point _grab;
    // Drop placeholder: a dashed card in the category under the pointer, at the spot the instance will land.
    private DropSlot? _slot;
    private CategoryGroup? _slotGroup;

    public HomeView()
    {
        InitializeComponent();
        // Drop is allowed on the whole page (AllowDrop is inherited), so drag-over events keep coming and the
        // ghost follows the pointer everywhere; only category boxes actually accept the drop.
        DragDrop.SetAllowDrop(this, true);
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
        if (!_pressed || _dragging || sender is not Control { DataContext: Instance inst } card) return;
        var d = e.GetPosition(this) - _down;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
        _dragging = true;
        _pressed = false;
        _dragged = inst;
        _slot = new DropSlot { Width = card.Bounds.Width, Height = card.Bounds.Height };
        ShowGhost(card, e);
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(Format, inst.Id));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            _dragged = null;
            HideGhost();
            HideSlot();
            _slot = null;
            ClearHighlights();
        }
    }

    /// <summary>Puts the placeholder in <paramref name="g"/>, or removes it when null (or the instance is already there).</summary>
    private void ShowSlot(CategoryGroup? g)
    {
        if (_dragged == null || _slot == null || g == null || g.Items.Contains(_dragged)) { HideSlot(); return; }
        if (_slotGroup != g) HideSlot();
        _slotGroup = g;
        g.ShowDropSlot(_slot, _dragged);
    }

    private void HideSlot()
    {
        _slotGroup?.HideDropSlot();
        _slotGroup = null;
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed && !_dragging && e.InitialPressMouseButton == MouseButton.Left && sender is Control { DataContext: Instance inst })
            Vm.OpenCommand.Execute(inst);
        _pressed = false;
    }

    private void ShowGhost(Control card, PointerEventArgs e)
    {
        HideGhost();
        var size = card.Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;
        try
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
                new Vector(96 * scale, 96 * scale));
            bmp.Render(card);
            _ghost = new Border
            {
                Width = size.Width,
                Height = size.Height,
                CornerRadius = new CornerRadius(12),
                ClipToBounds = true,
                Opacity = 0.92,
                BoxShadow = BoxShadows.Parse("0 12 28 0 #66000000"),
                RenderTransform = new RotateTransform(-2),
                Child = new Image { Source = bmp, Stretch = Stretch.Fill },
            };
            _grab = e.GetPosition(card);
            DragLayer.Children.Add(_ghost);
            MoveGhost(e.GetPosition(DragLayer));
            _source = card;
            card.Opacity = 0.35;
        }
        catch { _ghost = null; } // no ghost is better than no drag
    }

    private void MoveGhost(Point p)
    {
        if (_ghost == null) return;
        Canvas.SetLeft(_ghost, p.X - _grab.X);
        Canvas.SetTop(_ghost, p.Y - _grab.Y);
    }

    private void HideGhost()
    {
        if (_ghost != null)
        {
            DragLayer.Children.Remove(_ghost);
            if (_ghost is Border { Child: Image { Source: IDisposable bmp } }) bmp.Dispose();
            _ghost = null;
        }
        if (_source != null)
        {
            _source.Opacity = 1;
            _source = null;
        }
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
        var ours = e.DataTransfer.Contains(Format);
        if (ours) MoveGhost(e.GetPosition(DragLayer));
        var target = ours ? GroupBorder(e.Source) : null;
        e.DragEffects = target != null ? DragDropEffects.Move : DragDropEffects.None;
        ClearHighlights();
        target?.Classes.Add("dropTarget");
        if (ours) ShowSlot(target?.DataContext as CategoryGroup);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        ClearHighlights();
        // Leave events also come when the pointer moves between elements of the page; only leaving the page clears the slot.
        if (!new Rect(Bounds.Size).Contains(e.GetPosition(this))) HideSlot();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ClearHighlights();
        HideGhost();
        HideSlot();
        if (_dragged != null && GroupBorder(e.Source) is { DataContext: CategoryGroup g })
            Vm.MoveInstance(_dragged, g.Key);
    }
}
