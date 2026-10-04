using System;
using FluentGpu.Media.Adaptive;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The buffer-preserving half of the ABR switch policy: a measured downswitch is deferred while the forward buffer holds
/// 25 s or more, the forced probe is timed in wall time (10 s, doubling per failure), and <see cref="AbrSwitchGate"/>
/// paces switches (8 s minimum interval, emergency and cap exceptions) and makes a forced probe wait for evidence from its
/// own rung. The controller's clock is injected, so nothing waits.
/// </summary>
public sealed class AdaptiveSwitchPolicyTests
{
    private static readonly int[] TwoRungs = [300_000, 1_000_000];
    private const double JustBelowTheNextRung = 1_000.0;   // kbps: the 1 Mbps rung never clears the 0.85 climb budget

    private sealed class Clock
    {
        public long Now;
    }

    private static AdaptiveBitrateController NewController(Clock clock) => new()
    {
        UpgradeBuffer = TimeSpan.Zero,
        NowMs = () => clock.Now,
    };

    // ── a measured downswitch is gated on the forward buffer ────────────────────────────────────────────────────────────

    [Fact]
    public void MeasuredDownswitch_WithAFullBuffer_IsDeferred_AndWithAThinOneIsImmediate()
    {
        int[] bitrates = [300_000, 1_000_000, 3_000_000];
        var abr = new AdaptiveBitrateController { UpgradeBuffer = TimeSpan.FromSeconds(10) };
        abr.SeedCurrent(2);

        // 500 kbps cannot hold the 3 Mbps rung, but 30 s are already downloaded: ride the dip out.
        Assert.Equal(2, abr.Choose(bitrates, TimeSpan.FromSeconds(30), 500, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.DeferredDecrease, abr.LastDecisionReason);

        // 10 s is thin: the downswitch is immediate.
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(10), 500, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.Throughput, abr.LastDecisionReason);
    }

