namespace BorderLess.Core;

public sealed record LatencyResult(int Samples, double? MedianMs, double? PeakMs, int ObservedQueuePeak)
{
    public static LatencyResult Summarize(IEnumerable<double> samples, int queuePeak)
    {
        var sorted = samples.Order().ToArray();
        if (sorted.Length == 0) return new(0, null, null, queuePeak);
        var middle = sorted.Length / 2;
        double median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return new(sorted.Length, median, sorted[^1], queuePeak);
    }
}
