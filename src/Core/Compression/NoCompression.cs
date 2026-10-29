namespace Core;

public sealed class NoCompression : ICompressor
{
    public string Name => "none";

    public CodeTable BuildTable(ReadOnlySpan<byte> data) => new() { Entries = [] };
    public byte[] Compress(ReadOnlySpan<byte> data, CodeTable table) => data.ToArray();
    public byte[] Decompress(ReadOnlySpan<byte> bitstream, CodeTable table, int symbolCount) => bitstream.ToArray();
}