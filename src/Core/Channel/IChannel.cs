namespace Core;

public readonly record struct ChannelResult(bool Delivered, double ArrivalTimeSec, bool DroppedByBudget);

public interface IChannel
{
    ChannelResult Send(ReadOnlySpan<byte> packetBytes);
    Dictionary<string, object> GetStats();
}