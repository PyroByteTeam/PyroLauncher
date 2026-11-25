using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PyroLauncher.Core;
using PyroLauncher.Desktop.Views;
using PyroLauncher.Instances;

namespace PyroLauncher.Desktop;

/// <summary>Avalonia implementation of the shared code's platform layer.</summary>
public sealed class AvaloniaPlatform : IPlatformUi
{
    private static Window? Main => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public object? DecodeImage(byte[] data, int decodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(data);
            return decodeWidth > 0 ? Bitmap.DecodeToWidth(ms, decodeWidth, BitmapInterpolationMode.HighQuality) : new Bitmap(ms);
        }
        catch { return null; }
    }

    public object? LoadImageFile(string path, int decodeWidth)
    {
        try { return DecodeImage(File.ReadAllBytes(path), decodeWidth); }
        catch { return null; }
    }

    public (int Width, int Height, byte[] Bgra)? DecodePixels(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var bmp = new Bitmap(ms);
            var w = bmp.PixelSize.Width;
            var h = bmp.PixelSize.Height;
            var buffer = new byte[w * h * 4];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buffer.Length, w * 4); }
            finally { handle.Free(); }
            // Alpha is byte 3 in both BGRA and RGBA; swap red/blue when needed so callers always get BGRA.
            if (bmp.Format == PixelFormat.Rgba8888)
                for (int i = 0; i < buffer.Length; i += 4) (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
            return (w, h, buffer);
        }
        catch { return null; }
    }

    public object? CropImage(byte[] data, int x, int y, int width, int height, int scale)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var bmp = new Bitmap(ms);
            return new CroppedBitmap(bmp, new PixelRect(x, y, width, height));
        }
        catch { return null; }
    }

    private static IStorageProvider? Storage => Main?.StorageProvider;

    private static List<FilePickerFileType> Types(IReadOnlyList<FileFilter> filters) =>
        filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList();

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<FileFilter> filters)
    {
        if (Storage is not { } s) return null;
        var files = await s.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = Types(filters) });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<FileFilter> filters)
    {
        if (Storage is not { } s) return [];
        var files = await s.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true, FileTypeFilter = Types(filters) });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory)
    {
        if (Storage is not { } s) return null;
        var start = initialDirectory != null && Directory.Exists(initialDirectory) ? await s.TryGetFolderFromPathAsync(initialDirectory) : null;
        var dirs = await s.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, SuggestedStartLocation = start });
        return dirs.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task SetClipboardTextAsync(string text)
    {
        if (Main?.Clipboard is { } c) await c.SetTextAsync(text);
    }

    /// <summary>Puts the image file on the clipboard (pastes as the image in chat apps and file managers).</summary>
    public async Task<bool> SetClipboardImageAsync(string path)
    {
        if (Main is not { Clipboard: { } c } w) return false;
        var file = await w.StorageProvider.TryGetFileFromPathAsync(path);
        if (file == null) return false;
        var data = new DataObject();
        data.Set(DataFormats.Files, new[] { file });
        await c.SetDataObjectAsync(data);
        return true;
    }

    public void ShowConsole(Instance instance, bool activate) => ConsoleWindow.ShowFor(instance, activate);
    public void CloseConsoles() => ConsoleWindow.CloseAll();

    public void MainWindow(WindowAction action)
    {
        if (Main is not { } w) return;
        switch (action)
        {
            case WindowAction.Minimize: w.WindowState = WindowState.Minimized; break;
            case WindowAction.Hide: w.Hide(); break;
            case WindowAction.Restore:
                w.Show();
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
                break;
            case WindowAction.Quit:
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life) life.Shutdown();
                break;
        }
    }
}
