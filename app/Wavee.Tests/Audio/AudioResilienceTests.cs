using System;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class PlaybackErrorPathTests
{
    [Fact]
    public async Task LocalPlay_ResolveFailure_SurfacesTypedError_NotSilentDrop()
    {
        var host = new SilentAudioHost();
        var proj = new NowPlayingProjection("dev");
        var resolver = new ThrowingResolver(AudioKeyFailureReason.License403);
        var controller = new PlaybackController(host, resolver, proj, EmptyContextResolver.Instance, "dev");
        PlaybackErrorInfo? surfaced = null;
        controller.OnPlaybackError = m => surfaced = m;

        await controller.PlayTrackAsync("spotify:track:abc");

        Assert.Equal(1, resolver.Calls);
        Assert.Equal(AudioKeyFailureReason.License403, surfaced?.Reason);
        Assert.Equal(AudioKeyFailureReason.License403.ToUserMessage(), surfaced?.UserMessage);
        controller.Dispose();
    }

    [Fact]
    public async Task RetryCurrent_ReResolves()
    {
        var host = new SilentAudioHost();
        var proj = new NowPlayingProjection("dev");
        var resolver = new ThrowingResolver(AudioKeyFailureReason.Network);
        var controller = new PlaybackController(host, resolver, proj, EmptyContextResolver.Instance, "dev");
        int errors = 0;
        controller.OnPlaybackError = _ => errors++;

        await controller.PlayTrackAsync("spotify:track:abc");
        await controller.RetryCurrentAsync();

        Assert.Equal(2, resolver.Calls);
        Assert.Equal(2, errors);
        controller.Dispose();
    }
}

public class ProjectionPrebufferingTests
{
    [Fact]
    public void Prebuffering_ReadsAsBuffering_ThenClearsOnPlaying()
    {
        var proj = new NowPlayingProjection("dev");

        proj.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
        Assert.True(proj.IsBuffering);
        Assert.True(proj.IsPrebuffering);

        proj.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Playing, 100));
        Assert.False(proj.IsBuffering);
        Assert.True(proj.IsPlaying);
        proj.Dispose();
    }
}

public class StatusServiceTests
{
    [Fact]
    public void KeyFailure_SetsIssue_FiresChanged_ThenClears()
    {
        var s = new AudioRuntimeStatusService();
        int changed = 0;
        s.Changed += () => changed++;

        Assert.False(s.HasIssue);
        s.SetKeyFailure(AudioKeyFailureReason.Network, "x");
        Assert.True(s.HasIssue);
        Assert.Equal(AudioKeyFailureReason.Network, s.Reason);

        s.ClearKeyFailure();
        Assert.False(s.HasIssue);
        Assert.True(changed >= 2);
    }
}

public class KeyValidationTests
{
    [Fact]
    public void ValidateKeyOnBodyRange_TrueForRightKey_FalseForWrong()
    {
        var key = A.Key16(1);
        var plain = new byte[0xc0];
        "OggS"u8.CopyTo(plain.AsSpan(SpotifyAesCtr.OggMagicOffset));
        var cipher = SpotifyAesCtr.Decrypt(plain, key, 0);

        Assert.True(SpotifyAesCtr.ValidateKeyOnBodyRange(cipher, key));
        Assert.False(SpotifyAesCtr.ValidateKeyOnBodyRange(cipher, A.Key16(2)));
    }
}
