namespace Core;

public sealed class CodeTable
{
    public required IReadOnlyList<(byte Symbol, byte Length)> Entries { get; init; }
    public int SymbolCount => Entries.Count;
}