namespace Tests;

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Core;
using Xunit;

public class SecurityTests
{
    [Fact]
    public void SpecExample_Matches_Exact_Ciphertext_HMAC_And_Full_Packet()
    {
        string password = "algo2026";
        byte[] salt = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        byte[] nonce = Convert.FromHexString("010203040506070800000000");
        ushort deviceId = 1;
        uint packetSeq = 0;

        byte[] payload = Convert.FromHexString("000100000007000101030304D704E804FD04FF030000000C000000040CE6DF90");
        byte[] expectedCiphertext = Convert.FromHexString("8E51D20BA4B608DFA5B8A5748C5410989DB93D2BFD04DD1CEEB0C925DAE946AF");
        byte[] expectedHmac = Convert.FromHexString("DD881BA276FB275C767958046D3EB9B75D4F7C95EFA3F8CAC0B9411E18461A8E");

        byte[] expectedFullPacket = Convert.FromHexString(
            "544C4D3101000100000000" +
            "000102030405060708090A0B0C0D0E0F" +
            "010203040506070800000000" +
            "00000020" +
            "8E51D20BA4B608DFA5B8A5748C5410989DB93D2BFD04DD1CEEB0C925DAE946AF" +
            "DD881BA276FB275C767958046D3EB9B75D4F7C95EFA3F8CAC0B9411E18461A8E"
        );

        var (kEnc, kMac) = KeyDerivation.DeriveKeys(password, salt);
        Assert.Equal(32, kEnc.Length);
        Assert.Equal(32, kMac.Length);
        Assert.NotEqual(kEnc, kMac);

        byte[] actualCiphertext = CipherService.Encrypt(payload, kEnc, nonce);
        Assert.Equal(expectedCiphertext, actualCiphertext);

        var security = new SecurityService();
        var header = new PacketHeader(deviceId, packetSeq, salt, nonce, (uint)payload.Length);
        byte[] actualPacket = security.Protect(header, payload, kEnc, kMac);

        Assert.Equal(107, actualPacket.Length);
        Assert.Equal(expectedFullPacket, actualPacket);

        byte[] tag = actualPacket[^32..];
        Assert.Equal(expectedHmac, tag);

        var unprotectResult = security.Unprotect(actualPacket, kEnc, kMac);
        Assert.True(unprotectResult.IsSuccess);
        Assert.Equal(RejectionCode.None, unprotectResult.Code);
        Assert.Equal(payload, unprotectResult.PlaintextPayload);
    }

    [Fact]
    public void FIPS_197_AES_256_ECB_Known_Vector()
    {
        byte[] key = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        byte[] pt  = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        byte[] exp = Convert.FromHexString("8EA2B7CA516745BFEAFC49904B496089");

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        using var encryptor = aes.CreateEncryptor();
        byte[] actual = new byte[16];
        encryptor.TransformBlock(pt, 0, 16, actual, 0);

        Assert.Equal(exp, actual);
    }

    [Fact]
    public void NIST_SP_800_38A_CTR_AES256_Known_Vector()
    {
        byte[] key = Convert.FromHexString("603DEB1015CA71BE2B73AEF0857D77811F352C073B6108D72D9810A30914DFF4");
        byte[] pt1 = Convert.FromHexString("6BC1BEE22E409F96E93D7E117393172A");
        byte[] ct1 = Convert.FromHexString("601EC313775789A5B7A7F504BBF3D228");
        byte[] ctr1 = Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF");

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();

        byte[] ks1 = new byte[16];
        encryptor.TransformBlock(ctr1, 0, 16, ks1, 0);

        byte[] actualCt1 = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            actualCt1[i] = (byte)(pt1[i] ^ ks1[i]);
        }

