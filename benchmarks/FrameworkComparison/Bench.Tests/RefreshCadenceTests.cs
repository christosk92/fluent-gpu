using Bench.Contracts;
using Xunit;

namespace Bench.Tests;

public sealed class RefreshCadenceTests
{
    [Fact]
    public void OrdinaryJitterAround120Hz_IsNotAMiss()
    {
        double refresh = 1000.0 / 120.0; // ~8.333 ms
        double[] intervals = [8.33, 8.45, 8.9, 9.1, 8.2, 8.7, 8.35, 8.6];
        MissedVblankResult result = RefreshCadence.ClassifyMissed(intervals, refresh, refreshCounts: null);
        Assert.Equal("interval-1.5x-fallback", result.Method);
        Assert.Equal(0, result.MissedVblanks);
        Assert.All(intervals, i => Assert.True(RefreshCadence.IsOrdinaryJitter(i, refresh)));
    }

    [Fact]
    public void IntervalAbove1_5Refresh_CountsAsMiss_WhenNoRefreshCounts()
    {
        double refresh = 1000.0 / 120.0;
        double[] intervals = [8.33, 8.4, 13.0, 8.3]; // 13 > 1.5*8.333
        MissedVblankResult result = RefreshCadence.ClassifyMissed(intervals, refresh, null);
        Assert.Equal(1, result.MissedVblanks);
    }

    [Fact]
    public void RefreshCountDelta_PreferredOverIntervalFallback()
    {
        // Intervals look fine, but DXGI refresh counts skipped one slot.
        double refresh = 8.333;
        double[] intervals = [8.3, 8.4, 8.3];
        ulong[] counts = [10, 11, 13]; // delta 2 ⇒ one missed slot
        MissedVblankResult result = RefreshCadence.ClassifyMissed(intervals, refresh, counts);
        Assert.Equal("dxgi-refresh-count-delta", result.Method);
        Assert.Equal(1, result.MissedVblanks);
    }

    [Fact]
    public void Nominal99VsMeasured120_IsConflict()
    {
        Assert.True(RefreshCadence.NominalConflictsWithMeasured(99, 120));
        Assert.False(RefreshCadence.NominalConflictsWithMeasured(120, 119.5));
    }

    [Fact]
    public void MeasuredRefresh_PrefersDisplayChangeP50()
    {
        double[] display = [8.33, 8.34, 8.32, 8.33, 8.35, 8.31, 8.33, 8.34];
        double[] present = [16, 16, 16, 16, 16, 16, 16, 16];
        Assert.Equal(8.33, RefreshCadence.MeasuredRefreshMs(display, present)!.Value, precision: 2);
    }
}
