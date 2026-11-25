using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PyroLauncher.Core;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to read {path}", ex);
            return default;
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }

    public static JsonNode? ParseFile(string path) => JsonNode.Parse(File.ReadAllText(path));

    public static void WriteNode(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static JsonNode? Get(this JsonNode? n, string key) =>
        n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static string? Str(this JsonNode? n, string key)
    {
        var v = n.Get(key);
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            return jv.ToJsonString();
        }
        return null;
    }

    public static long Long(this JsonNode? n, string key, long def = 0)
    {
        if (n.Get(key) is not JsonValue jv) return def;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<double>(out var d)) return (long)d;
        if (jv.TryGetValue<string>(out var s) && long.TryParse(s, out l)) return l;
        return def;
    }

    public static int Int(this JsonNode? n, string key, int def = 0) => (int)n.Long(key, def);

    public static bool Bool(this JsonNode? n, string key, bool def = false)
    {
        if (n.Get(key) is not JsonValue jv) return def;
        if (jv.TryGetValue<bool>(out var b)) return b;
        if (jv.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
        return def;
    }

    public static JsonArray Arr(this JsonNode? n, string key) => n.Get(key) as JsonArray ?? new JsonArray();

    public static JsonObject? Obj(this JsonNode? n, string key) => n.Get(key) as JsonObject;

    public static IEnumerable<string> Strings(this JsonNode? n, string key) =>
        n.Arr(key).Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => s != null)!;

    public static string? AsStr(this JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
