using FluentGpu.Hosting;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The hosted flush runs to quiescence and never slices around a slow unit; a unit longer than the frame period
/// is REPORTED (always-on <c>[signals.slow-unit]</c>) so it is fixed at its source. <see cref="SlowReactiveUnits"/> owns
/// when that line is written: the first slow unit at once, then at most one line a second carrying the count it folded.</summary>
public sealed class SlowReactiveUnitsTests
{
    private const long Second = 10_000_000;   // a 10 MHz Stopwatch
    private const long Frame = Second / 120;  // an 8.3 ms period

    [Fact]
    public void AUnitWithinTheFramePeriodIsNotSlow()
    {
        var s = new SlowReactiveUnits();
        Assert.False(s.Note(Frame, Frame, 0, Second, out _));
        Assert.False(s.Note(Frame / 2, Frame, 0, Second, out _));
        Assert.Equal(0, s.Total);
        Assert.False(SlowReactiveUnits.IsSlow(Frame * 5, 0));   // no period known: nothing to judge against
    }

    [Fact]
    public void TheFirstSlowUnitIsReportedAtOnce_ThenAtMostOnceASecond_CarryingTheFoldedCount()
    {
        var s = new SlowReactiveUnits();
        Assert.True(s.Note(Frame + 1, Frame, 100, Second, out int folded));
        Assert.Equal(0, folded);
        Assert.False(s.Note(Frame * 3, Frame, 100 + Second / 2, Second, out _));
        Assert.False(s.Note(Frame * 2, Frame, 100 + Second - 1, Second, out _));
        Assert.True(s.Note(Frame * 2, Frame, 100 + Second, Second, out folded));
        Assert.Equal(2, folded);
        Assert.Equal(4, s.Total);
    }

    [Fact]
    public void FastUnitsBetweenSlowOnesDoNotResetTheCadence()
    {
        var s = new SlowReactiveUnits();
        Assert.True(s.Note(Frame * 2, Frame, 0, Second, out _));
        Assert.False(s.Note(1, Frame, Second / 4, Second, out _));
        Assert.False(s.Note(Frame * 2, Frame, Second / 2, Second, out _));
        Assert.True(s.Note(Frame * 2, Frame, Second * 2, Second, out int folded));
        Assert.Equal(1, folded);
    }
}
