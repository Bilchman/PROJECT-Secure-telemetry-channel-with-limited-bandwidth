namespace Core;

public sealed class NoSecurity : ISecurity
{
    public (byte[] KEnc, byte[] KMac) DeriveKeys(string password, byte[] salt) => 
        (new byte[32], new byte[32]);

    public byte[] Protect(PacketHeader header, byte[] payloadPlaintext, byte[] kEnc, byte[] kMac)
    {
        var result = new byte[PacketHeader.SizeInBytes + payloadPlaintext.Length + 32];
        header.WriteTo(result.AsSpan(0, PacketHeader.SizeInBytes));
        payloadPlaintext.CopyTo(result.AsSpan(PacketHeader.SizeInBytes));
        return result;
    }

    public CryptoResult Unprotect(ReadOnlySpan<byte> rawPacket, byte[] kEnc, byte[] kMac)
    {
        if (rawPacket.Length < 75)
            return new CryptoResult(RejectionCode.E_SHORT, null);

        var (code, header) = PacketHeader.TryRead(rawPacket);
        if (code != RejectionCode.None)
            return new CryptoResult(code, null);

        int payloadLen = rawPacket.Length - 43 - 32;
        var payloadBytes = rawPacket.Slice(43, payloadLen).ToArray();

        return new CryptoResult(RejectionCode.None, payloadBytes);
    }
}