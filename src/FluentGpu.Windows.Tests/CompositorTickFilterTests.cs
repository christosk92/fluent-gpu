using System;
using System.Collections.Generic;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The compositor clock's decision layer, driven entirely by a synthetic timestamp domain (1 tick = 1 microsecond), so
/// every rule below is exact arithmetic rather than a timing race. These are the behaviours the rewrite exists for:
/// a sub-millisecond return storm must NOT cost the session its vblank phase, a failed wait must degrade to a
/// software-paced beat rather than a latch, stamps must land on a constant lattice, and a window on a slower monitor
/// than the compositor must be paced at the WINDOW's rate.
/// </summary>
public sealed class CompositorTickFilterTests
{
    private const long Freq = 1_000_000;    // 1 QPC tick == 1 microsecond
    private const uint Ok = 0, TimedOut = 0x00000102, Failed = 0xFFFFFFFF;
    private const long P120 = 8333;         // 120 Hz beat, microseconds
    private const long P240 = 4167;         // 240 Hz beat
    private const long P50 = 20_000;        // 50 Hz window

    /// <summary>Feeds a scripted sequence of wait returns at scripted instants and records what came out. Mirrors the
    /// clock's own call order exactly: one <see cref="CompositorTickFilter.Observe"/> per return, and one
    /// <see cref="CompositorTickFilter.IsSlot"/> per published tick.</summary>
    private sealed class Feeder(long hintQpc = 0, long startQpc = 1_000_000)
    {
        public readonly CompositorTickFilter Filter = new(Freq, hintQpc);
        public long Now = startQpc;
        public readonly List<TickVerdict> Verdicts = [];
        public readonly List<long> Stamps = [];   // publish stamps of every Tick / SynthesizedTick
        public readonly List<long> Slots = [];    // slot stamps actually published to consumers

        public TickVerdict Step(long advanceQpc, uint result = Ok) => At(Now + advanceQpc, result);

        public TickVerdict At(long absoluteQpc, uint result = Ok)
        {
            Now = absoluteQpc;
            TickVerdict verdict = Filter.Observe(result, Now, out long publish);
            Verdicts.Add(verdict);
            if (verdict is TickVerdict.Tick or TickVerdict.SynthesizedTick)
            {
                Stamps.Add(publish);
                if (Filter.IsSlot(publish, out long slot)) Slots.Add(slot);
            }
            return verdict;
        }

        /// <summary>Run <paramref name="count"/> evenly spaced hardware ticks — the ordinary case every test starts from.</summary>
        public void Beat(int count, long periodQpc)
        {
            for (int i = 0; i < count; i++) Step(periodQpc);
        }

        public int CountOf(TickVerdict verdict) => Verdicts.FindAll(v => v == verdict).Count;
        public long LastStamp => Stamps[^1];
    }

    [Fact]
    public void SpacedHardwareTicksProduceAConstantLatticeAndAMeasuredPeriod()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(3, P120);
        Assert.Equal(0, feed.Filter.MeasuredTickPeriodQpc);   // two deltas is not yet a median

