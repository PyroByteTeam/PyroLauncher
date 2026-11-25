using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PyroLauncher.Core;

namespace PyroLauncher.Desktop.Controls;

/// <summary>
/// 3D player preview drawn in software: every skin texel is a quad on the Minecraft box model (both layers, plus cape),
/// projected with perspective and painted back to front. Drag to rotate.
/// </summary>
public class SkinViewer : Control
{
    public static readonly StyledProperty<byte[]?> SkinProperty = AvaloniaProperty.Register<SkinViewer, byte[]?>(nameof(Skin));
    public static readonly StyledProperty<byte[]?> CapeProperty = AvaloniaProperty.Register<SkinViewer, byte[]?>(nameof(Cape));
    public static readonly StyledProperty<bool> SlimProperty = AvaloniaProperty.Register<SkinViewer, bool>(nameof(Slim));

    public byte[]? Skin { get => GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    public byte[]? Cape { get => GetValue(CapeProperty); set => SetValue(CapeProperty, value); }
    public bool Slim { get => GetValue(SlimProperty); set => SetValue(SlimProperty, value); }

    private record struct Vec(double X, double Y, double Z)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator *(Vec a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    }

    private sealed record Quad(Vec A, Vec B, Vec C, Vec D, Color Color, Vec Normal);

    private (int W, int H, byte[] Px)? _skin, _cape;
    private List<Quad> _quads = [];
    private double _yaw = -25, _pitch = 12;
    private Point? _drag;

    static SkinViewer()
    {
        AffectsRender<SkinViewer>(SkinProperty, CapeProperty, SlimProperty);
        ClipToBoundsProperty.OverrideDefaultValue<SkinViewer>(true);
    }

    public SkinViewer() => Cursor = new Cursor(StandardCursorType.SizeWestEast);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SkinProperty) _skin = Skin != null ? Ui.Platform?.DecodePixels(Skin) : null;
        if (change.Property == CapeProperty) _cape = Cape != null ? Ui.Platform?.DecodePixels(Cape) : null;
        if (change.Property == SkinProperty || change.Property == CapeProperty || change.Property == SlimProperty) Build();
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } last) return;
        var p = e.GetPosition(this);
        _yaw += (p.X - last.X) * 0.6;
        _pitch = Math.Clamp(_pitch + (p.Y - last.Y) * 0.3, -40, 40);
        _drag = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = null;
        e.Pointer.Capture(null);
    }

    // ------------------------------------------------------------------ model

    private Color Texel((int W, int H, byte[] Px) img, int x, int y, int scale)
    {
        x *= scale; y *= scale;
        if (x < 0 || y < 0 || x >= img.W || y >= img.H) return Colors.Transparent;
        var i = (y * img.W + x) * 4;
        return Color.FromArgb(img.Px[i + 3], img.Px[i + 2], img.Px[i + 1], img.Px[i]);
    }

    private bool HasTransparency((int W, int H, byte[] Px) img, int scale, int x, int y, int w, int h)
    {
        for (int j = y; j < y + h; j++)
            for (int i = x; i < x + w; i++)
                if (Texel(img, i, j, scale).A < 128) return true;
        return false;
    }

    /// <summary>Adds the six faces of a box using Minecraft's UV layout (top/bottom row, then right/front/left/back).</summary>
    private void Box((int W, int H, byte[] Px) img, int scale, int u, int v, int w, int h, int d,
        double x0, double y0, double z0, double inflate, bool cape = false)
    {
        double x1 = x0 + w + inflate, y1 = y0 + h + inflate, z1 = z0 + d + inflate;
        x0 -= inflate; y0 -= inflate; z0 -= inflate;
        double sx = (x1 - x0) / w, sy = (y1 - y0) / h, sz = (z1 - z0) / d;

        void Face(int tu, int tv, int tw, int th, Vec origin, Vec a, Vec b, Vec n)
        {
            for (int j = 0; j < th; j++)
                for (int i = 0; i < tw; i++)
                {
                    var c = Texel(img, tu + i, tv + j, scale);
                    if (c.A == 0) continue;
                    var p = origin + a * i + b * j;
                    _quads.Add(new Quad(p, p + a, p + a + b, p + b, c, n));
                }
        }

        var down = new Vec(0, -sy, 0);
        // A cape's outside (seen from behind) is the "front" region of its texture.
        var (frontU, backU) = cape ? (u + 2 * d + w, u + d) : (u + d, u + 2 * d + w);
        Face(frontU, v + d, w, h, new Vec(x0, y1, z1), new Vec(sx, 0, 0), down, new Vec(0, 0, 1));      // front (+z)
        Face(backU, v + d, w, h, new Vec(x1, y1, z0), new Vec(-sx, 0, 0), down, new Vec(0, 0, -1));     // back
        Face(u, v + d, d, h, new Vec(x0, y1, z0), new Vec(0, 0, sz), down, new Vec(-1, 0, 0));          // right (player's right, -x)
        Face(u + d + w, v + d, d, h, new Vec(x1, y1, z1), new Vec(0, 0, -sz), down, new Vec(1, 0, 0));  // left
        Face(u + d, v, w, d, new Vec(x0, y1, z0), new Vec(sx, 0, 0), new Vec(0, 0, sz), new Vec(0, 1, 0));       // top
        Face(u + d + w, v, w, d, new Vec(x0, y0, z1), new Vec(sx, 0, 0), new Vec(0, 0, -sz), new Vec(0, -1, 0)); // bottom
    }

    private void Build()
    {
        _quads = [];
        if (_skin is not { } s) { InvalidateVisual(); return; }
        var scale = Math.Max(1, s.W / 64);
        bool legacy = s.H / scale == 32;
        int arm = Slim ? 3 : 4;

        // Base layer: (u, v), size, position (feet at y = 0, facing +z)
        Box(s, scale, 0, 0, 8, 8, 8, -4, 24, -4, 0);                 // head
        Box(s, scale, 16, 16, 8, 12, 4, -4, 12, -2, 0);              // body
        Box(s, scale, 40, 16, arm, 12, 4, -4 - arm, 12, -2, 0);      // right arm
        Box(s, scale, legacy ? 40 : 32, legacy ? 16 : 48, arm, 12, 4, 4, 12, -2, 0); // left arm
        Box(s, scale, 0, 16, 4, 12, 4, -4, 0, -2, 0);                // right leg
        Box(s, scale, legacy ? 0 : 16, legacy ? 16 : 48, 4, 12, 4, 0, 0, -2, 0);     // left leg

        // Second layer (hat, jacket, sleeves, pants), slightly larger. Old 64×32 skins often fill the hat area with
        // solid colour; like the game, ignore it unless it has some transparency.
        if (!legacy || HasTransparency(s, scale, 32, 0, 32, 16))
            Box(s, scale, 32, 0, 8, 8, 8, -4, 24, -4, 0.5);
        if (!legacy)
        {
            Box(s, scale, 16, 32, 8, 12, 4, -4, 12, -2, 0.25);
            Box(s, scale, 40, 32, arm, 12, 4, -4 - arm, 12, -2, 0.25);
            Box(s, scale, 48, 48, arm, 12, 4, 4, 12, -2, 0.25);
            Box(s, scale, 0, 32, 4, 12, 4, -4, 0, -2, 0.25);
            Box(s, scale, 0, 48, 4, 12, 4, 0, 0, -2, 0.25);
        }

        if (_cape is { } c)
            Box(c, Math.Max(1, c.W / 64), 0, 0, 10, 16, 1, -5, 8, -3.2, 0, cape: true);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ rendering

    public override void Render(DrawingContext ctx)
    {
        if (_quads.Count == 0) return;
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);

        Vec Rot(Vec p)
        {
            p = new Vec(p.X, p.Y - 16, p.Z);                                 // rotate around the body's middle
            var x = p.X * cy + p.Z * sy;
            var z = -p.X * sy + p.Z * cy;
            var y = p.Y * cp - z * sp;
            z = p.Y * sp + z * cp;
            return new Vec(x, y, z);
        }

        // Fit the 32-unit-tall model into the control.
        var unit = Math.Min(Bounds.Width / 26, Bounds.Height / 38);
        const double cam = 80;                                               // camera distance (perspective)
        Point Project(Vec p)
        {
            var f = cam / (cam - p.Z);
            return new Point(Bounds.Width / 2 + p.X * unit * f, Bounds.Height / 2 - p.Y * unit * f);
        }

        var visible = new List<(double depth, Quad q, Vec a, Vec b, Vec c, Vec d, double shade)>(_quads.Count);
        foreach (var q in _quads)
        {
            var n = Rot(q.Normal + new Vec(0, 16, 0));
            if (n.Z <= 0.01) continue;                                       // facing away
            var a = Rot(q.A); var b = Rot(q.B); var c = Rot(q.C); var d = Rot(q.D);
            var shade = 0.62 + 0.38 * Math.Max(n.Z, Math.Max(0, n.Y));       // light from the viewer and above
            visible.Add(((a.Z + b.Z + c.Z + d.Z) / 4, q, a, b, c, d, shade));
        }
        visible.Sort((l, r) => l.depth.CompareTo(r.depth));                  // far to near

        foreach (var (_, q, a, b, c, d, shade) in visible)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(Project(a), true);
                g.LineTo(Project(b));
                g.LineTo(Project(c));
                g.LineTo(Project(d));
                g.EndFigure(true);
            }
            var col = Color.FromArgb(q.Color.A, (byte)(q.Color.R * shade), (byte)(q.Color.G * shade), (byte)(q.Color.B * shade));
            var brush = new SolidColorBrush(col);
            // A hairline in the same colour closes the seams between neighbouring texels.
            ctx.DrawGeometry(brush, q.Color.A == 255 ? new Pen(brush, 0.6) : null, geo);
        }
    }
}
