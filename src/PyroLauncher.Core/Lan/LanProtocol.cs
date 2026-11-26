using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PyroLauncher.Lan;

/// <summary>
/// Wire format of PyroLauncher LAN. Every TCP connection to a lobby starts with newline-separated JSON messages:
/// the host greets with "hi" (lobby name, password flag, a random nonce), the client answers with "hello" naming
/// what the connection is for:
///   probe  - connection test: lobby info and ping/pong, nothing else;
///   join   - control connection of a player: lobby state, pings, kick notices;
///   tunnel - one Minecraft connection; after "ok" the socket carries raw game bytes to the host's LAN world.
/// Peers connect to each other directly (no relay or third-party service in the path), so the only thing that has
/// to be reachable is the host's lobby port.
/// </summary>
public static class LanProtocol
{
    public const int Version = 1;
    public const string App = "PyroLauncher-LAN";
    public const int DefaultPort = 25570;
    public const int MaxLine = 16 * 1024;
    public const string CodePrefix = "PYRO-";

    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Password proof: HMAC-SHA256(key = password, data = nonce). The password itself never crosses the wire.</summary>
    public static string Proof(string password, string nonce) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(nonce)));

    public static bool ProofMatches(string password, string nonce, string? proof) =>
        proof != null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Proof(password, nonce)), Encoding.ASCII.GetBytes(proof));

    public static string NewToken(int bytes = 24) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>Minecraft-style name: letters, digits and underscore, at most 16 characters.</summary>
    public static string CleanName(string? name)
    {
        var s = new string((name ?? "").Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_').Take(16).ToArray());
        return s.Length == 0 ? "Player" : s;
    }

    public static string CleanText(string? text, int max) =>
        new string((text ?? "").Where(c => !char.IsControl(c)).Take(max).ToArray()).Trim();

    // ---------- invite codes ----------

    /// <summary>"PYRO-" + base64url("1|addr1,addr2|port"). Holds addresses only - never the password.</summary>
    public static string MakeCode(IEnumerable<string> addresses, int port)
    {
        var raw = $"{Version}|{string.Join(",", addresses.Distinct())}|{port}";
        return CodePrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Accepts an invite code, or a plain "host", "host:port", "1.2.3.4:port" or "[ipv6]:port".</summary>
    public static (List<string> Hosts, int Port)? ParseCode(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var b64 = text[CodePrefix.Length..].Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
                if (parts.Length < 3 || !int.TryParse(parts[2], out var p) || p is < 1 or > 65535) return null;
                var hosts = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsSaneHost).ToList();
                return hosts.Count == 0 ? null : (hosts, p);
            }
            catch { return null; }
        }

        string host = text;
        int port = DefaultPort;
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end < 0) return null;
            host = text[1..end];
            var rest = text[(end + 1)..];
            if (rest.StartsWith(':') && !int.TryParse(rest[1..], out port)) return null;
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var i = text.IndexOf(':');
            host = text[..i];
            if (!int.TryParse(text[(i + 1)..], out port)) return null;
        }
        if (port is < 1 or > 65535 || !IsSaneHost(host)) return null;
        return ([host], port);
    }

    private static bool IsSaneHost(string h) =>
        h.Length is > 0 and < 254 && (IPAddress.TryParse(h, out _) || h.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'));

    public static string FormatEndpoint(string host, int port) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]:{port}" : $"{host}:{port}";

    // ---------- framing ----------

    public static byte[] Encode(JsonObject msg) => Encoding.UTF8.GetBytes(msg.ToJsonString() + "\n");

    public static JsonObject Msg(string type, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["t"] = type };
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }

    /// <summary>Connects to the first of several endpoints that answers (tried in parallel), within <paramref name="timeout"/>.</summary>
    public static async Task<(TcpClient Client, string Host)> ConnectAnyAsync(IReadOnlyList<string> hosts, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var tasks = hosts.Select(async h =>
        {
            var c = new TcpClient(IPAddress.TryParse(h, out var ip) ? ip.AddressFamily : AddressFamily.InterNetworkV6);
            if (ip == null) c.Client.DualMode = true;
            try
            {
                if (ip != null) await c.ConnectAsync(ip, port, cts.Token);
                else await c.ConnectAsync(h, port, cts.Token);
                c.NoDelay = true;
                return (c, h);
            }
            catch { c.Dispose(); throw; }
        }).ToList();

        Exception? last = null;
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            try
            {
                var result = await done;
                cts.Cancel();
                // Close the losers that may still finish connecting.
                foreach (var t in tasks) _ = t.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result.c.Dispose(); });
                return result;
            }
            catch (Exception ex) { last = ex; }
        }
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        throw last is OperationCanceledException
            ? new TimeoutException("No answer from the host (timed out).")
            : last ?? new IOException("Could not connect.");
    }

    /// <summary>Copies bytes both ways until either side closes.</summary>
    public static async Task BridgeAsync(Stream a, Stream b, ReadOnlyMemory<byte> pendingForB, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (!pendingForB.IsEmpty) await b.WriteAsync(pendingForB, cts.Token);
            var t1 = Pump(a, b, cts.Token);
            var t2 = Pump(b, a, cts.Token);
            await Task.WhenAny(t1, t2);
        }
        catch { }
        finally { cts.Cancel(); }

        static async Task Pump(Stream from, Stream to, CancellationToken ct)
        {
            var buf = new byte[32 * 1024];
            try
            {
                int n;
                while ((n = await from.ReadAsync(buf, ct)) > 0)
                {
                    await to.WriteAsync(buf.AsMemory(0, n), ct);
                    await to.FlushAsync(ct);
                }
            }
            catch { }
        }
    }

    /// <summary>True for addresses that belong to this computer (loopback or one of its interfaces).</summary>
    public static bool IsLocalAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.Equals(ip));
        }
        catch { return false; }
    }

    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

/// <summary>Reads newline-terminated JSON messages with a size limit, keeping any bytes read past the last line.</summary>
public sealed class LineReader(Stream stream)
{
    private readonly byte[] _buf = new byte[LanProtocol.MaxLine * 2];
    private int _start, _end;

    public async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var nl = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
            if (nl >= 0)
            {
                var line = Encoding.UTF8.GetString(_buf, _start, nl - _start);
                _start = nl + 1;
                if (line.Length == 0) continue;
                try { return JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("Bad message"); }
                catch (System.Text.Json.JsonException) { throw new InvalidDataException("Bad message"); }
            }
            if (_end - _start >= LanProtocol.MaxLine) throw new InvalidDataException("Message too long");
            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            var n = await stream.ReadAsync(_buf.AsMemory(_end), ct);
            if (n == 0) return null;
            _end += n;
        }
    }

    /// <summary>Bytes already read from the socket after the last message (raw game data once a tunnel starts).</summary>
    public ReadOnlyMemory<byte> Remaining => _buf.AsMemory(_start, _end - _start);
}

/// <summary>Serialises writes on one connection.</summary>
public sealed class LineWriter(Stream stream)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task SendAsync(JsonObject msg, CancellationToken ct = default)
    {
        var bytes = LanProtocol.Encode(msg);
        await _lock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        finally { _lock.Release(); }
    }

    public async void Post(JsonObject msg)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await SendAsync(msg, cts.Token);
        }
        catch { }
    }
}
