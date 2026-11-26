using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyroLauncher.Core;

namespace PyroLauncher.Lan;

public sealed class LanHostOptions
{
    public required string LobbyName { get; init; }
    public string? Password { get; init; }
    public int Port { get; init; } = LanProtocol.DefaultPort;
    public int MaxPlayers { get; init; } = 8;
    public required string HostName { get; init; }
    public required string HostUuid { get; init; }
    public required Func<IPAddress, bool> IsBanned { get; init; }
}

/// <summary>A player connected to the lobby this computer hosts.</summary>
public sealed class HostedPlayer
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string Uuid { get; init; }
    public required IPAddress Ip { get; init; }
    public required string Token { get; init; }
    internal LineWriter Writer { get; init; } = null!;
    internal TcpClient Client { get; init; } = null!;
    internal CancellationTokenSource Cts { get; } = new();
    internal int Tunnels;
    public int? PingMs { get; internal set; }
}

/// <summary>
/// The lobby server. Listens on one TCP port (IPv4 and IPv6), authenticates players, and forwards their Minecraft
/// connections to the LAN world open on this computer. Guests can only ever reach that one world port, nothing else.
/// </summary>
public sealed class LanHost : IDisposable
{
    private const int MaxConnectionsPerIp = 12;
    private const int MaxConnections = 96;
    private const int MaxTunnelsPerPlayer = 8;

    private readonly LanHostOptions _o;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, HostedPlayer> _players = new();
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private readonly LanWorldWatcher _watcher = new();
    private TcpListener? _listener;
    private int _nextId = 1, _connections;
    private int? _manualGamePort;

    public LanHost(LanHostOptions options) => _o = options;

    /// <summary>Raised on a background thread whenever players, pings or the world change.</summary>
    public event Action? Changed;

    public int Port => _o.Port;
    public string LobbyName => _o.LobbyName;
    public bool HasPassword => !string.IsNullOrEmpty(_o.Password);
    public IReadOnlyList<HostedPlayer> Players => _players.Values.OrderBy(p => p.Id).ToList();

    /// <summary>World being shared: the manual port if set, otherwise what Minecraft announces.</summary>
    public LanWorld? World => _manualGamePort is int p ? new LanWorld(_watcher.World?.Motd ?? _o.LobbyName, p) : _watcher.World;
    public LanWorld? DetectedWorld => _watcher.World;

    public void SetManualGamePort(int? port)
    {
        _manualGamePort = port;
        BroadcastState();
        Changed?.Invoke();
    }

