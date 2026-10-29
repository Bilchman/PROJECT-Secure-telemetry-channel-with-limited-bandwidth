namespace Core;

using System.Buffers.Binary;

public sealed record PacketHeader(
    ushort DeviceId,
    uint   PacketSeq,
    byte[] Salt,       // 16 байтів
    byte[] Nonce,      // 12 байтів
    uint   CiphertextLen)
{
    public const int SizeInBytes = 43;
    public static readonly byte[] ExpectedMagic = [0x54, 0x4C, 0x4D, 0x31]; // "TLM1"
    public const byte ExpectedVersion = 1;

    public void WriteTo(Span<byte> dest)
    {
        ExpectedMagic.CopyTo(dest[..4]);
        dest[4] = ExpectedVersion;
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(5, 2), DeviceId);
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(7, 4), PacketSeq);
        Salt.AsSpan(0, 16).CopyTo(dest.Slice(11, 16));
        Nonce.AsSpan(0, 12).CopyTo(dest.Slice(27, 12));
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(39, 4), CiphertextLen);
    }

    public static (RejectionCode code, PacketHeader? header) TryRead(ReadOnlySpan<byte> source)
    {
        if (source.Length < SizeInBytes)
            return (RejectionCode.E_SHORT, null);

        if (!source[..4].SequenceEqual(ExpectedMagic) || source[4] != ExpectedVersion)
            return (RejectionCode.E_MAGIC, null);

        var deviceId = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(5, 2));
        var seq = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(7, 4));
        var salt = source.Slice(11, 16).ToArray();
        var nonce = source.Slice(27, 12).ToArray();
        var ctLen = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(39, 4));

        return (RejectionCode.None, new PacketHeader(deviceId, seq, salt, nonce, ctLen));
    }
}