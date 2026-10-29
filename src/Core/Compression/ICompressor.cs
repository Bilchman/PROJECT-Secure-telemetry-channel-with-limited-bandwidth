namespace Core;

public interface ICompressor
{
    string Name { get; }

    // Будує таблицю частот і канонічних довжин (максимум 15 біт!)
    CodeTable BuildTable(ReadOnlySpan<byte> data);

    // Кодує байти у потік MSB-first
    byte[] Compress(ReadOnlySpan<byte> data, CodeTable table);

    // Декодує рівно symbolCount символів (завжди 12 * frame_count)
    byte[] Decompress(ReadOnlySpan<byte> bitstream, CodeTable table, int symbolCount);
}