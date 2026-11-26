using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyroLauncher.Core;
using PyroLauncher.UI;

namespace PyroLauncher.Lan;

/// <summary>An error with a message meant for the user.</summary>
public sealed class LanException(string message) : Exception(message);

public record LobbyPlayer(int Id, string Name, string Uuid, bool IsHost, int? PingMs);

/// <summary>Result of a connection test against a lobby.</summary>
public sealed class LanTestResult
{
    public string Address { get; init; } = "";
    public int Port { get; init; }
    public double ConnectMs { get; init; }
    public string LobbyName { get; init; } = "";
    public int Players { get; init; }
    public int MaxPlayers { get; init; }
    public bool PasswordRequired { get; init; }
    public string? World { get; init; }
    public List<double> Pings { get; init; } = [];
    public int Sent { get; init; }
    public double Min => Pings.Count > 0 ? Pings.Min() : 0;
    public double Max => Pings.Count > 0 ? Pings.Max() : 0;
    public double Avg => Pings.Count > 0 ? Pings.Average() : 0;
    /// <summary>Mean difference between consecutive pings.</summary>
    public double Jitter => Pings.Count > 1 ? Pings.Zip(Pings.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
    public int LossPercent => Sent == 0 ? 0 : (int)Math.Round(100.0 * (Sent - Pings.Count) / Sent);
}

internal static class LanHandshake
{
    /// <summary>Opens a connection and reads the host's greeting. Throws <see cref="LanException"/> with a readable reason.</summary>
    public static async Task<(TcpClient Client, string Host, LineReader Reader, LineWriter Writer, JsonObject Hi)> OpenAsync(
        IReadOnlyList<string> hosts, int port, CancellationToken ct)
    {
        TcpClient client;
        string host;
        try { (client, host) = await LanProtocol.ConnectAnyAsync(hosts, port, TimeSpan.FromSeconds(6), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new LanException(L.F("Could not reach the lobby at {0}: {1}", string.Join(", ", hosts.Select(h => LanProtocol.FormatEndpoint(h, port))), ex.Message));
        }
        try
        {
            var stream = client.GetStream();
            var reader = new LineReader(stream);
            var writer = new LineWriter(stream);
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(LanProtocol.HandshakeTimeout);
            var hi = await reader.ReadAsync(hs.Token);
            if (hi?.Str("t") == "denied") throw new LanException(DeniedText(hi.Str("reason")));
            if (hi?.Str("t") != "hi" || hi.Str("app") != LanProtocol.App)
                throw new LanException(L.T("Something answered on that address, but it is not a PyroLauncher lobby."));
            if (hi.Int("v") > LanProtocol.Version)
                throw new LanException(L.T("The host uses a newer version of PyroLauncher. Update the launcher to join."));
            return (client, host, reader, writer, hi);
        }
        catch (LanException) { client.Dispose(); throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new LanException(L.T("The host did not answer in time."));
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            client.Dispose();
            throw new LanException(L.F("The connection broke during the handshake: {0}", ex.Message));
        }
    }

    public static string DeniedText(string? reason) => reason switch
    {
        "banned" => L.T("You are banned from this lobby."),
        "password" => L.T("Wrong lobby password."),
        "full" => L.T("The lobby is full."),
        "duplicate" => L.T("This Minecraft account is already in the lobby."),
        "noworld" => L.T("The host has not opened a world to LAN yet."),
        "gameoffline" => L.T("The host's world is not answering. They may have closed it."),
        "token" => L.T("The host did not recognise this connection. Rejoin the lobby."),
        _ => L.T("The host refused the connection."),
    };
}

/// <summary>
/// A player's side of a lobby: the control connection to the host, plus a local port that Minecraft connects to.
/// Each Minecraft connection on that port becomes a tunnel connection to the host's world.
/// </summary>
public sealed class LanGuest : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpClient _control;
    private readonly LineReader _reader;
    private readonly LineWriter _writer;
    private readonly List<string> _hosts;
    private TcpListener? _gameListener;
    private LanWorldAnnouncer? _announcer;
    private string? _closeReason;

    public string HostAddress { get; }
    public int Port { get; }
    public string Token { get; }
    public int MyId { get; }
    public string MyName { get; }
    public string LobbyName { get; private set; }
    public IReadOnlyList<LobbyPlayer> Players { get; private set; } = [];
    public string? WorldMotd { get; private set; }
    public int? LocalGamePort { get; private set; }
    public int? PingMs { get; private set; }

    /// <summary>Raised on a background thread when the lobby state changes.</summary>
    public event Action? Changed;
    /// <summary>Raised once when the connection ends, with a message for the user (null when the user left).</summary>
    public event Action<string?>? Closed;

