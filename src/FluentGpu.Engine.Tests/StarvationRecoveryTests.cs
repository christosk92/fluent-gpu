using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F2 (playback smoothness, H-1/F2): a dry decode ring is NOT a reason to stop the device. These drive the REAL
/// <see cref="PcmAudioSession"/> — a deterministic device (<see cref="BufferedAudioEndpoint"/>) plus a hand-pumped
/// <see cref="AudioFeedThread"/> — and pin what the stop/start-cycle machine it replaced could never provide: the RT submits
/// SILENCE for the block (inside the tripwire), the device is never <c>Stop()</c>ped or <c>Reset()</c>, the content clock holds
/// while the raw device clock keeps running, resume waits for its cushion and fades in over 5 ms, and the cushion doubles per
/// incident (never for a seek rebuffer) and decays after 30 quiet seconds. Also F5 (the RT starts before the clock thread flips
/// the state). Fakes only; no wall clock.
/// </summary>
public sealed class StarvationRecoveryTests
{
    private static readonly MixFormat Fmt = new(48000, 2);
    private const int Block = 480;                 // 10 ms
    // The audio the producer has ready when the rig starts: the 100 ms startup cushion PLUS the one block the RT renders before the
    // clock thread's first Buffering tick (F5), so the ring still holds the cushion when that tick evaluates BufferingReady().
    private const int Content = 4800 + Block;

    /// <summary>An endless constant source whose SUPPLY the test gates: <see cref="AvailableFrames"/> is the cumulative number of
    /// frames it may have produced — past that it returns 0 without being exhausted, exactly a producer that fell behind.</summary>
    private sealed class GatedSource(float value, int available) : IAudioSource
    {
        public volatile int AvailableFrames = available;
        private long _position;
        public long PositionFrames => Volatile.Read(ref _position);
        public bool Exhausted => false;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public int Read(Span<float> dst, int channels)
        {
            long room = AvailableFrames - Volatile.Read(ref _position);
            int frames = (int)Math.Max(0, Math.Min(dst.Length / channels, room));
            dst[..(frames * channels)].Fill(value);
            Interlocked.Add(ref _position, frames);
            return frames;
        }
    }

    /// <summary>One session on a 960-frame device with a hand-pumped feed; <see cref="Step"/> is 10 ms of wall time.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly BufferedAudioEndpoint Endpoint;
        public readonly PcmAudioSession Session;
        public readonly AudioFeedThread Feed;
        public readonly GatedSource Source;
        public readonly MediaPlayerCore Core = new();

        public Rig(int availableFrames, int ringFrames = 48000, int aheadFrames = 24000, int deviceFrames = 960)
        {
            Endpoint = new BufferedAudioEndpoint(Fmt, capacityFrames: deviceFrames, captureFrames: 4 * 48000);
            Session = new PcmAudioSession(Fmt, Endpoint, Endpoint, Block, driveWithOwnThread: false);
            Feed = new AudioFeedThread(Session, blockFrames: Block, ringFrames: ringFrames, targetAheadFrames: aheadFrames);   // attaches itself
            Source = new GatedSource(0.5f, availableFrames);
            Session.SetVoice(Source, TimeSpan.FromSeconds(60), 60 * 48000L, NormMode.Off, -14f, initialVolume: 1f);
            Session.ConnectSignals(new MediaSignalSink(Core));
            _ = Session.PlayAsync();
        }

        /// <summary>10 ms: the worker decodes ahead (unless withheld), the RT renders one block, the device consumes one, the clock thread ticks.</summary>
        public void Step(bool pump = true)
        {
            if (pump) Feed.WorkerPumpOnce();
            Feed.FeedOnce();
            Endpoint.AdvanceHardware(Block);
            Session.TickControl(Block);
        }

