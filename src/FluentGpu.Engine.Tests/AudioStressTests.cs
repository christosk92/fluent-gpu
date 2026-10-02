using System;
using System.Collections.Generic;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The policy-level load-test model (<see cref="AudioStressModel"/>, plan §4.18, V-PE34): a scripted producer, the 2 s decode-ahead
/// ring, the 100 ms device buffer and a starvation policy — no threads, no clock, no engine session. These are the numbers the real
/// session is then driven against (<c>StarvationRecoveryTests</c>) and the on-box runner measures (<c>--stress-audio</c>).
///
/// EXPECTATIONS ARE DERIVED FOR THE 2 s TARGET AND THE SHALLOW F2 SILENCE (WP-1b), by hand, from the model's own arithmetic (10 ms steps,
/// ring and device full at t = 0; the shallow floor is <see cref="PcmAudioSession.SilencePaddingFloorBlocks"/> × 10 ms = 20 ms of device
/// padding — the RT queues silence only below it):
///   * the ring drains 10 ms per step, so a silent producer exhausts a full ring at t ≈ 2.0 s and the 100 ms device buffer at ≈ 2.1 s —
///     an absence up to ~2.0 s is INAUDIBLE. A 2.5 s absence is one incident: the speakers run out of content at 2.10 s and the returning
///     audio (ring ≥ 100 ms at 2.52 s) reaches them at 2.53 s, behind at most one block of silence — a gap of ≈ 430 ms, about one
///     block (not a whole device buffer) more than the legacy stop/start path (the full-depth silence this replaced: ≈ 510 ms);
///   * a producer FASTER than real time never starves (so a "1.2×" test proves nothing); only a sustained shortfall (< 1×) can loop.
///     At 0.8× the ring drains 0.2 ms/ms from the low-water mark: it empties at ≈ 6.0 s, and from then on the cushion policy needs
///     100, 200, 400, 800, 1600 ms of ring before it resumes (each fill takes threshold/0.8) — five incidents in 20 s, none after.
///     Because the device FIFO is shallow when a starve ends, the RT refills it from the ring in 3-block bursts and the ring is ≈ one
///     device buffer shallower than it was under full-depth silence: the incidents come at ≈ 5.97, 6.22, 7.07, 9.17 and 13.77 s;
///   * a stop-the-world pause of T ms is audible only for T − (device buffer) — and not at all while T fits in the buffer.
///
/// Every test builds its own policies and scenarios: no static mutable state, nothing shared between parallel tests.
/// </summary>
public sealed class AudioStressTests
{
    private const double Block = 10.0;
    private const double Device = 100.0;

    // ── the scripted producer ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProducerSchedule_RateAt_LaterWindowsWin_AndAbsencesAreTheZeroRateWindows()
    {
        var schedule = ProducerSchedule.Steady(4.0)
            .With(1000, 2000, 0.8)           // [1000, 3000) slow
            .WithAbsence(2000, 500);         // [2000, 2500) absent, inside the slow window — the later window wins

        Assert.Equal(4.0, schedule.Rate);
        Assert.Equal(4.0, schedule.RateAt(0));
        Assert.Equal(0.8, schedule.RateAt(1500));
        Assert.Equal(0.0, schedule.RateAt(2000));
        Assert.Equal(0.0, schedule.RateAt(2499.9));
        Assert.Equal(0.8, schedule.RateAt(2500));
        Assert.Equal(4.0, schedule.RateAt(3000));
        Assert.True(schedule.IsAbsent(2200));
        Assert.False(schedule.IsAbsent(1500));

        var absences = new List<RateWindow>(schedule.Absences);
        Assert.Single(absences);
        Assert.Equal(new RateWindow(2000, 2500, 0.0), absences[0]);
    }

    [Fact]
    public void ProducerSchedule_IsImmutable_WithReturnsANewSchedule()
    {
        var steady = ProducerSchedule.Steady(2.0);
        var slowed = steady.With(0, 1000, 0.5);

        Assert.Empty(steady.Windows);
        Assert.Single(slowed.Windows);
        Assert.Equal(2.0, steady.RateAt(500));
        Assert.Equal(0.5, slowed.RateAt(500));
    }

