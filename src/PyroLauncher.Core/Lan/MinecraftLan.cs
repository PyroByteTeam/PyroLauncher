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
        _ = ScanLoop(_cts.Token);
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
                Set(world, scanned: false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(200, CancellationToken.None); }
        }
    }

    private readonly Lock _gate = new();
    /// <summary>The current world was found by the port scan, not heard from Minecraft's broadcast.</summary>
    private bool _scanned;

    private void Set(LanWorld? world, bool scanned)
    {
        bool changed;
        lock (_gate)
        {
            if (!scanned) _lastSeen = DateTime.UtcNow;
            changed = world != _world;
            _world = world;
            _scanned = scanned && world != null;
        }
        if (changed) Changed?.Invoke(world);
    }

    private async Task ExpireLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch { break; }
            // Minecraft announces every 1.5 s; a few missed pings mean the world was closed.
            bool expired;
            lock (_gate) expired = _world != null && !_scanned && DateTime.UtcNow - _lastSeen > TimeSpan.FromSeconds(7);
            if (expired) Set(null, scanned: false);
        }
    }

    /// <summary>
    /// Fallback for when the broadcast never arrives (firewall, VPN adapters): looks for a port that a running
    /// Minecraft opened and asks it for its status, like the server list does. Rechecks a found world so it goes
    /// away when the world is closed.
    /// </summary>
    private async Task ScanLoop(CancellationToken ct)
    {
        try { await Task.Delay(4000, ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            bool scan;
            lock (_gate) scan = _world == null || _scanned;
            if (scan)
            {
                LanWorld? found = null;
                try { found = await LocalWorldScan.FindAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Info("LAN world scan failed: " + ex.Message); }
                bool apply;
                lock (_gate) apply = _world == null || _scanned; // the broadcast wins if it arrived meanwhile
                if (apply) Set(found, scanned: true);
            }
            try { await Task.Delay(5000, ct); } catch { return; }
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

/// <summary>
/// Finds a world opened to LAN without Minecraft's broadcast: takes the TCP ports that running Minecraft processes
/// listen on and asks each one for its status. Windows only (it needs the port → process table).
/// </summary>
internal static class LocalWorldScan
{
    public static async Task<LanWorld?> FindAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var pids = GamePids();
        if (pids.Count == 0) return null;
        var ports = ListeningPorts().Where(p => pids.Contains(p.Pid) && p.Port >= 1024).Select(p => p.Port).Distinct().ToList();
        foreach (var port in ports)
        {
            ct.ThrowIfCancellationRequested();
            if (await ProbeAsync(port, ct) is { } world) return world;
        }
        return null;
    }

    /// <summary>Game clients started by the launcher, plus javaw.exe processes (games from other launchers).</summary>
    private static HashSet<int> GamePids()
    {
        var pids = new HashSet<int>();
        foreach (var inst in Instances.InstanceManager.Instances.ToList())
        {
            try { if (!inst.IsServer && inst.Process is { HasExited: false } p) pids.Add(p.Id); } catch { }
        }
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("javaw"))
        {
            pids.Add(p.Id);
            p.Dispose();
        }
        return pids;
    }

    private static async Task<LanWorld?> ProbeAsync(int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var (status, _) = await MinecraftLan.StatusAsync(tcp.GetStream(), "127.0.0.1", port, cts.Token);
            if (status?.Obj("version") == null) return null;
            var d = status["description"];
            var motd = d is JsonObject o ? o.Str("text") : d?.GetValueKind() == System.Text.Json.JsonValueKind.String ? d.GetValue<string>() : null;
            return new LanWorld(string.IsNullOrWhiteSpace(motd) ? "Minecraft" : motd, port);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    private const int ListenerTable = 3; // TCP_TABLE_OWNER_PID_LISTENER

    /// <summary>Listening TCP ports with their owning process, IPv4 and IPv6.</summary>
    private static List<(int Port, int Pid)> ListeningPorts()
    {
        var list = new List<(int, int)>();
        Read(2, 24, 8, 20, list);   // AF_INET:  MIB_TCPROW_OWNER_PID  {state, localAddr, localPort, remoteAddr, remotePort, pid}
        Read(23, 56, 20, 52, list); // AF_INET6: MIB_TCP6ROW_OWNER_PID {localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid}
        return list;
    }

    private static void Read(int family, int rowSize, int portOffset, int pidOffset, List<(int, int)> list)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, ListenerTable, 0);
        if (size <= 0) return;
        var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, family, ListenerTable, 0) != 0) return;
            var count = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
            for (var i = 0; i < count; i++)
            {
                var row = buf + 4 + i * rowSize;
                var raw = System.Runtime.InteropServices.Marshal.ReadInt32(row + portOffset);
                var port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); // network byte order in the low 16 bits
                var pid = System.Runtime.InteropServices.Marshal.ReadInt32(row + pidOffset);
                list.Add((port, pid));
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
    }
}
