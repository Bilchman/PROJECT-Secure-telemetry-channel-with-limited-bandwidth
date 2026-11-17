using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Core;

public class RandomSearchOptimizer : IOptimizer
{
    private const int MinWindow = 16;
    private const int MaxWindow = 128;
    private const double MinThreshold = 0.70;
    private const double MaxThreshold = 1.15;
    private const double MinCriticalWeight = 0.5;
    private const double MaxCriticalWeight = 5.0;

    private readonly Random _random;
    private readonly List<(int Step, double BestFitness, double CurrentFitness)> _history = new();

    public string Name => "random-search";
    public IReadOnlyList<(int Step, double BestFitness, double CurrentFitness)> History => _history;

    public RandomSearchOptimizer(int seed = 1001)
    {
        _random = new Random(seed);
    }

    public SystemParameters Optimize(Func<SystemParameters, double> evaluateFitness, int evaluationBudget)
    {
        ArgumentNullException.ThrowIfNull(evaluateFitness);
        if (evaluationBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(evaluationBudget));

        _history.Clear();

        SystemParameters bestParams = default!;
        double bestFitness = double.NegativeInfinity;

        for (int step = 1; step <= evaluationBudget; step++)
        {
            var candidate = new SystemParameters(
                WindowFrames: _random.Next(MinWindow, MaxWindow + 1),
                CompressionThreshold: MinThreshold + _random.NextDouble() * (MaxThreshold - MinThreshold),
                CriticalWeight: MinCriticalWeight + _random.NextDouble() * (MaxCriticalWeight - MinCriticalWeight)
            );

            double fitness = evaluateFitness(candidate);
            if (fitness > bestFitness)
            {
                bestFitness = fitness;
                bestParams = candidate;
            }

            _history.Add((step, bestFitness, fitness));
        }

        return bestParams;
    }

    public void SaveHistoryCsv(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(filePath);
        writer.WriteLine("step,best_fitness,current_fitness");

        foreach (var item in _history)
        {
            writer.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1:F6},{2:F6}",
                item.Step,
                item.BestFitness,
                item.CurrentFitness));
        }
    }
}