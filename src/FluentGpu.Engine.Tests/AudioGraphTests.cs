using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// M2 golden-PCM + graph tests (spec docs/plans/media-playback-api-spec.md §7/§8, milestone M2): the crossfade/gapless
/// envelope math, RBJ biquad EQ, the ReplayGain scalar + terminal limiter, the AudioGraphHost atomic-swap + consume-gated
/// quarantine, the AudioParam smoothing plane, the derived-position clock, zero managed alloc on the block path, and the
/// PcmAudioPlayer as a routed backend driving the MediaSignalSink. All deterministic — synthetic clock + null sink, ticked
/// by hand; no device, no wall clock.
/// </summary>
public sealed class AudioGraphTests
{
    private static readonly ParamPlane Plane = new();
    private static BlockCtx Ctx(int channels, long start = 0, int rate = 48000) => new(start, rate, channels, Plane);

    // ── (1) Crossfade envelope math (equal-power + linear) ───────────────────────────────────────────────────────────

    [Fact]
    public void EqualPowerCurve_IsConstantPower_AndEndpointsExact()
    {
        Assert.Equal(1f, CrossfadeCurves.Out(CrossCurve.EqualPower, 0f), 5);
        Assert.Equal(0f, CrossfadeCurves.In(CrossCurve.EqualPower, 0f), 5);
        Assert.Equal(0f, CrossfadeCurves.Out(CrossCurve.EqualPower, 1f), 5);
        Assert.Equal(1f, CrossfadeCurves.In(CrossCurve.EqualPower, 1f), 5);
        for (float p = 0f; p <= 1f; p += 0.05f)
        {
            float o = CrossfadeCurves.Out(CrossCurve.EqualPower, p);
            float i = CrossfadeCurves.In(CrossCurve.EqualPower, p);
            Assert.Equal(1f, o * o + i * i, 4);   // constant power
        }
        Assert.Equal(0.70710677f, CrossfadeCurves.Out(CrossCurve.EqualPower, 0.5f), 5);
    }

    [Fact]
    public void LinearCurve_IsLinear()
    {
        Assert.Equal(0.75f, CrossfadeCurves.Out(CrossCurve.Linear, 0.25f), 5);
        Assert.Equal(0.25f, CrossfadeCurves.In(CrossCurve.Linear, 0.25f), 5);
    }

    [Fact]
    public void GainEnvelope_PinsBeforeAndAfterTheFadeWindow()
    {
        var fadeIn = GainEnvelope.Fade(FadeKind.In, fadeStartFrame: 100, fadeFrames: 50, CrossCurve.Linear);
        Assert.Equal(0f, fadeIn.GainAt(50), 5);    // before → silent
        Assert.Equal(1f, fadeIn.GainAt(200), 5);   // after → full
        Assert.Equal(0.5f, fadeIn.GainAt(125), 5); // midpoint (linear)

        var fadeOut = GainEnvelope.Fade(FadeKind.Out, 100, 50, CrossCurve.Linear);
        Assert.Equal(1f, fadeOut.GainAt(50), 5);
        Assert.Equal(0f, fadeOut.GainAt(200), 5);
    }

    // ── (2) CrossfadeMixer: two tracks at different ReplayGain, crossfaded, baked per-voice PRE-mix ──────────────────

    [Fact]
    public void CrossfadeMixer_BakesReplayGainPerVoice_BeforeMix_LinearEnvelope()
    {
        const int frames = 64, ch = 1;
        var a = new MemoryAudioSource(Ones(frames, ch), ch);   // constant 1.0
        var b = new MemoryAudioSource(Ones(frames, ch), ch);

        var mixer = new CrossfadeMixer(ch, frames);
        mixer.AddVoice(new MixVoice { Src = a, Env = GainEnvelope.Fade(FadeKind.Out, 0, frames, CrossCurve.Linear), StartFrame = 0, ReplayGainScalar = 0.5f });
        mixer.AddVoice(new MixVoice { Src = b, Env = GainEnvelope.Fade(FadeKind.In, 0, frames, CrossCurve.Linear), StartFrame = 0, ReplayGainScalar = 2.0f });

        var dst = new float[frames * ch];
        mixer.Render(dst, frames, Ctx(ch));

        for (int f = 0; f < frames; f++)
        {
            float p = (float)f / frames;
            float expected = 0.5f * (1f - p) + 2.0f * p;   // A(0.5, fading out) + B(2.0, fading in) — RG baked per voice
            Assert.Equal(expected, dst[f], 4);
        }
    }

