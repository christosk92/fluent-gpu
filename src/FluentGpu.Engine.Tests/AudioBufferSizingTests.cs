using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// M4 buffer-sizing tests (spec §7.9) for the audible-silence-on-stall fix: the WASAPI device buffer is ~100 ms, so the
/// decode-ahead ring must hold well more than that at ANY device rate (sizing in TIME, not a fixed frame count — a frame
/// count collapses at high rates: 4096 frames is 85 ms at 48 kHz but only 21 ms at 192 kHz), the RT catch-up burst after a
/// stall must be CAPPED so it drains that ring gradually instead of instantly, and the worker's low-water wake must fire
/// once per drop (an edge, not a poll) so a stall-recovery burst doesn't retrigger it every block. Also covers the PumpAhead
/// transient-vs-exhausted distinction (a network 0-read must never be latched as EOF). Pure xunit — FeedOnce/WorkerPumpOnce/
/// PumpAhead/CheckLowWaterEdge are driven directly; no real WASAPI, no live threads, no wall-clock waits.
/// </summary>
public sealed class AudioBufferSizingTests
{
    // ── (1) sized in TIME: >= 500 ms of decode-ahead at any device rate ────────────────────────────────────────────────

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void MsSizedCtor_TargetAheadIsAtLeast500Ms_AtAnySampleRate(int sampleRate)
    {
        var fmt = new MixFormat(sampleRate, 2);
        using var endpoint = new HeadlessAudioEndpoint(fmt);
        var session = new PcmAudioSession(fmt, endpoint.Sink, endpoint.Clock, maxBlock: 8192, driveWithOwnThread: false);
        var feed = new AudioFeedThread(session, sampleRate: sampleRate);   // named — disambiguates from the frame ctor

        long frames = sampleRate * 5L;
        var voice = new SignalGeneratorSource(2, sampleRate, 220, 0.5f, frames);
        session.SetVoice(voice, TimeSpan.FromSeconds(5), frames, NormMode.Off, -14f, initialVolume: 1f);

        Assert.Equal(1, feed.RingCount);
        var ring = feed.RingsSnapshot[0].Ring;

        int minFrames = (int)Math.Round(sampleRate * 500.0 / 1000.0);
        Assert.True(ring.TargetFrames >= minFrames,
            $"target-ahead {ring.TargetFrames} frames < 500 ms ({minFrames} frames) at {sampleRate} Hz");

        // The ring itself must be large enough to actually HOLD that target-ahead depth (not just claim it).
        for (int i = 0; i < 32; i++) feed.WorkerPumpOnce();
        Assert.True(ring.BufferedFrames >= minFrames,
            $"buffered {ring.BufferedFrames} frames < 500 ms ({minFrames} frames) at {sampleRate} Hz after filling");

        feed.Dispose();
    }

    [Fact]
    public void MsSizedCtor_DefaultBlockMs_Is10MsAtAnyRate()
    {
        // BlockFrames converts blockMs (default 10 ms) against sampleRate, not a fixed frame count that drifts at other
        // rates (spec §7.9).
        var fmt96 = new MixFormat(96000, 2);
        using var ep96 = new HeadlessAudioEndpoint(fmt96);
        var session96 = new PcmAudioSession(fmt96, ep96.Sink, ep96.Clock, maxBlock: 8192, driveWithOwnThread: false);
        var feed96 = new AudioFeedThread(session96, sampleRate: 96000);
        Assert.Equal(960, feed96.BlockFrames);   // 10 ms @ 96 kHz
        feed96.Dispose();
    }

