using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyroLauncher.Core;

namespace PyroLauncher.Content;

public enum ShaderLoader { None, Iris, Oculus, OptiFine }

/// <summary>
/// Reads/writes the in-game state of resource packs (options.txt → resourcePacks) and the selected shader pack
/// (Iris / Oculus config, or OptiFine's optionsshaders.txt), so packs chosen in the launcher are active in game.
/// </summary>
public static class GameOptions
{
    private static string OptionsPath(string gameDir) => Path.Combine(gameDir, "options.txt");

    // ------------------------------------------------------------------ resource packs

    /// <summary>Pack file/folder names that are enabled in options.txt (in load order, lowest priority first).</summary>
    public static List<string> ReadResourcePacks(string gameDir)
    {
        var line = ReadOption(OptionsPath(gameDir), "resourcePacks");
        if (line == null) return [];
        try
        {
            return (JsonNode.Parse(line) as JsonArray ?? [])
                .Select(n => n.AsStr())
                .Where(s => s != null && s.StartsWith("file/"))
                .Select(s => s![5..])
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Writes the enabled packs, keeping built-in/mod entries (e.g. "vanilla", "fabric") that the game manages.</summary>
    public static void WriteResourcePacks(string gameDir, IEnumerable<string> packFileNames)
    {
        var path = OptionsPath(gameDir);
        var existing = ReadOption(path, "resourcePacks");
        var builtin = new List<string>();
        try
        {
            if (existing != null)
                builtin = (JsonNode.Parse(existing) as JsonArray ?? [])
                    .Select(n => n.AsStr()).Where(s => s != null && !s.StartsWith("file/")).Cast<string>().ToList();
        }
        catch { }
        if (!builtin.Contains("vanilla")) builtin.Insert(0, "vanilla");

        var all = builtin.Concat(packFileNames.Select(p => "file/" + p)).ToList();
        WriteOption(path, "resourcePacks", JsonSerializer.Serialize(all));

        // Drop packs we no longer use from the "incompatible" acknowledgement list.
        var incompatible = ReadOption(path, "incompatibleResourcePacks");
        if (incompatible != null)
        {
            try
            {
                var kept = (JsonNode.Parse(incompatible) as JsonArray ?? []).Select(n => n.AsStr()).Where(s => s != null && all.Contains(s)).ToList();
                WriteOption(path, "incompatibleResourcePacks", JsonSerializer.Serialize(kept));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ shaders

    public static ShaderLoader DetectShaderLoader(IEnumerable<LocalItem> mods)
    {
        foreach (var m in mods.Where(m => m.IsEnabled))
        {
            var id = (m.ModId ?? "").ToLowerInvariant();
            var file = m.FileName.ToLowerInvariant();
            if (id == "iris" || file.StartsWith("iris")) return ShaderLoader.Iris;
            if (id == "oculus" || file.StartsWith("oculus")) return ShaderLoader.Oculus;
            if (id == "optifine" || file.Contains("optifine")) return ShaderLoader.OptiFine;
        }
        return ShaderLoader.None;
    }

    public static string? ReadShader(string gameDir)
    {
        foreach (var file in new[] { "iris.properties", "oculus.properties" })
        {
            var p = Path.Combine(gameDir, "config", file);
            if (!File.Exists(p)) continue;
            var enabled = ReadOption(p, "enableShaders", '=');
            var pack = ReadOption(p, "shaderPack", '=');
            if (enabled == "false") return null;
            if (!string.IsNullOrEmpty(pack)) return pack;
        }
        var of = ReadOption(Path.Combine(gameDir, "optionsshaders.txt"), "shaderPack", '=');
        return of is null or "OFF" or "(internal)" ? null : of;
    }

    /// <summary>Selects (or clears, with null) the shader pack for every shader loader we know about.</summary>
    public static void WriteShader(string gameDir, string? packName, ShaderLoader loader)
    {
        var targets = new List<string>();
        if (loader is ShaderLoader.Iris or ShaderLoader.None) targets.Add(Path.Combine(gameDir, "config", "iris.properties"));
        if (loader == ShaderLoader.Oculus) targets.Add(Path.Combine(gameDir, "config", "oculus.properties"));
        foreach (var t in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            if (packName != null) WriteOption(t, "shaderPack", packName, '=');
            WriteOption(t, "enableShaders", packName != null ? "true" : "false", '=');
        }
        if (loader == ShaderLoader.OptiFine || File.Exists(Path.Combine(gameDir, "optionsshaders.txt")))
            WriteOption(Path.Combine(gameDir, "optionsshaders.txt"), "shaderPack", packName ?? "OFF", '=');
    }

    // ------------------------------------------------------------------ key/value files

    private static string? ReadOption(string path, string key, char sep = ':')
    {
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path))
        {
            var i = line.IndexOf(sep);
            if (i > 0 && line[..i].Trim() == key) return line[(i + 1)..].Trim();
        }
        return null;
    }

    private static void WriteOption(string path, string key, string value, char sep = ':')
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var idx = lines.FindIndex(l => l.IndexOf(sep) is > 0 and var i && l[..i].Trim() == key);
        var text = $"{key}{sep}{value}";
        if (idx >= 0) lines[idx] = text; else lines.Add(text);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
