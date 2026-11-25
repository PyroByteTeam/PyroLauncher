using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using PyroLauncher.Accounts;
using PyroLauncher.Core;

namespace PyroLauncher.Servers;

public partial class PlayerEntry : ObservableObject
{
    public string Name { get; init; } = "";
    public string Uuid { get; init; } = "";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isOp;
    [ObservableProperty] private bool _isWhitelisted;
    [ObservableProperty] private bool _isBanned;
    public string AvatarUrl => $"https://mc-heads.net/avatar/{(string.IsNullOrEmpty(Uuid) ? Name : Uuid.Replace("-", ""))}/64";
}

/// <summary>
/// Players a server knows about (usercache, ops, whitelist, bans). While the server runs, changes go through
/// console commands so they apply immediately; otherwise the JSON files are edited directly.
/// </summary>
public static class ServerPlayers
{
    public static List<PlayerEntry> Load(ServerPack pack)
    {
        var dir = pack.ServerDir;
        var players = new Dictionary<string, PlayerEntry>(StringComparer.OrdinalIgnoreCase);
        PlayerEntry Get(string name, string? uuid)
        {
            if (!players.TryGetValue(name, out var p)) players[name] = p = new PlayerEntry { Name = name, Uuid = uuid ?? "" };
            return p;
        }
        foreach (var n in Read(dir, "usercache.json")) if (n.Str("name") is { } name) Get(name, n.Str("uuid"));
        foreach (var n in Read(dir, "ops.json")) if (n.Str("name") is { } name) Get(name, n.Str("uuid")).IsOp = true;
        foreach (var n in Read(dir, "whitelist.json")) if (n.Str("name") is { } name) Get(name, n.Str("uuid")).IsWhitelisted = true;
        foreach (var n in Read(dir, "banned-players.json")) if (n.Str("name") is { } name) Get(name, n.Str("uuid")).IsBanned = true;
        foreach (var name in pack.OnlinePlayers) Get(name, null).IsOnline = true;
        return players.Values.OrderByDescending(p => p.IsOnline).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<JsonNode> Read(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return [];
        try { return (JsonUtil.ParseFile(path) as JsonArray)?.OfType<JsonNode>().ToList() ?? []; }
        catch { return []; }
    }

    public static async Task SetOpAsync(ServerPack pack, PlayerEntry p, bool op)
    {
        if (pack.IsRunning) { pack.SendCommand($"{(op ? "op" : "deop")} {p.Name}"); return; }
        var uuid = await ResolveUuidAsync(pack, p);
        Edit(pack, "ops.json", p.Name, op ? new JsonObject { ["uuid"] = uuid, ["name"] = p.Name, ["level"] = 4, ["bypassesPlayerLimit"] = false } : null);
    }

    public static async Task SetWhitelistedAsync(ServerPack pack, string name, bool on, PlayerEntry? known = null)
    {
        if (pack.IsRunning) { pack.SendCommand($"whitelist {(on ? "add" : "remove")} {name}"); return; }
        var uuid = await ResolveUuidAsync(pack, known ?? new PlayerEntry { Name = name });
        Edit(pack, "whitelist.json", name, on ? new JsonObject { ["uuid"] = uuid, ["name"] = name } : null);
    }

    public static async Task SetBannedAsync(ServerPack pack, PlayerEntry p, bool banned)
    {
        if (pack.IsRunning) { pack.SendCommand(banned ? $"ban {p.Name}" : $"pardon {p.Name}"); return; }
        var uuid = await ResolveUuidAsync(pack, p);
        Edit(pack, "banned-players.json", p.Name, banned
            ? new JsonObject
            {
                ["uuid"] = uuid, ["name"] = p.Name, ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zz00"),
                ["source"] = "PyroLauncher", ["expires"] = "forever", ["reason"] = "Banned by an operator.",
            }
            : null);
    }

    private static void Edit(ServerPack pack, string file, string name, JsonObject? add)
    {
        var path = Path.Combine(pack.ServerDir, file);
        var arr = File.Exists(path) ? JsonUtil.ParseFile(path) as JsonArray ?? [] : [];
        foreach (var n in arr.Where(n => string.Equals(n.Str("name"), name, StringComparison.OrdinalIgnoreCase)).ToList()) arr.Remove(n);
        if (add != null) arr.Add(add);
        JsonUtil.WriteNode(path, arr);
    }

    /// <summary>Online-mode servers need the Mojang UUID; offline-mode servers use the name-based one.</summary>
    private static async Task<string> ResolveUuidAsync(ServerPack pack, PlayerEntry p)
    {
        if (!string.IsNullOrEmpty(p.Uuid)) return p.Uuid;
        var online = ServerProperties.Load(pack.PropertiesPath).Get("online-mode") != "false";
        string raw = Account.OfflineUuid(p.Name);
        if (online)
        {
            try { raw = (await Http.GetJsonAsync($"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(p.Name)}")).Str("id") ?? raw; }
            catch (Exception ex) { Log.Warn($"UUID lookup for {p.Name} failed: {ex.Message}"); }
        }
        return Guid.TryParseExact(raw, "N", out var g) ? g.ToString("D") : raw;
    }
}
