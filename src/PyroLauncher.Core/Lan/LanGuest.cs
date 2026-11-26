using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyroLauncher.Core;
using PyroLauncher.UI;

namespace PyroLauncher.Lan;

/// <summary>An error with a message meant for the user.</summary>
public class LanException(string message) : Exception(message);

/// <summary>Nothing answered on the host's TCP addresses (as opposed to the host answering and refusing).</summary>
public sealed class LanUnreachableException(string message) : LanException(message);

public record LobbyPlayer(int Id, string Name, string Uuid, bool IsHost, int? PingMs);

/// <summary>How a guest reaches the host: plain TCP to one of the invite addresses, or a hole-punched UDP session.</summary>
public abstract class LanRoute : IDisposable
{
    /// <summary>Expected host fingerprint (from the invite code); null when the user typed an address.</summary>
    public string? Fingerprint { get; init; }
    public abstract string Label { get; }
    public abstract bool ViaPunch { get; }
    public abstract Task<(Stream Stream, IDisposable Owner)> OpenAsync(CancellationToken ct);
    public virtual void Dispose() { }
}

public sealed class TcpRoute(IReadOnlyList<string> hosts, int port) : LanRoute
{
    private string? _winner;
    public int Port => port;
    public override string Label => LanProtocol.FormatEndpoint(_winner ?? hosts[0], port);
    public override bool ViaPunch => false;

    public override async Task<(Stream, IDisposable)> OpenAsync(CancellationToken ct)
    {
        var candidates = _winner != null ? [_winner] : hosts;
        try
        {
            var (client, host) = await LanProtocol.ConnectAnyAsync(candidates, port, TimeSpan.FromSeconds(6), ct);
            _winner = host;
            return (client.GetStream(), client);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new LanUnreachableException(L.F("Could not reach the lobby at {0}: {1}",
                string.Join(", ", candidates.Select(h => LanProtocol.FormatEndpoint(h, port))), ex.Message));
        }
    }
}

public sealed class PunchRoute(UdpMux mux, PunchSession session) : LanRoute
{
    public override string Label => session.Remote.ToString();
    public override bool ViaPunch => true;
    public PunchSession Session => session;

    public override Task<(Stream, IDisposable)> OpenAsync(CancellationToken ct)
    {
        try
        {
            var s = session.OpenStream();
            return Task.FromResult<(Stream, IDisposable)>((s, s));
        }
        catch (IOException) { throw new LanException(L.T("Lost connection to the host.")); }
    }

    public override void Dispose()
    {
        session.Close();
        mux.Dispose();
    }
}

