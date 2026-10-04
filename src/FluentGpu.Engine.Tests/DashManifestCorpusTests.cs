using System;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The shared DASH corpus for the ONE parser (<see cref="DashManifestParser"/>): template number formats,
/// <c>$Time$</c> addressing, open-ended <c>@r=-1</c> runs, <c>@startNumber</c>, stable ids, the segment cap and the
/// protection / SAP metadata the protected-descriptor mapper reads. The Windows tests map the same shapes onto a
/// <c>DashSourceDescriptor</c>.</summary>
public sealed class DashManifestCorpusTests
{
    private static readonly Uri Source = new("https://example.test/dash/manifest.mpd");

    private static AdaptiveRepresentation Video(AdaptiveManifest manifest) => manifest.TrackGroups[0].Representations[0];

    private static AdaptiveManifest ParseVideo(string mpdAttributes, string template, string representation = """<Representation id="v" bandwidth="1000000" width="1280" height="720"/>""")
        => DashManifestParser.Parse($"""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" {mpdAttributes}>
              <Period><AdaptationSet contentType="video" mimeType="video/mp4" codecs="avc1.640028">
                {template}
                {representation}
              </AdaptationSet></Period>
            </MPD>
            """, Source);

    [Fact]
    public void PaddedNumberTemplate_IsZeroPaddedToItsWidth_FromTheStartNumber()
    {
        var manifest = ParseVideo("""mediaPresentationDuration="PT6S" """, """
            <SegmentTemplate timescale="1000" initialization="init-$RepresentationID$.mp4" media="$RepresentationID$-$Number%05d$.m4s" startNumber="7">
              <SegmentTimeline><S t="0" d="2000" r="2"/></SegmentTimeline>
            </SegmentTemplate>
            """);

        var rep = Video(manifest);
        Assert.Equal("https://example.test/dash/init-v.mp4", rep.Initialization!.AbsoluteUri);
        Assert.Equal(3, rep.Segments.Count);
        Assert.Equal("https://example.test/dash/v-00007.m4s", rep.Segments[0].Uri.AbsoluteUri);
        Assert.Equal("https://example.test/dash/v-00009.m4s", rep.Segments[2].Uri.AbsoluteUri);
        Assert.Equal(7, rep.Segments[0].Number);
        Assert.Equal(9, rep.Segments[2].Number);
    }

    [Theory]
    [InlineData("$Number%03d$", 1000, "1000")]   // a wider number is never truncated
    [InlineData("$Number%03d$", 7, "007")]
    [InlineData("$Number%1d$", 7, "7")]
    [InlineData("$Number$", 7, "7")]
    public void NumberFormat_PadsToAtLeastTheWidth(string token, int startNumber, string expected)
    {
        var manifest = ParseVideo("""mediaPresentationDuration="PT2S" """,
            $"""<SegmentTemplate duration="2" timescale="1" startNumber="{startNumber}" initialization="i.mp4" media="s-{token}.m4s"/>""");

        Assert.Equal($"https://example.test/dash/s-{expected}.m4s", Video(manifest).Segments[0].Uri.AbsoluteUri);
    }

    [Fact]
    public void TimeTemplate_AddressesEachSegmentByItsStartTime()
    {
        var manifest = ParseVideo("""mediaPresentationDuration="PT5S" """, """
            <SegmentTemplate timescale="1000" initialization="i.mp4" media="t-$Time$.m4s">
              <SegmentTimeline><S t="500" d="2000" r="1"/><S d="1000"/></SegmentTimeline>
            </SegmentTemplate>
            """);

        var segs = Video(manifest).Segments;
        Assert.Equal(3, segs.Count);
        Assert.Equal("https://example.test/dash/t-500.m4s", segs[0].Uri.AbsoluteUri);
        Assert.Equal("https://example.test/dash/t-2500.m4s", segs[1].Uri.AbsoluteUri);
        Assert.Equal("https://example.test/dash/t-4500.m4s", segs[2].Uri.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(0.5), segs[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2), segs[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), segs[2].Duration);
        Assert.Equal(new long[] { 1, 2, 3 }, new[] { segs[0].Number, segs[1].Number, segs[2].Number });
    }

    [Fact]
    public void OpenEndedRepeat_RunsToThePeriodEnd()
    {
        var manifest = ParseVideo("""mediaPresentationDuration="PT10S" """, """
            <SegmentTemplate timescale="1000" initialization="i.mp4" media="s-$Number$.m4s">
              <SegmentTimeline><S t="0" d="2000" r="-1"/></SegmentTimeline>
            </SegmentTemplate>
            """);

        var segs = Video(manifest).Segments;
        Assert.Equal(5, segs.Count);
        Assert.Equal(TimeSpan.FromSeconds(8), segs[4].Start);
    }

    [Fact]
    public void OpenEndedRepeat_StopsAtTheNextExplicitTime()
    {
        var manifest = ParseVideo("""mediaPresentationDuration="PT14S" """, """
            <SegmentTemplate timescale="1" initialization="i.mp4" media="s-$Number$.m4s">
              <SegmentTimeline><S t="0" d="2" r="-1"/><S t="10" d="2" r="1"/></SegmentTimeline>
            </SegmentTemplate>
            """);

        var segs = Video(manifest).Segments;
        Assert.Equal(7, segs.Count);                                  // 0,2,4,6,8 then 10,12: no overlap, no gap
        Assert.Equal(TimeSpan.FromSeconds(8), segs[4].Start);
        Assert.Equal(TimeSpan.FromSeconds(10), segs[5].Start);
        Assert.Equal(7, segs[^1].Number);
    }

