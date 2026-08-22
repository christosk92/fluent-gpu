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

    // ---- pages -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Page_Video_IsAWatchDocumentWithTheHeroFactsDescriptionAndActions()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(ModulePageDoc.TemplateWatch, page!.Template);
        Assert.Equal("Claude FM", page.Hero!.Title);
        Assert.Equal("Anthropic", page.Hero.Subtitle);
        Assert.True(page.Hero.IsLive);
        Assert.Equal("https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", page.Hero.ImageUrl);

        PageAction play = page.Actions.Single(a => a.Kind == PageAction.KindPlay);
        Assert.Equal(Id, play.PlayableId);
        Assert.True(play.Primary);

        PageAction open = page.Actions.Single(a => a.Kind == PageAction.KindOpenUrl);
        Assert.Equal("https://www.youtube.com/watch?v=" + Id, open.Url);
        Assert.Equal("Open on YouTube", open.Label);

        // The facts and description sections are untouched by the watch layout: the new template folds them into its
        // description card, and an app that does not know "watch" still renders exactly this document the old way.
        PageSection facts = page.Sections.Single(x => x.Kind == PageSection.KindFacts);
        Assert.Contains(facts.Rows!, r => r[0] == "Views" && r[1] == "1,234");

        PageSection text = page.Sections.Single(x => x.Kind == PageSection.KindText);
        Assert.Contains("continuous broadcast", text.Text!, StringComparison.Ordinal);

        // The one-card channel shelf stays as the fallback for an app that does not read Hero.SubtitleEntityId.
        PageItem channel = page.Sections.Single(x => x.Kind == PageSection.KindCards).Items!.Single();
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId, channel.EntityId);
    }

    [Fact]
    public async Task Page_Video_CarriesTheOwnerAvatarAndTheSubtitleEntityId()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        // The avatar exists nowhere in the player response — it is the whole reason /next is called.
        Assert.Equal(YouTubeFixtures.ChannelAvatarUrl, page!.Hero!.AvatarUrl);
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId, page.Hero.SubtitleEntityId);
        Assert.Equal("https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", page.Hero.ImageUrl);
    }

    [Fact]
    public async Task Page_Video_LivePrefersTheWatchingCountOverLifetimeViews()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        string meta = page!.Hero!.MetaLine!;
        Assert.Contains("Live now", meta, StringComparison.Ordinal);
        Assert.Contains("12,345 watching now", meta, StringComparison.Ordinal);
        Assert.Contains("Started streaming 3 hours ago", meta, StringComparison.Ordinal);
        // videoDetails.viewCount on a broadcast is a lifetime total; printing it beside a LIVE badge would be a lie.
        Assert.DoesNotContain("1,234 views", meta, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rail as YouTube actually answers it today: <c>lockupViewModel</c> entries. A real WEB capture taken
    /// 2026-08-23 held 20 of them and zero <c>compactVideoRenderer</c>, so this is the branch that decides whether a
    /// real page has a shelf at all.
    /// </summary>
    [Fact]
    public async Task Page_Video_BuildsTheUpNextShelfFromALockupRail()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        PageSection shelf = page!.Sections.Single(x => x.Kind == PageSection.KindPlayables);
        Assert.Equal("Up next", shelf.Title);

        // Four rail entries in, two cards out: the playlist lockup (its contentId is a list id, not a video) and the
        // continuation are both skipped.
        Assert.Equal(2, shelf.Items!.Length);

        PageItem vod = shelf.Items[0];
        Assert.Equal("A recorded talk", vod.Title);
        Assert.Equal("Anthropic", vod.Subtitle);                       // metadata row 0
        Assert.Equal(YouTubeFixtures.RelatedVodId, vod.PlayableId);     // contentId
        Assert.Equal("video:" + YouTubeFixtures.RelatedVodId, vod.EntityId);
        Assert.Equal(MediaForm.Video, vod.Form);
        Assert.False(vod.IsLive);
        Assert.Equal("1:01:12", vod.Meta);                              // the thumbnail overlay badge
        Assert.Equal(YouTubeFixtures.RelatedVodThumbnailUrl, vod.ImageUrl);

        PageItem live = shelf.Items[1];
        Assert.Equal("Another broadcast", live.Title);
        Assert.Equal("Someone Else", live.Subtitle);
        Assert.Equal(YouTubeFixtures.RelatedLiveId, live.PlayableId);
        Assert.True(live.IsLive);
        Assert.Equal("4,200 watching", live.Meta);
        Assert.Equal(YouTubeFixtures.RelatedLiveThumbnailUrl, live.ImageUrl);
    }

    /// <summary>The same rail in the older <c>compactVideoRenderer</c> spelling. Both are in flight upstream, so the
    /// module reads whichever arrived and the two produce the same shelf.</summary>
    [Fact]
    public async Task Page_Video_FallsBackToACompactVideoRendererRail()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk, YouTubeFixtures.NextWatchCompactRail);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        PageSection shelf = page!.Sections.Single(x => x.Kind == PageSection.KindPlayables);
        Assert.Equal("Up next", shelf.Title);
        Assert.Equal(2, shelf.Items!.Length);

        PageItem vod = shelf.Items[0];
        Assert.Equal("A recorded talk", vod.Title);
        Assert.Equal("Anthropic", vod.Subtitle);
        Assert.Equal(YouTubeFixtures.RelatedVodId, vod.PlayableId);
        Assert.Equal("video:" + YouTubeFixtures.RelatedVodId, vod.EntityId);
        Assert.Equal(MediaForm.Video, vod.Form);
        Assert.False(vod.IsLive);
        Assert.Equal("1:01:12", vod.Meta);
        Assert.Equal(YouTubeFixtures.RelatedVodThumbnailUrl, vod.ImageUrl);

        PageItem live = shelf.Items[1];
        Assert.Equal(YouTubeFixtures.RelatedLiveId, live.PlayableId);
        Assert.True(live.IsLive);
        Assert.Equal("4,200 watching", live.Meta);
        Assert.Equal(YouTubeFixtures.RelatedLiveThumbnailUrl, live.ImageUrl);
    }

    [Fact]
    public async Task Page_Video_LivePageExpiresWithinTheMinute()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.NotNull(page!.ExpiresAtUnixMs);
        Assert.InRange(page.ExpiresAtUnixMs!.Value, before, before + YouTubeModule.LivePageTtlMs + 10_000);
    }

    [Fact]
    public async Task Page_Video_VodKeepsTheDefaultCacheWindow()
    {
        var http = Player(YouTubeFixtures.PlayerVodOk, YouTubeFixtures.NextWatchVod);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.Null(page!.ExpiresAtUnixMs);
        Assert.Equal("Video", page.Hero!.Eyebrow);
        // The count is not live here, so the lifetime total is the honest one to print.
        Assert.Contains("987,654 views", page.Hero.MetaLine!, StringComparison.Ordinal);
        Assert.Contains("Aug 20, 2026", page.Hero.MetaLine!, StringComparison.Ordinal);
        Assert.DoesNotContain(page.Sections, x => x.Kind == PageSection.KindPlayables);
    }

    /// <summary>
    /// The honesty guarantee: /next is enrichment, never a dependency. Whatever it does — refuse, answer garbage,
    /// answer a shape we no longer recognise, or never answer at all — the page /player served is unchanged.
    /// </summary>
    [Theory]
    [InlineData("status500")]
    [InlineData("garbage")]
    [InlineData("unknownShape")]
    [InlineData("timeout")]
    public async Task Page_Video_NextFailureCostsOnlyTheEnrichment(string failure)
    {
        var http = new ScriptedHttpHandler();
        switch (failure)
        {
            case "status500":
                http.OnUrl("youtubei/v1/next", HttpStatusCode.InternalServerError, "", "text/plain");
                break;
            case "garbage":
                http.OnUrl("youtubei/v1/next", HttpStatusCode.OK, YouTubeFixtures.NextGarbage, "application/json");
                break;
            case "unknownShape":
                http.OnUrl("youtubei/v1/next", HttpStatusCode.OK, YouTubeFixtures.NextWatchUnknownShape,
                    "application/json");
                break;
            default:
                http.On(r => r.Url.Contains("youtubei/v1/next", StringComparison.Ordinal),
                    _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                        new TimeoutException()));
                break;
        }

        http.OnUrl("youtubei/v1/player", HttpStatusCode.OK, YouTubeFixtures.PlayerLiveOk, "application/json");
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Single(NextCalls(http));
        Assert.Equal(ModulePageDoc.TemplateWatch, page!.Template);
        Assert.Equal("Claude FM", page.Hero!.Title);
        Assert.Equal("Anthropic", page.Hero.Subtitle);
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId, page.Hero.SubtitleEntityId);
        Assert.True(page.Hero.IsLive);
        Assert.Equal("https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", page.Hero.ImageUrl);

        // Everything /next would have added, and only that, is gone.
        Assert.Null(page.Hero.AvatarUrl);
        Assert.DoesNotContain("watching", page.Hero.MetaLine!, StringComparison.Ordinal);
        Assert.DoesNotContain(page.Sections, x => x.Kind == PageSection.KindPlayables);

        // The sections /player paid for are untouched.
        PageSection facts = page.Sections.Single(x => x.Kind == PageSection.KindFacts);
        Assert.Contains(facts.Rows!, r => r[0] == "Views" && r[1] == "1,234");
        Assert.Contains("continuous broadcast",
            page.Sections.Single(x => x.Kind == PageSection.KindText).Text!, StringComparison.Ordinal);
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId,
            page.Sections.Single(x => x.Kind == PageSection.KindCards).Items!.Single().EntityId);
    }

    /// <summary>Without /next, the date and the channel id fall back to the microformat the player response already
    /// carried — no extra request, which is why those members are modelled at all.</summary>
    [Fact]
    public async Task Page_Video_VodFallsBackToTheMicroformatDateAndChannelId()
    {
        var http = new ScriptedHttpHandler()
            .OnUrl("youtubei/v1/next", HttpStatusCode.InternalServerError, "", "text/plain")
            .OnUrl("youtubei/v1/player", HttpStatusCode.OK, YouTubeFixtures.PlayerVodOk, "application/json");
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        // PlayerVodOk carries no videoDetails.channelId at all; externalChannelId is the only source here.
        Assert.Equal("channel:" + YouTubeFixtures.ChannelId, page!.Hero!.SubtitleEntityId);
        Assert.Contains("2026-08-20", page.Hero.MetaLine!, StringComparison.Ordinal);
        Assert.Contains("987,654 views", page.Hero.MetaLine!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_Video_NextIsAskedAsTheWebMetadataClient()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        RecordedRequest next = Assert.Single(NextCalls(http));
        Assert.Equal("POST", next.Method);
        Assert.Contains("\"videoId\":\"" + Id + "\"", next.Body, StringComparison.Ordinal);
        Assert.Contains("\"clientName\":\"WEB\"", next.Body, StringComparison.Ordinal);
        Assert.Contains("\"hl\":\"en\"", next.Body, StringComparison.Ordinal);
        Assert.Equal("1", next.Header("X-YouTube-Client-Name"));
        Assert.Equal("https://www.youtube.com", next.Header("Origin"));
        Assert.Equal(YouTubeModule.ConsentCookie, next.Header("Cookie"));
        Assert.Equal(YouTubeModule.DesktopUserAgent, next.Header("User-Agent"));

        // /next plays nothing, so the playback members of the /player body have no business here.
        Assert.DoesNotContain("playbackContext", next.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("contentCheckOk", next.Body, StringComparison.Ordinal);
    }

    /// <summary>The WEB block is metadata-only: it must never enter the /player fallback walk, whose bans on WEB
    /// (SABR-only sessions needing the JS player) are unchanged.</summary>
    [Fact]
    public void ClientTable_KeepsWebOutOfThePlayerWalk()
    {
        var module = new YouTubeModule(new ScriptedHttpHandler(), disposeHandler: true);

        Assert.Equal(["visionos", "android", "ios"], module.Clients.Select(c => c.Key));
        Assert.All(module.Clients, c => Assert.True(c.IsPlayback));
        Assert.Equal("web", module.MetadataClient!.Key);
        Assert.Equal("WEB", module.MetadataClient.ClientName);
        Assert.True(module.MetadataClient.IsMetadata);
        Assert.Equal(4, module.ClientTable.Length);
    }

    [Fact]
    public async Task Page_Video_NeedsNoPlayableManifest()
    {
        // A SABR-only session cannot play, but the page is still worth showing — so no HLS url and no preflight.
        var http = Player(YouTubeFixtures.PlayerSabrOnly);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal("Claude FM", page!.Hero!.Title);
        Assert.DoesNotContain(http.Requests, r => r.Url.Contains("manifest.googlevideo.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Page_Video_VodShowsItsLength()
    {
        var http = Player(YouTubeFixtures.PlayerVodOk, YouTubeFixtures.NextWatchVod);
        (ModuleTestHost host, _) = Make(http);

        ModulePageDoc? page = await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);

        Assert.False(page!.Hero!.IsLive);
        PageSection facts = page.Sections.Single(x => x.Kind == PageSection.KindFacts);
        Assert.Contains(facts.Rows!, r => r[0] == "Length" && r[1] == "1:01:12");
        Assert.Contains(facts.Rows!, r => r[0] == "Views" && r[1] == "987,654");
    }

    [Fact]
    public async Task Page_Video_BlockedOnEveryClientIsATypedFailure()
    {
        var http = Player(YouTubeFixtures.PlayerVideoIdMismatch);
        (ModuleTestHost host, _) = Make(http);

        ModuleException ex = await Assert.ThrowsAsync<ModuleException>(
            () => host.PageAsync("video:" + Id, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
    }

    [Fact]
    public async Task Page_Channel_WithNothingResolvedYetIsHonestAboutIt()
    {
        (ModuleTestHost host, ScriptedHttpHandler http) = Make(new ScriptedHttpHandler());

        ModulePageDoc? page = await host.PageAsync("channel:UC123", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Empty(http.Requests);                                  // there is no channel endpoint to call
        Assert.False(page!.Hero!.IsLive);
        PageAction open = Assert.Single(page.Actions);
        Assert.Equal("https://www.youtube.com/channel/UC123", open.Url);
        PageSection note = Assert.Single(page.Sections);
        Assert.Equal(PageSection.KindText, note.Kind);
        Assert.Contains("player response", note.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_Channel_ShowsLiveNowOnceTheChannelsBroadcastWasResolved()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);
        ModulePageDoc? page = await host.PageAsync(resolved.SubtitleEntityId!, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal("Anthropic", page!.Hero!.Title);
        Assert.True(page.Hero.IsLive);
        PageSection live = Assert.Single(page.Sections);
        Assert.Equal(PageSection.KindPlayables, live.Kind);
        PageItem item = Assert.Single(live.Items!);
        Assert.Equal(Id, item.PlayableId);
        Assert.Equal("video:" + Id, item.EntityId);
        Assert.True(item.IsLive);
        Assert.Equal(MediaForm.Video, item.Form);
    }

    /// <summary>A channel page still makes ZERO http calls of its own: the avatar was cached by the video page that
    /// last mentioned this channel, so <c>/next</c> is paid for once and reused.</summary>
    [Fact]
    public async Task Page_Channel_ShowsTheAvatarAVideoPageCachedWithoutAnyRequestOfItsOwn()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk);
        (ModuleTestHost host, _) = Make(http);

        await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);
        int requestsAfterTheVideoPage = http.Requests.Count;

        ModulePageDoc? page = await host.PageAsync("channel:" + YouTubeFixtures.ChannelId,
            TestContext.Current.CancellationToken);

        Assert.Equal(requestsAfterTheVideoPage, http.Requests.Count);
        Assert.Equal("Anthropic", page!.Hero!.Title);
        Assert.Equal(YouTubeFixtures.ChannelAvatarUrl, page.Hero.AvatarUrl);
        Assert.True(page.Hero.IsLive);
        PageItem item = Assert.Single(page.Sections.Single(x => x.Kind == PageSection.KindPlayables).Items!);
        Assert.Equal(Id, item.PlayableId);
    }

    /// <summary>A resolve after a page visit must not erase the avatar that visit learned: the snapshot merges.</summary>
    [Fact]
    public async Task Page_Channel_KeepsTheAvatarWhenAResolveLaterOverwritesTheSnapshot()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.PageAsync("video:" + Id, TestContext.Current.CancellationToken);
        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);
        ModulePageDoc? page = await host.PageAsync("channel:" + YouTubeFixtures.ChannelId,
            TestContext.Current.CancellationToken);

        Assert.Equal(YouTubeFixtures.ChannelAvatarUrl, page!.Hero!.AvatarUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("station:http://x/y")]
    [InlineData("video:not-an-id")]
    [InlineData("channel:")]
    public async Task Page_ForeignOrMalformedEntityIdsAreNull(string entityId)
    {
        (ModuleTestHost host, _) = Make(new ScriptedHttpHandler());

        Assert.Null(await host.PageAsync(entityId, TestContext.Current.CancellationToken));
    }

    /// <summary>Playback latency must not pay for page copy: /next is a page-path call and nothing else. The rule
    /// IS scripted here, so this asserts the module chose not to call it rather than that it could not.</summary>
    [Fact]
    public async Task Resolve_NeverCallsTheWatchNextEndpoint()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.Empty(NextCalls(http));
        Assert.DoesNotContain(http.Requests, r => r.Url.Contains("youtubei/v1/next", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_CarriesThePageAndSubtitleEntityIds()
    {
        var http = Player(YouTubeFixtures.PlayerLiveOk).WithManifest();
        (ModuleTestHost host, _) = Make(http);

        ResolvedPlayable resolved = await host.ResolveAsync(Id, TestContext.Current.CancellationToken);

        Assert.Equal("video:" + Id, resolved.PageEntityId);
        Assert.Equal("channel:UCAAAAAAAAAAAAAAAAAAAAA", resolved.SubtitleEntityId);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Answers every client's player request with the same body, and every <c>/next</c> request with a watch-next
    /// document. The <c>/next</c> rule is here rather than in the page tests alone because
    /// <see cref="ScriptedHttpHandler"/> throws on an unscripted request: a resolve test that never calls the
    /// endpoint proves it by the recorded requests, not by the transport blowing up.
    /// </summary>
    /// <param name="playerJson">The <c>/player</c> body every client gets.</param>
    /// <param name="nextJson">The <c>/next</c> body, defaulting to the live watch-next document.</param>
    private static ScriptedHttpHandler Player(string playerJson, string? nextJson = null)
        => new ScriptedHttpHandler()
            .OnUrl("youtubei/v1/next", HttpStatusCode.OK, nextJson ?? YouTubeFixtures.NextWatchLive,
                "application/json")
            .OnUrl("youtubei/v1/player", HttpStatusCode.OK, playerJson, "application/json");

    private static RecordedRequest[] PlayerCalls(ScriptedHttpHandler http)
        => http.Requests.Where(r => r.Url.Contains("youtubei/v1/player", StringComparison.Ordinal)).ToArray();

    private static RecordedRequest[] NextCalls(ScriptedHttpHandler http)
        => http.Requests.Where(r => r.Url.Contains("youtubei/v1/next", StringComparison.Ordinal)).ToArray();
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
