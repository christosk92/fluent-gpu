using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Wavee.Module.YouTube;
using Wavee.Sdk;
using Wavee.Tests.Modules.Fixtures;
using Xunit;

namespace Wavee.Tests.Modules;

/// <summary>
/// The YouTube module, driven through <see cref="ModuleTestHost"/> over a scripted transport. Nothing here touches
/// the network: every InnerTube response, channel page and HLS master is a fixture.
/// </summary>
public class YouTubeModuleTests
{
    private const string Id = YouTubeFixtures.VideoId;

    private static (ModuleTestHost Host, ScriptedHttpHandler Http) Make(ScriptedHttpHandler http)
        => (new ModuleTestHost(new YouTubeModule(http), TestDataDir()), http);

    /// <summary>A data dir that exists but holds no clients.json, so the built-in table is what runs.</summary>
    private static string TestDataDir() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-yt-tests-empty");

    // ---- match -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=tRsQsTMvPNg")]
    [InlineData("https://www.youtube.com/watch?v=tRsQsTMvPNg&t=42s")]
    [InlineData("https://m.youtube.com/watch?v=tRsQsTMvPNg")]
    [InlineData("https://music.youtube.com/watch?v=tRsQsTMvPNg&list=RDAMVM")]
    [InlineData("https://youtu.be/tRsQsTMvPNg")]
    [InlineData("https://youtu.be/tRsQsTMvPNg?t=10")]
    [InlineData("https://www.youtube.com/live/tRsQsTMvPNg")]
    [InlineData("https://www.youtube.com/shorts/tRsQsTMvPNg")]
    [InlineData("https://www.youtube.com/embed/tRsQsTMvPNg")]
    [InlineData("https://www.youtube.com/v/tRsQsTMvPNg")]
    [InlineData("https://www.youtube-nocookie.com/embed/tRsQsTMvPNg")]
    [InlineData("youtube.com/watch?v=tRsQsTMvPNg")]
    [InlineData("tRsQsTMvPNg")]
    public async Task Match_AcceptsEveryVideoUrlForm(string input)
    {
        (ModuleTestHost host, _) = Make(new ScriptedHttpHandler());

        MatchResult? match = await host.MatchAsync(input, TestContext.Current.CancellationToken);

        Assert.NotNull(match);
        Assert.Equal(Id, match.PlayableId);
        Assert.Equal(MediaForm.Video, match.Form);
    }

    [Theory]
    [InlineData("https://www.twitch.tv/somebody")]
    [InlineData("https://example.org/stream.mp3")]
    [InlineData("https://www.youtube.com/playlist?list=PL1234")]
    [InlineData("https://www.youtube.com/embed/videoseries?list=PL1234")]
    [InlineData("")]
    [InlineData("not a link")]
    public async Task Match_DeclinesEverythingElse(string input)
    {
        (ModuleTestHost host, _) = Make(new ScriptedHttpHandler());

        Assert.Null(await host.MatchAsync(input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Match_ChannelLivePage_ReadsCurrentVideoEndpointFirst()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("/live", HttpStatusCode.OK, YouTubeFixtures.ChannelLiveHtmlWithEndpoint, "text/html");
        (ModuleTestHost host, _) = Make(http);

        MatchResult? match = await host.MatchAsync("https://www.youtube.com/@anthropic/live",
            TestContext.Current.CancellationToken);

        Assert.NotNull(match);
        Assert.Equal(Id, match.PlayableId);
        Assert.True(match.IsLive);
        Assert.Equal(YouTubeModule.DesktopUserAgent, http.Requests[0].Header("User-Agent"));
    }

    [Fact]
    public async Task Match_ChannelLivePage_FallsBackToCanonical()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("/live", HttpStatusCode.OK, YouTubeFixtures.ChannelLiveHtmlCanonicalOnly, "text/html");
        (ModuleTestHost host, _) = Make(http);

        MatchResult? match = await host.MatchAsync("https://www.youtube.com/channel/UCAAAAAAAAAAAAAAAAAAAAA/live",
            TestContext.Current.CancellationToken);

        Assert.Equal(Id, match!.PlayableId);
    }

    [Fact]
    public async Task Match_ChannelLivePage_OfflineIsOffline()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("/live", HttpStatusCode.OK, YouTubeFixtures.ChannelLiveHtmlOffline, "text/html");
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.MatchAsync("https://www.youtube.com/@anthropic/live", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Offline, ex.Code);
    }

    // ---- resolve: the happy path and the request shape ----------------------------------------------------------