    [Fact]
    public void StressConfig_BuiltFromTheEnginesD8RingSizing_IsTheDefaultModel()
    {
        // 10 ms blocks, 2 s ahead, 4 s ring, 1 s kept behind — the one RingSizing the feed and every prepared voice use.
        var d8 = new RingSizing(BlockMs: 10.0, AheadMs: 2000.0, RingMs: 4000.0, KeepBehindMs: 1000.0);

        Assert.Equal(StressConfig.Default, StressConfig.From(d8));
        Assert.Equal(3000.0, StressConfig.Default.GrowAheadCapMs);
    }

    // ── the model itself ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HealthyProducer_NeverStarves_AndEveryMillisecondIsContent()
    {
        var scenario = new StressScenario("healthy", 10_000, ProducerSchedule.Steady(4.0), StressConfig.Default);

        foreach (IStarvationPolicy policy in new IStarvationPolicy[] { new LegacyStarvationPolicy(), new CushionStarvationPolicy() })
        {
            StarvationVerdict v = AudioStressModel.Run(scenario, policy);
            Assert.True(v.Incidents == 0 && v.AudibleGaps == 0 && v.DeviceStops == 0, v.Describe());
            Assert.Equal(10_000.0, v.ContentPlayedMs, 6);
            Assert.Equal(0.0, v.TotalGapMs);
        }
    }

    [Fact]
    public void Run_IsDeterministic_TwoRunsOfOneScenarioAreIdentical()
    {
        StressScenario scenario = AudioStressModel.SlowThenRecover(0.8, 20_000, 1.5, 40_000);

        StarvationVerdict a = AudioStressModel.Run(scenario, new CushionStarvationPolicy());
        StarvationVerdict b = AudioStressModel.Run(scenario, new CushionStarvationPolicy());

        Assert.Equal(a.Describe(), b.Describe());
        Assert.Equal(a.ContentPlayedMs, b.ContentPlayedMs);
        Assert.Equal(a.IncidentLog.Count, b.IncidentLog.Count);
        for (int i = 0; i < a.IncidentLog.Count; i++) Assert.Equal(a.IncidentLog[i], b.IncidentLog[i]);
    }

    [Fact]
    public void Run_ResetsThePolicy_SoAPolicyObjectCanBeReused()
    {
        var policy = new CushionStarvationPolicy();
        StressScenario scenario = AudioStressModel.Absence(2_500, 8_000);

        StarvationVerdict first = AudioStressModel.Run(scenario, policy);
        StarvationVerdict second = AudioStressModel.Run(scenario, policy);

        Assert.Equal(first.Describe(), second.Describe());
        Assert.Equal(CushionStarvationPolicy.DefaultInitialResumeMs * 2, second.FinalResumeThresholdMs);   // one incident → one doubling, not two
    }

