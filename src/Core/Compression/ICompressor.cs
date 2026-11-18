namespace Core;

public interface ICompressor
{
    string Name { get; }

    // Будує таблицю частот і канонічних довжин
    CodeTable BuildTable(ReadOnlySpan<byte> data);

    // Кодує байти у потік MSB-first
    byte[] Compress(ReadOnlySpan<byte> data, CodeTable table);

    // Декодує рівно symbolCount символів
    byte[] Decompress(ReadOnlySpan<byte> bitstream, CodeTable table, int symbolCount);
}