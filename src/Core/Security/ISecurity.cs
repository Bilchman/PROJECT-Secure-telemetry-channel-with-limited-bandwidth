namespace Core;

public readonly record struct CryptoResult(
    RejectionCode Code,
    byte[]? PlaintextPayload)
{
    public bool IsSuccess => Code == RejectionCode.None && PlaintextPayload != null;
}

public interface ISecurity
{
    (byte[] KEnc, byte[] KMac) DeriveKeys(string password, byte[] salt);
    byte[] Protect(PacketHeader header, byte[] payloadPlaintext, byte[] kEnc, byte[] kMac);
    CryptoResult Unprotect(ReadOnlySpan<byte> rawPacket, byte[] kEnc, byte[] kMac);
}