    [Fact]
    public void CrossfadeMixer_SumsNVoices_AndAdvancesConsumeSeq()
    {
        const int frames = 32, ch = 2;
        var a = new MemoryAudioSource(Const(frames, ch, 0.25f), ch);
        var b = new MemoryAudioSource(Const(frames, ch, 0.5f), ch);
        var mixer = new CrossfadeMixer(ch, frames);
        mixer.AddVoice(new MixVoice { Src = a, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });
        mixer.AddVoice(new MixVoice { Src = b, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });

        var dst = new float[frames * ch];
        mixer.Render(dst, frames, Ctx(ch));
        Assert.Equal(frames, mixer.ConsumeSeq);
        for (int i = 0; i < dst.Length; i++) Assert.Equal(0.75f, dst[i], 5);
    }

    // ── (3) Gapless butt-join: LeadIn/TrailPad trim is sample-accurate; overlap-0 join is seamless ──────────────────

    [Fact]
    public void TrimmingSource_AppliesLeadInAndTrailPad_SampleAccurate()
    {
        const int total = 100, ch = 1;
        var ramp = new float[total];
        for (int i = 0; i < total; i++) ramp[i] = i;   // frame value == index
        var inner = new MemoryAudioSource(ramp, ch);
        var trim = new TrimmingSource(inner, new GaplessInfo(LeadInFrames: 10, TrailPadFrames: 10, ExactFrames: 80, TailKnown: true), ch, innerTotalFrames: total);

        var buf = new float[total];
        int got = trim.Read(buf, ch);
        Assert.Equal(80, got);
        Assert.Equal(10f, buf[0], 5);    // first emitted = frame 10 (lead-in skipped)
        Assert.Equal(89f, buf[79], 5);   // last emitted = frame 89 (trail-pad trimmed)
        Assert.True(trim.Exhausted);
    }

    [Fact]
    public void Gapless_ButtJoin_TwoTrimmedVoices_IsSeamless()
    {
        const int ch = 1, len = 40;
        var aRamp = new float[len]; for (int i = 0; i < len; i++) aRamp[i] = i;                 // 0..39
        var bRamp = new float[len]; for (int i = 0; i < len; i++) bRamp[i] = 100 + i;           // 100..139
        var a = new TrimmingSource(new MemoryAudioSource(aRamp, ch), GaplessInfo.None, ch, len);
        var b = new TrimmingSource(new MemoryAudioSource(bRamp, ch), GaplessInfo.None, ch, len);

        var mixer = new CrossfadeMixer(ch, 128);
        mixer.AddVoice(new MixVoice { Src = a, Env = GainEnvelope.Constant, StartFrame = 0, ReplayGainScalar = 1f });
        mixer.AddVoice(new MixVoice { Src = b, Env = GainEnvelope.Constant, StartFrame = len, ReplayGainScalar = 1f });  // overlap 0 = butt-join

        var dst = new float[80];
        mixer.Render(dst, 80, Ctx(ch));
        Assert.Equal(39f, dst[39], 5);    // A's last frame
        Assert.Equal(100f, dst[40], 5);   // B's first frame — joined at frame index, no gap/overlap
        Assert.Equal(139f, dst[79], 5);
    }

    // ── (4) RBJ biquad EQ coefficients + per-channel cascade ────────────────────────────────────────────────────────

    [Fact]
    public void BiquadCoeffs_LowPassPassesDc_HighPassBlocksDc()
    {
        var lp = BiquadCoeffs.Design(new BiquadBand(BiquadType.LowPass, 1000f, 0.7071f, 0f), 48000);
        float lpDc = (lp.B0 + lp.B1 + lp.B2) / (1f + lp.A1 + lp.A2);
        Assert.Equal(1f, lpDc, 3);

        var hp = BiquadCoeffs.Design(new BiquadBand(BiquadType.HighPass, 1000f, 0.7071f, 0f), 48000);
        float hpDc = (hp.B0 + hp.B1 + hp.B2) / (1f + hp.A1 + hp.A2);
        Assert.Equal(0f, hpDc, 3);
    }

    [Fact]
    public void BiquadCoeffs_PeakingZeroGain_IsIdentity()
    {
        var c = BiquadCoeffs.Design(new BiquadBand(BiquadType.Peaking, 1000f, 1f, 0f), 48000);
        // A 0 dB peaking filter is a pass-through: b == a (b0=1, b1=a1, b2=a2 after normalization).
        Assert.Equal(1f, c.B0, 4);
        Assert.Equal(c.A1, c.B1, 4);
        Assert.Equal(c.A2, c.B2, 4);
    }

