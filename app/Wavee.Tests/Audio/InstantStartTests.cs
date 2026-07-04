using System;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class InstantStartTests
{
    [Fact]
    public async Task Play_StartsOnHead_BeforeBodyResolves_ThenSuppliesBody()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("dev");
        var bodyTcs = new TaskCompletionSource<AudioStreamHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new AudioFastStart("spotify:track:x", "fid", AudioFormat.OggVorbis320, 1000, 0f, new byte[10]);
        var fast = new FakeFastResolver(new FastStartPlan(start, bodyTcs.Task));
        var controller = new PlaybackController(host, new StubTrackResolver(), proj, EmptyContextResolver.Instance, "dev", fast: fast);

        await controller.PlayTrackAsync("spotify:track:x");

        // Head + play happen immediately; the body has NOT resolved yet → SupplyBody not called.
        Assert.True(host.LoadFastStartCalled);
        Assert.True(host.PlayCalled);
        Assert.False(host.SupplyBodyCalled);
        Assert.Equal(1, fast.Calls);

        // The parallel body lands → the controller supplies it to the host.
        bodyTcs.SetResult(new AudioStreamHandle("spotify:track:x", "fid", "https://cdn", new byte[16], AudioFormat.OggVorbis320, 1000, 0f, new[] { "https://cdn" }, 10));
        await host.SupplyBodySignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.SupplyBodyCalled);

        controller.Dispose();
    }

    [Fact]
    public async Task Play_FastResolveFailure_SurfacesError_NoStart()
    {
        var host = new RecordingAudioHost();
        var proj = new NowPlayingProjection("dev");
        var fast = new ThrowingFastResolver(AudioKeyFailureReason.RotationDrift);
        var controller = new PlaybackController(host, new StubTrackResolver(), proj, EmptyContextResolver.Instance, "dev", fast: fast);
        PlaybackErrorInfo? err = null;
        controller.OnPlaybackError = m => err = m;

        await controller.PlayTrackAsync("spotify:track:x");

        Assert.False(host.LoadFastStartCalled);
        Assert.Equal(AudioKeyFailureReason.RotationDrift, err?.Reason);
        Assert.Equal(AudioKeyFailureReason.RotationDrift.ToUserMessage(), err?.UserMessage);
        controller.Dispose();
    }
}

sealed class ThrowingFastResolver : IFastTrackResolver
{
    readonly AudioKeyFailureReason _reason;
    public ThrowingFastResolver(AudioKeyFailureReason reason) => _reason = reason;
    public Task<FastStartPlan> ResolveFastAsync(Wavee.Core.Track track, System.Threading.CancellationToken ct = default)
        => throw new AudioPlaybackException(_reason, "meta failed");
}
