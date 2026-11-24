# Microsoft login for Minecraft — step by step

PyroLauncher signs in with the **OAuth 2.0 device code flow**: the launcher shows a short code, you enter it on
`microsoft.com/link` in any browser, and the launcher exchanges the result for a Minecraft token. No passwords ever
pass through the launcher, and no redirect URI or local web server is needed.

You need your **own Azure application (client) ID**. Microsoft does not allow apps to reuse another launcher's ID,
and new IDs must be approved by Mojang before they can call the Minecraft API.

---

## Part 1 — Register the Azure application

1. Sign in to the Azure portal: <https://portal.azure.com> (a free personal Microsoft account works; you don't need a paid subscription).
2. Open **Microsoft Entra ID → App registrations → New registration**.
3. Fill in:
   - **Name:** e.g. `PyroLauncher` (users see this on the consent screen).
   - **Supported account types:** *Personal Microsoft accounts only*
     (or *Accounts in any organizational directory and personal Microsoft accounts* — both work, Minecraft accounts are personal accounts).
   - **Redirect URI:** leave empty. The device code flow doesn't use one.
4. Click **Register**.
5. On the app's **Overview** page, copy the **Application (client) ID** (a GUID such as `1a2b3c4d-…`).
6. Go to **Authentication** (or **Authentication → Settings** in the new UI) and set **Allow public client flows** to **Yes**, then **Save**.
   This is required: the device code flow is a public-client flow, and without it sign-in fails with `AADSTS7000218`.
7. You do **not** need to add API permissions in the portal. The launcher requests the
   `XboxLive.signin offline_access` scopes at sign-in time and the user consents to them.
8. You do **not** need a client secret or certificate — a desktop launcher can't keep a secret, so it is a *public* client.

## Part 2 — Get the app approved for Minecraft

Since 2023, new Azure apps get **HTTP 403 "Invalid app registration"** from
`api.minecraftservices.com/authentication/login_with_xbox` until Mojang approves them.

1. Open the Minecraft app review form: <https://aka.ms/mce-reviewappid>.
2. Submit your **Application (client) ID**, your **tenant/app details**, and a short description of the launcher
   (what it does, that it uses the official Microsoft/Xbox/Minecraft services flow, and where it's published).
3. Wait for the approval email. Until then, Xbox sign-in works but the Minecraft step returns 403.

## Part 3 — Configure PyroLauncher

1. Open **Settings → Launcher → Microsoft client ID** and paste the Application (client) ID.
2. Go to **Profile → Sign in with Microsoft**.
3. The launcher copies a code to the clipboard and opens <https://www.microsoft.com/link>. Paste the code, sign in, and accept.
4. The account appears in the list and becomes active. Tokens refresh automatically before each launch.

To ship the ID with your build instead of asking users, set the default in
`src/PyroLauncher/Core/Settings.cs` (`LauncherSettings.MicrosoftClientId`).

---

## How the flow works (what the code does)

All of this lives in `src/PyroLauncher/Accounts/MicrosoftAuth.cs`.

| # | Request | Purpose |
|---|---|---|
| 1 | `POST https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode`<br>`client_id`, `scope=XboxLive.signin offline_access` | Returns `user_code`, `device_code`, `verification_uri`, `interval`, `expires_in`. Show the code to the user. |
| 2 | `POST …/oauth2/v2.0/token` every `interval` seconds<br>`grant_type=urn:ietf:params:oauth:grant-type:device_code`, `client_id`, `device_code` | Returns `authorization_pending` until the user finishes, then a Microsoft `access_token` + `refresh_token`. Handle `slow_down` (+5 s), `authorization_declined`, `expired_token`. |
| 3 | `POST https://user.auth.xboxlive.com/user/authenticate`<br>`{"Properties":{"AuthMethod":"RPS","SiteName":"user.auth.xboxlive.com","RpsTicket":"d=<ms_access_token>"},"RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT"}` | Xbox Live user token. Note the `d=` prefix (required for tokens from the v2.0 endpoint). |
| 4 | `POST https://xsts.auth.xboxlive.com/xsts/authorize`<br>`{"Properties":{"SandboxId":"RETAIL","UserTokens":["<xbl_token>"]},"RelyingParty":"rp://api.minecraftservices.com/","TokenType":"JWT"}` | XSTS token plus the user hash `uhs` and `xid` (XUID) in `DisplayClaims.xui[0]`. |
| 5 | `POST https://api.minecraftservices.com/authentication/login_with_xbox`<br>`{"identityToken":"XBL3.0 x=<uhs>;<xsts_token>"}` | Minecraft `access_token` (valid ~24 h). 403 here = app not approved (Part 2). |
| 6 | `GET https://api.minecraftservices.com/minecraft/profile` with `Authorization: Bearer <mc_token>` | Player UUID, name, skins and capes. **404 = the account doesn't own Java Edition.** |

**Refreshing:** before every launch, if the Minecraft token expires within 10 minutes, the launcher calls the token
endpoint with `grant_type=refresh_token` and repeats steps 3–6 (`AccountManager.EnsureValidAsync`).

**Launching:** the game receives `--accessToken <mc_token> --uuid <id> --username <name> --userType msa --xuid <xid> --clientId <client_id>`.

**Storage:** accounts (including refresh tokens) are saved in `%APPDATA%\PyroLauncher\accounts.dat`, encrypted with
Windows DPAPI for the current user (`ProtectedData`, `DataProtectionScope.CurrentUser`).

### XSTS error codes (step 4)

| `XErr` | Meaning | Shown to the user |
|---|---|---|
| 2148916233 | No Xbox profile on this Microsoft account | Create one at xbox.com first |
| 2148916235 | Xbox Live isn't available in the account's country | — |
| 2148916236 / 2148916237 | Adult verification needed (South Korea) | Verify on xbox.com |
| 2148916238 | Child account not in a Microsoft Family | An adult must add it to a Family |

### Skins and capes (Profile page)

Uses the same Minecraft token (`src/PyroLauncher/Accounts/ProfileApi.cs`):

| Action | Request |
|---|---|
| Read skins & capes | `GET /minecraft/profile` |
| Upload skin | `POST /minecraft/profile/skins` (multipart: `variant` = `classic`/`slim`, `file` = 64×64 PNG) |
| Reset skin | `DELETE /minecraft/profile/skins/active` |
| Show cape | `PUT /minecraft/profile/capes/active` with `{"capeId":"…"}` |
| Hide cape | `DELETE /minecraft/profile/capes/active` |

## Troubleshooting

| Symptom | Fix |
|---|---|
| "Microsoft login needs an Azure application (client) ID" | Paste the ID in Settings → Launcher. |
| `AADSTS7000218` / "client_assertion or client_secret required" | Turn on **Allow public client flows** (Part 1, step 6). |
| `AADSTS700016` / "application not found" | Wrong client ID, or the app only allows organizational accounts — change *Supported account types*. |
| `unauthorized_client` | Same as above: the app must accept personal Microsoft accounts. |
| HTTP 403 "Invalid app registration" at login_with_xbox | App not approved for Minecraft yet (Part 2). |
| HTTP 404 at /minecraft/profile | The account doesn't own Minecraft: Java Edition (or hasn't created a profile name yet). |
| The code expired | Codes last ~15 minutes; start sign-in again. |
