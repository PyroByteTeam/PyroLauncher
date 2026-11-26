using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PyroLauncher.Core;

namespace PyroLauncher.Desktop.Controls;

/// <summary>
/// Loads an image from a URL asynchronously (disk + memory cache):
/// <c>c:RemoteImage.Url="{Binding IconUrl}" c:RemoteImage.DecodeWidth="64"</c> on an Image.
/// </summary>
public class RemoteImage : AvaloniaObject
{
    private static readonly ConcurrentDictionary<string, WeakReference<IImage>> Memory = new();
    private static readonly ConcurrentDictionary<string, IImage> Strong = new();
    private static readonly SemaphoreSlim Throttle = new(8);

    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<RemoteImage, Image, string?>("Url");

    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<RemoteImage, Image, int>("DecodeWidth", 96);

    public static string? GetUrl(Image d) => d.GetValue(UrlProperty);
    public static void SetUrl(Image d, string? v) => d.SetValue(UrlProperty, v);
    public static int GetDecodeWidth(Image d) => d.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image d, int v) => d.SetValue(DecodeWidthProperty, v);

    static RemoteImage()
    {
        UrlProperty.Changed.AddClassHandler<Image>((img, e) => _ = OnUrlChanged(img, e.NewValue as string));
    }

    private static async Task OnUrlChanged(Image img, string? url)
    {
        img.Source = null;
        if (string.IsNullOrWhiteSpace(url)) return;
        var width = GetDecodeWidth(img);
        var key = $"{width}|{url}";
        if (Strong.TryGetValue(key, out var s)) { img.Source = s; return; }
        if (Memory.TryGetValue(key, out var wr) && wr.TryGetTarget(out var cached)) { img.Source = cached; return; }
        var src = await LoadAsync(url, width);
        if (src == null) return;
        // Icons are small: keep them for the session so lists reopen without flicker. Big images stay weak.
        // Animations hold many frames, so they are only cached weakly.
        if (width <= 160 && src is not AnimatedBitmap) Strong[key] = src;
        else Memory[key] = new WeakReference<IImage>(src);
        if (GetUrl(img) == url) img.Source = src;
    }

    public static async Task<IImage?> LoadAsync(string url, int decodeWidth)
    {
        try
        {
            var name = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
            var file = Path.Combine(AppPaths.ImageCache, name);
            byte[] bytes;
            if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file);
            else
            {
                await Throttle.WaitAsync();
                try { bytes = await Http.GetBytesAsync(url); }
                finally { Throttle.Release(); }
                try { await File.WriteAllBytesAsync(file, bytes); } catch { }
            }
            return await Task.Run(() => Ui.DecodeImage(bytes, decodeWidth) as IImage);
        }
        catch (Exception ex)
        {
            Log.Warn($"Image load failed {url}: {ex.Message}");
            return null;
        }
    }
}
