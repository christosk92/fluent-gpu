using FluentGpu.Scroll;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Idle-power gate for <see cref="ScrollKernel.WakeActiveCount"/> (scroll-v3-plan §4): a KeepAlive-parked
/// body stays resident in the kernel's active list by design (so it resumes cleanly on unpark) but the per-tick
/// physics loop skips it outright, so it never does real work and must not, by itself, justify waking the render
/// loop. <see cref="ScrollKernel.ActiveCount"/> (raw residency, including parked bodies) is left unchanged for its
/// existing diagnostics/capacity-gate callers.</summary>
public sealed class ScrollKernelWakeCountTests
{
    private sealed class NullSink : IScrollSink
    {
        public void Apply(int node, in ScrollWrite w) { }
    }

    private static ScrollFrameSpec Frame(float extent, float viewport)
        => new(0, extent, 300f, viewport, 300f, 1f, false, 0f, 0f, 0f, null);

    [Fact]
    public void ParkedBody_CountsTowardActiveCount_ButNotWakeActiveCount()
    {
        var kernel = new ScrollKernel(new NullSink(), ScrollFeel.Shipping);
        const int node = 1;

        kernel.Port.Post(ScrollInput.Bind(node));
        kernel.Port.Post(ScrollInput.SetFrame(node, Frame(2000f, 400f)));
        kernel.Reclamp();

        // A non-immediate ScrollTo is a real Driven glide (Tick-only — not structural), so it lands MarkActive and
        // Activity=Driven (not settled) same as a genuine in-flight scroll.
        kernel.Port.Post(ScrollInput.ScrollTo(node, 300f, immediate: false));
        // Park in the SAME batch — the physics loop must never advance a parked body, so it stays un-settled.
        kernel.Port.Post(ScrollInput.Park(node, parked: true));

        var clock = new ScrollClock(0.0, 0.00833f, 0.0, 0.00833f);
        kernel.Tick(in clock);

        Assert.Equal(1, kernel.ActiveCount);
        Assert.Equal(0, kernel.WakeActiveCount);

        // Unparking the same (still-Driven, still-unsettled) body must bring it back into WakeActiveCount.
        kernel.Port.Post(ScrollInput.Park(node, parked: false));
        kernel.Tick(in clock);

        Assert.Equal(1, kernel.ActiveCount);
        Assert.Equal(1, kernel.WakeActiveCount);
    }

    [Fact]
    public void NoBodies_BothCountsAreZero()
    {
        var kernel = new ScrollKernel(new NullSink(), ScrollFeel.Shipping);
        Assert.Equal(0, kernel.ActiveCount);
        Assert.Equal(0, kernel.WakeActiveCount);
    }
}