    public void Start()
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, _o.Port);
            listener.Server.DualMode = true;
            listener.Start(32);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
        {
            listener = new TcpListener(IPAddress.Any, _o.Port);
            listener.Start(32);
        }
        _listener = listener;
        _watcher.Changed += _ => { BroadcastState(); Changed?.Invoke(); };
        _watcher.Start();
        _ = AcceptLoop(listener, _cts.Token);
        _ = PingLoop(_cts.Token);
        Log.Info($"LAN lobby \"{_o.LobbyName}\" listening on TCP {_o.Port}");
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var ip = LanProtocol.Normalize(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
        var count = _perIp.AddOrUpdate(ip, 1, (_, n) => n + 1);
        var total = Interlocked.Increment(ref _connections);
        try
        {
            client.NoDelay = true;
            using var conn = client;
            var stream = client.GetStream();
            var writer = new LineWriter(stream);
            if (_o.IsBanned(ip))
            {
                await writer.SendAsync(LanProtocol.Msg("denied", ("reason", "banned")), ct);
                return;
            }
            if (count > MaxConnectionsPerIp || total > MaxConnections) return;

            var nonce = LanProtocol.NewToken(16);
            await writer.SendAsync(LanProtocol.Msg("hi", ("app", LanProtocol.App), ("v", LanProtocol.Version),
                ("name", _o.LobbyName), ("pw", HasPassword), ("nonce", nonce)), ct);

            var reader = new LineReader(stream);
            JsonObject? hello;
            using (var hs = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hs.CancelAfter(LanProtocol.HandshakeTimeout);
                hello = await reader.ReadAsync(hs.Token);
            }
            if (hello?.Str("t") != "hello") return;

            switch (hello.Str("kind"))
            {
                case "probe": await ProbeAsync(reader, writer, ct); break;
                case "join": await JoinAsync(client, ip, nonce, hello, reader, writer, ct); break;
                case "tunnel": await TunnelAsync(client, ip, hello, reader, writer, ct); break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException) { }
        catch (Exception ex) { Log.Warn("LAN connection error: " + ex.Message); }
        finally
        {
            Interlocked.Decrement(ref _connections);
            if (_perIp.AddOrUpdate(ip, 0, (_, n) => n - 1) <= 0) _perIp.TryRemove(new KeyValuePair<IPAddress, int>(ip, 0));
        }
    }

    private JsonObject Info() => LanProtocol.Msg("info", ("name", _o.LobbyName), ("pw", HasPassword),
        ("players", _players.Count + 1), ("max", _o.MaxPlayers), ("world", World?.Motd), ("v", LanProtocol.Version));

    /// <summary>Connection test: lobby info and echoing pings, no lobby membership.</summary>
    private async Task ProbeAsync(LineReader reader, LineWriter writer, CancellationToken ct)
    {
        await writer.SendAsync(Info(), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        for (var i = 0; i < 200; i++)
        {
            var m = await reader.ReadAsync(cts.Token);
            if (m == null) return;
            if (m.Str("t") == "ping") await writer.SendAsync(LanProtocol.Msg("pong", ("ts", m["ts"]?.DeepClone())), cts.Token);
        }
    }

    private async Task JoinAsync(TcpClient client, IPAddress ip, string nonce, JsonObject hello, LineReader reader, LineWriter writer, CancellationToken ct)
    {
        if (HasPassword && !LanProtocol.ProofMatches(_o.Password!, nonce, hello.Str("auth")))
        {
            await writer.SendAsync(LanProtocol.Msg("denied", ("reason", "password")), ct);
            return;
        }
        if (_players.Count + 1 >= _o.MaxPlayers)
        {
            await writer.SendAsync(LanProtocol.Msg("denied", ("reason", "full")), ct);
            return;
        }
        var uuid = LanProtocol.CleanText(hello.Str("uuid"), 36);
        var baseName = LanProtocol.CleanName(hello.Str("name"));
        if (uuid == _o.HostUuid || _players.Values.Any(p => p.Uuid == uuid && uuid.Length > 0))
        {
            await writer.SendAsync(LanProtocol.Msg("denied", ("reason", "duplicate")), ct);
            return;
        }
        var name = baseName;
        for (var n = 2; name == _o.HostName || _players.Values.Any(p => p.Name == name); n++)
            name = baseName[..Math.Min(baseName.Length, 13)] + "_" + n;

        var player = new HostedPlayer
        {
            Id = Interlocked.Increment(ref _nextId), Name = name, Uuid = uuid, Ip = ip,
            Token = LanProtocol.NewToken(), Writer = writer, Client = client,
        };
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, player.Cts.Token);
        _players[player.Id] = player;
        Log.Info($"LAN: {name} joined from {ip}");
        try
        {
            await writer.SendAsync(LanProtocol.Msg("welcome", ("id", player.Id), ("token", player.Token), ("name", name)), link.Token);
            BroadcastState();
            Changed?.Invoke();
            while (!link.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(link.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(30)); // pings run every 3 s; silence means the player is gone
                var m = await reader.ReadAsync(idle.Token);
                if (m == null) break;
                switch (m.Str("t"))
                {
                    case "ping": await writer.SendAsync(LanProtocol.Msg("pong", ("ts", m["ts"]?.DeepClone())), link.Token); break;
                    case "pong":
                        var ts = m.Long("ts");
                        if (ts > 0) player.PingMs = (int)Math.Clamp(Stopwatch.GetElapsedTime(ts).TotalMilliseconds, 0, 99999);
                        break;
                    case "bye": return;
                }
            }
        }
        finally
        {
            if (_players.TryRemove(player.Id, out _))
            {
                Log.Info($"LAN: {name} left");
                BroadcastState();
                Changed?.Invoke();
            }
            player.Cts.Cancel();
        }
    }

    private async Task TunnelAsync(TcpClient client, IPAddress ip, JsonObject hello, LineReader reader, LineWriter writer, CancellationToken ct)
    {
        var token = hello.Str("token");
        var player = _players.Values.FirstOrDefault(p => token != null && p.Token == token);
        // The token is only valid from the address the player joined from.
        if (player == null || !player.Ip.Equals(ip))
        {
            await writer.SendAsync(LanProtocol.Msg("denied", ("reason", "token")), ct);
            return;
        }
        if (World is not { } world)
        {
            await writer.SendAsync(LanProtocol.Msg("denied", ("reason", "noworld")), ct);
            return;
        }
        if (Interlocked.Increment(ref player.Tunnels) > MaxTunnelsPerPlayer)
        {
            Interlocked.Decrement(ref player.Tunnels);
            return;
        }
        try
        {
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, player.Cts.Token);
            using var game = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            using (var cc = CancellationTokenSource.CreateLinkedTokenSource(link.Token))
            {
                cc.CancelAfter(TimeSpan.FromSeconds(5));
                try { await game.ConnectAsync(IPAddress.Loopback, world.Port, cc.Token); }
                catch
                {
                    await writer.SendAsync(LanProtocol.Msg("denied", ("reason", "gameoffline")), ct);
                    return;
                }
            }
            await writer.SendAsync(LanProtocol.Msg("ok"), link.Token);
            await LanProtocol.BridgeAsync(client.GetStream(), game.GetStream(), reader.Remaining.ToArray(), link.Token);
        }
        finally { Interlocked.Decrement(ref player.Tunnels); }
    }

    private async Task PingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch { break; }
            var ts = Stopwatch.GetTimestamp();
            foreach (var p in _players.Values) p.Writer.Post(LanProtocol.Msg("ping", ("ts", ts)));
            // Pongs arrive within the next second or so; publish them with the next state.
            try { await Task.Delay(1000, ct); } catch { break; }
            BroadcastState();
            Changed?.Invoke();
        }
    }

    private JsonObject State()
    {
        var list = new JsonArray { new JsonObject { ["id"] = 0, ["name"] = _o.HostName, ["uuid"] = _o.HostUuid, ["host"] = true } };
        foreach (var p in Players)
            list.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["uuid"] = p.Uuid, ["ping"] = p.PingMs });
        var w = World;
        return LanProtocol.Msg("state", ("name", _o.LobbyName), ("players", list),
            ("world", w == null ? null : new JsonObject { ["motd"] = w.Motd }));
    }

    private void BroadcastState()
    {
        var s = State();
        foreach (var p in _players.Values) p.Writer.Post((JsonObject)s.DeepClone());
    }

    /// <summary>Disconnects a player; with <paramref name="banned"/> they are told they were banned.</summary>
    public void Kick(int id, bool banned)
    {
        if (!_players.TryGetValue(id, out var p)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(2000);
                await p.Writer.SendAsync(LanProtocol.Msg("kicked", ("reason", banned ? "banned" : "kicked")), cts.Token);
            }
            catch { }
            p.Cts.Cancel();
            try { p.Client.Close(); } catch { }
        });
    }

    /// <summary>Disconnects everyone connected from <paramref name="ip"/> (after a ban).</summary>
    public void KickIp(IPAddress ip)
    {
        foreach (var p in _players.Values.Where(p => p.Ip.Equals(ip))) Kick(p.Id, true);
    }

    public void Dispose()
    {
        foreach (var p in _players.Values)
        {
            try { p.Writer.Post(LanProtocol.Msg("closed")); } catch { }
        }
        _ = Task.Delay(300).ContinueWith(_ =>
        {
            _cts.Cancel();
            foreach (var p in _players.Values) { p.Cts.Cancel(); try { p.Client.Close(); } catch { } }
        });
        try { _listener?.Stop(); } catch { }
        _watcher.Dispose();
    }
}