        Assert.Equal(ct1, actualCt1);
    }

    [Fact]
    public void KeyDerivation_Generates_Distinct_Keys_For_Enc_And_Mac()
    {
        byte[] salt = new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var (kEnc, kMac) = KeyDerivation.DeriveKeys("secure_pass", salt);

        Assert.Equal(32, kEnc.Length);
        Assert.Equal(32, kMac.Length);
        Assert.False(kEnc.AsSpan().SequenceEqual(kMac));
    }

    [Fact]
    public void KeyDerivation_PerformanceInvariant_Caching_Executes_Fast()
    {
        byte[] salt = new byte[16] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6 };
        string password = "session_secret_pass";

        var (firstEnc, firstMac) = KeyDerivation.DeriveKeys(password, salt);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            var (kEnc, kMac) = KeyDerivation.DeriveKeys(password, salt);
            Assert.Equal(firstEnc, kEnc);
            Assert.Equal(firstMac, kMac);
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50);
    }

    [Fact]
    public void KeyDerivation_ClearCache_Forces_Recalculation()
    {
        byte[] salt = new byte[16];
        KeyDerivation.ClearCache();
        var (k1, m1) = KeyDerivation.DeriveKeys("test", salt);

        KeyDerivation.ClearCache();
        var (k2, m2) = KeyDerivation.DeriveKeys("test", salt);

        Assert.Equal(k1, k2);
        Assert.Equal(m1, m2);
    }

    [Fact]
    public void Nonce_Is_Composed_Of_8_Random_Bytes_And_4_Bytes_Seq_BigEndian()
    {
        byte[] sessionRandom = [0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44];
        uint packetSeq = 0x01020304;

        byte[] nonce = CipherService.CreateNonce(sessionRandom, packetSeq);

        Assert.Equal(12, nonce.Length);
        Assert.Equal(sessionRandom, nonce[..8]);
        Assert.Equal([0x01, 0x02, 0x03, 0x04], nonce[8..]);
    }

    [Fact]
    public void CipherService_Counter_Starts_At_One_Not_Zero()
    {
        byte[] kEnc = new byte[32];
        byte[] nonce = new byte[12];
        byte[] plaintext = new byte[16];

        byte[] expectedCounter1 = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(expectedCounter1.AsSpan(12, 4), 1);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = kEnc;
        using var encryptor = aes.CreateEncryptor();
        byte[] expectedKeystream = new byte[16];
        encryptor.TransformBlock(expectedCounter1, 0, 16, expectedKeystream, 0);

        byte[] ct = CipherService.Encrypt(plaintext, kEnc, nonce);
        Assert.Equal(expectedKeystream, ct);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(43)]
    [InlineData(74)]
    public void Unprotect_Returns_E_SHORT_When_Packet_Too_Short(int size)
    {
        var security = new SecurityService();
        byte[] shortPacket = new byte[size];
        var (kEnc, kMac) = KeyDerivation.DeriveKeys("pass", new byte[16]);

        var result = security.Unprotect(shortPacket, kEnc, kMac);

        Assert.False(result.IsSuccess);
        Assert.Equal(RejectionCode.E_SHORT, result.Code);
        Assert.Null(result.PlaintextPayload);
    }

    [Fact]
    public void Unprotect_Returns_E_MAGIC_When_Magic_Is_Corrupted()
    {
        var (security, validPacket, kEnc, kMac) = CreateValidTestPacket();
        validPacket[0] = (byte)'X';

        var result = security.Unprotect(validPacket, kEnc, kMac);

        Assert.False(result.IsSuccess);
        Assert.Equal(RejectionCode.E_MAGIC, result.Code);
        Assert.Null(result.PlaintextPayload);
    }

    [Fact]
    public void Unprotect_Returns_E_MAGIC_When_Version_Is_Invalid()
    {
        var (security, validPacket, kEnc, kMac) = CreateValidTestPacket();
        validPacket[4] = 2;

        var result = security.Unprotect(validPacket, kEnc, kMac);

        Assert.False(result.IsSuccess);
        Assert.Equal(RejectionCode.E_MAGIC, result.Code);
        Assert.Null(result.PlaintextPayload);
    }

    [Fact]
    public void Unprotect_Returns_E_LENGTH_When_Packet_Truncated()
    {
        var (security, validPacket, kEnc, kMac) = CreateValidTestPacket();
        byte[] truncated = validPacket[..^5];

        var result = security.Unprotect(truncated, kEnc, kMac);

        Assert.False(result.IsSuccess);
        Assert.Equal(RejectionCode.E_LENGTH, result.Code);
        Assert.Null(result.PlaintextPayload);
    }

    [Fact]
    public void Unprotect_Returns_E_LENGTH_When_CiphertextLen_Does_Not_Match()
    {
        var (security, validPacket, kEnc, kMac) = CreateValidTestPacket();
        uint fakeLen = 1000;
        BinaryPrimitives.WriteUInt32BigEndian(validPacket.AsSpan(39, 4), fakeLen);

        var result = security.Unprotect(validPacket, kEnc, kMac);

        Assert.False(result.IsSuccess);
        Assert.Equal(RejectionCode.E_LENGTH, result.Code);
        Assert.Null(result.PlaintextPayload);
    }

    [Fact]
    public void Unprotect_Returns_E_HMAC_When_Ciphertext_Byte_Is_Tampered()
    {
        var (security, validPacket, kEnc, kMac) = CreateValidTestPacket();
        validPacket[43] ^= 0x01;

        var result = security.Unprotect(validPacket, kEnc, kMac);

        Assert.False(result.IsSuccess);
        Assert.Equal(RejectionCode.E_HMAC, result.Code);
        Assert.Null(result.PlaintextPayload);
    }

    [Fact]
    public void Unprotect_Returns_E_HMAC_When_Header_Field_Is_Tampered()
    {
        var (security, validPacket, kEnc, kMac) = CreateValidTestPacket();
        validPacket[5] ^= 0xFF;

        var result = security.Unprotect(validPacket, kEnc, kMac);

        Assert.False(result.IsSuccess);
        Assert.Equal(RejectionCode.E_HMAC, result.Code);
        Assert.Null(result.PlaintextPayload);
    }

    [Fact]
    public void Unprotect_Returns_E_HMAC_When_Tag_Is_Tampered()
    {
        var (security, validPacket, kEnc, kMac) = CreateValidTestPacket();
        validPacket[^1] ^= 0xFF;

        var result = security.Unprotect(validPacket, kEnc, kMac);

        Assert.False(result.IsSuccess);
        Assert.Equal(RejectionCode.E_HMAC, result.Code);
        Assert.Null(result.PlaintextPayload);
    }

    [Fact]
    public void Unprotect_Returns_E_HMAC_When_Password_Is_Wrong()
    {
        var (security, validPacket, _, _) = CreateValidTestPacket();
        byte[] salt = validPacket[11..27];
        var (wrongKEnc, wrongKMac) = KeyDerivation.DeriveKeys("incorrect_pass", salt);

        var result = security.Unprotect(validPacket, wrongKEnc, wrongKMac);

        Assert.False(result.IsSuccess);
        Assert.Equal(RejectionCode.E_HMAC, result.Code);
        Assert.Null(result.PlaintextPayload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(128)]
    [InlineData(1024)]
    public void Protect_And_Unprotect_Roundtrip_Works_For_Any_Payload_Size(int payloadSize)
    {
        var security = new SecurityService();
        byte[] salt = new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var (kEnc, kMac) = security.DeriveKeys("roundtrip_pass", salt);

        byte[] payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++)
        {
            payload[i] = (byte)(i % 256);
        }

        byte[] nonce = CipherService.CreateNonce([1, 2, 3, 4, 5, 6, 7, 8], 42);
        var header = new PacketHeader(1, 42, salt, nonce, (uint)payload.Length);

        byte[] packet = security.Protect(header, payload, kEnc, kMac);
        Assert.Equal(43 + payloadSize + 32, packet.Length);

        var result = security.Unprotect(packet, kEnc, kMac);
        Assert.True(result.IsSuccess);
        Assert.Equal(payload, result.PlaintextPayload);
    }

    private static (SecurityService security, byte[] packet, byte[] kEnc, byte[] kMac) CreateValidTestPacket()
    {
        var security = new SecurityService();
        byte[] salt = new byte[16] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        var (kEnc, kMac) = security.DeriveKeys("test_password", salt);

        byte[] payload = Encoding.UTF8.GetBytes("Telemetry test payload 12345");
        byte[] nonce = CipherService.CreateNonce([1, 1, 1, 1, 2, 2, 2, 2], 1);
        var header = new PacketHeader(10, 1, salt, nonce, (uint)payload.Length);

        byte[] packet = security.Protect(header, payload, kEnc, kMac);
        return (security, packet, kEnc, kMac);
    }
}
