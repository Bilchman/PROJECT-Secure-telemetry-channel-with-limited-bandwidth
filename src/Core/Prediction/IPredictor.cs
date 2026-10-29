namespace Core;

public interface IPredictor
{
    string Name { get; }
    FrameClass[] Classify(IReadOnlyList<TelemetryFrame> frames);
    TelemetryFrame PredictResidual(IReadOnlyList<TelemetryFrame> history, TelemetryFrame actual);
    TelemetryFrame RestoreResidual(IReadOnlyList<TelemetryFrame> history, TelemetryFrame residual);
}