    // ── (2) the RT catch-up burst cap ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RenderBurst_CapsBlocksPerWake_EvenWithASimulatedStallOfBufferedAudio()
    {
        var fmt = new MixFormat(48000, 2);
        using var endpoint = new HeadlessAudioEndpoint(fmt);
        var session = new PcmAudioSession(fmt, endpoint.Sink, endpoint.Clock, maxBlock: 8192, driveWithOwnThread: false);
        const int maxBlocksPerWake = 3;
        var feed = new AudioFeedThread(session, sampleRate: 48000, maxBlocksPerWake: maxBlocksPerWake);

        long frames = 48000L * 10;
        var voice = new SignalGeneratorSource(2, 48000, 220, 0.5f, frames);
        session.SetVoice(voice, TimeSpan.FromSeconds(10), frames, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        feed.ControlTickOnce();   // Opening → Buffering
        feed.ControlTickOnce();   // Buffering → Ready → Playing
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        // Simulate the post-stall condition: the decode-ahead ring is fully replenished (many blocks' worth buffered),
        // and the sink never blocks (HeadlessAudioEndpoint's Write always returns instantly) — exactly the situation
        // where an uncapped RT loop would drain the whole ring in one wake.
        for (int i = 0; i < 64; i++) feed.WorkerPumpOnce();
        var ring = feed.RingsSnapshot[0].Ring;
        int bufferedBeforeFrames = ring.BufferedFrames;
        Assert.True(bufferedBeforeFrames > maxBlocksPerWake * feed.BlockFrames,
            "test setup didn't actually buffer more than one burst's worth of audio");

        int blocks = feed.RenderBurst();

        Assert.Equal(maxBlocksPerWake, blocks);   // capped, even though far more was available to drain
        int drainedFrames = bufferedBeforeFrames - ring.BufferedFrames;
        Assert.True(drainedFrames <= maxBlocksPerWake * feed.BlockFrames,
            $"burst drained {drainedFrames} frames — more than the {maxBlocksPerWake}-block cap allows");

        // A second wake continues making progress (the cap is per-wake, not a permanent stop).
        int blocks2 = feed.RenderBurst();
        Assert.Equal(maxBlocksPerWake, blocks2);

        feed.Dispose();
    }

    [Fact]
    public void RenderBurst_StopsEarly_WhenNothingIsRendered()
    {
        // No voice attached ⇒ RtRenderOnce has nothing to mix (paused/inert) ⇒ FeedOnce renders 0 ⇒ RenderBurst must stop
        // immediately rather than spinning to the cap.
        var fmt = new MixFormat(48000, 2);
        using var endpoint = new HeadlessAudioEndpoint(fmt);
        var session = new PcmAudioSession(fmt, endpoint.Sink, endpoint.Clock, maxBlock: 8192, driveWithOwnThread: false);
        var feed = new AudioFeedThread(session, sampleRate: 48000, maxBlocksPerWake: 5);

        int blocks = feed.RenderBurst();
        Assert.True(blocks < 5, "RenderBurst should stop before the cap when nothing renders");

        feed.Dispose();
    }

    // ── (3) low-water: edge-triggered, once per drop ────────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckLowWaterEdge_FiresOncePerDrop_NotRepeatedlyWhileStillLow()
    {
        var inner = new MemoryAudioSource(new float[48000 * 2], 2);   // plenty available, never exhausted mid-test
        var ring = new RingAudioSource(inner, 2, ringFrames: 4096, targetAheadFrames: 2048, pumpFrames: 512);

        // Empty ring ⇒ well below half target ⇒ the edge fires exactly once.
        Assert.True(ring.CheckLowWaterEdge());
        Assert.False(ring.CheckLowWaterEdge());   // still low — no repeat signal
        Assert.False(ring.CheckLowWaterEdge());   // still low — no repeat signal

        // Fill back above half target: the edge re-arms (returns false while at/above half, since it's not a drop).
        ring.PumpAhead();
        Assert.True(ring.BufferedFrames >= ring.TargetFrames / 2);
        Assert.False(ring.CheckLowWaterEdge());

        // Drain it below half again (RT reads) — a NEW drop must fire exactly once more.
        var dst = new float[(ring.TargetFrames) * 2];
        ring.Read(dst, 2);
        Assert.True(ring.BufferedFrames < ring.TargetFrames / 2);
        Assert.True(ring.CheckLowWaterEdge());
        Assert.False(ring.CheckLowWaterEdge());
    }

    [Fact]
    public void FeedOnce_SetsLowWaterEdgeOncePerRing_AsRingDrains()
    {
        // Drive the low-water edge the way FeedOnce actually exercises it: repeated RT reads of a ring that starts full
        // and is never refilled (worker starved) — the edge must fire exactly once as fill crosses the halfway mark, not
        // once per remaining block.
        var inner = new MemoryAudioSource(new float[96000 * 2], 2);
        var ring = new RingAudioSource(inner, 2, ringFrames: 4096, targetAheadFrames: 2048, pumpFrames: 512);
        ring.PumpAhead();   // fill to target once — then starve it (no further PumpAhead calls)
        Assert.True(ring.BufferedFrames >= ring.TargetFrames);

        var dst = new float[128 * 2];
        int fires = 0;
        for (int i = 0; i < 200 && ring.BufferedFrames > 0; i++)
        {
            ring.Read(dst, 2);
            if (ring.CheckLowWaterEdge()) fires++;
        }

        Assert.Equal(1, fires);
    }

    // ── (4) PumpAhead: transient 0-read must not be treated as EOF ──────────────────────────────────────────────────────

    /// <summary>A source that returns a TRANSIENT 0 (network not ready) for a fixed number of reads, then produces real
    /// data — but is NEVER exhausted, exercising the distinction PumpAhead must make.</summary>
    private sealed class TransientStallSource : IAudioSource
    {
        private readonly int _channels;
        private int _stallReadsLeft;
        public int ReadCalls { get; private set; }

        public TransientStallSource(int channels, int stallReads)
        {
            _channels = channels;
            _stallReadsLeft = stallReads;
        }

        public int Read(Span<float> dst, int channels)
        {
            ReadCalls++;
            if (_stallReadsLeft > 0) { _stallReadsLeft--; return 0; }   // transient: nothing ready yet — NOT exhausted
            dst.Clear();
            return dst.Length / channels;   // an endless live source once the "stall" clears
        }

        public long PositionFrames => 0;
        public bool Exhausted => false;   // a live source that never ends
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
    }

    [Fact]
    public void PumpAhead_TransientZeroRead_DoesNotLatchProducerDone()
    {
        var inner = new TransientStallSource(2, stallReads: 3);
        var ring = new RingAudioSource(inner, 2, ringFrames: 2048, targetAheadFrames: 1024, pumpFrames: 256);

        int decoded = ring.PumpAhead();   // one Read attempt: hits the transient stall, ends the pass with nothing
        Assert.Equal(0, decoded);
        Assert.False(ring.Exhausted);     // a transient 0-read must NEVER be treated like EOF
        Assert.Equal(1, inner.ReadCalls);
    }

    [Fact]
    public void PumpAhead_RetriesPromptly_OnceTheTransientStallClears()
    {
        var inner = new TransientStallSource(2, stallReads: 3);
        var ring = new RingAudioSource(inner, 2, ringFrames: 2048, targetAheadFrames: 1024, pumpFrames: 256);

        int decoded = 0;
        for (int i = 0; i < 8 && decoded == 0; i++) decoded = ring.PumpAhead();   // each call = one retry attempt

        Assert.True(decoded > 0, "PumpAhead never resumed decoding once the transient stall cleared");
        Assert.False(ring.Exhausted);
        Assert.True(inner.ReadCalls >= 4);   // 3 stalled attempts + the one that finally produced data
    }

    [Fact]
    public void PumpAhead_GenuineExhaustion_StillLatchesProducerDone()
    {
        // Sanity check: a REAL EOF (Exhausted) must still latch — the fix only carves out the transient case.
        var inner = new MemoryAudioSource(Array.Empty<float>(), 2);
        var ring = new RingAudioSource(inner, 2, ringFrames: 1024, targetAheadFrames: 512, pumpFrames: 128);

        ring.PumpAhead();
        Assert.True(ring.Exhausted);
    }
}
