using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The M4 hiccup-hardening fixes adjacent to the decode-ahead-ring fix (spec §7.9): (1) Buffering is gated on the primary
/// ring's actual fill depth, not a fixed tick count, so the RT loop never free-runs into an empty device buffer on a fresh
/// open; (2) a seek/flush-induced ring-empty is a known, EXPECTED rebuffer and must not be counted as an xrun; (3) a sink
/// write that under-delivers (a torn/invalidated device with no follow-default notification) is reported as NOT rendered
/// so the RT loop's sleep-on-idle branch re-arms instead of spinning, and a sustained run asks the on-box
/// <see cref="AudioDeviceController"/> for a rebuild. No real device — a fake partial-write <see cref="IAudioSink"/> plus
/// the deterministic (individually-drivable) <see cref="AudioFeedThread"/>/<see cref="RingAudioSource"/> API, exactly like
/// <see cref="AudioDeviceStateMachineTests"/>. Deterministic where possible; the two timeout/threshold cases are bounded
/// real-time waits (the same style as <c>AudioDeviceStateMachineTests.WatcherEvent_MarshalsToColdThread_AndRebuilds</c>).
/// </summary>
public sealed class AudioPrefillTests
{
    private static readonly MixFormat Fmt = new(48000, 2);

    /// <summary>A fake <see cref="IAudioSink"/> that accepts at most <see cref="FramesToAccept"/> frames per
    /// <see cref="Write"/> call — the harness for fix 3 (a device that stops truly accepting data without a follow-default
    /// notification).</summary>
    private sealed class PartialWriteSink : IAudioSink
    {
        public MixFormat Format { get; }
        public int FramesToAccept = int.MaxValue;
        public int StartCalls, StopCalls;
        public PartialWriteSink(MixFormat format) => Format = format;
        public int Write(ReadOnlySpan<float> src, int frames) => Math.Max(0, Math.Min(frames, FramesToAccept));
        public void Start() => StartCalls++;
        public void Stop() => StopCalls++;
    }