    private LanGuest(TcpClient control, string host, int port, LineReader reader, LineWriter writer, JsonObject welcome, string lobbyName)
    {
        _control = control;
        _reader = reader;
        _writer = writer;
        HostAddress = host;
        _hosts = [host];
        Port = port;
        Token = welcome.Str("token") ?? "";
        MyId = welcome.Int("id");
        MyName = welcome.Str("name") ?? "";
        LobbyName = lobbyName;
    }

    public static async Task<LanGuest> JoinAsync(IReadOnlyList<string> hosts, int port, string name, string uuid, string? password, CancellationToken ct)
    {
        var (client, host, reader, writer, hi) = await LanHandshake.OpenAsync(hosts, port, ct);
        try
        {
            var pwRequired = hi.Bool("pw");
            if (pwRequired && string.IsNullOrEmpty(password))
                throw new LanException(L.T("This lobby has a password. Enter it and try again."));
            await writer.SendAsync(LanProtocol.Msg("hello", ("kind", "join"), ("name", name), ("uuid", uuid),
                ("auth", pwRequired ? LanProtocol.Proof(password!, hi.Str("nonce") ?? "") : null)), ct);

            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(LanProtocol.HandshakeTimeout);
            var reply = await reader.ReadAsync(hs.Token);
            if (reply?.Str("t") == "denied") throw new LanException(LanHandshake.DeniedText(reply.Str("reason")));
            if (reply?.Str("t") != "welcome") throw new LanException(L.T("The host refused the connection."));

            var guest = new LanGuest(client, host, port, reader, writer, reply, LanProtocol.CleanText(hi.Str("name"), 40));
            _ = guest.ReadLoop();
            _ = guest.PingLoop();
            return guest;
        }
        catch (LanException) { client.Dispose(); throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new LanException(L.T("The host did not answer in time."));
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            client.Dispose();
            throw new LanException(L.F("The connection broke during the handshake: {0}", ex.Message));
        }
    }

    private async Task ReadLoop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                var m = await _reader.ReadAsync(idle.Token);
                if (m == null) { _closeReason ??= L.T("The host closed the connection."); break; }
                switch (m.Str("t"))
                {
                    case "ping": _writer.Post(LanProtocol.Msg("pong", ("ts", m["ts"]?.DeepClone()))); break;
                    case "pong":
                        var ts = m.Long("ts");
                        if (ts > 0) PingMs = (int)Math.Clamp(Stopwatch.GetElapsedTime(ts).TotalMilliseconds, 0, 99999);
                        break;
                    case "state": ApplyState(m); break;
                    case "kicked":
                        _closeReason = m.Str("reason") == "banned" ? L.T("The host banned you from the lobby.") : L.T("The host removed you from the lobby.");
                        return;
                    case "closed":
                        _closeReason = L.T("The host closed the lobby.");
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { _closeReason ??= L.T("Lost connection to the host (no answer for 30 seconds)."); }
        catch (Exception) when (!_cts.IsCancellationRequested) { _closeReason ??= L.T("Lost connection to the host."); }
        catch { }
        finally
        {
            var userLeft = _leaving;
            Shutdown();
            Closed?.Invoke(userLeft ? null : _closeReason ?? L.T("Lost connection to the host."));
        }
    }

    private async Task PingLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            _writer.Post(LanProtocol.Msg("ping", ("ts", Stopwatch.GetTimestamp())));
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    private void ApplyState(JsonObject m)
    {
        LobbyName = LanProtocol.CleanText(m.Str("name"), 40);
        Players = m.Arr("players").OfType<JsonObject>().Take(64).Select(p => new LobbyPlayer(
            p.Int("id"), LanProtocol.CleanName(p.Str("name")), LanProtocol.CleanText(p.Str("uuid"), 36), p.Bool("host"),
            p.Get("ping") is JsonValue ? p.Int("ping") : null)).ToList();
        var world = m.Obj("world");
        var motd = world == null ? null : LanProtocol.CleanText(world.Str("motd"), 80);
        if (motd != null && motd.Length == 0) motd = LobbyName;
        WorldMotd = motd;
        UpdateGamePort();
        Changed?.Invoke();
    }

    /// <summary>Opens the local game port and announces the world to Minecraft while the host has a world open.</summary>
    private void UpdateGamePort()
    {
        if (_cts.IsCancellationRequested) return;
        if (WorldMotd == null)
        {
            _announcer?.Dispose();
            _announcer = null;
            return; // keep the port open so a world reopened by the host gets the same address
        }
        if (_gameListener == null)
        {
            // All interfaces, because Minecraft connects to the address the LAN announcement came from;
            // connections from other computers are refused in GameAcceptLoop.
            var l = new TcpListener(IPAddress.Any, 0);
            l.Start(16);
            _gameListener = l;
            LocalGamePort = ((IPEndPoint)l.LocalEndpoint).Port;
            _ = GameAcceptLoop(l, _cts.Token);
        }
        var motd = $"{WorldMotd} - {LobbyName}";
        if (_announcer == null) _announcer = new LanWorldAnnouncer(motd, LocalGamePort!.Value);
        else _announcer.Update(motd, LocalGamePort!.Value);
    }

