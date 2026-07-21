using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// RT-safe crossfade THROUGH the M4 audio feed (spec docs/plans/media-playback-api-spec.md §7.9, §8): a crossfade voice
/// added via <see cref="PcmAudioSession.AddCrossfadeVoice"/> is wrapped in its OWN decode↔RT firewall ring (the worker
/// decodes ahead, the RT thread mixes copy-only) and — when it retires on the RT thread — its ring is handed to the worker
/// for OFF-RT disposal via a lock-free SPSC retire queue. Two proofs: (1) golden — the RT-fed crossfade output is byte-for-
/// byte identical to the single-thread pull path (equal-power overlap AND gapless butt-join); (2) the RT render is zero
/// managed alloc AND the retiring voice's ring is disposed on the WORKER, never the RT thread. Deterministic — no wall
/// clock, no timers; every case is bounded.
/// </summary>
public sealed class CrossfadeRtFeedTests
{
    private static readonly MixFormat Fmt = new(48000, 2);
    private const int Ch = 2;

    /// <summary>A pass-through source that records whether it was disposed (proving WHICH thread frees the ring).</summary>
    private sealed class DisposeTrackingSource : IAudioSource, IDisposable
    {
        private readonly IAudioSource _inner;
        public bool Disposed { get; private set; }
        public DisposeTrackingSource(IAudioSource inner) => _inner = inner;
        public int Read(Span<float> dst, int channels) => _inner.Read(dst, channels);
        public long PositionFrames => _inner.PositionFrames;
        public bool Exhausted => _inner.Exhausted;
        public GaplessInfo Gapless => _inner.Gapless;
        public ReplayGainInfo Loudness => _inner.Loudness;
        public void Dispose() => Disposed = true;
    }

    private static PcmAudioSession NewSession(out HeadlessAudioEndpoint endpoint, out AudioFeedThread? feed,
        bool withFeed, int captureFrames)
    {
        endpoint = new HeadlessAudioEndpoint(Fmt, captureFrames: captureFrames);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        feed = withFeed ? new AudioFeedThread(session, blockFrames: 256) : null;   // ctor attaches to the session
        session.Configure(AudioGraphSpec.Passthrough);
        return session;
    }

    private static SignalGeneratorSource Tone(double freq, long frames)
        => new(Ch, Fmt.SampleRate, freq, 0.4f, frames);

    // ── golden: the RT-fed crossfade equals the single-thread pull path, sample-for-sample ───────────────────────────

    [Fact]
    public void Crossfade_ThroughRtFeed_MatchesSingleThreadGoldenPcm()
    {
        float[] single = RenderCrossfadeCapture(withFeed: false, primaryFrames: 2000, incomingFrames: 2000, overlap: 800);
        float[] rt = RenderCrossfadeCapture(withFeed: true, primaryFrames: 2000, incomingFrames: 2000, overlap: 800);

        Assert.Equal(single.Length, rt.Length);
        for (int i = 0; i < single.Length; i++)
            Assert.True(MathF.Abs(single[i] - rt[i]) < 1e-5f,
                $"crossfade diverged at sample {i}: single={single[i]} rt={rt[i]}");

        // The crossfade actually did something (not silence, not a hard cut): the overlap region is non-trivially blended.
        bool anyLoud = false;
        for (int i = 0; i < rt.Length; i++) if (MathF.Abs(rt[i]) > 0.1f) { anyLoud = true; break; }
        Assert.True(anyLoud, "captured crossfade was silent");
    }

    [Fact]
    public void GaplessButtJoin_ThroughRtFeed_MatchesSingleThreadGoldenPcm()
    {
        // overlap 0 ⇒ B butt-joins at A's natural end with a constant envelope (the gapless / CrossfadeMs==0 path).
        float[] single = RenderCrossfadeCapture(withFeed: false, primaryFrames: 1500, incomingFrames: 1500, overlap: 0);
        float[] rt = RenderCrossfadeCapture(withFeed: true, primaryFrames: 1500, incomingFrames: 1500, overlap: 0);

        Assert.Equal(single.Length, rt.Length);
        for (int i = 0; i < single.Length; i++)
            Assert.True(MathF.Abs(single[i] - rt[i]) < 1e-5f,
                $"gapless join diverged at sample {i}: single={single[i]} rt={rt[i]}");
    }

    // ── zero-alloc RT render + off-RT ring disposal on retire ─────────────────────────────────────────────────────────

