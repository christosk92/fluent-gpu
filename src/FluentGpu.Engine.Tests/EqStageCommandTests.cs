using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// H-4 (playback smoothness, V-PE25/V-PE31): the EQ is no longer mutated from the clock thread. Coefficients are designed off the
/// RT, travel as ONE mixer command (<c>CmdSetEq</c>) and are adopted by the stage's fixed 16-band storage at a block boundary;
/// every voice chain is <c>[GainStage, EqStage]</c> so enabling the EQ ramps in from identity and the preamp (−max(0, largest
/// band gain) dB) rides the voice's gain slot. Deterministic: a headless endpoint, no wall clock.
/// </summary>
public sealed class EqStageCommandTests
{
    private static readonly MixFormat Fmt = new(48000, 2);
    private static readonly float[] Freqs = { 31f, 125f, 500f, 2000f, 8000f, 16000f, 63f, 250f, 1000f, 4000f, 12000f, 20f, 40f, 80f, 160f, 315f };

    private static BiquadBand[] Bands(params float[] gainsDb)
    {
        var bands = new BiquadBand[gainsDb.Length];
        for (int i = 0; i < bands.Length; i++) bands[i] = new BiquadBand(BiquadType.Peaking, Freqs[i % Freqs.Length], 1f, gainsDb[i]);
        return bands;
    }

    private static float[] Flat(int n, float gainDb) { var g = new float[n]; Array.Fill(g, gainDb); return g; }

    /// <summary>A session with its EQ bound to a real <see cref="AudioEffects"/>; with <paramref name="withFeed"/> the mixer-command
    /// queue is drained only by the RT's next block (<c>FeedOnce</c>), without one it is drained inline at enqueue.</summary>
    private static (PcmAudioSession session, AudioEffects fx, AudioFeedThread? feed) Build(bool eq, bool withFeed)
    {
        var endpoint = new HeadlessAudioEndpoint(Fmt);
        var fx = new AudioEffects();
        // The voice below is baked under NormMode.Off, and production opens every voice under the effects surface's own mode
        // (PcmAudioPlayer.ResolveNorm). Keep the two in agreement: AudioEffects defaults to Album, and the first reconcile would
        // otherwise rebase the voice's normalization (+4 dB for an untagged source at −14 LUFS) into the gain slot the preamp rides.
        fx.Normalization.Value = NormMode.Off;
        if (eq) fx.Equalizer.Apply(EqPreset.FiveBand());
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        var feed = withFeed ? new AudioFeedThread(session, blockFrames: 512, ringFrames: 48_000, targetAheadFrames: 24_000) : null;   // attaches itself
        session.Configure(PcmAudioPlayer.BuildGraphSpec(fx, Fmt));
        session.SetVoice(new SignalGeneratorSource(2, Fmt.SampleRate, 440, 0.3f, -1), TimeSpan.FromSeconds(60), 60 * 48_000L, NormMode.Off, -14f, initialVolume: 1f);
        session.BindEffects(fx);
        return (session, fx, feed);
    }

    [Fact]
    public void ChangesLandOnlyThroughCmdSetEq_AtABlockBoundary()
    {
        var (session, fx, feed) = Build(eq: true, withFeed: true);
        using var feedGuard = feed;
        var stage = session.PrimaryVoiceEq!;
        Assert.Equal(5, stage.ActiveBandCount);                     // built from the published spec (flat)

        fx.Equalizer.Bands[2].GainDb.Value = 9f;
        session.ReconcileEffects();                                  // designs + enqueues; the RT has not run a block yet

        Assert.False(stage.IsRamping);                               // the control thread never touches the cascade
        feed!.FeedOnce();                                            // the RT's block boundary: drains ReplacePrimary, then SetEq

        Assert.True(stage.IsRamping);
        Assert.Equal(5, stage.BandCount);
        _ = session.DisposeAsync();
    }

