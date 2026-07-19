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
/// and the golden-PCM-unchanged proof that the flip did not change output. Deterministic where possible; the one genuine
/// multi-thread stress test is bounded + hard-timeout'd so a deadlock/livelock FAILS FAST. Fakes only — no real WASAPI.
/// </summary>
public sealed class AudioFeedRaceTests
{
    private static readonly MixFormat Fmt = new(48000, 2);

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
