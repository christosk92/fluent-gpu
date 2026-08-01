namespace Bench.Contracts;

/// <summary>
/// Pure refresh / missed-vblank classification used by the PresentMon summarizer and its synthetic tests.
/// Ordinary sub-threshold jitter (e.g. 8.4–9.2 ms around an ~8.33 ms cadence) is never a miss.
/// </summary>
public static class RefreshCadence
{
    public const double MissMultiplier = 1.5;

    public static double? MeasuredRefreshMs(IReadOnlyList<double> displayChangeMs, IReadOnlyList<double> presentMs)
    {
        if (displayChangeMs.Count >= 8) return Percentile(displayChangeMs, 50);
        if (presentMs.Count >= 8) return Percentile(presentMs, 50);
        return null;
    }

    public static bool NominalConflictsWithMeasured(double nominalHz, double measuredHz)
    {
        if (nominalHz <= 1 || measuredHz <= 1) return false;
        double ratio = measuredHz / nominalHz;
        return ratio < 0.92 || ratio > 1.08;
    }

    /// <summary>Prefer DXGI refresh-count deltas; fall back to intervals &gt; 1.5× measured refresh.</summary>
    public static MissedVblankResult ClassifyMissed(
        IReadOnlyList<double> presentIntervalsMs,
        double measuredRefreshMs,
        IReadOnlyList<ulong>? refreshCounts)
    {
        if (refreshCounts is { Count: > 1 })
        {
            int missed = 0;
            for (int i = 1; i < refreshCounts.Count; i++)
            {
                if (refreshCounts[i] > refreshCounts[i - 1])
                {
                    long slots = (long)(refreshCounts[i] - refreshCounts[i - 1] - 1UL);
                    if (slots > 0) missed += (int)slots;
                }
            }
            return new MissedVblankResult("dxgi-refresh-count-delta", missed, measuredRefreshMs * MissMultiplier);
        }

        if (measuredRefreshMs <= 0 || presentIntervalsMs.Count == 0)
            return new MissedVblankResult("unavailable", 0, null);

        double threshold = measuredRefreshMs * MissMultiplier;
        int count = 0;
        for (int i = 0; i < presentIntervalsMs.Count; i++)
            if (presentIntervalsMs[i] > threshold) count++;
        return new MissedVblankResult("interval-1.5x-fallback", count, threshold);
    }

    public static bool IsOrdinaryJitter(double intervalMs, double measuredRefreshMs)
    {
        if (measuredRefreshMs <= 0) return false;
        return intervalMs >= measuredRefreshMs * 0.95 && intervalMs <= measuredRefreshMs * 1.15
               && intervalMs <= measuredRefreshMs * MissMultiplier;
    }

    public static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = new double[values.Count];
        for (int i = 0; i < values.Count; i++) sorted[i] = values[i];
        Array.Sort(sorted);
        int rank = Math.Clamp((int)Math.Ceiling(p / 100d * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[rank];
    }
}

public readonly record struct MissedVblankResult(string Method, int MissedVblanks, double? ThresholdMs);