        feed.Beat(7, P120);
        Assert.Equal(10, feed.CountOf(TickVerdict.Tick));
        Assert.Equal(10, feed.Verdicts.Count);
        Assert.Equal(P120, feed.Filter.MeasuredTickPeriodQpc);
        Assert.Equal(P120, feed.Filter.PeriodQpc);
        for (int i = 1; i < feed.Stamps.Count; i++)
            Assert.Equal(P120, feed.Stamps[i] - feed.Stamps[i - 1]);
        Assert.False(feed.Filter.Decimating);
    }

    [Fact]
    public void ATwentyOneReturnSubMillisecondBurstIsSwallowedAndTheNextRealTickLandsOnTheLattice()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(5, P120);
        long lastReal = feed.Now;

        for (int i = 0; i < 21; i++) Assert.Equal(TickVerdict.Ignored, feed.Step(100));

        Assert.Equal(21, feed.Filter.FastBurst);
        Assert.Equal(0, feed.CountOf(TickVerdict.SynthesizedTick));
        Assert.Equal(5, feed.Stamps.Count);                       // nothing was published during the storm
        Assert.Equal(2_000, feed.Filter.FastBurstSpanQpc);   // first ignored return to last, 20 x 100 us

        Assert.Equal(TickVerdict.Tick, feed.At(lastReal + P120));  // the beat resumes on schedule
        Assert.Equal(lastReal + P120, feed.LastStamp);             // ...and on the lattice, not on the storm
        Assert.Equal(0, feed.Filter.FastBurst);
    }

    [Fact]
    public void TwoHundredInstantReturnsNeverLatchTheClockOff()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(5, P120);

        for (int i = 0; i < 200; i++) feed.Step(10);

        Assert.Equal(200, feed.Filter.FastBurst);
        Assert.Equal(200, feed.CountOf(TickVerdict.Ignored));
        Assert.Equal(0, feed.CountOf(TickVerdict.HardLatch));
        Assert.Equal(0, feed.CountOf(TickVerdict.SynthesizedTick));
        Assert.Equal(5, feed.Stamps.Count);
        Assert.Equal(P120, feed.Filter.MeasuredTickPeriodQpc);     // the storm never became "the period"
    }

    [Fact]
    public void WaitFailureSynthesizesOnTheLatticeAndRealTicksResumeOnIt()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(5, P120);
        long lastReal = feed.LastStamp;

        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(TickVerdict.SynthesizedTick, feed.Step(1_000, Failed));
            Assert.Equal(lastReal + i * P120, feed.LastStamp);     // the lattice keeps beating, one period at a time
            Assert.Equal(i, feed.Filter.SynthesizedRun);
        }

        // Hardware comes back 300 us shy of the next lattice point: that is a snap, not a resync.
        Assert.Equal(TickVerdict.Tick, feed.At(lastReal + 4 * P120 - 300));
        Assert.Equal(lastReal + 4 * P120, feed.LastStamp);
        Assert.Equal(0, feed.Filter.SynthesizedRun);
    }

    [Fact]
    public void AReturnThreeMillisecondsLateResyncsTheLatticeOntoItself()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(5, P120);
        long lattice = feed.LastStamp + P120;

        Assert.Equal(TickVerdict.Tick, feed.At(lattice + 3_000));   // 3 ms > the 2 ms snap window
        Assert.Equal(lattice + 3_000, feed.LastStamp);              // stamped where it actually happened

        Assert.Equal(TickVerdict.Tick, feed.Step(P120));            // and the lattice was rebased there
        Assert.Equal(lattice + 3_000 + P120, feed.LastStamp);
    }

    [Fact]
    public void AGapOfMoreThanTwoPeriodsResyncsInsteadOfSnapping()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(5, P120);
        long gapArrival = feed.Now + (5 * P120) / 2;                // 2.5 periods of nothing: a parked clock

        Assert.Equal(TickVerdict.Tick, feed.At(gapArrival));
        Assert.Equal(gapArrival, feed.LastStamp);
        Assert.Equal(TickVerdict.Tick, feed.Step(P120));
        Assert.Equal(gapArrival + P120, feed.LastStamp);
    }

    [Fact]
    public void OneBogusDeltaNeverBecomesThePeriod()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(9, P120);                                          // fill the ring
        Assert.Equal(P120, feed.Filter.MeasuredTickPeriodQpc);

        feed.Step(29);                                               // GPUI's 29 us "period": not even a tick
        Assert.Equal(TickVerdict.Ignored, feed.Verdicts[^1]);
        Assert.Equal(P120, feed.Filter.MeasuredTickPeriodQpc);

        feed.Step(90_000);                                           // a legal-but-outlying 90 ms sample
        Assert.Equal(TickVerdict.Tick, feed.Verdicts[^1]);
        Assert.Equal(P120, feed.Filter.MeasuredTickPeriodQpc);       // the median shrugs it off

        feed.Step(150_000);                                          // beyond the sample bound: not a sample at all
        Assert.Equal(P120, feed.Filter.MeasuredTickPeriodQpc);
    }

    [Fact]
    public void TwoHundredFortyHertzTicksAreNeverMistakenForDoubleTicks()
    {
        var feed = new Feeder(hintQpc: P240);
        feed.Beat(20, P240);

        Assert.Equal(20, feed.CountOf(TickVerdict.Tick));
        Assert.Equal(0, feed.CountOf(TickVerdict.Ignored));
        Assert.Equal(P240, feed.Filter.MeasuredTickPeriodQpc);
    }

    [Fact]
    public void ATimeoutEndsTheBurstButLeavesTheLatticeStanding()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(5, P120);
        long lastReal = feed.Now;
        for (int i = 0; i < 3; i++) feed.Step(100);
        Assert.Equal(3, feed.Filter.FastBurst);

        Assert.Equal(TickVerdict.Timeout, feed.Step(1_000, TimedOut));
        Assert.Equal(0, feed.Filter.FastBurst);
        Assert.Equal(5, feed.Stamps.Count);                          // a timeout is never a tick

        Assert.Equal(TickVerdict.Tick, feed.At(lastReal + P120));
        Assert.Equal(lastReal + P120, feed.LastStamp);               // the lattice survived the timeout
    }

    [Fact]
    public void ResetClearsTheDerivedModelButKeepsTheWindowPeriod()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Filter.SetWindowPeriodQpc(P50);
        feed.Beat(9, P120);
        feed.Step(100);                                              // leave a burst open
        Assert.True(feed.Filter.Decimating);
        Assert.Equal(1, feed.Filter.FastBurst);

        feed.Filter.Reset();

        Assert.Equal(P50, feed.Filter.WindowPeriodQpc);
        Assert.Equal(0, feed.Filter.MeasuredTickPeriodQpc);
        Assert.False(feed.Filter.Decimating);
        Assert.Equal(0, feed.Filter.FastBurst);
        Assert.Equal(0, feed.Filter.SynthesizedRun);

        // No lattice and no slot phase: the first tick after a reset stamps itself and IS a slot.
        Assert.Equal(TickVerdict.Tick, feed.Step(P120));
        Assert.Equal(feed.Now, feed.LastStamp);
        Assert.Equal(feed.Now, feed.Slots[^1]);
    }

    [Fact]
    public void AFiftyHertzWindowOnAOneHundredTwentyHertzCompositorPublishesFiftyEvenSlotsPerSecond()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Filter.SetWindowPeriodQpc(P50);
        feed.Beat(260, P120);                                        // ~2.17 s of compositor beats

        Assert.True(feed.Filter.Decimating);
        Assert.Equal(P50, feed.Filter.PeriodQpc);
        Assert.Equal(P120, feed.Filter.MeasuredTickPeriodQpc);
        Assert.Equal(260, feed.CountOf(TickVerdict.Tick));            // every beat is still a tick...
        Assert.True(feed.Slots.Count < feed.Stamps.Count);            // ...but only some are slots

        // The whole point: slot stamps are EXACTLY one window period apart, so the panel sees an even cadence.
        int anchor = feed.Slots.IndexOf(feed.Slots[^1] - 40 * P50);
        Assert.True(anchor >= 0);
        for (int i = anchor + 1; i < feed.Slots.Count; i++)
            Assert.Equal(P50, feed.Slots[i] - feed.Slots[i - 1]);

        long from = feed.Slots[^1] - 1_000_000;
        int inTheLastSecond = feed.Slots.FindAll(s => s > from && s <= feed.Slots[^1]).Count;
        Assert.Equal(50, inTheLastSecond);
    }

    [Theory]
    [InlineData(10_000L, P120)]   // a 100 Hz window on a 120 Hz beat: 1.20x — under the threshold, pass through
    [InlineData(P120, P120)]     // 120/120
    [InlineData(16_666L, 16_666L)] // 60/60
    public void AWindowLessThanOneQuarterSlowerThanTheBeatPassesThrough(long windowQpc, long beatQpc)
    {
        var feed = new Feeder(hintQpc: beatQpc);
        feed.Filter.SetWindowPeriodQpc(windowQpc);
        feed.Beat(30, beatQpc);

        Assert.False(feed.Filter.Decimating);
        Assert.Equal(beatQpc, feed.Filter.PeriodQpc);
        Assert.Equal(feed.Stamps.Count, feed.Slots.Count);           // every tick is a slot, stamped as itself
        for (int i = 0; i < feed.Stamps.Count; i++) Assert.Equal(feed.Stamps[i], feed.Slots[i]);
    }

    [Theory]
    [InlineData(10_416L, false)]  // 1.2500x - epsilon
    [InlineData(10_417L, true)]   // 1.2501x
    public void OneAndAQuarterIsTheStatedDecimationThreshold(long windowQpc, bool expected)
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Filter.SetWindowPeriodQpc(windowQpc);
        feed.Beat(30, P120);

        Assert.Equal(expected, feed.Filter.Decimating);
    }

    [Fact]
    public void AnUnknownWindowPeriodPassesThrough()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(30, P120);

        Assert.Equal(0, feed.Filter.WindowPeriodQpc);
        Assert.False(feed.Filter.Decimating);
        Assert.Equal(feed.Stamps.Count, feed.Slots.Count);
    }

    [Fact]
    public void AWindowPeriodLearnedMidRunSwitchesModeOnlyAfterTwoConsecutiveTicksAgree()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(6, P120);
        Assert.False(feed.Filter.Decimating);

        feed.Filter.SetWindowPeriodQpc(P50);                         // the window hopped onto a 50 Hz panel
        Assert.False(feed.Filter.Decimating);

        feed.Step(P120);
        Assert.False(feed.Filter.Decimating);                        // one agreeing tick is not a mode switch
        feed.Step(P120);
        Assert.True(feed.Filter.Decimating);
    }

    [Fact]
    public void AHalfSecondHoleInDecimateModeResyncsTheSlotLatticeAndTheNextTickIsASlot()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Filter.SetWindowPeriodQpc(P50);
        feed.Beat(60, P120);
        Assert.True(feed.Filter.Decimating);
        int slotsBefore = feed.Slots.Count;

        Assert.Equal(TickVerdict.Tick, feed.Step(500_000));           // the monitor slept for half a second

        Assert.Equal(slotsBefore + 1, feed.Slots.Count);              // the first tick back is a slot...
        Assert.Equal(feed.Now, feed.Slots[^1]);                       // ...stamped on the resynced lattice
        Assert.Equal(P120, feed.Filter.MeasuredTickPeriodQpc);        // and the hole was not a period sample
        Assert.True(feed.Filter.Decimating);
    }
}