    [Fact]
    public void EqStage_PerChannelCascade_IsIndependentAcrossChannels()
    {
        const int ch = 2, frames = 256;
        var eq = new EqStage(ch);
        eq.SetBands(new[] { new BiquadBand(BiquadType.LowPass, 500f, 0.7071f, 0f) }, 48000);

        // Left = a mid-freq sine, Right = silence. The right channel must stay exactly silent (independent state).
        var src = new float[frames * ch];
        for (int f = 0; f < frames; f++) src[f * 2] = MathF.Sin(2f * MathF.PI * 4000f * f / 48000f);
        var dst = new float[frames * ch];
        eq.Process(src, dst, frames, Ctx(ch));

        for (int f = 0; f < frames; f++) Assert.Equal(0f, dst[f * 2 + 1], 6);   // right stays silent
        bool leftMoved = false;
        for (int f = 0; f < frames; f++) if (MathF.Abs(dst[f * 2]) > 1e-4f) { leftMoved = true; break; }
        Assert.True(leftMoved);
    }

    [Fact]
    public void EqStage_GainOnlyChange_CrossRamps_NoZipper()
    {
        const int ch = 1, frames = 512;
        var eq = new EqStage(ch, declickSamples: 256);
        eq.SetBands(new[] { new BiquadBand(BiquadType.Peaking, 1000f, 1f, 0f) }, 48000);

        var input = new float[frames];
        for (int f = 0; f < frames; f++) input[f] = 0.5f;   // DC — a peaking filter passes DC at any gain, so output stays ~0.5
        var dst = new float[frames];
        eq.Process(input, dst, frames, Ctx(ch));

        eq.SetBandGain(0, 9f);   // a gain-only change → cross-ramp (no coefficient step transient)
        Assert.True(eq.IsRamping);
        eq.Process(input, dst, frames, Ctx(ch));

        // No zipper: adjacent output samples never jump abruptly during the cross-ramp.
        for (int f = 1; f < frames; f++) Assert.True(MathF.Abs(dst[f] - dst[f - 1]) < 0.05f);
        Assert.False(eq.IsRamping);   // 512 frames > 256-sample declick window
    }

    // ── (5) AudioParam smoothing: a gain change RAMPS (no zipper) vs a set jumps ─────────────────────────────────────

    [Fact]
    public void GainStage_TargetChange_RampsLinearly_NoStep()
    {
        const int ch = 1, frames = 100;
        var gain = new GainStage(0f);
        gain.SetTargetLinear(1f, rampSamples: frames);
        var input = new float[frames]; Array.Fill(input, 1f);
        var dst = new float[frames];
        gain.Process(input, dst, frames, Ctx(ch));

        Assert.Equal(0f, dst[0], 3);
        Assert.True(dst[^1] > 0.98f);
        for (int f = 1; f < frames; f++)
        {
            Assert.True(dst[f] >= dst[f - 1] - 1e-4f);          // monotonic up
            Assert.True(MathF.Abs(dst[f] - dst[f - 1]) < 0.05f); // no zipper jump
        }
    }

    // ── (6) ReplayGain scalar selection + terminal limiter ──────────────────────────────────────────────────────────

    [Fact]
    public void ReplayGain_SelectsTrackVsAlbum_AndOffIsUnity()
    {
        var info = new ReplayGainInfo(TrackGainDb: -6f, AlbumGainDb: -3f, TrackPeak: 1f, AlbumPeak: 1f);
        Assert.Equal(1f, ReplayGain.ScalarLinear(info, NormMode.Off));
        Assert.Equal(MathF.Pow(10f, -6f / 20f), ReplayGain.ScalarLinear(info, NormMode.Track, ReplayGain.TagReferenceLufs), 5);
        Assert.Equal(MathF.Pow(10f, -3f / 20f), ReplayGain.ScalarLinear(info, NormMode.Album, ReplayGain.TagReferenceLufs), 5);
        // Retarget from the tag reference (-18) to -14 adds +4 dB.
        Assert.Equal(MathF.Pow(10f, (-3f + 4f) / 20f), ReplayGain.ScalarLinear(info, NormMode.Album, -14f), 5);
    }