    [Fact]
    public async Task Resolve_UsesVisionOsFirstAndStops()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        RecordedRequest[] players = PlayerCalls(http);
        Assert.Single(players);
        Assert.Contains("\"clientName\":\"VISIONOS\"", players[0].Body, StringComparison.Ordinal);
        Assert.Equal("101", players[0].Header("X-YouTube-Client-Name"));
        Assert.Equal("1.02", players[0].Header("X-YouTube-Client-Version"));
        Assert.Equal("https://www.youtube.com", players[0].Header("Origin"));

        Assert.Equal(YouTubeFixtures.HlsManifestUrl, resolved.Media.Url);
        Assert.Equal(MediaLocator.ContainerHls, resolved.Media.Container);
        Assert.Equal(MediaForm.Video, resolved.Form);
        Assert.True(resolved.IsLive);
        Assert.Equal(0, resolved.DurationMs);
        Assert.Equal("Claude FM", resolved.Title);
        Assert.Equal(new[] { "Anthropic" }, resolved.Artists);
        Assert.Equal("https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", resolved.ArtworkUrl);
    }

    [Fact]
    public async Task Resolve_RequestBodyOmitsSignatureTimestampAndParams()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        string body = PlayerCalls(http)[0].Body;
        Assert.Contains("\"contentCheckOk\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"racyCheckOk\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"html5Preference\":\"HTML5_PREF_WANTS\"", body, StringComparison.Ordinal);
        Assert.Contains("\"timeZone\":\"UTC\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("signatureTimestamp", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"params\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("serviceIntegrityDimensions", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_AndroidBlockCarriesTheSdkVersion()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("\"clientName\":\"VISIONOS\"", HttpStatusCode.OK, YouTubeFixtures.PlayerUnplayable)
            .OnBody("\"clientName\":\"ANDROID\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk)
            .WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        RecordedRequest android = PlayerCalls(http)[1];
        Assert.Contains("\"androidSdkVersion\":30", android.Body, StringComparison.Ordinal);
        Assert.Equal("3", android.Header("X-YouTube-Client-Name"));
        Assert.Equal("com.google.android.youtube/21.26.364 (Linux; U; Android 11) gzip",
            android.Header("User-Agent"));
    }

    [Fact]
    public async Task Resolve_VodKeepsItsDuration()
    {
        var http = Player(YouTubeFixtures.PlayerVodOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.False(resolved.IsLive);
        Assert.Equal(3_672_000, resolved.DurationMs);
    }

    // ---- resolve: the fallback table ----------------------------------------------------------------------------

    [Fact]
    public async Task Resolve_VideoIdMismatchAdvancesToTheNextClient()
    {
        var http = new ScriptedHttpHandler()
            .OnBody("\"clientName\":\"VISIONOS\"", HttpStatusCode.OK, YouTubeFixtures.PlayerVideoIdMismatch)
            .OnBody("\"clientName\":\"ANDROID\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk)
            .WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, PlayerCalls(http).Length);
        Assert.Equal(Id, resolved.PlayableId);
    }

    [Fact]
    public async Task Resolve_SabrOnlyOnEveryClientIsUnavailable()
    {
        var http = Player(YouTubeFixtures.PlayerSabrOnly);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Contains("SABR", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, PlayerCalls(http).Length);
    }

    /// <summary>The bot wall is per CLIENT (verified 2026-08-22: VISIONOS was walled for a live stream ANDROID then
    /// served from the same IP), so it is the verdict only when every configured client says it.</summary>
    [Fact]
    public async Task Resolve_BotWallOnEveryClientIsUnavailable()
    {
        var http = Player(YouTubeFixtures.PlayerBotWall);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Contains("blocking this network", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, PlayerCalls(http).Length);
    }

    [Fact]
    public async Task Resolve_BotWallOnTheFirstClientFallsThroughToTheNext()
    {
        var http = new ScriptedHttpHandler()
            .On(r => r.Url.Contains("youtubei/v1/player", StringComparison.Ordinal)
                     && r.Header("X-YouTube-Client-Name") == "101",
                _ => new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(YouTubeFixtures.PlayerBotWall,
                        System.Text.Encoding.UTF8, "application/json"),
                })
            .OnUrl("youtubei/v1/player", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk, "application/json")
            .WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.True(resolved.IsLive);
        Assert.Equal(2, PlayerCalls(http).Length);
        Assert.Equal("101", PlayerCalls(http)[0].Header("X-YouTube-Client-Name"));
        Assert.Equal("3", PlayerCalls(http)[1].Header("X-YouTube-Client-Name"));
    }

    [Fact]
    public async Task Resolve_AgeGateIsNeedsAuth()
    {
        var http = Player(YouTubeFixtures.PlayerAgeGate);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.NeedsAuth, ex.Code);
        Assert.Single(PlayerCalls(http));
    }

    [Fact]
    public async Task Resolve_LiveStreamOfflineIsOfflineWithTheStartTime()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOffline);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Offline, ex.Code);
        Assert.Contains("2026-09-01", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_UnplayableWalksTheTableThenReportsTheReasonVerbatim()
    {
        var http = Player(YouTubeFixtures.PlayerUnplayable);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Equal("This video is not available on this app.", ex.Message);
        Assert.Equal(3, PlayerCalls(http).Length);
    }

    [Fact]
    public async Task Resolve_ManifestPreflight403AdvancesToTheNextClient()
    {
        int manifestCalls = 0;
        var http = new ScriptedHttpHandler()
            .OnBody("\"clientName\":\"VISIONOS\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk)
            .OnBody("\"clientName\":\"ANDROID\"", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk);
        http.On(r => r.Url.Contains("manifest.googlevideo.com", StringComparison.Ordinal), _ =>
        {
            manifestCalls++;
            return manifestCalls == 1
                ? ScriptedHttpHandler.Respond(HttpStatusCode.Forbidden, "")
                : ScriptedHttpHandler.Respond(HttpStatusCode.OK, YouTubeFixtures.HlsMaster,
                    "application/vnd.apple.mpegurl");
        });
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, PlayerCalls(http).Length);
        Assert.Equal(2, manifestCalls);
        Assert.Equal(YouTubeFixtures.HlsManifestUrl, resolved.Media.Url);
    }

    [Fact]
    public async Task Resolve_NonPlaylistManifestBodyIsUnavailable()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk)
            .OnUrl("manifest.googlevideo.com", HttpStatusCode.OK, "<html>nope</html>", "text/html");
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync(Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        Assert.Single(PlayerCalls(http));
    }

    [Fact]
    public async Task Resolve_RejectsAnIdThatIsNotAVideoId()
    {
        (ModuleTestHost host, _) = Make(new ScriptedHttpHandler());

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(() =>
            host.ResolveAsync("not-an-id", TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.NotOwned, ex.Code);
    }

    // ---- expiry ------------------------------------------------------------------------------------------------

    [Fact]
    public void ExpiresAt_TakesTheEarlierOfTheSignedExpiryAndTheSessionLifetime()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_767_000_000);

        // The signed /expire/ is earlier than now + 21540.
        long? signedWins = YouTubeModule.ExpiresAt("https://x/expire/1767010000/playlist/index.m3u8", "21540", now);
        Assert.Equal((1_767_010_000L - 600) * 1000L, signedWins!.Value);

        // The session lifetime is earlier than the signed expiry.
        long? lifetimeWins = YouTubeModule.ExpiresAt("https://x/expire/1799999999/playlist/index.m3u8", "600", now);
        Assert.Equal((1_767_000_600L - 600) * 1000L, lifetimeWins!.Value);
    }

    [Fact]
    public void ExpiresAt_IsNullWhenYouTubeSignedNeither()
        => Assert.Null(YouTubeModule.ExpiresAt("https://x/playlist/index.m3u8", null,
            DateTimeOffset.FromUnixTimeSeconds(1_767_000_000)));

    [Fact]
    public async Task Resolve_PublishesTheExpiryTheHostReResolvesOn()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        // The fixture's manifest signs /expire/1767225600/, which is inside the 21540 s session window here.
        Assert.NotNull(resolved.ExpiresAtUnixMs);
        Assert.True(resolved.ExpiresAtUnixMs!.Value <= (1_767_225_600L - 600) * 1000L);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>Answers every client's player request with the same body.</summary>
    private static ScriptedHttpHandler Player(string playerJson)
        => new ScriptedHttpHandler().OnUrl("youtubei/v1/player", HttpStatusCode.OK, playerJson, "application/json");

    private static RecordedRequest[] PlayerCalls(ScriptedHttpHandler http)
        => http.Requests.Where(r => r.Url.Contains("youtubei/v1/player", StringComparison.Ordinal)).ToArray();
}

/// <summary>Small script builders shared by the YouTube tests.</summary>
internal static class YouTubeScriptExtensions
{
    /// <summary>Answers the manifest preflight with a valid HLS master.</summary>
    /// <param name="http">The handler to extend.</param>
    public static ScriptedHttpHandler WithManifest(this ScriptedHttpHandler http)
        => http.OnUrl("manifest.googlevideo.com", HttpStatusCode.OK, YouTubeFixtures.HlsMaster,
            "application/vnd.apple.mpegurl");
}
