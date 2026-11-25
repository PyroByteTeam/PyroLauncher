using System.IO;
using PyroLauncher.UI;

namespace PyroLauncher.Servers;

public enum PropertyKind { Text, Bool, Number, Choice }

public record PropertyInfo(string Description, PropertyKind Kind = PropertyKind.Text, string[]? Choices = null);

/// <summary>Reads and writes server.properties, keeping comments, order and unknown keys.</summary>
public class ServerProperties
{
    private readonly List<string> _lines = [];

    public static ServerProperties Load(string path)
    {
        var p = new ServerProperties();
        if (File.Exists(path)) p._lines.AddRange(File.ReadAllLines(path));
        return p;
    }

    public IEnumerable<KeyValuePair<string, string>> Entries
    {
        get
        {
            foreach (var l in _lines)
                if (Split(l) is { } kv) yield return kv;
        }
    }

    public string? Get(string key) => Entries.Where(e => e.Key == key).Select(e => (string?)e.Value).FirstOrDefault();

    public void Set(string key, string value)
    {
        for (int i = 0; i < _lines.Count; i++)
            if (Split(_lines[i]) is { } kv && kv.Key == key) { _lines[i] = $"{key}={Escape(value)}"; return; }
        _lines.Add($"{key}={Escape(value)}");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, _lines);
    }

    private static KeyValuePair<string, string>? Split(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || t[0] is '#' or '!') return null;
        var i = t.IndexOfAny(['=', ':']);
        if (i <= 0) return null;
        return new(t[..i].Trim(), Unescape(t[(i + 1)..].TrimStart()));
    }

    private static string Unescape(string v) => v.Replace("\\:", ":").Replace("\\=", "=").Replace("\\\\", "\\");
    private static string Escape(string v) => v.Replace("\\", "\\\\").Replace(":", "\\:").Replace("=", "\\=");

    /// <summary>Parses "key=value" lines (the default overrides from Settings).</summary>
    public static IEnumerable<KeyValuePair<string, string>> ParseOverrides(string? text)
    {
        foreach (var line in (text ?? "").Split('\n'))
            if (Split(line.TrimEnd('\r')) is { } kv) yield return kv;
    }

    /// <summary>
    /// Creates server.properties with the vanilla defaults (what the server would write on its first start)
    /// plus the user's default overrides, so it can be edited before the server ever runs.
    /// </summary>
    public static void CreateDefault(string path, string? overrides, string? motd = null)
    {
        var p = File.Exists(path) ? Load(path) : new ServerProperties();
        if (!File.Exists(path))
        {
            p._lines.Add("#Minecraft server properties");
            foreach (var (k, v) in Defaults) p.Set(k, v);
            if (motd != null) p.Set("motd", motd);
        }
        foreach (var kv in ParseOverrides(overrides)) p.Set(kv.Key, kv.Value);
        p.Save(path);
    }

    public static readonly (string Key, string Value)[] Defaults =
    [
        ("accepts-transfers", "false"), ("allow-flight", "false"), ("allow-nether", "true"), ("broadcast-console-to-ops", "true"),
        ("broadcast-rcon-to-ops", "true"), ("difficulty", "easy"), ("enable-command-block", "false"), ("enable-jmx-monitoring", "false"),
        ("enable-query", "false"), ("enable-rcon", "false"), ("enable-status", "true"), ("enforce-secure-profile", "true"),
        ("enforce-whitelist", "false"), ("entity-broadcast-range-percentage", "100"), ("force-gamemode", "false"),
        ("function-permission-level", "2"), ("gamemode", "survival"), ("generate-structures", "true"), ("generator-settings", "{}"),
        ("hardcore", "false"), ("hide-online-players", "false"), ("initial-disabled-packs", ""), ("initial-enabled-packs", "vanilla"),
        ("level-name", "world"), ("level-seed", ""), ("level-type", "minecraft:normal"), ("log-ips", "true"),
        ("max-chained-neighbor-updates", "1000000"), ("max-players", "20"), ("max-tick-time", "60000"), ("max-world-size", "29999984"),
        ("motd", "A Minecraft Server"), ("network-compression-threshold", "256"), ("online-mode", "true"), ("op-permission-level", "4"),
        ("player-idle-timeout", "0"), ("prevent-proxy-connections", "false"), ("pvp", "true"), ("query.port", "25565"),
        ("rate-limit", "0"), ("rcon.password", ""), ("rcon.port", "25575"), ("require-resource-pack", "false"),
        ("resource-pack", ""), ("resource-pack-id", ""), ("resource-pack-prompt", ""), ("resource-pack-sha1", ""),
        ("server-ip", ""), ("server-port", "25565"), ("simulation-distance", "10"), ("spawn-monsters", "true"),
        ("spawn-protection", "16"), ("sync-chunk-writes", "true"), ("text-filtering-config", ""), ("use-native-transport", "true"),
        ("view-distance", "10"), ("white-list", "false"),
    ];

    private static PropertyInfo B(string d) => new(d, PropertyKind.Bool);
    private static PropertyInfo N(string d) => new(d, PropertyKind.Number);

    /// <summary>What each known setting does. Shown next to the field in the Properties tab.</summary>
    public static readonly Dictionary<string, PropertyInfo> Known = new()
    {
        ["motd"] = new("Message shown under the server name in the multiplayer list."),
        ["server-port"] = N("Port the server listens on. 25565 is the default players don't have to type."),
        ["server-ip"] = new("Bind to one network address only. Leave empty to listen on all of them."),
        ["max-players"] = N("How many players can be online at once."),
        ["online-mode"] = B("Check players against Microsoft accounts. Turn off only for offline/LAN play - anyone could join with any name."),
        ["white-list"] = B("Only players on the whitelist can join."),
        ["enforce-whitelist"] = B("Kick online players who aren't whitelisted when the whitelist is reloaded."),
        ["gamemode"] = new("Game mode for new players.", PropertyKind.Choice, ["survival", "creative", "adventure", "spectator"]),
        ["force-gamemode"] = B("Put players back into the default game mode every time they join."),
        ["difficulty"] = new("World difficulty.", PropertyKind.Choice, ["peaceful", "easy", "normal", "hard"]),
        ["hardcore"] = B("Players are switched to spectator when they die. Difficulty is locked to hard."),
        ["pvp"] = B("Players can damage each other."),
        ["allow-flight"] = B("Don't kick players for flying. Needed by many mods (jetpacks, wings)."),
        ["allow-nether"] = B("Players can travel to the Nether."),
        ["spawn-monsters"] = B("Hostile mobs spawn."),
        ["generate-structures"] = B("Villages, temples and other structures generate in new chunks."),
        ["level-name"] = new("Folder name of the world."),
        ["level-seed"] = new("Seed for a new world. Leave empty for a random one."),
        ["level-type"] = new("World type for a new world.", PropertyKind.Choice, ["minecraft:normal", "minecraft:flat", "minecraft:large_biomes", "minecraft:amplified", "minecraft:single_biome_surface"]),
        ["view-distance"] = N("How many chunks around each player are sent (3-32). Lower values save a lot of CPU and RAM."),
        ["simulation-distance"] = N("How many chunks around each player are ticked (3-32)."),
        ["spawn-protection"] = N("Radius around spawn that only operators can build in. 0 turns it off."),
        ["max-world-size"] = N("World border radius in blocks."),
        ["player-idle-timeout"] = N("Kick players idle for this many minutes. 0 never kicks."),
        ["enable-command-block"] = B("Command blocks work."),
        ["op-permission-level"] = N("Permission level operators get (1-4)."),
        ["function-permission-level"] = N("Permission level datapack functions run with (1-4)."),
        ["max-tick-time"] = N("Milliseconds one tick may take before the watchdog stops the server. -1 disables it (useful for heavy modpacks)."),
        ["network-compression-threshold"] = N("Packets larger than this many bytes are compressed. -1 turns compression off."),
        ["rate-limit"] = N("Maximum packets per second a player may send before being kicked. 0 means no limit."),
        ["enable-status"] = B("Show the server as online in the multiplayer list."),
        ["hide-online-players"] = B("Don't reveal who is online in the server list."),
        ["enable-query"] = B("Answer GameSpy4 queries (used by some server lists)."),
        ["query.port"] = N("Port for queries."),
        ["enable-rcon"] = B("Allow remote console access. Set a strong rcon.password first."),
        ["rcon.port"] = N("Port for remote console."),
        ["rcon.password"] = new("Password for remote console."),
        ["resource-pack"] = new("URL of a resource pack players are offered when they join."),
        ["resource-pack-sha1"] = new("SHA-1 of the resource pack, so clients can cache it."),
        ["require-resource-pack"] = B("Disconnect players who decline the resource pack."),
        ["resource-pack-prompt"] = new("Custom message shown with the resource pack prompt."),
        ["enforce-secure-profile"] = B("Require players to have signed chat keys."),
        ["prevent-proxy-connections"] = B("Kick players whose connection comes from a different IP than the one they authenticated with."),
        ["sync-chunk-writes"] = B("Write chunks to disk synchronously. Safer, but slower."),
        ["use-native-transport"] = B("Use optimized networking on Linux."),
        ["entity-broadcast-range-percentage"] = N("How far away entities are sent to players, in percent of the default."),
        ["log-ips"] = B("Write player IP addresses to the log."),
        ["broadcast-console-to-ops"] = B("Send console command output to online operators."),
        ["broadcast-rcon-to-ops"] = B("Send remote console command output to online operators."),
        ["accepts-transfers"] = B("Accept players transferred from another server."),
        ["max-chained-neighbor-updates"] = N("Limit for chained block updates before the rest are skipped."),
    };

    public static PropertyInfo Describe(string key, string value) =>
        Known.TryGetValue(key, out var info)
            ? info with { Description = L.T(info.Description) }
            : new PropertyInfo("", value is "true" or "false" ? PropertyKind.Bool : long.TryParse(value, out _) ? PropertyKind.Number : PropertyKind.Text);
}
