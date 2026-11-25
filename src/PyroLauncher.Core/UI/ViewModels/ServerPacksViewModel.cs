using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyroLauncher.Content;
using PyroLauncher.Core;
using PyroLauncher.Instances;
using PyroLauncher.Servers;

namespace PyroLauncher.UI.ViewModels;

/// <summary>The Servers page: a grid of server packs.</summary>
public partial class ServerPacksViewModel : ObservableObject
{
    public ObservableCollection<ServerPack> Packs => ServerPackManager.Packs;
    public bool IsEmpty => Packs.Count == 0;
    public bool IsGrid => SettingsService.Current.ServerListStyle == ContentListStyle.Grid;
    public bool ShowGrid => !IsEmpty && IsGrid;
    public bool ShowList => !IsEmpty && !IsGrid;

    public ServerPacksViewModel()
    {
        Packs.CollectionChanged += (_, _) => Notify();
        SettingsService.AppearanceChanged += () => Ui.Post(Notify);
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsGrid));
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowList));
    }

    [RelayCommand] private void Create() => Dialogs.Show(new NewServerPackViewModel(null));
    [RelayCommand] private void Open(ServerPack pack) => ServerService.Open(pack);
    [RelayCommand] private async Task Toggle(ServerPack pack) => await ServerService.ToggleAsync(pack);
}

public enum SideFilter { All, Client, Server, Both, Unknown }
public enum ModSort { Name, Side, Size }

public partial class ServerModChoice : ObservableObject
{
    public LocalItem Item { get; init; } = null!;
    [ObservableProperty] private ModSide _side;
    [ObservableProperty] private bool _isChecked = true;
    public string Name => Item.Name;
    public string FileName => Item.FileName;
    public object? Icon => Item.Icon;
    public string Initials => Item.Initials;
    public string SizeText => DownloadManager.FormatBytes(Item.Size);
    public string SideText => Side switch
    {
        ModSide.Client => L.T("Client"),
        ModSide.Server => L.T("Server"),
        ModSide.Both => L.T("Both"),
        _ => L.T("Unknown"),
    };
    public bool IsClientOnly => Side == ModSide.Client;
    partial void OnSideChanged(ModSide value)
    {
        OnPropertyChanged(nameof(SideText));
        OnPropertyChanged(nameof(IsClientOnly));
    }
}

/// <summary>Creating a server pack from an instance: pick the instance, then the mods to take along.</summary>
public partial class NewServerPackViewModel : ObservableObject
{
    public ObservableCollection<Instance> Instances { get; } = new(InstanceManager.Instances);
    public ObservableCollection<ServerModChoice> Mods { get; } = [];
    public ICollectionView ModsView { get; }
    public SideFilter[] SideFilters { get; } = Enum.GetValues<SideFilter>();
    public ModSort[] Sorts { get; } = Enum.GetValues<ModSort>();

    [ObservableProperty] private Instance? _selectedInstance;
    [ObservableProperty] private bool _isPickStep = true;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private SideFilter _sideFilter = SideFilter.All;
    [ObservableProperty] private ModSort _sort = ModSort.Name;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _copyConfigs = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isResolving;
    [ObservableProperty] private string? _status;

    public bool CanGoBack { get; }
    public string SelectionText => L.F("{0} of {1} mods selected", Mods.Count(m => m.IsChecked), Mods.Count);

    public NewServerPackViewModel(Instance? instance)
    {
        ModsView = new ListCollectionView(Mods) { Filter = o => o is ServerModChoice m && Matches(m) };
        CanGoBack = instance == null;
        if (instance != null)
        {
            SelectedInstance = instance;
            _ = NextAsync();
        }
    }

    private bool Matches(ServerModChoice m) =>
        (SideFilter == SideFilter.All || SideFilter.ToString() == m.Side.ToString())
        && (Search.Length == 0 || m.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || m.FileName.Contains(Search, StringComparison.OrdinalIgnoreCase));

    partial void OnSideFilterChanged(SideFilter value) => ModsView.Refresh();
    partial void OnSearchChanged(string value) => ModsView.Refresh();
    partial void OnSortChanged(ModSort value) => ApplySort();

