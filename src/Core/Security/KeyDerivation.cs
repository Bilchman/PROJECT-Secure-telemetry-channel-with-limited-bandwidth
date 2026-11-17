namespace Core;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

public static class KeyDerivation
{
    private const int MaxCacheEntries = 64;
    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, (byte[] KEnc, byte[] KMac)> _cache = new();

    public static (byte[] KEnc, byte[] KMac) DeriveKeys(string password, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (salt == null || salt.Length != 16)
        {
            throw new ArgumentException("Salt must be 16 bytes", nameof(salt));
        }

        string cacheKey = $"{password}\0{Convert.ToHexString(salt)}";

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        byte[] masterKey = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100000,
            HashAlgorithmName.SHA256,
            64
        );

        byte[] kEnc = new byte[32];
        byte[] kMac = new byte[32];
        Array.Copy(masterKey, 0, kEnc, 0, 32);
        Array.Copy(masterKey, 32, kMac, 0, 32);

        lock (_cacheLock)
        {
            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }

            _cache[cacheKey] = (kEnc, kMac);
            return (kEnc, kMac);
        }
    }

    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
        }
    }
}