/// <summary>Result of a connection test against a lobby.</summary>
public sealed class LanTestResult
{
    public string Address { get; init; } = "";
    public double ConnectMs { get; init; }
    public string LobbyName { get; init; } = "";
    public int Players { get; init; }
    public int MaxPlayers { get; init; }
    public bool PasswordRequired { get; init; }
    public bool Verified { get; init; }
    public bool ViaPunch { get; init; }
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
    /// <summary>Opens an encrypted connection and reads the host's greeting. Throws <see cref="LanException"/> with a readable reason.</summary>
    public static async Task<(IDisposable Owner, SecureStream Stream, LineReader Reader, LineWriter Writer, JsonObject Hi)> OpenAsync(
        LanRoute route, CancellationToken ct)
    {
        var (raw, owner) = await route.OpenAsync(ct);
        try
        {
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(LanProtocol.HandshakeTimeout);
            var stream = await SecureChannel.ClientAsync(raw, route.Fingerprint, hs.Token);
            var reader = new LineReader(stream);
            var writer = new LineWriter(stream);
            var hi = await reader.ReadAsync(hs.Token);
            if (hi?.Str("t") == "denied") throw new LanException(DeniedText(hi.Str("reason")));
            if (hi?.Str("t") != "hi" || hi.Str("app") != LanProtocol.App)
                throw new LanException(L.T("Something answered on that address, but it is not a PyroLauncher lobby."));
            if (hi.Int("v") > LanProtocol.Version)
                throw new LanException(L.T("The host uses a newer version of PyroLauncher. Update the launcher to join."));
            return (owner, stream, reader, writer, hi);
        }
        catch (LanException) { owner.Dispose(); throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            owner.Dispose();
            throw new LanException(L.T("The host did not answer in time."));
        }
        catch (InvalidDataException)
        {
            owner.Dispose();
            throw new LanException(L.T("Something answered on that address, but it is not a PyroLauncher lobby (or the connection was tampered with)."));
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
        {
            owner.Dispose();
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
    private readonly IDisposable _control;
    private readonly LineReader _reader;
    private readonly LineWriter _writer;
    private readonly LanRoute _route;
    private TcpListener? _gameListener;
    private LanWorldAnnouncer? _announcer;
    private string? _closeReason;
    private volatile bool _leaving;

    public string HostAddress => _route.Label;
    public bool ViaPunch => _route.ViaPunch;
    /// <summary>The host proved it owns the key from the invite code.</summary>
    public bool Verified => _route.Fingerprint != null;
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

    private LanGuest(LanRoute route, IDisposable control, LineReader reader, LineWriter writer, JsonObject welcome, string lobbyName)
    {
        _route = route;
        _control = control;
        _reader = reader;
        _writer = writer;
        Token = welcome.Str("token") ?? "";
        MyId = welcome.Int("id");
        MyName = welcome.Str("name") ?? "";
        LobbyName = lobbyName;
    }

    /// <summary>Joins through <paramref name="route"/>; on success the guest owns the route and disposes it when leaving.</summary>
    public static async Task<LanGuest> JoinAsync(LanRoute route, string name, string uuid, string? password, CancellationToken ct)
    {
        var (owner, _, reader, writer, hi) = await LanHandshake.OpenAsync(route, ct);
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

            var guest = new LanGuest(route, owner, reader, writer, reply, LanProtocol.CleanText(hi.Str("name"), 40));
            _ = guest.ReadLoop();
            _ = guest.PingLoop();
            return guest;
        }
        catch (LanException) { owner.Dispose(); throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            owner.Dispose();
            throw new LanException(L.T("The host did not answer in time."));
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            owner.Dispose();
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
            var (owner, stream, pending) = await OpenTunnelAsync(ct);
            using (owner) await LanProtocol.BridgeAsync(game.GetStream(), stream, pending, ct);
        }
        catch (Exception ex) { Log.Info("LAN tunnel failed: " + ex.Message); }
    }

    /// <summary>An encrypted connection to the host's world. Bytes in Pending already arrived from the host.</summary>
    private async Task<(IDisposable Owner, Stream Stream, ReadOnlyMemory<byte> Pending)> OpenTunnelAsync(CancellationToken ct)
    {
        var (owner, stream, reader, writer, _) = await LanHandshake.OpenAsync(_route, ct);
        try
        {
            await writer.SendAsync(LanProtocol.Msg("hello", ("kind", "tunnel"), ("token", Token)), ct);
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(LanProtocol.HandshakeTimeout);
            var reply = await reader.ReadAsync(hs.Token);
            if (reply?.Str("t") != "ok") throw new LanException(LanHandshake.DeniedText(reply?.Str("reason")));
            return (owner, stream, reader.Remaining.ToArray());
        }
        catch { owner.Dispose(); throw; }
    }

    /// <summary>Asks the host's world for its status through a tunnel, like the multiplayer screen does.</summary>
    public async Task<(JsonObject? Status, TimeSpan Ping)> TestWorldAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var (owner, stream, _) = await OpenTunnelAsync(cts.Token);
        using (owner) return await MinecraftLan.StatusAsync(stream, "localhost", 25565, cts.Token);
    }

    /// <summary>Ping test over the route this guest is connected through.</summary>
    public Task<LanTestResult> ProbeAsync(IProgress<int>? progress, CancellationToken ct) => ProbeAsync(_route, 10, progress, ct);

    public static async Task<LanTestResult> ProbeAsync(LanRoute route, int count, IProgress<int>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (owner, _, reader, writer, _) = await LanHandshake.OpenAsync(route, ct);
        var connectMs = sw.Elapsed.TotalMilliseconds;
        using var o = owner;
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
                Address = route.Label, ConnectMs = connectMs, Verified = route.Fingerprint != null, ViaPunch = route.ViaPunch,
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
        try { _control.Dispose(); } catch { }
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        _route.Dispose();
    }

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
