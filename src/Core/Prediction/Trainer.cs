namespace Core;

using Core.Prediction;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

// Результати експериментальної оцінки класифікатора за пунктами 4.12 та 4.13
public sealed class EvaluationMetrics
{
    public int[][] ConfusionMatrix { get; init; } = new int[3][];
    public double[] Precision { get; init; } = new double[3];
    public double[] Recall { get; init; } = new double[3];
    public double[] F1PerClass { get; init; } = new double[3];
    public double MacroF1 { get; init; }
    public int BestSeed { get; init; }
}


// Модуль тренування перцептрона, оцінки якості за Macro-F1 та експорту звітів у каталог results/.
public sealed class Trainer
{
    private readonly int _featureCount;
    private readonly int _hiddenSize;

    public Trainer(int featureCount = FeatureExtractor.FeatureCount, int hiddenSize = 16)
    {
        _featureCount = featureCount;
        _hiddenSize = hiddenSize;
    }

    
    // Розбиває вхідні дані на навчальну та валідаційну частини (пункт 4.12)
    public static (List<(double[] Features, int Label)> Train, List<(double[] Features, int Label)> Validation)
        SplitData(IReadOnlyList<(double[] Features, int Label)> dataset, double trainRatio = 0.8, int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var shuffled = new List<(double[] Features, int Label)>(dataset);

        // Детерміноване перемішування Фішера-Єйтса
        var rng = new Random(seed);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        int trainCount = (int)Math.Round(shuffled.Count * trainRatio);
        var train = shuffled.GetRange(0, trainCount);
        var validation = shuffled.GetRange(trainCount, shuffled.Count - trainCount);

        return (train, validation);
    }

    // Оцінює роботу перцептрона на вибірці - будує матрицю помилок 3x3 та рахує незважений Macro-F1, у разі нульової суми Precision + Recall класу призначається F1 = 0.0.
    public static EvaluationMetrics Evaluate(MultilayerPerceptron model, IReadOnlyList<(double[] Features, int Label)> validationData, int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(validationData);

        var cm = new int[3][]
        {
            new int[3],
            new int[3],
            new int[3]
        };

        foreach (var sample in validationData)
        {
            double[] probs = model.Forward(sample.Features);
            int predicted = 0;
            double maxP = probs[0];
            for (int c = 1; c < probs.Length; c++)
            {
                if (probs[c] > maxP)
                {
                    maxP = probs[c];
                    predicted = c;
                }
            }

            cm[sample.Label][predicted]++;
        }

        var precision = new double[3];
        var recall = new double[3];
        var f1PerClass = new double[3];
        double sumF1 = 0.0;

        for (int c = 0; c < 3; c++)
        {
            int tp = cm[c][c];
            int actualTotal = cm[c][0] + cm[c][1] + cm[c][2];
            int predictedTotal = cm[0][c] + cm[1][c] + cm[2][c];

            precision[c] = predictedTotal > 0 ? (double)tp / predictedTotal : 0.0;
            recall[c] = actualTotal > 0 ? (double)tp / actualTotal : 0.0;

            if (precision[c] + recall[c] > 1e-12)
            {
                f1PerClass[c] = 2.0 * precision[c] * recall[c] / (precision[c] + recall[c]);
            }
            else
            {
                f1PerClass[c] = 0.0; // Вимога пункту 4.13
            }

            sumF1 += f1PerClass[c];
        }

        return new EvaluationMetrics
        {
            ConfusionMatrix = cm,
            Precision = precision,
            Recall = recall,
            F1PerClass = f1PerClass,
            MacroF1 = sumF1 / 3.0,
            BestSeed = seed
        };
    }

    // Протокол 3-х незалежних ініціалізацій (пункти 4.12, 9.24). Обирає найкращу модель за Macro-F1.
    public (MultilayerPerceptron BestModel, EvaluationMetrics BestMetrics) TrainThreeSeeds(
        IReadOnlyList<(double[] Features, int Label)> trainData,
        IReadOnlyList<(double[] Features, int Label)> valData,
        int[] seeds,
        int epochs = 35,
        double lr = 0.05,
        double momentum = 0.85)
    {
        MultilayerPerceptron? bestModel = null;
        EvaluationMetrics? bestMetrics = null;

        foreach (int seed in seeds)
        {
            var model = new MultilayerPerceptron(_featureCount, _hiddenSize, outputSize: 3, seed: seed);

            for (int epoch = 0; epoch < epochs; epoch++)
            {
                foreach (var sample in trainData)
                {
                    model.TrainStep(sample.Features, sample.Label, lr, momentum);
                }
            }

            var metrics = Evaluate(model, valData, seed);

            if (bestMetrics == null || metrics.MacroF1 > bestMetrics.MacroF1)
            {
                bestMetrics = metrics;
                bestModel = model;
            }
        }

        return (bestModel!, bestMetrics!);
    }

    // Зберігає машинні артефакти експерименту у файли results/classifier_metrics.json та results/confusion_matrix.csv.
    public static void SaveArtifacts(string resultsDir, EvaluationMetrics metrics)
    {
        Directory.CreateDirectory(resultsDir);

        // Збереження confusion_matrix.csv
        string csvPath = Path.Combine(resultsDir, "confusion_matrix.csv");
        using (var writer = new StreamWriter(csvPath))
        {
            writer.WriteLine("Actual\\Predicted,Class_0_Normal,Class_1_Anomaly,Class_2_Transient");
            string[] classNames = { "Class_0_Normal", "Class_1_Anomaly", "Class_2_Transient" };
            for (int r = 0; r < 3; r++)
            {
                writer.WriteLine($"{classNames[r]},{metrics.ConfusionMatrix[r][0]},{metrics.ConfusionMatrix[r][1]},{metrics.ConfusionMatrix[r][2]}");
            }
        }

        // Збереження classifier_metrics.json
        string jsonPath = Path.Combine(resultsDir, "classifier_metrics.json");
        var report = new
        {
            selected_seed = metrics.BestSeed,
            macro_f1 = Math.Round(metrics.MacroF1, 4),
            classes = new
            {
                normal = new { precision = Math.Round(metrics.Precision[0], 4), recall = Math.Round(metrics.Recall[0], 4), f1 = Math.Round(metrics.F1PerClass[0], 4) },
                anomaly = new { precision = Math.Round(metrics.Precision[1], 4), recall = Math.Round(metrics.Recall[1], 4), f1 = Math.Round(metrics.F1PerClass[1], 4) },
                transient = new { precision = Math.Round(metrics.Precision[2], 4), recall = Math.Round(metrics.Recall[2], 4), f1 = Math.Round(metrics.F1PerClass[2], 4) }
            },
            confusion_matrix = metrics.ConfusionMatrix
        };

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
    }
}