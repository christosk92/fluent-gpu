using System;
using FluentGpu.Foundation;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// M3 §8.3/§8.4: the crossfade/gapless golden PCM, the seek-invalidated <see cref="PreparedSlot"/> state machine (the named
/// "crossfade-prepared-next" scar), preroll degradation, and sample-accurate 3-track gapless. All deterministic — the sample
/// clock is ticked by hand; no wall clock, no timers.
/// </summary>
public sealed class VoiceSchedulerTests
{
    private const int Rate = 48000, Ch = 1;

    private static float[] Const(int len, float v) { var a = new float[len]; Array.Fill(a, v); return a; }
    private static ScheduledTransition Xfade(int overlapFrames, Easing curve)
        => new(TransitionKind.Crossfade, TimeSpan.FromSeconds((double)overlapFrames / Rate), null, null, curve);
    private static AudioPreparedItem Item(IAudioSource src, long total)
        => new(src, GaplessInfo.None, default, total, TimeSpan.FromSeconds((double)total / Rate));

    private static float[] RenderWithScheduler(CrossfadeMixer mixer, VoiceScheduler sched, int totalFrames, int block)
    {
        var result = new float[totalFrames * Ch];
        var plane = new ParamPlane();
        long clock = 0;
        while (clock < totalFrames)
        {
            int fr = (int)Math.Min(block, totalFrames - clock);
            sched.Commit(clock, mixer);
            var ctx = new BlockCtx(clock, Rate, Ch, plane);
            mixer.Render(result.AsSpan((int)(clock * Ch), fr * Ch), fr, ctx);
            clock += fr;
        }
        return result;
    }

    // ── crossfade golden: two overlapping voices, equal-power envelope, ReplayGain baked per voice pre-mix ────────────

    [Fact]
    public void Crossfade_EqualPower_TwoVoicesOverlap_GoldenPcm()
    {
        const int len = 100, overlap = 40;
        var a = new MemoryAudioSource(Const(len, 1f), Ch);
        var b = new MemoryAudioSource(Const(len, 1f), Ch);
        var mixer = new CrossfadeMixer(Ch, 128);
        mixer.AddVoice(new MixVoice { Id = 1, Src = a, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });

        var sched = new VoiceScheduler(Rate, Ch);
        sched.BeginActive(1, 0, len, Xfade(overlap, Easing.EaseInOut), latencyMarginFrames: 10, NormMode.Off, -14f);
        uint e = sched.MarkPreparing();
        Assert.True(sched.SubmitPrepared(e, Item(b, len)));
        Assert.Equal(PrepState.Ready, sched.State);

        // Derive the exact join off the scheduler (frame counts round through TimeSpan, so never hard-code them).
        long cs = sched.CrossfadeStartFrame;
        long joinEnd = sched.JoinEndFrame;
        int ov = (int)(joinEnd - cs);

        var dst = RenderWithScheduler(mixer, sched, 140, block: 1);
        Assert.Equal(TransitionOutcome.Crossfaded, sched.Outcome);

        for (int f = 0; f < 140; f++)
        {
            float expected;
            if (f < cs) expected = 1f;                              // A only, before the crossfade
            else if (f < joinEnd)                                  // the overlap: equal-power sum of two unity voices
            {
                float p = (f - cs) / (float)ov;
                expected = CrossfadeCurves.Out(CrossCurve.EqualPower, p) + CrossfadeCurves.In(CrossCurve.EqualPower, p);
            }
            else expected = 1f;                                    // only B remains, at full gain
            Assert.Equal(expected, dst[f], 3);
        }
    }

    [Fact]
    public void Crossfade_Linear_CorrelatedMaterial_StaysConstant()
    {
        const int len = 100, overlap = 40;
        var a = new MemoryAudioSource(Const(len, 1f), Ch);
        var b = new MemoryAudioSource(Const(len, 1f), Ch);
        var mixer = new CrossfadeMixer(Ch, 128);
        mixer.AddVoice(new MixVoice { Id = 1, Src = a, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });

        var sched = new VoiceScheduler(Rate, Ch);
        sched.BeginActive(1, 0, len, Xfade(overlap, Easing.Linear), 10, NormMode.Off, -14f);
        uint e = sched.MarkPreparing();
        sched.SubmitPrepared(e, Item(b, len));

        var dst = RenderWithScheduler(mixer, sched, 140, block: 1);
        for (int f = 0; f < 140; f++) Assert.Equal(1f, dst[f], 3);   // linear sum of identical unity signals is constant
    }