    [Fact]
    public void StartNumber_NumbersTheDurationAddressedSegments()
    {
        var manifest = ParseVideo("""mediaPresentationDuration="PT6S" """,
            """<SegmentTemplate duration="2" timescale="1" startNumber="42" initialization="i.mp4" media="s-$Number$.m4s"/>""");

        var segs = Video(manifest).Segments;
        Assert.Equal(3, segs.Count);
        Assert.Equal(42, segs[0].Number);
        Assert.Equal("https://example.test/dash/s-44.m4s", segs[2].Uri.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(4), segs[2].Start);
    }

    [Fact]
    public void MissingRepresentationIds_AreStablePositionIdsAcrossReparse()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S">
              <Period><AdaptationSet contentType="video" codecs="avc1.4d401f">
                <SegmentTemplate duration="2" initialization="i.mp4" media="s-$Number$.m4s"/>
                <Representation bandwidth="1"/><Representation bandwidth="2"/>
              </AdaptationSet></Period>
            </MPD>
            """;

        var first = DashManifestParser.Parse(mpd, Source).TrackGroups[0].Representations;
        var second = DashManifestParser.Parse(mpd, Source).TrackGroups[0].Representations;

        Assert.Equal("p0a0r0", first[0].Quality.Id);
        Assert.Equal("p0a0r1", first[1].Quality.Id);
        Assert.Equal(first[0].Quality.Id, second[0].Quality.Id);
        Assert.Equal(first[1].Quality.Id, second[1].Quality.Id);
    }

    [Fact]
    public void SegmentCap_TruncatesALongPresentationAtTheBound()
    {
        var byDuration = ParseVideo("""mediaPresentationDuration="PT5000S" """,
            """<SegmentTemplate duration="1" timescale="1" initialization="i.mp4" media="s-$Number$.m4s"/>""");
        var byTimeline = ParseVideo("""mediaPresentationDuration="PT10000S" """, """
            <SegmentTemplate timescale="1" initialization="i.mp4" media="s-$Number$.m4s">
              <SegmentTimeline><S t="0" d="1" r="9999"/></SegmentTimeline>
            </SegmentTemplate>
            """);

        Assert.Equal(DashManifestParser.MaxSegmentsPerRepresentation, Video(byDuration).Segments.Count);
        Assert.Equal(DashManifestParser.MaxSegmentsPerRepresentation, Video(byTimeline).Segments.Count);
        Assert.Equal(4096, DashManifestParser.MaxSegmentsPerRepresentation);
    }

    [Fact]
    public void ContentProtection_PlayReadyDataWinsOverWidevine_AndCarriesKidAndSap()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013" xmlns:mspr="urn:microsoft:playready"
                 type="static" mediaPresentationDuration="PT4S">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028" startWithSAP="0">
                <ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" cenc:default_KID="4060A865-8878-4267-9CBF-91AE5BAE1E72"/>
                <ContentProtection schemeIdUri="urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95"><mspr:pro>BAUG</mspr:pro><cenc:pssh>AQID</cenc:pssh></ContentProtection>
                <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"><cenc:pssh>BwgJ</cenc:pssh></ContentProtection>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;

        var rep = DashManifestParser.Parse(mpd, Source).TrackGroups[0].Representations[0];

        Assert.Equal("playready", rep.DrmScheme);
        Assert.Equal(new byte[] { 1, 2, 3 }, rep.InitData.ToArray());   // the pssh, not the pro, not Widevine's
        Assert.Equal("4060a865887842679cbf91ae5bae1e72", rep.DefaultKid);
        Assert.Equal("avc1.640028", rep.Codecs);
        Assert.False(rep.SegmentsStartWithKeyframe);                     // startWithSAP="0"
    }

    [Fact]
    public void ContentProtection_PlayReadyWithoutData_DoesNotInheritAWidevineSiblingsData()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013" type="static">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028">
                <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"><cenc:pssh>BwgJ</cenc:pssh></ContentProtection>
                <ContentProtection schemeIdUri="urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95"/>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;

        var rep = DashManifestParser.Parse(mpd, Source).TrackGroups[0].Representations[0];

        Assert.Equal("playready", rep.DrmScheme);
        Assert.True(rep.InitData.IsEmpty);
        Assert.True(rep.SegmentsStartWithKeyframe);                      // undeclared SAP is assumed
    }

    [Fact]
    public void AdaptationSetWithoutAType_TakesItFromItsRepresentationsMimeType()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period><AdaptationSet>
                <Representation id="a" mimeType="audio/mp4" codecs="mp4a.40.2" bandwidth="128000"/>
              </AdaptationSet></Period>
            </MPD>
            """;

        var manifest = DashManifestParser.Parse(mpd, Source);

        Assert.Equal(AdaptiveTrackType.Audio, manifest.TrackGroups[0].Type);
        Assert.Equal(CodecId.Aac, manifest.TrackGroups[0].Representations[0].Quality.Codec.Audio);
    }
}
