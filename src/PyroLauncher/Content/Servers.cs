using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using PyroLauncher.Core;

namespace PyroLauncher.Content;

// ------------------------------------------------------------------ minimal NBT

public class NbtCompound : Dictionary<string, object> { }

public class NbtList : List<object>
{
    public byte ElementType { get; set; }
}

public static class Nbt
{
    public static NbtCompound Read(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.UTF8, true);
        var type = r.ReadByte();
        if (type != 10) throw new InvalidDataException("Root tag is not a compound");
        ReadString(r);
        return (NbtCompound)ReadPayload(r, 10);
    }

    public static void Write(Stream s, NbtCompound root)
    {
        using var w = new BinaryWriter(s, Encoding.UTF8, true);
        w.Write((byte)10);
        WriteString(w, "");
        WritePayload(w, root);
    }

    private static short RS(BinaryReader r) => BinaryPrimitives.ReadInt16BigEndian(r.ReadBytes(2));
    private static int RI(BinaryReader r) => BinaryPrimitives.ReadInt32BigEndian(r.ReadBytes(4));
    private static long RL(BinaryReader r) => BinaryPrimitives.ReadInt64BigEndian(r.ReadBytes(8));

    private static string ReadString(BinaryReader r)
    {
        var len = (ushort)RS(r);
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    private static object ReadPayload(BinaryReader r, byte type)
    {
        switch (type)
        {
            case 1: return r.ReadSByte();
            case 2: return RS(r);
            case 3: return RI(r);
            case 4: return RL(r);
            case 5: return BitConverter.Int32BitsToSingle(RI(r));
            case 6: return BitConverter.Int64BitsToDouble(RL(r));
            case 7: return r.ReadBytes(RI(r));
            case 8: return ReadString(r);
            case 9:
            {
                var et = r.ReadByte();
                var len = RI(r);
                var list = new NbtList { ElementType = et };
                for (int i = 0; i < len; i++) list.Add(ReadPayload(r, et));
                return list;
            }
            case 10:
            {
                var c = new NbtCompound();
                while (true)
                {
                    var t = r.ReadByte();
                    if (t == 0) break;
                    var name = ReadString(r);
                    c[name] = ReadPayload(r, t);
                }
                return c;
            }
            case 11:
            {
                var len = RI(r);
                var arr = new int[len];
                for (int i = 0; i < len; i++) arr[i] = RI(r);
                return arr;
            }
            case 12:
            {
                var len = RI(r);
                var arr = new long[len];
                for (int i = 0; i < len; i++) arr[i] = RL(r);
                return arr;
            }
            default: throw new InvalidDataException($"Unknown NBT tag {type}");
        }
    }

    private static byte TypeOf(object o) => o switch
    {
        sbyte or byte or bool => 1,
        short => 2,
        int => 3,
        long => 4,
        float => 5,
        double => 6,
        byte[] => 7,
        string => 8,
        NbtList => 9,
        NbtCompound => 10,
        int[] => 11,
        long[] => 12,
        _ => throw new InvalidDataException($"Unsupported NBT value {o.GetType()}"),
    };

    private static void WS(BinaryWriter w, short v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); w.Write(b); }
    private static void WI(BinaryWriter w, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); w.Write(b); }
    private static void WL(BinaryWriter w, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); w.Write(b); }

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WS(w, (short)bytes.Length);
        w.Write(bytes);
    }

    private static void WritePayload(BinaryWriter w, object o)
    {
        switch (o)
        {
            case sbyte sb: w.Write(sb); break;
            case byte b: w.Write(b); break;
            case bool bo: w.Write((byte)(bo ? 1 : 0)); break;
            case short s: WS(w, s); break;
            case int i: WI(w, i); break;
            case long l: WL(w, l); break;
            case float f: WI(w, BitConverter.SingleToInt32Bits(f)); break;
            case double d: WL(w, BitConverter.DoubleToInt64Bits(d)); break;
            case byte[] ba: WI(w, ba.Length); w.Write(ba); break;
            case string str: WriteString(w, str); break;
            case NbtList list:
                var et = list.Count > 0 ? TypeOf(list[0]) : list.ElementType;
                w.Write(et);
                WI(w, list.Count);
                foreach (var x in list) WritePayload(w, x);
                break;
            case NbtCompound c:
                foreach (var kv in c)
                {
                    w.Write(TypeOf(kv.Value));
                    WriteString(w, kv.Key);
                    WritePayload(w, kv.Value);
                }
                w.Write((byte)0);
                break;
            case int[] ia: WI(w, ia.Length); foreach (var x in ia) WI(w, x); break;
            case long[] la: WI(w, la.Length); foreach (var x in la) WL(w, x); break;
        }
    }
}

// ------------------------------------------------------------------ servers.dat

public class ServerEntry
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string? IconBase64 { get; set; }
    public NbtCompound Raw { get; set; } = new();
}