    // ── the scar: a Seek mid-preroll bumps Epoch, invalidates, re-prepares; a stale late Prepare is dropped ──────────

    [Fact]
    public void Scar_SeekBumpsEpoch_ReprepareStillCrossfades_StaleDropped()
    {
        var a = new MemoryAudioSource(Const(500, 1f), Ch);
        var mixer = new CrossfadeMixer(Ch, 256);
        mixer.AddVoice(new MixVoice { Id = 1, Src = a, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });

        var sched = new VoiceScheduler(Rate, Ch);
        sched.BeginActive(1, 0, 200, Xfade(40, Easing.EaseInOut), 10, NormMode.Off, -14f);

        // Preroll B under epoch e0 (drive off the scheduler's own ending-soon frame — no hard-coded numbers).
        Assert.True(sched.NeedsPrepare(sched.EndingSoonFrame));
        uint e0 = sched.MarkPreparing();
        var b0 = new MemoryAudioSource(Const(200, 1f), Ch);
        Assert.True(sched.SubmitPrepared(e0, Item(b0, 200)));
        Assert.Equal(PrepState.Ready, sched.State);

        // SEEK → bump epoch, drop the slot, re-arm the join at the new position.
        uint e1 = sched.Invalidate();
        Assert.NotEqual(e0, e1);
        Assert.Equal(PrepState.Idle, sched.State);
        sched.BeginActive(1, 200, 200, Xfade(40, Easing.EaseInOut), 10, NormMode.Off, -14f);

        // A STALE late Prepare (the old epoch) arrives → DROPPED, mixer/slot uncorrupted (the exact scar).
        var stale = new MemoryAudioSource(Const(200, 9f), Ch);
        Assert.False(sched.SubmitPrepared(e0, Item(stale, 200)));
        Assert.Equal(PrepState.Idle, sched.State);
        Assert.Equal(1, mixer.VoiceCount);

        // Re-prepare under the new epoch → Ready → crossfade STILL fires at the NEW join.
        Assert.True(sched.NeedsPrepare(sched.EndingSoonFrame));
        uint e2 = sched.MarkPreparing();
        Assert.Equal(e1, e2);
        var b1 = new MemoryAudioSource(Const(200, 1f), Ch);
        Assert.True(sched.SubmitPrepared(e2, Item(b1, 200)));

        Assert.Equal(TransitionOutcome.Crossfaded, sched.Commit(sched.CrossfadeStartFrame, mixer));
        Assert.Equal(2, mixer.VoiceCount);
        Assert.True(sched.IncomingVoiceId > 0);
    }

    // ── degradation: not Ready by the join → never truncates A, never starts B mid-fill ─────────────────────────────

    [Fact]
    public void Degrade_NotReadyByJoin_ButtJoinsAtNaturalEnd_NeverTruncatesA()
    {
        var a = new MemoryAudioSource(Const(100, 1f), Ch);
        var mixer = new CrossfadeMixer(Ch, 128);
        mixer.AddVoice(new MixVoice { Id = 1, Src = a, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });

        var sched = new VoiceScheduler(Rate, Ch);
        sched.BeginActive(1, 0, 100, Xfade(40, Easing.EaseInOut), 10, NormMode.Off, -14f);
        long cs = sched.CrossfadeStartFrame;
        long joinEnd = sched.JoinEndFrame;

        // At the crossfade start B is NOT Ready → the scheduler decides to DEGRADE (no crossfade).
        Assert.Equal(TransitionOutcome.None, sched.Commit(cs, mixer));

        // B becomes Ready mid-way (still before A's natural end).
        uint e = sched.MarkPreparing();
        var b = new MemoryAudioSource(Const(100, 1f), Ch);
        sched.SubmitPrepared(e, Item(b, 100));

        Assert.Equal(TransitionOutcome.DegradedGapless, sched.Commit(joinEnd - 20, mixer));
        Assert.Equal(2, mixer.VoiceCount);

        var span = mixer.VoicesSpan;
        foreach (var v in span)
        {
            if (v.Id == 1) Assert.Equal(FadeKind.None, v.Env.Kind);          // A is NEVER faded out / truncated
            else Assert.Equal(100, v.StartFrame);                            // B butt-joins at A's natural end, not mid-fill
        }
    }