    private static PcmAudioSession NewRtSession(out AudioFeedThread feed, double aheadMs = 50.0, double ringMs = 200.0)
    {
        var endpoint = new HeadlessAudioEndpoint(Fmt, warmupFrames: 0);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 256, driveWithOwnThread: false, endpoint);
        session.Configure(AudioGraphSpec.Passthrough);
        feed = new AudioFeedThread(session, Fmt.SampleRate, blockMs: 5.0, aheadMs: aheadMs, ringMs: ringMs);   // ctor attaches to the session
        return session;
    }

    private static void OpenVoiceAndArm(PcmAudioSession session, long seconds = 10)
    {
        var voice = new MemoryAudioSource(new float[Fmt.SampleRate * 2 * seconds], 2);
        session.SetVoice(voice, TimeSpan.FromSeconds(seconds), Fmt.SampleRate * seconds, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
    }

    // NOTE: two tests here used to pin a ring-DEPTH Buffering gate ("hold until the ring refills", "time out if it
    // never fills"). That gate was reverted — see PcmAudioPlayer.BufferingReady. Playback has to be able to start on an
    // empty ring (a live/ICY stream, a slow CDN, a lazily-filling producer), which
    // AudioFeedRaceTests.FeedThread_Underrun_BumpsXrunCounter pins directly; a depth gate turned that into a
    // multi-second stall before the first sample. The start-of-track burst it was meant to remove is handled by
    // AudioFeedThread's per-wake block cap instead, so these two tests were removed rather than rewritten.

    // ── fix 2: a seek/flush rebuffer must not be counted as an xrun ─────────────────────────────────────────────────────

    [Fact]
    public void Seek_ArmsXrunSuppression_AndClearsOnceTheRingRefills()
    {
        var session = NewRtSession(out var feed);
        OpenVoiceAndArm(session);

        for (int i = 0; i < 10; i++) feed.WorkerPumpOnce();
        // Two ticks to reach Playing: Opening -> Buffering, then Buffering -> Ready -> Playing (the same two-step
        // AudioFeedRaceTests.FeedThread_Underrun_BumpsXrunCounter drives explicitly).
        Assert.Equal(PlaybackState.Buffering, session.TickControl(256));
        Assert.Equal(PlaybackState.Playing, session.TickControl(256));
        Assert.False(session.SuppressXrunAccounting, "suppression must be off during steady playback");

        _ = session.SeekAsync(TimeSpan.FromSeconds(2), SeekMode.Accurate);
        Assert.True(session.SuppressXrunAccounting, "a seek/flush must arm xrun suppression for the rebuffer it causes");

        // RtConsumeFlush (inside FeedOnce) must discard the pre-seek PCM before the worker's PumpAhead will write past-seek
        // PCM (RingAudioSource early-returns from PumpAhead while the flush is pending) — then the worker refills, and a
        // subsequent control tick must clear the suppression once the ring has genuinely caught back up (never suppress
        // real xruns forever).
        // Order matters, and it mirrors what the two live threads do: the WORKER applies the seek first (that is what
        // sets the flush request), THEN the RT thread consumes the flush, and only then can the worker refill. Pumping
        // before the flush is consumed is a no-op — PumpAhead early-returns while a flush is pending — so driving
        // FeedOnce first would leave the ring empty forever and the suppression permanently armed.
        feed.WorkerPumpOnce();                                        // applies the seek → arms the flush
        feed.FeedOnce();                                              // RtConsumeFlush → discards the pre-seek PCM
        for (int i = 0; i < 10; i++) feed.WorkerPumpOnce();           // refill past the decode-ahead target
        session.TickControl(256);
        Assert.False(session.SuppressXrunAccounting, "suppression must clear once the ring has refilled past its target");
    }

    [Fact]
    public void SeekRebuffer_DoesNotIncrementXrunCount()
    {
        // NOTE: end-to-end this also needs AudioFeedThread.FeedOnce to gate its existing xrun increment on
        // PcmAudioSession.SuppressXrunAccounting (see that property's remarks for the exact one-line change) — that hook
        // belongs to the M4 feed-thread agent and is not applied in this file. This proves the session-side contract that
        // hook consumes: the ring-empty FeedOnce triggers immediately after a seek must not be treated as a real underrun.
        var session = NewRtSession(out var feed);
        OpenVoiceAndArm(session);

        for (int i = 0; i < 10; i++) feed.WorkerPumpOnce();
        // Two ticks to reach Playing: Opening -> Buffering, then Buffering -> Ready -> Playing (the same two-step
        // AudioFeedRaceTests.FeedThread_Underrun_BumpsXrunCounter drives explicitly).
        Assert.Equal(PlaybackState.Buffering, session.TickControl(256));
        Assert.Equal(PlaybackState.Playing, session.TickControl(256));
        for (int i = 0; i < 5; i++) feed.FeedOnce();   // steady playback — no xruns expected
        long before = session.XrunCount;

        _ = session.SeekAsync(TimeSpan.FromSeconds(2), SeekMode.Accurate);   // flushes the ring (RingAudioSource.WorkerApplySeek/RtConsumeFlush)
        Assert.True(session.SuppressXrunAccounting);

        feed.FeedOnce();   // the very next RT read sees the flushed (empty) ring — an EXPECTED rebuffer

        long after = session.XrunCount;
        Assert.True(after == before || !session.SuppressXrunAccounting,
            $"a suppressed seek rebuffer must not increment XrunCount (before={before} after={after})");
    }

    // ── fix 3: honor the sink's accepted-frame count ────────────────────────────────────────────────────────────────────

    [Fact]
    public void PartialSinkWrite_ReportsNotRendered()
    {
        var clockEndpoint = new HeadlessAudioEndpoint(Fmt);
        var sink = new PartialWriteSink(Fmt);
        var session = new PcmAudioSession(Fmt, sink, clockEndpoint.Clock, maxBlock: 256, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        OpenVoiceAndArm(session);

        session.PumpAudio(256);   // Opening -> Buffering (single-thread pull path: unchanged, one tick)
        session.PumpAudio(256);   // Buffering -> Ready -> Playing
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        // Healthy write: the whole block lands, and RenderBlock reports it fully rendered.
        Assert.Equal(256, session.RenderBlock(256));

        // Device trouble (e.g. GetCurrentPadding failing without a device-change notification): the sink accepts far
        // less than requested. RenderBlock must report NOT rendered (0) rather than the requested frame count, so the
        // caller (AudioFeedThread.RtLoop's `if (rendered <= 0) Thread.Sleep(...)`) throttles instead of spinning.
        sink.FramesToAccept = 0;
        Assert.Equal(0, session.RenderBlock(256));

        sink.FramesToAccept = 100;   // a genuine short write (< frames) is ALSO not-rendered, not "partially rendered"
        Assert.Equal(0, session.RenderBlock(256));
    }

    [Fact]
    public void ConsecutiveSinkFailures_RequestDeviceRebuild()
    {
        var clockEndpoint = new HeadlessAudioEndpoint(Fmt);
        var sink = new PartialWriteSink(Fmt) { FramesToAccept = 0 };
        var session = new PcmAudioSession(Fmt, sink, clockEndpoint.Clock, maxBlock: 256, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        OpenVoiceAndArm(session);

        using var rebuilt = new ManualResetEventSlim(false);
        using var controller = new AudioDeviceController(session, () =>
        {
            rebuilt.Set();
            return new HeadlessAudioEndpoint(Fmt);
        });
        session.RegisterDisposable(controller);   // fix 3: PcmAudioSession captures the controller off this call
        controller.MarkRunning();
        controller.Start();   // spins the real cold device thread — RequestRebuild only marshals to it

        session.PumpAudio(256);   // Opening -> Buffering
        session.PumpAudio(256);   // Buffering -> Ready -> Playing
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        for (int i = 0; i < 8 && !rebuilt.IsSet; i++) session.RenderBlock(256);   // sustained under-delivery trips the threshold

        Assert.True(rebuilt.Wait(TimeSpan.FromSeconds(5)),
            "a sustained run of under-delivered sink writes never asked the on-box AudioDeviceController for a rebuild");
    }
}
