using System.Diagnostics;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>Locks the pure decision seam of the display-paced native input wait (<c>Win32Window.WaitForPacedWork</c>):
/// which messages may wait for the display tick and how the absolute deadline is honoured. Wheel packets became
/// deferrable with the scroll pacing fix (S2): the input ring sums consecutive wheel deltas, so a packet waiting for
/// the tick loses nothing, and the frame that consumes it is produced in phase instead of slipping one refresh.
/// Down/Up/Key/Timer stay urgent — they end the wait at once.</summary>
public sealed class PacedInputWaitClassifierTests
{
    [Fact]
    public void PointerMotionCompanions_StayDeferrable()
    {
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmPointerUpdate));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmNcPointerUpdate));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmMouseMove));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmNcMouseMove));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmSetCursor));
    }

    [Fact]
    public void WheelPackets_AreDeferrable_ButNotMotion()
    {
        Assert.Equal(0x024Eu, PacedInputWaitClassifier.WmPointerWheel);
        Assert.Equal(0x024Fu, PacedInputWaitClassifier.WmPointerHWheel);

        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmPointerWheel));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmPointerHWheel));

        // The motion census counts pointer-motion messages only; a wheel packet waiting for the tick is not motion.
        Assert.False(PacedInputWaitClassifier.IsMotion(PacedInputWaitClassifier.WmPointerWheel));
        Assert.False(PacedInputWaitClassifier.IsMotion(PacedInputWaitClassifier.WmPointerHWheel));
    }

    [Fact]
    public void UrgentInput_IsNeverDeferrable()
    {
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0246)); // WM_POINTERDOWN
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0247)); // WM_POINTERUP
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0100)); // WM_KEYDOWN
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0113)); // WM_TIMER
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0000)); // WM_NULL / explicit Wake
    }

    [Fact]
    public void MotionCensus_CountsPointerMotionOnly()
    {
        Assert.True(PacedInputWaitClassifier.IsMotion(PacedInputWaitClassifier.WmPointerUpdate));
        Assert.True(PacedInputWaitClassifier.IsMotion(PacedInputWaitClassifier.WmNcPointerUpdate));
        Assert.True(PacedInputWaitClassifier.IsMotion(PacedInputWaitClassifier.WmMouseMove));
        Assert.True(PacedInputWaitClassifier.IsMotion(PacedInputWaitClassifier.WmNcMouseMove));
        Assert.False(PacedInputWaitClassifier.IsMotion(PacedInputWaitClassifier.WmSetCursor));
    }

    [Fact]
    public void MotionStormCannotSlideAbsoluteDeadline()
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
