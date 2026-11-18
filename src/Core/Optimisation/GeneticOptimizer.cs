using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Core;

public record GenerationRecord(
    int Generation,
    double BestFitness,
    double AvgFitness,
    SystemParameters BestParameters
);

public class GeneticOptimizer : IOptimizer
{
    private const int MinWindow = 16;
    private const int MaxWindow = 128;
    private const double MinThreshold = 0.70;
    private const double MaxThreshold = 1.15;
    private const double MinCriticalWeight = 0.5;
    private const double MaxCriticalWeight = 5.0;

    private readonly int _populationSize;
    private readonly int _tournamentSize;
    private readonly double _crossoverRate;
    private readonly double _mutationRate;
    private readonly int _elitismCount;
    private readonly Random _random;
    private readonly List<GenerationRecord> _history = new();

    public string Name => "ga";
    public IReadOnlyList<GenerationRecord> History => _history;

    public GeneticOptimizer(
        int populationSize = 10,
        int tournamentSize = 2,
        double crossoverRate = 0.85,
        double mutationRate = 0.35,
        int elitismCount = 1,
        int seed = 1001)
    {
        if (populationSize < 2) throw new ArgumentOutOfRangeException(nameof(populationSize));

        _populationSize = populationSize;
        _tournamentSize = Math.Clamp(tournamentSize, 2, populationSize);
        _crossoverRate = crossoverRate;
        _mutationRate = mutationRate;
        _elitismCount = Math.Clamp(elitismCount, 1, populationSize - 1);
        _random = new Random(seed);
    }

    public SystemParameters Optimize(Func<SystemParameters, double> evaluateFitness, int evaluationBudget)
    {
        ArgumentNullException.ThrowIfNull(evaluateFitness);
        if (evaluationBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(evaluationBudget));

        _history.Clear();
        int evals = 0;

        // випадкова початкова популяція
        var population = new List<(SystemParameters Params, double Fitness)>(_populationSize);
        for (int i = 0; i < _populationSize && evals < evaluationBudget; i++)
        {
            var p = CreateRandom();
            double f = evaluateFitness(p);
            evals++;
            population.Add((p, f));
        }

        int generation = 0;
        RecordGeneration(generation, population);

        // цикл еволюції
        while (evals < evaluationBudget)
        {
            generation++;
            population.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));

            var nextPopulation = new List<(SystemParameters Params, double Fitness)>(_populationSize);

            // найкращі хромосоми переводимо у наступне покоління без змін
            int elites = Math.Min(_elitismCount, population.Count);
            for (int i = 0; i < elites; i++)
            {
                nextPopulation.Add(population[i]);
            }

            // проводимо турнір з кросовером та мутаціями
            while (nextPopulation.Count < _populationSize && evals < evaluationBudget)
            {
                var parent1 = TournamentSelect(population);
                var parent2 = TournamentSelect(population);

                var child = Crossover(parent1, parent2);
                child = Mutate(child);

                double fitness = evaluateFitness(child);
                evals++;
                nextPopulation.Add((child, fitness));
            }

            population = nextPopulation;
            RecordGeneration(generation, population);
        }

        population.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));
        return population[0].Params;
    }

    private SystemParameters CreateRandom()
    {
        int w = _random.Next(MinWindow, MaxWindow + 1);
        double t = MinThreshold + _random.NextDouble() * (MaxThreshold - MinThreshold);
        double c = MinCriticalWeight + _random.NextDouble() * (MaxCriticalWeight - MinCriticalWeight);
        return new SystemParameters(w, t, c);
    }

    private SystemParameters TournamentSelect(List<(SystemParameters Params, double Fitness)> population)
    {
        var best = population[_random.Next(population.Count)];
        for (int i = 1; i < _tournamentSize; i++)
        {
            var challenger = population[_random.Next(population.Count)];
            if (challenger.Fitness > best.Fitness)
            {
                best = challenger;
            }
        }
        return best.Params;
    }

    // Рівномірний кросовер по 3 генах.
    private SystemParameters Crossover(SystemParameters p1, SystemParameters p2)
    {
        if (_random.NextDouble() >= _crossoverRate)
            return p1;

        var window = _random.Next(2) == 0 ? p1.WindowFrames : p2.WindowFrames;
        var threshold = _random.Next(2) == 0 ? p1.CompressionThreshold : p2.CompressionThreshold;
        var weight = _random.Next(2) == 0 ? p1.CriticalWeight : p2.CriticalWeight;

        return new SystemParameters(window, threshold, weight);
    }

    // Мутація випадковим зсувом кожного гена
    private SystemParameters Mutate(SystemParameters p)
    {
        int window = p.WindowFrames;
        double threshold = p.CompressionThreshold;
        double weight = p.CriticalWeight;

        if (_random.NextDouble() < _mutationRate)
            window = Math.Clamp(window + _random.Next(-12, 13), MinWindow, MaxWindow);

        if (_random.NextDouble() < _mutationRate)
            threshold = Math.Clamp(threshold + (_random.NextDouble() * 2.0 - 1.0) * 0.08, MinThreshold, MaxThreshold);

        if (_random.NextDouble() < _mutationRate)
            weight = Math.Clamp(weight + (_random.NextDouble() * 2.0 - 1.0) * 0.6, MinCriticalWeight, MaxCriticalWeight);

        return new SystemParameters(window, threshold, weight);
    }

    private void RecordGeneration(int generation, List<(SystemParameters Params, double Fitness)> population)
    {
        var best = population[0];
        double sum = 0.0;

        for (int i = 0; i < population.Count; i++)
        {
            sum += population[i].Fitness;
            if (population[i].Fitness > best.Fitness)
                best = population[i];
        }

        _history.Add(new GenerationRecord(generation, best.Fitness, sum / population.Count, best.Params));
    }

    public void SaveHistoryCsv(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(filePath);
        writer.WriteLine("generation,best_fitness,avg_fitness,window_frames,compression_threshold,critical_weight");

        foreach (var rec in _history)
        {
            writer.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1:F6},{2:F6},{3},{4:F4},{5:F4}",
                rec.Generation,
                rec.BestFitness,
                rec.AvgFitness,
                rec.BestParameters.WindowFrames,
                rec.BestParameters.CompressionThreshold,
                rec.BestParameters.CriticalWeight));
        }
    }
}