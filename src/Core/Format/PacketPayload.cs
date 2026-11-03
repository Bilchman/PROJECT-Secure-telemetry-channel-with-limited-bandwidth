namespace Core;

using System.Buffers.Binary;

public sealed record PacketPayload(
    ushort FrameCount,
    byte Flags,
    CodeTable Table,
    uint SymbolCount,
    byte[] Bitstream,
    byte[]? ClassTable = null)
{
    public const int HeaderSize = 14;

    public byte[] Serialize()
    {
        int tableSize = Table.Entries.Count * 2;
        int totalSize = HeaderSize + tableSize + Bitstream.Length + (ClassTable?.Length ?? 0);
        var dest = new byte[totalSize];
        var span = dest.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(0, 2), FrameCount);
        span[2] = Flags;
        span[3] = 0; // reserved = 0 обов'язково
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(4, 2), (ushort)Table.Entries.Count);

        int offset = 6;
        foreach (var (sym, len) in Table.Entries)
        {
            span[offset++] = sym;
            span[offset++] = len;
        }

        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), SymbolCount);
        offset += 4;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), (uint)Bitstream.Length);
        offset += 4;

        Bitstream.CopyTo(span.Slice(offset, Bitstream.Length));
        offset += Bitstream.Length;

        ClassTable?.CopyTo(span.Slice(offset, ClassTable.Length));

        return dest;
    }

    public static (RejectionCode code, PacketPayload? payload) TryRead(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
            return (RejectionCode.E_PAYLOAD, null);

        var frameCount = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(0, 2));
        var flags = source[2];
        var reserved = source[3];
        if (reserved != 0)
            return (RejectionCode.E_PAYLOAD, null);

        var tableNsym = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(4, 2));
        int expectedTableBytes = tableNsym * 2;

        if (source.Length < HeaderSize + expectedTableBytes)
            return (RejectionCode.E_PAYLOAD, null);

        var entries = new List<(byte Symbol, byte Length)>(tableNsym);
        int offset = 6;
        for (int i = 0; i < tableNsym; i++)
        {
            byte sym = source[offset++];
            byte len = source[offset++];
            if (len > 15) return (RejectionCode.E_CODE, null);
            entries.Add((sym, len));
        }

        var symbolCount = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(offset, 4));
        offset += 4;

        if (symbolCount != 12 * frameCount)
            return (RejectionCode.E_PAYLOAD, null);

        var bitstreamLen = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(offset, 4));
        offset += 4;

        if (source.Length < offset + bitstreamLen)
            return (RejectionCode.E_PAYLOAD, null);

        var bitstream = source.Slice(offset, (int)bitstreamLen).ToArray();
        offset += (int)bitstreamLen;

        byte[]? classTable = null;
        if ((flags & 0x04) != 0)
        {
            if (source.Length < offset + frameCount)
                return (RejectionCode.E_PAYLOAD, null);
            classTable = source.Slice(offset, frameCount).ToArray();
        }

        var table = new CodeTable { Entries = entries };
        return (RejectionCode.None, new PacketPayload(frameCount, flags, table, symbolCount, bitstream, classTable));
    }
}