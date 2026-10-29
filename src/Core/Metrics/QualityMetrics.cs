namespace Core;

public static class QualityMetric
{
    public static double Compute(
        long refBits, long sentBits, 
        int restoredCritical, int totalCritical, 
        double refTimeSec, double actualTimeSec,
        double alpha = 0.5, double beta = 0.35, double gamma = 0.15)
    {
        double S = (double)refBits / (refBits + sentBits);
        double R = totalCritical > 0 ? (double)restoredCritical / totalCritical : 1.0;
        double T = refTimeSec / (refTimeSec + actualTimeSec);

        return alpha * S + beta * R + gamma * T;
    }
}