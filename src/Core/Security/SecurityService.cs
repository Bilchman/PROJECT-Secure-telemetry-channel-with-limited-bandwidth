namespace Core;

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

public sealed class SecurityService : ISecurity
{
    public (byte[] KEnc, byte[] KMac) DeriveKeys(string password, byte[] salt)
    {
        return KeyDerivation.DeriveKeys(password, salt);
    }

    public byte[] Protect(PacketHeader header, byte[] payloadPlaintext, byte[] kEnc, byte[] kMac)
    {
        byte[] ciphertext = CipherService.Encrypt(payloadPlaintext, kEnc, header.Nonce);
        var updatedHeader = header with { CiphertextLen = (uint)ciphertext.Length };

        int dataLength = 43 + ciphertext.Length;
        byte[] packet = new byte[dataLength + 32];

        updatedHeader.WriteTo(packet.AsSpan(0, 43));
        ciphertext.CopyTo(packet.AsSpan(43));

        byte[] tag = Authenticator.ComputeTag(kMac, packet.AsSpan(0, dataLength));
        tag.CopyTo(packet.AsSpan(dataLength, 32));

        return packet;
    }

    public CryptoResult Unprotect(ReadOnlySpan<byte> rawPacket, byte[] kEnc, byte[] kMac)
    {
        if (rawPacket.Length < 75)
        {
            return new CryptoResult(RejectionCode.E_SHORT, null);
        }

        if (!rawPacket[..4].SequenceEqual(PacketHeader.ExpectedMagic) || rawPacket[4] != PacketHeader.ExpectedVersion)
        {
            return new CryptoResult(RejectionCode.E_MAGIC, null);
        }

        uint ciphertextLen = BinaryPrimitives.ReadUInt32BigEndian(rawPacket.Slice(39, 4));
        long expectedLength = 43L + ciphertextLen + 32L;

        if (rawPacket.Length != expectedLength)
        {
            return new CryptoResult(RejectionCode.E_LENGTH, null);
        }

        int dataLength = 43 + (int)ciphertextLen;
        var data = rawPacket[..dataLength];
        var tag = rawPacket.Slice(dataLength, 32);

        if (!Authenticator.VerifyTag(kMac, data, tag))
        {
            return new CryptoResult(RejectionCode.E_HMAC, null);
        }

        var nonce = rawPacket.Slice(27, 12);
        var ciphertext = rawPacket.Slice(43, (int)ciphertextLen);
        byte[] plaintext = CipherService.Decrypt(ciphertext, kEnc, nonce);

        return new CryptoResult(RejectionCode.None, plaintext);
    }

    public static byte[] GenerateSalt()
    {
        byte[] salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }
}