    [Fact]
    public void LimiterStage_NeverExceedsCeiling_OnBoostedSignal()
    {
        const int ch = 2, frames = 512;
        var lim = new LimiterStage(ceilingDbTp: -1.5f, releaseMs: 50f, mixRate: 48000);
        float ceiling = LimiterStage.DbToLinear(-1.5f);

        var src = new float[frames * ch];
        for (int f = 0; f < frames; f++) { float s = 4.0f * MathF.Sin(2f * MathF.PI * 200f * f / 48000f); src[f * 2] = s; src[f * 2 + 1] = s; }
        var dst = new float[frames * ch];
        lim.Process(src, dst, frames, Ctx(ch));

        for (int i = 0; i < dst.Length; i++) Assert.True(MathF.Abs(dst[i]) <= ceiling + 1e-4f, $"sample {i} = {dst[i]} exceeds {ceiling}");
    }

    // ── (7) AudioGraphHost: atomic swap + consume-gated quarantine (RenderInFlightDepth+1) ─────────────────────────────

    [Fact]
    public void AudioGraphHost_RetiresOldGraph_AfterQuarantineConsumeSteps()
    {
        var host = new AudioGraphHost(2, 48000);
        var first = host.Live;
        Assert.NotNull(first);

        host.Publish(AudioGraphSpec.Passthrough with { MasterChain = ImmutableArray.Create<EffectSpec>(new GainSpec(0.5f)) });
        Assert.NotSame(first, host.Live);       // atomic swap — Live is the new graph immediately
        Assert.Equal(1, host.PendingRetire);
        Assert.Equal(0, host.RetiredCount);

        host.MarkConsumed();                    // step 1 — still quarantined (Quarantine = RenderInFlightDepth + 1 = 2)
        Assert.Equal(0, host.RetiredCount);
        host.MarkConsumed();                    // step 2 — now eligible
        Assert.Equal(1, host.RetiredCount);
        Assert.Equal(0, host.PendingRetire);
    }

    [Fact]
    public void AudioGraphHost_ConsumePath_IsZeroAlloc()
    {
        var host = new AudioGraphHost(2, 48000);
        for (int i = 0; i < 1000; i++) host.MarkConsumed();   // warm
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200_000; i++) host.MarkConsumed();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── (8) Zero managed alloc across a tight "pull N blocks" loop through the full graph ────────────────────────────

    [Fact]
    public void RenderBlock_PullNBlocks_IsZeroManagedAlloc()
    {
        var session = BuildHeadlessSession(out _, freqHz: 220, seconds: 10.0);
        session.PumpAudio(256); // Opening→Buffering
        session.PumpAudio(256); // →Ready
        Assert.Equal(PlaybackState.Ready, session.CurrentState);

        for (int i = 0; i < 2000; i++) session.RenderBlock(256);   // warm the JIT
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++) session.RenderBlock(256);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── (9) Derived position off a synthetic clock: extrapolation, latency subtraction, IsValid warmup gate ─────────

    [Fact]
    public void AudioClockPosition_WarmupGateHoldsUntilNonZero_ThenSubtractsLatency()
    {
        const int rate = 48000;
        var clock = new SyntheticAudioClock(rate, streamLatencyFrames: 480, warmupFrames: rate);   // 10 ms latency, 1 s warmup
        var pos = new AudioClockPosition();

        clock.Advance(rate / 2);   // 0.5 s rendered — still inside warmup, GetPosition reads 0
        pos.Sample(clock);
        Assert.False(pos.IsValid);
        Assert.Equal(TimeSpan.Zero, pos.Project(clock.NowTicks100ns));

        clock.Advance(rate);       // now 1.5 s rendered — past warmup
        pos.Sample(clock);
        Assert.True(pos.IsValid);

        var projected = pos.Project(clock.NowTicks100ns);
        // 1.5 s played minus 480-frame (10 ms) latency ≈ 1.49 s.
        Assert.Equal(1.5 - 480.0 / rate, projected.TotalSeconds, 3);
    }

    [Fact]
    public void AudioClockPosition_ExtrapolatesBetweenPolls()
    {
        const int rate = 48000;
        var clock = new SyntheticAudioClock(rate);
        var pos = new AudioClockPosition();
        clock.Advance(rate);          // 1 s
        pos.Sample(clock);
        long baseTicks = clock.NowTicks100ns;

        var atPoll = pos.Project(baseTicks);
        var later = pos.Project(baseTicks + 5_000_000);   // +0.5 s of QPC with no new poll
        Assert.True(later > atPoll);
        Assert.Equal(atPoll.TotalSeconds + 0.5, later.TotalSeconds, 3);
    }

