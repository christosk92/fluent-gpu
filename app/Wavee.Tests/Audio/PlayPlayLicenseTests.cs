using System;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Protocol.Playplay;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class PlayPlayLicenseTests
{
    [Fact]
    public void PlayPlayKey_UsesFormUrlEncodedContentType()
    {
        Assert.Equal("application/x-www-form-urlencoded", SpotifyHeaders.PlayPlayKey()["Content-Type"]);
    }

    [Fact]
    public void BuildRequestBody_TimestampIsUnixSeconds_NotMilliseconds()
    {
        var cfg = A.Pack(Convert.ToHexString(new byte[32])).ToConfig();
        const long ts = 1_783_178_797L;
        var body = PlayPlayLicenseClient.BuildRequestBody(cfg, ts);
        var parsed = PlayPlayLicenseRequest.Parser.ParseFrom(body);

        Assert.Equal(ts, parsed.Timestamp);
        Assert.True(body.Length <= 30, $"expected ~30-byte body, got {body.Length}");
        Assert.NotEqual(DateTimeOffset.FromUnixTimeSeconds(ts).ToUnixTimeMilliseconds(), parsed.Timestamp);
    }

    [Fact]
    public void BuildRequestBody_UsesHardcodedPlayPlayToken()
    {
        var cfg = A.Pack(Convert.ToHexString(new byte[32])).ToConfig();
        Assert.NotEqual(SpotifyRuntimeIdentity.DefaultPlayPlayToken, cfg.PlayPlayToken);
        var parsed = PlayPlayLicenseRequest.Parser.ParseFrom(PlayPlayLicenseClient.BuildRequestBody(cfg));
        Assert.Equal(SpotifyRuntimeIdentity.DefaultPlayPlayToken, parsed.Token.ToByteArray());
    }

    [Fact]
    public void SpotifyHeaders_AppVersion_IsNineDigits()
    {
        Assert.Equal(9, SpotifyHeaders.AppVersion.Length);
        Assert.Equal("129300667", SpotifyHeaders.AppVersion);
    }

    [Fact]
    public void UserAgent_IncludesRealOsDescriptor_NotPcDesktopPlaceholder()
    {
        var ua = SpotifyRuntimeIdentity.Default.UserAgent;
        Assert.DoesNotContain("(PC desktop)", ua);
        Assert.Contains("Windows", ua);
    }
}