    [Fact]
    public void AMidRampAdopt_CommitsThePendingCascadeFirst()
    {
        var plane = new ParamPlane();
        var stage = new EqStage(2, declickSamples: 256);
        var first = Bands(0f, 6f, 0f);
        var firstCoeffs = EqStage.Design(first, 48_000);
        stage.AdoptPending(first, firstCoeffs);
        Assert.True(stage.IsRamping);
        Assert.Equal(0, stage.ActiveBandCount);                      // ramping IN from identity
        Assert.Equal(3, stage.BandCount);

        var src = new float[100 * 2]; Array.Fill(src, 0.5f);
        var dst = new float[100 * 2];
        stage.Process(src, dst, 100, new BlockCtx(0, 48_000, 2, plane));   // part-way through the 256-sample ramp
        Assert.True(stage.IsRamping);
        Assert.Equal(0, stage.ActiveBandCount);

        var second = Bands(3f, 6f, 0f);
        stage.AdoptPending(second, EqStage.Design(second, 48_000));

        Assert.Equal(3, stage.ActiveBandCount);                      // the in-flight cascade was COMMITTED …
        Assert.Equal(firstCoeffs[1], stage.ActiveCoeffs(1));
        Assert.True(stage.IsRamping);                                // … and a fresh ramp towards the new set began
    }

    [Fact]
    public void ALiveStageNeverReallocates_AcrossTopologyChanges()
    {
        var plane = new ParamPlane();
        var stage = new EqStage(2);
        var sets = new[] { Bands(Flat(16, 3f)), Bands(Flat(16, -3f)), Bands(Flat(7, 6f)), Array.Empty<BiquadBand>() };   // 16 bands (the capacity), 16, 7, none
        var coeffs = new BiquadCoeffs[sets.Length][];
        for (int i = 0; i < sets.Length; i++) coeffs[i] = EqStage.Design(sets[i], 48_000);
        var src = new float[256 * 2]; Array.Fill(src, 0.25f);
        var dst = new float[256 * 2];

        for (int i = 0; i < sets.Length * 2; i++)                    // warm every path (the first calls JIT)
        {
            stage.AdoptPending(sets[i % sets.Length], coeffs[i % sets.Length]);
            stage.Process(src, dst, 256, new BlockCtx(0, 48_000, 2, plane));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 40; i++)
        {
            stage.AdoptPending(sets[i % sets.Length], coeffs[i % sets.Length]);
            stage.Process(src, dst, 256, new BlockCtx(0, 48_000, 2, plane));
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, stage.BandCount);                            // the last set was the empty one: the EQ ramped OUT
        Assert.Equal(EqStage.MaxBands, 16);
        Assert.Equal(EqStage.MaxBands, EqStage.Design(Bands(Flat(20, 1f)), 48_000).Length);   // a longer set is truncated to the capacity
    }

    [Fact]
    public void EnablingTheEq_OnAVoiceBuiltWithItOff_RampsInFromIdentity()
    {
        var (session, fx, _) = Build(eq: false, withFeed: false);
        var stage = session.PrimaryVoiceEq;
        Assert.NotNull(stage);                                       // the chain ALWAYS has an EQ stage
        Assert.Equal(0, stage!.BandCount);

        fx.Equalizer.Apply(EqPreset.FiveBand());                     // enabled + five bands → a topology change
        fx.Equalizer.Bands[2].GainDb.Value = 9f;
        session.ReconcileEffects();                                  // no feed: the command is applied inline

        Assert.True(stage.IsRamping);
        Assert.Equal(0, stage.ActiveBandCount);                      // still audibly identity at the moment of the edit …
        Assert.Equal(5, stage.BandCount);                            // … converging on the five-band cascade
        session.RenderBlock(512);                                    // 512 frames > the 256-sample declick window
        Assert.False(stage.IsRamping);
        Assert.Equal(5, stage.ActiveBandCount);
        _ = session.DisposeAsync();
    }