    private void ApplySort()
    {
        var sorted = (Sort switch
        {
            ModSort.Side => Mods.OrderBy(m => m.Side).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            ModSort.Size => Mods.OrderByDescending(m => m.Item.Size),
            _ => Mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
        }).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var from = Mods.IndexOf(sorted[i]);
            if (from != i) Mods.Move(from, i);
        }
        ModsView.Refresh();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (SelectedInstance is not { } inst) return;
        Name = L.F("{0} Server", inst.Name);
        IsPickStep = false;
        IsResolving = true;
        Mods.Clear();
        var list = ContentStore.Get(inst, ContentType.Mod);
        await list.RefreshAsync();
        var items = list.Items.Where(i => i.IsEnabled).ToList();
        foreach (var i in items)
        {
            var c = new ServerModChoice { Item = i };
            c.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ServerModChoice.IsChecked)) OnPropertyChanged(nameof(SelectionText)); };
            Mods.Add(c);
        }
        ApplySort();
        OnPropertyChanged(nameof(SelectionText));
        try
        {
            var sides = await ModSides.ResolveAsync(items);
            foreach (var m in Mods)
            {
                m.Side = sides.GetValueOrDefault(m.Item);
                m.IsChecked = m.Side != ModSide.Client;
            }
            if (Sort == ModSort.Side) ApplySort();
        }
        catch (Exception ex) { Log.Warn($"Mod side lookup failed: {ex.Message}"); }
        IsResolving = false;
        ModsView.Refresh();
        OnPropertyChanged(nameof(SelectionText));
    }

    [RelayCommand] private void Back() => IsPickStep = true;
    [RelayCommand] private void SelectAll() { foreach (var m in ModsView.Cast<ServerModChoice>()) m.IsChecked = true; }
    [RelayCommand] private void SelectNone() { foreach (var m in ModsView.Cast<ServerModChoice>()) m.IsChecked = false; }
    [RelayCommand] private void Cancel() => Dialogs.Close(this);

    [RelayCommand]
    private async Task Create()
    {
        if (SelectedInstance is not { } inst || IsBusy) return;
        if (string.IsNullOrWhiteSpace(Name)) { Dialogs.Toast(L.T("Give the server a name."), true); return; }
        IsBusy = true;
        try
        {
            Status = L.T("Copying files…");
            var cfg = inst.Config;
            var pack = ServerPackManager.Create(Name, cfg.MinecraftVersion, cfg.Loader, cfg.LoaderVersion, inst);
            var chosen = Mods.Where(m => m.IsChecked).Select(m => m.Item).ToList();
            await Task.Run(() =>
            {
                ServerPackManager.CopyMods(inst, pack, chosen);
                if (CopyConfigs) ServerPackManager.CopyConfigs(inst, pack);
            });
            Dialogs.Close(this);
            ServerService.Open(pack, "panel");
            if (await ServerService.InstallAsync(pack)) await ServerService.SetupAsync(pack);
        }
        catch (Exception ex)
        {
            Log.Error("Creating server pack failed", ex);
            Dialogs.Toast(L.F("Couldn't create the server pack: {0}", ex.Message), true);
        }
        finally { IsBusy = false; Status = null; }
    }

    /// <summary>Builds a server pack from a CurseForge server pack zip (downloaded through the CurseForge API).</summary>
    public static async Task CreateFromServerZipAsync(string name, string mc, ModLoader loader, string? loaderVersion, string zipUrl,
        ModpackSource? source, Instance? linkTo)
    {
        var pack = ServerPackManager.Create(name, mc, loader, loaderVersion, linkTo, source);
        ServerService.Open(pack, "panel");
        pack.IsBusy = true;
        var cts = new CancellationTokenSource();
        var job = DownloadManager.Begin(L.F("{0} - server pack", name), cts);
        try
        {
            pack.Status = L.T("Downloading the server pack…");
            var zip = Path.Combine(AppPaths.Cache, "serverpacks", Path.GetFileName(new Uri(zipUrl).LocalPath));
            await Http.DownloadFileAsync(zipUrl, zip, cts.Token);
            pack.Status = L.T("Unpacking…");
            await Task.Run(() => ServerInstaller.ExtractServerPack(zip, pack.ServerDir));
            File.Delete(zip);
        }
        catch (Exception ex)
        {
            job.Fail();
            Log.Error("Server pack download failed", ex);
            Dialogs.Toast(L.F("Couldn't download the server pack: {0}", ex.Message), true);
            return;
        }
        finally
        {
            job.Dispose();
            pack.IsBusy = false;
            pack.Status = null;
        }
        if (await ServerService.InstallAsync(pack)) await ServerService.SetupAsync(pack);
    }
}

