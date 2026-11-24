using PyroLauncher.UI;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PyroLauncher.Core;
using PyroLauncher.Instances;

namespace PyroLauncher.Content;

public partial class LocalItem : ObservableObject
{
    public const string DisabledSuffix = ".disabled";

    public ContentType Type { get; init; }
    public bool IsDirectory { get; init; }
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string? _description;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private string? _iconUrl;
    [ObservableProperty] private ContentRef? _ref;
    [ObservableProperty] private ProjectVersion? _update;
    public bool HasUpdate => Update != null;
    partial void OnUpdateChanged(ProjectVersion? value) => OnPropertyChanged(nameof(HasUpdate));
    public string? ModId { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    /// <summary>Raw icon bytes captured while reading metadata (only until the scanner caches them).</summary>
    internal byte[]? IconBytes { get; set; }

    [ObservableProperty] private bool _isSelected;
    /// <summary>Ticked for bulk actions (multi-select).</summary>
    [ObservableProperty] private bool _isChecked;

    /// <summary>First two letters of the name, shown when there is no icon.</summary>
    public string Initials
    {
        get
        {
            var letters = new string(Name.Where(char.IsLetterOrDigit).Take(2).ToArray());
            return letters.Length == 0 ? "?" : char.ToUpperInvariant(letters[0]) + (letters.Length > 1 ? letters[1..].ToLowerInvariant() : "");
        }
    }
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Initials));

    // ---- Resource packs / shaders: "enabled" means active in game (options.txt / shader config), not renamed.
    private bool _active;
    /// <summary>Called when a resource pack or shader is switched on/off; returns whether the change was applied.</summary>
    public Func<LocalItem, bool, bool>? ActivationHandler { get; set; }
    internal void SetActiveSilently(bool value)
    {
        if (_active == value) return;
        _active = value;
        OnPropertyChanged(nameof(IsEnabled));
    }

    public string FileName => Path.GetFileName(FilePath);
    /// <summary>File name without the ".disabled" suffix; stable key for the content index.</summary>
    public string BaseName => FileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? FileName[..^DisabledSuffix.Length] : FileName;
    public string IndexKey => $"{Type.Folder()}/{BaseName}";
    public string SourceText => Ref == null ? "Local" : Ref.Source == ContentSource.Modrinth ? "Modrinth" : "CurseForge";
    public bool HasIcon => Icon != null || IconUrl != null;

    public bool IsEnabled
    {
        get => Type == ContentType.Mod ? !FileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) : _active;
        set
        {
            if (value == IsEnabled) return;
            if (Type != ContentType.Mod)
            {
                if (ActivationHandler?.Invoke(this, value) == true) _active = value;
                OnPropertyChanged();
                return;
            }
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                var target = Path.Combine(dir, value ? BaseName : BaseName + DisabledSuffix);
                if (IsDirectory) Directory.Move(FilePath, target);
                else File.Move(FilePath, target);
                FilePath = target;
            }
            catch (Exception ex)
            {
                UI.Dialogs.Toast(L.F("Could not toggle {0}: {1}", Name, ex.Message), true);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileName));
        }
    }

    partial void OnRefChanged(ContentRef? value) => OnPropertyChanged(nameof(SourceText));
    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));
    partial void OnIconUrlChanged(string? value) => OnPropertyChanged(nameof(HasIcon));
}

/// <summary>Per-instance record of where content files came from (content.json in the instance folder).</summary>
public class ContentIndex
{
    public Dictionary<string, ContentRef> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Lock = new();

    public static string PathFor(Instance inst) => Path.Combine(inst.Dir, "content.json");

    public static ContentIndex Load(Instance inst)
    {
        lock (Lock)
        {
            var idx = JsonUtil.Load<ContentIndex>(PathFor(inst)) ?? new ContentIndex();
            idx.Files = new Dictionary<string, ContentRef>(idx.Files ?? [], StringComparer.OrdinalIgnoreCase);
            return idx;
        }
    }

    public void Save(Instance inst)
    {
        lock (Lock) JsonUtil.Save(PathFor(inst), this);
    }

