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

    /// <summary>Deterministic ±300 µs arrival jitter for tick <paramref name="i"/> (a fixed LCG — the same script every run).</summary>
    private static long Jitter(int i)
    {
        uint x = (uint)(i * 1103515245 + 12345);
        x ^= x >> 13;
        return (long)(x % 601) - 300;
    }

    /// <summary>Runs the owner's "half fps after scrolling a lot" script in the filter's own domain: a 120 Hz compositor
    /// with ±0.3 ms arrival jitter, 20 observed ticks, then a LOADED stretch of 40 ticks where the waiter observes only
    /// every second hardware tick (it was busy for the other one), then load ends. Returns the tick index after the load.</summary>
    private static int RunMissedTickLoad(Feeder feed, Func<int, long> trueTick)
    {
        int tick = 0;
        for (int i = 0; i < 20; i++) feed.At(trueTick(tick++));                 // warm, every tick observed
        for (int i = 0; i < 40; i++) { tick++; feed.At(trueTick(tick++)); }     // loaded: every other tick missed
        return tick;
    }

    /// <summary>The loaded stretch's 2P gaps are MISSED ticks, not evidence that the display period changed: the period
    /// the filter publishes (the host's refresh period — present-time prediction, pacing) must stay the true 120 Hz beat
    /// on EVERY tick once the load ends, every tick must be accepted one period apart, and decimation must never engage.
    /// Current code: the median of accepted gaps drifts to 2P during the load (16.6 ms published for a 8.33 ms display)
    /// and only recovers as 1P samples re-fill the median.</summary>
    [Fact]
    public void MissedTicksUnderLoadNeverChangeThePublishedPeriod()
    {
        var feed = new Feeder(hintQpc: P120, startQpc: 0);
        const long T0 = 1_000_000;
        long TrueTick(int k) => T0 + k * P120 + Jitter(k);
        int tick = RunMissedTickLoad(feed, TrueTick);
        int before = feed.Verdicts.Count, stampsBefore = feed.Stamps.Count;
        var periods = new List<long>();
        for (int i = 0; i < 40; i++) { feed.At(TrueTick(tick++)); periods.Add(feed.Filter.PeriodQpc); }

        Assert.All(feed.Verdicts.GetRange(before, feed.Verdicts.Count - before), v => Assert.Equal(TickVerdict.Tick, v));
        Assert.False(feed.Filter.Decimating);
        Assert.All(periods, p => Assert.InRange(p, P120 - 700, P120 + 700));
        for (int i = stampsBefore + 1; i < feed.Stamps.Count; i++)
            Assert.InRange(feed.Stamps[i] - feed.Stamps[i - 1], P120 - 700, P120 + 700);
    }

    /// <summary>After the same loaded stretch, the double-tick guard must still be ARMED: a spurious second return 1 ms
    /// after a real tick is a duplicate and must be Ignored (holds on the current code — kept as the invariant any fix
    /// of the published-period defect above must preserve).</summary>
    [Fact]
    public void MissedTicksUnderLoadLeaveTheDoubleTickGuardArmed()
    {
        var feed = new Feeder(hintQpc: P120, startQpc: 0);
        const long T0 = 1_000_000;
        long TrueTick(int k) => T0 + k * P120 + Jitter(k);
        int tick = RunMissedTickLoad(feed, TrueTick);
        for (int i = 0; i < 10; i++) feed.At(TrueTick(tick++));                 // load gone
        long real = TrueTick(tick++);
        Assert.Equal(TickVerdict.Tick, feed.At(real));
        Assert.Equal(TickVerdict.Ignored, feed.At(real + 1_000));             // a double return 1 ms later
    }

    /// <summary>The double-tick reference must stay on the IDEAL lattice across a missed tick. A tick the waiter did not
    /// observe (it was busy) leaves a 2P gap before the next accepted return; that return is still the tick on the
    /// lattice, so a spurious second return 0.1 ms after it is a duplicate and must be Ignored. The defect clamped the
    /// reference to <c>min(expected + P, now)</c> — one period BEHIND the real tick after a miss — so the spurious return
    /// looked 1.0x a period after the reference and was published as a second tick 0.1 ms after the first.</summary>
    [Fact]
    public void ASpuriousReturnJustAfterARealTickThatFollowedAMissedTickIsIgnored()
    {
        var feed = new Feeder(hintQpc: P120, startQpc: 0);
        const long T0 = 1_000_000;
        long TrueTick(int k) => T0 + k * P120 + Jitter(k);
        int tick = 0;
        for (int i = 0; i < 20; i++) feed.At(TrueTick(tick++));   // warm, every tick observed
        tick++;                                                   // one hardware tick the waiter missed
        long real = TrueTick(tick++);
        Assert.Equal(TickVerdict.Tick, feed.At(real));            // the next tick lands 2P after the last accepted one
        Assert.Equal(TickVerdict.Ignored, feed.At(real + 100));   // a spurious return 0.1 ms later is a duplicate
        Assert.Equal(TickVerdict.Tick, feed.At(TrueTick(tick++))); // and the beat continues on schedule
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

    // Item D: the double-tick ignore window used to be measured from the RAW last-accepted return, so one late
    // return (this tick's own observation landing well into what should have been its neighbour's window — a
    // scheduling delay, not a duplicate vblank) dragged that reference forward and swallowed the genuinely NEXT
    // tick, which was still on the original hardware cadence, as if it were a duplicate of the late one.
    [Fact]
    public void ALateReturnFollowedByAnOnTimeTickKeepsTheOnTimeTick()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(9, P120);                              // warm the median so the ignore window is a real half-period
        long lastOnTime = feed.Now;

        // This tick's own observation lands 5 ms into what should have been the NEXT tick's window — still clearly
        // its own (distinct) tick, not a burst return, so it must be accepted.
        Assert.Equal(TickVerdict.Tick, feed.Step(P120 + 5_000));

        // The following tick is genuinely on the ORIGINAL hardware cadence — only 3.33 ms after the late one's raw
        // arrival, comfortably inside the naive "since the raw last-accepted return" ignore window (half of ~8.33 ms).
        // Measuring against the lattice's own idealized slot instead means the late observation never drags that
        // reference forward, so this real, on-time tick is still accepted (the bug this test guards against).
        Assert.Equal(TickVerdict.Tick, feed.At(lastOnTime + 2 * P120));
    }

    // Item D: an always-on tally of every Ignored verdict, not just the bursts long enough to earn their own
    // fast-burst log line (FastBurstLogMin = 8 in Win32CompositorClock) — a single swallowed double tick must still
    // be countable.
    [Fact]
    public void IgnoredCountTalliesEverySwallowedReturnEvenBelowTheBurstLogThreshold()
    {
        var feed = new Feeder(hintQpc: P120);
        feed.Beat(9, P120);
        Assert.Equal(0, feed.Filter.IgnoredCount);

        Assert.Equal(TickVerdict.Ignored, feed.Step(100));   // one lone double-tick return — well under FastBurstLogMin
        Assert.Equal(1, feed.Filter.IgnoredCount);

        Assert.Equal(TickVerdict.Ignored, feed.Step(100));
        Assert.Equal(2, feed.Filter.IgnoredCount);

        // The counter survives the burst ending (unlike FastBurst) and is NOT cleared by Reset — a reprobe/idle-park
        // does not make the session's double-tick noise disappear.
        Assert.Equal(TickVerdict.Tick, feed.At(feed.Now + P120));
        Assert.Equal(2, feed.Filter.IgnoredCount);
        feed.Filter.Reset();
        Assert.Equal(2, feed.Filter.IgnoredCount);
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
