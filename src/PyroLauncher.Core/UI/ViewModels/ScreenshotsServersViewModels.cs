using PyroLauncher.Core;
using System.Collections.ObjectModel;
using System.IO;
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
    [ObservableProperty] private object? _thumb;
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

    private List<string> _lastFiles = [];

    [RelayCommand]
    public void Load()
    {
        if (!Directory.Exists(Dir)) { Items.Clear(); _lastFiles = []; IsEmpty = true; return; }
        var files = new DirectoryInfo(Dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTime).ToList();
        // Reuse existing items (and their thumbnails) when nothing changed.
        if (files.Select(f => f.FullName).SequenceEqual(_lastFiles)) { IsEmpty = Items.Count == 0; return; }
        _lastFiles = files.Select(f => f.FullName).ToList();
        Items.Clear();
        foreach (var f in files) Items.Add(new ScreenshotItem { Path = f.FullName, Date = f.LastWriteTime });
        IsEmpty = Items.Count == 0;
        _ = LoadThumbsAsync(Items.ToList());
    }

    private static async Task LoadThumbsAsync(List<ScreenshotItem> items)
    {
        foreach (var item in items)
            item.Thumb = await ThumbnailCache.GetAsync(item.Path, 400);
    }

    [RelayCommand] private void Open(ScreenshotItem item) => Dialogs.ShowGallery(Items.Select(i => new GalleryEntry(i.Path, null, null)).ToList(), Items.IndexOf(item));
    [RelayCommand] private void OpenFolder() { Directory.CreateDirectory(Dir); Dialogs.OpenPath(Dir); }
    [RelayCommand] private void Reveal(ScreenshotItem item) => Dialogs.RevealInExplorer(item.Path);

    [RelayCommand]
    private async Task Copy(ScreenshotItem item)
    {
        try
        {
            if (await Ui.Platform.SetClipboardImageAsync(item.Path)) Dialogs.Toast(L.T("Screenshot copied to clipboard"));
        }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
    }

    [RelayCommand]
    private async Task Delete(ScreenshotItem item)
    {
        if (!await Dialogs.ConfirmAsync(L.T("Delete screenshot"), L.F("Delete {0}.png?", item.Name), L.T("Delete"), true)) return;
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
    [ObservableProperty] private object? _icon;
    [ObservableProperty] private string _status = L.T("Pinging…");
    [ObservableProperty] private string _motd = "";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _ping = "";

    public static object? DecodeIcon(string? b64)
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
        catch (Exception ex) { Dialogs.Toast(L.F("Could not read servers.dat: {0}", ex.Message), true); }
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
            item.Status = st.Online ? L.F("{0}/{1} players", st.Players, st.MaxPlayers) : L.T("Offline");
            item.Ping = st.Online ? L.F("{0} ms", st.PingMs) : "";
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
        list.Add(new ServerEntry { Name = string.IsNullOrWhiteSpace(NewName) ? L.T("Minecraft Server") : NewName.Trim(), Address = NewAddress.Trim() });
        if (Save(list))
        {
            IsAdding = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task Remove(ServerItem item)
    {
        if (!await Dialogs.ConfirmAsync(L.T("Remove server"), L.F("Remove \"{0}\" from the server list?", item.Name), L.T("Remove"), true)) return;
        var list = Items.Where(i => i != item).Select(i => i.Entry).ToList();
        if (Save(list)) Refresh();
    }

    /// <summary>Starts the game and connects to the server right away, skipping the title screen.</summary>
    [RelayCommand]
    private async Task Join(ServerItem item)
    {
        if (Instance.IsRunning) { Dialogs.Toast(L.T("The game is already running."), true); return; }
        if (Instance.IsBusy) return;
        await LaunchService.LaunchAsync(Instance, item.Address);
    }

    [RelayCommand]
    private void CopyAddress(ServerItem item)
    {
        try { _ = Ui.Platform.SetClipboardTextAsync(item.Address); Dialogs.Toast(L.T("Address copied")); } catch { }
    }

    private bool Save(List<ServerEntry> list)
    {
        if (Instance.IsRunning)
        {
            Dialogs.Toast(L.T("Close the game first - Minecraft overwrites servers.dat when it exits."), true);
            return false;
        }
        try { ServersDat.Save(Instance.GameDir, list); return true; }
        catch (Exception ex) { Dialogs.Toast(L.F("Could not save servers.dat: {0}", ex.Message), true); return false; }
    }
}
