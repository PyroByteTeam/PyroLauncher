using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Instances;

namespace PyroLauncher.UI.ViewModels;

public partial class ScreenshotItem : ObservableObject
{
    public string Path { get; init; } = "";
    public DateTime Date { get; init; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string DateText => Date.ToString("d MMM yyyy, HH:mm");
    [ObservableProperty] private ImageSource? _thumb;
}

public partial class ScreenshotsViewModel : ObservableObject
{
    public Instance Instance { get; }
    public ObservableCollection<ScreenshotItem> Items { get; } = [];
    [ObservableProperty] private bool _isEmpty;
    private string Dir => Path.Combine(Instance.GameDir, "screenshots");

    public ScreenshotsViewModel(Instance inst)
    {
        Instance = inst;
        Load();
    }

    [RelayCommand]
    public void Load()
    {
        Items.Clear();
        if (!Directory.Exists(Dir)) { IsEmpty = true; return; }
        var files = new DirectoryInfo(Dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTime).ToList();
        foreach (var f in files) Items.Add(new ScreenshotItem { Path = f.FullName, Date = f.LastWriteTime });
        IsEmpty = Items.Count == 0;
        _ = LoadThumbsAsync(Items.ToList());
    }

    private static async Task LoadThumbsAsync(List<ScreenshotItem> items)
    {
        foreach (var item in items)
        {
            var thumb = await Task.Run(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 400;
                    bmp.UriSource = new Uri(item.Path);
                    bmp.EndInit();
                    bmp.Freeze();
                    return (ImageSource?)bmp;
                }
                catch { return null; }
            });
            item.Thumb = thumb;
        }
    }

    [RelayCommand] private void Open(ScreenshotItem item) => Dialogs.ShowImage(item.Path, null);
    [RelayCommand] private void OpenFolder() { Directory.CreateDirectory(Dir); Dialogs.OpenPath(Dir); }
    [RelayCommand] private void Reveal(ScreenshotItem item) => Dialogs.RevealInExplorer(item.Path);

    [RelayCommand]
    private void Copy(ScreenshotItem item)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(item.Path);
            bmp.EndInit();
            Clipboard.SetImage(bmp);
            Dialogs.Toast("Screenshot copied to clipboard");
        }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }

    [RelayCommand]
    private async Task Delete(ScreenshotItem item)
    {
        if (!await Dialogs.ConfirmAsync("Delete screenshot", $"Delete {item.Name}.png?", "Delete", true)) return;
        try
        {
            File.Delete(item.Path);
            Items.Remove(item);
            IsEmpty = Items.Count == 0;
        }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }
}

public partial class ServerItem : ObservableObject
{
    public ServerEntry Entry { get; init; } = null!;
    public string Name => Entry.Name;
    public string Address => Entry.Address;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private string _status = "Pinging…";
    [ObservableProperty] private string _motd = "";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _ping = "";

    public static ImageSource? DecodeIcon(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        var comma = b64.IndexOf(',');
        if (comma >= 0) b64 = b64[(comma + 1)..];
        try { return LocalContentScanner.LoadImage(Convert.FromBase64String(b64), 64); }
        catch { return null; }
    }
}

public partial class ServersViewModel : ObservableObject
{
    public Instance Instance { get; }
    public ObservableCollection<ServerItem> Items { get; } = [];
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newAddress = "";
    private CancellationTokenSource? _cts;

    public ServersViewModel(Instance inst)
    {
        Instance = inst;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Items.Clear();
        try
        {
            foreach (var s in ServersDat.Load(Instance.GameDir))
                Items.Add(new ServerItem { Entry = s, Icon = ServerItem.DecodeIcon(s.IconBase64) });
        }
        catch (Exception ex) { Dialogs.Toast($"Could not read servers.dat: {ex.Message}", true); }
        IsEmpty = Items.Count == 0;
        foreach (var item in Items) _ = PingAsync(item, _cts.Token);
    }

    private static async Task PingAsync(ServerItem item, CancellationToken ct)
    {
        try
        {
            var st = await ServerPinger.PingAsync(item.Address, ct);
            if (ct.IsCancellationRequested) return;
            item.IsOnline = st.Online;
            item.Status = st.Online ? $"{st.Players}/{st.MaxPlayers} players" : "Offline";
            item.Ping = st.Online ? $"{st.PingMs} ms" : "";
            item.Motd = st.Online ? st.Motd : "";
            if (item.Icon == null && st.FaviconBase64 != null) item.Icon = ServerItem.DecodeIcon(st.FaviconBase64);
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand] private void ShowAdd() { IsAdding = true; NewName = ""; NewAddress = ""; }
    [RelayCommand] private void CancelAdd() => IsAdding = false;

    [RelayCommand]
    private void Add()
    {
        if (string.IsNullOrWhiteSpace(NewAddress)) return;
        var list = Items.Select(i => i.Entry).ToList();
        list.Add(new ServerEntry { Name = string.IsNullOrWhiteSpace(NewName) ? "Minecraft Server" : NewName.Trim(), Address = NewAddress.Trim() });
        if (Save(list))
        {
            IsAdding = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task Remove(ServerItem item)
    {
        if (!await Dialogs.ConfirmAsync("Remove server", $"Remove \"{item.Name}\" from the server list?", "Remove", true)) return;
        var list = Items.Where(i => i != item).Select(i => i.Entry).ToList();
        if (Save(list)) Refresh();
    }

    [RelayCommand]
    private void CopyAddress(ServerItem item)
    {
        try { Clipboard.SetText(item.Address); Dialogs.Toast("Address copied"); } catch { }
    }

    private bool Save(List<ServerEntry> list)
    {
        if (Instance.IsRunning)
        {
            Dialogs.Toast("Close the game first — Minecraft overwrites servers.dat when it exits.", true);
            return false;
        }
        try { ServersDat.Save(Instance.GameDir, list); return true; }
        catch (Exception ex) { Dialogs.Toast($"Could not save servers.dat: {ex.Message}", true); return false; }
    }
}
