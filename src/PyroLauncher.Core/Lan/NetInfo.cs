using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using PyroLauncher.Core;

namespace PyroLauncher.Lan;

/// <summary>Addresses of this computer, for invite codes.</summary>
public static class NetInfo
{
    /// <summary>Private IPv4 addresses (home network) of active adapters, skipping virtual/VPN-only ones where possible.</summary>
    public static List<IPAddress> LocalIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
                .Distinct().ToList();
        }
        catch { return []; }
    }

    /// <summary>Global (internet-routable) IPv6 addresses. IPv6 usually needs no port forwarding, only an open firewall.</summary>
    public static List<IPAddress> GlobalIPv6()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6
                            && !a.Address.IsIPv6LinkLocal && !a.Address.IsIPv6SiteLocal && !a.Address.IsIPv6UniqueLocal
                            && !a.Address.IsIPv6Teredo && (a.Address.GetAddressBytes()[0] & 0xE0) == 0x20 // 2000::/3
                            && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
                // Stable addresses first; temporary (privacy) ones change during the day.
                .OrderBy(a => OperatingSystem.IsWindows() && a.SuffixOrigin == SuffixOrigin.Random ? 1 : 0)
                .Select(a => a.Address)
                .Distinct().Take(2).ToList();
        }
        catch { return []; }
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Carrier-grade NAT (100.64.0.0/10) or a private address on the router's WAN side: no one can reach you over IPv4.</summary>
    public static bool IsCgnat(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork && ((b[0] == 100 && (b[1] & 0xC0) == 64) || IsPrivate(ip));
    }

    /// <summary>
    /// Public IPv4 as seen from the internet. Several independent services are asked at once and any answer wins,
    /// so one being blocked or down does not matter. Only used to build the invite code; the game traffic never goes there.
    /// </summary>
    public static async Task<IPAddress?> PublicIPv4Async(CancellationToken ct)
    {
        string[] urls =
        [
            "https://ipv4-internet.yandex.net/api/v0/ip",
            "https://api.ipify.org",
            "https://ifconfig.me/ip",
        ];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        var tasks = urls.Select(async u =>
        {
            var s = await Http.GetStringAsync(u, cts.Token);
            return IPAddress.TryParse(s.Trim().Trim('"'), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
        }).ToList();
        while (tasks.Count > 0)
        {
            var t = await Task.WhenAny(tasks);
            tasks.Remove(t);
            if (t.IsCompletedSuccessfully && t.Result != null) { cts.Cancel(); return t.Result; }
        }
        return null;
    }
}

/// <summary>
/// Minimal UPnP IGD client: asks the home router to forward the lobby port to this computer, so friends can reach it
/// without manual port forwarding. Talks only to the router on the local network.
/// </summary>
public sealed class UpnpMapping
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
    { Timeout = TimeSpan.FromSeconds(6) };

    public required string ControlUrl { get; init; }
    public required string ServiceType { get; init; }
    public required IPAddress LocalAddress { get; init; }
    public int Port { get; private set; }
    public IPAddress? ExternalAddress { get; private set; }

    /// <summary>Finds the router and maps TCP <paramref name="port"/> to this computer. Returns null when no router answers or it refuses.</summary>
    public static async Task<UpnpMapping?> TryMapAsync(int port, CancellationToken ct)
    {
        foreach (var gateway in await DiscoverAsync(ct))
        {
            try
            {
                await gateway.AddAsync(port, ct);
                try { gateway.ExternalAddress = await gateway.GetExternalAsync(ct); } catch { }
                return gateway;
            }
            catch (Exception ex) { Log.Info($"UPnP mapping via {gateway.ControlUrl} failed: {ex.Message}"); }
        }
        return null;
    }

    private static async Task<List<UpnpMapping>> DiscoverAsync(CancellationToken ct)
    {
        var locations = new HashSet<string>();
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            foreach (var st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2", "urn:schemas-upnp-org:service:WANIPConnection:1" })
            {
                var req = $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n";
                try { await udp.SendAsync(Encoding.ASCII.GetBytes(req), target, ct); } catch { }
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2.5));
            try
            {
                while (true)
                {
                    var r = await udp.ReceiveAsync(cts.Token);
                    foreach (var line in Encoding.ASCII.GetString(r.Buffer).Split("\r\n"))
                        if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                        {
                            var loc = line[9..].Trim();
                            // Only accept descriptions served by the device that answered (it must be on our network).
                            if (Uri.TryCreate(loc, UriKind.Absolute, out var u) && u.Scheme == "http" && IPAddress.TryParse(u.Host, out var h) && h.Equals(r.RemoteEndPoint.Address))
                                locations.Add(loc);
                        }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        var result = new List<UpnpMapping>();
        foreach (var loc in locations)
        {
            try
            {
                var xml = XDocument.Parse(await Client.GetStringAsync(loc, ct));
                XNamespace ns = xml.Root!.Name.Namespace;
                var baseUrl = xml.Root.Element(ns + "URLBase")?.Value is { Length: > 0 } b ? new Uri(b) : new Uri(loc);
                foreach (var svc in xml.Descendants(ns + "service"))
                {
                    var type = svc.Element(ns + "serviceType")?.Value ?? "";
                    if (!type.Contains("WANIPConnection") && !type.Contains("WANPPPConnection")) continue;
                    var control = svc.Element(ns + "controlURL")?.Value;
                    if (string.IsNullOrEmpty(control)) continue;
                    var uri = new Uri(baseUrl, control);
                    result.Add(new UpnpMapping { ControlUrl = uri.ToString(), ServiceType = type, LocalAddress = LocalAddressTowards(IPAddress.Parse(uri.Host)) });
                }
            }
            catch (Exception ex) { Log.Info($"UPnP description {loc} unreadable: {ex.Message}"); }
        }
        return result.OrderBy(m => m.ServiceType.Contains("WANIP") ? 0 : 1).ToList();
    }

    private static IPAddress LocalAddressTowards(IPAddress router)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(router, 1900);
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }

    private async Task AddAsync(int port, CancellationToken ct)
    {
        Port = port;
        string Args(int lease) =>
            $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{LocalAddress}</NewInternalClient><NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>PyroLauncher LAN</NewPortMappingDescription><NewLeaseDuration>{lease}</NewLeaseDuration>";
        try { await SoapAsync("AddPortMapping", Args(7200), ct); }
        catch (HttpRequestException) { await SoapAsync("AddPortMapping", Args(0), ct); } // some routers only allow permanent leases
    }

    /// <summary>Renews the lease (mappings expire after two hours so a crashed launcher does not leave a hole open forever).</summary>
    public Task RenewAsync(CancellationToken ct) => AddAsync(Port, ct);

    public async Task RemoveAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await SoapAsync("DeletePortMapping",
                $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{Port}</NewExternalPort><NewProtocol>TCP</NewProtocol>", cts.Token);
        }
        catch (Exception ex) { Log.Info("UPnP unmapping failed: " + ex.Message); }
    }

    private async Task<IPAddress?> GetExternalAsync(CancellationToken ct)
    {
        var xml = await SoapAsync("GetExternalIPAddress", "", ct);
        var v = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
        return IPAddress.TryParse(v, out var ip) ? ip : null;
    }

    private async Task<XDocument> SoapAsync(string action, string args, CancellationToken ct)
    {
        var body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                   $"<s:Body><u:{action} xmlns:u=\"{ServiceType}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, ControlUrl) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{ServiceType}#{action}\"");
        using var resp = await Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{action}: HTTP {(int)resp.StatusCode}");
        return XDocument.Parse(text);
    }
}
