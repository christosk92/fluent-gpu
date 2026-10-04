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

    // ── F250: the engage/release thresholds are fractions of the measured display period ────────────

    private const double Hz60 = 1000.0 / 60.0, Hz120 = 1000.0 / 120.0, Hz144 = 1000.0 / 144.0;

    [Fact]
    public void At60Hz_TheThresholdsAreExactlyTheFixedCeilings()
    {
        GpuGovernorWake.Thresholds(Hz60, weakWithLiveVideo: false, out double engage, out double release);
        Assert.Equal(AppHost.GpuGovernorEngageMs, engage);     // 0.9 x 16.67 = 15 ms would be LESS responsive than 10
        Assert.Equal(AppHost.GpuGovernorReleaseMs, release);
    }

    [Theory]
    [InlineData(1000.0 / 120.0)]
    [InlineData(1000.0 / 144.0)]
    public void OnAFastPanel_TheThresholdsFollowThePeriod(double refreshMs)
    {
        GpuGovernorWake.Thresholds(refreshMs, weakWithLiveVideo: false, out double engage, out double release);
        Assert.Equal(GpuGovernorWake.EngageFraction * refreshMs, engage, 6);
        Assert.Equal(GpuGovernorWake.ReleaseFraction * refreshMs, release, 6);
        Assert.True(engage < AppHost.GpuGovernorEngageMs && release < AppHost.GpuGovernorReleaseMs);
    }

    [Fact]
    public void WeakTierWithLiveVideo_EngagesEarlierStill()
    {
        GpuGovernorWake.Thresholds(Hz120, weakWithLiveVideo: false, out double engage, out double release);
        GpuGovernorWake.Thresholds(Hz120, weakWithLiveVideo: true, out double weakEngage, out double weakRelease);
        Assert.Equal(GpuGovernorWake.WeakVideoEngageFraction * Hz120, weakEngage, 6);
        Assert.Equal(GpuGovernorWake.WeakVideoReleaseFraction * Hz120, weakRelease, 6);
        Assert.True(weakEngage < engage && weakRelease < release);
        // At 60 Hz both pairs are still bounded by the fixed ceilings (the lower fraction only bites on a faster panel).
        GpuGovernorWake.Thresholds(Hz60, weakWithLiveVideo: true, out double e60, out double r60);
        Assert.True(e60 <= AppHost.GpuGovernorEngageMs && r60 <= AppHost.GpuGovernorReleaseMs);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void AnUnknownPeriod_KeepsTheFixedCeilings(double refreshMs)
    {
        GpuGovernorWake.Thresholds(refreshMs, weakWithLiveVideo: true, out double engage, out double release);
        Assert.Equal(AppHost.GpuGovernorEngageMs, engage);
        Assert.Equal(AppHost.GpuGovernorReleaseMs, release);
    }

    [Theory]
    [InlineData(1000.0 / 24.0)]
    [InlineData(1000.0 / 60.0)]
    [InlineData(1000.0 / 75.0)]
    [InlineData(1000.0 / 120.0)]
    [InlineData(1000.0 / 144.0)]
    [InlineData(1000.0 / 240.0)]
    public void ReleaseIsAlwaysStrictlyBelowEngage_SoTheGovernorHasHysteresis(double refreshMs)
    {
        foreach (bool weakVideo in new[] { false, true })
        {
            GpuGovernorWake.Thresholds(refreshMs, weakVideo, out double engage, out double release);
            Assert.True(release > 0.0 && release < engage, $"{refreshMs:F2} ms weakVideo={weakVideo}: release {release} engage {engage}");
        }
    }

    /// <summary>The on-box case from the audit: at 120 Hz a steady ~8.4 ms UI frame misses every vblank, and the fixed 10 ms
    /// engage point never fires; the period-relative one does.</summary>
    [Fact]
    public void At120Hz_ASteady8Point4MsFrameEngagesTheGovernor_WhereTheFixedThresholdNeverDid()
    {
        GpuGovernorWake.Thresholds(Hz120, weakWithLiveVideo: false, out double engage, out double release);
        ulong fixedSeq = 0, liveSeq = 0;
        double fixedEma = 0.0, liveEma = 0.0;
        bool fixedEngaged = false, liveEngaged = false;
        for (ulong s = 1; s <= 80; s++)
        {
            AppHost.TryAdvanceAdaptiveGpuGovernor(8.4, s, ref fixedSeq, ref fixedEma, ref fixedEngaged);   // the default 10 / 8 ceilings
            AppHost.TryAdvanceAdaptiveGpuGovernor(8.4, s, ref liveSeq, ref liveEma, ref liveEngaged, engage, release);
        }
        Assert.False(fixedEngaged);
        Assert.True(liveEngaged);

        // Release needs the EMA to fall to the release fraction: 6.5 ms samples (0.78 of the period) are not enough, 4 ms are.
        for (ulong s = 81; s <= 160; s++) AppHost.TryAdvanceAdaptiveGpuGovernor(6.5, s, ref liveSeq, ref liveEma, ref liveEngaged, engage, release);
        Assert.True(liveEngaged);   // 6.5 ms is above the 5.83 ms release point: it holds
        for (ulong s = 161; s <= 260; s++) AppHost.TryAdvanceAdaptiveGpuGovernor(4.0, s, ref liveSeq, ref liveEma, ref liveEngaged, engage, release);
        Assert.False(liveEngaged);
    }
}
