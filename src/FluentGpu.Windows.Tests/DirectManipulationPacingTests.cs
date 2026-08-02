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
    public void StalledLiveGesture_TripsTheInertiaWatchdog_ButIdleAndProgressingOnesDoNot()
    {
        const long t0 = 10_000;
        long timeout = Win32DirectManipulation.DmInertiaStallTimeoutMs;

        // A live gesture (RUNNING or INERTIA) that produced nothing for longer than the timeout is stuck: this is the
        // coast whose fling target a navigation unmounted, which never reaches READY on its own. UpdateIfDue's caller
        // then issues Viewport.Stop(), whose READY callback runs the ordinary terminal path (clearing GestureLive and
        // with it NeedsClockTick, which is what releases the host wait from its due-now 0 clamp).
        Assert.True(DmInertiaStall.IsStalled(gestureLive: true, nowMs: t0 + timeout + 1, lastProgressMs: t0));

        // Boundary: exactly at the timeout is NOT yet stalled (strict >), matching the engage wedge watchdog's shape.
        Assert.False(DmInertiaStall.IsStalled(gestureLive: true, nowMs: t0 + timeout, lastProgressMs: t0));

        // A gesture that is still producing content deltas / status changes is never stalled, however long it coasts.
        Assert.False(DmInertiaStall.IsStalled(gestureLive: true, nowMs: t0 + 8, lastProgressMs: t0));

        // Idle DM (READY between gestures) is never "stalled" no matter how old the last progress stamp is — it holds
        // nothing awake, and Stopping it would be a spurious COM call on every pump.
        Assert.False(DmInertiaStall.IsStalled(gestureLive: false, nowMs: t0 + 10 * timeout, lastProgressMs: t0));
    }

    [Fact]
    public void DueNowClamp_NeverRepeatsWithoutAnInterveningConsume()
    {
        var pacer = new DmManualUpdatePacer();
        long now = Stopwatch.Frequency;   // arbitrary non-zero epoch
        pacer.ArmImmediate(now);

        // The healthy loop: a due-now 0 wait returns immediately, the pump drains, UpdateIfDue consumes the slot — which
        // advances the absolute deadline, so the next clamp is a real wait again. The run never exceeds 1.
        for (int frame = 0; frame < 50; frame++)
        {
            Assert.Equal(0, pacer.ClampWait(-1, now));
            Assert.True(pacer.ZeroWaitRun <= 1);
            Assert.True(pacer.TryConsume(now));
            Assert.Equal(0, pacer.ZeroWaitRun);
            Assert.True(pacer.ClampWait(-1, now) > 0);   // the consumed slot moved the deadline into the future
            now += Stopwatch.Frequency / 100;            // 10 ms — past the 7 ms interval on any tick frequency
        }

        // The pathology the tripwire names: clamps that keep returning 0 with no consume in between. That is the host
        // polling MsgWaitForMultipleObjectsEx at 0 ms — the 1000-3300 it/s free-run — and the counter climbs with it.
        var stuck = new DmManualUpdatePacer();
        stuck.ArmImmediate(0);
        for (int i = 1; i <= 5; i++)
        {
            Assert.Equal(0, stuck.ClampWait(-1, Stopwatch.Frequency));
            Assert.Equal(i, stuck.ZeroWaitRun);
        }
        Assert.True(DmManualUpdatePacer.MaxZeroWaitRun >= 5);   // surfaced as diag dm.pacer.zeroWaitRun

        // Disarming (the terminal path a Stop() ultimately reaches) clears the run along with the deadline.
        stuck.Disarm();
        Assert.Equal(0, stuck.ZeroWaitRun);
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

    [Fact]
    public void DisplayPacedWait_DefersOnlyPointerMotionCompanions()
    {
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmPointerUpdate));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmNcPointerUpdate));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmMouseMove));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmNcMouseMove));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmSetCursor));

        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0246)); // WM_POINTERDOWN
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0247)); // WM_POINTERUP
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x024E)); // WM_POINTERWHEEL
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0100)); // WM_KEYDOWN
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0113)); // WM_TIMER
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0000)); // WM_NULL / explicit Wake
    }

    [Fact]
    public void DisplayPacedWait_MotionStormCannotSlideAbsoluteDeadline()
    {
        long start = Stopwatch.Frequency * 10L;
        long deadline = start + (long)Math.Ceiling(8.0 * Stopwatch.Frequency / 1000.0);
        int previous = int.MaxValue;

        // Four thousand early motion wakes all consult the same deadline. Remaining time can only decrease and reaches
        // zero at the original 8 ms boundary; no wake restarts an 8 ms relative timeout.
        for (int i = 0; i < 4_000; i++)
        {
            long now = start + (deadline - start) * i / 4_000;
            int remaining = PacedInputWaitClassifier.RemainingMilliseconds(deadline, now);
            Assert.InRange(remaining, 1, previous);
            previous = remaining;
        }
        Assert.Equal(0, PacedInputWaitClassifier.RemainingMilliseconds(deadline, deadline));
        Assert.Equal(0, PacedInputWaitClassifier.RemainingMilliseconds(deadline, deadline + Stopwatch.Frequency));
    }
}