    [Fact]
    public void Run_RejectsAConfigThatCannotStep()
    {
        var scenario = new StressScenario("bad", 1_000, ProducerSchedule.Steady(4.0), new StressConfig(BlockMs: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => AudioStressModel.Run(scenario, new CushionStarvationPolicy()));
    }

    // ── absence: the producer thread descheduled ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Absence_ThatFitsInTheRingAndTheDevice_IsInaudible_ForBothPolicies()
    {
        // 1.8 s < ring 2.0 s: the ring bottoms out at ≈ 210 ms when the producer returns — no starve, no gap, no device stop.
        StressScenario scenario = AudioStressModel.Absence(1_800, 6_000);

        foreach (IStarvationPolicy policy in new IStarvationPolicy[] { new LegacyStarvationPolicy(), new CushionStarvationPolicy() })
        {
            StarvationVerdict v = AudioStressModel.Run(scenario, policy);
            Assert.True(v.Incidents == 0 && v.AudibleGaps == 0 && v.DeviceStops == 0, v.Describe());
        }
    }

    [Fact]
    public void Absence_2500ms_Cushion_IsOneBoundedIncident_AndNeverStopsTheDevice()
    {
        // Step by step (10 ms steps): the ring empties in the step at t = 2.01 s (the incident edge), while the device still holds 90 ms of
        // CONTENT. Shallow silence submits nothing until the device is down to 10 ms (the floor is 20), so the speakers run out of content
        // at 2.10 s and play silence from there. The producer returns at 2.5 s (4× = 40 ms per step): the ring is 40, 80, 120 ms at
        // 2.50, 2.51, 2.52 s — the 100 ms cushion is met at 2.52 s, the RT resumes behind the 10 ms of silence the device still holds, and
        // the first content reaches the speakers at 2.53 s. The audible gap is [2.10, 2.53) = 430 ms; the starve (edge → resume) 510 ms.
        StarvationVerdict v = AudioStressModel.Run(AudioStressModel.Absence(2_500, 8_000), new CushionStarvationPolicy());

        Assert.True(v.Incidents == 1 && v.Resumes == 1 && v.AudibleGaps == 1, v.Describe());
        Assert.Equal(0, v.DeviceStops);
        Assert.InRange(v.LongestGapMs, 420, 440);
        GapRecord gap = Assert.Single(v.GapLog);
        Assert.InRange(gap.StartMs, 2_090, 2_110);
        IncidentRecord incident = Assert.Single(v.IncidentLog);
        Assert.True(incident.Resumed);
        Assert.Equal(CushionStarvationPolicy.DefaultInitialResumeMs, incident.ResumeThresholdMs);
        Assert.InRange(incident.StarvedMs, 490, 530);
        Assert.InRange(incident.AtMs, 1_990, 2_030);
        Assert.True(v.FinalRingTargetMs > 2_000, "GrowAhead fires once for the incident: " + v.Describe());
    }

    [Fact]
    public void Absence_2500ms_Legacy_StopsAndRestartsTheDevice()
    {
        // Same absence: the legacy path withholds, lets the device drain (≈ 2.1 s), STOPS it, and restarts 15 ms after the ring holds 20 ms.
        StarvationVerdict v = AudioStressModel.Run(AudioStressModel.Absence(2_500, 8_000), new LegacyStarvationPolicy());

        Assert.True(v.Incidents == 1 && v.Resumes == 1 && v.AudibleGaps == 1, v.Describe());
        Assert.Equal(1, v.DeviceStops);
        Assert.InRange(v.LongestGapMs, 350, 500);
    }

    [Fact]
    public void Absence_2500ms_ShallowCushionPaysAboutOneBlockMoreGapThanLegacy_ForNoDeviceStop()
    {
        // The honest trade (see the file header): legacy lets the device run dry (a hole) and restarts it 15 ms after the ring holds 20 ms
        // — its content reaches the speakers at 2.52 s (gap 420 ms). F2 resumes at 2.52 s too (the 100 ms cushion) and the content queues
        // behind ONE block of silence: 2.53 s (gap 430 ms). The ~90 ms a full-depth device FIFO of silence used to add is gone.
        StressScenario scenario = AudioStressModel.Absence(2_500, 8_000);
        StarvationVerdict cushion = AudioStressModel.Run(scenario, new CushionStarvationPolicy());
        StarvationVerdict legacy = AudioStressModel.Run(scenario, new LegacyStarvationPolicy());

        double extra = cushion.LongestGapMs - legacy.LongestGapMs;
        Assert.InRange(extra, 0, 2 * Block);
        Assert.Equal(0, cushion.DeviceStops);
        Assert.Equal(1, legacy.DeviceStops);
    }

    [Fact]
    public void Absence_2500ms_ShallowSilenceSavesAboutADeviceBufferOfGap_ComparedWithFullDepthSilence()
    {
        // The pre-WP-1b F2 queued silence ALL the way to the top of the 100 ms device FIFO, so the returning audio sat behind ~90 ms of it:
        // the content reaches the speakers at 2.61 s instead of 2.53 s (gap 510 ms vs 430 ms). An infinite floor models that design.
        StressScenario scenario = AudioStressModel.Absence(2_500, 8_000);
        StarvationVerdict shallow = AudioStressModel.Run(scenario, new CushionStarvationPolicy());
        StarvationVerdict deep = AudioStressModel.Run(scenario, new CushionStarvationPolicy(silencePaddingFloorBlocks: double.PositiveInfinity));

        Assert.InRange(deep.LongestGapMs, 500, 520);
        Assert.InRange(deep.LongestGapMs - shallow.LongestGapMs, 70, 90);
        Assert.Equal(shallow.Incidents, deep.Incidents);          // the starve itself (ring empty → cushion met) is identical
        Assert.Equal(shallow.IncidentLog[0].StarvedMs, deep.IncidentLog[0].StarvedMs);
    }

    // ── slow producer: the stutter loop ──────────────────────────────────────────────────────────────────────────────

    // 0.8× for 20 s, then 1.5×, 40 s in all. NOT 1.2×: a producer faster than real time never starves, with any policy.
    private static StressScenario SlowProducer() => AudioStressModel.SlowThenRecover(0.8, 20_000, 1.5, 40_000);

    [Fact]
    public void SlowProducer_Legacy_IsTheStutterLoop()
    {
        StarvationVerdict v = AudioStressModel.Run(SlowProducer(), new LegacyStarvationPolicy());

        // From ≈ 6 s the ring refills 20 ms (25 ms of production) and resumes, drains it in two steps and starves again: a resume every
        // ≈ 40 ms (≈ 25 per second — the model's loop period), each cycle ending in a hole at the speakers once the device buffer is gone.
        // How often the device is STOPPED inside that loop depends on the phase of the 8 ms production quantum against the device level
        // (it settles into a limit cycle that rarely stops it), so only the loop itself is pinned, not the stop count.
        Assert.True(v.StutterLoop, v.Describe());
        Assert.True(v.PeakResumesPerSecond > StarvationVerdict.StutterResumesPerSecond, v.Describe());
        Assert.True(v.Resumes >= 50, v.Describe());
        Assert.True(v.AudibleGaps >= 50, v.Describe());
    }

    [Fact]
    public void SlowProducer_Cushion_IsNotTheStutterLoop_AndNeverStopsTheDevice()
    {
        StarvationVerdict v = AudioStressModel.Run(SlowProducer(), new CushionStarvationPolicy());

        Assert.False(v.StutterLoop, v.Describe());
        Assert.Equal(0, v.DeviceStops);
        Assert.InRange(v.Incidents, 4, 6);                      // five by hand: ≈ 5.97, 6.22, 7.07, 9.17, 13.77 s (shallow silence)
        Assert.Equal(v.Incidents, v.AudibleGaps);               // one audible gap per incident, no device holes
        Assert.Equal(v.Incidents, v.Resumes);                   // and every one of them resumed
    }

    [Fact]
    public void SlowProducer_ShallowSilence_RefillsTheDeviceFromTheRing_SoTheSecondStarveComesSoonerThanUnderFullDepthSilence()
    {
        // First starve at 5.97 s (both designs: the ring empties on the producer's schedule, not the device's), resumed at 6.10 s with the ring
        // at 104 ms. Full-depth silence left the device FIFO already full, so the RT only matched the device's 10 ms per step and the ring
        // stayed ≈ 104 ms, draining 2 ms per step (0.8× against 10 ms): empty again ≈ 50 steps later, at ≈ 6.58 s. Shallow silence leaves the
        // device FIFO at ≈ 18 ms, so the RT bursts 3 blocks (30 ms) per wake until it is full again: the ring falls from 104 to ≈ 16 ms by
        // 6.13 s, and the same 2 ms per step empties it at ≈ 6.21 s — the second incident edge is ≈ 6.22 s.
        StressScenario scenario = SlowProducer();

        StarvationVerdict shallow = AudioStressModel.Run(scenario, new CushionStarvationPolicy());
        StarvationVerdict deep = AudioStressModel.Run(scenario, new CushionStarvationPolicy(silencePaddingFloorBlocks: double.PositiveInfinity));

        Assert.InRange(shallow.IncidentLog[0].AtMs, 5_950, 5_990);
        Assert.Equal(shallow.IncidentLog[0].AtMs, deep.IncidentLog[0].AtMs);
        Assert.InRange(shallow.IncidentLog[1].AtMs, 6_150, 6_300);
        Assert.InRange(deep.IncidentLog[1].AtMs, 6_500, 6_650);
        // and the first gap, 6.07 → 6.12 s instead of 6.07 → 6.19 s, is the bigger win: the content queues behind one block, not a device buffer
        Assert.InRange(shallow.GapLog[0].DurationMs, 40, 60);
        Assert.InRange(deep.GapLog[0].DurationMs, 110, 135);
    }

    [Fact]
    public void SlowProducer_Cushion_DoublesPerIncident_AndEachStarveIsBoundedByItsOwnCushion()
    {
        StarvationVerdict v = AudioStressModel.Run(SlowProducer(), new CushionStarvationPolicy());

        double expected = CushionStarvationPolicy.DefaultInitialResumeMs;
        for (int i = 0; i < v.IncidentLog.Count; i++)
        {
            IncidentRecord incident = v.IncidentLog[i];
            Assert.True(incident.Resumed, $"incident {i} never resumed: " + v.Describe());
            Assert.Equal(expected, incident.ResumeThresholdMs);                       // 100, 200, 400, 800, 1600, then capped at the 2 s ahead
            double fill = incident.ResumeThresholdMs / 0.8;                           // the ring refills at 0.8 ms per ms
            Assert.InRange(incident.StarvedMs, fill - 2 * Block, fill + 2 * Block);   // the gap is the fill time, give or take the step
            expected = Math.Min(expected * 2, StressConfig.Default.TargetAheadMs);
        }
    }

    [Fact]
    public void SlowProducer_BothPolicies_StopStarvingOnceTheProducerRecovers()
    {
        foreach (IStarvationPolicy policy in new IStarvationPolicy[] { new LegacyStarvationPolicy(), new CushionStarvationPolicy() })
        {
            StarvationVerdict v = AudioStressModel.Run(SlowProducer(), policy);
            foreach (IncidentRecord incident in v.IncidentLog)
                Assert.True(incident.AtMs <= 20_500, $"an incident at {incident.AtMs} ms, after the producer recovered at 20 s: " + v.Describe());
        }
    }

    [Fact]
    public void SlowProducer_TheShortWindow_CushionIsFourIncidents_AndLegacyStillStutters()
    {
        // The plan's scenario as first written — 0.8× for 10 s, then 1.5× — against the 2 s target: the ring only empties at ≈ 6.0 s. With
        // shallow silence the cushion gets incidents at ≈ 5.97, 6.22, 7.07 and 9.17 s (thresholds 100/200/400/800 ms) — the fourth is
        // still starved when the producer recovers at 10 s (1.5×) and resumes at ≈ 10.09 s having waited 0.92 s of the 1 s a 0.8× producer
        // needs for 800 ms. (With full-depth silence the ring stayed ≈ 85 ms deeper after every resume: three incidents, at ≈ 6.0, 6.6 and
        // 7.8 s; the third ended at 8.3 s and the ring then lasted until the producer recovered.)
        StressScenario scenario = AudioStressModel.SlowThenRecover(0.8, 10_000, 1.5, 30_000);

        StarvationVerdict cushion = AudioStressModel.Run(scenario, new CushionStarvationPolicy());
        StarvationVerdict legacy = AudioStressModel.Run(scenario, new LegacyStarvationPolicy());

        Assert.Equal(4, cushion.Incidents);
        Assert.InRange(cushion.IncidentLog[3].AtMs, 9_100, 9_250);
        Assert.InRange(cushion.IncidentLog[3].StarvedMs, 880, 960);
        Assert.False(cushion.StutterLoop, cushion.Describe());
        Assert.Equal(0, cushion.DeviceStops);
        Assert.True(legacy.StutterLoop, legacy.Describe());
        Assert.True(legacy.Resumes > cushion.Resumes * 10, $"{legacy.Describe()} vs {cushion.Describe()}");
    }

    // ── stop-the-world pause: both threads frozen, the device plays on ───────────────────────────────────────────────

    [Fact]
    public void GcPause_ShorterThanTheDeviceBuffer_IsInaudible()
    {
        StarvationVerdict v = AudioStressModel.Run(AudioStressModel.GcPause(60, 1_000, 3_000), new CushionStarvationPolicy());

        Assert.True(v.Incidents == 0 && v.AudibleGaps == 0, v.Describe());
    }

    [Theory]
    [InlineData(300)]
    [InlineData(500)]
    [InlineData(1_000)]
    public void GcPause_LongerThanTheDeviceBuffer_IsAudibleForThePauseMinusTheBuffer(int pauseMs)
    {
        StarvationVerdict v = AudioStressModel.Run(AudioStressModel.GcPause(pauseMs, 1_000, 4_000), new CushionStarvationPolicy());

        Assert.Equal(1, v.AudibleGaps);
        Assert.InRange(v.LongestGapMs, pauseMs - Device - 2 * Block, pauseMs - Device + 2 * Block);
        Assert.Equal(0, v.Incidents);                           // the RT was frozen too: it never saw an empty ring
        Assert.Equal(0, v.DeviceStops);
    }

    // ── the policies, one decision at a time ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CushionPolicy_DoublesAtEveryResume_AndCapsAtTheAheadTarget()
    {
        var policy = new CushionStarvationPolicy();
        double[] needed = [100, 200, 400, 800, 1600, 2000, 2000];
        double now = 0;
        const double belowTheFloor = 10;   // the device holds 10 ms: under the 20 ms shallow floor, so the starved slot gets silence

        for (int i = 0; i < needed.Length; i++)
        {
            Assert.Equal(needed[i], policy.ResumeThresholdMs);

            StarvationStep edge = policy.Decide(new RtView(now, 0, belowTheFloor, Block, 2_000));
            Assert.Equal(BlockAction.Silence, edge.Action);
            Assert.Equal(StarvationEvents.Incident | StarvationEvents.GrowAhead, edge.Events);
            now += Block;

            StarvationStep below = policy.Decide(new RtView(now, needed[i] - 1, belowTheFloor, Block, 2_000));
            Assert.Equal(BlockAction.Silence, below.Action);
            Assert.Equal(StarvationEvents.None, below.Events);
            now += Block;

            StarvationStep resume = policy.Decide(new RtView(now, needed[i], 90, Block, 2_000));   // the resume is not gated by the device level
            Assert.Equal(BlockAction.Content, resume.Action);
            Assert.Equal(StarvationEvents.Resume, resume.Events);
            now += Block;
        }
    }

    [Fact]
    public void CushionPolicy_QueuesSilenceOnlyBelowTheDevicePaddingFloor_AndWithholdsAtOrAboveIt()
    {
        // RenderSilence's floor: SilencePaddingFloorBlocks (2) render blocks = 20 ms of device padding at the 10 ms block. At or above it the RT
        // submits nothing (the device plays on what it holds); below it, one block of silence goes in.
        var policy = new CushionStarvationPolicy();
        double floor = PcmAudioSession.SilencePaddingFloorBlocks * Block;
        Assert.Equal(20.0, floor);

        // The starve edge with 90 ms of real audio still queued: still ONE incident (raised at the edge), but nothing is queued behind it.
        StarvationStep edge = policy.Decide(new RtView(0, 0, 90, Block, 2_000));
        Assert.Equal(BlockAction.Withhold, edge.Action);
        Assert.Equal(StarvationEvents.Incident | StarvationEvents.GrowAhead, edge.Events);

        Assert.Equal(BlockAction.Withhold, policy.Decide(new RtView(10, 0, floor, Block, 2_000)).Action);          // exactly the floor: enough
        Assert.Equal(BlockAction.Silence, policy.Decide(new RtView(20, 0, floor - 0.5, Block, 2_000)).Action);    // just below: top up
        Assert.Equal(BlockAction.Silence, policy.Decide(new RtView(30, 0, 0, Block, 2_000)).Action);              // a dry device is topped up too
        StarvationStep stillBelowCushion = policy.Decide(new RtView(40, 99, 90, Block, 2_000));
        Assert.Equal(BlockAction.Withhold, stillBelowCushion.Action);                                              // starved + deep device: withheld
        Assert.Equal(StarvationEvents.None, stillBelowCushion.Events);                                             // and no second incident edge
    }

    [Fact]
    public void CushionPolicy_WithAnInfiniteFloor_IsTheFullDepthSilenceOfThePreWp1bSession()
    {
        var policy = new CushionStarvationPolicy(silencePaddingFloorBlocks: double.PositiveInfinity);

        StarvationStep edge = policy.Decide(new RtView(0, 0, 90, Block, 2_000));
        Assert.Equal(BlockAction.Silence, edge.Action);                                       // however deep the device FIFO already is
        Assert.Equal(StarvationEvents.Incident | StarvationEvents.GrowAhead, edge.Events);
        Assert.Equal(BlockAction.Silence, policy.Decide(new RtView(10, 50, 99, Block, 2_000)).Action);
    }

    [Fact]
    public void CushionPolicy_DefaultFloor_IsTheEnginesOwnConstant()
    {
        // The model moves with PcmAudioSession.SilencePaddingFloorBlocks: one block below the floor silence is queued, at it nothing is.
        var policy = new CushionStarvationPolicy();
        policy.Decide(new RtView(0, 0, 0, Block, 2_000));    // edge

        double floorMs = PcmAudioSession.SilencePaddingFloorBlocks * Block;
        Assert.Equal(BlockAction.Silence, policy.Decide(new RtView(10, 0, floorMs - Block, Block, 2_000)).Action);
        Assert.Equal(BlockAction.Withhold, policy.Decide(new RtView(20, 0, floorMs, Block, 2_000)).Action);
    }

    [Fact]
    public void CushionPolicy_HalvesAfter30SecondsWithoutAnIncident_OncePerWindow_NeverBelowTheFloor()
    {
        var policy = new CushionStarvationPolicy();

        // two incidents → 400 ms, the last one resuming at t = 30
        policy.Decide(new RtView(0, 0, 90, Block, 2_000));
        policy.Decide(new RtView(10, 100, 90, Block, 2_000));
        policy.Decide(new RtView(20, 0, 90, Block, 2_000));
        policy.Decide(new RtView(30, 200, 90, Block, 2_000));
        Assert.Equal(400, policy.ResumeThresholdMs);

        policy.Decide(new RtView(30 + 30_000, 500, 90, Block, 2_000));       // exactly 30 s: not yet
        Assert.Equal(400, policy.ResumeThresholdMs);

        policy.Decide(new RtView(30 + 30_001, 500, 90, Block, 2_000));       // past 30 s: halved, and the window restarts
        Assert.Equal(200, policy.ResumeThresholdMs);

        policy.Decide(new RtView(30 + 30_001 + 29_000, 500, 90, Block, 2_000));
        Assert.Equal(200, policy.ResumeThresholdMs);

        policy.Decide(new RtView(30 + 30_001 + 30_001, 500, 90, Block, 2_000));
        Assert.Equal(100, policy.ResumeThresholdMs);

        policy.Decide(new RtView(30 + 3 * 30_002, 500, 90, Block, 2_000));    // the floor holds
        Assert.Equal(100, policy.ResumeThresholdMs);
    }

    [Fact]
    public void LegacyPolicy_WithholdsWhileDraining_StopsTheDeviceOnlyOnceItIsDry_AndRestartsAfterTheDelay()
    {
        var policy = new LegacyStarvationPolicy(resumeMs: 20, restartMs: 15);

        StarvationStep edge = policy.Decide(new RtView(0, 0, 90, Block, 2_000));
        Assert.Equal(BlockAction.Withhold, edge.Action);
        Assert.Equal(StarvationEvents.Incident, edge.Events);

        StarvationStep draining = policy.Decide(new RtView(10, 10, 50, Block, 2_000));    // ring below 20, device still has audio
        Assert.Equal(BlockAction.Withhold, draining.Action);
        Assert.Equal(StarvationEvents.None, draining.Events);

        StarvationStep dry = policy.Decide(new RtView(20, 10, 0, Block, 2_000));          // device drained → stop + reset
        Assert.Equal(BlockAction.Withhold, dry.Action);
        Assert.Equal(StarvationEvents.DeviceStop, dry.Events);

        Assert.Equal(StarvationEvents.None, policy.Decide(new RtView(30, 10, 0, Block, 2_000)).Events);   // ring still below 20
        Assert.Equal(BlockAction.Withhold, policy.Decide(new RtView(100, 25, 0, Block, 2_000)).Action);  // ring ready at t = 100 …
        Assert.Equal(BlockAction.Withhold, policy.Decide(new RtView(114, 40, 0, Block, 2_000)).Action);  // … 14 ms later: not yet
        StarvationStep restart = policy.Decide(new RtView(115, 48, 0, Block, 2_000));                    // … 15 ms: restart
        Assert.Equal(BlockAction.Content, restart.Action);
        Assert.Equal(StarvationEvents.Resume, restart.Events);
    }

    [Fact]
    public void LegacyPolicy_ResumesWithoutStoppingTheDevice_WhenTheRingRefillsBeforeTheDeviceDrains()
    {
        var policy = new LegacyStarvationPolicy(resumeMs: 20, restartMs: 15);

        policy.Decide(new RtView(0, 0, 90, Block, 2_000));                                 // starve
        StarvationStep resume = policy.Decide(new RtView(10, 24, 70, Block, 2_000));       // ring ≥ 20 while the device still plays
        Assert.Equal(BlockAction.Content, resume.Action);
        Assert.Equal(StarvationEvents.Resume, resume.Events);                              // no DeviceStop: it never went dry
    }
}
