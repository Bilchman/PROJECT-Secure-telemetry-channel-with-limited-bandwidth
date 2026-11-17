namespace Core;

using System;
using System.Security.Cryptography;

public static class Authenticator
{
    public static byte[] ComputeTag(byte[] kMac, ReadOnlySpan<byte> data)
    {
        return HMACSHA256.HashData(kMac, data);
    }

    public static bool VerifyTag(byte[] kMac, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedTag)
    {
        if (expectedTag.Length != 32)
        {
            return false;
        }

        byte[] actualTag = ComputeTag(kMac, data);
        return CryptographicOperations.FixedTimeEquals(actualTag, expectedTag);
    }
}
