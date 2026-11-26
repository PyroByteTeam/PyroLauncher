using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PyroLauncher.Lan;

/// <summary>
/// STUN binding requests (RFC 5389): ask a public server which address and port our UDP socket appears as on the
/// internet. Only used to learn our own address for hole punching; no game data goes to these servers.
/// </summary>
public static class Stun
{
    public const uint MagicCookie = 0x2112A442;

    /// <summary>
    /// Independent public STUN servers from different operators and countries; any two answering is enough.
    /// Cloudflare's is deliberately not used.
    /// </summary>
    public static readonly (string Host, int Port)[] Servers =
    [
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun.sipnet.ru", 3478),
        ("stun.nextcloud.com", 443),
        ("stun.voipbuster.com", 3478),
    ];

    public static byte[] Request(out byte[] transactionId)
    {
        var msg = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(msg, 0x0001); // Binding Request
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), MagicCookie);
        transactionId = RandomNumberGenerator.GetBytes(12);
        transactionId.CopyTo(msg, 8);
        return msg;
    }

    public static bool IsStun(ReadOnlySpan<byte> data) =>
        data.Length >= 20 && (data[0] & 0xC0) == 0 && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == MagicCookie;

    /// <summary>Mapped address from a Binding Success Response, or null.</summary>
    public static IPEndPoint? ParseResponse(ReadOnlySpan<byte> data, out byte[] transactionId)
    {
        transactionId = data.Length >= 20 ? data[8..20].ToArray() : [];
        if (!IsStun(data) || BinaryPrimitives.ReadUInt16BigEndian(data) != 0x0101) return null;
        var len = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var attrs = data.Slice(20, Math.Min(len, data.Length - 20));
        IPEndPoint? mapped = null;
        while (attrs.Length >= 4)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(attrs);
            var alen = BinaryPrimitives.ReadUInt16BigEndian(attrs[2..]);
            if (attrs.Length < 4 + alen) break;
            var v = attrs.Slice(4, alen);
            if ((type == 0x0020 || type == 0x8020) && v.Length >= 8 && v[1] == 0x01) // XOR-MAPPED-ADDRESS, IPv4
            {
                var port = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(v[2..]) ^ (MagicCookie >> 16));
                var ip = BinaryPrimitives.ReadUInt32BigEndian(v[4..]) ^ MagicCookie;
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, ip);
                return new IPEndPoint(new IPAddress(bytes), port);
            }
            if (type == 0x0001 && v.Length >= 8 && v[1] == 0x01) // MAPPED-ADDRESS (old servers)
                mapped = new IPEndPoint(new IPAddress(v.Slice(4, 4)), BinaryPrimitives.ReadUInt16BigEndian(v[2..]));
            attrs = attrs[(4 + ((alen + 3) & ~3))..];
        }
        return mapped;
    }

    public static async Task<List<IPEndPoint>> ResolveServersAsync(CancellationToken ct)
    {
        var tasks = Servers.Select(async s =>
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(s.Host, AddressFamily.InterNetwork, ct);
                return addrs.Length > 0 ? new IPEndPoint(addrs[0], s.Port) : null;
            }
            catch { return null; }
        });
        return (await Task.WhenAll(tasks)).OfType<IPEndPoint>().ToList();
    }
}
