using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PyroLauncher.Core;

namespace PyroLauncher.UI.Controls;

/// <summary>
/// Attached properties to load an image from a URL asynchronously (with disk + memory cache):
/// <c>controls:RemoteImage.Url="{Binding IconUrl}" controls:RemoteImage.DecodeWidth="64"</c>.
/// </summary>
public static class RemoteImage
{
    private static readonly ConcurrentDictionary<string, WeakReference<ImageSource>> Memory = new();
    private static readonly SemaphoreSlim Throttle = new(8);

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(RemoteImage), new PropertyMetadata(null, OnUrlChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(RemoteImage), new PropertyMetadata(96));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? v) => d.SetValue(UrlProperty, v);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject d, int v) => d.SetValue(DecodeWidthProperty, v);

    private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        var url = e.NewValue as string;
        img.Source = null;
        if (string.IsNullOrWhiteSpace(url)) return;
        var width = GetDecodeWidth(img);
        var key = $"{width}|{url}";
        if (Memory.TryGetValue(key, out var wr) && wr.TryGetTarget(out var cached))
        {
            img.Source = cached;
            return;
        }
        var src = await LoadAsync(url, width);
        if (src == null) return;
        Memory[key] = new WeakReference<ImageSource>(src);
        if (GetUrl(img) == url) img.Source = src;
    }

    public static async Task<ImageSource?> LoadAsync(string url, int decodeWidth)
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
            return await Task.Run(() =>
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.StreamSource = new MemoryStream(bytes);
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.EndInit();
                bmp.Freeze();
                return (ImageSource)bmp;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Image load failed {url}: {ex.Message}");
            return null;
        }
    }
}