    private async Task GameAcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient game;
            try { game = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            var from = ((IPEndPoint)game.Client.RemoteEndPoint!).Address;
            if (!LanProtocol.IsLocalAddress(from)) { game.Dispose(); continue; }
            _ = ForwardAsync(game, ct);
        }
    }

    private async Task ForwardAsync(TcpClient game, CancellationToken ct)
    {
        using var g = game;
        game.NoDelay = true;
        try
        {
            using var tunnel = await OpenTunnelAsync(ct);
            await LanProtocol.BridgeAsync(game.GetStream(), tunnel.Stream, tunnel.Pending, ct);
        }
        catch (Exception ex) { Log.Info("LAN tunnel failed: " + ex.Message); }
    }

    internal sealed class Tunnel(TcpClient client, ReadOnlyMemory<byte> pending) : IDisposable
    {
        public Stream Stream => client.GetStream();
        public ReadOnlyMemory<byte> Pending { get; } = pending;
        public void Dispose() => client.Dispose();
    }

    /// <summary>A raw connection to the host's world through the lobby port.</summary>
    internal async Task<Tunnel> OpenTunnelAsync(CancellationToken ct)
    {
        var (client, _, reader, writer, _) = await LanHandshake.OpenAsync(_hosts, Port, ct);
        try
        {
            await writer.SendAsync(LanProtocol.Msg("hello", ("kind", "tunnel"), ("token", Token)), ct);
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(LanProtocol.HandshakeTimeout);
            var reply = await reader.ReadAsync(hs.Token);
            if (reply?.Str("t") != "ok") throw new LanException(LanHandshake.DeniedText(reply?.Str("reason")));
            // Anything after "ok" is game data from the host; normally nothing, as the Minecraft client speaks first.
            return new Tunnel(client, reader.Remaining.ToArray());
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Asks the host's world for its status through a tunnel, like the multiplayer screen does.</summary>
    public async Task<(JsonObject? Status, TimeSpan Ping)> TestWorldAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var tunnel = await OpenTunnelAsync(cts.Token);
        return await MinecraftLan.StatusAsync(tunnel.Stream, "localhost", 25565, cts.Token);
    }

    public static async Task<LanTestResult> ProbeAsync(IReadOnlyList<string> hosts, int port, int count, IProgress<int>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (client, host, reader, writer, _) = await LanHandshake.OpenAsync(hosts, port, ct);
        var connectMs = sw.Elapsed.TotalMilliseconds;
        using var c = client;
        try
        {
            await writer.SendAsync(LanProtocol.Msg("hello", ("kind", "probe")), ct);
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(LanProtocol.HandshakeTimeout);
            var info = await reader.ReadAsync(hs.Token);
            if (info?.Str("t") != "info") throw new LanException(L.T("The host refused the connection."));

            var pings = new List<double>();
            var sent = 0;
            for (var i = 0; i < count; i++)
            {
                sent++;
                var ts = Stopwatch.GetTimestamp();
                await writer.SendAsync(LanProtocol.Msg("ping", ("ts", ts)), ct);
                using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);
                one.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    while (true)
                    {
                        var m = await reader.ReadAsync(one.Token);
                        if (m == null) throw new LanException(L.T("The host closed the connection."));
                        if (m.Str("t") == "pong" && m.Long("ts") == ts) { pings.Add(Stopwatch.GetElapsedTime(ts).TotalMilliseconds); break; }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* lost */ }
                progress?.Report(i + 1);
                await Task.Delay(250, ct);
            }
            return new LanTestResult
            {
                Address = host, Port = port, ConnectMs = connectMs,
                LobbyName = LanProtocol.CleanText(info.Str("name"), 40), Players = info.Int("players"), MaxPlayers = info.Int("max"),
                PasswordRequired = info.Bool("pw"), World = info.Str("world") is { } w ? LanProtocol.CleanText(w, 80) : null,
                Pings = pings, Sent = sent,
            };
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            throw new LanException(L.F("The connection broke during the test: {0}", ex.Message));
        }
    }

    private void Shutdown()
    {
        _announcer?.Dispose();
        _announcer = null;
        try { _gameListener?.Stop(); } catch { }
        try { _control.Close(); } catch { }
        if (!_cts.IsCancellationRequested) _cts.Cancel();
    }

    private volatile bool _leaving;

    /// <summary>Leaves the lobby (tells the host, then closes).</summary>
    public void Dispose()
    {
        if (_leaving || _cts.IsCancellationRequested) return;
        _leaving = true;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(1000);
                await _writer.SendAsync(LanProtocol.Msg("bye"), cts.Token);
            }
            catch { }
            Shutdown();
        });
    }
}
