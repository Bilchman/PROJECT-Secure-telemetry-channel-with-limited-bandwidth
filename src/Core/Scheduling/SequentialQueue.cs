namespace Core;

public sealed class SequentialQueue : IScheduler
{
    public string Name => "sequential";

    public IReadOnlyList<Packet> OrderPackets(IReadOnlyList<Packet> packets) => packets;
}