    // ── (10) PcmAudioPlayer as a routed backend drives the MediaSignalSink (Opening→…→Ended), synchronous transport ──

    [Fact]
    public async Task PcmAudioPlayer_RoutedBackend_DrivesSignalSink_ToEnded()
    {
        var player = MediaPlayer.Build()
            .WithBackend(MediaKind.PcmAudio, new PcmAudioPlayer(new MixFormat(48000, 2), maxBlock: 512))
            .Build();

        var wav = MakeWavPcm16(48000, 2, ToneStereo(48000, 0.2, 440));   // 0.2 s stereo tone
        await player.OpenAsync(MediaSource.FromBytes(wav).WithKind(MediaKind.PcmAudio)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackState.Opening, player.State.Peek());

        var session = Assert.IsType<PcmAudioSession>(player.Session);
        session.PumpAudio(512);   // Opening → Buffering
        session.PumpAudio(512);   // Buffering → Ready
        Assert.Equal(PlaybackState.Ready, player.State.Peek());
        Assert.Equal(0.2, player.Duration.Peek().TotalSeconds, 2);

        // Transport is idempotent + synchronous — never blocks.
        var play = player.PlayAsync();
        Assert.True(play.IsCompleted);
        await play.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        session.PumpAudio(512);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());

        for (int i = 0; i < 400 && player.State.Peek() != PlaybackState.Ended; i++) session.PumpAudio(512);
        Assert.Equal(PlaybackState.Ended, player.State.Peek());
        Assert.True(player.Position.Peek() > TimeSpan.Zero);

        await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PcmAudioSession_MasterLimiter_KeepsPresentedPcmUnderCeiling()
    {
        var endpoint = new HeadlessAudioEndpoint(new MixFormat(48000, 2), captureFrames: 4096);
        var voice = new SignalGeneratorSource(2, 48000, 200, amplitude: 4.0f, totalFrames: 48000);
        var session = new PcmAudioSession(new MixFormat(48000, 2), endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        session.SetVoice(voice, TimeSpan.FromSeconds(1), 48000, NormMode.Off, -14f, initialVolume: 1f);

        for (int i = 0; i < 8; i++) session.RenderBlock(512);   // 4096 frames captured

        var captured = ((NullAudioSink)endpoint.Sink).Captured;
        float ceiling = LimiterStage.DbToLinear(-1.5f);
        bool anyLoud = false;
        for (int i = 0; i < captured.Length; i++)
        {
            Assert.True(MathF.Abs(captured[i]) <= ceiling + 1e-3f);
            if (MathF.Abs(captured[i]) > 0.1f) anyLoud = true;
        }
        Assert.True(anyLoud);   // it actually produced (limited) audio, not silence
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static PcmAudioSession BuildHeadlessSession(out HeadlessAudioEndpoint endpoint, double freqHz, double seconds)
    {
        var fmt = new MixFormat(48000, 2);
        endpoint = new HeadlessAudioEndpoint(fmt);
        long frames = (long)(seconds * fmt.SampleRate);
        var voice = new SignalGeneratorSource(2, fmt.SampleRate, freqHz, 0.5f, frames);
        var session = new PcmAudioSession(fmt, endpoint.Sink, endpoint.Clock, maxBlock: 256, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        session.SetVoice(voice, TimeSpan.FromSeconds(seconds), frames, NormMode.Off, -14f, initialVolume: 1f);
        // Connect a bare sink so PumpAudio can publish (a core is fine; we only need the state machine to run).
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        return session;
    }

    private static float[] Ones(int frames, int ch) => Const(frames, ch, 1f);
    private static float[] Const(int frames, int ch, float v) { var a = new float[frames * ch]; Array.Fill(a, v); return a; }

    private static float[] ToneStereo(int rate, double seconds, double freq)
    {
        int frames = (int)(rate * seconds);
        var a = new float[frames * 2];
        for (int f = 0; f < frames; f++) { float s = 0.5f * MathF.Sin(2f * MathF.PI * (float)freq * f / rate); a[f * 2] = s; a[f * 2 + 1] = s; }
        return a;
    }

    private static byte[] MakeWavPcm16(int rate, int channels, float[] interleaved)
    {
        int frames = interleaved.Length / channels;
        int dataBytes = frames * channels * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in interleaved) w.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
