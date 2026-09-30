using FluentGpu.Hosting;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The adaptive GPU governor paces AMBIENT work only. A touchpad contact produces frames through the
/// frame-aligned scroll producer (<see cref="WakeReasons.ScrollProducer"/>: DirectManipulation engaged/pending, or a
/// hi-res wheel gesture live) — including the frames where the finger rests on the pad and no plan is moving yet (the
/// engage window, a resting finger), which carry NO ScrollAnim bit. Pacing those to 30 fps is exactly the "half fps
/// while scrolling" class: the next contact sample waits up to a governor period before it is even pumped.</summary>
public sealed class GpuGovernorWakeTests
{
    [Fact]
    public void AScrollProducerFrameIsNeverPaced()
    {
        Assert.False(GpuGovernorWake.MayPace(WakeReasons.ScrollProducer));                        // resting finger / engage window
        Assert.False(GpuGovernorWake.MayPace(WakeReasons.ScrollProducer | WakeReasons.ImageCrossfades));
        Assert.False(GpuGovernorWake.MayPace(WakeReasons.ScrollProducer | WakeReasons.Anim));
    }

    [Fact]
    public void AmbientWorkAloneIsPaceable_AndEveryInteractionBitExemptsTheFrame()
    {
        Assert.True(GpuGovernorWake.MayPace(WakeReasons.ImageCrossfades));
        Assert.True(GpuGovernorWake.MayPace(WakeReasons.Anim | WakeReasons.ImagesPending));
        Assert.True(GpuGovernorWake.MayPace(WakeReasons.PopupAnim));   // deliberately paceable (see NeverPace)
        foreach (var bit in new[]
                 {
                     WakeReasons.ScrollAnim, WakeReasons.ScrollProducer, WakeReasons.Repeat, WakeReasons.DragActive,
                     WakeReasons.DragDropWork, WakeReasons.GestureHold, WakeReasons.TouchPress, WakeReasons.FrameClockPoller,
                 })
            Assert.False(GpuGovernorWake.MayPace(bit | WakeReasons.ImageCrossfades), bit.ToString());
    }
}
