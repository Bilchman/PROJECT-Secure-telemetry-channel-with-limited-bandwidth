namespace Core;

using System;
using System.Security.Cryptography;
using System.Text;

public static class KeyDerivation
{
    private static readonly object _cacheLock = new();
    private static string? _cachedPassword;
    private static byte[]? _cachedSalt;
    private static byte[]? _cachedKEnc;
    private static byte[]? _cachedKMac;

    public static (byte[] KEnc, byte[] KMac) DeriveKeys(string password, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (salt == null || salt.Length != 16)
        {
            throw new ArgumentException("Salt must be 16 bytes", nameof(salt));
        }

        lock (_cacheLock)
        {
            if (_cachedPassword == password && _cachedSalt != null && _cachedSalt.AsSpan().SequenceEqual(salt))
            {
                return (_cachedKEnc!, _cachedKMac!);
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

            _cachedPassword = password;
            _cachedSalt = (byte[])salt.Clone();
            _cachedKEnc = kEnc;
            _cachedKMac = kMac;

            return (kEnc, kMac);
        }
    }

    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedPassword = null;
            _cachedSalt = null;
            _cachedKEnc = null;
            _cachedKMac = null;
        }
    }
}