namespace Core;

public interface IScheduler
{
    string Name { get; }
    IReadOnlyList<Packet> OrderPackets(IReadOnlyList<Packet> packets);
}