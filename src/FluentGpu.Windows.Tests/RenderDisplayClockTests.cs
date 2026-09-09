using System.Threading;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

public sealed class RenderDisplayClockTests
{
    private sealed class ControlledClock : IDisposable
    {
        public readonly AutoResetEvent Input = new(false);
        public uint Result;
        public int Consumed;
        public uint Wait()
        {
            if (!Input.WaitOne(100)) return 0x102;
            uint result = Volatile.Read(ref Result);
            Interlocked.Increment(ref Consumed);
            return result;
        }
        public void Pulse(uint result = 0) { Volatile.Write(ref Result, result); Input.Set(); }
        public void Dispose() => Input.Dispose();
    }

    [Fact]
    public void UiDisarmCannotStopOrConsumeRenderTicks()
    {
        using var input = new ControlledClock();
        using var clock = new Win32CompositorClock(input.Wait);
        using var render = clock.CreateRenderSubscription();
        clock.Arm();
        render.SetActive(true);
        input.Pulse();
        Assert.True(render.Tick.WaitOne(1000));
        clock.Disarm();
        input.Pulse();
        Assert.True(render.Tick.WaitOne(1000));
        Assert.True(render.IsAvailable);

        render.SetActive(false);
        clock.Arm();
        long sequence = clock.TickSeq;
        input.Pulse();
        Assert.True(SpinWait.SpinUntil(() => clock.TickSeq > sequence, 1000));
        Assert.False(render.Tick.WaitOne(0));
    }

    [Fact]
    public void CapabilityFailureWakesRenderAndReprobePreservesItsSubscription()
    {
        using var input = new ControlledClock();
        using var clock = new Win32CompositorClock(input.Wait);
        using var render = clock.CreateRenderSubscription();
        render.SetActive(true);
        // Three consecutive failed waits take the unavailable branch; no platform API is called in this test.
        input.Result = 0xFFFFFFFF;
        for (int i = 0; i < 3; i++)
        {
            int consumed = Volatile.Read(ref input.Consumed);
            input.Pulse(0xFFFFFFFF);
            // Wait until the primitive consumed this controlled result before enqueueing the next one.
            // Auto-reset pulses must not be collapsed into one failure.
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref input.Consumed) > consumed, 1000));
        }
        Assert.True(render.Tick.WaitOne(1000));
        Assert.False(render.IsAvailable);
        clock.Reprobe();
        input.Pulse();
        Assert.True(render.Tick.WaitOne(1000));
        Assert.True(render.IsAvailable);
    }

    [Fact]
    public void DisposedSubscriberCanBeReplacedWithoutRecycledHandleSignals()
    {
        using var input = new ControlledClock();
        using var clock = new Win32CompositorClock(input.Wait);
        var old = clock.CreateRenderSubscription();
        old.SetActive(true);
        old.Dispose();
        using var replacement = clock.CreateRenderSubscription();
        replacement.SetActive(true);
        input.Pulse();
        Assert.True(replacement.Tick.WaitOne(1000));
        old.SetActive(false); // stale disposal/arming may not disarm the new consumer
        input.Pulse();
        Assert.True(replacement.Tick.WaitOne(1000));
    }
}
