using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using PyroLauncher.Core;

namespace PyroLauncher.Content;

/// <summary>
/// Disk + memory cache of local content metadata and icons, keyed by file name, size and modification time.
/// Icons are stored once by content hash in cache/icons and decoded images are kept in memory.
/// </summary>
public static class MetaCache
{
    public class Entry
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string? ModId { get; set; }
        public string? Description { get; set; }
        public string? IconHash { get; set; }
    }

    private static readonly string FilePath = Path.Combine(AppPaths.Cache, "content-meta.json");
    private static readonly string IconDir = Path.Combine(AppPaths.Cache, "icons");
    private static ConcurrentDictionary<string, Entry>? _entries;
    private static readonly ConcurrentDictionary<string, ImageSource?> Icons = new();
    private static int _dirty;
    private static readonly object SaveLock = new();

    private static ConcurrentDictionary<string, Entry> Entries
    {
        get
        {
            if (_entries != null) return _entries;
            var loaded = JsonUtil.Load<Dictionary<string, Entry>>(FilePath) ?? [];
            return _entries = new ConcurrentDictionary<string, Entry>(loaded);
        }
    }

    private static string Key(LocalItem item, string baseName) => $"{item.Type}|{baseName}|{item.Size}|{item.Modified.Ticks}";

    public static bool TryApply(LocalItem item, string baseName)
    {
        if (!Entries.TryGetValue(Key(item, baseName), out var e)) return false;
        item.Name = e.Name;
        item.Version = e.Version;
        item.ModId = e.ModId;
        item.Description = e.Description;
        if (e.IconHash != null) item.Icon = GetIcon(e.IconHash);
        return true;
    }

    public static void Store(LocalItem item, string baseName)
    {
        string? hash = null;
        if (item.IconBytes is { Length: > 0 } bytes)
        {
            hash = Convert.ToHexStringLower(SHA1.HashData(bytes));
            var path = Path.Combine(IconDir, hash + ".png");
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(IconDir);
                    File.WriteAllBytes(path, bytes);
                }
            }
            catch { }
            item.Icon = Icons.GetOrAdd(hash, _ => LocalContentScanner.LoadImage(bytes));
            item.IconBytes = null;
        }
        Entries[Key(item, baseName)] = new Entry
        {
            Name = item.Name, Version = item.Version, ModId = item.ModId, Description = item.Description, IconHash = hash,
        };
        Interlocked.Exchange(ref _dirty, 1);
    }

    private static ImageSource? GetIcon(string hash) => Icons.GetOrAdd(hash, h =>
    {
        var path = Path.Combine(IconDir, h + ".png");
        try { return File.Exists(path) ? LocalContentScanner.LoadImage(File.ReadAllBytes(path)) : null; }
        catch { return null; }
    });

    /// <summary>Persists new entries (called after scans).</summary>
    public static void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        lock (SaveLock)
        {
            try { JsonUtil.Save(FilePath, new Dictionary<string, Entry>(Entries)); }
            catch (Exception ex) { Log.Warn($"Meta cache save failed: {ex.Message}"); }
        }
    }
}
