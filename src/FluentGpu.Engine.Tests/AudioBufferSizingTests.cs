using System;
using System.Threading;
using System.Threading.Tasks;
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
        feed.WorkerPumpOnce();
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

    // ── (5) D8: one RingSizing — 2 s ahead + 1 s kept behind — for the feed and every ring it wraps ───────────────────────

    private static readonly RingSizing ProductionSizing = new(BlockMs: 10.0, AheadMs: 2000.0, RingMs: 4000.0, KeepBehindMs: 1000.0);

    private static PcmAudioSession NewSession(MixFormat fmt)
    {
        var endpoint = new HeadlessAudioEndpoint(fmt);
        return new PcmAudioSession(fmt, endpoint.Sink, endpoint.Clock, maxBlock: 8192, driveWithOwnThread: false);
    }

    private static MemoryAudioSource Seconds(int seconds, int rate = 48000) => new(new float[rate * 2 * seconds], 2);

    /// <summary>An endless stereo source whose every sample equals its frame index, so a ring read can be checked for
    /// exactness. <see cref="BeforeRead"/> runs inside <see cref="Read"/> — i.e. AFTER the producer measured the ring's free
    /// space and BEFORE it writes — which is exactly where a consumer-side rewind lands in the race the carry exists for.</summary>
    private sealed class FrameIndexSource : IAudioSource
    {
        private long _pos;
        private int _readsUntilHook;
        private Action? _hook;
        public void RunOnReadNumber(int readNumber, Action hook) { _readsUntilHook = readNumber; _hook = hook; }

        public int Read(Span<float> dst, int channels)
        {
            if (_readsUntilHook > 0 && --_readsUntilHook == 0) { var hook = _hook; _hook = null; hook?.Invoke(); }
            int frames = dst.Length / channels;
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < channels; c++) dst[i * channels + c] = _pos + i;
            _pos += frames;
            return frames;
        }

        public long PositionFrames => _pos;
        public bool Exhausted => false;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
    }

    [Fact]
    public void RingSizingCtor_D8_SizesTheFeedAndEveryRingItWraps()
    {
        var session = NewSession(new MixFormat(48000, 2));
        var feed = new AudioFeedThread(session, sampleRate: 48000, rt: null, sizing: ProductionSizing);

        Assert.Equal(480, feed.BlockFrames);
        Assert.Equal(96000, feed.TargetAheadFrames);           // 2 s
        Assert.Equal(48000, feed.KeepBehindFrames);            // 1 s

        var ring = (RingAudioSource)feed.Wrap(Seconds(10));
        Assert.Equal(96000, ring.TargetFrames);                // the ring is held 2 s ahead …
        Assert.Equal(0, ring.KeptBehindFrames);                // … and has played nothing yet
        for (int i = 0; i < 4; i++) feed.WorkerPumpOnce();
        Assert.True(ring.BufferedFrames >= 96000, $"the 2 s decode-ahead was not reached: {ring.BufferedFrames} frames");

        var chunk = new float[500 * 2];
        for (int i = 0; i < 60; i++) Assert.Equal(500, ring.Read(chunk, 2));   // 30 000 frames played
        Assert.Equal(30000, ring.KeptBehindFrames);            // all of it is still kept …
        for (int i = 0; i < 60; i++) Assert.Equal(500, ring.Read(chunk, 2));   // 60 000 frames played
        Assert.Equal(48000, ring.KeptBehindFrames);            // … up to the 1 s protected span

        var second = (RingAudioSource)feed.WrapAdditional(Seconds(10), voiceId: 99);
        Assert.Equal(96000, second.TargetFrames);              // a crossfade/prepared voice gets the same cushion
        feed.Dispose();
        _ = session.DisposeAsync();
    }

    [Fact]
    public void MsSizedCtor_KeepsNothingBehind_AndForwardsToTheSameRingSizingPath()
    {
        var session = NewSession(new MixFormat(48000, 2));
        var feed = new AudioFeedThread(session, sampleRate: 48000);
        Assert.Equal(0, feed.KeepBehindFrames);
        Assert.Equal(24000, feed.TargetAheadFrames);           // 500 ms, as before

        var ring = (RingAudioSource)feed.Wrap(Seconds(5));
        feed.WorkerPumpOnce();
        var chunk = new float[480 * 2];
        for (int i = 0; i < 10; i++) ring.Read(chunk, 2);
        Assert.Equal(0, ring.KeptBehindFrames);                // nothing is protected, so nothing is rewindable
        Assert.False(ring.RtTryJump(-1));
        feed.Dispose();
        _ = session.DisposeAsync();
    }

    [Fact]
    public void Resize_RederivesTheKeptBehindSpanAgainstTheNewRate()
    {
        var session = NewSession(new MixFormat(48000, 2));
        var feed = new AudioFeedThread(session, sampleRate: 48000, rt: null, sizing: ProductionSizing);
        feed.Resize(new MixFormat(96000, 2));
        Assert.Equal(960, feed.BlockFrames);
        Assert.Equal(192000, feed.TargetAheadFrames);          // still 2 s
        Assert.Equal(96000, feed.KeepBehindFrames);            // still 1 s
        feed.Dispose();
        _ = session.DisposeAsync();
    }

    [Fact]
    public void Ring_StartsItsContentCursorWhereTheAlreadySoughtDecoderIs()
    {
        var inner = Seconds(1);
        inner.SeekFrame(1000);
        Assert.Equal(1000, new RingAudioSource(inner, 2, 2048, 1024, 256).PositionFrames);                       // V-PE3: not 0
        Assert.Equal(5000, new RingAudioSource(inner, 2, 2048, 1024, 256, startFrames: 5000).PositionFrames);    // the larger wins
        Assert.Equal(1000, new RingAudioSource(inner, 2, 2048, 1024, 256, startFrames: 10).PositionFrames);
    }

    [Fact]
    public void RtTryJump_RewindsIntoTheKeptSpan_AndSkipsInsidePublishedData_MovingTheContentCursor()
    {
        var ring = new RingAudioSource(Seconds(10), 2, ringFrames: 8192, targetAheadFrames: 2048, pumpFrames: 256, keepBehindFrames: 1024);
        ring.PumpAhead();
        Assert.True(ring.BufferedFrames >= 2048);
        var chunk = new float[512 * 2];
        for (int i = 0; i < 4; i++) Assert.Equal(512, ring.Read(chunk, 2));   // 2048 frames played
        Assert.Equal(2048, ring.PositionFrames);
        Assert.Equal(1024, ring.KeptBehindFrames);

        Assert.False(ring.RtTryJump(-1025));                   // beyond the kept span: refused, nothing moves
        Assert.Equal(2048, ring.PositionFrames);
        Assert.True(ring.RtTryJump(-1024));
        Assert.Equal(1024, ring.PositionFrames);               // the content cursor moves with the head
        Assert.Equal(0, ring.KeptBehindFrames);
        Assert.False(ring.RtTryJump(-1));

        int buffered = ring.BufferedFrames;
        Assert.True(ring.RtTryJump(100));
        Assert.Equal(1124, ring.PositionFrames);
        Assert.Equal(buffered - 100, ring.BufferedFrames);
        Assert.False(ring.RtTryJump(1_000_000));               // past the published data
        Assert.Equal(1124, ring.PositionFrames);
    }

    [Fact]
    public void RtTryJumpReadAndGrowAhead_AllocateNothing()
    {
        var ring = new RingAudioSource(Seconds(10), 2, ringFrames: 8192, targetAheadFrames: 2048, pumpFrames: 256, keepBehindFrames: 1024);
        ring.PumpAhead();
        var chunk = new float[64 * 2];

        void Cycle()
        {
            ring.Read(chunk, 2);
            ring.RtTryJump(-64);                               // re-read the same 64 frames forever: the ring never drains
            _ = ring.KeptBehindFrames;
            ring.GrowAhead();
        }

        for (int i = 0; i < 200; i++) Cycle();                 // warm the JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) Cycle();
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── (6) one xrun per incident (V-PE30), GrowAhead once per incident ──────────────────────────────────────────────────

    [Fact]
    public void Starvation_IsOneIncident_UntilAFullReadClosesIt()
    {
        var ring = new RingAudioSource(Seconds(1), 2, ringFrames: 2048, targetAheadFrames: 1024, pumpFrames: 256);
        var dst = new float[256 * 2];

        for (int i = 0; i < 3; i++) Assert.Equal(0, ring.Read(dst, 2));   // an empty ring, the source not exhausted: three starved blocks
        Assert.True(ring.ConsumeStarve());                      // ONE incident edge …
        Assert.Equal(3 * 256, ring.ConsumeStarveFrames());      // … carrying the whole shortfall

        Assert.Equal(0, ring.Read(dst, 2));
        Assert.False(ring.ConsumeStarve());                     // the incident is still open: no second edge
        Assert.Equal(256, ring.ConsumeStarveFrames());          // but its frames keep accruing

        ring.PumpAhead();                                       // the producer catches up
        Assert.Equal(1024, ring.BufferedFrames);
        for (int i = 0; i < 4; i++) Assert.Equal(256, ring.Read(dst, 2));   // full reads close the incident
        Assert.False(ring.ConsumeStarve());
        Assert.Equal(0, ring.ConsumeStarveFrames());

        Assert.Equal(0, ring.Read(dst, 2));                     // drained again: a NEW incident
        Assert.True(ring.ConsumeStarve());
    }

    [Fact]
    public void RecordStarvedFrames_SharesTheIncidentLatch_WithTheRead()
    {
        var ring = new RingAudioSource(Seconds(1), 2, ringFrames: 2048, targetAheadFrames: 1024, pumpFrames: 256);
        for (int i = 0; i < 3; i++) ring.RecordStarvedFrames(480);   // three silence blocks the session submitted
        Assert.True(ring.ConsumeStarve());                      // one incident …
        Assert.Equal(3 * 480, ring.ConsumeStarveFrames());      // … with the severity of all three

        ring.RecordStarvedFrames(480);
        Assert.False(ring.ConsumeStarve());                     // still the same incident
        Assert.Equal(480, ring.ConsumeStarveFrames());

        ring.PumpAhead();
        Assert.Equal(256, ring.Read(new float[256 * 2], 2));    // a full read closes it
        ring.RecordStarvedFrames(10);
        Assert.True(ring.ConsumeStarve());                      // the next starve is a new incident
    }

    [Fact]
    public void GrowAhead_DoublesTheTarget_CappedAboveTheKeptBehindSpan()
    {
        var plain = new RingAudioSource(Seconds(1), 2, ringFrames: 4096, targetAheadFrames: 1024, pumpFrames: 256);
        Assert.Equal(1024, plain.TargetFrames);
        plain.GrowAhead();
        Assert.Equal(2048, plain.TargetFrames);
        plain.GrowAhead();
        Assert.Equal(4096, plain.TargetFrames);                 // the whole 8192-float ring
        plain.GrowAhead();
        Assert.Equal(4096, plain.TargetFrames);                 // capped

        var kept = new RingAudioSource(Seconds(1), 2, ringFrames: 4096, targetAheadFrames: 1024, pumpFrames: 256, keepBehindFrames: 1024);
        Assert.Equal(1024, kept.TargetFrames);
        kept.GrowAhead();
        Assert.Equal(2048, kept.TargetFrames);
        kept.GrowAhead();
        Assert.Equal(3072, kept.TargetFrames);                  // 4096 − the 1024 frames that stay protected behind the head
        kept.GrowAhead();
        Assert.Equal(3072, kept.TargetFrames);
    }

    [Fact]
    public async Task FeedOnce_GrowsAStarvedRingsDecodeAheadOnTheIncidentEdge()
    {
        var fmt = new MixFormat(48000, 2);
        var endpoint = new HeadlessAudioEndpoint(fmt);
        var session = new PcmAudioSession(fmt, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        var feed = new AudioFeedThread(session, blockFrames: 256);   // 8192-frame ring, 4096 frames ahead
        session.Configure(AudioGraphSpec.Passthrough);
        long frames = 5L * fmt.SampleRate;
        session.SetVoice(new SignalGeneratorSource(2, fmt.SampleRate, 220, 0.5f, frames), TimeSpan.FromSeconds(5), frames, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        feed.ControlTickOnce();
        feed.ControlTickOnce();
        feed.FeedOnce();
        feed.WorkerPumpOnce();
        feed.ControlTickOnce();
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        var ring = feed.RingsSnapshot[0].Ring;
        Assert.Equal(4096, ring.TargetFrames);
        for (int i = 0; i < 128; i++) feed.FeedOnce();          // nobody pumps: the ring drains and starves
        feed.WorkerPumpOnce();
        for (int i = 0; i < 16; i++) feed.FeedOnce();
        Assert.True(feed.XrunCount >= 1);
        Assert.Equal(8192, ring.TargetFrames);                  // grown on the incident edge, capped by the 16384-float ring

        feed.Dispose();
        await session.DisposeAsync();
    }

    // ── (7) PumpAhead never drops decoded audio when the consumer rewinds mid-decode ─────────────────────────────────────

    [Fact]
    public void PumpAhead_KeepsDecodedAudio_WhenTheConsumerRewindsBetweenTheFreeSpaceReadAndTheWrite()
    {
        // ch = 2, ring 256 floats, keep-behind 32 floats, target 192 floats, pump block 32 floats.
        var source = new FrameIndexSource();
        var ring = new RingAudioSource(source, 2, ringFrames: 128, targetAheadFrames: 96, pumpFrames: 16, keepBehindFrames: 16);

        ring.PumpAhead();                                       // frames 0..95 buffered (192 floats)
        Assert.Equal(96, ring.BufferedFrames);
        var chunk = new float[20 * 2];
        Assert.Equal(20, ring.Read(chunk, 2));                  // play frames 0..19 → head at float 40
        for (int i = 0; i < 20; i++) Assert.Equal((float)i, chunk[i * 2]);

        // The 2nd decode read of the next pass sees 40 free floats, then the RT rewinds 6 frames (12 floats): only 28 floats fit.
        source.RunOnReadNumber(2, () => Assert.True(ring.RtTryJump(-6)));
        int decoded = ring.PumpAhead();
        Assert.Equal(16 + 14, decoded);                         // 16 frames, then 14 of the next 16 — the last 2 are carried
        Assert.Equal(14, ring.PositionFrames);                  // 20 played − the 6-frame rewind

        var played = new float[112 * 2];
        Assert.Equal(112, ring.Read(played, 2));
        for (int i = 0; i < 112; i++)
        {
            Assert.Equal((float)(14 + i), played[i * 2]);       // frames 14..125: the rewound span re-read, then the new audio
            Assert.Equal((float)(14 + i), played[i * 2 + 1]);
        }
        Assert.Equal(126, ring.PositionFrames);

        ring.PumpAhead();                                       // the carry (frames 126, 127) goes in BEFORE anything newer
        var next = new float[30 * 2];
        Assert.Equal(30, ring.Read(next, 2));
        for (int i = 0; i < 30; i++) Assert.Equal((float)(126 + i), next[i * 2]);   // no gap at 126/127, no repeat
    }

    // ── (8) ReadyWake / IsReady: event-driven readiness (V-PE10) ─────────────────────────────────────────────────────────

    [Fact]
    public void IsReady_NeedsBufferedFramesOrAFinishedProducer_AndNeverWithAPendingFlush()
    {
        var ring = new RingAudioSource(Seconds(5), 2, ringFrames: 4096, targetAheadFrames: 1024, pumpFrames: 256);
        Assert.False(ring.IsReady(1));                          // empty, producer running
        ring.PumpAhead();
        Assert.True(ring.IsReady(1024));
        Assert.False(ring.IsReady(2000));                       // not enough yet, and the producer is not done

        var tiny = new RingAudioSource(new MemoryAudioSource(new float[1000 * 2], 2), 2, ringFrames: 4096, targetAheadFrames: 4096, pumpFrames: 256);
        tiny.PumpAhead();                                       // a 1000-frame source shorter than the 4096-frame target: exhausted
        Assert.True(tiny.ProducerDone);
        Assert.True(tiny.IsReady(1_000_000));                   // waiting longer cannot help: ready

        ring.WorkerApplySeek(500);                              // a seek flush is now pending
        Assert.True(ring.HasPendingFlush);
        Assert.False(ring.IsReady(0));                          // what it holds is pre-seek audio the RT is about to discard
        ring.RtConsumeFlush();
        Assert.True(ring.IsReady(0));
    }

    [Fact]
    public void Producer_SetsReadyWake_OnceTheMinimumIsMet()
    {
        var ring = new RingAudioSource(Seconds(10), 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512);
        Volatile.Write(ref ring.ReadyMinimum, 2000);
        ring.ReadyWake.Reset();
        ring.StartProducer();
        Assert.True(ring.ReadyWake.WaitOne(3000), "the producer never signalled readiness");
        Assert.True(ring.IsReady(2000));
        ring.Dispose();
        Assert.True(ring.JoinProducer(3000));
    }

    [Fact]
    public async Task WaitUntilReadyAsync_CompletesFromTheProducersSignal()
    {
        var ring = new RingAudioSource(Seconds(10), 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512);
        ring.StartProducer();
        await ring.WaitUntilReadyAsync(3000, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ring.BufferedFrames >= 3000);
        ring.Dispose();
        Assert.True(ring.JoinProducer(3000));
    }

    [Fact]
    public async Task WaitUntilReadyAsync_ObservesCancellation_WithoutAProducer()
    {
        var ring = new RingAudioSource(Seconds(10), 2, ringFrames: 8192, targetAheadFrames: 4096, pumpFrames: 512);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ring.WaitUntilReadyAsync(1000, cts.Token));
        ring.Dispose();
    }

    [Fact]
    public void Dispose_ReleasesReadyWakeWithTheRing()
    {
        var ring = new RingAudioSource(Seconds(1), 2, ringFrames: 2048, targetAheadFrames: 1024, pumpFrames: 256);
        ring.Dispose();                                         // no producer was ever started: the ring owns the event's disposal
        Assert.ThrowsAny<ObjectDisposedException>(() => ring.ReadyWake.Reset());
    }
}
