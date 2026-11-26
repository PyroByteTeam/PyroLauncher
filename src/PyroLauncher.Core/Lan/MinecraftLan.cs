using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyroLauncher.Core;

namespace PyroLauncher.Lan;

/// <summary>A world opened to LAN in Minecraft ("Open to LAN").</summary>
public record LanWorld(string Motd, int Port);

/// <summary>
/// Minecraft's own LAN discovery: a game with a world open to LAN sends "[MOTD]name[/MOTD][AD]port[/AD]" to the
/// multicast group 224.0.2.60:4445 every 1.5 s, and the multiplayer screen lists every sender it hears.
/// </summary>
public static partial class MinecraftLan
{
    public static readonly IPAddress Group = IPAddress.Parse("224.0.2.60");
    public const int DiscoveryPort = 4445;
    /// <summary>Added to worlds the launcher re-announces, so they are never picked up again as a local world.</summary>
    public const string RelayTag = " [PyroLauncher LAN]";

    [GeneratedRegex(@"\[MOTD\](.*?)\[/MOTD\]\[AD\](\d{1,5})\[/AD\]", RegexOptions.Singleline)]
    private static partial Regex PingRegex();

    public static LanWorld? Parse(string text)
    {
        var m = PingRegex().Match(text);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var port) || port is < 1 or > 65535) return null;
        return new LanWorld(m.Groups[1].Value, port);
    }

    public static string Format(string motd, int port) => $"[MOTD]{motd}[/MOTD][AD]{port}[/AD]";

    internal static IEnumerable<IPAddress> MulticastInterfaces()
    {
        IEnumerable<IPAddress> list;
        try
        {
            list = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
        }
        catch { list = []; }
        return list.Append(IPAddress.Loopback).Distinct();
    }

    /// <summary>
    /// Server list ping (status request) against a Minecraft server - here, a LAN world reached through the tunnel.
    /// Returns the status JSON and the round trip of the ping packet.
    /// </summary>
    public static async Task<(JsonObject? Status, TimeSpan Ping)> StatusAsync(Stream s, string host, int port, CancellationToken ct)
    {
        var hs = new MemoryStream();
        WriteVarInt(hs, 0x00);
        WriteVarInt(hs, 767); // protocol number is informational for status requests
        var hb = Encoding.UTF8.GetBytes(host);
        WriteVarInt(hs, hb.Length); hs.Write(hb);
        hs.WriteByte((byte)(port >> 8)); hs.WriteByte((byte)port);
        WriteVarInt(hs, 1); // next state: status
        await WritePacket(s, hs.ToArray(), ct);
        await WritePacket(s, [0x00], ct);

        var resp = await ReadPacket(s, ct);
        using var rs = new MemoryStream(resp);
        if (ReadVarInt(rs) != 0x00) throw new InvalidDataException("Unexpected status reply");
        var len = ReadVarInt(rs);
        var json = Encoding.UTF8.GetString(resp, (int)rs.Position, Math.Min(len, resp.Length - (int)rs.Position));
        JsonObject? status = null;
        try { status = JsonNode.Parse(json) as JsonObject; } catch { }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ping = new byte[9]; ping[0] = 0x01;
        BitConverter.TryWriteBytes(ping.AsSpan(1), DateTime.UtcNow.Ticks);
        await WritePacket(s, ping, ct);
        try { await ReadPacket(s, ct); } catch { }
        return (status, sw.Elapsed);
    }

    private static async Task WritePacket(Stream s, byte[] body, CancellationToken ct)
    {
        var ms = new MemoryStream();
        WriteVarInt(ms, body.Length);
        ms.Write(body);
        await s.WriteAsync(ms.ToArray(), ct);
        await s.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadPacket(Stream s, CancellationToken ct)
    {
        int len = 0, shift = 0;
        while (true)
        {
            var b = new byte[1];
            if (await s.ReadAsync(b, ct) == 0) throw new EndOfStreamException();
            len |= (b[0] & 0x7F) << shift;
            if ((b[0] & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) throw new InvalidDataException("VarInt too long");
        }
        if (len is < 0 or > 1 << 20) throw new InvalidDataException("Packet too large");
        var buf = new byte[len];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    private static void WriteVarInt(Stream s, int v)
    {
        var u = (uint)v;
        do
        {
            var b = (byte)(u & 0x7F);
            u >>= 7;
            if (u != 0) b |= 0x80;
            s.WriteByte(b);
        } while (u != 0);
    }

    private static int ReadVarInt(Stream s)
    {
        int v = 0, shift = 0, b;
        do
        {
            b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            v |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0 && shift < 35);
        return v;
    }
}

/// <summary>Host side: listens for this computer's Minecraft announcing a LAN world.</summary>
public sealed class LanWorldWatcher : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private LanWorld? _world;
    private DateTime _lastSeen;

    /// <summary>Raised (on a background thread) when the detected world appears, changes or disappears.</summary>
    public event Action<LanWorld?>? Changed;
    public LanWorld? World => _world;

    public void Start()
    {
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MinecraftLan.DiscoveryPort));
            var joined = 0;
            foreach (var nic in MinecraftLan.MulticastInterfaces())
            {
                try { udp.JoinMulticastGroup(MinecraftLan.Group, nic); joined++; } catch { }
            }
            if (joined == 0) udp.JoinMulticastGroup(MinecraftLan.Group);
            _udp = udp;
            _ = ReceiveLoop(udp, _cts.Token);
            _ = ExpireLoop(_cts.Token);
        }
        catch (Exception ex) { Log.Warn("LAN world detection unavailable: " + ex.Message); }
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                // Only worlds hosted on this computer: never forward a stranger's game from the real LAN.
                if (!LanProtocol.IsLocalAddress(r.RemoteEndPoint.Address)) continue;
                var text = Encoding.UTF8.GetString(r.Buffer);
                if (text.Contains(MinecraftLan.RelayTag)) continue;
                if (MinecraftLan.Parse(text) is not { } world) continue;
                _lastSeen = DateTime.UtcNow;
                if (world != _world)
                {
                    _world = world;
                    Changed?.Invoke(world);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(200, CancellationToken.None); }
        }
    }

    private async Task ExpireLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch { break; }
            // Minecraft announces every 1.5 s; a few missed pings mean the world was closed.
            if (_world != null && DateTime.UtcNow - _lastSeen > TimeSpan.FromSeconds(7))
            {
                _world = null;
                Changed?.Invoke(null);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
    }
}

