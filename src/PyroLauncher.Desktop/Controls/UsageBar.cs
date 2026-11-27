using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DispatcherTimer = Avalonia.Threading.DispatcherTimer;
using DispatcherPriority = Avalonia.Threading.DispatcherPriority;
using PyroLauncher.Core;
using PyroLauncher.UI;

namespace PyroLauncher.Desktop.Controls;

/// <summary>
/// A rounded horizontal bar split into coloured parts (Settings → Storage). Set <see cref="Segments"/> for a stacked bar,
/// or <see cref="Value"/> + <see cref="BarColor"/> for a single meter. Parts grow in when the data changes and show their size on hover.
/// </summary>
public class UsageBar : Control
{
    public static readonly StyledProperty<IEnumerable<UsageSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<UsageBar, IEnumerable<UsageSegment>?>(nameof(Segments));
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Value));
    /// <summary>Optional second, fainter part after <see cref="Value"/> (e.g. other files on a drive).</summary>
    public static readonly StyledProperty<double> SecondaryValueProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(SecondaryValue));
    public static readonly StyledProperty<string?> BarColorProperty = AvaloniaProperty.Register<UsageBar, string?>(nameof(BarColor));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> SecondaryBrushProperty = AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(SecondaryBrush));
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Gap), 2);

    public IEnumerable<UsageSegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double SecondaryValue { get => GetValue(SecondaryValueProperty); set => SetValue(SecondaryValueProperty, value); }
    public string? BarColor { get => GetValue(BarColorProperty); set => SetValue(BarColorProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? SecondaryBrush { get => GetValue(SecondaryBrushProperty); set => SetValue(SecondaryBrushProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }

    static UsageBar()
    {
        AffectsRender<UsageBar>(TrackBrushProperty, SecondaryBrushProperty, BarColorProperty, GapProperty);
        SegmentsProperty.Changed.AddClassHandler<UsageBar>((b, _) => b.Animate());
        ValueProperty.Changed.AddClassHandler<UsageBar>((b, _) => b.Animate());
        SecondaryValueProperty.Changed.AddClassHandler<UsageBar>((b, _) => b.Animate());
    }

    private double _t = 1;
    private DispatcherTimer? _timer;
    private DateTime _start;
    private const double DurationMs = 550;
    private readonly List<(Rect Rect, UsageSegment Segment)> _hit = [];

    public UsageBar()
    {
        Height = 14;
        ClipToBounds = true;
    }

    private void Animate()
    {
        _start = DateTime.UtcNow;
        _t = 0;
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var p = Math.Clamp((DateTime.UtcNow - _start).TotalMilliseconds / DurationMs, 0, 1);
            _t = 1 - Math.Pow(1 - p, 3); // ease-out cubic
            if (p >= 1) _timer!.Stop();
            InvalidateVisual();
        });
        _timer.Start();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _t = 1;
    }

    private static IBrush Brush(string? hex) =>
        new SolidColorBrush(string.IsNullOrEmpty(hex) ? Colors.Gray : Color.FromUInt32(ThemeManager.ParseArgb(hex)));

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var radius = h / 2;
        var full = new Rect(0, 0, w, h);
        _hit.Clear();

        using (ctx.PushClip(new RoundedRect(full, radius)))
        {
            if (TrackBrush != null) ctx.FillRectangle(TrackBrush, full);

            var segs = Segments?.Where(s => s.Bytes > 0).ToList();
            if (segs is { Count: > 0 })
            {
                // Tiny parts still get a visible sliver; the rest is shared out by size.
                const double minPx = 4;
                var gap = Gap;
                var avail = Math.Max(0, w - gap * (segs.Count - 1));
                var widths = segs.Select(s => Math.Max(minPx, s.Fraction * avail)).ToArray();
                var over = widths.Sum() - avail;
                if (over > 0)
                {
                    var big = widths.Where(x => x > minPx).Sum();
                    for (var i = 0; i < widths.Length; i++)
                        if (widths[i] > minPx && big > 0) widths[i] -= over * widths[i] / big;
                }

                var reveal = w * _t; // grows in from the left
                double x = 0;
                for (var i = 0; i < segs.Count; i++)
                {
                    var r = new Rect(x, 0, widths[i], h);
                    _hit.Add((r, segs[i]));
                    var visible = r.Intersect(new Rect(0, 0, reveal, h));
                    if (visible.Width > 0) ctx.FillRectangle(Brush(segs[i].Color), visible);
                    x += widths[i] + gap;
                }
            }
            else
            {
                var v = Math.Clamp(Value, 0, 1) * _t;
                var s2 = Math.Clamp(SecondaryValue, 0, 1) * _t;
                if (s2 > 0 && SecondaryBrush != null) ctx.FillRectangle(SecondaryBrush, new Rect(0, 0, w * Math.Max(s2, v), h));
                if (v > 0) ctx.FillRectangle(Brush(BarColor), new Rect(0, 0, Math.Max(h, w * v), h));
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var seg = _hit.FirstOrDefault(h => p.X >= h.Rect.Left - 1 && p.X <= h.Rect.Right + 1).Segment;
        var tip = seg == null ? null : $"{seg.Label}: {seg.SizeText} ({seg.PercentText})";
        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
            ToolTip.SetIsOpen(this, tip != null);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hit.Count > 0)
        {
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, null);
        }
    }
}