    [Fact]
    public void AnEnqueueTheQueueRefuses_IsRetriedOnTheNextTick()
    {
        var (session, fx, feed) = Build(eq: true, withFeed: true);
        using var feedGuard = feed;
        while (session.SetVoiceEnvelope(1, GainEnvelope.Constant)) { }   // fill the (non-priority) command queue

        fx.Equalizer.Bands[1].GainDb.Value = 6f;
        session.ReconcileEffects();
        Assert.True(session.EqCommandPending);                       // refused: stays dirty, nothing was lost

        feed!.FeedOnce();                                            // the RT drains the queue
        session.ReconcileEffects();                                  // the next tick re-sends the freshly designed set
        Assert.False(session.EqCommandPending);
        feed.FeedOnce();
        Assert.True(session.PrimaryVoiceEq!.IsRamping);
        _ = session.DisposeAsync();
    }

    [Fact]
    public void ThePreampRecomputesOnEveryBandGainEdit_EvenAGainOnlyOne()
    {
        var (session, fx, _) = Build(eq: true, withFeed: false);
        Assert.Equal(1f, session.EqPreampLinear);                    // flat EQ → no attenuation

        fx.Equalizer.Bands[2].GainDb.Value = 9f;
        session.ReconcileEffects();
        Assert.Equal(MathF.Pow(10f, -9f / 20f), session.EqPreampLinear, 4);
        session.RenderBlock(512);                                    // the gain slot ramps (512 samples) to the preamp
        Assert.Equal(MathF.Pow(10f, -9f / 20f), session.PrimaryVoiceGainStage!.CurrentGain, 3);

        fx.Equalizer.Bands[2].GainDb.Value = 3f;                     // the largest boost is now band 0's
        fx.Equalizer.Bands[0].GainDb.Value = 6f;
        session.ReconcileEffects();
        Assert.Equal(MathF.Pow(10f, -6f / 20f), session.EqPreampLinear, 4);

        fx.Equalizer.Bands[2].GainDb.Value = -4f;                    // cuts need no preamp
        fx.Equalizer.Bands[0].GainDb.Value = 0f;
        session.ReconcileEffects();
        Assert.Equal(1f, session.EqPreampLinear);
        session.RenderBlock(512);
        Assert.Equal(1f, session.PrimaryVoiceGainStage!.CurrentGain, 3);
        _ = session.DisposeAsync();
    }

    [Fact]
    public void ANewVoice_StartsFromTheLiveDesign_NotTheStalePublishedSpec()
    {
        var (session, fx, _) = Build(eq: true, withFeed: false);
        fx.Equalizer.Bands[2].GainDb.Value = 9f;                     // gain-only: the graph spec is NOT republished
        session.ReconcileEffects();

        var chain = session.BuildVoiceChain();                       // what the queue scheduler builds for the next track

        Assert.IsType<GainStage>(chain[0]);
        var eq = Assert.IsType<EqStage>(chain[1]);
        Assert.Equal(5, eq.ActiveBandCount);
        Assert.Equal(BiquadCoeffs.Design(new BiquadBand(BiquadType.Peaking, 500f, 1f, 9f), 48_000), eq.ActiveCoeffs(2));
        Assert.Equal(MathF.Pow(10f, -9f / 20f), ((GainStage)chain[0]).CurrentGain, 4);
        _ = session.DisposeAsync();
    }

    [Fact]
    public void SetVoiceGain_RampsTheVoiceSlot_AndTracksTheNormalizationDelta()
    {
        var (session, _, _) = Build(eq: false, withFeed: false);
        long primary = session.PrimaryVoiceIdValue;

        Assert.True(session.SetVoiceGain(primary, 0.5f, rampMs: 5));
        session.RenderBlock(512);

        Assert.Equal(0.5f, session.PrimaryVoiceGainStage!.CurrentGain, 4);
        Assert.Equal(0.5f, session.PrimaryVoiceReplayGainScalar, 4);   // baked 1 × delta 0.5
        Assert.False(session.SetVoiceGain(999, 0.5f));                 // a voice the session does not know
        Assert.False(session.SetVoiceGain(primary, float.NaN));
        _ = session.DisposeAsync();
    }
}
