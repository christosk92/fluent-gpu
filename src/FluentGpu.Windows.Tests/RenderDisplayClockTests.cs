using System;
using System.Threading;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The compositor clock's threading contract, driven by an injected wait primitive AND an injected timestamp domain
/// (1 tick = 1 microsecond, one 120 Hz period per pulse). Both seams matter: the clock now filters its returns, so a
/// controlled clock that never advanced time would look like a sub-millisecond storm and every pulse would be
/// (correctly) swallowed as a double tick.
/// </summary>
public sealed class RenderDisplayClockTests
{
    private const long Freq = 1_000_000;      // 1 QPC tick == 1 microsecond
    private const long Period = 8_333;        // one 120 Hz compositor beat per pulse
    private const long SlowWindowPeriod = 20_000;  // a 50 Hz panel: 2.4 compositor beats per refresh
    private const uint WaitFailed = 0xFFFFFFFF;

    private sealed class ControlledClock : IDisposable
    {
        public readonly AutoResetEvent Input = new(false);
        public uint Result;
        public int Consumed;
        /// <summary>Set to make the wait itself throw — the missing-export case, the one permanent latch.</summary>
        public volatile Exception? Throw;
        /// <summary>True while the waiter is blocked inside the wait — i.e. it has finished publishing whatever the
        /// previous return produced. The tests' barrier against asserting on a tick the waiter has not published yet.</summary>
        public volatile bool Waiting;
        private long _now = 1_000_000;

        /// <summary>The clock's timestamp source: a synthetic domain that only moves when a pulse is delivered, so
        /// every tick spacing in these tests is exactly one refresh period regardless of real wall-clock timing.</summary>
        public long Now => Volatile.Read(ref _now);

        public uint Wait()
        {
            if (Throw is { } ex) throw ex;
            Waiting = true;
            bool signalled = Input.WaitOne(100);
            Waiting = false;
            if (!signalled) return 0x102;
            uint result = Volatile.Read(ref Result);
            Interlocked.Increment(ref Consumed);
            return result;
        }

        public void Pulse(uint result = 0)
        {
            Volatile.Write(ref Result, result);
            Interlocked.Add(ref _now, Period);   // advance BEFORE signalling: the waiter stamps after the wait returns
            Input.Set();
        }

        public void Dispose() => Input.Dispose();
    }

    /// <summary>Deliver one pulse, wait until the primitive actually consumed it, then wait until the waiter is back
    /// inside the wait. Auto-reset pulses must never be collapsed into one return (the scripted tick spacing would
    /// stop being one period), and no assertion may run against a tick the waiter has not finished publishing.</summary>
    private static void PulseAndWait(ControlledClock input, uint result = 0)
    {
        int consumed = Volatile.Read(ref input.Consumed);
        input.Pulse(result);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref input.Consumed) > consumed, 2000));
        Assert.True(SpinWait.SpinUntil(() => input.Waiting, 2000));
    }

    private static Win32CompositorClock NewClock(ControlledClock input, long hint = Period) =>
        new(input.Wait, () => input.Now, refreshPeriodHintQpc: hint, qpcFrequency: Freq);

    [Fact]
    public void UiDisarmCannotStopOrConsumeRenderTicks()
    {
        using var input = new ControlledClock();
        using var clock = NewClock(input);
        using var render = clock.CreateRenderSubscription();
        clock.Arm();
        render.SetActive(true);
        PulseAndWait(input);
        Assert.True(render.Tick.WaitOne(1000));
        clock.Disarm();
        PulseAndWait(input);
        Assert.True(render.Tick.WaitOne(1000));
        Assert.True(render.IsAvailable);

        render.SetActive(false);
        clock.Arm();
        long sequence = clock.TickSeq;
        PulseAndWait(input);
        Assert.True(SpinWait.SpinUntil(() => clock.TickSeq > sequence, 1000));
        Assert.False(render.Tick.WaitOne(0));
    }

    [Fact]
    public void WaitFailuresSynthesizeTicksAndNeverTakeTheClockAway()
    {
        using var input = new ControlledClock();
        using var clock = NewClock(input);
        using var render = clock.CreateRenderSubscription();
        render.SetActive(true);

        // The old clock latched off after three of these and never paced again this session. Now each one becomes a
        // software-paced tick on the lattice: the phase survives a topology change instead of being surrendered.
        for (int i = 0; i < 3; i++) PulseAndWait(input, WaitFailed);

        Assert.True(render.Tick.WaitOne(1000));
        Assert.True(render.IsAvailable);
        Assert.True(clock.IsAvailable);
        Assert.True(SpinWait.SpinUntil(() => clock.TickSeq > 0, 1000));

        clock.Reprobe();                       // still legal, still preserves the subscription
        PulseAndWait(input);
        Assert.True(render.Tick.WaitOne(1000));
        Assert.True(render.IsAvailable);
    }

    [Fact]
    public void ExportMissingIsTheOnlyPermanentLatch()
    {
        using var input = new ControlledClock();
        input.Throw = new EntryPointNotFoundException("DCompositionWaitForCompositorClock");
        using var clock = NewClock(input);
        using var render = clock.CreateRenderSubscription();
        render.SetActive(true);

        Assert.True(render.Tick.WaitOne(1000));   // woken once so it can select its fallback
        Assert.True(SpinWait.SpinUntil(() => !clock.IsAvailable, 1000));
        Assert.False(render.IsAvailable);

        input.Throw = null;
        clock.Reprobe();
        clock.Arm();
        PulseAndWait(input);
        Assert.True(render.Tick.WaitOne(1000));
        Assert.True(clock.IsAvailable);
        Assert.True(render.IsAvailable);
    }

    [Fact]
    public void AWindowOnASlowerPanelIsPacedAtThePanelRateNotTheCompositorRate()
    {
        using var input = new ControlledClock();
        using var clock = NewClock(input, hint: SlowWindowPeriod);   // 50 Hz window, 120 Hz compositor beat
        using var render = clock.CreateRenderSubscription();
        render.SetActive(true);
        clock.Arm();

        for (int i = 0; i < 12; i++) PulseAndWait(input);            // measure the beat, then confirm the mode switch

        Assert.True(clock.Decimating);
        Assert.Equal(Period, clock.MeasuredRefreshPeriodQpc);
        Assert.Equal(SlowWindowPeriod, clock.LatticePeriodQpc);

        long before = clock.TickSeq;
        long dropsBefore = clock.SlotDrops;
        for (int i = 0; i < 24; i++) PulseAndWait(input);             // 24 beats == 199.99 ms == ~10 panel refreshes

        long advanced = clock.TickSeq - before;
        Assert.InRange(advanced, 9, 11);
        Assert.Equal(24 - advanced, clock.SlotDrops - dropsBefore);   // every beat is either a slot or a counted drop
    }

    [Fact]
    public void DisposedSubscriberCanBeReplacedWithoutRecycledHandleSignals()
    {
        using var input = new ControlledClock();
        using var clock = NewClock(input);
        var old = clock.CreateRenderSubscription();
        old.SetActive(true);
        old.Dispose();
        using var replacement = clock.CreateRenderSubscription();
        replacement.SetActive(true);
        PulseAndWait(input);
        Assert.True(replacement.Tick.WaitOne(1000));
        old.SetActive(false); // stale disposal/arming may not disarm the new consumer
        PulseAndWait(input);
        Assert.True(replacement.Tick.WaitOne(1000));
    }
}
