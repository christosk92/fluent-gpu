using FluentGpu.Hosting;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary><c>FrameClock.PaceableTick</c>: per-frame motion the adaptive GPU governor may pace, unlike a
/// <c>FrameClock.Tick</c> poller; the Energy Saver power cap reaches both.</summary>
public sealed class PaceableFrameClockTests
{
    [Fact]
    public void The_governor_may_pace_a_paceable_poller_but_never_a_plain_one()
    {
        Assert.True(GpuGovernorWake.MayPace(WakeReasons.FrameClockPaceable));
        Assert.False(GpuGovernorWake.MayPace(WakeReasons.FrameClockPoller));
        Assert.False(GpuGovernorWake.MayPace(WakeReasons.FrameClockPaceable | WakeReasons.ScrollAnim));
    }

    [Fact]
    public void The_power_cap_reaches_both_frame_clocks()
    {
        Assert.Equal(WakeReasons.None, AppHost.PowerCapNeverPace & WakeReasons.FrameClockPaceable);
        Assert.Equal(WakeReasons.None, AppHost.PowerCapNeverPace & WakeReasons.FrameClockPoller);
    }
}
