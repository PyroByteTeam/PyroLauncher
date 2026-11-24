using System.IO;
using System.Security.Cryptography;

namespace PyroLauncher.Core;

public static class Hashing
{
    public static string Sha1File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(fs));
    }

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>CurseForge file fingerprint: MurmurHash2 (seed 1) over the file with whitespace bytes removed.</summary>
    public static uint CurseForgeFingerprint(string path)
    {
        var raw = File.ReadAllBytes(path);
        var buf = new byte[raw.Length];
        int len = 0;
        foreach (var b in raw)
            if (b != 9 && b != 10 && b != 13 && b != 32)
                buf[len++] = b;
        return Murmur2(buf, len, 1);
    }

    private static uint Murmur2(byte[] data, int len, uint seed)
    {
        const uint m = 0x5bd1e995;
        const int r = 24;
        uint h = seed ^ (uint)len;
        int i = 0;
        unchecked
        {
            while (len - i >= 4)
            {
                uint k = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
                k *= m; k ^= k >> r; k *= m;
                h *= m; h ^= k;
                i += 4;
            }
            switch (len - i)
            {
                case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
                case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
                case 1: h ^= data[i]; h *= m; break;
            }
            h ^= h >> 13; h *= m; h ^= h >> 15;
        }
        return h;
    }
}
