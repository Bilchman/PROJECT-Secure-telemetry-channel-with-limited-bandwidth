namespace Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core;
using Xunit;

public class PackingTests
{
    [Fact]
    public void Case08_DP_Knapsack_Gives_Value_Not_Less_Than_Greedy()
    {
        var dpPacker = new KnapsackPacker();
        var greedyPacker = new GreedyPacker();

        var candidates = new List<FrameCandidate>
        {
            new FrameCandidate(0, 30, 10),
            new FrameCandidate(1, 45, 25),
            new FrameCandidate(2, 60, 30),
            new FrameCandidate(3, 20, 12),
            new FrameCandidate(4, 50, 28),
            new FrameCandidate(5, 35, 20)
        };

        int[] capacities = new int[] { 50, 75, 100, 120, 150 };

        foreach (int cap in capacities)
        {
            var dpIndices = dpPacker.SelectFrames(candidates, cap);
            var greedyIndices = greedyPacker.SelectFrames(candidates, cap);

            int dpValue = dpIndices.Sum(idx => candidates.First(c => c.FrameIndex == idx).Value);
            int greedyValue = greedyIndices.Sum(idx => candidates.First(c => c.FrameIndex == idx).Value);

            Assert.True(dpValue >= greedyValue);
        }
    }

    [Fact]
    public void Knapsack_Does_Not_Exceed_Capacity()
    {
        var packer = new KnapsackPacker();
        var candidates = new List<FrameCandidate>
        {
            new FrameCandidate(0, 100, 15),
            new FrameCandidate(1, 150, 25),
            new FrameCandidate(2, 200, 40),
            new FrameCandidate(3, 80, 10),
            new FrameCandidate(4, 120, 30)
        };

        int capacity = 300;
        var selected = packer.SelectFrames(candidates, capacity);

        int totalWeight = selected.Sum(idx => candidates.First(c => c.FrameIndex == idx).WeightBits);
        Assert.True(totalWeight <= capacity);
    }

    [Fact]
    public void Knapsack_Handles_Empty_And_Zero_Capacity()
    {
        var packer = new KnapsackPacker();

        var emptyList = new List<FrameCandidate>();
        Assert.Empty(packer.SelectFrames(emptyList, 100));

        var candidates = new List<FrameCandidate> { new FrameCandidate(0, 50, 10) };
        Assert.Empty(packer.SelectFrames(candidates, 0));
        Assert.Empty(packer.SelectFrames(candidates, -10));
    }

    [Fact]
    public void Greedy_TieBreaking_Is_Deterministic()
    {
        var greedyPacker = new GreedyPacker();

        var candidates = new List<FrameCandidate>
        {
            new FrameCandidate(10, 40, 40),
            new FrameCandidate(5, 40, 40)
        };

        var selected = greedyPacker.SelectFrames(candidates, 40);

        Assert.Single(selected);
        Assert.Equal(5, selected[0]);
    }

    [Fact]
    public void DP_Matches_BruteForce_On_100_Random_Small_Datasets()
    {
        var dpPacker = new KnapsackPacker();
        var rng = new Random(42);

        for (int test = 0; test < 100; test++)
        {
            int n = rng.Next(5, 13);
            int capacity = rng.Next(50, 250);

            var candidates = new List<FrameCandidate>(n);
            for (int i = 0; i < n; i++)
            {
                candidates.Add(new FrameCandidate(i, rng.Next(10, 60), rng.Next(5, 100)));
            }

            var dpSelected = dpPacker.SelectFrames(candidates, capacity);
            int dpTotalWeight = dpSelected.Sum(idx => candidates[idx].WeightBits);
            int dpTotalValue = dpSelected.Sum(idx => candidates[idx].Value);

            Assert.True(dpTotalWeight <= capacity);

            int bruteForceBestValue = 0;
            int totalSubsets = 1 << n;

            for (int mask = 0; mask < totalSubsets; mask++)
            {
                int currentWeight = 0;
                int currentValue = 0;
                bool valid = true;

                for (int bit = 0; bit < n; bit++)
                {
                    if ((mask & (1 << bit)) != 0)
                    {
                        currentWeight += candidates[bit].WeightBits;
                        if (currentWeight > capacity)
                        {
                            valid = false;
                            break;
                        }
                        currentValue += candidates[bit].Value;
                    }
                }

                if (valid && currentValue > bruteForceBestValue)
                {
                    bruteForceBestValue = currentValue;
                }
            }

            Assert.Equal(bruteForceBestValue, dpTotalValue);
        }
    }

