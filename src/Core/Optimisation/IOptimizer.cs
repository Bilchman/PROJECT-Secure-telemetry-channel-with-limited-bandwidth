namespace Core;

public record SystemParameters(
    int WindowFrames,
    double CompressionThreshold,
    double CriticalWeight
);

public interface IOptimizer
{
    string Name { get; }
    SystemParameters Optimize(Func<SystemParameters, double> evaluateFitness, int evaluationBudget);
}