    [Fact]
    public void MeasuredDownswitch_DefersAtExactly25Seconds_AndNotJustBelow()
    {
        int[] bitrates = [300_000, 1_000_000];
        var abr = new AdaptiveBitrateController();
        abr.SeedCurrent(1);

        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromMilliseconds(24_999), 400, estimateIsPrior: false));
        abr.SeedCurrent(1);
        Assert.Equal(1, abr.Choose(bitrates, TimeSpan.FromSeconds(25), 400, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.DeferredDecrease, abr.LastDecisionReason);
    }

    [Fact]
    public void ADeferredDecrease_ReturnsBeforeTheForcedProbe_SoNoProbeFiresWhileADecreaseIsIndicated()
    {
        var clock = new Clock();
        var abr = NewController(clock);
        abr.SeedCurrent(1);   // sitting on the 1 Mbps rung while the link measures 500 kbps

        for (int i = 0; i < 12; i++)
        {
            clock.Now += 10_000;   // far past the probe interval each time
            Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(40), 500, estimateIsPrior: false));
            Assert.Equal(AbrDecisionReason.DeferredDecrease, abr.LastDecisionReason);
        }
    }

    [Fact]
    public void ADeferredDecrease_LeavesAProbeInFlight_AndTheLaterRevertStillCountsAsItsFailure()
    {
        var clock = new Clock();
        var abr = NewController(clock);

        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = 10_000;
        Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.ForcedProbe, abr.LastDecisionReason);

        // The probe rung cannot be held (500 kbps) but the buffer is full: kept, the probe is still in flight ...
        clock.Now = 11_000;
        Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), 500, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.DeferredDecrease, abr.LastDecisionReason);

        // ... until the buffer thins: the revert is the probe's failure, so the next probe waits 20 s, not 10.
        clock.Now = 12_000;
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), 500, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.Throughput, abr.LastDecisionReason);

        clock.Now = 13_000;   // steady again from here
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = 32_999;   // 19.999 s steady
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = 33_000;   // 20 s
        Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.ForcedProbe, abr.LastDecisionReason);
    }

    // ── the forced probe is timed ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForcedProbe_FiresAfter10SecondsOfSteadyTime_NotAfterThreeDecisions()
    {
        var clock = new Clock();
        var abr = NewController(clock);

        // One decision a second, as the protected session makes them: the old budget probed on the third.
        for (int i = 0; i < 10; i++)
        {
            clock.Now = i * 1_000L;
            Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        }
        clock.Now = 9_999;
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = 10_000;
        Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.ForcedProbe, abr.LastDecisionReason);
    }

    [Fact]
    public void ForcedProbe_BacksOffTo20And40Seconds_AfterEachRevert()
    {
        var clock = new Clock();
        var abr = NewController(clock);
        const long start = 0;

        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = start + 10_000;
        Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), JustBelowTheNextRung, estimateIsPrior: false));   // probe 1

        clock.Now += 1_000;   // the probe rung cannot be held: revert, failure 1
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), 500, estimateIsPrior: false));
        long steadyFrom = clock.Now + 1_000;
        clock.Now = steadyFrom;
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = steadyFrom + 19_999;
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = steadyFrom + 20_000;
        Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), JustBelowTheNextRung, estimateIsPrior: false));   // probe 2

        clock.Now += 1_000;   // revert again: failure 2
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), 500, estimateIsPrior: false));
        steadyFrom = clock.Now + 1_000;
        clock.Now = steadyFrom;
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = steadyFrom + 39_999;
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = steadyFrom + 40_000;
        Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(20), JustBelowTheNextRung, estimateIsPrior: false));   // probe 3
    }

    [Fact]
    public void DeclineLastDecision_ASwitchThatNeverHappenedIsNotAFailedProbe()
    {
        var clock = new Clock();
        var abr = NewController(clock);

        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = 10_000;
        Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));   // probe

        abr.DeclineLastDecision(0);   // the caller never made the switch
        Assert.Equal(0, abr.CurrentIndex);

        // The cadence is the base 10 s, not the backed-off 20 s a failure would have cost.
        clock.Now = 11_000;
        Assert.Equal(0, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        clock.Now = 21_000;
        Assert.Equal(1, abr.Choose(TwoRungs, TimeSpan.FromSeconds(30), JustBelowTheNextRung, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.ForcedProbe, abr.LastDecisionReason);
    }

    // ── AbrSwitchGate: the minimum interval ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate_FirstSwitchIsAlwaysAllowed_ThenASecondWithin8SecondsIsRefused()
    {
        var gate = new AbrSwitchGate();
        Assert.True(gate.Allows(1_000, isDecrease: false, forwardBufferedMs: 60_000, AbrDecisionReason.Throughput));

        gate.NoteSwitch(1_000);
        Assert.False(gate.Allows(1_001, isDecrease: false, forwardBufferedMs: 60_000, AbrDecisionReason.Throughput));
        Assert.False(gate.Allows(8_999, isDecrease: true, forwardBufferedMs: 60_000, AbrDecisionReason.Throughput));
        Assert.True(gate.Allows(9_000, isDecrease: false, forwardBufferedMs: 60_000, AbrDecisionReason.Throughput));
        Assert.True(gate.IntervalElapsed(9_000));
        Assert.False(gate.IntervalElapsed(8_999));
    }

    [Fact]
    public void Gate_AnEmergencyDecrease_BreaksTheInterval_ButAnIncreaseNeverDoes()
    {
        var gate = new AbrSwitchGate();
        gate.NoteSwitch(0);

        Assert.True(gate.Allows(1_000, isDecrease: true, forwardBufferedMs: 9_999, AbrDecisionReason.Throughput));
        Assert.False(gate.Allows(1_000, isDecrease: true, forwardBufferedMs: 10_000, AbrDecisionReason.Throughput));
        Assert.False(gate.Allows(1_000, isDecrease: false, forwardBufferedMs: 2_000, AbrDecisionReason.Throughput));
        Assert.False(gate.Allows(1_000, isDecrease: false, forwardBufferedMs: 2_000, AbrDecisionReason.ForcedProbe));
    }

    [Theory]
    [InlineData(AbrDecisionReason.CapDownswitch)]
    [InlineData(AbrDecisionReason.Capped)]
    [InlineData(AbrDecisionReason.Pinned)]
    public void Gate_CapAndPinReasons_BypassTheInterval(AbrDecisionReason reason)
    {
        var gate = new AbrSwitchGate();
        gate.NoteSwitch(0);
        Assert.True(gate.Allows(500, isDecrease: false, forwardBufferedMs: 60_000, reason));
    }

    [Fact]
    public void Gate_IntervalIsConfigurable_AndResetForgetsTheLastSwitch()
    {
        var gate = new AbrSwitchGate { MinSwitchIntervalMs = 2_000 };
        gate.NoteSwitch(0);
        Assert.False(gate.IntervalElapsed(1_999));
        Assert.True(gate.IntervalElapsed(2_000));

        gate.NoteSwitch(5_000);
        gate.Reset();
        Assert.True(gate.IntervalElapsed(5_001));
    }

    // ── AbrSwitchGate: a forced probe is judged on its own evidence ──────────────────────────────────────────────────────

    [Fact]
    public void Gate_AProbeIsHeldUntilItsRungHasLanded_AndAtLeastOneSampleHasFollowed()
    {
        var gate = new AbrSwitchGate();
        Assert.False(gate.HoldForProbeEvidence(acceptedSamples: 3, forwardBufferedMs: 60_000));   // no probe: never held

        gate.BeginProbe();
        Assert.True(gate.ProbeUnjudged);
        Assert.True(gate.HoldForProbeEvidence(3, 60_000));    // not landed yet
        Assert.True(gate.HoldForProbeEvidence(9, 60_000));    // samples of the OLD rung do not count

        gate.ProbeLanded(acceptedSamples: 9);                 // the probe rung is downloading from here
        Assert.True(gate.HoldForProbeEvidence(9, 60_000));    // no sample of its own yet
        Assert.False(gate.HoldForProbeEvidence(10, 60_000));  // one sample on the probe rung: judge it now
        Assert.False(gate.ProbeUnjudged);
        Assert.False(gate.HoldForProbeEvidence(10, 60_000));  // and it stays judged
    }

    [Fact]
    public void Gate_AProbeVerdictDoesNotWaitInEmergencyTerritory()
    {
        var gate = new AbrSwitchGate();
        gate.BeginProbe();
        Assert.False(gate.HoldForProbeEvidence(acceptedSamples: 0, forwardBufferedMs: 9_999));
        Assert.True(gate.ProbeUnjudged);   // not judged, merely not held: the decision may not wait

        gate.EndProbe();
        Assert.False(gate.ProbeUnjudged);
        Assert.False(gate.HoldForProbeEvidence(0, 60_000));
    }

    [Fact]
    public void Gate_ProbeLanded_IsIgnoredWhenNoProbeIsUnjudged_AndOnlyTheFirstLandingCounts()
    {
        var gate = new AbrSwitchGate();
        gate.ProbeLanded(5);   // an ordinary switch landing
        gate.BeginProbe();
        Assert.True(gate.HoldForProbeEvidence(100, 60_000));   // the stray landing set no baseline

        gate.ProbeLanded(7);
        gate.ProbeLanded(50);  // a second landing must not move the baseline up
        Assert.False(gate.HoldForProbeEvidence(8, 60_000));
    }
}