/// <summary>
/// Guest side: announces the host's world to this computer's Minecraft so it shows under "LAN worlds".
/// Packets use TTL 0, so they never leave this computer.
/// </summary>
public sealed class LanWorldAnnouncer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private volatile string _payload;

    public LanWorldAnnouncer(string motd, int localPort)
    {
        _payload = MinecraftLan.Format(motd + MinecraftLan.RelayTag, localPort);
        _ = Loop(_cts.Token);
    }

    public void Update(string motd, int localPort) => _payload = MinecraftLan.Format(motd + MinecraftLan.RelayTag, localPort);

    private async Task Loop(CancellationToken ct)
    {
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var nic in MinecraftLan.MulticastInterfaces())
            {
                try
                {
                    var u = new UdpClient(new IPEndPoint(nic, 0));
                    u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, nic.GetAddressBytes());
                    u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
                    u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                    sockets.Add(u);
                }
                catch { }
            }
            var target = new IPEndPoint(MinecraftLan.Group, MinecraftLan.DiscoveryPort);
            while (!ct.IsCancellationRequested)
            {
                var bytes = Encoding.UTF8.GetBytes(_payload);
                foreach (var u in sockets)
                {
                    try { await u.SendAsync(bytes, target, ct); } catch (OperationCanceledException) { throw; } catch { }
                }
                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { foreach (var u in sockets) u.Dispose(); }
    }

    public void Dispose() => _cts.Cancel();
}
