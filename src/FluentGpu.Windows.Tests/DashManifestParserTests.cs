using System;
using System.IO;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>Unit tests for <see cref="DashDescriptorMapper"/> (the engine's one DASH parser mapped onto the protected
/// open path's <see cref="DashSourceDescriptor"/>): parse a small Axinom-style PlayReady MPD (offline, no network) and
/// assert the init/media template, segment range, PSSH, and default KID are extracted correctly, plus
/// <c>$RepresentationID$</c> substitution + SegmentTimeline counting + BaseURL resolution, the ABR catalog, and that a
/// manifest the prefix/number/suffix model cannot express is rejected instead of mangled.</summary>
public sealed class DashManifestParserTests
{
    private const string AxinomMpdUrl =
        "https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/manifest.mpd";

    // The cenc:pssh payload below: a whole ISO pssh box (size 36, 'pssh', v0, the PlayReady system id, 4 data bytes "ABCD").
    private const string PlayReadyPsshBase64 = "AAAAJHBzc2gAAAAAmgTweZhAQoarkuZb4IhflQAAAARBQkNE";

    // An Axinom-style single-key PlayReady MPD: SegmentTemplate on the AdaptationSet with a literal media name and
    // @duration/@timescale, PlayReady ContentProtection with a cenc:pssh + a cenc ContentProtection with default_KID.
    private const string AxinomMpd = """
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013"
             profiles="urn:mpeg:dash:profile:isoff-live:2011" type="static"
             mediaPresentationDuration="PT30S" minBufferTime="PT4S">
          <Period>
            <AdaptationSet contentType="video" mimeType="video/mp4" segmentAlignment="true" startWithSAP="1">
              <ContentProtection schemeIdUri="urn:mpeg:cenc:2013" cenc:default_KID="4060A865-8878-4267-9CBF-91AE5BAE1E72"/>
              <ContentProtection schemeIdUri="urn:uuid:9A04F079-9840-4286-AB92-E65BE0885F95">
                <cenc:pssh>AAAAJHBzc2gAAAAAmgTweZhAQoarkuZb4IhflQAAAARBQkNE</cenc:pssh>
              </ContentProtection>
              <SegmentTemplate initialization="video-H264-720-2100k_init.mp4"
                               media="video-H264-720-2100k_$Number$.m4s"
                               startNumber="1" duration="60000" timescale="12000"/>
              <Representation id="video-H264-720-2100k" codecs="avc1.640028" bandwidth="2100000" width="1280" height="720"/>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public void Parse_AxinomSingleKey_ExtractsInitMediaPsshKid()
    {
        var d = DashDescriptorMapper.Parse(AxinomMpd, AxinomMpdUrl);

        const string basePath = "https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/";
        Assert.Equal(basePath + "video-H264-720-2100k_init.mp4", d.InitUrl);
        Assert.Equal(basePath, d.SegmentBaseUrl);
        Assert.Equal("video-H264-720-2100k_", d.SegmentPrefix);
        Assert.Equal(".m4s", d.SegmentSuffix);
        Assert.Equal(1, d.StartNumber);
        Assert.Equal(1, d.SegmentStride);
        Assert.Equal(6, d.SegmentCount);   // PT30S / (60000/12000 = 5s) = 6 segments

        Assert.Equal(Convert.FromBase64String(PlayReadyPsshBase64), d.Pssh.ToArray());   // the whole box, handed to the CDM as is
        Assert.Equal("4060a865887842679cbf91ae5bae1e72", d.DefaultKid);          // dashless, lowercase
        Assert.Contains("avc1", d.Codecs);
    }

    [Fact]
    public void Parse_BuildsTheCatalog_SoAbrIsNotSilentlyOff()
    {
        var d = DashDescriptorMapper.Parse(AxinomMpd, AxinomMpdUrl);

        Assert.NotNull(d.Catalog);
        var track = Assert.Single(d.Catalog!.Tracks);
        Assert.Equal(FluentGpu.Media.TrackKind.Video, track.Kind);
        Assert.True(track.IsDefault);
        var rep = Assert.Single(track.Representations);
        Assert.Equal("video-H264-720-2100k", rep.Id);
        Assert.Equal(d.InitUrl, rep.InitUrl);          // the session finds its initial quality by init URL
        Assert.Equal(d.SegmentPrefix, rep.SegmentPrefix);
        Assert.Equal(720, rep.Quality.Resolution.Height);
        Assert.Equal(2_100_000, rep.Quality.Bitrate);
        Assert.Equal(6, rep.SegmentCount);
    }

    [Fact]
    public void Parse_SeveralRepresentations_AreAllInTheCatalog_FirstIsTheOpenedOne()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" codecs="avc1.640028">
                  <SegmentTemplate initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/seg-$Number$.m4s" startNumber="1" duration="4" timescale="1"/>
                  <Representation id="low" bandwidth="500000" width="640" height="360"/>
                  <Representation id="high" bandwidth="3000000" width="1920" height="1080"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var d = DashDescriptorMapper.Parse(mpd, "https://cdn.example.com/v/manifest.mpd");

        Assert.Equal("low", d.RepresentationId);
        Assert.Equal("https://cdn.example.com/v/low/init.mp4", d.InitUrl);
        var track = Assert.Single(d.Catalog!.Tracks);
        Assert.Equal(new[] { "low", "high" }, new[] { track.Representations[0].Id, track.Representations[1].Id });
        Assert.Equal("https://cdn.example.com/v/high/", track.Representations[1].SegmentBaseUrl);
        Assert.Equal("1080p", track.Label);
        Assert.Null(d.AudioInitUrl);   // no soundtrack: video only, never a failure
    }

    // A live-profile MPD: $RepresentationID$ template on the Representation, a SegmentTimeline (r-repeats), and a
    // relative BaseURL to resolve.
    private const string TimelineMpd = """
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013" type="static">
          <BaseURL>dash/</BaseURL>
          <Period>
            <AdaptationSet contentType="video">
              <ContentProtection schemeIdUri="urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95">
                <cenc:pssh>QUJDRA==</cenc:pssh>
              </ContentProtection>
              <Representation id="v0" codecs="avc1.4d401f" bandwidth="1500000" width="1280" height="720">
                <SegmentTemplate initialization="$RepresentationID$/init.mp4"
                                 media="$RepresentationID$/seg-$Number$.m4s" startNumber="1">
                  <SegmentTimeline>
                    <S t="0" d="48000" r="4"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public void Parse_RepresentationIdTemplate_And_SegmentTimeline_ResolvesBaseUrl()
    {
        var d = DashDescriptorMapper.Parse(TimelineMpd, "https://cdn.example.com/vod/manifest.mpd");

        Assert.Equal("https://cdn.example.com/vod/dash/v0/init.mp4", d.InitUrl);
        Assert.Equal("https://cdn.example.com/vod/dash/v0/", d.SegmentBaseUrl);
        Assert.Equal("seg-", d.SegmentPrefix);
        Assert.Equal(".m4s", d.SegmentSuffix);
        Assert.Equal(1, d.StartNumber);
        Assert.Equal(5, d.SegmentCount);   // one S with r=4 → 1 + 4 = 5 segments
        Assert.Equal("v0", d.RepresentationId);
    }

    [Fact]
    public void Parse_NoVideoRepresentation_Throws()
    {
        const string audioOnly = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period><AdaptationSet contentType="audio" mimeType="audio/mp4">
                <Representation id="a0" codecs="mp4a.40.2" bandwidth="128000"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(audioOnly, "https://x/y.mpd"));
    }

    [Fact]
    public void Parse_NonH264VideoOnly_Throws()
    {
        const string hevc = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT4S">
              <Period><AdaptationSet contentType="video">
                <SegmentTemplate initialization="i.mp4" media="s-$Number$.m4s" duration="4" timescale="1"/>
                <Representation id="h" codecs="hvc1.1.6.L93.B0" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        var ex = Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(hevc, "https://x/y.mpd"));
        Assert.Contains("H.264", ex.Message);
    }

    [Fact]
    public void Parse_MalformedXml_ThrowsTyped()
        => Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse("<MPD><not-closed>", "https://x/y.mpd"));

    [Fact]
    public void Parse_RelativeMpdUrl_ThrowsTyped()
        => Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(AxinomMpd, "manifest.mpd"));

    [Fact]
    public void Parse_LiveMpd_IsRejected()
    {
        const string live = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic" availabilityStartTime="2026-01-01T00:00:00Z" timeShiftBufferDepth="PT12S">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028">
                <SegmentTemplate duration="2" timescale="1" startNumber="1" initialization="i.mp4" media="s-$Number$.m4s"/>
                <Representation id="v" bandwidth="300000"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        var ex = Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(live, "https://x/y.mpd"));
        Assert.Contains("live", ex.Message);
    }

    // ── templates the prefix + number + suffix model cannot express: rejected, never mangled ─────────────────────────

    [Fact]
    public void Parse_PaddedTemplateThatCrossesADigitBoundary_IsRejected()
    {
        // $Number%02d$ from 8: seg-08, seg-09, seg-10, ... - the prefix "seg-0" recovered from the first URL + "10" would
        // be "seg-010", a 404.
        const string padded = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT10S">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028">
                <SegmentTemplate initialization="i.mp4" media="seg-$Number%02d$.m4s" startNumber="8" duration="2" timescale="1"/>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        var ex = Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(padded, "https://cdn.example.com/v/manifest.mpd"));
        Assert.Contains("cannot express", ex.Message);
        Assert.Contains("digit boundary", ex.Message);
    }

    [Fact]
    public void Parse_PaddedTemplateAcrossNineToTen_IsRejectedWhateverItsWidth()
    {
        // $Number%05d$ over 1..12: seg-00009 then seg-00010. The width is never outgrown, but the first URL's prefix
        // "seg-0000" + "10" would be "seg-000010", so a fixed prefix cannot name segment 10.
        const string padded = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT24S">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028">
                <SegmentTemplate initialization="i.mp4" media="seg-$Number%05d$.m4s" startNumber="1" duration="2" timescale="1"/>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        var ex = Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(padded, "https://cdn.example.com/v/manifest.mpd"));
        Assert.Contains("digit boundary", ex.Message);
    }

    [Fact]
    public void Parse_PaddedTemplateWithAConstantDigitCount_ReproducesEverySegmentUrlExactly()
    {
        // $Number%05d$ over 1..4: every number has one digit, so the padding zeros fold into the prefix and the
        // concatenation is byte-exact.
        const string padded = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028">
                <SegmentTemplate initialization="i.mp4" media="seg-$Number%05d$.m4s" startNumber="1" duration="2" timescale="1"/>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;

        var d = DashDescriptorMapper.Parse(padded, "https://cdn.example.com/v/manifest.mpd");

        Assert.Equal(4, d.SegmentCount);
        for (int n = 1; n <= d.SegmentCount; n++)
            Assert.Equal($"https://cdn.example.com/v/seg-{n:D5}.m4s",
                d.SegmentBaseUrl + d.SegmentPrefix + (d.StartNumber + (n - 1)) + d.SegmentSuffix);
    }

    [Fact]
    public void Parse_TimeTemplate_IsRejected()
    {
        const string timeAddressed = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT6S">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028">
                <SegmentTemplate timescale="1000" initialization="i.mp4" media="seg-$Time$.m4s">
                  <SegmentTimeline><S t="0" d="2000" r="2"/></SegmentTimeline>
                </SegmentTemplate>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        var ex = Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(timeAddressed, "https://cdn.example.com/v/manifest.mpd"));
        Assert.Contains("$Time$", ex.Message);
    }

    [Fact]
    public void Parse_AMediaNameWithoutAnExtension_IsRejected()
    {
        // The native side defaults an empty suffix to ".m4s", which would not be this URL.
        const string bare = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT4S">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028">
                <SegmentTemplate initialization="i.mp4" media="seg-$Number$" duration="2" timescale="1"/>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(bare, "https://cdn.example.com/v/manifest.mpd"));
    }

    [Fact]
    public void Parse_APresentationTheEngineTruncated_IsRejectedNotPlayedShort()
    {
        // 5000 one-second segments exceed the engine's 4096-segment bound: the tail would silently vanish.
        const string longVod = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT5000S">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028">
                <SegmentTemplate initialization="i.mp4" media="seg-$Number$.m4s" duration="1" timescale="1"/>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        var ex = Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(longVod, "https://cdn.example.com/v/manifest.mpd"));
        Assert.Contains("4096", ex.Message);
    }

    [Fact]
    public void Parse_SeveralPeriods_AreRejectedNotPlayedAsTheFirst()
    {
        const string periods = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT40S">
              <Period id="p0" duration="PT20S"><AdaptationSet contentType="video" codecs="avc1.640028">
                <SegmentTemplate initialization="a-i.mp4" media="a-$Number$.m4s" duration="4" timescale="1"/>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
              <Period id="p1" start="PT20S" duration="PT20S"><AdaptationSet contentType="video" codecs="avc1.640028">
                <SegmentTemplate initialization="b-i.mp4" media="b-$Number$.m4s" duration="4" timescale="1"/>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;
        Assert.Throws<DashManifestException>(() => DashDescriptorMapper.Parse(periods, "https://cdn.example.com/v/manifest.mpd"));
    }

    // ── DRM init data: only a real PlayReady pssh box reaches the CDM ────────────────────────────────────────────────

    [Fact]
    public void Parse_PlayReadyElementWithOnlyAProObject_LeavesTheInitDataToTheInitSegment()
    {
        // <mspr:pro> is a bare PlayReady Object, not a pssh box: the native GenerateRequest("cenc") would be handed
        // garbage, so the descriptor carries no PSSH and the native side reads the protection from the init segment.
        const string proOnly = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013" xmlns:mspr="urn:microsoft:playready" mediaPresentationDuration="PT8S">
              <Period><AdaptationSet contentType="video" codecs="avc1.640028">
                <ContentProtection schemeIdUri="urn:mpeg:cenc:2013" cenc:default_KID="4060A865-8878-4267-9CBF-91AE5BAE1E72"/>
                <ContentProtection schemeIdUri="urn:uuid:9A04F079-9840-4286-AB92-E65BE0885F95">
                  <mspr:pro>QUJDRA==</mspr:pro>
                </ContentProtection>
                <SegmentTemplate initialization="i.mp4" media="seg-$Number$.m4s" startNumber="1" duration="4" timescale="1"/>
                <Representation id="v" bandwidth="1000000"/>
              </AdaptationSet></Period>
            </MPD>
            """;

        var d = DashDescriptorMapper.Parse(proOnly, "https://cdn.example.com/v/manifest.mpd");

        Assert.True(d.Pssh.IsEmpty);
        Assert.Equal("4060a865887842679cbf91ae5bae1e72", d.DefaultKid);
    }

    // ── the ABR ladder and the paired soundtrack must share the opened representation's segment grid ─────────────────

    [Fact]
    public void Parse_AnAlternateWithAnotherStartNumber_IsLeftOutOfTheLadder()
    {
        // The session swaps only init/base/prefix/suffix, so "high" (numbered from 7) would be fetched as segments 1..2.
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" codecs="avc1.640028">
                  <Representation id="low" bandwidth="500000" width="640" height="360">
                    <SegmentTemplate initialization="low/init.mp4" media="low/seg-$Number$.m4s" startNumber="1" duration="4" timescale="1"/>
                  </Representation>
                  <Representation id="high" bandwidth="3000000" width="1920" height="1080">
                    <SegmentTemplate initialization="high/init.mp4" media="high/seg-$Number$.m4s" startNumber="7" duration="4" timescale="1"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var d = DashDescriptorMapper.Parse(mpd, "https://cdn.example.com/v/manifest.mpd");

        Assert.Equal("low", d.RepresentationId);
        var track = Assert.Single(d.Catalog!.Tracks);
        var only = Assert.Single(track.Representations);
        Assert.Equal("low", only.Id);
        Assert.Equal("360p", track.Label);
    }

    [Fact]
    public void Parse_AnAlternateTheModelCannotExpress_IsLeftOutNotRejected()
    {
        // "high" has no file extension (not expressible); only the opened representation's failure rejects the manifest.
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" codecs="avc1.640028">
                  <Representation id="low" bandwidth="500000" width="640" height="360">
                    <SegmentTemplate initialization="low/init.mp4" media="low/seg-$Number$.m4s" startNumber="1" duration="4" timescale="1"/>
                  </Representation>
                  <Representation id="high" bandwidth="3000000" width="1920" height="1080">
                    <SegmentTemplate initialization="high/init.mp4" media="high/seg-$Number$" startNumber="1" duration="4" timescale="1"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var d = DashDescriptorMapper.Parse(mpd, "https://cdn.example.com/v/manifest.mpd");

        Assert.Equal("low", Assert.Single(Assert.Single(d.Catalog!.Tracks).Representations).Id);
    }

    [Theory]
    [InlineData(1, 4, true)]    // the same grid: kept
    [InlineData(1, 3, false)]   // 3 s segments over PT8S: 3 segments, but their boundaries (3 s, 6 s) are off the 4 s grid
    [InlineData(5, 4, false)]   // another StartNumber: the native shares one numbering
    [InlineData(1, 2, false)]   // 4 segments against the video's 2: the native shares one count
    public void Parse_AudioOffTheVideoGrid_DegradesToVideoOnly(int audioStartNumber, int audioSegmentSeconds, bool keepsAudio)
    {
        string mpd = $"""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" codecs="avc1.640028">
                  <SegmentTemplate initialization="v-init.mp4" media="v-$Number$.m4s" startNumber="1" duration="4" timescale="1"/>
                  <Representation id="v" bandwidth="1000000" width="1280" height="720"/>
                </AdaptationSet>
                <AdaptationSet contentType="audio" lang="en" codecs="mp4a.40.2">
                  <SegmentTemplate initialization="a-init.mp4" media="a-$Number$.m4s" startNumber="{audioStartNumber}" duration="{audioSegmentSeconds}" timescale="1"/>
                  <Representation id="a" bandwidth="128000"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var d = DashDescriptorMapper.Parse(mpd, "https://cdn.example.com/v/manifest.mpd");

        Assert.Equal("https://cdn.example.com/v/v-init.mp4", d.InitUrl);
        Assert.Equal(2, d.SegmentCount);
        if (keepsAudio)
        {
            Assert.Equal("https://cdn.example.com/v/a-init.mp4", d.AudioInitUrl);
            Assert.Equal("mp4a.40.2", d.AudioCodecs);
            Assert.Equal(2, d.Catalog!.Tracks.Count);
        }
        else
        {
            Assert.Null(d.AudioInitUrl);
            Assert.Null(d.AudioSegmentBaseUrl);
            Assert.Null(d.AudioSegmentPrefix);
            Assert.Null(d.AudioSegmentSuffix);
            Assert.Null(d.AudioCodecs);
            Assert.Single(d.Catalog!.Tracks);
        }
    }

    [Fact]
    public void Parse_AudioWithOneExtraTrailingSegmentOnTheSameGrid_IsKept()
    {
        // The count may differ by one when the boundaries agree: the audio ends with a 30 ms tail (the gate clip does the same).
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" codecs="avc1.640028">
                  <SegmentTemplate initialization="v-init.mp4" media="v-$Number$.m4s" startNumber="1" duration="4" timescale="1"/>
                  <Representation id="v" bandwidth="1000000" width="1280" height="720"/>
                </AdaptationSet>
                <AdaptationSet contentType="audio" lang="en" codecs="mp4a.40.2">
                  <SegmentTemplate initialization="a-init.mp4" media="a-$Number$.m4s" startNumber="1" timescale="1000">
                    <SegmentTimeline><S t="0" d="4000"/><S d="3970"/><S d="30"/></SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="a" bandwidth="128000"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var d = DashDescriptorMapper.Parse(mpd, "https://cdn.example.com/v/manifest.mpd");

        Assert.Equal(2, d.SegmentCount);
        Assert.Equal("https://cdn.example.com/v/a-init.mp4", d.AudioInitUrl);
        Assert.Equal(2, d.Catalog!.Tracks.Count);
    }

    [Fact]
    public void Parse_AnAlternateOnAnotherSegmentLength_IsLeftOutOfTheLadder()
    {
        // Same StartNumber and SegmentCount (2 segments over PT8S) but 6 s segments: an ABR switch at index 1 would fetch
        // 6-12 s instead of 4-8 s, so "high" stays out of the catalog while the descriptor still maps.
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" codecs="avc1.640028">
                  <Representation id="low" bandwidth="500000" width="640" height="360">
                    <SegmentTemplate initialization="low/init.mp4" media="low/seg-$Number$.m4s" startNumber="1" duration="4" timescale="1"/>
                  </Representation>
                  <Representation id="high" bandwidth="3000000" width="1920" height="1080">
                    <SegmentTemplate initialization="high/init.mp4" media="high/seg-$Number$.m4s" startNumber="1" duration="6" timescale="1"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var d = DashDescriptorMapper.Parse(mpd, "https://cdn.example.com/v/manifest.mpd");

        Assert.Equal("low", d.RepresentationId);
        var track = Assert.Single(d.Catalog!.Tracks);
        Assert.Equal("low", Assert.Single(track.Representations).Id);
        Assert.Equal("360p", track.Label);
    }

    // ── the seek index hints (segment length, presentation duration, keyframe-aligned segment starts) ────────────────

    [Fact]
    public void Parse_AxinomSingleKey_ExtractsTheSeekIndexHints()
    {
        var d = DashDescriptorMapper.Parse(AxinomMpd, AxinomMpdUrl);

        Assert.Equal(5_000, d.SegmentLengthMs);   // @duration 60000 / @timescale 12000
        Assert.Equal(30_000, d.DurationMs);       // PT30S
        Assert.True(d.SegmentsStartWithKeyframe); // startWithSAP="1"
    }

    [Theory]
    [InlineData("""startWithSAP="1" """, true)]
    [InlineData("""startWithSAP="2" """, true)]
    [InlineData("""startWithSAP="3" """, false)]
    [InlineData("""startWithSAP="0" """, false)]
    [InlineData("", true)]   // undeclared: the DASH-IF segment-template profiles require it
    public void Parse_OnlyStartWithSapOneOrTwo_PromisesKeyframeSegmentStarts(string sapAttribute, bool startsWithKeyframe)
    {
        string mpd = $"""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT8S">
              <Period>
                <AdaptationSet contentType="video" {sapAttribute}>
                  <SegmentTemplate initialization="init.mp4" media="seg-$Number$.m4s" startNumber="1" duration="4" timescale="1"/>
                  <Representation id="v" codecs="avc1.640028" bandwidth="1000000" width="1280" height="720"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var d = DashDescriptorMapper.Parse(mpd, "https://cdn.example.com/v/manifest.mpd");

        Assert.Equal(startsWithKeyframe, d.SegmentsStartWithKeyframe);
        Assert.Equal(4_000, d.SegmentLengthMs);
        Assert.Equal(2, d.SegmentCount);
    }

    [Fact]
    public void Parse_TheGateClipFixture_ReadsItsTemplateGrid()
    {
        // The seek-gate clip (Fixtures/video/README.md): an ffmpeg DASH export, 5 × 4 s segments on a 2 s GOP, named
        // chunk-stream0-00001.m4s ($Number%05d$). Its numbers 1..5 all have one digit, so the padding zeros fold into the
        // prefix and base + prefix + n + suffix still names the real files.
        string xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "video", "gate.mpd"));

        var d = DashDescriptorMapper.Parse(xml, "https://cdn.test/gate/gate.mpd");

        Assert.Equal("https://cdn.test/gate/init-stream0.m4s", d.InitUrl);
        Assert.Equal("https://cdn.test/gate/", d.SegmentBaseUrl);
        Assert.Equal("chunk-stream0-0000", d.SegmentPrefix);
        Assert.Equal(".m4s", d.SegmentSuffix);
        Assert.Equal(1, d.StartNumber);
        Assert.Equal(5, d.SegmentCount);          // S r="4"
        Assert.Equal(4_000, d.SegmentLengthMs);   // S@d 61440 / @timescale 15360
        Assert.Equal(20_000, d.DurationMs);       // PT20.0S
        Assert.True(d.SegmentsStartWithKeyframe);
        Assert.Equal("0", d.RepresentationId);
        Assert.Equal("avc1.64001e", d.Codecs);
        Assert.True(d.Pssh.IsEmpty);              // a clear clip carries no PlayReady protection
        Assert.Null(d.DefaultKid);

        for (int n = 1; n <= 5; n++)
            Assert.Equal($"https://cdn.test/gate/chunk-stream0-{n:D5}.m4s",
                d.SegmentBaseUrl + d.SegmentPrefix + n + d.SegmentSuffix);

        // The paired soundtrack rides the same grid.
        Assert.Equal("https://cdn.test/gate/init-stream1.m4s", d.AudioInitUrl);
        Assert.Equal("https://cdn.test/gate/", d.AudioSegmentBaseUrl);
        Assert.Equal("chunk-stream1-0000", d.AudioSegmentPrefix);
        Assert.Equal(".m4s", d.AudioSegmentSuffix);
        Assert.Equal("mp4a.40.2", d.AudioCodecs);
        Assert.Equal(2, d.Catalog!.Tracks.Count);
        Assert.Equal(FluentGpu.Media.TrackKind.Audio, d.Catalog.Tracks[1].Kind);
    }
}
