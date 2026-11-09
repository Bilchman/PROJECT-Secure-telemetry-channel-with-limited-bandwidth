namespace Core;

public sealed class PriorityScheduler : IScheduler
{
    public string Name => "priority";

    public IReadOnlyList<Packet> OrderPackets(IReadOnlyList<Packet> packets)
    {
        return packets.OrderByDescending(p => p.Value).ToList();
    }
}
