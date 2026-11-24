using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace PyroLauncher.UI.Controls;

/// <summary>
/// Interactive 3D Minecraft player model built from a skin texture (64×64 or legacy 64×32), with second layer,
/// slim/classic arms and an optional cape. Drag to rotate.
/// </summary>
public class SkinViewer : Grid
{
    public static readonly DependencyProperty SkinProperty = DependencyProperty.Register(nameof(Skin), typeof(BitmapSource), typeof(SkinViewer),
        new PropertyMetadata(null, (d, _) => ((SkinViewer)d).Rebuild()));
    public static readonly DependencyProperty CapeProperty = DependencyProperty.Register(nameof(Cape), typeof(BitmapSource), typeof(SkinViewer),
        new PropertyMetadata(null, (d, _) => ((SkinViewer)d).Rebuild()));
    public static readonly DependencyProperty SlimProperty = DependencyProperty.Register(nameof(Slim), typeof(bool), typeof(SkinViewer),
        new PropertyMetadata(false, (d, _) => ((SkinViewer)d).Rebuild()));

    public BitmapSource? Skin { get => (BitmapSource?)GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    public BitmapSource? Cape { get => (BitmapSource?)GetValue(CapeProperty); set => SetValue(CapeProperty, value); }
    public bool Slim { get => (bool)GetValue(SlimProperty); set => SetValue(SlimProperty, value); }

    private readonly ModelVisual3D _visual = new();
    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), 28);
    private readonly AxisAngleRotation3D _pitch = new(new Vector3D(1, 0, 0), 8);
    private Point? _drag;
    private readonly PerspectiveCamera _camera;
    private const double CameraDistance = 72;

    /// <summary>
    /// WPF's FieldOfView is horizontal, so on wide-but-short areas the model got cropped. Pick the FOV so the whole
    /// player (≈36 units tall incl. hat, ≈24 wide when rotated) always fits, whatever the aspect ratio.
    /// </summary>
    private void FitCamera()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double aspect = ActualWidth / ActualHeight;
        const double halfHeight = 21, halfWidth = 15;
        double vertical = Math.Atan(halfHeight / CameraDistance);                  // half-angle needed vertically
        double horizFromVertical = Math.Atan(Math.Tan(vertical) * aspect);         // as a horizontal half-angle
        double horizontal = Math.Atan(halfWidth / CameraDistance);
        _camera.FieldOfView = 2 * Math.Max(horizFromVertical, horizontal) * 180 / Math.PI;
    }

    /// <summary>Slim (Alex) skins leave the outer 2 columns of the right arm's back face transparent.</summary>
    public static bool DetectSlim(BitmapSource? skin)
    {
        if (skin == null || skin.PixelWidth != 64 || skin.PixelHeight != 64) return false;
        try
        {
            var bgra = skin.Format == PixelFormats.Bgra32 ? skin : new FormatConvertedBitmap(skin, PixelFormats.Bgra32, null, 0);
            var px = new byte[4];
            bool Transparent(int x, int y)
            {
                bgra.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                return px[3] == 0;
            }
            return Transparent(54, 20) && Transparent(55, 20) && Transparent(54, 31) && Transparent(55, 31);
        }
        catch { return false; }
    }

    public SkinViewer()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        _camera = new PerspectiveCamera(new Point3D(0, 16, CameraDistance), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 34);
        var viewport = new Viewport3D { Camera = _camera, IsHitTestVisible = false };
        SizeChanged += (_, _) => FitCamera();
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0xC8, 0xC8, 0xC8)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x50, 0x50), new Vector3D(-0.4, -0.6, -1)));
        viewport.Children.Add(new ModelVisual3D { Content = lights });

        var tg = new Transform3DGroup();
        tg.Children.Add(new TranslateTransform3D(0, -16, 0));
        tg.Children.Add(new RotateTransform3D(_yaw));
        tg.Children.Add(new RotateTransform3D(_pitch));
        tg.Children.Add(new TranslateTransform3D(0, 16, 0));
        _visual.Transform = tg;
        viewport.Children.Add(_visual);
        Children.Add(viewport);
        RenderOptions.SetBitmapScalingMode(viewport, BitmapScalingMode.NearestNeighbor);

        MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(this); CaptureMouse(); };
        MouseLeftButtonUp += (_, _) => { _drag = null; ReleaseMouseCapture(); };
        MouseMove += OnMouseMove;
        Cursor = Cursors.SizeWE;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        _yaw.Angle += (p.X - start.X) * 0.7;
        _pitch.Angle = Math.Clamp(_pitch.Angle + (p.Y - start.Y) * 0.35, -35, 35);
        _drag = p;
    }

    // ------------------------------------------------------------------ model

    private void Rebuild()
    {
        if (Skin is not { } skin) { _visual.Content = null; return; }
        int texW = skin.PixelWidth, texH = skin.PixelHeight;
        double s = texW / 64.0; // HD skins
        bool legacy = texH * 2 == texW;
        var brush = MakeBrush(skin);
        var basePart = new MeshGeometry3D();
        var overlay = new MeshGeometry3D();
        int aw = Slim ? 3 : 4;

        void Box(MeshGeometry3D m, double x0, double y0, double z0, double x1, double y1, double z1, int u, int v, int w, int h, int d, double inflate = 0)
            => AddBox(m, x0 - inflate, y0 - inflate, z0 - inflate, x1 + inflate, y1 + inflate, z1 + inflate, u * s, v * s, w * s, h * s, d * s, texW, texH);

        // Head + hat
        Box(basePart, -4, 24, -4, 4, 32, 4, 0, 0, 8, 8, 8);
        Box(overlay, -4, 24, -4, 4, 32, 4, 32, 0, 8, 8, 8, 0.5);
        // Body
        Box(basePart, -4, 12, -2, 4, 24, 2, 16, 16, 8, 12, 4);
        // Right arm (player's right = viewer's left) and right leg
        Box(basePart, -4 - aw, 12, -2, -4, 24, 2, 40, 16, aw, 12, 4);
        Box(basePart, -4, 0, -2, 0, 12, 2, 0, 16, 4, 12, 4);
        if (legacy)
        {
            Box(basePart, 4, 12, -2, 4 + aw, 24, 2, 40, 16, aw, 12, 4);
            Box(basePart, 0, 0, -2, 4, 12, 2, 0, 16, 4, 12, 4);
        }
        else
        {
            Box(basePart, 4, 12, -2, 4 + aw, 24, 2, 32, 48, aw, 12, 4);
            Box(basePart, 0, 0, -2, 4, 12, 2, 16, 48, 4, 12, 4);
            // Second layer
            Box(overlay, -4, 12, -2, 4, 24, 2, 16, 32, 8, 12, 4, 0.25);
            Box(overlay, -4 - aw, 12, -2, -4, 24, 2, 40, 32, aw, 12, 4, 0.25);
            Box(overlay, 4, 12, -2, 4 + aw, 24, 2, 48, 48, aw, 12, 4, 0.25);
            Box(overlay, -4, 0, -2, 0, 12, 2, 0, 32, 4, 12, 4, 0.25);
            Box(overlay, 0, 0, -2, 4, 12, 2, 0, 48, 4, 12, 4, 0.25);
        }

        var group = new Model3DGroup();
        group.Children.Add(new GeometryModel3D(basePart, new DiffuseMaterial(brush)));

        if (Cape is { } cape)
        {
            var cs = cape.PixelWidth / 64.0;
            var mesh = new MeshGeometry3D();
            AddBox(mesh, -5, -16, -0.5, 5, 0, 0.5, 0, 0, 10 * cs, 16 * cs, 1 * cs, cape.PixelWidth, cape.PixelHeight);
            var capeModel = new GeometryModel3D(mesh, new DiffuseMaterial(MakeBrush(cape))) { BackMaterial = null };
            var t = new Transform3DGroup();
            t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 180)));
            t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 9)));
            t.Children.Add(new TranslateTransform3D(0, 24, -2.6));
            capeModel.Transform = t;
            group.Children.Add(capeModel);
        }

        // Transparent second layer last so it blends over the base.
        group.Children.Add(new GeometryModel3D(overlay, new DiffuseMaterial(brush)) { BackMaterial = new DiffuseMaterial(brush) });
        _visual.Content = group;
    }

    /// <summary>Upscales with nearest neighbour so WPF's bilinear texture sampling stays crisp.</summary>
    private static ImageBrush MakeBrush(BitmapSource src)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        int scale = Math.Max(1, 512 / Math.Max(w, 1));
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        int W = w * scale, H = h * scale;
        var big = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            int sy = y / scale;
            for (int x = 0; x < W; x++)
            {
                int si = (sy * w + x / scale) * 4, di = (y * W + x) * 4;
                big[di] = pixels[si]; big[di + 1] = pixels[si + 1]; big[di + 2] = pixels[si + 2]; big[di + 3] = pixels[si + 3];
            }
        }
        var wb = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, W, H), big, W * 4, 0);
        wb.Freeze();
        var brush = new ImageBrush(wb)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            TileMode = TileMode.None,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        return brush;
    }

    private static void AddBox(MeshGeometry3D m, double x0, double y0, double z0, double x1, double y1, double z1,
        double u, double v, double w, double h, double d, double texW, double texH)
    {
        // Each face: corners TL, TR, BR, BL as seen from outside + texture rect (Minecraft box UV layout).
        AddFace(m, new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1), u + d, v, w, d, texW, texH);              // top
        AddFace(m, new(x0, y0, z1), new(x1, y0, z1), new(x1, y0, z0), new(x0, y0, z0), u + d + w, v, w, d, texW, texH);          // bottom
        AddFace(m, new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), new(x0, y0, z0), u, v + d, d, h, texW, texH);              // right (-X)
        AddFace(m, new(x0, y1, z1), new(x1, y1, z1), new(x1, y0, z1), new(x0, y0, z1), u + d, v + d, w, h, texW, texH);          // front (+Z)
        AddFace(m, new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), new(x1, y0, z1), u + d + w, v + d, d, h, texW, texH);      // left (+X)
        AddFace(m, new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z0), new(x1, y0, z0), u + 2 * d + w, v + d, w, h, texW, texH);  // back (-Z)
    }

    private static void AddFace(MeshGeometry3D m, Point3D tl, Point3D tr, Point3D br, Point3D bl,
        double rx, double ry, double rw, double rh, double texW, double texH)
    {
        const double e = 0.02; // tiny inset against texture bleeding
        int i = m.Positions.Count;
        m.Positions.Add(tl); m.Positions.Add(tr); m.Positions.Add(br); m.Positions.Add(bl);
        double l = (rx + e) / texW, r = (rx + rw - e) / texW, t = (ry + e) / texH, b = (ry + rh - e) / texH;
        m.TextureCoordinates.Add(new Point(l, t));
        m.TextureCoordinates.Add(new Point(r, t));
        m.TextureCoordinates.Add(new Point(r, b));
        m.TextureCoordinates.Add(new Point(l, b));
        m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 3); m.TriangleIndices.Add(i + 2);
        m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 2); m.TriangleIndices.Add(i + 1);
    }
}
