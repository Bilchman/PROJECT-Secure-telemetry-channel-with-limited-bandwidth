namespace Core;

public sealed class SequentialPacker : IPacker
{
    public string Name => "sequential";

    public IReadOnlyList<int> SelectFrames(IReadOnlyList<FrameCandidate> candidates, int capacityBits)
    {
        var selected = new List<int>();
        int accumulatedBits = 0;

        foreach (var c in candidates)
        {
            if (accumulatedBits + c.WeightBits <= capacityBits)
            {
                selected.Add(c.FrameIndex);
                accumulatedBits += c.WeightBits;
            }
        }
        return selected;
    }
}