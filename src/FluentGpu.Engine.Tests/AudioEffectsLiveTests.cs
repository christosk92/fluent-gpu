using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>M3 §7.10: the REAL <see cref="IAudioEffects"/> driving the M2 graph — an EQ gain-only tweak RAMPS (no zipper, no
/// republish); a freq/Q change RE-PUBLISHES the graph (new coefficients live after the RenderInFlightDepth+1 quarantine);
/// NormMode/ReferenceLufs select the per-voice ReplayGain scalar; the Visualizer Tap publishes frames. Deterministic.</summary>
public sealed class AudioEffectsLiveTests
{
    private static (PcmAudioSession session, AudioEffects fx, HeadlessAudioEndpoint ep) Build(bool eq = true, NormMode norm = NormMode.Album)
    {
        var fmt = new MixFormat(48000, 2);
        var ep = new HeadlessAudioEndpoint(fmt, captureFrames: 8192);
        var fx = new AudioEffects();
        if (eq) fx.Equalizer.Apply(EqPreset.FiveBand());
        var session = new PcmAudioSession(fmt, ep.Sink, ep.Clock, maxBlock: 512, driveWithOwnThread: false);
        session.Configure(PcmAudioPlayer.BuildGraphSpec(fx, fmt));
        var voice = new SignalGeneratorSource(2, 48000, 440, 0.3f, 48000);
        session.SetVoice(voice, TimeSpan.FromSeconds(1), 48000, norm, -14f, initialVolume: 1f);
        session.BindEffects(fx);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        return (session, fx, ep);
    }

    [Fact]
    public void EqGainOnlyChange_Ramps_NoRepublish()
    {
        var (session, fx, _) = Build(eq: true);
        Assert.NotNull(session.PrimaryVoiceEq);
        int pendingBefore = session.Graph.PendingRetire;

        fx.Equalizer.Bands[2].GainDb.Value = 9f;   // gain-only tweak
        session.ReconcileEffects();

        Assert.True(session.PrimaryVoiceEq!.IsRamping);          // it RAMPS (cross-ramp), never a step
        Assert.Equal(pendingBefore, session.Graph.PendingRetire); // no topology republish for a gain change
    }

    [Fact]
    public void EqFreqChange_RepublishesGraph_NewCoeffsLiveAfterQuarantine()
    {
        var (session, fx, _) = Build(eq: true);
        int retiredBefore = session.Graph.RetiredCount;

        fx.Equalizer.Bands[2].FreqHz.Value = 3000f;   // a freq change → recompute coefficients OFF-block + republish
        session.ReconcileEffects();
        Assert.True(session.Graph.PendingRetire >= 1);

        for (int i = 0; i < 4; i++) session.RenderBlock(512);   // consume steps → old graph retires past the quarantine
        Assert.True(session.Graph.RetiredCount > retiredBefore);
    }

    [Fact]
    public void Normalization_And_ReferenceLufs_SelectThePerVoiceScalar()
    {
        var (session, fx, _) = Build(eq: false, norm: NormMode.Album);

        fx.Normalization.Value = NormMode.Off;
        session.ReconcileEffects();
        Assert.Equal(1f, session.PrimaryVoiceReplayGainScalar, 4);   // Off → unity

        fx.Normalization.Value = NormMode.Track;
        fx.ReferenceLufs.Value = -14f;
        session.ReconcileEffects();
        // 0 dB track gain retargeted from the tag reference (-18) to -14 = +4 dB.
        Assert.Equal(MathF.Pow(10f, 4f / 20f), session.PrimaryVoiceReplayGainScalar, 4);
    }

    [Fact]
    public void VisualizerTap_PublishesFrames_OffTheBlockPath()
    {
        var (session, fx, _) = Build(eq: false);
        Assert.Equal(0f, fx.Visualizer.Peek().Peak);   // silence before playback

        _ = session.PlayAsync();
        for (int i = 0; i < 8; i++) session.PumpAudio(512);   // Opening→Buffering→Ready→Playing→(render)

        var frame = fx.Visualizer.Peek();
        Assert.True(frame.Peak > 0f, "the tap published a non-zero peak");
        Assert.True(frame.Rms > 0f, "the tap published a non-zero RMS");
    }

    [Fact]
    public void Balance_Ramps_WithoutRepublishingTheGraph()
    {
        var (session, fx, _) = Build(eq: false);
        var liveBefore = session.Graph.Live;

        fx.Balance.Value = 0.5f;
        session.ReconcileEffects();
        _ = session.PlayAsync();
        for (int i = 0; i < 6; i++) session.PumpAudio(512);

        Assert.Same(liveBefore, session.Graph.Live);   // balance is a smoothed param, never swaps the published graph
    }
}
