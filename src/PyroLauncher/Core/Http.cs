using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace PyroLauncher.Core;

public class HttpError(HttpStatusCode status, string url, string body)
    : Exception($"HTTP {(int)status} from {new Uri(url).Host}: {Trim(body)}")
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;
    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

public static class Http
{
    public static readonly string AppVersion = typeof(Http).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"PyroLauncher/{AppVersion}");
        return c;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        if (req.RequestUri != null) DownloadManager.HostSeen(req.RequestUri.Host);
        var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new HttpError(resp.StatusCode, req.RequestUri!.ToString(), body);
        }
        return resp;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default, Action<HttpRequestMessage>? configure = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(req);
        using var resp = await SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct = default, Action<HttpRequestMessage>? configure = null)
        => JsonNode.Parse(await GetStringAsync(url, ct, configure));

    public static async Task<JsonNode?> PostJsonAsync(string url, JsonNode body, CancellationToken ct = default, Action<HttpRequestMessage>? configure = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        configure?.Invoke(req);
        using var resp = await SendAsync(req, ct);
        var s = await resp.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(s) ? null : JsonNode.Parse(s);
    }

    public static async Task<JsonNode?> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        using var resp = await SendAsync(req, ct);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAsync(req, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Downloads to a temp file and atomically moves it into place.</summary>
    public static async Task DownloadFileAsync(string url, string path, CancellationToken ct = default, IProgress<(long done, long total)>? progress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".part";
        var job = DownloadManager.Current;
        var host = new Uri(url).Host;
        if (job != null) await job.WaitIfPausedAsync(ct);
        DownloadManager.HostStart(host);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await SendAsync(req, ct, TimeSpan.FromMinutes(30));
            var total = resp.Content.Headers.ContentLength ?? -1;
            job?.BeginFile(Path.GetFileName(path), total);
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            var lastReport = Environment.TickCount64;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                job?.AddBytes(read);
                DownloadManager.HostBytes(host, read);
                if (job != null) await job.WaitIfPausedAsync(ct);
                if (progress != null && Environment.TickCount64 - lastReport > 100)
                {
                    lastReport = Environment.TickCount64;
                    progress.Report((done, total));
                }
            }
        }
        finally
        {
            DownloadManager.HostEnd(host);
        }
        File.Move(tmp, path, true);
    }
}