public static class ServersDat
{
    public static string PathFor(string gameDir) => Path.Combine(gameDir, "servers.dat");

    public static List<ServerEntry> Load(string gameDir)
    {
        var path = PathFor(gameDir);
        if (!File.Exists(path)) return [];
        using var fs = File.OpenRead(path);
        var root = Nbt.Read(fs);
        if (!root.TryGetValue("servers", out var listObj) || listObj is not NbtList list) return [];
        return list.OfType<NbtCompound>().Select(c => new ServerEntry
        {
            Name = c.TryGetValue("name", out var n) ? n as string ?? "" : "",
            Address = c.TryGetValue("ip", out var ip) ? ip as string ?? "" : "",
            IconBase64 = c.TryGetValue("icon", out var ic) ? ic as string : null,
            Raw = c,
        }).ToList();
    }

    public static void Save(string gameDir, IEnumerable<ServerEntry> servers)
    {
        var list = new NbtList { ElementType = 10 };
        foreach (var s in servers)
        {
            var c = s.Raw;
            c["name"] = s.Name;
            c["ip"] = s.Address;
            if (s.IconBase64 != null) c["icon"] = s.IconBase64; else c.Remove("icon");
            list.Add(c);
        }
        var root = new NbtCompound { ["servers"] = list };
        var path = PathFor(gameDir);
        Directory.CreateDirectory(gameDir);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) Nbt.Write(fs, root);
        File.Move(tmp, path, true);
    }
}

// ------------------------------------------------------------------ server list ping

public record ServerStatus(bool Online, int Players, int MaxPlayers, long PingMs, string Motd, string Version, string? FaviconBase64);

public static class ServerPinger
{
    public static async Task<ServerStatus> PingAsync(string address, CancellationToken ct)
    {
        var (host, port) = ParseAddress(address);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, cts.Token);
            var ping = sw.ElapsedMilliseconds;
            var stream = client.GetStream();

            // Handshake (protocol -1 / 767 doesn't matter for status), next state 1
            var hs = new MemoryStream();
            WriteVarInt(hs, 0x00);
            WriteVarInt(hs, 767);
            var hostBytes = Encoding.UTF8.GetBytes(host);
            WriteVarInt(hs, hostBytes.Length);
            hs.Write(hostBytes);
            hs.WriteByte((byte)(port >> 8));
            hs.WriteByte((byte)(port & 0xff));
            WriteVarInt(hs, 1);
            await SendPacketAsync(stream, hs.ToArray(), cts.Token);
            await SendPacketAsync(stream, [0x00], cts.Token);

            var len = await ReadVarIntAsync(stream, cts.Token);
            var data = new byte[len];
            await stream.ReadExactlyAsync(data, cts.Token);
            var ms = new MemoryStream(data);
            var id = ReadVarInt(ms);
            if (id != 0) throw new InvalidDataException("Unexpected packet");
            var strLen = ReadVarInt(ms);
            var json = Encoding.UTF8.GetString(data, (int)ms.Position, strLen);
            var j = JsonNode.Parse(json);
            return new ServerStatus(true,
                j.Obj("players").Int("online"), j.Obj("players").Int("max"), ping,
                LocalContentScanner.StripFormatting(LocalContentScanner.ChatToText(j.Get("description"))).Trim(),
                j.Obj("version").Str("name") ?? "",
                j.Str("favicon"));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new ServerStatus(false, 0, 0, -1, "", "", null);
        }
    }

    private static (string host, int port) ParseAddress(string address)
    {
        address = address.Trim();
        var idx = address.LastIndexOf(':');
        if (idx > 0 && !address.EndsWith(']') && int.TryParse(address[(idx + 1)..], out var p))
            return (address[..idx], p);
        return (address, 25565);
    }

    private static async Task SendPacketAsync(NetworkStream s, byte[] payload, CancellationToken ct)
    {
        var ms = new MemoryStream();
        WriteVarInt(ms, payload.Length);
        ms.Write(payload);
        await s.WriteAsync(ms.ToArray(), ct);
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        while (true)
        {
            if ((v & ~0x7Fu) == 0) { s.WriteByte((byte)v); return; }
            s.WriteByte((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
    }

    private static int ReadVarInt(Stream s)
    {
        int value = 0, pos = 0;
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            value |= (b & 0x7F) << pos;
            if ((b & 0x80) == 0) return value;
            pos += 7;
            if (pos >= 32) throw new InvalidDataException("VarInt too big");
        }
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream s, CancellationToken ct)
    {
        int value = 0, pos = 0;
        var buf = new byte[1];
        while (true)
        {
            await s.ReadExactlyAsync(buf, ct);
            value |= (buf[0] & 0x7F) << pos;
            if ((buf[0] & 0x80) == 0) return value;
            pos += 7;
            if (pos >= 32) throw new InvalidDataException("VarInt too big");
        }
    }
}