    [Fact]
    public void Crossfade_RtRender_ZeroAlloc_AndRetiresRingOffRt()
    {
        var session = NewSession(out _, out var feed, withFeed: true, captureFrames: 0);
        Assert.NotNull(feed);
        Assert.True(session.IsRtDriven);

        // Primary: effectively endless (never retires during the test).
        var primary = Tone(220, 1_000_000);
        session.SetVoice(primary, TimeSpan.FromSeconds(20), 1_000_000, NormMode.Off, -14f, initialVolume: 1f);

        // A second, persistent ring-wrapped voice so TWO rings are active during the zero-alloc measurement.
        var persistent = Tone(440, 1_000_000);
        session.AddCrossfadeVoice(persistent, GainEnvelope.Constant, startFrame: 0, replayGain: 1f, chain: null, id: 2);
        Assert.Equal(2, feed!.RingCount);

        const int block = 256;

        // Warm the JIT (allocations here are not measured); pump the worker each iter to keep the rings fed.
        for (int i = 0; i < 32; i++) { session.RenderBlock(block); feed.WorkerPumpOnce(); }

        // Re-fill the rings, then measure a batch of RT renders WITHOUT pumping (so only RenderBlock's allocs count).
        for (int i = 0; i < 40; i++) feed.WorkerPumpOnce();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) session.RenderBlock(block);   // both ring-wrapped voices active
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, delta);   // RT render is zero managed alloc even through the rings + retire-report path

        // ── now prove an outgoing voice retires its ring OFF the RT thread ───────────────────────────────────────────
        var outgoingInner = new DisposeTrackingSource(new MemoryAudioSource(new float[400 * Ch], Ch));  // 400 frames, finite
        long startNow = session.ConsumeSeqFrames;
        session.AddCrossfadeVoice(outgoingInner, GainEnvelope.Constant, startFrame: startNow, replayGain: 1f, chain: null, id: 3);
        Assert.Equal(3, feed.RingCount);

        // Buffer the whole 400-frame source into its ring so we can drain it to exhaustion WITHOUT pumping the worker
        // (pumping would drain the retire queue before we can observe the RT-side hand-off).
        for (int i = 0; i < 20; i++) feed.WorkerPumpOnce();
        Assert.False(outgoingInner.Disposed);

        // Render (no worker pump) until voice 3 exhausts + retires on the RT thread.
        bool retiredOnRt = false;
        for (int i = 0; i < 8 && !retiredOnRt; i++)
        {
            session.RenderBlock(block);
            if (!session.MixerRef.HasVoice(3))
            {
                retiredOnRt = true;
                // The RT thread retired the voice from the mixer but did NOT touch the ring table or dispose the ring.
                Assert.Equal(3, feed.RingCount);
                Assert.False(outgoingInner.Disposed);
            }
        }
        Assert.True(retiredOnRt, "voice 3 never retired on the RT path");

        // The worker drains the retire queue: it disposes the ring off-RT and removes it (no leak).
        feed.WorkerPumpOnce();
        Assert.Equal(2, feed.RingCount);
        Assert.True(outgoingInner.Disposed, "the retired voice's ring was never disposed by the worker");

        feed.Dispose();
    }

    // ── shared render helper (kept simple: build → play out → capture) ───────────────────────────────────────────────

    private static float[] RenderCrossfadeCapture(bool withFeed, int primaryFrames, int incomingFrames, int overlap)
    {
        long cs = primaryFrames - overlap;
        int total = (int)cs + incomingFrames;
        var endpoint = new HeadlessAudioEndpoint(Fmt, captureFrames: total + 512);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        var feed = withFeed ? new AudioFeedThread(session, blockFrames: 256) : null;
        session.Configure(AudioGraphSpec.Passthrough);

        var primary = Tone(220, primaryFrames);
        session.SetVoice(primary, TimeSpan.FromSeconds((double)primaryFrames / Fmt.SampleRate), primaryFrames,
            NormMode.Off, -14f, initialVolume: 1f);

        var incoming = Tone(330, incomingFrames);
        var inEnv = overlap > 0 ? GainEnvelope.Fade(FadeKind.In, cs, overlap, CrossCurve.EqualPower) : GainEnvelope.Constant;
        session.AddCrossfadeVoice(incoming, inEnv, startFrame: cs, replayGain: 1f, chain: null, id: 2);
        if (overlap > 0)
            session.SetVoiceEnvelope(session.PrimaryVoiceIdValue,
                GainEnvelope.Fade(FadeKind.Out, cs, overlap, CrossCurve.EqualPower));

        if (withFeed) for (int i = 0; i < 60; i++) feed!.WorkerPumpOnce();

        const int block = 256;
        for (int rendered = 0; rendered < total; rendered += block)
        {
            int fr = Math.Min(block, total - rendered);
            session.RenderBlock(fr);
            if (withFeed) feed!.WorkerPumpOnce();
        }

        var captured = ((NullAudioSink)endpoint.Sink).Captured.ToArray();
        feed?.Dispose();
        return captured;
    }
}
