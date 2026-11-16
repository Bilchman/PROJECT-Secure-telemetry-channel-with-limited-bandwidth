namespace Core;

public sealed class GreedyPacker : IPacker
{
    public string Name => "greedy";

    public IReadOnlyList<int> SelectFrames(IReadOnlyList<FrameCandidate> candidates, int capacityBits)
    {
        if (capacityBits <= 0 || candidates.Count == 0)
        {
            return new List<int>();
        }

        var items = new List<FrameCandidate>(candidates);
        items.Sort((a, b) =>
        {
            double ratioA = 0;
            if (a.WeightBits > 0)
            {
                ratioA = (double)a.Value / a.WeightBits;
            }

            double ratioB = 0;
            if (b.WeightBits > 0)
            {
                ratioB = (double)b.Value / b.WeightBits;
            }

            if (ratioA > ratioB) return -1;
            if (ratioA < ratioB) return 1;

            if (a.Value > b.Value) return -1;
            if (a.Value < b.Value) return 1;

            return a.FrameIndex.CompareTo(b.FrameIndex);
        });

        var selected = new List<int>();
        int accumulatedBits = 0;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (accumulatedBits + item.WeightBits <= capacityBits)
            {
                selected.Add(item.FrameIndex);
                accumulatedBits += item.WeightBits;
            }
        }

        selected.Sort();
        return selected;
    }
}