    [Fact]
    public void Degrade_ReadyAfterAEnds_BoundedMicroGap_Declicked()
    {
        var a = new MemoryAudioSource(Const(100, 1f), Ch);
        var mixer = new CrossfadeMixer(Ch, 128);
        mixer.AddVoice(new MixVoice { Id = 1, Src = a, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });

        var sched = new VoiceScheduler(Rate, Ch);
        sched.BeginActive(1, 0, 100, Xfade(40, Easing.EaseInOut), 10, NormMode.Off, -14f);
        Assert.Equal(TransitionOutcome.None, sched.Commit(sched.CrossfadeStartFrame, mixer));   // not ready → degrade decision

        uint e = sched.MarkPreparing();
        var b = new MemoryAudioSource(Const(100, 1f), Ch);
        sched.SubmitPrepared(e, Item(b, 100));

        long late = sched.JoinEndFrame + 30;   // A ended, B ready only now → a bounded micro-gap
        Assert.Equal(TransitionOutcome.DegradedMicroGap, sched.Commit(late, mixer));
        foreach (var v in mixer.VoicesSpan)
            if (v.Id != 1) { Assert.Equal(late, v.StartFrame); Assert.Equal(FadeKind.In, v.Env.Kind); }   // declick fade-in
    }

    // ── 3-track gapless album: per-source LeadIn/TrailPad trim is sample-accurate across the butt-joins ─────────────

    [Fact]
    public void Gapless_ThreeTrackAlbum_TrimmedButtJoins_AreSampleAccurate()
    {
        const int raw = 60, emit = 50;   // trim 5 lead-in + 5 trail-pad → 50 emitted per track
        GaplessInfo trim = new(LeadInFrames: 5, TrailPadFrames: 5, ExactFrames: emit, TailKnown: true);

        var a = new TrimmingSource(new MemoryAudioSource(M3TestSupport.Ramp(raw, 0f), Ch), trim, Ch, raw);
        var b = new TrimmingSource(new MemoryAudioSource(M3TestSupport.Ramp(raw, 100f), Ch), trim, Ch, raw);
        var c = new TrimmingSource(new MemoryAudioSource(M3TestSupport.Ramp(raw, 200f), Ch), trim, Ch, raw);

        var mixer = new CrossfadeMixer(Ch, 256);
        mixer.AddVoice(new MixVoice { Id = 1, Src = a, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });
        mixer.AddVoice(new MixVoice { Id = 2, Src = b, Env = GainEnvelope.Constant, StartFrame = emit, ReplayGainScalar = 1f });
        mixer.AddVoice(new MixVoice { Id = 3, Src = c, Env = GainEnvelope.Constant, StartFrame = 2 * emit, ReplayGainScalar = 1f });

        var dst = new float[3 * emit * Ch];
        mixer.Render(dst, 3 * emit, new BlockCtx(0, Rate, Ch, new ParamPlane()));

        Assert.Equal(5f, dst[0], 4);       // A first emitted = raw frame 5 (lead-in skipped)
        Assert.Equal(54f, dst[49], 4);     // A last emitted = raw frame 54 (trail-pad trimmed)
        Assert.Equal(105f, dst[50], 4);    // B first — sample-accurate butt-join (no gap/overlap)
        Assert.Equal(154f, dst[99], 4);    // B last
        Assert.Equal(205f, dst[100], 4);   // C first — sample-accurate butt-join
        Assert.Equal(254f, dst[149], 4);   // C last
    }
}
