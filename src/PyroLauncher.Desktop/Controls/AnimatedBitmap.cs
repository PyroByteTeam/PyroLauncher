using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace PyroLauncher.Desktop.Controls;

/// <summary>
/// A multi-frame image (animated GIF / WebP) usable anywhere an <see cref="IImage"/> is, e.g. <c>Image.Source</c>.
/// Frames are pre-decoded at the requested width. <see cref="Install"/> hooks every <see cref="Image"/> globally, so a
/// plain <c>Source="{Binding Icon}"</c> animates; the frame timer only runs while one of its Images is on screen.
/// </summary>
public sealed class AnimatedBitmap : IImage
{
    /// <summary>Upper bound on decoded pixel memory per animation; longer animations drop frames evenly.</summary>
    private const long MaxBytes = 24L * 1024 * 1024;
    private const int MinDelayMs = 20;

    private readonly Bitmap[] _frames;
    private readonly int[] _delays;
    private readonly List<WeakReference<Image>> _viewers = [];
    private int _index;
    private DispatcherTimer? _timer;

    private AnimatedBitmap(Bitmap[] frames, int[] delays) { _frames = frames; _delays = delays; }

    public Size Size => _frames[0].Size;

    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) => ((IImage)_frames[_index]).Draw(context, sourceRect, destRect);

    /// <summary>Tracks which Images show an animation. Call once on the UI thread at startup.</summary>
    public static void Install() =>
        Image.SourceProperty.Changed.AddClassHandler<Image>((img, e) =>
        {
            (e.OldValue as AnimatedBitmap)?.RemoveViewer(img);
            (e.NewValue as AnimatedBitmap)?.AddViewer(img);
        });

    private void AddViewer(Image img)
    {
        _viewers.Add(new WeakReference<Image>(img));
        img.AttachedToVisualTree += OnViewerAttached;
        EnsureRunning();
    }

    private void RemoveViewer(Image img)
    {
        _viewers.RemoveAll(w => !w.TryGetTarget(out var t) || t == img);
        img.AttachedToVisualTree -= OnViewerAttached;
    }

    private void OnViewerAttached(object? sender, VisualTreeAttachmentEventArgs e) => EnsureRunning();

    private void EnsureRunning()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(_delays[_index]) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _index = (_index + 1) % _frames.Length;
        bool shown = false;
        for (int i = _viewers.Count - 1; i >= 0; i--)
        {
            if (!_viewers[i].TryGetTarget(out var img)) { _viewers.RemoveAt(i); continue; }
            if (img.GetVisualRoot() == null || !img.IsEffectivelyVisible) continue;
            img.InvalidateVisual();
            shown = true;
        }
        if (shown) { _timer!.Interval = TimeSpan.FromMilliseconds(_delays[_index]); return; }
        // Viewers exist but are hidden (IsVisible / tab switch): poll slowly so the animation resumes when they show.
        if (_viewers.Count > 0) { _timer!.Interval = TimeSpan.FromMilliseconds(400); return; }
        // No viewers left: stop until one is added or attached again.
        _timer!.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    /// <summary>True when the data starts with a GIF or WebP signature (the only formats checked for animation).</summary>
    public static bool MaybeAnimated(ReadOnlySpan<byte> d) =>
        (d.Length > 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F') ||
        (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P');

    /// <summary>Decodes all frames, or returns null when the data isn't a multi-frame image (callers fall back to a still bitmap).</summary>
    public static AnimatedBitmap? TryDecode(byte[] data, int decodeWidth)
    {
        if (!MaybeAnimated(data)) return null;
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec == null || codec.FrameCount < 2) return null;

        var info = codec.Info;
        var full = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int w = decodeWidth > 0 && decodeWidth < info.Width ? decodeWidth : info.Width;
        int h = Math.Max(1, (int)Math.Round((double)info.Height * w / info.Width));
        var scaled = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

        var frameInfos = codec.FrameInfo;
        int count = frameInfos.Length;
        // Keep every n-th frame when the full animation would be too large; skipped frames' time is folded into the kept ones.
        int step = (int)Math.Max(1, Math.Ceiling((double)count * w * h * 4 / MaxBytes));

        var frames = new List<Bitmap>();
        var delays = new List<int>();
        using var canvas = new SKBitmap(full);
        using var prior = new SKBitmap(full);
        int priorIndex = -1;
        for (int i = 0; i < count; i++)
        {
            // Compose the frame onto its required predecessor when we still hold it; otherwise (prior frame -1) Skia
            // decodes whatever frames it depends on by itself.
            var req = frameInfos[i].RequiredFrame;
            int priorFrame = -1;
            if (req >= 0 && req == priorIndex) { prior.CopyTo(canvas); priorFrame = req; }
            else canvas.Erase(SKColors.Transparent);

            var r = codec.GetPixels(full, canvas.GetPixels(), new SKCodecOptions(i, priorFrame));
            if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) break;
            canvas.CopyTo(prior);
            priorIndex = i;

            // Same as browsers: tiny/zero delays mean "default speed".
            int delay = frameInfos[i].Duration <= 10 ? 100 : Math.Max(frameInfos[i].Duration, MinDelayMs);
            if (i % step != 0) { if (delays.Count > 0) delays[^1] += delay; continue; }

            using var resized = w == info.Width ? null : canvas.Resize(scaled, SKFilterQuality.High);
            var src = resized ?? canvas;
            frames.Add(new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, src.GetPixels(), new PixelSize(src.Width, src.Height), new Vector(96, 96), src.RowBytes));
            delays.Add(delay);
        }

        if (frames.Count < 2)
        {
            foreach (var f in frames) f.Dispose();
            return null;
        }
        return new AnimatedBitmap([.. frames], [.. delays]);
    }
}
