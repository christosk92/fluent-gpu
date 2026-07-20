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
    public void AbrDownshiftsImmediatelyAndRequiresTwoBufferedUpgradeVotes()
    {
        var abr = new AdaptiveBitrateController { UpgradeBuffer = TimeSpan.FromSeconds(10), SafetyFactor = 0.8 };
        int[] bitrates = [300_000, 1_000_000, 3_000_000];
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));
        Assert.Equal(2, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 500));
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(3), 5_000));
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
        abr.RecordDownload(2_000_000, TimeSpan.FromSeconds(1));

        Assert.Equal(0, abr.Choose(variants, TimeSpan.FromSeconds(20)));
        Assert.Equal(2, abr.Choose(variants, TimeSpan.FromSeconds(20)));
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
