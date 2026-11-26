using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    /// <summary>Host accepts players through hole punching (shows the "accept a reply code" box).</summary>
    [ObservableProperty] private bool _holePunching;
    /// <summary>Hole punching status for the host, or a reason it is unavailable (the address itself is in PunchAddress).</summary>
    [ObservableProperty] private string? _punchText;
    /// <summary>Host's public UDP address for hole punching. Masked on screen (people stream), with a copy button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPunchAddress))]
    private string? _punchAddress;
    public bool HasPunchAddress => PunchAddress != null;

    // Guest, while hole punching
    /// <summary>Code the guest sends back to the host so both sides can punch through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaitingForHost))]
    private string? _replyCode;
    public bool IsWaitingForHost => ReplyCode != null;

    /// <summary>"Encrypted, host verified" and how the connection is made (guest).</summary>
    [ObservableProperty] private string? _securityText;

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
/// host (TCP to the lobby port, or UDP hole punching), with no relay servers in between that could be blocked.
/// Every connection is end-to-end encrypted. The host's "Open to LAN" world is forwarded through it and shows up
/// in each guest's "LAN worlds" list.
/// </summary>
public static class LanService
{
    public static LanState State { get; } = new();
    private static LanSettings S => SettingsService.Current.Lan;

    private static LanHost? _host;
    private static LanGuest? _guest;
    private static UpnpMapping? _upnp;
    private static CancellationTokenSource? _hostCts;
    private static CancellationTokenSource? _joinCts;
    private static ECDiffieHellman? _identity;

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

    /// <summary>
    /// The host's long-lived key (kept encrypted in settings), so its fingerprint - and an invite code sent earlier -
    /// stays valid across launcher restarts.
    /// </summary>
    private static ECDiffieHellman Identity()
    {
        if (_identity != null) return _identity;
        if (!string.IsNullOrEmpty(S.ProtectedHostKey))
        {
            try
            {
                var key = SecureChannel.NewKey();
                key.ImportPkcs8PrivateKey(SecureStore.Unprotect(Convert.FromBase64String(S.ProtectedHostKey)), out _);
                return _identity = key;
            }
            catch (Exception ex) { Log.Warn("LAN host key unreadable, making a new one: " + ex.Message); }
        }
        var fresh = SecureChannel.NewKey();
        S.ProtectedHostKey = Convert.ToBase64String(SecureStore.Protect(fresh.ExportPkcs8PrivateKey()));
        SettingsService.Save();
        return _identity = fresh;
    }

    // ---------- hosting ----------

    public static void Host(string lobbyName, string? password, int maxPlayers, int port, bool useUpnp, bool useIPv6, bool holePunching)
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
        S.UseIPv6 = useIPv6;
        S.HolePunching = holePunching;
        SettingsService.Save();

