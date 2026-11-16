namespace Core;

public sealed class KnapsackPacker : IPacker
{
    public string Name => "dp-knapsack";

    public IReadOnlyList<int> SelectFrames(IReadOnlyList<FrameCandidate> candidates, int capacityBits)
    {
        if (capacityBits <= 0 || candidates.Count == 0)
        {
            return new List<int>();
        }

        int n = candidates.Count;
        int[] dp = new int[capacityBits + 1];
        bool[,] keep = new bool[n, capacityBits + 1];

        for (int i = 0; i < n; i++)
        {
            int w = candidates[i].WeightBits;
            int v = candidates[i].Value;

            if (w <= 0) continue;

            for (int c = capacityBits; c >= w; c--)
            {
                int valWithItem = dp[c - w] + v;
                if (valWithItem > dp[c])
                {
                    dp[c] = valWithItem;
                    keep[i, c] = true;
                }
            }
        }

        var selected = new List<int>();
        int currCap = capacityBits;

        for (int i = n - 1; i >= 0; i--)
        {
            if (keep[i, currCap])
            {
                selected.Add(candidates[i].FrameIndex);
                currCap -= candidates[i].WeightBits;
            }
        }

        selected.Reverse();
        return selected;
    }
}