/// <summary>First start: memory and the Minecraft EULA.</summary>
public partial class ServerSetupViewModel : ObservableObject
{
    public ServerPack Pack { get; }
    public TaskCompletionSource<bool> Result { get; } = new();
    [ObservableProperty] private int? _memoryMb;

    public ServerSetupViewModel(ServerPack pack)
    {
        Pack = pack;
        MemoryMb = pack.Config.MemoryMb > 0 ? pack.Config.MemoryMb : 4096;
    }

    [RelayCommand] private void OpenEula() => Dialogs.OpenUrl("https://aka.ms/MinecraftEULA");
    [RelayCommand] private void OpenProperties() { Accept(); ServerService.Open(Pack, "properties"); }

    [RelayCommand]
    private void Accept()
    {
        Pack.Config.MemoryMb = Math.Max(512, MemoryMb ?? 4096);
        Pack.Config.EulaAccepted = true;
        ServerService.WriteEula(Pack.ServerDir);
        Pack.Save();
        Pack.Refresh();
        Dialogs.Close(this);
        Result.TrySetResult(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Pack.Kill();
        Dialogs.Close(this);
        Result.TrySetResult(false);
    }
}

/// <summary>A server pack's own page.</summary>
public partial class ServerPackViewModel : ObservableObject
{
    public ServerPack Pack { get; }
    private readonly Dictionary<string, object> _tabs = [];
    [ObservableProperty] private string _tab;
    [ObservableProperty] private object? _currentTab;

    public ServerPackViewModel(ServerPack pack, string? tab)
    {
        Pack = pack;
        _tab = tab ?? "panel";
        CurrentTab = GetTab(_tab);
        Pack.PropertyChanged += OnPackChanged;
    }

    public void Detach()
    {
        Pack.PropertyChanged -= OnPackChanged;
        foreach (var t in _tabs.Values.OfType<ContentTabViewModel>()) t.Detach();
        foreach (var t in _tabs.Values.OfType<ServerPanelViewModel>()) t.Detach();
    }

    private void OnPackChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerPack.IsRunning) or nameof(ServerPack.IsBusy) or nameof(ServerPack.IsInstalled))
        {
            OnPropertyChanged(nameof(ToggleText));
            OnPropertyChanged(nameof(ToggleGlyph));
        }
    }

    public string ToggleText => Pack.IsRunning ? L.T("Stop") : Pack.IsInstalled ? L.T("Start") : L.T("Install");
    public string ToggleGlyph => Pack.IsRunning ? "" : Pack.IsInstalled ? "" : "";

    partial void OnTabChanged(string value) => CurrentTab = GetTab(value);

    private object GetTab(string key)
    {
        if (_tabs.TryGetValue(key, out var vm))
        {
            if (vm is ContentTabViewModel c) c.RefreshIfStale();
            if (vm is ServerPlayersViewModel p) p.Reload();
            if (vm is ServerPropertiesViewModel pr) pr.ReloadIfClean();
            return vm;
        }
        vm = key switch
        {
            "mods" => new ContentTabViewModel(Pack.Instance, ContentType.Mod),
            "properties" => new ServerPropertiesViewModel(Pack),
            "players" => new ServerPlayersViewModel(Pack),
            "logs" => new ServerLogsViewModel(Pack),
            "settings" => new ServerSettingsViewModel(Pack),
            _ => new ServerPanelViewModel(Pack),
        };
        _tabs[key] = vm;
        return vm;
    }

    [RelayCommand] private async Task Toggle() => await ServerService.ToggleAsync(Pack);
    [RelayCommand] private void OpenFolder() => Dialogs.OpenPath(Pack.ServerDir);
    [RelayCommand]
    private void Back()
    {
        Detach();
        MainViewModel.Instance.CloseServerPack();
    }
}

public partial class ServerPanelViewModel : ObservableObject
{
    public ServerPack Pack { get; }
    public ObservableCollection<string> Completions { get; } = [];
    [ObservableProperty] private string _command = "";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<string> _history = [];

