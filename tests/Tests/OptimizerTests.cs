using System;
using System.IO;
using Core;
using Xunit;

namespace Tests;

public sealed class OptimizerTests
{
    private static double UnimodalTestFitness(SystemParameters p)
    {
        // Штучна гладка функція з максимумом 1.0 у точці (48, 0.95, 2.5)
        double dw = (p.WindowFrames - 48) / 50.0;
        double dt = p.CompressionThreshold - 0.95;
        double dc = (p.CriticalWeight - 2.5) / 3.0;
        return 1.0 - (dw * dw + dt * dt + dc * dc);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(42)]
    [InlineData(777)]
    public void GeneticOptimizer_Elitism_LastGenerationNotWorseThanGenerationZero(int seed)
    {
        // через елітизм результат останнього покоління не може бути гіршим за покоління 0
        var ga = new GeneticOptimizer(populationSize: 10, tournamentSize: 2, mutationRate: 0.35, seed: seed);

        ga.Optimize(UnimodalTestFitness, evaluationBudget: 60);

        Assert.NotEmpty(ga.History);
        double gen0Best = ga.History[0].BestFitness;
        double lastGenBest = ga.History[^1].BestFitness;

        Assert.True(lastGenBest >= gen0Best,
            $"Порушено інваріант елітизму: Gen0={gen0Best:F6}, LastGen={lastGenBest:F6}");

        // Перевірка монотонності на кожному кроці еволюції
        for (int i = 1; i < ga.History.Count; i++)
        {
            Assert.True(ga.History[i].BestFitness >= ga.History[i - 1].BestFitness,
                $"Погіршення в поколінні {i}: {ga.History[i].BestFitness} < {ga.History[i - 1].BestFitness}");
        }
    }

    [Theory]
    [InlineData(50)]
    [InlineData(37)] // Некратний розміру популяції бюджет
    public void Optimizers_UseExactEvaluationBudget(int budget)
    {
        int gaCalls = 0;
        var ga = new GeneticOptimizer(populationSize: 10, seed: 1001);
        ga.Optimize(p =>
        {
            gaCalls++;
            return UnimodalTestFitness(p);
        }, budget);

        int rsCalls = 0;
        var rs = new RandomSearchOptimizer(seed: 1001);
        rs.Optimize(p =>
        {
            rsCalls++;
            return UnimodalTestFitness(p);
        }, budget);

        Assert.Equal(budget, gaCalls);
        Assert.Equal(budget, rsCalls);
    }

    [Fact]
    public void Optimizers_AlwaysStayWithinDefinedParameterBounds()
    {
        var ga = new GeneticOptimizer(populationSize: 10, mutationRate: 1.0, seed: 123);
        var rs = new RandomSearchOptimizer(seed: 123);

        void AssertBounds(SystemParameters p)
        {
            Assert.InRange(p.WindowFrames, 16, 128);
            Assert.InRange(p.CompressionThreshold, 0.70, 1.15);
            Assert.InRange(p.CriticalWeight, 0.5, 5.0);
        }

        var bestGa = ga.Optimize(p =>
        {
            AssertBounds(p);
            return UnimodalTestFitness(p);
        }, evaluationBudget: 50);

        var bestRs = rs.Optimize(p =>
        {
            AssertBounds(p);
            return UnimodalTestFitness(p);
        }, evaluationBudget: 50);

        AssertBounds(bestGa);
        AssertBounds(bestRs);
    }

    [Fact]
    public void GeneticOptimizer_SameSeed_ProducesIdenticalResultsAndHistory()
    {
        var ga1 = new GeneticOptimizer(populationSize: 10, tournamentSize: 2, mutationRate: 0.35, seed: 1001);
        var ga2 = new GeneticOptimizer(populationSize: 10, tournamentSize: 2, mutationRate: 0.35, seed: 1001);

        var res1 = ga1.Optimize(UnimodalTestFitness, evaluationBudget: 50);
        var res2 = ga2.Optimize(UnimodalTestFitness, evaluationBudget: 50);

        Assert.Equal(res1, res2);
        Assert.Equal(ga1.History.Count, ga2.History.Count);
        for (int i = 0; i < ga1.History.Count; i++)
        {
            Assert.Equal(ga1.History[i].BestFitness, ga2.History[i].BestFitness);
            Assert.Equal(ga1.History[i].AvgFitness, ga2.History[i].AvgFitness);
        }
    }

    [Fact]
    public void GeneticOptimizer_UnimodalFunction_ImprovesAverageFitnessAndConverges()
    {
        var ga = new GeneticOptimizer(populationSize: 12, tournamentSize: 2, mutationRate: 0.35, seed: 42);
        var best = ga.Optimize(UnimodalTestFitness, evaluationBudget: 120);

        double initialAvg = ga.History[0].AvgFitness;
        double finalAvg = ga.History[^1].AvgFitness;

        Assert.True(finalAvg > initialAvg, $"Середня пристосованість не зросла: {initialAvg:F4} -> {finalAvg:F4}");
        Assert.True(UnimodalTestFitness(best) > 0.98, $"ГА не зійшовся достатньо близько до оптимуму: {UnimodalTestFitness(best):F4}");
    }

    [Fact]
    public void SaveHistoryCsv_WritesValidEvolutionLogs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "TelemetryLinkOptimizerTests", Guid.NewGuid().ToString("N"));
        try
        {
            string gaCsv = Path.Combine(tempDir, "ga_evolution.csv");
            string rsCsv = Path.Combine(tempDir, "random_search_history.csv");

            var ga = new GeneticOptimizer(populationSize: 8, seed: 1001);
            ga.Optimize(UnimodalTestFitness, evaluationBudget: 24);
            ga.SaveHistoryCsv(gaCsv);

            var rs = new RandomSearchOptimizer(seed: 1001);
            rs.Optimize(UnimodalTestFitness, evaluationBudget: 24);
            rs.SaveHistoryCsv(rsCsv);

            Assert.True(File.Exists(gaCsv));
            Assert.True(File.Exists(rsCsv));

            var gaLines = File.ReadAllLines(gaCsv);
            var rsLines = File.ReadAllLines(rsCsv);

            Assert.Equal("generation,best_fitness,avg_fitness,window_frames,compression_threshold,critical_weight", gaLines[0]);
            Assert.Equal(ga.History.Count + 1, gaLines.Length);
            Assert.Equal("step,best_fitness,current_fitness", rsLines[0]);
            Assert.Equal(25, rsLines.Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Optimizers_InvalidBudget_ThrowsArgumentOutOfRangeException(int invalidBudget)
    {
        var ga = new GeneticOptimizer();
        var rs = new RandomSearchOptimizer();

        Assert.Throws<ArgumentOutOfRangeException>(() => ga.Optimize(UnimodalTestFitness, invalidBudget));
        Assert.Throws<ArgumentOutOfRangeException>(() => rs.Optimize(UnimodalTestFitness, invalidBudget));
    }
}