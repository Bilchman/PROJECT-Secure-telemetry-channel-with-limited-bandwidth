namespace Core.Prediction;

using System;
using System.Collections.Generic;
using Core;


// Генератор часових ознак телеметрії з ковзним вікном історії.
public sealed class FeatureExtractor
{
    public const int DefaultWindowSize = 8;
    public const int FeatureCount = 12;

    private const double AltitudeScale = 1000.0;
    private const double TempScale = 100.0;
    private const double VibrationScale = 50.0;
    private const double ModeMax = 7.0;

    private readonly int _windowSize;

    public FeatureExtractor(int windowSize = DefaultWindowSize)
    {
        _windowSize = windowSize > 0 ? windowSize : DefaultWindowSize;
    }

    public double[] ExtractFeatures(IReadOnlyList<TelemetryFrame> history, TelemetryFrame current)
    {
        var features = new double[FeatureCount];
        int historyCount = history.Count;

        // Поточні нормалізовані значення сигналів
        features[0] = current.AltitudeDm / AltitudeScale;
        features[1] = current.TempC10 / TempScale;
        features[2] = current.Vibration / VibrationScale;
        features[3] = Math.Abs(current.Vibration) / VibrationScale;
        features[4] = current.Mode / ModeMax;
        features[5] = current.Critical == 1 ? 1.0 : 0.0;

        // Дельти між сусідніми кадрами
        if (historyCount > 0)
        {
            TelemetryFrame prev = history[historyCount - 1];
            features[6] = (current.AltitudeDm - prev.AltitudeDm) / AltitudeScale;
            features[7] = (current.TempC10 - prev.TempC10) / TempScale;
            features[8] = (current.Vibration - prev.Vibration) / VibrationScale;
        }

        // Віконні статистичні параметри (ковзні відхилення)
        int windowStart = Math.Max(0, historyCount - _windowSize);
        int windowLength = historyCount - windowStart;

        if (windowLength > 0)
        {
            double sumAlt = current.AltitudeDm;
            double sumVib = current.Vibration;
            int modeChanges = 0;
            byte lastMode = current.Mode;

            for (int i = historyCount - 1; i >= windowStart; i--)
            {
                TelemetryFrame f = history[i];
                sumAlt += f.AltitudeDm;
                sumVib += f.Vibration;

                if (f.Mode != lastMode)
                {
                    modeChanges++;
                    lastMode = f.Mode;
                }
            }

            int total = windowLength + 1;
            double meanAlt = sumAlt / total;
            double meanVib = sumVib / total;

            double varAlt = Math.Pow(current.AltitudeDm - meanAlt, 2);
            double varVib = Math.Pow(current.Vibration - meanVib, 2);

            for (int i = historyCount - 1; i >= windowStart; i--)
            {
                varAlt += Math.Pow(history[i].AltitudeDm - meanAlt, 2);
                varVib += Math.Pow(history[i].Vibration - meanVib, 2);
            }

            features[9] = Math.Sqrt(varAlt / total) / AltitudeScale;
            features[10] = Math.Sqrt(varVib / total) / VibrationScale;
            features[11] = (double)modeChanges / total;
        }

        // Захист від можливих NaN та нескінченностей
        for (int i = 0; i < features.Length; i++)
        {
            if (double.IsNaN(features[i]) || double.IsInfinity(features[i]))
            {
                features[i] = 0.0;
            }
        }

        return features;
    }
}