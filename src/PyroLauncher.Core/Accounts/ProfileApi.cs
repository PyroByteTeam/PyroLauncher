using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PyroLauncher.Core;

namespace PyroLauncher.Accounts;

public record SkinInfo(string Id, string Url, bool Slim, bool Active);
public record CapeInfo(string Id, string Alias, string Url, bool Active);
public record MinecraftProfile(string Id, string Name, List<SkinInfo> Skins, List<CapeInfo> Capes)
{
    public SkinInfo? ActiveSkin => Skins.FirstOrDefault(s => s.Active) ?? Skins.FirstOrDefault();
    public CapeInfo? ActiveCape => Capes.FirstOrDefault(c => c.Active);
}

/// <summary>Official Minecraft Services profile API: skins and capes for Microsoft accounts.</summary>
public static class ProfileApi
{
    private const string Base = "https://api.minecraftservices.com/minecraft/profile";

    private static Action<HttpRequestMessage> Bearer(Account acc) =>
        r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acc.AccessToken);

    public static async Task<MinecraftProfile> GetAsync(Account acc, CancellationToken ct)
    {
        await AccountManager.EnsureValidAsync(acc, ct);
        return Parse(await Http.GetJsonAsync(Base, ct, Bearer(acc)));
    }

    private static MinecraftProfile Parse(JsonNode? p) => new(
        p.Str("id") ?? "",
        p.Str("name") ?? "",
        p.Arr("skins").Select(s => new SkinInfo(s.Str("id") ?? "", Https(s.Str("url")), s.Str("variant") == "SLIM", s.Str("state") == "ACTIVE")).ToList(),
        p.Arr("capes").Select(c => new CapeInfo(c.Str("id") ?? "", c.Str("alias") ?? "Cape", Https(c.Str("url")), c.Str("state") == "ACTIVE")).ToList());

    private static string Https(string? url) => (url ?? "").Replace("http://", "https://");

    /// <summary>Shows the given cape, or hides the cape when <paramref name="capeId"/> is null.</summary>
    public static async Task<MinecraftProfile> SetCapeAsync(Account acc, string? capeId, CancellationToken ct)
    {
        await AccountManager.EnsureValidAsync(acc, ct);
        using var req = capeId == null
            ? new HttpRequestMessage(HttpMethod.Delete, $"{Base}/capes/active")
            : new HttpRequestMessage(HttpMethod.Put, $"{Base}/capes/active")
            {
                Content = new StringContent(new JsonObject { ["capeId"] = capeId }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
        Bearer(acc)(req);
        using var resp = await Http.SendAsync(req, ct);
        return Parse(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)));
    }

    /// <summary>Uploads a PNG skin (64×64 or 64×32).</summary>
    public static async Task<MinecraftProfile> UploadSkinAsync(Account acc, string pngPath, bool slim, CancellationToken ct)
    {
        await AccountManager.EnsureValidAsync(acc, ct);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(slim ? "slim" : "classic"), "variant");
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(pngPath, ct));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", Path.GetFileName(pngPath));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/skins") { Content = form };
        Bearer(acc)(req);
        using var resp = await Http.SendAsync(req, ct, TimeSpan.FromMinutes(2));
        var body = await resp.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(body) ? await GetAsync(acc, ct) : Parse(JsonNode.Parse(body));
    }

    /// <summary>Resets to the default skin.</summary>
    public static async Task<MinecraftProfile> ResetSkinAsync(Account acc, CancellationToken ct)
    {
        await AccountManager.EnsureValidAsync(acc, ct);
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/skins/active");
        Bearer(acc)(req);
        using var resp = await Http.SendAsync(req, ct);
        return Parse(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)));
    }
}
