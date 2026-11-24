using System.Security.Cryptography;
using PyroLauncher.Core;

namespace PyroLauncher.Accounts;

/// <summary>
/// Encrypts the saved accounts. Windows uses DPAPI (tied to the Windows user). macOS and Linux use AES-GCM
/// with a random key kept in a file only the current user can read.
/// </summary>
public static class SecureStore
{
    private static string KeyFile => Path.Combine(AppPaths.Root, "accounts.key");
    private static readonly byte[] Magic = "PYR1"u8.ToArray();

    public static byte[] Protect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        var key = GetKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return [.. Magic, .. nonce, .. tag, .. cipher];
    }

    public static byte[] Unprotect(byte[] blob)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
        if (blob.Length < 32 || !blob.AsSpan(0, 4).SequenceEqual(Magic)) throw new CryptographicException("Unknown accounts file format");
        var nonce = blob.AsSpan(4, 12);
        var tag = blob.AsSpan(16, 16);
        var cipher = blob.AsSpan(32);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(GetKey(), 16)) aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private static byte[] GetKey()
    {
        if (File.Exists(KeyFile)) return File.ReadAllBytes(KeyFile);
        Directory.CreateDirectory(AppPaths.Root);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(KeyFile, key);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
        return key;
    }
}
