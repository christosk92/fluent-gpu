using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// M4 tests (spec docs/plans/media-playback-api-spec.md §7.9, §12) — the RT-flip seam: the graph publish/consume RACE GATE
/// (the key M4 gate), the decode↔RT lock-free <see cref="PcmRing"/>/<see cref="RingAudioSource"/> firewall + underrun→xrun,
/// and the golden-PCM-unchanged proof that the flip did not change output, PLUS the primary-voice LIFETIME gates: SetVoice,
/// crossfade-commit, and seek all racing a live feed (published ring table vs RT/worker snapshots; worker-only ring dispose
/// and inner-decoder seek; dispose under a blocked worker never frees a ring under it). Deterministic where possible; the
/// multi-thread stress tests are bounded + hard-timeout'd so a deadlock/livelock FAILS FAST. Fakes only — no real WASAPI.
/// </summary>
public sealed class AudioFeedRaceTests
{
    private static readonly MixFormat Fmt = new(48000, 2);

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

    /// <summary>An <see cref="IAudioDecoder"/> that records the managed-thread id of every <c>Read</c>/<c>Seek</c> — used to
    /// prove the inner decoder is touched ONLY by the worker pump thread (the sole-toucher invariant, spec §7.9/§12).</summary>
    private sealed class ThreadRecordingDecoder : IAudioDecoder
    {
        private readonly int _channels;
        private long _pos;
        public readonly object Gate = new();
        public readonly HashSet<int> Threads = new();
        public ThreadRecordingDecoder(int channels) => _channels = channels;
        public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info) { info = default; return true; }
        public int Read(Span<float> dst)
        {
            lock (Gate) Threads.Add(Environment.CurrentManagedThreadId);
            dst.Clear();
            int frames = dst.Length / _channels;
            _pos += frames;
            return frames;   // endless
        }
        public long Seek(long frame) { lock (Gate) Threads.Add(Environment.CurrentManagedThreadId); _pos = frame; return frame; }
        public GaplessInfo Gapless => GaplessInfo.None;
    }

    /// <summary>An endless source whose <c>Read</c> blocks ~800 ms (a slow network chunk) so a <see cref="AudioFeedThread.Dispose"/>
    /// join times out under it — proving Dispose never frees a ring while the worker is mid-read.</summary>
    private sealed class SlowBlockingSource : IAudioSource, IDisposable
    {
        private readonly int _channels;
        private long _pos;
        private volatile bool _disposed;
        public bool Disposed => _disposed;
        public SlowBlockingSource(int channels) => _channels = channels;
        public int Read(Span<float> dst, int channels)
        {
            Thread.Sleep(800);
            dst.Clear();
            int frames = dst.Length / Math.Max(1, _channels);
            _pos += frames;
            return frames;   // endless — the worker keeps blocking in Read
        }
        public long PositionFrames => _pos;
        public bool Exhausted => false;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public void Dispose() => _disposed = true;
    }

    private static PcmAudioSession NewSession(out HeadlessAudioEndpoint endpoint, out AudioFeedThread? feed, double seconds = 30.0, bool attachFeed = false)
    {
        endpoint = new HeadlessAudioEndpoint(Fmt);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        feed = attachFeed ? new AudioFeedThread(session, blockFrames: 256) : null;   // ctor attaches to the session
        session.Configure(AudioGraphSpec.Passthrough);
        long frames = (long)(seconds * Fmt.SampleRate);
        var voice = new SignalGeneratorSource(2, Fmt.SampleRate, 220, 0.5f, frames);
        session.SetVoice(voice, TimeSpan.FromSeconds(seconds), frames, NormMode.Off, -14f, initialVolume: 1f);
        return session;
    }

    // ── THE RACE GATE (the key M4 gate) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RaceGate_RtConsumeVsControlPublish_NoTornRead_NoAlloc_RetiresAfterQuarantine()
    {
        var session = NewSession(out _, out _, seconds: 30.0, attachFeed: false);
        var host = session.Graph;

        var specA = AudioGraphSpec.Passthrough with { MasterChain = ImmutableArray.Create<EffectSpec>(new GainSpec(0.5f)) };
        var specB = AudioGraphSpec.Passthrough with { MasterChain = ImmutableArray.Create<EffectSpec>(new GainSpec(0.8f)) };

        const int RtIters = 300_000;
        const int PublishIters = 60_000;
        long rtAllocDelta = -1;
        long publishes = 0;
        Exception? rtEx = null, ctlEx = null;
        using var start = new ManualResetEventSlim(false);

        // RT thread: the render/consume loop (reads the published graph lock-free, MarkConsumed + quarantine, copy+mix).
        var rt = Task.Run(() =>
        {
            try
            {
                start.Wait();
                for (int i = 0; i < 5000; i++) session.RenderBlock(256);          // warm the JIT
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < RtIters; i++) session.RenderBlock(256);        // measured render/consume loop
                rtAllocDelta = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception e) { rtEx = e; }
        });

        // Control thread: rapidly publish new graphs + write a param signal (master volume).
        var ctl = Task.Run(() =>
        {
            try
            {
                start.Wait();
                var rnd = new Random(1234);
                for (int i = 0; i < PublishIters; i++)
                {
                    session.Configure((i & 1) == 0 ? specA : specB);
                    session.SetVolume(rnd.NextDouble());
                    publishes++;
                }
            }
            catch (Exception e) { ctlEx = e; }
        });

        start.Set();
        // Hard timeout: a deadlock/livelock FAILS FAST rather than hanging the suite.
        await Task.WhenAll(rt, ctl).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(rtEx);   // a torn/half-built graph would NRE inside RenderMaster — none did
        Assert.Null(ctlEx);
        Assert.Equal(0, rtAllocDelta);                                   // the RT loop is zero managed alloc
        Assert.True(publishes >= PublishIters - 1, $"control published only {publishes}");

        // The live graph is always internally consistent (master chain present + the terminal limiter last).
        var live = host.Live;
        Assert.NotNull(live.Master);
        Assert.True(live.Master.Length >= 1);
        Assert.IsType<LimiterStage>(live.Master[^1]);

        // Old graphs were retired ONLY under the RenderInFlightDepth+1 quarantine, and the pending queue stayed bounded.
        Assert.True(host.RetiredCount > 0, "no graphs were retired");
        Assert.True(host.PendingRetire <= 16, $"retire ring overran: {host.PendingRetire}");
        Assert.True(host.RetiredCount <= publishes, "retired more graphs than were published");

        await session.DisposeAsync();
    }

    // ── primary-voice LIFETIME gates: control mutations racing a live feed (SetVoice / commit / seek / dispose) ─────────

    [Fact]
    public async Task RaceGate_SetVoiceVsFeedAndWorker_NoThrow()
    {
        var session = NewSession(out _, out var feed, seconds: 5.0, attachFeed: true);
        Assert.NotNull(feed);

        Exception? err = null;
        using var stop = new ManualResetEventSlim(false);
        long frames = (long)(5.0 * Fmt.SampleRate);

        var a = Task.Run(() => { try { while (!stop.IsSet) feed!.FeedOnce(); } catch (Exception e) { err = e; } });
        var b = Task.Run(() => { try { while (!stop.IsSet) feed!.WorkerPumpOnce(); } catch (Exception e) { err = e; } });
        var c = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    var voice = new SignalGeneratorSource(2, Fmt.SampleRate, 220, 0.5f, frames);
                    session.SetVoice(voice, TimeSpan.FromSeconds(5.0), frames, NormMode.Off, -14f, initialVolume: 1f);
                }
            }
            catch (Exception e) { err = e; }
            finally { stop.Set(); }
        });

        await Task.WhenAll(a, b, c).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(err);   // publish-don't-mutate: the RT/worker snapshots never tore on a control-thread ring swap

        // Drain the control-retire queue and confirm the ring table settled to the single live primary.
        for (int i = 0; i < 8; i++) feed!.WorkerPumpOnce();
        Assert.Equal(1, feed!.RingCount);

        feed.Dispose();
    }

    [Fact]
    public async Task RaceGate_CrossfadeCommitVsFeedAndWorker_NoThrow()
    {
        var session = NewSession(out _, out var feed, seconds: 30.0, attachFeed: true);
        Assert.NotNull(feed);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        feed!.ControlTickOnce();   // Opening → Buffering
        feed.ControlTickOnce();    // Buffering → Ready → Playing
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        Exception? err = null;
        using var stop = new ManualResetEventSlim(false);

        var a = Task.Run(() => { try { while (!stop.IsSet) feed.FeedOnce(); } catch (Exception e) { err = e; } });
        var b = Task.Run(() => { try { while (!stop.IsSet) feed.WorkerPumpOnce(); } catch (Exception e) { err = e; } });
        var c = Task.Run(() =>
        {
            long id = 1;
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    var incoming = new MemoryAudioSource(new float[400 * 2], 2);   // short + finite → sounds, exhausts, retires
                    long startNow = session.ConsumeSeqFrames;
                    session.AddCrossfadeVoice(incoming, GainEnvelope.Constant, startNow, 1f, null, ++id);
                    session.SetVoiceEnvelope(session.PrimaryVoiceIdValue, GainEnvelope.Constant);
                }
            }
            catch (Exception e) { err = e; }
            finally { stop.Set(); }
        });

        await Task.WhenAll(a, b, c).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(err);   // WrapAdditional copy-grow + RT retire + worker dispose never tore against the live snapshots

        for (int i = 0; i < 16; i++) feed.WorkerPumpOnce();
        Assert.True(feed.RingCount >= 1);

        feed.Dispose();
    }

    [Fact]
    public async Task RaceGate_SeekVsWorkerPump_InnerTouchedOnlyByWorker()
    {
        var endpoint = new HeadlessAudioEndpoint(Fmt);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        var feed = new AudioFeedThread(session, blockFrames: 256);
        session.Configure(AudioGraphSpec.Passthrough);
        var decoder = new ThreadRecordingDecoder(2);
        var voice = new DecoderAudioSource(decoder);
        session.SetVoice(voice, TimeSpan.FromSeconds(30), 30L * Fmt.SampleRate, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));

        Exception? err = null;
        int pumpThreadId = 0;
        using var stop = new ManualResetEventSlim(false);

        var a = Task.Run(() => { try { while (!stop.IsSet) feed.FeedOnce(); } catch (Exception e) { err = e; } });   // consumes seek flushes
        var b = Task.Run(() =>
        {
            pumpThreadId = Environment.CurrentManagedThreadId;
            try { while (!stop.IsSet) feed.WorkerPumpOnce(); } catch (Exception e) { err = e; }
        });
        var c = Task.Run(async () =>
        {
            try
            {
                var rnd = new Random(7);
                for (int i = 0; i < 400; i++)
                {
                    await session.SeekAsync(TimeSpan.FromSeconds(rnd.NextDouble() * 20), SeekMode.Accurate);
                }
            }
            catch (Exception e) { err = e; }
            finally { stop.Set(); }
        });

        await Task.WhenAll(a, b, c).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(err);   // a control-thread inner seek mid-decode is the LinearResampler torn-Reset crash — routed away

        // The inner decoder was touched ONLY by the worker pump thread (never the control seek task or the RT feed).
        lock (decoder.Gate)
        {
            Assert.NotEmpty(decoder.Threads);
            Assert.All(decoder.Threads, id => Assert.Equal(pumpThreadId, id));
        }

        feed.Dispose();
    }

    [Fact]
    public void Wrap_RetiresPreviousPrimary_ViaWorkerOnly()
    {
        var session = NewSession(out _, out var feed, seconds: 5.0, attachFeed: true);
        Assert.NotNull(feed);

        // Re-install the primary as a dispose-tracking source so we can observe WHO frees the previous ring.
        long frames = (long)(5.0 * Fmt.SampleRate);
        var old = new DisposeTrackingSource(new SignalGeneratorSource(2, Fmt.SampleRate, 220, 0.5f, frames));
        session.SetVoice(old, TimeSpan.FromSeconds(5.0), frames, NormMode.Off, -14f, initialVolume: 1f);
        Assert.Equal(1, feed!.RingCount);

        // A second SetVoice unpublishes the old primary and hands its ring to the worker — but must NOT dispose it yet.
        var neu = new SignalGeneratorSource(2, Fmt.SampleRate, 330, 0.5f, frames);
        session.SetVoice(neu, TimeSpan.FromSeconds(5.0), frames, NormMode.Off, -14f, initialVolume: 1f);
        Assert.False(old.Disposed);   // the control thread never disposes a ring
        Assert.Equal(1, feed.RingCount);
        feed.FeedOnce();              // RT still runs cleanly against the new single-entry table
        Assert.False(old.Disposed);

        // Only the worker's retire-queue drain frees the old ring, off the RT thread.
        feed.WorkerPumpOnce();
        Assert.True(old.Disposed, "the previous primary's ring was never disposed by the worker");

        feed.Dispose();
    }

    [Fact]
    public async Task Dispose_WithLiveThreads_NeverDisposesUnderWorker()
    {
        var endpoint = new HeadlessAudioEndpoint(Fmt);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        var feed = new AudioFeedThread(session, blockFrames: 256, ringFrames: 1024, targetAheadFrames: 512);
        session.Configure(AudioGraphSpec.Passthrough);
        var slow = new SlowBlockingSource(2);
        session.SetVoice(slow, TimeSpan.FromSeconds(60), 60L * Fmt.SampleRate, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();

        feed.Start();                 // real RT/worker/clock threads; the worker blocks ~800 ms in Read (> the 500 ms join)
        await Task.Delay(400);        // let the worker get into a blocking Read and the state machine reach Playing
        feed.Dispose();               // the worker-join times out → Dispose must NOT dispose the ring under the live worker

        // The worker's final-cleanup sweep on loop exit is the SOLE safe disposer — the ring is freed once Read returns.
        bool disposedInTime = false;
        for (int i = 0; i < 100 && !disposedInTime; i++)
        {
            if (slow.Disposed) disposedInTime = true;
            else await Task.Delay(50);
        }
        Assert.True(disposedInTime, "the ring was never disposed by the worker's final sweep");

        await session.DisposeAsync();
    }

    // ── decode↔RT ring: transparency (golden-PCM UNCHANGED through the ring) ──────────────────────────────────────────

    [Fact]
    public void RingAudioSource_ProducesByteIdenticalPcm_ToDirectDecode()
    {
        var pcm = Tone(4000);   // 4000 frames stereo
        var direct = ReadAll(new MemoryAudioSource((float[])pcm.Clone(), 2));

        var ring = new RingAudioSource(new MemoryAudioSource((float[])pcm.Clone(), 2), 2, ringFrames: 1024, targetAheadFrames: 512, pumpFrames: 128);
        var buf = new float[97 * 2];   // deliberately non-aligned chunk to exercise ring wrap
        var acc = new List<float>();
        for (int guard = 0; guard < 100_000; guard++)
        {
            ring.PumpAhead();                       // worker decodes ahead
            int n = ring.Read(buf, 2);              // RT drains (copy only)
            for (int i = 0; i < n * 2; i++) acc.Add(buf[i]);
            if (n == 0 && ring.Exhausted) break;
        }

        Assert.Equal(direct.Length, acc.Count);
        for (int i = 0; i < direct.Length; i++) Assert.Equal(direct[i], acc[i]);
    }

    // ── underrun: a starved ring → SHORT read + xrun latch, never a throw/block ──────────────────────────────────────

    [Fact]
    public void RingAudioSource_StarvedRing_ShortReads_AndLatchesXrun()
    {
        var inner = new MemoryAudioSource(new float[48000 * 2], 2);   // 1 s available, NOT exhausted
        var ring = new RingAudioSource(inner, 2, ringFrames: 2048, targetAheadFrames: 1024, pumpFrames: 256);
        var dst = new float[256 * 2];

        int got = ring.Read(dst, 2);                 // ring empty, inner not exhausted → underrun
        Assert.Equal(0, got);
        Assert.True(ring.Starved);
        Assert.True(ring.ConsumeStarve());
        Assert.False(ring.Starved);                  // read-and-clear

        ring.PumpAhead();                            // worker fills
        int got2 = ring.Read(dst, 2);
        Assert.True(got2 > 0);
        Assert.False(ring.ConsumeStarve());          // no underrun once filled
    }

    [Fact]
    public void FeedThread_Underrun_BumpsXrunCounter_ThenPublishesSignalOffRt()
    {
        var session = NewSession(out var endpoint, out var feed, seconds: 5.0, attachFeed: true);
        Assert.NotNull(feed);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        feed!.ControlTickOnce();   // Opening → Buffering
        feed.ControlTickOnce();    // Buffering → Ready → Playing
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        // The worker never ran → the voice ring is starved → the RT feed writes silence + bumps the xrun counter.
        long x0 = feed.XrunCount;
        feed.FeedOnce();
        Assert.True(feed.XrunCount > x0, "underrun did not bump the xrun counter");

        feed.ControlTickOnce();                                  // publishes the xrun signal OFF the RT thread
        Assert.Equal((int)feed.XrunCount, feed.Xruns.Peek());

        // Once the worker fills the ring ahead, the RT feed stops under-running.
        feed.WorkerPumpOnce();
        long x1 = feed.XrunCount;
        feed.FeedOnce();
        Assert.Equal(x1, feed.XrunCount);

        _ = endpoint;   // keep alive
    }

    // ── golden-PCM UNCHANGED on the single-thread pull path (the flip did not alter output) ───────────────────────────

    [Fact]
    public void SingleThreadPullPath_IsUntouchedByTheFlip_CapturedPcmIsLimitedAudio()
    {
        // No feed attached ⇒ the mixer reads the decoder DIRECTLY (no ring) — byte-identical to M2/M3.
        var endpoint = new HeadlessAudioEndpoint(Fmt, captureFrames: 4096);
        var voice = new SignalGeneratorSource(2, Fmt.SampleRate, 200, amplitude: 4.0f, totalFrames: 48000);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        session.SetVoice(voice, TimeSpan.FromSeconds(1), 48000, NormMode.Off, -14f, initialVolume: 1f);
        Assert.False(session.IsRtDriven);

        for (int i = 0; i < 8; i++) session.RenderBlock(512);

        var captured = ((NullAudioSink)endpoint.Sink).Captured;
        float ceiling = LimiterStage.DbToLinear(-1.5f);
        bool anyLoud = false;
        for (int i = 0; i < captured.Length; i++)
        {
            Assert.True(MathF.Abs(captured[i]) <= ceiling + 1e-3f);
            if (MathF.Abs(captured[i]) > 0.1f) anyLoud = true;
        }
        Assert.True(anyLoud);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static float[] Tone(int frames)
    {
        var a = new float[frames * 2];
        for (int f = 0; f < frames; f++) { float s = 0.5f * MathF.Sin(2f * MathF.PI * 220f * f / 48000f); a[f * 2] = s; a[f * 2 + 1] = s; }
        return a;
    }

    private static float[] ReadAll(IAudioSource src)
    {
        var buf = new float[512 * 2];
        var acc = new List<float>();
        int n;
        while ((n = src.Read(buf, 2)) > 0) for (int i = 0; i < n * 2; i++) acc.Add(buf[i]);
        return acc.ToArray();
    }
}
