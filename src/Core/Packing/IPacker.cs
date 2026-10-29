namespace Core;

public interface IPacker
{
    string Name { get; }

    // W = 8 * (max_payload_bytes - 14 - 2 * table_nsym)
    IReadOnlyList<int> SelectFrames(IReadOnlyList<FrameCandidate> candidates, int capacityBits);
}