    public static void Update(Instance inst, Action<ContentIndex> change)
    {
        lock (Lock)
        {
            var idx = Load(inst);
            change(idx);
            idx.Save(inst);
        }
    }
}

public static class LocalContentScanner
{
    public static List<LocalItem> Scan(Instance inst, ContentType type)
    {
        var folder = Path.Combine(inst.GameDir, type.Folder());
        Directory.CreateDirectory(folder);
        var index = ContentIndex.Load(inst);
        var items = new List<LocalItem>();

        foreach (var file in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(file);
            var baseName = name.EndsWith(LocalItem.DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^LocalItem.DisabledSuffix.Length] : name;
            var ext = Path.GetExtension(baseName).ToLowerInvariant();
            if (type == ContentType.Mod && ext != ".jar") continue;
            if (type != ContentType.Mod && ext != ".zip") continue;

            var fi = new FileInfo(file);
            var item = new LocalItem { Type = type, FilePath = file, Name = Path.GetFileNameWithoutExtension(baseName), Size = fi.Length, Modified = fi.LastWriteTimeUtc };
            // Metadata + icons are cached on disk by (name, size, mtime) so large mod folders open instantly.
            if (!MetaCache.TryApply(item, baseName))
            {
                try
                {
                    if (type == ContentType.Mod) ReadModMetadata(item);
                    else if (type == ContentType.ResourcePack) ReadPackMetadataZip(item);
                }
                catch (Exception ex) { Log.Warn($"Metadata read failed for {name}: {ex.Message}"); }
                MetaCache.Store(item, baseName);
            }
            ApplyRef(item, index);
            items.Add(item);
        }

        if (type != ContentType.Mod)
        {
            foreach (var dir in Directory.GetDirectories(folder))
            {
                var item = new LocalItem { Type = type, IsDirectory = true, FilePath = dir, Name = Path.GetFileName(dir) };
                if (type == ContentType.ResourcePack)
                {
                    var mcmeta = Path.Combine(dir, "pack.mcmeta");
                    if (!File.Exists(mcmeta)) continue;
                    try
                    {
                        item.Description = PackDescription(File.ReadAllText(mcmeta));
                        var png = Path.Combine(dir, "pack.png");
                        if (File.Exists(png)) item.Icon = LoadImage(File.ReadAllBytes(png));
                    }
                    catch { }
                }
                ApplyRef(item, index);
                items.Add(item);
            }
        }
        MetaCache.Flush();
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ApplyRef(LocalItem item, ContentIndex index)
    {
        if (!index.Files.TryGetValue(item.IndexKey, out var r)) return;
        item.Ref = r;
        if (item.Icon == null && r.IconUrl != null) item.IconUrl = r.IconUrl;
        if (string.IsNullOrEmpty(item.Version) && r.VersionNumber != null) item.Version = r.VersionNumber;
        if (item.Type != ContentType.Mod && r.Title != null) item.Name = r.Title;
    }

    // ------------------------------------------------------------ mod metadata

    public static void ReadModMetadata(LocalItem item)
    {
        using var zip = ZipFile.OpenRead(item.FilePath);
        string? iconPath = null;

        if (zip.GetEntry("fabric.mod.json") is { } fabric)
        {
            var j = JsonNode.Parse(ReadText(fabric), documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            item.ModId = j.Str("id");
            item.Name = j.Str("name") ?? item.ModId ?? item.Name;
            item.Version = j.Str("version") ?? "";
            item.Description = j.Str("description");
            var icon = j.Get("icon");
            iconPath = icon.AsStr() ?? (icon as JsonObject)?.LastOrDefault().Value.AsStr();
        }
        else if (zip.GetEntry("quilt.mod.json") is { } quilt)
        {
            var j = JsonNode.Parse(ReadText(quilt)).Obj("quilt_loader");
            item.ModId = j.Str("id");
            item.Version = j.Str("version") ?? "";
            var meta = j.Obj("metadata");
            item.Name = meta.Str("name") ?? item.ModId ?? item.Name;
            item.Description = meta.Str("description");
            iconPath = meta.Str("icon");
        }
        else if ((zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml")) is { } toml)
        {
            var t = ParseModsToml(ReadText(toml));
            item.ModId = t.GetValueOrDefault("modId");
            item.Name = t.GetValueOrDefault("displayName") ?? item.ModId ?? item.Name;
            item.Version = t.GetValueOrDefault("version") ?? "";
            item.Description = t.GetValueOrDefault("description")?.Trim();
            iconPath = t.GetValueOrDefault("logoFile");
            if (item.Version.Contains("${file.jarVersion}") && zip.GetEntry("META-INF/MANIFEST.MF") is { } mf)
            {
                var m = Regex.Match(ReadText(mf), @"Implementation-Version:\s*(\S+)");
                item.Version = m.Success ? m.Groups[1].Value : "";
            }
        }
        else if (zip.GetEntry("mcmod.info") is { } info)
        {
            var j = JsonNode.Parse(ReadText(info), documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true });
            var first = (j as JsonArray)?.FirstOrDefault() ?? j.Arr("modList").FirstOrDefault();
            item.ModId = first.Str("modid");
            item.Name = first.Str("name") ?? item.Name;
            item.Version = first.Str("version") ?? "";
            item.Description = first.Str("description");
            iconPath = first.Str("logoFile");
        }

        if (item.Version.Contains("${")) item.Version = "";
        if (!string.IsNullOrEmpty(iconPath) && zip.GetEntry(iconPath.TrimStart('/')) is { } iconEntry)
        {
            using var s = iconEntry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            item.IconBytes = ms.ToArray();
        }
    }

    private static void ReadPackMetadataZip(LocalItem item)
    {
        using var zip = ZipFile.OpenRead(item.FilePath);
        if (zip.GetEntry("pack.mcmeta") is { } meta)
            item.Description = PackDescription(ReadText(meta));
        if (zip.GetEntry("pack.png") is { } png)
        {
            using var s = png.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            item.IconBytes = ms.ToArray();
        }
    }

    private static string? PackDescription(string json)
    {
        try
        {
            var d = JsonNode.Parse(json).Obj("pack")?.Get("description");
            return StripFormatting(ChatToText(d));
        }
        catch { return null; }
    }

    public static string ChatToText(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v => v.AsStr() ?? v.ToJsonString(),
        JsonArray a => string.Concat(a.Select(ChatToText)),
        JsonObject o => (o.Str("text") ?? o.Str("translate") ?? "") + string.Concat(o.Arr("extra").Select(ChatToText)),
        _ => "",
    };

    public static string StripFormatting(string s) => Regex.Replace(s, "§.", "");

    private static string ReadText(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>Very small TOML reader: returns top-level keys plus keys of the first [[mods]] table.</summary>
    public static Dictionary<string, string> ParseModsToml(string text)
    {
        var result = new Dictionary<string, string>();
        var lines = text.Replace("\r", "").Split('\n');
        string section = "";
        bool firstModsDone = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                if (section == "mods") firstModsDone = true;
                section = line.StartsWith("[[mods]]") ? "mods" : line;
                continue;
            }
            if (section != "" && (section != "mods" || firstModsDone)) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().Trim('"');
            var val = line[(eq + 1)..].Trim();
            string parsed;
            if (val.StartsWith("'''") || val.StartsWith("\"\"\""))
            {
                var q = val[..3];
                var rest = val[3..];
                var sb = new StringBuilder();
                while (true)
                {
                    var end = rest.IndexOf(q, StringComparison.Ordinal);
                    if (end >= 0) { sb.Append(rest[..end]); break; }
                    sb.AppendLine(rest);
                    if (++i >= lines.Length) break;
                    rest = lines[i];
                }
                parsed = sb.ToString();
            }
            else if (val.StartsWith('"') || val.StartsWith('\''))
            {
                var q = val[0];
                var end = val.IndexOf(q, 1);
                parsed = end > 0 ? val[1..end] : val.Trim(q);
                if (q == '"') parsed = parsed.Replace("\\n", "\n").Replace("\\\"", "\"");
            }
            else
            {
                var hash = val.IndexOf('#');
                parsed = (hash >= 0 ? val[..hash] : val).Trim();
            }
            result.TryAdd(key, parsed);
        }
        return result;
    }

    public static BitmapImage? LoadImage(byte[] bytes, int decodeWidth = 64)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
