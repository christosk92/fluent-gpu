using System;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>Unit tests for the net-new <see cref="DashManifestParser"/>: parse a small Axinom-style PlayReady MPD (offline,
/// no network) and assert the init/media template, segment range, PSSH, and default KID are extracted correctly, plus
/// <c>$RepresentationID$</c> substitution + SegmentTimeline counting + BaseURL resolution.</summary>
public sealed class DashManifestParserTests
{
    private const string AxinomMpdUrl =
        "https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/manifest.mpd";

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
                <cenc:pssh>QUJDRA==</cenc:pssh>
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
        var d = DashManifestParser.Parse(AxinomMpd, AxinomMpdUrl);

        const string basePath = "https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/";
        Assert.Equal(basePath + "video-H264-720-2100k_init.mp4", d.InitUrl);
        Assert.Equal(basePath, d.SegmentBaseUrl);
        Assert.Equal("video-H264-720-2100k_", d.SegmentPrefix);
        Assert.Equal(".m4s", d.SegmentSuffix);
        Assert.Equal(1, d.StartNumber);
        Assert.Equal(6, d.SegmentCount);   // PT30S / (60000/12000 = 5s) = 6 segments

        Assert.Equal(new byte[] { 0x41, 0x42, 0x43, 0x44 }, d.Pssh.ToArray());   // base64 "QUJDRA==" == "ABCD"
        Assert.Equal("4060a865887842679cbf91ae5bae1e72", d.DefaultKid);          // dashless, lowercase
        Assert.Contains("avc1", d.Codecs);
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
        var d = DashManifestParser.Parse(TimelineMpd, "https://cdn.example.com/vod/manifest.mpd");

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
        Assert.Throws<DashManifestException>(() => DashManifestParser.Parse(audioOnly, "https://x/y.mpd"));
    }

    [Fact]
    public void Parse_MalformedXml_ThrowsTyped()
        => Assert.Throws<DashManifestException>(() => DashManifestParser.Parse("<MPD><not-closed>", "https://x/y.mpd"));
}
