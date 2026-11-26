using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    public void PostBackground(Action action) => Dispatcher.UIThread.Post(action, Avalonia.Threading.DispatcherPriority.Background);

    public object? DecodeImage(byte[] data, int decodeWidth)
    {
        try
        {
            try { if (Controls.AnimatedBitmap.TryDecode(data, decodeWidth) is { } anim) return anim; }
            catch (Exception ex) { Log.Warn($"Animated image decode failed: {ex.Message}"); }
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

    public object? ImageFromPixels(int width, int height, byte[] bgra)
    {
        try
        {
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(),
                    new PixelSize(width, height), new Vector(96, 96), width * 4);
            }
            finally { handle.Free(); }
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

    public async Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters)
    {
        if (Storage is not { } s) return null;
        var file = await s.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = Types(filters),
            DefaultExtension = filters.FirstOrDefault()?.Patterns.FirstOrDefault()?.TrimStart('*', '.'),
        });
        return file?.TryGetLocalPath();
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
        await c.SetFileAsync(file);
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
                if (w.WindowState == WindowState.Minimized)
                    w.WindowState = w is Desktop.MainWindow m ? m.RestoreState : WindowState.Normal;
                w.Activate();
                break;
            case WindowAction.Quit:
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life) life.Shutdown();
                break;
        }
    }

    public bool IsMainWindowActive => Main is { } w && w.IsVisible && w.IsActive && w.WindowState != WindowState.Minimized;

    public void ShowSystemNotification(string title, string body)
    {
        if (OperatingSystem.IsWindows()) SystemNotifications.ShowWindows(Main, title, body);
        else if (OperatingSystem.IsMacOS())
            SystemNotifications.Run("osascript", "-e", $"display notification \"{SystemNotifications.AppleEscape(body)}\" with title \"{SystemNotifications.AppleEscape(title)}\"");
        else SystemNotifications.Run("notify-send", "-a", "PyroLauncher", title, body);
    }
}

/// <summary>
/// OS notifications without extra packages. On Windows a tray balloon, which Windows 10/11 shows as a regular
/// notification; the tray icon is only there while notifications are recent.
/// </summary>
internal static class SystemNotifications
{
    public static void Run(string file, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Log.Warn($"{file} failed: {ex.Message}"); }
    }

    public static string AppleEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---------- Windows ----------
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const int NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;
    private const int IconId = 0x5059; // "PY"

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);

    private static IntPtr _icon, _hwnd;
    private static bool _added;
    private static Avalonia.Threading.DispatcherTimer? _removeTimer;

    /// <summary>Must run on the UI thread.</summary>
    public static void ShowWindows(Window? window, string title, string body)
    {
        var hwnd = window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        if (_icon == IntPtr.Zero && Environment.ProcessPath is { } exe)
        {
            ExtractIconExW(exe, 0, out var large, out var small, 1);
            _icon = large != IntPtr.Zero ? large : small;
        }

        var data = NewData(hwnd);
        if (!_added || _hwnd != hwnd)
        {
            data.uFlags = NIF_ICON | NIF_TIP;
            _added = Shell_NotifyIconW(NIM_ADD, ref data) || Shell_NotifyIconW(NIM_MODIFY, ref data);
            _hwnd = hwnd;
            data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4
            Shell_NotifyIconW(NIM_SETVERSION, ref data);
        }
        data = NewData(hwnd);
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(body, 255);
        data.dwInfoFlags = _icon != IntPtr.Zero ? NIIF_USER | NIIF_LARGE_ICON : 0;
        data.hBalloonIcon = _icon;
        if (!Shell_NotifyIconW(NIM_MODIFY, ref data)) Log.Warn("Windows did not accept the notification.");

        // Keep the tray icon around while notifications are recent, then take it away again.
        _removeTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(30), Avalonia.Threading.DispatcherPriority.Background, (_, _) => Remove());
        _removeTimer.Stop();
        _removeTimer.Start();
    }

    /// <summary>Removes the tray icon (also called when the launcher quits).</summary>
    public static void Remove()
    {
        _removeTimer?.Stop();
        if (!_added) return;
        var data = NewData(_hwnd);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    private static NotifyIconData NewData(IntPtr hwnd) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = hwnd,
        uID = IconId,
        hIcon = _icon,
        szTip = "PyroLauncher",
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