        var identity = Identity();
        var host = new LanHost(new LanHostOptions
        {
            LobbyName = lobbyName,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Port = port,
            MaxPlayers = maxPlayers,
            HostName = account.Username,
            HostUuid = account.Uuid,
            IsBanned = IsBanned,
            Identity = identity,
            HolePunching = holePunching,
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
        host.PlayerEvent += (name, e) => Ui.Post(() => OnPlayerEvent(host, name, e));
        State.Error = null;
        State.LobbyName = lobbyName;
        State.LobbyPort = port;
        State.HasPassword = host.HasPassword;
        State.HolePunching = holePunching;
        State.PunchText = holePunching ? L.T("Finding your public address for hole punching…") : null;
        State.PunchAddress = null;
        State.InviteCode = null;
        State.AddressesText = L.T("Finding your addresses…");
        State.ReachabilityText = null;
        State.ReachabilityWarning = false;
        State.Mode = LanMode.Hosting;
        SyncHost();
        _ = PrepareReachabilityAsync(host, SecureChannel.Fingerprint(identity), port, useUpnp, useIPv6, _hostCts.Token);
    }

    /// <summary>Opens the port on the router (UPnP), asks STUN for the punching address, and builds the invite code.</summary>
    private static async Task PrepareReachabilityAsync(LanHost host, string fingerprint, int port, bool useUpnp, bool useIPv6, CancellationToken ct)
    {
        var upnpTask = useUpnp ? Task.Run(() => UpnpMapping.TryMapAsync(port, ct), ct) : Task.FromResult<UpnpMapping?>(null);
        var publicTask = Task.Run(async () => { try { return await NetInfo.PublicIPv4Async(ct); } catch { return null; } }, ct);
        var stunTask = host.Mux is { } mux
            ? Task.Run(async () => { try { return await mux.DiscoverAsync(ct); } catch { return null; } }, ct)
            : Task.FromResult<IPEndPoint?>(null);
        UpnpMapping? upnp = null;
        IPAddress? pub = null;
        IPEndPoint? udp = null;
        try { upnp = await upnpTask; } catch { }
        try { pub = await publicTask; } catch { }
        try { udp = await stunTask; } catch { }
        if (ct.IsCancellationRequested)
        {
            if (upnp != null) _ = upnp.RemoveAsync();
            return;
        }

        var v6 = useIPv6 ? NetInfo.GlobalIPv6() : [];
        var local = NetInfo.LocalIPv4();
        var routerWan = upnp?.ExternalAddress;
        pub ??= udp?.Address;
        var v4 = pub ?? (routerWan != null && !NetInfo.IsCgnat(routerWan) ? routerWan : null);
        var addresses = new List<string>();
        if (v4 != null) addresses.Add(v4.ToString());
        addresses.AddRange(v6.Select(a => a.ToString()));
        addresses.AddRange(local.Select(a => a.ToString()));

        var notes = new List<string>();
        var warn = false;
        var cgnat = routerWan != null && (NetInfo.IsCgnat(routerWan) || (pub != null && !routerWan.Equals(pub)));
        if (upnp != null) notes.Add(L.F("Router port forwarding set up automatically (UPnP): TCP {0}.", port));
        else if (useUpnp)
        {
            notes.Add(L.F("The router did not accept automatic port forwarding (UPnP). If you forwarded TCP port {0} yourself, turn off automatic forwarding and this warning goes away.", port));
            // Hole punching or IPv6 can still get friends in, so only warn when neither is there.
            warn = host.Mux == null && v6.Count == 0;
        }
        else notes.Add(L.F("Using the port forwarding you set up on your router: TCP {0} to this computer.", port));

        if (cgnat)
        {
            notes.Add(L.T("Your internet provider puts you behind a shared address (CGNAT), so friends cannot reach you over IPv4 even with port forwarding. IPv6 or hole punching may still work; otherwise let a friend with a public address host."));
            warn = host.Mux == null && v6.Count == 0;
        }
        if (v6.Count > 0) notes.Add(L.T("IPv6 is available: friends with IPv6 can usually connect without port forwarding."));
        else if (!useIPv6) notes.Add(L.T("IPv6 addresses are left out of the invite code (turned off)."));
        notes.Add(L.T("Windows Firewall may ask whether to allow PyroLauncher on the network - allow it, or nobody can connect."));

        string? punchText = null;
        if (host.Mux is { } m)
        {
            if (udp == null)
            {
                punchText = L.T("Hole punching is unavailable: no STUN server answered, so your public UDP address is unknown.");
                warn = true;
            }
            else if (m.LikelySymmetricNat)
                punchText = L.T("Your router changes ports for every destination (symmetric NAT), so hole punching will probably fail; port forwarding is more reliable.");
            else
                punchText = L.T("Friends who cannot connect directly get a reply code to send you; paste it below.");
        }

        Ui.Post(() =>
        {
            if (_host != host || ct.IsCancellationRequested) { if (upnp != null) _ = upnp.RemoveAsync(); return; }
            _upnp = upnp;
            State.InviteCode = addresses.Count > 0 || udp != null ? LanProtocol.MakeCode(addresses, port, fingerprint, udp) : null;
            var lines = new List<string>();
            if (v4 != null) lines.Add(L.F("Internet (IPv4): {0}", LanProtocol.FormatEndpoint(v4.ToString(), port)));
            foreach (var a in v6) lines.Add(L.F("Internet (IPv6): {0}", LanProtocol.FormatEndpoint(a.ToString(), port)));
            foreach (var a in local) lines.Add(L.F("Same home network: {0}", LanProtocol.FormatEndpoint(a.ToString(), port)));
            if (udp != null) lines.Add(L.F("Hole punching (UDP): {0}", udp));
            lines.Add(L.F("Host key fingerprint: {0}", fingerprint));
            State.AddressesText = lines.Count > 0 ? string.Join("\n", lines) : L.T("No network address found. Are you connected to the internet?");
            State.ReachabilityText = string.Join("\n", notes.Select(n => "• " + n));
            State.ReachabilityWarning = warn;
            State.PunchText = punchText;
            State.PunchAddress = udp?.ToString();
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

    /// <summary>Host: starts punching towards a guest who sent a reply code.</summary>
    public static async Task AcceptReplyAsync(string code)
    {
        if (_host is not { } host) throw new LanException(L.T("You are not hosting a lobby."));
        if (!State.HolePunching) throw new LanException(L.T("Hole punching is turned off for this lobby."));
        if (LanProtocol.ParseReply(code) is not { } reply) throw new LanException(L.T("That is not a valid reply code."));
        if (IsBanned(reply.Udp.Address)) throw new LanException(L.T("You are banned from this lobby."));
        var session = host.AcceptReply(reply) ?? throw new LanException(L.T("Hole punching is turned off for this lobby."));
        try { await session.Established.WaitAsync(TimeSpan.FromSeconds(45)); }
        catch (TimeoutException) { throw new LanException(L.T("Could not reach the player. Both routers may block hole punching; try port forwarding or IPv6 instead.")); }
        catch (IOException) { throw new LanException(L.T("Could not reach the player. Both routers may block hole punching; try port forwarding or IPv6 instead.")); }
    }

    /// <summary>Host: a toast (Settings: LAN page) and an OS notification (Settings → Notifications) for joins and leaves.</summary>
    private static void OnPlayerEvent(LanHost host, string name, LanPlayerEvent e)
    {
        if (_host != host || e == LanPlayerEvent.Kicked) return; // the host kicked them, no need to tell them
        var text = e switch
        {
            LanPlayerEvent.Joined => L.F("{0} joined the lobby", name),
            LanPlayerEvent.TimedOut => L.F("{0} timed out (no answer for 30 seconds)", name),
            _ => L.F("{0} left the lobby", name),
        };
        if (S.NotifyPlayers)
        {
            Dialogs.Toast(text, e == LanPlayerEvent.TimedOut);
            Notifications.Notify(State.LobbyName ?? L.T("LAN lobby"), text, NotifyKind.Lobby);
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

    private static LanInvite ParseOrThrow(string code) =>
        LanProtocol.ParseCode(code) ?? throw new LanException(
            LanProtocol.ParseReply(code) != null
                ? L.T("That is a reply code - it goes to the host. Paste the host's invite code here.")
                : L.T("That is not a valid invite code or address."));

    /// <summary>
    /// Joins a lobby: directly over TCP first; if nothing answers and the host offers hole punching, punches
    /// through UDP (showing a reply code the guest sends to the host meanwhile).
    /// </summary>
    public static async Task JoinAsync(string code, string? password)
    {
        RequireEnabled();
        var account = RequireAccount();
        var invite = ParseOrThrow(code);
        Stop();
        S.LastJoinCode = code.Trim();
        SettingsService.Save();

        var cts = _joinCts = new CancellationTokenSource();
        State.Busy = true;
        LanRoute? route = null;
        try
        {
            LanGuest guest;
            try
            {
                if (invite.Hosts.Count == 0) throw new LanUnreachableException("");
                route = new TcpRoute(invite.Hosts, invite.Port) { Fingerprint = invite.Fingerprint };
                guest = await LanGuest.JoinAsync(route, account.Username, account.Uuid, password, cts.Token);
            }
            catch (LanUnreachableException) when (invite.Udp != null)
            {
                route?.Dispose();
                route = await PunchToHostAsync(invite, cts.Token);
                guest = await LanGuest.JoinAsync(route, account.Username, account.Uuid, password, cts.Token);
            }
            route = null; // the guest owns it now

            if (!S.Enabled || cts.IsCancellationRequested) { guest.Dispose(); return; } // turned off or cancelled while connecting
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
            State.HostAddress = guest.HostAddress;
            State.LobbyName = guest.LobbyName;
            State.SecurityText = (guest.Verified
                    ? L.T("Encrypted end to end. The host proved it is the one from the invite code.")
                    : L.T("Encrypted end to end, but the host could not be verified because you typed an address instead of an invite code."))
                + " " + (guest.ViaPunch ? L.T("Connected through hole punching (UDP).") : L.T("Connected directly (TCP)."));
            State.Mode = LanMode.Joined;
            SyncGuest();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            route?.Dispose();
            State.Busy = false;
            State.ReplyCode = null;
            if (_joinCts == cts) _joinCts = null;
        }
    }

    private static async Task<PunchRoute> PunchToHostAsync(LanInvite invite, CancellationToken ct)
    {
        var mux = new UdpMux(acceptUnsolicited: false);
        try
        {
            if (await mux.DiscoverAsync(ct) is not { } me)
                throw new LanException(L.T("The host could not be reached directly, and hole punching needs your public address, but no STUN server answered."));
            var session = mux.Connect(invite.Udp!, UdpMux.NewSessionId(), opener: true);
            // Some routers let our packets in without the host punching back; give that a few seconds first.
            try { await session.Established.WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch (TimeoutException)
            {
                Ui.Post(() => State.ReplyCode = LanProtocol.MakeReply(me, session.Id));
                try { await session.Established.WaitAsync(TimeSpan.FromMinutes(3), ct); }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    throw new LanException(mux.LikelySymmetricNat
                        ? L.T("Hole punching failed: your router changes ports for every destination (symmetric NAT). Ask the host to forward the port or use IPv6.")
                        : L.T("Hole punching failed. Make sure the host pasted your reply code; if it still fails, both routers block it and the host needs port forwarding or IPv6."));
                }
            }
            return new PunchRoute(mux, session) { Fingerprint = invite.Fingerprint };
        }
        catch { mux.Dispose(); throw; }
    }

    /// <summary>Stops a join that is still connecting or waiting for the host.</summary>
    public static void CancelJoin() => _joinCts?.Cancel();

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

    /// <summary>Connection test before joining (direct TCP only; hole punching needs the host's help).</summary>
    public static async Task<LanTestResult> TestAsync(string code, IProgress<int>? progress, CancellationToken ct)
    {
        var invite = ParseOrThrow(code);
        if (invite.Hosts.Count == 0)
            throw new LanException(L.T("This lobby can only be reached through hole punching, which needs the host to accept your reply code. Press Join to start."));
        using var route = new TcpRoute(invite.Hosts, invite.Port) { Fingerprint = invite.Fingerprint };
        try { return await LanGuest.ProbeAsync(route, 10, progress, ct); }
        catch (LanUnreachableException ex) when (invite.Udp != null)
        {
            throw new LanException(ex.Message + "\n" + L.T("A direct connection is not possible, but the host offers hole punching: press Join and send the host the reply code you get."));
        }
    }

    /// <summary>Ping test against the lobby this computer is in.</summary>
    public static Task<LanTestResult> TestCurrentAsync(IProgress<int>? progress, CancellationToken ct) =>
        _guest is { } g ? g.ProbeAsync(progress, ct) : throw new LanException(L.T("You are not in a lobby."));

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
        _joinCts?.Cancel();
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
        State.HolePunching = false;
        State.PunchText = null;
        State.PunchAddress = null;
        State.InviteCode = null;
        State.AddressesText = null;
        State.ReachabilityText = null;
        State.HostAddress = null;
        State.SecurityText = null;
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
