namespace Core;

public sealed record Packet(
    uint Sequence,
    byte[] Bytes,
    IReadOnlyList<int> FrameIndexes,
    int Value
);