    [Fact]
    public void DP_Finds_Better_Solution_Than_Greedy_When_Greedy_Is_Suboptimal()
    {
        var dpPacker = new KnapsackPacker();
        var greedyPacker = new GreedyPacker();

        var candidates = new List<FrameCandidate>
        {
            new FrameCandidate(0, 60, 60),
            new FrameCandidate(1, 50, 49),
            new FrameCandidate(2, 50, 49)
        };

        int capacity = 100;

        var greedySelected = greedyPacker.SelectFrames(candidates, capacity);
        int greedyValue = greedySelected.Sum(idx => candidates.First(c => c.FrameIndex == idx).Value);

        var dpSelected = dpPacker.SelectFrames(candidates, capacity);
        int dpValue = dpSelected.Sum(idx => candidates.First(c => c.FrameIndex == idx).Value);

        Assert.Equal(60, greedyValue);
        Assert.Equal(98, dpValue);
        Assert.True(dpValue > greedyValue);
    }

    [Fact]
    public void DP_Achieves_Optimal_Exact_Fit_When_Greedy_Leaves_Slack()
    {
        var dpPacker = new KnapsackPacker();
        var greedyPacker = new GreedyPacker();

        var candidates = new List<FrameCandidate>
        {
            new FrameCandidate(0, 51, 52),
            new FrameCandidate(1, 50, 50),
            new FrameCandidate(2, 50, 50)
        };

        int capacity = 100;

        var greedySelected = greedyPacker.SelectFrames(candidates, capacity);
        var dpSelected = dpPacker.SelectFrames(candidates, capacity);

        int greedyValue = greedySelected.Sum(idx => candidates.First(c => c.FrameIndex == idx).Value);
        int dpValue = dpSelected.Sum(idx => candidates.First(c => c.FrameIndex == idx).Value);

        Assert.Equal(52, greedyValue);
        Assert.Equal(100, dpValue);
        Assert.True(dpValue > greedyValue);
    }

    [Fact]
    public void DP_Outperforms_Greedy_On_Tight_Capacity_And_Diverse_Ratios()
    {
        var dpPacker = new KnapsackPacker();
        var greedyPacker = new GreedyPacker();
        var rng = new Random(100);

        int totalDpWins = 0;
        int capacity = 180;

        for (int s = 0; s < 30; s++)
        {
            var candidates = new List<FrameCandidate>(20);
            for (int i = 0; i < 20; i++)
            {
                candidates.Add(new FrameCandidate(i, rng.Next(30, 90), rng.Next(20, 150)));
            }

            var dpSel = dpPacker.SelectFrames(candidates, capacity);
            var greedySel = greedyPacker.SelectFrames(candidates, capacity);

            int dpVal = dpSel.Sum(idx => candidates[idx].Value);
            int greedyVal = greedySel.Sum(idx => candidates[idx].Value);

            Assert.True(dpVal >= greedyVal);

            if (dpVal > greedyVal)
            {
                totalDpWins++;
            }
        }

        Assert.True(totalDpWins > 0);
    }

    [Fact]
    public void Convergence_Test_Greedy_Approaches_DP_As_Capacity_Increases()
    {
        var dpPacker = new KnapsackPacker();
        var greedyPacker = new GreedyPacker();
        var rng = new Random(777);

        var candidates = new List<FrameCandidate>(40);
        for (int i = 0; i < 40; i++)
        {
            candidates.Add(new FrameCandidate(i, rng.Next(40, 80), rng.Next(10, 100)));
        }

        int dpTight = dpPacker.SelectFrames(candidates, 120).Sum(idx => candidates[idx].Value);
        int greedyTight = greedyPacker.SelectFrames(candidates, 120).Sum(idx => candidates[idx].Value);
        double diffTight = (double)(dpTight - greedyTight) / dpTight * 100.0;

        int dpLarge = dpPacker.SelectFrames(candidates, 1500).Sum(idx => candidates[idx].Value);
        int greedyLarge = greedyPacker.SelectFrames(candidates, 1500).Sum(idx => candidates[idx].Value);
        double diffLarge = (double)(dpLarge - greedyLarge) / dpLarge * 100.0;

        Assert.True(diffTight >= diffLarge);
    }
}
