using PyroLauncher.UI;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyroLauncher.Core;

namespace PyroLauncher.Minecraft;

/// <summary>A version JSON with its whole inheritsFrom chain merged.</summary>
public class ResolvedVersion
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public string MainClass { get; set; } = "";
    public List<JsonObject> Libraries { get; } = [];
    public List<JsonNode> GameArgs { get; } = [];
    public List<JsonNode> JvmArgs { get; } = [];
    public string? LegacyArgs { get; set; }
    public JsonObject? AssetIndex { get; set; }
    public string? Assets { get; set; }
    public JsonObject? ClientDownload { get; set; }
    /// <summary>Id of the version whose jar is the Minecraft client jar.</summary>
    public string JarId { get; set; } = "";
    public int JavaMajor { get; set; } = 8;
    public JsonObject? LoggingClient { get; set; }
}

public static class VersionResolver
{
    public static ResolvedVersion Resolve(string id)
    {
        // Build chain child -> parent
        var chain = new List<JsonNode>();
        var cur = id;
        var seen = new HashSet<string>();
        while (cur != null)
        {
            if (!seen.Add(cur)) throw new InvalidOperationException(L.F("Circular inheritsFrom at {0}", cur));
            var path = MojangMeta.VersionJsonPath(cur);
            if (!File.Exists(path)) throw new FileNotFoundException($"Version JSON missing: {cur}");
            var node = JsonUtil.ParseFile(path)!;
            chain.Add(node);
            cur = node.Str("inheritsFrom");
        }

        var r = new ResolvedVersion { Id = id };
        var libKeys = new HashSet<string>();
        bool javaSet = false;
        // Apply from child to root so child values win for "first set" semantics.
        foreach (var node in chain)
        {
            var nodeId = node.Str("id") ?? id;
            if (string.IsNullOrEmpty(r.MainClass)) r.MainClass = node.Str("mainClass") ?? "";
            r.LegacyArgs ??= node.Str("minecraftArguments");
            r.AssetIndex ??= node.Obj("assetIndex")?.DeepClone() as JsonObject;
            r.Assets ??= node.Str("assets");
            r.LoggingClient ??= node.Obj("logging")?.Obj("client")?.DeepClone() as JsonObject;
            if (r.ClientDownload == null && node.Obj("downloads")?.Obj("client") is { } client)
            {
                r.ClientDownload = (JsonObject)client.DeepClone();
                r.JarId = nodeId;
            }
            if (!javaSet && node.Obj("javaVersion") is { } jv)
            {
                r.JavaMajor = jv.Int("majorVersion", 8);
                javaSet = true;
            }
            r.Type = node == chain[^1] ? node.Str("type") ?? r.Type : r.Type;

            foreach (var lib in node.Arr("libraries").OfType<JsonObject>())
            {
                var name = lib.Str("name");
                if (name == null) continue;
                var key = LibKey(name) + (lib.Obj("natives") != null ? ":natives" : "");
                if (libKeys.Add(key)) r.Libraries.Add((JsonObject)lib.DeepClone());
            }
        }

        // Arguments: parent first, then children (root -> child order)
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var args = chain[i].Obj("arguments");
            if (args == null) continue;
            foreach (var a in args.Arr("game")) if (a != null) r.GameArgs.Add(a.DeepClone());
            foreach (var a in args.Arr("jvm")) if (a != null) r.JvmArgs.Add(a.DeepClone());
        }
        if (string.IsNullOrEmpty(r.JarId)) r.JarId = chain[^1].Str("id") ?? id;
        return r;
    }

    // ---------------- Rules ----------------

    /// <summary>Mojang's name for this OS in version rules: windows, osx or linux.</summary>
    public static string OsName => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string ArchName => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    public static bool RulesAllow(JsonNode? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is not JsonArray arr || arr.Count == 0) return true;
        bool allow = false;
        foreach (var rule in arr)
        {
            if (rule == null) continue;
            bool matches = true;
            if (rule.Obj("os") is { } os)
            {
                var name = os.Str("name");
                if (name != null && name != OsName) matches = false;
                var arch = os.Str("arch");
                if (arch == "x86" && Environment.Is64BitOperatingSystem) matches = false;
                if (arch is "arm64" or "aarch64" && ArchName != "arm64") matches = false;
                var ver = os.Str("version");
                if (ver != null)
                {
                    try { if (!Regex.IsMatch(Environment.OSVersion.Version.ToString(), ver)) matches = false; }
                    catch { /* ignore bad regex */ }
                }
            }
            if (rule.Obj("features") is { } feats)
            {
                foreach (var kv in feats)
                {
                    var want = kv.Value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                    var have = features != null && features.TryGetValue(kv.Key, out var h) && h;
                    if (want != have) matches = false;
                }
            }
            if (matches) allow = rule.Str("action") == "allow";
        }
        return allow;
    }

    // ---------------- Maven helpers ----------------

    public static string MavenPath(string name)
    {
        var ext = "jar";
        var at = name.IndexOf('@');
        if (at >= 0) { ext = name[(at + 1)..]; name = name[..at]; }
        var p = name.Split(':');
        if (p.Length < 3) throw new FormatException($"Bad maven coordinate: {name}");
        var group = p[0].Replace('.', '/');
        var classifier = p.Length > 3 ? "-" + p[3] : "";
        return $"{group}/{p[1]}/{p[2]}/{p[1]}-{p[2]}{classifier}.{ext}";
    }

    public static string LibKey(string name)
    {
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var p = name.Split(':');
        return p.Length switch
        {
            >= 4 => $"{p[0]}:{p[1]}:{p[3]}",
            >= 2 => $"{p[0]}:{p[1]}",
            _ => name,
        };
    }

    public static string? Classifier(string name)
    {
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var p = name.Split(':');
        return p.Length >= 4 ? p[3] : null;
    }
}
