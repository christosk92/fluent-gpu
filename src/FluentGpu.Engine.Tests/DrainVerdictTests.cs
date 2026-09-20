using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <see cref="DrainVerdict"/> (spec §7/§12): the pure nothing-to-render/Ended decision extracted out of
/// <see cref="PcmAudioSession.RenderBlock"/> and its Playing-state Ended check, plus the behavioural regression this
/// fix closes — a drained mixer through the RT feed + a FINITE buffered sink must reach <see cref="PlaybackState.Ended"/>
/// and stop advancing the mixer clock, instead of rendering zero-filled filler into the sink forever (Wavee: playback
/// sat at the tail with the clock running past the end — docs/plans wavee "explain why playback is indexed" diagnosis).
/// </summary>
public sealed class DrainVerdictTests
{
    // ── (1) pure Decide() truth table ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LiveMixer_AlwaysAllowsRender_NeverEnds()
    {
        var v = DrainVerdict.Decide(mixerDrained: false, cmdsPending: false, pendingFrames: 0,
            writableFrames: 100, capacityFrames: 100, bufferedSink: true);
        Assert.True(v.RenderAllowed);
        Assert.False(v.PublishEnded);
    }

    [Fact]
    public void DrainedMixer_NoCommandsPending_NoPendingFrames_SinkFullyEmpty_EndsAndBlocksRender()
    {
        var v = DrainVerdict.Decide(mixerDrained: true, cmdsPending: false, pendingFrames: 0,
            writableFrames: 100, capacityFrames: 100, bufferedSink: true);
        Assert.False(v.RenderAllowed);   // the fix: no filler once the mixer has nothing left
        Assert.True(v.PublishEnded);
    }

    [Fact]
    public void DrainedMixer_ButSinkStillDraining_BlocksRender_ButIsNotEndedYet()
    {
        // The buffered sink still holds unplayed filler from before the mixer drained — Ended must wait for it to
        // genuinely empty, but there is still nothing new to render in the meantime.
        var v = DrainVerdict.Decide(mixerDrained: true, cmdsPending: false, pendingFrames: 0,
            writableFrames: 50, capacityFrames: 100, bufferedSink: true);
        Assert.False(v.RenderAllowed);
        Assert.False(v.PublishEnded);
    }

    [Fact]
    public void DrainedMixer_WithCommandStillQueued_StillAllowsRender_NeverEndsEarly()
    {
        // A voice-add command raced the drained check (spec §12): a queued arrival must never be mistaken for the end,
        // on either RenderBlock (must still render once the command drains) or the Ended check.
        var v = DrainVerdict.Decide(mixerDrained: true, cmdsPending: true, pendingFrames: 0,
            writableFrames: 100, capacityFrames: 100, bufferedSink: true);
        Assert.True(v.RenderAllowed);
        Assert.False(v.PublishEnded);
    }

    [Fact]
    public void DrainedMixer_WithUnsubmittedPendingFrames_NotEndedYet()
    {
        var v = DrainVerdict.Decide(mixerDrained: true, cmdsPending: false, pendingFrames: 4,
            writableFrames: 100, capacityFrames: 100, bufferedSink: true);
        Assert.False(v.RenderAllowed);
        Assert.False(v.PublishEnded);
    }

    [Fact]
    public void DrainedMixer_NoBufferedSink_EndsAsSoonAsMixerAndCommandsClear()
    {
        // A synchronous/headless sink has no drain lag to wait out — nothing gates Ended but the mixer/commands.
        var v = DrainVerdict.Decide(mixerDrained: true, cmdsPending: false, pendingFrames: 0,
            writableFrames: 0, capacityFrames: 0, bufferedSink: false);
        Assert.False(v.RenderAllowed);
        Assert.True(v.PublishEnded);
    }

    // ── (2) behavioural: RT feed + finite buffered sink — a drained mixer reaches Ended, clock stops ─────────────────

    [Fact]
    public void DrainedMixer_ThroughRtFeedAndBufferedSink_ReachesEnded_AndMixerClockStops()
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new BufferedAudioEndpoint(format, capacityFrames: 64, captureFrames: 4096);
        var session = new PcmAudioSession(format, endpoint, endpoint, maxBlock: 32, driveWithOwnThread: false);
        using var feed = new AudioFeedThread(session, blockFrames: 32, ringFrames: 256, targetAheadFrames: 64);
        session.Configure(AudioGraphSpec.Passthrough);

        // A short, finite track (96 frames, 2ms) — it exhausts almost immediately so the fix's effect (no more filler
        // once it does) shows up within a handful of ticks.
        const int totalFrames = 96;
        session.SetVoice(new MemoryAudioSource(new float[totalFrames * 2], 2),
            TimeSpan.FromSeconds((double)totalFrames / format.SampleRate), totalFrames, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();

        void Step()
        {
            feed.WorkerPumpOnce();
            session.TickControl(32);
            feed.FeedOnce();
            endpoint.AdvanceHardware(32);
        }

        for (int i = 0; i < 200 && session.CurrentState != PlaybackState.Ended; i++) Step();
        Assert.Equal(PlaybackState.Ended, session.CurrentState);

        // Previously: RenderBlock kept mixing/submitting zero-filled blocks forever once the mixer drained, so the
        // buffered sink was never empty and Ended never fired at all (the reported bug — the mixer clock ran on past
        // the end indefinitely). Now: once Ended, nothing further is rendered — the mixer's consumed-frame clock and
        // submitted-frame count both go quiet.
        long clockAtEnded = session.SampleClock;
        long submittedAtEnded = session.SubmittedFrames;
        for (int i = 0; i < 50; i++) Step();
        Assert.Equal(clockAtEnded, session.SampleClock);
        Assert.Equal(submittedAtEnded, session.SubmittedFrames);

        _ = session.DisposeAsync();
    }
}