    private static readonly string[] Commands =
    [
        "advancement", "attribute", "ban", "ban-ip", "banlist", "bossbar", "clear", "clone", "data", "datapack", "debug", "defaultgamemode",
        "deop", "difficulty", "effect", "enchant", "execute", "experience", "fill", "forceload", "function", "gamemode", "gamerule", "give",
        "help", "item", "kick", "kill", "list", "locate", "loot", "me", "msg", "op", "pardon", "pardon-ip", "particle", "place", "playsound",
        "recipe", "reload", "save-all", "save-off", "save-on", "say", "schedule", "scoreboard", "seed", "setblock", "setidletimeout",
        "setworldspawn", "spawnpoint", "spectate", "spreadplayers", "stop", "stopsound", "summon", "tag", "team", "teleport", "tell",
        "tellraw", "time", "title", "tp", "trigger", "weather", "whitelist", "worldborder", "xp",
    ];
    private static readonly Dictionary<string, string[]> SubCommands = new()
    {
        ["whitelist"] = ["add", "remove", "list", "on", "off", "reload"],
        ["gamemode"] = ["survival", "creative", "adventure", "spectator"],
        ["defaultgamemode"] = ["survival", "creative", "adventure", "spectator"],
        ["difficulty"] = ["peaceful", "easy", "normal", "hard"],
        ["weather"] = ["clear", "rain", "thunder"],
        ["time"] = ["set day", "set night", "set noon", "set midnight", "add", "query daytime"],
        ["gamerule"] = ["keepInventory true", "keepInventory false", "doDaylightCycle", "doMobSpawning", "mobGriefing", "doFireTick", "playersSleepingPercentage"],
    };
    private static readonly HashSet<string> PlayerCommands = ["ban", "deop", "op", "kick", "pardon", "msg", "tell", "tp", "teleport", "give", "kill", "clear", "spectate", "xp", "experience", "effect", "enchant"];

    public ServerPanelViewModel(ServerPack pack)
    {
        Pack = pack;
        Pack.PlayersChanged += OnPlayers;
        _timer.Tick += (_, _) => Pack.OnPropertyChangedPublic(nameof(ServerPack.UptimeText));
        _timer.Start();
        UpdateCompletions();
    }

    public string PlayersText => Pack.OnlinePlayers.Count == 0 ? L.T("Nobody online") : string.Join(", ", Pack.OnlinePlayers);
    private void OnPlayers() => Ui.Post(() => { OnPropertyChanged(nameof(PlayersText)); UpdateCompletions(); });

    public void Detach()
    {
        _timer.Stop();
        Pack.PlayersChanged -= OnPlayers;
    }

    partial void OnCommandChanged(string value) => UpdateCompletions();

    /// <summary>Candidates for the command box: command names, then sub-commands and online player names.</summary>
    private void UpdateCompletions()
    {
        var text = Command.TrimStart('/');
        var parts = text.Split(' ');
        IEnumerable<string> list;
        if (parts.Length <= 1) list = Commands;
        else
        {
            var head = string.Join(' ', parts[..^1]);
            var cmd = parts[0];
            var options = new List<string>();
            if (parts.Length == 2 && SubCommands.TryGetValue(cmd, out var subs)) options.AddRange(subs);
            if (PlayerCommands.Contains(cmd) || (cmd is "whitelist" or "gamemode" && parts.Length == 3)) options.AddRange(Pack.OnlinePlayers);
            if (parts.Length == 2 && PlayerCommands.Contains(cmd)) options.Add("@a");
            list = options.Select(o => $"{head} {o}");
        }
        var items = list.ToList();
        if (items.SequenceEqual(Completions)) return;
        Completions.Clear();
        foreach (var i in items) Completions.Add(i);
    }

    [RelayCommand]
    private void Send()
    {
        var cmd = Command.Trim().TrimStart('/');
        if (cmd.Length == 0) return;
        if (!Pack.IsRunning) { Dialogs.Toast(L.T("Start the server to send commands."), true); return; }
        Pack.SendCommand(cmd);
        _history.Remove(cmd);
        _history.Add(cmd);
        Command = "";
        if (cmd.StartsWith("whitelist") || cmd.StartsWith("op ") || cmd.StartsWith("deop") || cmd.StartsWith("ban") || cmd.StartsWith("pardon"))
            Pack.OnPropertyChangedPublic(nameof(ServerPack.PlayerCountText));
    }

