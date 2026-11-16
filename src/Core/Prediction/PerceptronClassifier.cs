namespace Core;
using Core.Prediction;
using System;
using System.Collections.Generic;

// Класифікатор потоку телеметрії на базі багатошарового перцептрона. Реалізує публічний контракт IPredictor та інтегрує класифікацію у задачу пакування рюкзака (пункти 9.22, 9.26).
public sealed class PerceptronClassifier : IPredictor
{
    private readonly MultilayerPerceptron _mlp;
    private readonly FeatureExtractor _extractor;

    public string Name => "perceptron";

    public PerceptronClassifier(MultilayerPerceptron mlp, FeatureExtractor? extractor = null)
    {
        _mlp = mlp ?? throw new ArgumentNullException(nameof(mlp));
        _extractor = extractor ?? new FeatureExtractor();
    }

    // Класифікує потік кадрів за допомогою ковзного вікна історії k=8 та Softmax-інференсу перцептрона.
    public FrameClass[] Classify(IReadOnlyList<TelemetryFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        var result = new FrameClass[frames.Count];
        var history = new List<TelemetryFrame>();

        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            double[] features = _extractor.ExtractFeatures(history, frame);
            double[] probs = _mlp.Forward(features);

            // Знаходження найбільш імовірного класу
            int bestClass = 0;
            double maxP = probs[0];
            for (int c = 1; c < probs.Length; c++)
            {
                if (probs[c] > maxP)
                {
                    maxP = probs[c];
                    bestClass = c;
                }
            }

            result[i] = (FrameClass)bestClass;

            history.Add(frame);
            if (history.Count > FeatureExtractor.DefaultWindowSize)
            {
                history.RemoveAt(0);
            }
        }

        return result;
    }

    /// Розрахунок цінності кадру для рюкзака та планувальника черги    
    public static int CalculateFrameValue(FrameClass frameClass, byte critical, IReadOnlyDictionary<FrameClass, int> priorities, int criticalBonus)
    {
        ArgumentNullException.ThrowIfNull(priorities);

        int basePriority = priorities.TryGetValue(frameClass, out int p) ? p : 10;
        int bonus = (critical == 1) ? criticalBonus : 0;

        return basePriority + bonus;
    }

    // Базовий рівень. Залишки не кодуються, кадри передаються в оригіналі
    public TelemetryFrame PredictResidual(IReadOnlyList<TelemetryFrame> history, TelemetryFrame actual) => actual;
    public TelemetryFrame RestoreResidual(IReadOnlyList<TelemetryFrame> history, TelemetryFrame residual) => residual;
}