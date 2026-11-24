using System.IO;

namespace PyroLauncher.Core;

public record DownloadItem(string Url, string Path, string? Sha1 = null, long Size = -1, string[]? Fallbacks = null);

public readonly record struct TaskProgress(string Message, double Fraction = -1);

public static class Downloader
{
    public static int Concurrency { get; set; } = 12;

    public static bool NeedsDownload(DownloadItem item)
    {
        var fi = new FileInfo(item.Path);
        if (!fi.Exists) return true;
        if (item.Size >= 0) return fi.Length != item.Size;
        if (item.Sha1 != null)
        {
            try { return !Hashing.Sha1File(item.Path).Equals(item.Sha1, StringComparison.OrdinalIgnoreCase); }
            catch { return true; }
        }
        return false;
    }

    public static async Task DownloadAllAsync(IEnumerable<DownloadItem> items, IProgress<TaskProgress>? progress, string label, CancellationToken ct)
    {
        var list = items
            .Where(i => !string.IsNullOrEmpty(i.Url))
            .DistinctBy(i => i.Path.ToLowerInvariant())
            .ToList();
        progress?.Report(new TaskProgress($"Checking {label}…"));
        var needed = await Task.Run(() => list.Where(NeedsDownload).ToList(), ct);
        if (needed.Count == 0) return;

        int done = 0;
        var job = DownloadManager.Current;
        job?.AddFiles(needed.Count);
        progress?.Report(new TaskProgress($"Downloading {label} (0/{needed.Count})", 0));
        using var sem = new SemaphoreSlim(Math.Max(1, Concurrency));
        var errors = new List<Exception>();
        var tasks = needed.Select(async item =>
        {
            await sem.WaitAsync(ct);
            try { await DownloadCoreAsync(item, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
            finally
            {
                sem.Release();
                job?.FileDone();
                var d = Interlocked.Increment(ref done);
                progress?.Report(new TaskProgress($"Downloading {label} ({d}/{needed.Count})", (double)d / needed.Count));
            }
        });
        await Task.WhenAll(tasks);
        if (errors.Count > 0)
            throw new IOException($"{errors.Count} file(s) failed to download. First error: {errors[0].Message}", errors[0]);
    }

    /// <summary>Downloads a single file, counted as one file in the current download job.</summary>
    public static async Task DownloadOneAsync(DownloadItem item, CancellationToken ct)
    {
        var job = DownloadManager.Current;
        job?.AddFiles(1);
        try { await DownloadCoreAsync(item, ct); }
        finally { job?.FileDone(); }
    }

    private static async Task DownloadCoreAsync(DownloadItem item, CancellationToken ct)
    {
        var urls = new List<string> { item.Url };
        if (item.Fallbacks != null) urls.AddRange(item.Fallbacks);
        Exception? last = null;
        foreach (var url in urls)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await Http.DownloadFileAsync(url, item.Path, ct);
                    if (item.Sha1 != null && !Hashing.Sha1File(item.Path).Equals(item.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(item.Path);
                        throw new IOException($"Checksum mismatch for {System.IO.Path.GetFileName(item.Path)}");
                    }
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (HttpError e) when (e.Status == System.Net.HttpStatusCode.NotFound)
                {
                    last = e;
                    break; // try next url
                }
                catch (Exception e)
                {
                    last = e;
                    await Task.Delay(400 * attempt, ct);
                }
            }
        }
        throw new IOException($"Failed to download {item.Url}: {last?.Message}", last);
    }
}