    public string? HistoryAt(int index) => index >= 0 && index < _history.Count ? _history[^(index + 1)] : null;

    [RelayCommand] private async Task Start() => await ServerService.StartAsync(Pack);
    [RelayCommand] private async Task Stop() => await Pack.StopAsync();
    [RelayCommand] private void Kill() => Pack.Kill();
    [RelayCommand]
    private async Task Restart()
    {
        await Pack.StopAsync();
        await ServerService.StartAsync(Pack);
    }
    [RelayCommand] private void Clear() => Pack.Instance.ClearLog();
}

public partial class PropertyItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string Description { get; init; } = "";
    public PropertyKind Kind { get; init; }
    public string[]? Choices { get; init; }
    [ObservableProperty] private string _value = "";
    public bool IsBool => Kind == PropertyKind.Bool;
    public bool IsChoice => Kind == PropertyKind.Choice;
    public bool IsText => Kind is PropertyKind.Text or PropertyKind.Number;
    public bool HasDescription => Description.Length > 0;
    public bool BoolValue { get => Value == "true"; set { Value = value ? "true" : "false"; OnPropertyChanged(); } }
    public Action? Changed { get; set; }
    partial void OnValueChanged(string value) => Changed?.Invoke();
}

public partial class ServerPropertiesViewModel : ObservableObject
{
    public ServerPack Pack { get; }
    public ObservableCollection<PropertyItem> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isDirty;
    public bool Exists => File.Exists(Pack.PropertiesPath);

    public ServerPropertiesViewModel(ServerPack pack)
    {
        Pack = pack;
        ItemsView = new ListCollectionView(Items)
        {
            Filter = o => o is PropertyItem p && (Search.Length == 0
                || p.Key.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || p.Description.Contains(Search, StringComparison.OrdinalIgnoreCase)),
        };
        Load();
    }

    partial void OnSearchChanged(string value) => ItemsView.Refresh();

    public void ReloadIfClean() { if (!IsDirty) Load(); }

    [RelayCommand]
    private void Load()
    {
        Items.Clear();
        var props = ServerProperties.Load(Pack.PropertiesPath);
        var order = ServerProperties.Known.Keys.ToList();
        foreach (var (key, value) in props.Entries.OrderBy(e => order.IndexOf(e.Key) is var i && i >= 0 ? i : order.Count).ThenBy(e => e.Key))
        {
            var info = ServerProperties.Describe(key, value);
            Items.Add(new PropertyItem { Key = key, Description = info.Description, Kind = info.Kind, Choices = info.Choices, Value = value, Changed = () => IsDirty = true });
        }
        IsDirty = false;
        OnPropertyChanged(nameof(Exists));
    }

    [RelayCommand]
    private void Create()
    {
        ServerProperties.CreateDefault(Pack.PropertiesPath, SettingsService.Current.ServerPropertiesDefaults, Pack.Name);
        Load();
    }

    [RelayCommand]
    private void Save()
    {
        var props = ServerProperties.Load(Pack.PropertiesPath);
        foreach (var i in Items) props.Set(i.Key, i.Value);
        props.Save(Pack.PropertiesPath);
        IsDirty = false;
        Pack.Refresh();
        Dialogs.Toast(Pack.IsRunning ? L.T("Saved. Restart the server to apply the changes.") : L.T("Saved."));
    }

    [RelayCommand] private void OpenFile() => Dialogs.OpenPath(Pack.PropertiesPath);
}

public partial class ServerPlayersViewModel : ObservableObject
{
    public ServerPack Pack { get; }
    public ObservableCollection<PlayerEntry> Players { get; } = [];
    [ObservableProperty] private string _newPlayer = "";
    [ObservableProperty] private bool _onlineOnly;
    public string OnlineText => L.F("{0} online", Pack.OnlinePlayers.Count);
    public bool IsEmpty => Players.Count == 0;

    public ServerPlayersViewModel(ServerPack pack)
    {
        Pack = pack;
        Pack.PlayersChanged += () => Ui.Post(Reload);
        Reload();
    }

    partial void OnOnlineOnlyChanged(bool value) => Reload();

