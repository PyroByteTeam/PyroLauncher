using PyroLauncher.UI;
using System.Net.Http;
using System.Text.Json.Nodes;
using PyroLauncher.Core;

namespace PyroLauncher.Accounts;

public record DeviceCodeInfo(string UserCode, string VerificationUri, string DeviceCode, int Interval, int ExpiresIn);

/// <summary>Microsoft → Xbox Live → XSTS → Minecraft Services sign-in using the OAuth device code flow.</summary>
public static class MicrosoftAuth
{
    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string Scope = "XboxLive.signin offline_access";

    /// <summary>PyroLauncher's Azure application (client) ID, approved for the Minecraft API. Not user-configurable.</summary>
    public const string ClientId = "4e4681e7-5d96-4d58-a514-ff4b8911b951";

    public static async Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct)
    {
        var res = await Http.PostFormAsync($"{Authority}/devicecode", new()
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        }, ct);
        return new DeviceCodeInfo(res.Str("user_code")!, res.Str("verification_uri")!, res.Str("device_code")!,
            res.Int("interval", 5), res.Int("expires_in", 900));
    }

    public static async Task<Account> PollAndLoginAsync(DeviceCodeInfo dc, CancellationToken ct)
    {
        var interval = Math.Max(dc.Interval, 2);
        var deadline = DateTime.UtcNow.AddSeconds(dc.ExpiresIn);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            JsonNode? res;
            try
            {
                res = await Http.PostFormAsync($"{Authority}/token", new()
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = dc.DeviceCode,
                }, ct);
            }
            catch (HttpError e)
            {
                var err = JsonNode.Parse(e.Body).Str("error");
                switch (err)
                {
                    case "authorization_pending": continue;
                    case "slow_down": interval += 5; continue;
                    case "authorization_declined": throw new OperationCanceledException("Sign-in was declined.");
                    case "expired_token": throw new TimeoutException("The sign-in code expired. Try again.");
                    default: throw;
                }
            }
            return await LoginWithMsTokenAsync(res.Str("access_token")!, res.Str("refresh_token"), ct);
        }
        throw new TimeoutException("The sign-in code expired. Try again.");
    }

    public static async Task<Account> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var res = await Http.PostFormAsync($"{Authority}/token", new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        }, ct);
        return await LoginWithMsTokenAsync(res.Str("access_token")!, res.Str("refresh_token") ?? refreshToken, ct);
    }

    private static async Task<Account> LoginWithMsTokenAsync(string msToken, string? refresh, CancellationToken ct)
    {
        // Xbox Live
        var xbl = await Http.PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + msToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        }, ct);
        var xblToken = xbl.Str("Token")!;

        // XSTS
        JsonNode? xsts;
        try
        {
            xsts = await Http.PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["SandboxId"] = "RETAIL",
                    ["UserTokens"] = new JsonArray(xblToken),
                },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT",
            }, ct);
        }
        catch (HttpError e)
        {
            var code = JsonNode.Parse(string.IsNullOrEmpty(e.Body) ? "{}" : e.Body).Long("XErr");
            throw code switch
            {
                2148916233 => new InvalidOperationException(L.T("This Microsoft account has no Xbox profile. Create one at xbox.com first.")),
                2148916235 => new InvalidOperationException(L.T("Xbox Live is not available in your country.")),
                2148916236 or 2148916237 => new InvalidOperationException(L.T("This account needs adult verification on xbox.com.")),
                2148916238 => new InvalidOperationException(L.T("This is a child account; it must be added to a Family by an adult.")),
                _ => e,
            };
        }
        var xstsToken = xsts.Str("Token")!;
        var xui = xsts.Obj("DisplayClaims")?.Arr("xui").FirstOrDefault();
        var uhs = xui.Str("uhs")!;
        var xuid = xui.Str("xid");

        // Minecraft
        var mc = await Http.PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new JsonObject
        {
            ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}",
        }, ct);
        var mcToken = mc.Str("access_token")!;
        var expires = mc.Int("expires_in", 86400);

        JsonNode? profile;
        try
        {
            profile = await Http.GetJsonAsync("https://api.minecraftservices.com/minecraft/profile", ct,
                r => r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken));
        }
        catch (HttpError e) when (e.Status == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(L.T("This Microsoft account does not own Minecraft: Java Edition."));
        }

        return new Account
        {
            Type = AccountType.Microsoft,
            Username = profile.Str("name")!,
            Uuid = profile.Str("id")!,
            AccessToken = mcToken,
            MsRefreshToken = refresh,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires),
            Xuid = xuid,
        };
    }
}
