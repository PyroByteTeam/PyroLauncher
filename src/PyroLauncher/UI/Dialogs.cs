using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.UI.ViewModels;

namespace PyroLauncher.UI;

public partial class ToastItem : ObservableObject
{
    public string Message { get; init; } = "";
    public bool IsError { get; init; }
    public string Glyph => IsError ? "" : "";
}

/// <summary>In-window modal overlay + toast notifications.</summary>
public static class Dialogs
{
    public static event Action<bool>? OverlayChanged;
    public static bool IsOverlayOpen { get; private set; }
    public static ObservableCollection<ToastItem> Toasts { get; } = [];

    private static MainViewModel Main => MainViewModel.Instance;
    private static readonly Stack<object> Stack = new();

    public static void Show(object vm)
    {
        if (Main.Dialog != null) Stack.Push(Main.Dialog);
        Main.Dialog = vm;
        SetOverlay(true);
    }

    public static void Close(object? vm = null)
    {
        if (vm != null && Main.Dialog != vm) return;
        Main.Dialog = Stack.Count > 0 ? Stack.Pop() : null;
        SetOverlay(Main.Dialog != null);
    }

    private static void SetOverlay(bool open)
    {
        if (IsOverlayOpen == open) return;
        IsOverlayOpen = open;
        OverlayChanged?.Invoke(open);
    }

    public static Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false)
    {
        var vm = new ConfirmDialogViewModel(title, message, confirmText, danger);
        Show(vm);
        return vm.Result.Task;
    }

    public static Task<string?> PromptAsync(string title, string label, string initial = "", string confirmText = "Save")
    {
        var vm = new PromptDialogViewModel(title, label, initial, confirmText);
        Show(vm);
        return vm.Result.Task;
    }

    public static Task<string?> ChooseAsync(string title, string message, IEnumerable<string> options)
    {
        var vm = new ChoiceDialogViewModel(title, message, options);
        Show(vm);
        return vm.Result.Task;
    }

    public static void ShowImage(string? path, string? url, string? title = null) =>
        Show(new ImageViewerViewModel([new GalleryEntry(path, url, title)], 0));

    /// <summary>Image viewer with previous / next controls (arrow keys work too).</summary>
    public static void ShowGallery(IReadOnlyList<GalleryEntry> images, int index)
    {
        if (images.Count == 0) return;
        Show(new ImageViewerViewModel(images, Math.Clamp(index, 0, images.Count - 1)));
    }

    public static void Toast(string message, bool error = false)
    {
        void Add()
        {
            var t = new ToastItem { Message = message, IsError = error };
            Toasts.Add(t);
            while (Toasts.Count > 4) Toasts.RemoveAt(0);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 9 : 4.5) };
            timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
            timer.Start();
        }
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) Add(); else d.BeginInvoke(Add);
    }

    public static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Toast($"Not found: {path}", true);
        }
        catch (Exception ex) { Toast(ex.Message, true); }
    }

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Toast(ex.Message, true); }
    }

    public static void RevealInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }
}

public partial class ConfirmDialogViewModel(string title, string message, string confirmText, bool danger) : ObservableObject
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;
    public bool IsDanger { get; } = danger;
    public TaskCompletionSource<bool> Result { get; } = new();

    [RelayCommand] private void Confirm() { Dialogs.Close(this); Result.TrySetResult(true); }
    [RelayCommand] private void Cancel() { Dialogs.Close(this); Result.TrySetResult(false); }
}

public partial class PromptDialogViewModel(string title, string label, string initial, string confirmText) : ObservableObject
{
    public string Title { get; } = title;
    public string Label { get; } = label;
    public string ConfirmText { get; } = confirmText;
    [ObservableProperty] private string _text = initial;
    public TaskCompletionSource<string?> Result { get; } = new();

    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        Dialogs.Close(this);
        Result.TrySetResult(Text.Trim());
    }

    [RelayCommand] private void Cancel() { Dialogs.Close(this); Result.TrySetResult(null); }
}

public partial class ChoiceDialogViewModel(string title, string message, IEnumerable<string> options) : ObservableObject
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public List<string> Options { get; } = options.ToList();
    public TaskCompletionSource<string?> Result { get; } = new();

    [RelayCommand] private void Choose(string option) { Dialogs.Close(this); Result.TrySetResult(option); }
    [RelayCommand] private void Cancel() { Dialogs.Close(this); Result.TrySetResult(null); }
}

public record GalleryEntry(string? Path, string? Url, string? Title);

public partial class ImageViewerViewModel : ObservableObject
{
    public IReadOnlyList<GalleryEntry> Images { get; }
    [ObservableProperty] private int _index;

    private GalleryEntry Current => Images[Index];
    public string? Path => Current.Path;
    public string? Url => Current.Url;
    public string? LocalUri => Current.Path;
    public string? Title => Current.Title ?? (Current.Path != null ? System.IO.Path.GetFileName(Current.Path) : null);
    public bool HasMany => Images.Count > 1;
    public string PositionText => $"{Index + 1} / {Images.Count}";

    public ImageViewerViewModel(IReadOnlyList<GalleryEntry> images, int index)
    {
        Images = images;
        _index = index;
    }

    partial void OnIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(LocalUri));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PositionText));
    }

    // Wraps around at both ends.
    [RelayCommand] private void Previous() { if (HasMany) Index = (Index - 1 + Images.Count) % Images.Count; }
    [RelayCommand] private void Next() { if (HasMany) Index = (Index + 1) % Images.Count; }

    [RelayCommand] private void Close() => Dialogs.Close(this);
    [RelayCommand] private void OpenExternal() { if (Path != null) Dialogs.OpenPath(Path); else Dialogs.OpenUrl(Url); }
}