    public void Reload()
    {
        Players.Clear();
        foreach (var p in ServerPlayers.Load(Pack).Where(p => !OnlineOnly || p.IsOnline)) Players.Add(p);
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task Apply(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Dialogs.Toast(ex.Message, true); }
        await Task.Delay(Pack.IsRunning ? 800 : 0);
        Reload();
    }

    [RelayCommand] private Task ToggleOp(PlayerEntry p) => Apply(() => ServerPlayers.SetOpAsync(Pack, p, !p.IsOp));
    [RelayCommand] private Task ToggleWhitelist(PlayerEntry p) => Apply(() => ServerPlayers.SetWhitelistedAsync(Pack, p.Name, !p.IsWhitelisted, p));
    [RelayCommand]
    private async Task ToggleBan(PlayerEntry p)
    {
        if (!p.IsBanned && !await Dialogs.ConfirmAsync(L.T("Ban player"), L.F("Ban {0} from this server?", p.Name), L.T("Ban"), true)) return;
        await Apply(() => ServerPlayers.SetBannedAsync(Pack, p, !p.IsBanned));
    }
    [RelayCommand]
    private void Kick(PlayerEntry p)
    {
        Pack.SendCommand($"kick {p.Name}");
    }

    [RelayCommand]
    private async Task AddToWhitelist()
    {
        var name = NewPlayer.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{1,16}$"))
        {
            Dialogs.Toast(L.T("That isn't a valid Minecraft username."), true);
            return;
        }
        NewPlayer = "";
        await Apply(() => ServerPlayers.SetWhitelistedAsync(Pack, name, true));
    }

    [RelayCommand] private void RefreshList() { if (Pack.IsRunning) Pack.SendCommand("list"); Reload(); }
}

public record ServerLogFile(string Path, string Name, DateTime Modified, long Size)
{
    public string Info => $"{Modified:g} · {DownloadManager.FormatBytes(Size)}";
}

/// <summary>Previous server logs (the live one is on the Panel).</summary>
public partial class ServerLogsViewModel : ObservableObject
{
    public ServerPack Pack { get; }
    public ObservableCollection<ServerLogFile> Files { get; } = [];
    public ObservableCollection<LogLine> Lines { get; } = [];
    [ObservableProperty] private ServerLogFile? _selected;
    public bool IsEmpty => Files.Count == 0;

    public ServerLogsViewModel(ServerPack pack)
    {
        Pack = pack;
        LoadFiles();
    }

    [RelayCommand]
    private void LoadFiles()
    {
        Files.Clear();
        var dir = Path.Combine(Pack.ServerDir, "logs");
        if (Directory.Exists(dir))
            foreach (var f in new DirectoryInfo(dir).GetFiles()
                         .Where(f => (f.Name.EndsWith(".log.gz") || f.Name.EndsWith(".log")) && f.Name != "latest.log")
                         .OrderByDescending(f => f.LastWriteTime))
                Files.Add(new ServerLogFile(f.FullName, f.Name, f.LastWriteTime, f.Length));
        OnPropertyChanged(nameof(IsEmpty));
        Selected = Files.FirstOrDefault();
    }

    partial void OnSelectedChanged(ServerLogFile? value)
    {
        Lines.Clear();
        if (value == null) return;
        try
        {
            using var fs = File.OpenRead(value.Path);
            using Stream s = value.Name.EndsWith(".gz") ? new GZipStream(fs, CompressionMode.Decompress) : fs;
            using var reader = new StreamReader(s);
            var parser = new GameLogParser();
            while (reader.ReadLine() is { } line)
                foreach (var l in parser.Feed(line)) Lines.Add(l);
        }
        catch (Exception ex) { Lines.Add(new LogLine(ex.Message, LogLevel.Error)); }
    }

    [RelayCommand] private void OpenFolder() => Dialogs.OpenPath(Path.Combine(Pack.ServerDir, "logs"));
}

public partial class LinkChoice : ObservableObject
{
    public Instance Instance { get; init; } = null!;
    [ObservableProperty] private bool _isLinked;
    public Action<bool>? Changed { get; set; }
    partial void OnIsLinkedChanged(bool value) => Changed?.Invoke(value);
}

