using PyroLauncher.UI;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using PyroLauncher.Core;

namespace PyroLauncher.Accounts;

public enum AccountType { Offline, Microsoft }

public partial class Account : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AccountType Type { get; set; }
    public string Username { get; set; } = "";
    /// <summary>Minecraft profile UUID without dashes.</summary>
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "0";
    public string? MsRefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? Xuid { get; set; }

    [property: System.Text.Json.Serialization.JsonIgnore]
    [ObservableProperty] private bool _isSelected;

    public string TypeText => Type == AccountType.Microsoft ? "Microsoft account" : "Offline account";
    public string AvatarUrl => $"https://mc-heads.net/avatar/{(Type == AccountType.Microsoft ? Uuid : Username)}/64";

    public static string OfflineUuid(string name)
    {
        // Same algorithm as Java's UUID.nameUUIDFromBytes("OfflinePlayer:" + name)
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return Convert.ToHexStringLower(hash);
    }
}

public static class AccountManager
{
    public static ObservableCollection<Account> Accounts { get; } = [];
    public static event Action? SelectionChanged;

    /// <summary>Offline accounts are a developer feature.</summary>
    public static bool OfflineAllowed => SettingsService.Current.DeveloperMode;

    public static bool IsUsable(Account a) => a.Type == AccountType.Microsoft || OfflineAllowed;

    public static Account? Selected => Accounts.FirstOrDefault(a => a.IsSelected && IsUsable(a));

    public static event Action? DeveloperModeChanged;

    public static void NotifyDeveloperModeChanged()
    {
        DeveloperModeChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public static void Load()
    {
        Accounts.Clear();
        try
        {
            if (File.Exists(AppPaths.AccountsFile))
            {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.AccountsFile), null, DataProtectionScope.CurrentUser);
                var list = JsonSerializer.Deserialize<List<Account>>(bytes, JsonUtil.Options) ?? [];
                foreach (var a in list) Accounts.Add(a);
            }
        }
        catch (Exception ex) { Log.Error("Loading accounts failed", ex); }

        var sel = Accounts.FirstOrDefault(a => a.Id == SettingsService.Current.SelectedAccountId) ?? Accounts.FirstOrDefault();
        if (sel != null) sel.IsSelected = true;
    }

    public static void Save()
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Accounts.ToList(), JsonUtil.Options);
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllBytes(AppPaths.AccountsFile, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) { Log.Error("Saving accounts failed", ex); }
    }

    public static void Select(Account? account)
    {
        foreach (var a in Accounts) a.IsSelected = a == account;
        SettingsService.Current.SelectedAccountId = account?.Id;
        SettingsService.Save();
        SelectionChanged?.Invoke();
    }

    public static Account AddOffline(string name)
    {
        if (!OfflineAllowed) throw new InvalidOperationException(L.T("Offline accounts require developer mode."));
        var acc = new Account
        {
            Type = AccountType.Offline,
            Username = name,
            Uuid = Account.OfflineUuid(name),
            AccessToken = "0",
        };
        Accounts.Add(acc);
        Save();
        Select(acc);
        return acc;
    }

    public static void AddOrUpdate(Account acc)
    {
        var existing = Accounts.FirstOrDefault(a => a.Type == acc.Type && a.Uuid == acc.Uuid);
        if (existing != null)
        {
            acc.Id = existing.Id;
            Accounts[Accounts.IndexOf(existing)] = acc;
        }
        else Accounts.Add(acc);
        Save();
        Select(acc);
    }

    public static void Remove(Account acc)
    {
        var wasSelected = acc.IsSelected;
        Accounts.Remove(acc);
        Save();
        if (wasSelected) Select(Accounts.FirstOrDefault());
    }

    /// <summary>Refreshes Microsoft tokens if they are close to expiring.</summary>
    public static async Task EnsureValidAsync(Account acc, CancellationToken ct)
    {
        if (acc.Type != AccountType.Microsoft) return;
        if (acc.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10)) return;
        if (string.IsNullOrEmpty(acc.MsRefreshToken))
            throw new InvalidOperationException(L.T("Microsoft session expired. Please sign in again."));
        var refreshed = await MicrosoftAuth.RefreshAsync(acc.MsRefreshToken, ct);
        acc.AccessToken = refreshed.AccessToken;
        acc.MsRefreshToken = refreshed.MsRefreshToken;
        acc.ExpiresAt = refreshed.ExpiresAt;
        acc.Username = refreshed.Username;
        acc.Xuid = refreshed.Xuid;
        Save();
    }
}
