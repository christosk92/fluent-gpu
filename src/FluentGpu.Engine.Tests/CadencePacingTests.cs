using FluentGpu.Hosting;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Pure arithmetic gates for <see cref="CadencePacing.QuantizedWaitMs"/> — the vblank-anchored pacing math the
/// host's cadence wait branch uses (and the adaptive-GPU governor reuses at its own fixed rate). Locks the three laws
/// the deleted <c>AppHost.AmbientFrameWaitMs</c> encoded: a due time rounds to a WHOLE number of the panel's refreshes
/// (so the cadence is "every Nth vblank", never a rate that beats against the vsync-locked present), the resulting
/// period is PHASED on the last present, and the wait never returns 0 (which would spin the loop).</summary>
public sealed class CadencePacingTests
{
    private const double Due30 = 1000.0 / 30.0;    // 33.33 ms — the engine's default loop cadence
    private const double Due24 = 1000.0 / 24.0;    // 41.67 ms
    private const double Refresh120 = 1000.0 / 120.0;   // 8.333
    private const double Refresh60 = 1000.0 / 60.0;     // 16.667
    private const double Refresh50 = 1000.0 / 50.0;     // 20
    private const double Refresh144 = 1000.0 / 144.0;   // 6.944

    // sinceLastPresent = 0 ⇒ the returned wait IS ceil(periodMs): the phase term is `period − 0 % period`.
    private static int Period(double dueMs, double refreshMs) => CadencePacing.QuantizedWaitMs(dueMs, refreshMs, 0.0);

    [Theory]
    // 33.33 ms due: 4 refreshes at 120 Hz, 2 at 60, 2 at 50 (⇒ 25 fps — 30 is not a divisor of 50), 5 at 144.
    [InlineData(Due30, Refresh120, 34)]   // 33.3
    [InlineData(Due30, Refresh60, 34)]    // 33.3
    [InlineData(Due30, Refresh50, 40)]    // 40   — every 2nd refresh of a 50 Hz panel
    [InlineData(Due30, Refresh144, 35)]   // 34.7 — every 5th refresh of a 144 Hz panel
    // 41.67 ms due: 5 at 120, 2 at 60 (the 2.5 midpoint rounds DOWN — never pace slower than asked by a whole refresh
    // when the nearer lattice point is exactly as close), 2 at 50, 6 at 144.
    [InlineData(Due24, Refresh120, 42)]   // 41.7
    [InlineData(Due24, Refresh60, 34)]    // 33.3
    [InlineData(Due24, Refresh50, 40)]    // 40
    [InlineData(Due24, Refresh144, 42)]   // 41.7
    public void DueTime_QuantizesToAWholeNumberOfRefreshes(double dueMs, double refreshMs, int expectedPeriodMs)
        => Assert.Equal(expectedPeriodMs, Period(dueMs, refreshMs));

    [Fact]
    public void Period_IsPhasedOnTheLastPresent()
    {
        // 33.33 ms due on a 50 Hz panel ⇒ a 40 ms period. 5 ms into it ⇒ 35 ms left.
        Assert.Equal(40, Period(Due30, Refresh50));
        Assert.Equal(35, CadencePacing.QuantizedWaitMs(Due30, Refresh50, 5.0));
    }

    [Fact]
    public void StalePresentAnchor_WrapsIntoTheCurrentPeriod()
    {
        // A stretch of skip-submitted (byte-identical) frames leaves the anchor arbitrarily old: 45 ms into a 40 ms
        // period is 5 ms into the CURRENT one, so the answer is the same 35 — never 0 and never negative.
        Assert.Equal(35, CadencePacing.QuantizedWaitMs(Due30, Refresh50, 45.0));
        Assert.Equal(35, CadencePacing.QuantizedWaitMs(Due30, Refresh50, 5.0 + 40.0 * 1000));
    }

    [Fact]
    public void UnknownRefresh_FallsBackToTheRawDueTime()
    {
        Assert.Equal(34, CadencePacing.QuantizedWaitMs(Due30, 0.0, 5.0));    // headless / no present stats yet
        Assert.Equal(34, CadencePacing.QuantizedWaitMs(Due30, -1.0, 5.0));   // a bogus period is treated as unknown
    }

    [Fact]
    public void NoPresentYet_FallsBackToTheRawDueTime()
    {
        // −1 = "nothing presented yet": there is no vblank-locked anchor to phase on, so the raw due time stands even
        // though the refresh period is known.
        Assert.Equal(34, CadencePacing.QuantizedWaitMs(Due30, Refresh50, -1.0));
        Assert.Equal(42, CadencePacing.QuantizedWaitMs(Due24, Refresh144, -1.0));
    }

    [Fact]
    public void NeverPacesFasterThanThePanel()
    {
        // A row asking for 240 Hz on a 60 Hz panel still gets one whole refresh, not a fraction of one.
        Assert.Equal(17, CadencePacing.QuantizedWaitMs(1000.0 / 240.0, Refresh60, 0.0));
    }

    [Fact]
    public void WaitIsNeverBelowOneMillisecond()
    {
        // A present anchor a hair short of the whole period (sub-millisecond left) and a sub-millisecond due time with
        // no lattice both floor at 1 ms: returning 0 turns the host loop into a pure poll until the frame that
        // consumes the row actually runs.
        Assert.Equal(1, CadencePacing.QuantizedWaitMs(Due30, Refresh60, Due30 - 0.0005));
        Assert.Equal(1, CadencePacing.QuantizedWaitMs(0.2, 0.0, -1.0));
    }

    [Theory]
    // A row due in 10 ms, 5 ms after the present, under a 30 fps ceiling: the wake lands on the 33.3 ms lattice point
    // (5 + 29 = 34 = the Due30 period above), i.e. every 4th refresh at 120 Hz and every 2nd at 60 Hz — 30 fps.
    // Flooring the finished wait instead gives max(4, 33) = 33 → a wake at 38 ms, past the lattice point, so the frame
    // takes the NEXT vblank: 41.7 ms (24 fps) at 120 Hz, 50 ms (20 fps) at 60 Hz.
    [InlineData(Refresh120, 29)]
    [InlineData(Refresh60, 29)]
    public void Floor_IsAnIntervalBetweenPresents_OnTheLattice(double refreshMs, int expectedWaitMs)
        => Assert.Equal(expectedWaitMs, CadencePacing.FlooredWaitMs(10.0, Due30, refreshMs, 5.0));

    [Fact]
    public void Floor_NeverShortensASlowerRow_AndZeroDisablesIt()
    {
        // A 24 Hz row is already slower than a 30 fps ceiling: identical to the unfloored answer.
        Assert.Equal(CadencePacing.QuantizedWaitMs(Due24, Refresh120, 5.0), CadencePacing.FlooredWaitMs(Due24, Due30, Refresh120, 5.0));
        // floor 0 = no ceiling: the row's own answer verbatim, even for a fast row.
        Assert.Equal(CadencePacing.QuantizedWaitMs(10.0, Refresh120, 5.0), CadencePacing.FlooredWaitMs(10.0, 0.0, Refresh120, 5.0));
        // NaN due is "now" in QuantizedWaitMs; under the floor it paces at the floor instead of spinning.
        Assert.Equal(29, CadencePacing.FlooredWaitMs(double.NaN, Due30, Refresh120, 5.0));
    }
}
