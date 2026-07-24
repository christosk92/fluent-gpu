using System.Diagnostics;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

public sealed class DirectManipulationPacingTests
{
    [Fact]
    public void MessageStorm_IsBoundedByAbsoluteDeadline()
    {
        var pacer = new DmManualUpdatePacer();
        pacer.ArmImmediate(0);

        int updates = 0;
        const int wakesPerSecond = 20_000;
        for (int i = 0; i <= wakesPerSecond; i++)
        {
            long now = (long)(i * (Stopwatch.Frequency / (double)wakesPerSecond));
            if (pacer.TryConsume(now)) updates++;
        }

        Assert.InRange(updates, 142, 144);
    }

    [Fact]
    public void EarlyWakes_DoNotSlideDeadline_AndIdlePreservesHostWait()
    {
        var pacer = new DmManualUpdatePacer();
        long start = Stopwatch.Frequency;
        pacer.ArmImmediate(start);
        Assert.True(pacer.TryConsume(start));

        long oneMsLater = start + Stopwatch.Frequency / 1_000;
        int first = pacer.ClampWait(100, oneMsLater);
        for (int i = 0; i < 100; i++)
            Assert.Equal(first, pacer.ClampWait(100, oneMsLater));
        Assert.InRange(first, 5, 7);
        Assert.Equal(first, pacer.ClampWait(-1, oneMsLater));

        pacer.Disarm();
        Assert.Equal(-1, pacer.ClampWait(-1, oneMsLater));
        Assert.Equal(100, pacer.ClampWait(100, oneMsLater));
    }

    [Fact]
    public void LongGap_AdvancesOnce_WithoutCatchUpBurst()
    {
        var pacer = new DmManualUpdatePacer();
        pacer.ArmImmediate(0);
        Assert.True(pacer.TryConsume(0));

        long afterGap = Stopwatch.Frequency / 10; // 100 ms
        Assert.True(pacer.TryConsume(afterGap));
        Assert.False(pacer.TryConsume(afterGap));
        Assert.InRange(pacer.ClampWait(-1, afterGap), 1, DmManualUpdatePacer.IntervalMs);
    }

    [Fact]
    public void OnlyKnownPhysicalMouse_PreemptsLiveDirectManipulation()
    {
        Assert.Equal(DmWheelRoute.StopDmAndPass,
            DmWheelArbitration.Decide(dmLive: true, DmWheelSourceEvidence.PhysicalMouse));
        Assert.Equal(DmWheelRoute.DmOwned,
            DmWheelArbitration.Decide(dmLive: true, DmWheelSourceEvidence.Touchpad));
        Assert.Equal(DmWheelRoute.ExistingClassifier,
            DmWheelArbitration.Decide(dmLive: true, DmWheelSourceEvidence.Unknown));

        Assert.Equal(DmWheelRoute.ExistingClassifier,
            DmWheelArbitration.Decide(dmLive: false, DmWheelSourceEvidence.PhysicalMouse));
        Assert.Equal(DmWheelRoute.ExistingClassifier,
            DmWheelArbitration.Decide(dmLive: false, DmWheelSourceEvidence.Touchpad));
        Assert.Equal(DmWheelRoute.ExistingClassifier,
            DmWheelArbitration.Decide(dmLive: false, DmWheelSourceEvidence.Unknown));
    }
}
