using System;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>M3 cross-backend queue preparation (the plan requirement): the backend-agnostic <see cref="QueuePlaybackCoordinator"/>
/// prepares the next item on whichever backend the router resolves it to — an audio→audio next crossfades in the shared mixer;
/// an audio→video next is prepared via the cross-backend <see cref="IPreparableBackend"/> hook AHEAD of the boundary and joined
/// as a clean declicked HARD CUT (never an audio crossfade). Deterministic — pumped off the synthetic clock.</summary>
public sealed class CrossBackendQueueTests
{
    private static readonly MixFormat Fmt = new(48000, 2);
    private static byte[] Wav(double seconds) => M3TestSupport.MakeWavPcm16(48000, 2, M3TestSupport.ToneStereo(48000, seconds, 440));

    [Fact]
    public async Task CrossBackend_AudioToVideo_PreparesBeforeBoundary_HardCut_AdvancesEngines()
    {
        var router = new MediaRouter();
        router.Register(MediaKind.PcmAudio, new PcmAudioPlayer(Fmt, maxBlock: 512));
        var fakeVideo = new M3TestSupport.FakeVideoBackend();
        router.Register(MediaKind.MfVideoOrFile, fakeVideo);

        var queue = new PlayQueue();
        queue.Add(MediaSource.FromBytes(Wav(0.30)).WithKind(MediaKind.PcmAudio));      // A: audio
        queue.Add(MediaSource.FromBytes(new byte[] { 1, 2, 3 }).WithKind(MediaKind.MfVideoOrFile));   // B: video/DRM

        var core = new MediaPlayerCore();
        var coord = new QueuePlaybackCoordinator(queue, router, Fmt, new MediaSignalSink(core), effects: null, latencyMarginMs: 50);
        await coord.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(coord.AudioSession);
        _ = coord.AudioSession!.PlayAsync();

        long boundary = coord.AudioSession!.VoiceTotalFrames;   // A's natural end
        long prepareClock = -1;
        for (int i = 0; i < 400 && coord.LastOutcome != TransitionOutcome.HardCut; i++)
        {
            coord.Pump(512);
            await coord.DrainPrepareAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));   // deterministic submit
            if (fakeVideo.PrepareCount >= 1 && prepareClock < 0) prepareClock = coord.SampleClock;
        }

        Assert.True(fakeVideo.PrepareCount >= 1, "the video backend's prepare hook was invoked");
        Assert.True(prepareClock >= 0 && prepareClock < boundary, $"prepare fired BEFORE the boundary ({prepareClock} < {boundary})");
        Assert.Equal(TransitionOutcome.HardCut, coord.LastOutcome);   // a cross-backend join is a hard cut, never a crossfade

        await coord.AdvanceIfNeededAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, queue.CurrentIndex.Peek());                   // the queue advanced A→B
        Assert.IsType<M3TestSupport.FakeVideoSession>(coord.Current); // engines swapped to the video backend
        Assert.True(fakeVideo.OpenCount >= 1);

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task SameKind_AudioToAudio_Crossfades_InTheSharedMixer()
    {
        var router = new MediaRouter();
        router.Register(MediaKind.PcmAudio, new PcmAudioPlayer(Fmt, maxBlock: 512));

        var queue = new PlayQueue();
        queue.Add(MediaSource.FromBytes(Wav(0.30)).WithKind(MediaKind.PcmAudio));
        queue.Add(MediaSource.FromBytes(Wav(0.30)).WithKind(MediaKind.PcmAudio));

        var fx = new AudioEffects();
        fx.CrossfadeMs.Value = 100f;   // a 100 ms equal-power crossfade at the join

        var core = new MediaPlayerCore();
        var coord = new QueuePlaybackCoordinator(queue, router, Fmt, new MediaSignalSink(core), effects: fx, latencyMarginMs: 50);
        await coord.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        _ = coord.AudioSession!.PlayAsync();

        int maxVoices = 0;
        for (int i = 0; i < 400 && coord.LastOutcome != TransitionOutcome.Crossfaded; i++)
        {
            coord.Pump(512);
            await coord.DrainPrepareAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            maxVoices = Math.Max(maxVoices, coord.AudioSession!.MixerRef.VoiceCount);
        }

        Assert.Equal(TransitionOutcome.Crossfaded, coord.LastOutcome);   // two live audio voices overlapped
        Assert.True(maxVoices >= 2, "both voices were live in the shared mixer during the crossfade");

        await coord.DisposeAsync();
    }
}
