using System;
using System.Collections.Generic;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class AdaptiveMediaTests
{
    [Fact]
    public void Dash_NormalizesPaddedTemplateTimelineTracksAndPlayReadyInitData()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S">
              <BaseURL>media/</BaseURL><Period>
                <AdaptationSet id="v" contentType="video" mimeType="video/mp4" codecs="avc1.640028">
                  <ContentProtection schemeIdUri="urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95"><cenc:pssh xmlns:cenc="urn:mpeg:cenc:2013">AQID</cenc:pssh></ContentProtection>
                  <SegmentTemplate timescale="1000" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/seg-$Number%05d$.m4s" startNumber="7">
                    <SegmentTimeline><S t="0" d="2000" r="2"/></SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="1080" bandwidth="5000000" width="1920" height="1080" frameRate="30000/1001"/>
                </AdaptationSet>
                <AdaptationSet id="a" contentType="audio" lang="en" mimeType="audio/mp4" codecs="mp4a.40.2">
                  <Role value="main"/><SegmentTemplate duration="2" initialization="a-init.mp4" media="a-$Number$.m4s"/>
                  <Representation id="aac" bandwidth="128000"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var manifest = DashManifestParser.Parse(mpd, new Uri("https://example.test/root/manifest.mpd"));
        Assert.False(manifest.IsLive);
        Assert.Equal(2, manifest.TrackGroups.Count);
        var video = manifest.TrackGroups[0].Representations[0];
        Assert.Equal("https://example.test/root/media/1080/init.mp4", video.Initialization!.AbsoluteUri);
        Assert.EndsWith("seg-00007.m4s", video.Segments[0].Uri.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(4), video.Segments[2].Start);
        Assert.Equal("playready", video.DrmScheme);
        Assert.Equal(new byte[] { 1, 2, 3 }, video.InitData.ToArray());
        Assert.Equal(30000d / 1001d, video.Quality.FrameRate, 5);
    }

    [Fact]
    public void Dash_DynamicWindowIsBoundedAroundNow()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic" availabilityStartTime="2026-01-01T00:00:00Z" timeShiftBufferDepth="PT12S" minimumUpdatePeriod="PT2S">
              <Period><AdaptationSet contentType="video" codecs="avc1"><SegmentTemplate duration="2" timescale="1" startNumber="1" media="v-$Number$.m4s"/><Representation id="v" bandwidth="300000"/></AdaptationSet></Period>
            </MPD>
            """;
        var now = DateTimeOffset.Parse("2026-01-01T00:01:00Z");
        var manifest = DashManifestParser.Parse(mpd, new Uri("https://example.test/live.mpd"), now);
        var segments = manifest.TrackGroups[0].Representations[0].Segments;
        Assert.True(manifest.IsLive);
        Assert.Equal(6, segments.Count);
        Assert.Equal(26, segments[0].Number);
        Assert.Equal(31, segments[^1].Number);
    }

    [Fact]
    public void Hls_MasterExposesVariantsAlternateAudioAndSubtitles()
    {
        const string hls = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",LANGUAGE="en",NAME="English",DEFAULT=YES,AUTOSELECT=YES,URI="audio/en.m3u8"
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="sub",LANGUAGE="en",NAME="English CC",DEFAULT=YES,FORCED=NO,URI="subs/en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,AVERAGE-BANDWIDTH=700000,RESOLUTION=1280x720,FRAME-RATE=59.94,CODECS="avc1.64001f,mp4a.40.2",AUDIO="aud",SUBTITLES="sub"
            video/720.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=4200000,RESOLUTION=1920x1080,VIDEO-RANGE=PQ,CODECS="hvc1.2.4.L153.B0"
            video/1080-hdr.m3u8
            """;
        var manifest = HlsManifestParser.Parse(hls, new Uri("https://example.test/master.m3u8"));
        Assert.Equal(3, manifest.TrackGroups.Count);
        Assert.Equal(2, manifest.TrackGroups[0].Representations.Count);
        Assert.Equal(HdrFormat.Hdr10, manifest.TrackGroups[0].Representations[1].Quality.Hdr);
        Assert.Equal("https://example.test/audio/en.m3u8", manifest.TrackGroups[1].Representations[0].PlaylistUri);
        Assert.Equal(TrackRole.Subtitles, manifest.TrackGroups[2].Role);
    }

    [Fact]
    public void Hls_LowLatencyMediaKeepsPartsRangesDiscontinuityAndLiveWindow()
    {
        const string hls = """
            #EXTM3U
            #EXT-X-TARGETDURATION:4
            #EXT-X-PART-INF:PART-TARGET=0.5
            #EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=1.5
            #EXT-X-MEDIA-SEQUENCE:42
            #EXT-X-MAP:URI="init.mp4"
            #EXT-X-PROGRAM-DATE-TIME:2026-07-20T10:00:00Z
            #EXT-X-PART:DURATION=0.5,URI="p42.0.m4s",BYTERANGE="100@20"
            #EXT-X-PART:DURATION=0.5,URI="p42.1.m4s",BYTERANGE="120@120"
            #EXTINF:4.0,
            s42.m4s
            #EXT-X-DISCONTINUITY
            #EXT-X-GAP
            #EXTINF:4.0,
            s43.m4s
            """;
        var manifest = HlsManifestParser.Parse(hls, new Uri("https://example.test/live/index.m3u8"));
        var rep = manifest.TrackGroups[0].Representations[0];
        Assert.True(manifest.IsLive);
        Assert.True(manifest.IsLowLatency);
        Assert.Equal("https://example.test/live/init.mp4", rep.Initialization!.AbsoluteUri);
        Assert.True(rep.Segments[0].IsPartial);
        Assert.Equal(100, rep.Segments[0].ByteRangeLength);
        Assert.Equal(20, rep.Segments[0].ByteRangeOffset);
        Assert.Equal(1, rep.Segments[^1].DiscontinuitySequence);
        Assert.True(rep.Segments[^1].IsGap);
    }

    [Fact]
    public void SchedulerPlansInitAndMissingWindowForEverySelectedTrack()
    {
        var quality = new QualityVariant("0", 500_000, new SizeI(640, 360), 30,
            new MediaContentType(Container.Dash, CodecId.H264, CodecId.None));
        var segments = new[]
        {
            Segment(1, 0), Segment(2, 2), Segment(3, 4), Segment(4, 6), Segment(5, 8)
        };
        var rep = new AdaptiveRepresentation(quality, new Uri("https://x/init"), segments);
        var manifest = new AdaptiveManifest(new Uri("https://x/m.mpd"), AdaptiveManifestKind.Dash, false, false,
            TimeSpan.FromSeconds(10), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null,
            new[] { new AdaptiveTrackGroup("v", AdaptiveTrackType.Video, null, TrackRole.Main, new[] { rep }, true) });
        var policy = BufferPolicy.Vod with { TargetForward = TimeSpan.FromSeconds(5) };
        var plan = AdaptiveSegmentScheduler.Plan(manifest, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), policy, g => g.Representations[0]);
        Assert.Equal(4, plan.Count); // init + segments 2,3,4 (starts before target=8)
        Assert.Equal(AdaptiveRequestKind.Initialization, plan[0].Kind);
        Assert.Equal(2, plan[1].Segment.Number);
        Assert.DoesNotContain(plan, x => x.Segment.Number == 1);
    }

    [Fact]
    public void AbrDownshiftsImmediatelyAndGatesClimbsOnOneVoteThenTwo()
    {
        var abr = new AdaptiveBitrateController { UpgradeBuffer = TimeSpan.FromSeconds(10) };
        int[] bitrates = [300_000, 1_000_000, 3_000_000];

        // The FIRST climb after startup lands on ONE vote. The two-vote gate exists to damp steady-state
        // oscillation; applying it at startup is what left Auto parked on the opening rung for seconds after the
        // estimate had already justified moving off it.
        Assert.Equal(2, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));
        Assert.Equal(AbrDecisionReason.Throughput, abr.LastDecisionReason);

        // A downswitch is immediate and unconditional — a stall is worse than any resolution.
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 500));
        Assert.Equal(AbrDecisionReason.Throughput, abr.LastDecisionReason);

        // Having climbed once, a further climb needs two consecutive votes for the same candidate.
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));
        Assert.Equal(2, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));

        // A starved forward buffer blocks any climb regardless of throughput.
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 500));
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(3), 5_000));
        Assert.Equal(AbrDecisionReason.Buffer, abr.LastDecisionReason);
    }

    [Fact]
    public void AbrForcedProbeClimbsOffARungTheEstimateCannotJustify()
    {
        // The estimate is derived from what the CURRENT rung downloads, so a low rendition self-reinforces: it never
        // transfers enough per segment to justify climbing off itself. Without the forced probe this is how Auto sits
        // on the bottom rung over a fast link forever — the "Auto - 240p" report.
        long now = 0;   // the probe is timed in WALL time; one decision a second, a 2 s steady interval
        var abr = new AdaptiveBitrateController
        {
            UpgradeBuffer = TimeSpan.Zero, ForcedProbeInterval = TimeSpan.FromSeconds(2), NowMs = () => now,
        };
        int[] bitrates = [300_000, 1_000_000];
        const double justBelowTheNextRung = 1_000.0;   // kbps: 1 Mbps, so 1_000_000 bps never clears the 0.85 climb budget

        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), justBelowTheNextRung));
        now += 1_000;
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), justBelowTheNextRung));
        now += 1_000;
        Assert.Equal(1, abr.Choose(bitrates, TimeSpan.FromSeconds(20), justBelowTheNextRung));
        Assert.Equal(AbrDecisionReason.ForcedProbe, abr.LastDecisionReason);
    }

    [Fact]
    public void ThroughputEstimatorAcceptsALargeAggregateBelowTheDurationFloor()
    {
        // Above LargeSampleBytes the duration floor no longer applies: a 5 MB aggregate a CDN answered in 130 ms IS
        // 300 Mbps — the RTT share of 130 ms is the noise on a transfer this large, not the signal.
        var estimator = new ThroughputEstimator();
        Assert.True(estimator.Add(5_000_000, TimeSpan.FromMilliseconds(130)));
        Assert.InRange(estimator.EstimateKbps, 307_600, 307_800);   // 5_000_000 * 8 / 0.130 / 1000 ≈ 307_692.3 kbps

        // Below LargeSampleBytes the duration floor still applies, on top of the payload floor.
        Assert.False(estimator.Add(12 * 1024, TimeSpan.FromMilliseconds(40)));    // small AND short
        Assert.False(estimator.Add(100 * 1024, TimeSpan.FromMilliseconds(100)));  // small AND short
        Assert.True(estimator.Add(100 * 1024, TimeSpan.FromMilliseconds(250)));   // clears both floors
    }

    [Fact]
    public void AbrHoldsOnAPriorInsteadOfDownswitchingTheOpenedRung()
    {
        // A prior (the 2 Mbps seed, or a remembered estimate) must never move the ladder off the rung the backend
        // deliberately opened: the seed cannot "afford" the app's ≤480p opening rung, and acting on it put every
        // cold start through two representation switches (two swap chains) before the first frame (2026-09-22).
        var abr = new AdaptiveBitrateController { UpgradeBuffer = TimeSpan.FromSeconds(60) };
        int[] bitrates = [1_247_134, 2_687_348, 5_872_226];
        abr.SeedCurrent(1);

        Assert.Equal(1, abr.Choose(bitrates, TimeSpan.FromSeconds(60), 2_000, estimateIsPrior: true));
        Assert.Equal(AbrDecisionReason.Hold, abr.LastDecisionReason);

        // The first real measurement is free to move the ladder — the prior guard applies only while it is a prior.
        // A measured decrease is immediate only once the buffer is under MaxBufferForQualityDecrease (25 s); above it the
        // decrease is deferred (DeferredDecrease), so this check runs at 20 s.
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 1_870, estimateIsPrior: false));
        Assert.Equal(AbrDecisionReason.Throughput, abr.LastDecisionReason);
    }

    [Fact]
    public void AbrForcedProbeStillClimbsOffAPrior()
    {
        // Rule 4 (forced probe) is the only move off a prior: it must survive even while estimateIsPrior is true, or
        // a link with nothing but a seeded/remembered estimate could never climb off its opening rung.
        long now = 0;
        var abr = new AdaptiveBitrateController
        {
            UpgradeBuffer = TimeSpan.Zero, ForcedProbeInterval = TimeSpan.FromSeconds(2), NowMs = () => now,
        };
        int[] bitrates = [300_000, 1_000_000];
        const double justBelowTheNextRung = 1_000.0;   // kbps: 1 Mbps, so 1_000_000 bps never clears the 0.85 climb budget

        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), justBelowTheNextRung, estimateIsPrior: true));
        now += 1_000;
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), justBelowTheNextRung, estimateIsPrior: true));
        now += 1_000;
        Assert.Equal(1, abr.Choose(bitrates, TimeSpan.FromSeconds(20), justBelowTheNextRung, estimateIsPrior: true));
        Assert.Equal(AbrDecisionReason.ForcedProbe, abr.LastDecisionReason);
    }

    [Fact]
    public void AbrSeededEstimateIsAPriorUntilTheFirstSample()
    {
        // SeedEstimate hands in a remembered estimate (the app's LinkMemory) without pretending it was measured:
        // IsMeasured stays false, EstimateIsPrior stays true, and the first real measurement REPLACES it outright,
        // exactly like the 2 Mbps startup prior.
        var abr = new AdaptiveBitrateController();
        abr.SeedEstimate(300_000);
        Assert.Equal(300_000, abr.EstimatedKbps);
        Assert.True(abr.EstimateIsPrior);

        Assert.True(abr.RecordDownload(5 * 1024 * 1024, TimeSpan.FromMilliseconds(130)));
        Assert.False(abr.EstimateIsPrior);
        Assert.NotEqual(300_000, abr.EstimatedKbps);

        // Once anything has been measured, a later seed is ignored.
        abr.SeedEstimate(1);
        Assert.NotEqual(1, abr.EstimatedKbps);
    }

    [Fact]
    public void AbrResolutionCapReturnsOriginalVariantIndexAfterFiltering()
    {
        var codec = new MediaContentType(Container.Dash, CodecId.H264, CodecId.None);
        QualityVariant[] variants =
        [
            new("360", 300_000, new SizeI(640, 360), 30, codec),
            new("2160", 8_000_000, new SizeI(3840, 2160), 60, codec),
            new("720", 1_000_000, new SizeI(1280, 720), 30, codec),
        ];
        var abr = new AdaptiveBitrateController { MaxHeight = 720, UpgradeBuffer = TimeSpan.Zero };
        abr.RecordDownload(2_000_000, TimeSpan.FromSeconds(1));   // 16 Mbps — the first sample REPLACES the 2 Mbps prior

        // Filtering happens in local coordinates but the answer is a FULL-list index: 720p is index 2, not index 1.
        Assert.Equal(2, abr.Choose(variants, TimeSpan.FromSeconds(20)));
        // The 2160p rung is above the cap and must never be selected however fast the link is.
        Assert.NotEqual(1, abr.Choose(variants, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void AbrHeightCapBelowEveryRungTakesTheSmallestRungNotIndexZero()
    {
        // THE "Auto - 240p" BUG. A real video viewport (191 DIP docked rail, 202 DIP pop-out, 135 DIP minimum) sits
        // BELOW every rung a manifest offers, so every variant fails the MaxHeight filter and this is the COMMON
        // path, not a corner case. Returning index 0 pinned Auto to whatever happened to be first in the list.
        var codec = new MediaContentType(Container.Dash, CodecId.H264, CodecId.None);
        QualityVariant[] variants =
        [
            new("1080", 4_000_000, new SizeI(1920, 1080), 30, codec),
            new("240", 200_000, new SizeI(426, 240), 30, codec),
            new("720", 1_000_000, new SizeI(1280, 720), 30, codec),
        ];
        var abr = new AdaptiveBitrateController { MaxHeight = 191, UpgradeBuffer = TimeSpan.Zero };
        abr.RecordDownload(4_000_000, TimeSpan.FromSeconds(1));

        // The smallest rung by HEIGHT, addressed in full-list coordinates — index 1, not index 0.
        Assert.Equal(1, abr.Choose(variants, TimeSpan.FromSeconds(20)));
        Assert.Equal(AbrDecisionReason.Capped, abr.LastDecisionReason);
    }

    private static QualityVariant[] Ladder(params (int Height, int Bitrate)[] rungs)
    {
        var codec = new MediaContentType(Container.Dash, CodecId.H264, CodecId.None);
        var variants = new QualityVariant[rungs.Length];
        for (int i = 0; i < rungs.Length; i++)
            variants[i] = new QualityVariant(rungs[i].Height.ToString(), rungs[i].Bitrate,
                new SizeI(rungs[i].Height * 16 / 9, rungs[i].Height), 30, codec);
        return variants;
    }

    private static readonly (int Height, int Bitrate)[] FourRungs =
        [(240, 300_000), (480, 800_000), (720, 1_500_000), (1080, 4_000_000)];

    /// <summary>A controller on the 240/480/720/1080 ladder that has MEASURED a fast link and climbed to 1080 once
    /// (so the two-vote gate is armed, the state a long-lived shared controller is in).</summary>
    private static AdaptiveBitrateController ClimbedTo1080(QualityVariant[] variants)
    {
        var abr = new AdaptiveBitrateController();
        abr.RecordDownload(2_000_000, TimeSpan.FromSeconds(1));   // 16 Mbps, a measurement
        Assert.Equal(3, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        Assert.Equal(AbrDecisionReason.Throughput, abr.LastDecisionReason);
        return abr;
    }

    [Fact]
    public void AbrCapBelowTheCurrentRungTakesTheBestRungUnderTheCap_NotTheBottomOfTheLadder()
    {
        // F138: leaving fullscreen drops the cap under the 1080 rung. The local-index-0 fallback made every rule return
        // the cheapest rung (a quality crash to 240p, then a climb back); the answer is 720 at once.
        QualityVariant[] variants = Ladder(FourRungs);
        var abr = ClimbedTo1080(variants);

        abr.MaxHeight = 720;
        Assert.Equal(2, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        Assert.Equal(AbrDecisionReason.CapDownswitch, abr.LastDecisionReason);
    }

    [Fact]
    public void AbrCapDownswitchIgnoresAThinBuffer()
    {
        // Rule 2 (buffer < 6 s => hold the current rung) must not pin the rung the cap just excluded.
        QualityVariant[] variants = Ladder(FourRungs);
        var abr = ClimbedTo1080(variants);

        abr.MaxHeight = 720;
        Assert.Equal(2, abr.Choose(variants, TimeSpan.FromSeconds(2)));
        Assert.Equal(AbrDecisionReason.CapDownswitch, abr.LastDecisionReason);
    }

    [Fact]
    public void AbrCapDownswitchHappensOnAPriorToo()
    {
        // A prior skips rules 1 and 3, but a cap is not a throughput verdict: the excluded rung is still left.
        QualityVariant[] variants = Ladder(FourRungs);
        var abr = new AdaptiveBitrateController { MaxHeight = 720 };
        abr.SeedCurrent(3);
        Assert.True(abr.EstimateIsPrior);

        Assert.Equal(2, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        Assert.Equal(AbrDecisionReason.CapDownswitch, abr.LastDecisionReason);
    }

    [Fact]
    public void AbrCapDownswitchPicksTheRungByBitrate_NotTheNextHeightDown()
    {
        // Cap 720 on a 240/540/1080 ladder: nothing is at 720, the best allowed rung is 540.
        QualityVariant[] variants = Ladder((240, 300_000), (540, 1_200_000), (1080, 4_000_000));
        var abr = new AdaptiveBitrateController { MaxHeight = 720 };
        abr.SeedCurrent(2);

        Assert.Equal(1, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        Assert.Equal(AbrDecisionReason.CapDownswitch, abr.LastDecisionReason);
    }

    [Fact]
    public void AbrCapDownswitchOnAnUnsortedLadder_AndANonMonotonicOne()
    {
        // The pick is by bitrate / height, never by list position.
        QualityVariant[] unsorted = Ladder((1080, 4_000_000), (240, 300_000), (720, 1_500_000));
        var abr = new AdaptiveBitrateController { MaxHeight = 720 };
        abr.SeedCurrent(0);
        Assert.Equal(2, abr.Choose(unsorted, TimeSpan.FromSeconds(30)));

        // Non-monotonic: the only allowed rung costs MORE than the current one — take the tallest allowed rung.
        QualityVariant[] odd = Ladder((1080, 500_000), (720, 900_000), (480, 700_000));
        var abr2 = new AdaptiveBitrateController { MaxHeight = 720 };
        abr2.SeedCurrent(0);
        Assert.Equal(1, abr2.Choose(odd, TimeSpan.FromSeconds(30)));
        Assert.Equal(AbrDecisionReason.CapDownswitch, abr2.LastDecisionReason);
    }

    [Fact]
    public void AbrCurrentRungOutOfRangeAfterTheLadderShrankIsClamped()
    {
        // SeedCurrent publishes FULL-list coordinates; a shorter ladder on the next source must not index past the end.
        QualityVariant[] variants = Ladder((240, 300_000), (720, 1_500_000), (1080, 4_000_000));
        var abr = new AdaptiveBitrateController { MaxHeight = 720 };
        abr.SeedCurrent(9);

        Assert.Equal(1, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        Assert.Equal(AbrDecisionReason.CapDownswitch, abr.LastDecisionReason);
    }

    [Fact]
    public void AbrCapDownswitchDoesNotCountAsAFailedProbe()
    {
        // A cap is not a throughput verdict: it must not back the probe cadence off.
        QualityVariant[] variants = Ladder(FourRungs);
        long now = 0;
        var abr = new AdaptiveBitrateController
        {
            UpgradeBuffer = TimeSpan.Zero, ForcedProbeInterval = TimeSpan.FromSeconds(2), NowMs = () => now,
        };
        abr.RecordDownload(1_000_000, TimeSpan.FromSeconds(8));   // 1 Mbps: affords 480 only, never 720 by estimate
        abr.SeedCurrent(1);
        Assert.Equal(1, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        now += 1_000;
        Assert.Equal(1, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        now += 1_000;
        Assert.Equal(2, abr.Choose(variants, TimeSpan.FromSeconds(30)));   // forced probe 480 -> 720
        Assert.Equal(AbrDecisionReason.ForcedProbe, abr.LastDecisionReason);

        abr.MaxHeight = 480;
        now += 1_000;
        Assert.Equal(1, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        Assert.Equal(AbrDecisionReason.CapDownswitch, abr.LastDecisionReason);

        // Base cadence (2 s steady), not the backed-off 4 s: the next probe comes after the same two decisions.
        abr.MaxHeight = int.MaxValue;
        now += 1_000;
        Assert.Equal(1, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        now += 1_000;
        Assert.Equal(1, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        now += 1_000;
        Assert.Equal(2, abr.Choose(variants, TimeSpan.FromSeconds(30)));
        Assert.Equal(AbrDecisionReason.ForcedProbe, abr.LastDecisionReason);
    }

    [Fact]
    public void AbrResetForNewSourceMakesTheFirstClimbOneVoteAgain_AndKeepsTheLink()
    {
        var abr = new AdaptiveBitrateController { UpgradeBuffer = TimeSpan.FromSeconds(10) };
        int[] bitrates = [300_000, 1_000_000, 3_000_000];
        abr.RecordDownload(2_000_000, TimeSpan.FromSeconds(1));
        Assert.Equal(2, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));   // first climb: one vote, arms the gate
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 500));     // downswitch
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));   // armed: the climb now needs two votes

        abr.ResetForNewSource();
        abr.SeedCurrent(0);

        Assert.False(abr.EstimateIsPrior);   // the throughput history survives a new source
        Assert.Equal(2, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));   // one vote again
    }

    [Fact]
    public void AbrPolicyAndViewportCapsAreSeparateInputs_AndOnlyTheViewportIsResetPerSource()
    {
        var abr = new AdaptiveBitrateController();
        Assert.Equal(int.MaxValue, abr.MaxHeight);

        abr.MaxHeight = 720;                  // compat setter writes the POLICY cap
        Assert.Equal(720, abr.PolicyMaxHeight);
        Assert.Equal(int.MaxValue, abr.ViewportMaxHeight);

        abr.ViewportMaxHeight = 480;
        Assert.Equal(480, abr.MaxHeight);     // the getter is the effective cap
        abr.MaxHeight = 1080;                 // a policy write does not discard the viewport
        Assert.Equal(480, abr.ViewportMaxHeight);
        Assert.Equal(480, abr.MaxHeight);

        abr.ViewportMaxHeight = 2160;
        Assert.Equal(1080, abr.MaxHeight);    // and a viewport above the policy never raises it

        abr.ResetForNewSource();
        Assert.Equal(1080, abr.PolicyMaxHeight);
        Assert.Equal(int.MaxValue, abr.ViewportMaxHeight);
        Assert.Equal(1080, abr.MaxHeight);
    }

    [Fact]
    public void ThroughputEstimatorWeighsASampleByItsDuration()
    {
        // F150: a 64 KB / 400 ms slice used to move the fast EWMA by a fixed 35% whatever its size; weighted by duration
        // against a 2 s half-life it barely moves a 4 s history.
        var estimator = new ThroughputEstimator();
        Assert.True(estimator.Add(5_000_000, TimeSpan.FromSeconds(4)));   // 10 Mbps
        Assert.InRange(estimator.EstimateKbps, 9_999, 10_001);

        Assert.True(estimator.Add(64 * 1024, TimeSpan.FromMilliseconds(400)));   // 1.3 Mbps
        Assert.True(estimator.EstimateKbps > 8_000, $"estimate {estimator.EstimateKbps} fell too far on one small slice");

        // A long low-rate transfer DOES move it: weight is duration, so seconds of evidence count for more.
        Assert.True(estimator.Add(2_500_000, TimeSpan.FromSeconds(10)));   // 2 Mbps for 10 s
        Assert.True(estimator.EstimateKbps < 4_000, $"estimate {estimator.EstimateKbps} ignored 10 s of slow transfer");
    }

    [Fact]
    public void ThroughputEstimatorIsAPriorUntil128KbHaveBeenMeasured()
    {
        var estimator = new ThroughputEstimator();
        Assert.True(estimator.Add(70 * 1024, TimeSpan.FromMilliseconds(500)));
        Assert.False(estimator.IsMeasured);
        Assert.Equal(ThroughputEstimator.DefaultSeedKbps, estimator.EstimateKbps);
        estimator.Seed(300_000);              // still a prior, so a remembered estimate is still accepted
        Assert.Equal(300_000, estimator.EstimateKbps);

        Assert.True(estimator.Add(70 * 1024, TimeSpan.FromMilliseconds(500)));   // 140 KB total
        Assert.True(estimator.IsMeasured);
        Assert.InRange(estimator.EstimateKbps, 1_146, 1_148);   // 70 KB * 8 / 0.5 s: the measurement replaces the prior outright

        var abr = new AdaptiveBitrateController();
        Assert.True(abr.RecordDownload(70 * 1024, TimeSpan.FromMilliseconds(500)));
        Assert.True(abr.EstimateIsPrior);
        Assert.True(abr.RecordDownload(70 * 1024, TimeSpan.FromMilliseconds(500)));
        Assert.False(abr.EstimateIsPrior);
    }

    [Fact]
    public void AbrManualPinUsesStableVariantId_NotListIndex()
    {
        var codec = new MediaContentType(Container.Mp4, CodecId.H264, CodecId.None);
        QualityVariant[] variants =
        [
            new("5", 250_000, new SizeI(320, 180), 30, codec),
            new("2", 1_500_000, new SizeI(854, 480), 30, codec),
            new("0", 7_500_000, new SizeI(1920, 1080), 30, codec),
        ];
        var abr = new AdaptiveBitrateController { Selection = QualitySelection.Pin("0"), MaxHeight = 480 };

        Assert.Equal(2, abr.Choose(variants, TimeSpan.Zero));
    }

    [Fact]
    public void BufferingThresholdReportsProgressAndResumeReadiness()
    {
        var info = AdaptiveSegmentScheduler.Buffering(BufferingReason.Rebuffering, TimeSpan.FromSeconds(1.5),
            BufferPolicy.Vod with { ResumePlayback = TimeSpan.FromSeconds(3) });
        Assert.Equal(0.5, info.Percent, 4);
        Assert.False(info.CanResume);
    }

    private static AdaptiveSegment Segment(long n, double start) => new(new Uri($"https://x/{n}"), n,
        TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(2));
}