        public void Dispose()
        {
            _ = Session.DisposeAsync();   // no feed threads were started: completes synchronously
            Endpoint.Dispose();
        }
    }

    /// <summary>Cut the supply and play the ring dry; a few silent blocks later the state has followed (Stalled).</summary>
    private static void Starve(Rig rig)
    {
        rig.Source.AvailableFrames = (int)rig.Source.PositionFrames;
        for (int i = 0; i < 400 && !rig.Session.IsStarved; i++) rig.Step();
        Assert.True(rig.Session.IsStarved, "the ring never ran dry");
        for (int i = 0; i < 4; i++) rig.Step();
    }

    /// <summary>The producer comes back with plenty — the session resumes as soon as its cushion is met.</summary>
    private static void Supply(Rig rig)
    {
        rig.Source.AvailableFrames = (int)rig.Source.PositionFrames + 120_000;
        for (int i = 0; i < 40 && rig.Session.IsStarved; i++) rig.Step();
        Assert.False(rig.Session.IsStarved, "the session never resumed");
        for (int i = 0; i < 2; i++) rig.Step();
    }

    [Fact]
    public void TheRtStartsRendering_BeforeTheClockThreadFlipsToPlaying()
    {
        // F5: the RT evaluates "ready" itself (state Buffering/Ready + play requested + the startup cushion in the ring), so the
        // first block — and the device Start — no longer wait for the clock thread's next tick.
        using var rig = new Rig(availableFrames: 48_000);
        rig.Feed.WorkerPumpOnce();
        rig.Session.TickControl(Block);                       // Opening → Buffering
        Assert.Equal(PlaybackState.Buffering, rig.Session.CurrentState);
        Assert.Equal(0, rig.Session.SubmittedFrames);

        rig.Feed.FeedOnce();                                   // no tick has run since: the state is still Buffering

        Assert.Equal(PlaybackState.Buffering, rig.Session.CurrentState);
        Assert.True(rig.Session.SubmittedFrames > 0, "the RT waited for the clock thread");
        Assert.True(rig.Endpoint.IsStarted);
    }

    [Fact]
    public void DryRing_SubmitsSilence_NeverStopsTheDevice_AndHoldsTheContentClock()
    {
        using var rig = new Rig(availableFrames: Content);   // ~110 ms of audio, then the producer goes silent
        for (int i = 0; i < 40; i++) rig.Step(pump: i == 0);

        Assert.True(rig.Session.IsStarved);
        Assert.Equal(PlaybackState.Stalled, rig.Session.CurrentState);
        Assert.Equal(0, rig.Endpoint.StopCount);            // the old machine Stop()ped + Reset() the device on every dry ring
        Assert.Equal(0, rig.Endpoint.ResetCount);
        Assert.Equal(1, rig.Endpoint.StartCount);
        Assert.True(rig.Endpoint.IsStarted);
        Assert.Equal(Content, rig.Session.SampleClock);      // the mixer timeline held: only real audio was consumed

        // The device played the content and then keeps running over silence: the RAW clock advances, the CONTENT clock does not.
        long contentPlayed = rig.Session.PlayedFrames, rawPlayed = rig.Session.RawPlayedFrames, severity = rig.Session.XrunFramesLost;
        TimeSpan position = rig.Core.Position.Peek();
        Assert.Equal(Content, contentPlayed);
        Assert.True(rawPlayed > contentPlayed);
        for (int i = 0; i < 10; i++) rig.Step(pump: false);
        Assert.Equal(contentPlayed, rig.Session.PlayedFrames);
        Assert.Equal(position, rig.Core.Position.Peek());    // the published position holds with the content clock
        Assert.True(rig.Session.RawPlayedFrames > rawPlayed + 4 * Block);
        Assert.True(rig.Session.XrunFramesLost > severity);  // severity accrues every silent block …
        Assert.Equal(1, rig.Session.XrunCount);              // … but the whole starve is ONE incident (needs the ring's edge latch, WP 1a)
        Assert.True(rig.Session.DevicePaddingFrames <= 960);

        // The content-domain bookkeeping: everything submitted past index `Content` is silence.
        Assert.Equal(Content, rig.Session.ContentSubmittedFrames());
        Assert.Equal(Content, rig.Session.ContentIndexAt(10_000));
        Assert.Equal(rig.Session.SubmittedFrames - Content, rig.Session.SilenceBefore(rig.Session.SubmittedFrames));

        // What the device actually received: the audio, then zeros.
        float[] pcm = rig.Endpoint.Captured.ToArray();
        Assert.Equal(0.5f, pcm[2400 * 2], 3);                 // past the 20 ms fade-in
        for (int f = Content; f < Content + 2000; f++) Assert.Equal(0f, pcm[f * 2]);
    }

    [Fact]
    public void SilencePadding_StaysShallow_SoReturningAudioIsNotQueuedBehindAFullDeviceBuffer()
    {
        // A 100 ms device buffer and an RT that renders up to three blocks per wake: an unbounded silence top-up would fill the whole
        // buffer, and audio returning after the absence would queue behind ~100 ms of it. The floor keeps the queue at
        // SilencePaddingFloorBlocks (+ at most one block of granularity).
        const int content = 12_000;
        using var rig = new Rig(availableFrames: content, deviceFrames: 4800);
        int maxSilencePadding = 0;
        for (int i = 0; i < 120; i++)
        {
            if (i == 0) rig.Feed.WorkerPumpOnce();
            rig.Feed.RenderBurst();
            rig.Endpoint.AdvanceHardware(Block);
            rig.Session.TickControl(Block);
            if (rig.Session.SubmittedFrames > content) maxSilencePadding = Math.Max(maxSilencePadding, rig.Endpoint.PaddingFrames);
        }

        Assert.True(rig.Session.IsStarved);
        Assert.True(maxSilencePadding > 0, "no silence was ever submitted");
        Assert.True(maxSilencePadding <= (PcmAudioSession.SilencePaddingFloorBlocks + 1) * Block,
            $"silence queued {maxSilencePadding} frames ahead of the device");
        Assert.Equal(0, rig.Endpoint.StopCount);

        // The ledger recorded only the silence that was actually submitted, so the content clock is still exact.
        Assert.Equal(content, rig.Session.ContentSubmittedFrames());
        Assert.Equal(rig.Session.SubmittedFrames - content, rig.Session.SilenceBefore(rig.Session.SubmittedFrames));
        Assert.Equal(content, rig.Session.PlayedFrames);
    }

    [Fact]
    public void SilenceBlocks_AreAllocationFree()
    {
        using var rig = new Rig(availableFrames: Content);
        for (int i = 0; i < 40; i++) rig.Step(pump: i == 0);   // warm every path: content, the starve edge, steady silence
        Assert.True(rig.Session.IsStarved);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            rig.Session.RenderBlock(Block);
            rig.Endpoint.AdvanceHardware(Block);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, rig.Endpoint.StopCount);
    }

    [Fact]
    public void Resume_WaitsForTheCushion_ThenFadesInOverFiveMs_WithoutRestartingTheDevice()
    {
        using var rig = new Rig(availableFrames: Content);
        for (int i = 0; i < 30; i++) rig.Step(pump: i == 0);
        Assert.True(rig.Session.IsStarved);
        Assert.Equal(Fmt.SampleRate / 10, rig.Session.ResumeFrames);   // the base cushion: 100 ms

        // 50 ms of new audio is below the 100 ms cushion: still silence.
        rig.Source.AvailableFrames = Content + 2400;
        for (int i = 0; i < 5; i++) rig.Step();
        Assert.True(rig.Session.IsStarved);

        // Past the cushion: the transport resumes on the very next block.
        rig.Source.AvailableFrames = Content + 9600;
        for (int i = 0; i < 4; i++) rig.Step();
        Assert.False(rig.Session.IsStarved);
        Assert.Equal(PlaybackState.Playing, rig.Session.CurrentState);
        Assert.Equal(0, rig.Endpoint.StopCount);
        Assert.Equal(0, rig.Endpoint.ResetCount);
        Assert.Equal(1, rig.Endpoint.StartCount);

        // The first resumed sample is silent and the gain ramps up over 5 ms (240 frames) — no click on the way back in.
        float[] pcm = rig.Endpoint.Captured.ToArray();
        int first = -1;
        for (int f = Content + 1000; f < pcm.Length / 2; f++) if (pcm[f * 2] != 0f) { first = f; break; }
        Assert.True(first > 0, "no audio came back");
        Assert.Equal(0f, pcm[(first - 1) * 2]);
        for (int f = first; f < first + 240; f++) Assert.True(pcm[f * 2] >= pcm[(f - 1) * 2] - 1e-6f, $"the fade-in is not monotonic at frame {f}");
        Assert.True(pcm[first * 2] < 0.05f);
        Assert.Equal(0.5f, pcm[(first + 250) * 2], 3);
    }

    [Fact]
    public void ResumeCushion_DoublesPerIncident_CapsAtTheAhead_AndDecaysAfterThirtyQuietSeconds()
    {
        long now = 0;
        using var rig = new Rig(availableFrames: Content);
        rig.Session.TickClockMs = () => Volatile.Read(ref now);
        for (int i = 0; i < 30; i++) rig.Step(pump: i == 0);
        Assert.Equal(4800, rig.Session.ResumeFrames);

        Supply(rig);
        Assert.Equal(9600, rig.Session.ResumeFrames);        // incident 1 → ×2
        Starve(rig); Supply(rig);
        Assert.Equal(19_200, rig.Session.ResumeFrames);      // incident 2 → ×2
        Starve(rig); Supply(rig);
        Assert.Equal(24_000, rig.Session.ResumeFrames);      // incident 3 → capped at the decode-ahead target (never more than a full ring can supply)
        Starve(rig); Supply(rig);
        Assert.Equal(24_000, rig.Session.ResumeFrames);

        // Quiet for 30 s: the cushion halves, once per 30 s, down to the 100 ms floor.
        Volatile.Write(ref now, 31_000);
        rig.Step(); rig.Step();
        Assert.Equal(12_000, rig.Session.ResumeFrames);
        rig.Step();
        Assert.Equal(12_000, rig.Session.ResumeFrames);      // not again until another 30 s have passed
        Volatile.Write(ref now, 62_000);
        rig.Step();
        Assert.Equal(6_000, rig.Session.ResumeFrames);
        Volatile.Write(ref now, 93_000);
        rig.Step();
        Assert.Equal(4_800, rig.Session.ResumeFrames);
        Volatile.Write(ref now, 124_000);
        rig.Step();
        Assert.Equal(4_800, rig.Session.ResumeFrames);
    }

    [Fact]
    public void ASeekRebuffer_IsNotAnIncident_AndNeverGrowsTheCushion()
    {
        using var rig = new Rig(availableFrames: Content);
        for (int i = 0; i < 30; i++) rig.Step(pump: i == 0);
        Assert.True(rig.Session.IsStarved);
        int cushion = rig.Session.ResumeFrames;

        rig.Session.ArmSeekRebufferSuppression();            // what SeekAsync does before it flushes the ring
        rig.Source.AvailableFrames = Content + 60_000;
        rig.Feed.WorkerPumpOnce();
        rig.Feed.FeedOnce();                                  // the resume happens inside this block, before any tick can clear the flag

        Assert.False(rig.Session.IsStarved);
        Assert.Equal(cushion, rig.Session.ResumeFrames);
    }

    [Fact]
    public void GrowAhead_FiresOncePerIncident_NotPerSilentBlock()
    {
        // The feed's FeedOnce grows the ring's decode-ahead target on the incident EDGE (WP 1a): one doubling per starve.
        using var rig = new Rig(availableFrames: Content, ringFrames: 96_000, aheadFrames: 24_000);
        var ring = rig.Feed.RingsSnapshot[0].Ring;
        int before = ring.TargetFrames;
        for (int i = 0; i < 40; i++) rig.Step(pump: i == 0);

        Assert.True(rig.Session.IsStarved);
        Assert.Equal(before * 2, ring.TargetFrames);
    }

    [Fact]
    public async Task Pause_WhileStarved_DrainsAndStopsTheDeviceExactlyOnce()
    {
        using var rig = new Rig(availableFrames: Content);
        for (int i = 0; i < 30; i++) rig.Step(pump: i == 0);
        Assert.True(rig.Session.IsStarved);

        Task pause = rig.Session.PauseAsync().AsTask();
        for (int i = 0; i < 400 && !pause.IsCompleted; i++)
        {
            rig.Step(pump: false);
            await Task.Delay(1);
        }
        await pause.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackState.Paused, rig.Session.CurrentState);
        Assert.Equal(1, rig.Endpoint.StopCount);              // a pause stops the device once; a starve alone never does
        Assert.False(rig.Endpoint.IsStarted);
        Assert.Equal(3, rig.Session.TransportPhase);
    }
}
