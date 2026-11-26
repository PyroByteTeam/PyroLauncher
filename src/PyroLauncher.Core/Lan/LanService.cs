using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using PyroLauncher.Accounts;
using PyroLauncher.Core;
using PyroLauncher.UI;

namespace PyroLauncher.Lan;

public enum LanMode { Idle, Hosting, Joined }

/// <summary>One row in the lobby's player list.</summary>
public sealed class LanPlayerItem
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Uuid { get; init; } = "";
    public bool IsHost { get; init; }
    public bool IsYou { get; init; }
    public int? PingMs { get; init; }
    /// <summary>Only known to the host.</summary>
    public string? Ip { get; init; }
    /// <summary>Kick and ban buttons: only the host, and never on themselves.</summary>
    public bool CanModerate { get; init; }

    public string AvatarUrl => $"https://mc-heads.net/avatar/{(Uuid.Length >= 32 ? Uuid : Name)}/32";
    public string PingText => IsHost && IsYou ? "" : PingMs is int p ? L.F("{0} ms", p) : "…";
    public string RoleText => IsHost ? L.T("Host") : IsYou ? L.T("You") : "";
    public bool HasRole => RoleText.Length > 0;
    public bool HasIp => Ip != null;
}

/// <summary>State of LAN play, shown in the header status popup and on the LAN page. Changed on the UI thread only.</summary>
public partial class LanState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLobbyActive), nameof(IsHosting), nameof(IsJoined), nameof(IsIdle), nameof(HeaderText), nameof(StatusText), nameof(PlayersText))]
    private LanMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _lobbyName;

    /// <summary>Name of the world the host has open to LAN, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorld), nameof(WorldText))]
    private string? _worldMotd;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _error;

    // Host
    [ObservableProperty] private string? _inviteCode;
    [ObservableProperty] private string? _addressesText;
    [ObservableProperty] private string? _reachabilityText;
    [ObservableProperty] private bool _reachabilityWarning;
    [ObservableProperty] private int _lobbyPort;
    [ObservableProperty] private bool _hasPassword;
    /// <summary>Port of the world on this computer (host) - never exposed to the internet.</summary>
    [ObservableProperty] private int? _worldPort;
    [ObservableProperty] private bool _manualWorldPort;

    // Guest
    [ObservableProperty] private string? _hostAddress;
    [ObservableProperty] private int? _pingMs;
    /// <summary>Port on this computer that Minecraft connects to (guest).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalAddress))]
    private int? _localGamePort;

    public ObservableCollection<LanPlayerItem> Players { get; } = [];
    public ObservableCollection<LanBan> Bans { get; } = [];

    public LanState() => Players.CollectionChanged += (_, _) =>
    {
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(PlayersText));
    };

    public bool IsLobbyActive => Mode != LanMode.Idle;
    public bool IsHosting => Mode == LanMode.Hosting;
    public bool IsJoined => Mode == LanMode.Joined;
    public bool IsIdle => Mode == LanMode.Idle;
    public bool HasWorld => WorldMotd != null;
    public string LocalAddress => LocalGamePort is int p ? $"127.0.0.1:{p}" : "";

    public string WorldText => WorldMotd is { } w
        ? L.F("World \"{0}\" is open to LAN.", w)
        : IsHosting ? L.T("Waiting for you to open a world to LAN in Minecraft (Esc → Open to LAN).") : L.T("Waiting for the host to open a world to LAN.");

    /// <summary>Short text next to the header icon.</summary>
    public string HeaderText => IsLobbyActive ? L.F("LAN · {0}", Players.Count) : L.T("LAN");

    public string StatusText => Mode switch
    {
        LanMode.Hosting => L.F("You are hosting \"{0}\".", LobbyName ?? ""),
        LanMode.Joined => L.F("Connected to \"{0}\".", LobbyName ?? ""),
        _ => L.T("No LAN lobby is running."),
    };

    public string PlayersText => IsLobbyActive ? L.F("Players: {0}", Players.Count) : L.T("Start or join a lobby on the LAN page.");

    internal void RefreshTexts()
    {
        OnPropertyChanged(nameof(WorldText));
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>
/// Internet play that looks like LAN to Minecraft. One player hosts a lobby; the others connect straight to the
/// host's lobby port (no relay servers, so nothing in between can be blocked). The host's "Open to LAN" world is
/// forwarded through that connection and shows up in each guest's "LAN worlds" list.
/// </summary>
public static class LanService
{
    public static LanState State { get; } = new();
    private static LanSettings S => SettingsService.Current.Lan;

    private static LanHost? _host;
    private static LanGuest? _guest;
    private static UpnpMapping? _upnp;
    private static CancellationTokenSource? _hostCts;

    /// <summary>Raised on the UI thread when a lobby ends unexpectedly, with the reason.</summary>
    public static event Action<string>? Disconnected;

    public static void Init()
    {
        State.Enabled = S.Enabled;
        State.Bans.Clear();
        foreach (var b in S.Bans) State.Bans.Add(b);
    }

    public static void SetEnabled(bool on)
    {
        if (!on) Stop();
        S.Enabled = on;
        State.Enabled = on;
        SettingsService.Save();
    }

    private static Account RequireAccount() =>
        AccountManager.Selected ?? throw new LanException(L.T("Add a Minecraft account first: your lobby name comes from it."));

    private static void RequireEnabled()
    {
        if (!S.Enabled) throw new LanException(L.T("LAN is turned off."));
    }

    // ---------- hosting ----------

    public static void Host(string lobbyName, string? password, int maxPlayers, int port, bool useUpnp)
    {
        RequireEnabled();
        var account = RequireAccount();
        Stop();

        lobbyName = LanProtocol.CleanText(lobbyName, 40);
        if (lobbyName.Length == 0) lobbyName = L.F("{0}'s lobby", account.Username);
        S.LobbyName = lobbyName;
        S.MaxPlayers = maxPlayers = Math.Clamp(maxPlayers, 2, 32);
        S.Port = port;
        S.UseUpnp = useUpnp;
        SettingsService.Save();

        var host = new LanHost(new LanHostOptions
        {
            LobbyName = lobbyName,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Port = port,
            MaxPlayers = maxPlayers,
            HostName = account.Username,
            HostUuid = account.Uuid,
            IsBanned = IsBanned,
        });
        try { host.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            host.Dispose();
            throw new LanException(L.F("Port {0} is already used by another program. Pick another lobby port.", port));
        }
        catch (SocketException ex)
        {
            host.Dispose();
            throw new LanException(L.F("Could not open port {0}: {1}", port, ex.Message));
        }

        _host = host;
        _hostCts = new CancellationTokenSource();
        host.Changed += () => Ui.Post(SyncHost);
        State.Error = null;
        State.LobbyName = lobbyName;
        State.LobbyPort = port;
        State.HasPassword = host.HasPassword;
        State.InviteCode = null;
        State.AddressesText = L.T("Finding your addresses…");
        State.ReachabilityText = null;
        State.ReachabilityWarning = false;
        State.Mode = LanMode.Hosting;
        SyncHost();
        _ = PrepareReachabilityAsync(port, useUpnp, _hostCts.Token);
    }

    /// <summary>Opens the port on the router (UPnP) and works out which addresses friends can use.</summary>
    private static async Task PrepareReachabilityAsync(int port, bool useUpnp, CancellationToken ct)
    {
        var upnpTask = useUpnp ? Task.Run(() => UpnpMapping.TryMapAsync(port, ct), ct) : Task.FromResult<UpnpMapping?>(null);
        var publicTask = Task.Run(async () => { try { return await NetInfo.PublicIPv4Async(ct); } catch { return null; } }, ct);
        UpnpMapping? upnp = null;
        IPAddress? pub = null;
        try { upnp = await upnpTask; } catch { }
        try { pub = await publicTask; } catch { }
        if (ct.IsCancellationRequested)
        {
            if (upnp != null) _ = upnp.RemoveAsync();
            return;
        }

        var v6 = NetInfo.GlobalIPv6();
        var local = NetInfo.LocalIPv4();
        var routerWan = upnp?.ExternalAddress;
        var v4 = pub ?? (routerWan != null && !NetInfo.IsCgnat(routerWan) ? routerWan : null);
        var addresses = new List<string>();
        if (v4 != null) addresses.Add(v4.ToString());
        addresses.AddRange(v6.Select(a => a.ToString()));
        addresses.AddRange(local.Select(a => a.ToString()));

        var notes = new List<string>();
        var warn = false;
        if (upnp != null) notes.Add(L.F("Router port forwarding set up automatically (UPnP): TCP {0}.", port));
        else if (useUpnp) { notes.Add(L.F("The router did not accept automatic port forwarding (UPnP). Forward TCP port {0} to this computer in your router settings, or use IPv6.", port)); warn = true; }
        else notes.Add(L.F("Automatic port forwarding is off. Forward TCP port {0} to this computer in your router settings, or use IPv6.", port));

        if (routerWan != null && NetInfo.IsCgnat(routerWan) || (routerWan != null && pub != null && !routerWan.Equals(pub)))
        {
            notes.Add(L.T("Your internet provider puts you behind a shared address (CGNAT), so friends cannot reach you over IPv4 even with port forwarding. IPv6 may still work; otherwise let a friend with a public address host."));
            warn = true;
        }
        if (v6.Count > 0) notes.Add(L.T("IPv6 is available: friends with IPv6 can usually connect without port forwarding."));
        notes.Add(L.T("Windows Firewall may ask whether to allow PyroLauncher on the network - allow it, or nobody can connect."));

        Ui.Post(() =>
        {
            if (_host == null || ct.IsCancellationRequested) { if (upnp != null) _ = upnp.RemoveAsync(); return; }
            _upnp = upnp;
            State.InviteCode = addresses.Count > 0 ? LanProtocol.MakeCode(addresses, port) : null;
            var lines = new List<string>();
            if (v4 != null) lines.Add(L.F("Internet (IPv4): {0}", LanProtocol.FormatEndpoint(v4.ToString(), port)));
            foreach (var a in v6) lines.Add(L.F("Internet (IPv6): {0}", LanProtocol.FormatEndpoint(a.ToString(), port)));
            foreach (var a in local) lines.Add(L.F("Same home network: {0}", LanProtocol.FormatEndpoint(a.ToString(), port)));
            State.AddressesText = lines.Count > 0 ? string.Join("\n", lines) : L.T("No network address found. Are you connected to the internet?");
            State.ReachabilityText = string.Join("\n", notes.Select(n => "• " + n));
            State.ReachabilityWarning = warn;
        });

        // UPnP leases last two hours; renew while the lobby is open.
        if (upnp != null)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(45), ct); } catch { break; }
                try { await upnp.RenewAsync(ct); } catch (Exception ex) { Log.Info("UPnP renew failed: " + ex.Message); }
            }
        }
    }

    private static void SyncHost()
    {
        if (_host is not { } h) return;
        var me = AccountManager.Selected;
        var items = new List<LanPlayerItem>
        {
            new() { Id = 0, Name = me?.Username ?? "Host", Uuid = me?.Uuid ?? "", IsHost = true, IsYou = true },
        };
        items.AddRange(h.Players.Select(p => new LanPlayerItem
        {
            Id = p.Id, Name = p.Name, Uuid = p.Uuid, PingMs = p.PingMs, Ip = p.Ip.ToString(), CanModerate = true,
        }));
        ReplacePlayers(items);
        var w = h.World;
        State.WorldMotd = w?.Motd;
        State.WorldPort = w?.Port;
        State.RefreshTexts();
    }

    public static void SetManualWorldPort(int? port)
    {
        if (_host == null) return;
        _host.SetManualGamePort(port);
        State.ManualWorldPort = port != null;
        SyncHost();
    }

    public static void Kick(LanPlayerItem p) => _host?.Kick(p.Id, false);

    public static void Ban(LanPlayerItem p)
    {
        if (_host == null || p.Ip == null || !IPAddress.TryParse(p.Ip, out var ip)) return;
        if (!S.Bans.Any(b => b.Ip == p.Ip))
        {
            var ban = new LanBan { Ip = p.Ip, Name = p.Name, BannedAt = DateTimeOffset.Now };
            lock (S.Bans) S.Bans.Add(ban);
            State.Bans.Add(ban);
            SettingsService.Save();
        }
        _host.KickIp(ip);
    }

    public static void Unban(LanBan ban)
    {
        lock (S.Bans) S.Bans.RemoveAll(b => b.Ip == ban.Ip);
        var item = State.Bans.FirstOrDefault(b => b.Ip == ban.Ip);
        if (item != null) State.Bans.Remove(item);
        SettingsService.Save();
    }

    private static bool IsBanned(IPAddress ip)
    {
        var s = LanProtocol.Normalize(ip).ToString();
        lock (S.Bans) return S.Bans.Any(b => b.Ip == s);
    }

    // ---------- joining ----------

    public static async Task JoinAsync(string code, string? password)
    {
        RequireEnabled();
        var account = RequireAccount();
        if (LanProtocol.ParseCode(code) is not { } parsed) throw new LanException(L.T("That is not a valid invite code or address."));
        var (hosts, port) = parsed;
        Stop();
        S.LastJoinCode = code.Trim();
        SettingsService.Save();

        State.Busy = true;
        try
        {
            var guest = await LanGuest.JoinAsync(hosts, port, account.Username, account.Uuid, password, CancellationToken.None);
            if (!S.Enabled) { guest.Dispose(); return; } // turned off while connecting
            _guest = guest;
            guest.Changed += () => Ui.Post(SyncGuest);
            guest.Closed += reason => Ui.Post(() =>
            {
                if (_guest != guest) return;
                _guest = null;
                Reset();
                if (reason != null) Disconnected?.Invoke(reason);
            });
            State.Error = null;
            State.HostAddress = LanProtocol.FormatEndpoint(guest.HostAddress, port);
            State.LobbyPort = port;
            State.LobbyName = guest.LobbyName;
            State.Mode = LanMode.Joined;
            SyncGuest();
        }
        finally { State.Busy = false; }
    }

    private static void SyncGuest()
    {
        if (_guest is not { } g) return;
        State.LobbyName = g.LobbyName;
        State.PingMs = g.PingMs;
        State.WorldMotd = g.WorldMotd;
        State.LocalGamePort = g.LocalGamePort;
        ReplacePlayers(g.Players.Select(p => new LanPlayerItem
        {
            Id = p.Id, Name = p.Name, Uuid = p.Uuid, IsHost = p.IsHost, IsYou = p.Id == g.MyId,
            PingMs = p.IsHost ? g.PingMs : p.PingMs,
        }).ToList());
        State.RefreshTexts();
    }

    public static Task<LanTestResult> TestAsync(string code, IProgress<int>? progress, CancellationToken ct)
    {
        if (LanProtocol.ParseCode(code) is not { } parsed) throw new LanException(L.T("That is not a valid invite code or address."));
        var (hosts, port) = parsed;
        return LanGuest.ProbeAsync(hosts, port, 10, progress, ct);
    }

    /// <summary>Ping test against the lobby this computer is in.</summary>
    public static Task<LanTestResult> TestCurrentAsync(IProgress<int>? progress, CancellationToken ct)
    {
        if (_guest is not { } g) throw new LanException(L.T("You are not in a lobby."));
        return LanGuest.ProbeAsync([g.HostAddress], g.Port, 10, progress, ct);
    }

    public static Task<(System.Text.Json.Nodes.JsonObject? Status, TimeSpan Ping)> TestWorldAsync(CancellationToken ct) =>
        _guest is { } g ? g.TestWorldAsync(ct) : throw new LanException(L.T("You are not in a lobby."));

    // ---------- common ----------

    private static void ReplacePlayers(List<LanPlayerItem> items)
    {
        State.Players.Clear();
        foreach (var p in items) State.Players.Add(p);
    }

    /// <summary>Leaves or closes whatever lobby is running.</summary>
    public static void Stop()
    {
        _hostCts?.Cancel();
        _hostCts = null;
        _host?.Dispose();
        _host = null;
        if (_upnp != null) { _ = _upnp.RemoveAsync(); _upnp = null; }
        _guest?.Dispose();
        _guest = null;
        Reset();
    }

    private static void Reset()
    {
        State.Mode = LanMode.Idle;
        State.Players.Clear();
        State.LobbyName = null;
        State.WorldMotd = null;
        State.WorldPort = null;
        State.ManualWorldPort = false;
        State.InviteCode = null;
        State.AddressesText = null;
        State.ReachabilityText = null;
        State.HostAddress = null;
        State.PingMs = null;
        State.LocalGamePort = null;
        State.RefreshTexts();
    }

    /// <summary>On exit: close the lobby and remove the router port mapping (waits briefly for the router).</summary>
    public static void Shutdown()
    {
        var upnp = _upnp;
        _upnp = null;
        Stop();
        if (upnp != null) try { Task.Run(upnp.RemoveAsync).Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