public partial class ServerSettingsViewModel : ObservableObject
{
    public ServerPack Pack { get; }
    public ObservableCollection<LinkChoice> Links { get; } = [];
    [ObservableProperty] private string _name;
    [ObservableProperty] private int? _memoryMb;
    [ObservableProperty] private string _jvmArgs;
    [ObservableProperty] private string _javaPath;
    [ObservableProperty] private string _startArgs;

    /// <summary>The full command the server is started with, as it would be typed in a terminal.</summary>
    public string CommandPreview
    {
        get
        {
            var mem = Math.Max(512, MemoryMb ?? 4096);
            var java = string.IsNullOrWhiteSpace(JavaPath) ? L.F("java ({0})", L.F("Java {0}", Pack.Config.JavaMajor)) : JavaPath.Trim();
            return string.Join(' ', new[] { java, $"-Xmx{mem}M", $"-Xms{Math.Min(mem, 1024)}M", JvmArgs.Trim(), StartArgs.Trim() }.Where(s => s.Length > 0));
        }
    }

    partial void OnMemoryMbChanged(int? value) => OnPropertyChanged(nameof(CommandPreview));
    partial void OnJvmArgsChanged(string value) => OnPropertyChanged(nameof(CommandPreview));
    partial void OnJavaPathChanged(string value) => OnPropertyChanged(nameof(CommandPreview));
    partial void OnStartArgsChanged(string value) => OnPropertyChanged(nameof(CommandPreview));

    [RelayCommand]
    private void ResetStartArgs()
    {
        if (ServerInstaller.DefaultLaunchArgs(Pack) is { } args) StartArgs = ServerPack.JoinArgs(args);
        else Dialogs.Toast(L.T("Couldn't detect the default start command. Reinstall the server files to restore it."), true);
    }

    [RelayCommand]
    private async Task BrowseJava()
    {
        if (await Ui.Platform.PickFileAsync(L.T("Select the Java executable"), [Minecraft.JavaManager.ExecutableFilter]) is { } path)
            JavaPath = path;
    }

    public ServerSettingsViewModel(ServerPack pack)
    {
        Pack = pack;
        _name = pack.Name;
        _memoryMb = pack.Config.MemoryMb > 0 ? pack.Config.MemoryMb : 4096;
        _jvmArgs = pack.Config.JvmArgs ?? "";
        _javaPath = pack.Config.JavaPath ?? "";
        _startArgs = ServerPack.JoinArgs(pack.Config.LaunchArgs);
        foreach (var inst in InstanceManager.Instances)
        {
            var c = new LinkChoice { Instance = inst, IsLinked = pack.Config.LinkedInstances.Contains(inst.Id) };
            c.Changed = v => ServerPackManager.SetLinked(Pack, inst, v);
            Links.Add(c);
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (!string.IsNullOrWhiteSpace(Name)) Pack.Instance.Config.Name = Name.Trim();
        Pack.Config.MemoryMb = Math.Max(512, MemoryMb ?? 4096);
        Pack.Config.JvmArgs = string.IsNullOrWhiteSpace(JvmArgs) ? null : JvmArgs.Trim();
        Pack.Config.JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath.Trim();
        var args = ServerPack.SplitArgs(StartArgs);
        if (args.Count > 0) Pack.Config.LaunchArgs = args;
        else StartArgs = ServerPack.JoinArgs(Pack.Config.LaunchArgs);
        Pack.Save();
        Pack.Refresh();
        Dialogs.Toast(Pack.IsRunning ? L.T("Saved. Restart the server to apply the changes.") : L.T("Saved."));
    }

    [RelayCommand] private void OpenFolder() => Dialogs.OpenPath(Pack.ServerDir);

    [RelayCommand]
    private async Task Reinstall()
    {
        if (Pack.IsRunning) { Dialogs.Toast(L.T("Stop the server first."), true); return; }
        Pack.Config.InstalledKey = null;
        await ServerService.InstallAsync(Pack);
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (!await Dialogs.ConfirmAsync(L.T("Delete server pack"),
                L.F("Delete \"{0}\" with its world and all files? This can't be undone.", Pack.Name), L.T("Delete"), true)) return;
        try
        {
            MainViewModel.Instance.CloseServerPack();
            ServerPackManager.Delete(Pack);
        }
        catch (Exception ex) { Dialogs.Toast(L.F("Couldn't delete: {0}", ex.Message), true); }
    }
}
