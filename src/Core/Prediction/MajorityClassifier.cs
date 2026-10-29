namespace Core;

public sealed class MajorityClassifier : IPredictor
{
    public string Name => "majority";

    public FrameClass[] Classify(IReadOnlyList<TelemetryFrame> frames) => 
        new FrameClass[frames.Count];

    public TelemetryFrame PredictResidual(IReadOnlyList<TelemetryFrame> history, TelemetryFrame actual) => actual;
    public TelemetryFrame RestoreResidual(IReadOnlyList<TelemetryFrame> history, TelemetryFrame residual) => residual;
}