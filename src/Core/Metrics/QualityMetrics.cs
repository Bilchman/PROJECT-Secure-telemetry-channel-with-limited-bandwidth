namespace Core;

public static class QualityMetric
{
    public static double Compute(
        long refBits, long sentBits,
        int restoredCritical, int totalCritical,
        double refTimeSec, double actualTimeSec,
        double alpha = 0.5, double beta = 0.35, double gamma = 0.15)
    {
        if (totalCritical > 0 && restoredCritical == 0)
            return 0.0;

        double S = (refBits + sentBits) > 0 ? (double)refBits / (refBits + sentBits) : 1.0;
        double R = totalCritical > 0 ? (double)restoredCritical / totalCritical : 1.0;
        double T = (refTimeSec + actualTimeSec) > 0 ? refTimeSec / (refTimeSec + actualTimeSec) : 0.0;

        return alpha * S + beta * R + gamma * T;
    }
}
