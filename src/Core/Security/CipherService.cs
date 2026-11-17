namespace Core;

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

public static class CipherService
{
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] kEnc, ReadOnlySpan<byte> nonce)
    {
        return Transform(plaintext, kEnc, nonce);
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, byte[] kEnc, ReadOnlySpan<byte> nonce)
    {
        return Transform(ciphertext, kEnc, nonce);
    }

    public static byte[] Transform(ReadOnlySpan<byte> input, byte[] kEnc, ReadOnlySpan<byte> nonce)
    {
        if (input.Length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] output = new byte[input.Length];

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = kEnc;
        using var encryptor = aes.CreateEncryptor();

        byte[] counterBlock = new byte[16];
        nonce.CopyTo(counterBlock.AsSpan(0, 12));

        byte[] keystream = new byte[16];
        int offset = 0;
        uint blockIndex = 1;

        while (offset < input.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(counterBlock.AsSpan(12, 4), blockIndex);
            encryptor.TransformBlock(counterBlock, 0, 16, keystream, 0);

            int bytesInBlock = Math.Min(16, input.Length - offset);
            for (int i = 0; i < bytesInBlock; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
            }

            offset += bytesInBlock;
            blockIndex++;
        }

        return output;
    }

    public static byte[] CreateNonce(byte[] sessionRandom8, uint packetSeq)
    {
        byte[] nonce = new byte[12];
        Array.Copy(sessionRandom8, 0, nonce, 0, 8);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8, 4), packetSeq);
        return nonce;
    }
}
