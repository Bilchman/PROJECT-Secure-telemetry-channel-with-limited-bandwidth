namespace Core;

using System.Collections.Generic;
using System.Linq;

public sealed class PriorityScheduler : IScheduler
{
    public string Name => "priority";

    public IReadOnlyList<Packet> OrderPackets(IReadOnlyList<Packet> packets)
    {
        return packets
            .OrderByDescending(p => p.Bytes.Length > 0 ? (double)p.Value / p.Bytes.Length : 0.0)
            .ThenByDescending(p => p.Value)
            .ToList();
    }
}