using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

// The music-video data layer end-to-end over crafted protobuf (no network). The golden payload is the REAL
// VIDEO_ASSOCIATIONS bytes captured from spclient (base64 of the decompressed Any.value), so the proto shape is
// pinned against the wire; the rest exercises detect → cache → HasVideo + the etag/304 round-trip.
public class VideoAssociationTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    static System.Threading.CancellationToken CT => TestContext.Current.CancellationToken;

    // Real captured VIDEO_ASSOCIATIONS payload for spotify:track:2ZTU8atPwouhoQSvxv9aQj → associated video track
    // 3dzYeVS4L1mfAdqlxYxB12 with three file variants (2560x1440 / 1280x720 / 2560x1440).
    const string RealPayloadB64 =
        "CogBCiRzcG90aWZ5OnRyYWNrOjNkelllVlM0TDFtZkFkcWx4WXhCMTISYAoeChSrZ0LTAABTt1GrEGocjt1j+pNFMBAAGIAUIKALCh4KFKtnQtMAAFK3UasQahyO3WP6k0UwEAIYgAog0AUKHgoUq2dC0wAAU7dRqxBqHI7dY/qTRTAQBBiAFCCgCw==";

    [Fact]
    public void VideoAssociations_RealPayload_ParsesAgainstTheWire()
    {
        var va = Xm.VideoAssociations.Parser.ParseFrom(ByteString.FromBase64(RealPayloadB64));
        Assert.NotNull(va.Association);
        Assert.Equal("spotify:track:3dzYeVS4L1mfAdqlxYxB12", va.Association.AssociatedUri);

        var files = va.Association.Files.File;
        Assert.Equal(3, files.Count);
        Assert.Equal("ab6742d3000053b751ab106a1c8edd63fa934530", Convert.ToHexStringLower(files[0].FileId.Span));
        Assert.Equal((0, 2560, 1440), (files[0].Variant, files[0].Width, files[0].Height));
        Assert.Equal("ab6742d3000052b751ab106a1c8edd63fa934530", Convert.ToHexStringLower(files[1].FileId.Span));
        Assert.Equal((2, 1280, 720), (files[1].Variant, files[1].Width, files[1].Height));
        Assert.Equal((4, 2560, 1440), (files[2].Variant, files[2].Width, files[2].Height));
    }

    [Fact]
    public async Task Detect_ProjectsHasVideo_FileMap_AndNegativeCache()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("HAS"));
        store.UpsertTrack(Trk("NONE"));

        var resp = new Xm.BatchedExtensionResponse();
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.VideoAssociations };
        array.Header = new Xm.EntityExtensionDataArrayHeader { OfflineTtlInSeconds = 2592000 };
        array.ExtensionData.Add(Entry("spotify:track:HAS", 200, "etagHAS",
            VaPayload("spotify:track:VID", ("ab6742d3000053b751ab106a1c8edd63fa934530", 0, 2560, 1440))));
        array.ExtensionData.Add(Entry("spotify:track:NONE", 404, null, null));
        resp.ExtendedMetadata.Add(array);

        var svc = Service(store, (_, _) => new HttpResp(200, new Dictionary<string, string>(), resp.ToByteArray()));
        await svc.DetectAsync(new[] { "spotify:track:HAS", "spotify:track:NONE" }, CT);

        var has = store.GetVideoAssociation("spotify:track:HAS");
        Assert.NotNull(has);
        Assert.True(has!.HasVideo);
        Assert.Equal("spotify:track:VID", has.CounterpartUri);
        Assert.Equal("etagHAS", has.Etag);
        Assert.Equal(2592000, has.OfflineTtlSeconds);
        var f = Assert.Single(has.Files);
        Assert.Equal("ab6742d3000053b751ab106a1c8edd63fa934530", f.FileIdHex);
        Assert.Equal((2560, 1440), (f.Width, f.Height));
        Assert.True(store.GetTrack("spotify:track:HAS")!.HasVideo);   // the row indicator flips on

        var none = store.GetVideoAssociation("spotify:track:NONE");
        Assert.NotNull(none);
        Assert.False(none!.HasVideo);                                 // negative cached (404), so we stop re-asking
        Assert.False(store.GetTrack("spotify:track:NONE")!.HasVideo);
    }

    [Fact]
    public async Task Detect_SendsCachedEtag_ForConditionalRevalidation()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("HAS"));
        // A stale cached association carrying an etag → the next detect must send it (so the server can 304).
        store.UpsertVideoAssociation(VideoAssociation.None("spotify:track:HAS", "prevtag", DateTimeOffset.UtcNow.AddDays(-1), 0));

        HttpReq? captured = null;
        var svc = Service(store, (req, _) => { captured = req; return new HttpResp(200, new Dictionary<string, string>(), new Xm.BatchedExtensionResponse().ToByteArray()); });
        await svc.DetectAsync(new[] { "spotify:track:HAS" }, CT);

        Assert.NotNull(captured);
        var req = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(captured!.Body));
        var query = Assert.Single(Assert.Single(req.EntityRequest).Query);
        Assert.Equal(Xm.ExtensionKind.VideoAssociations, query.ExtensionKind);
        Assert.Equal("prevtag", query.Etag);   // the etag rode the ExtensionQuery
    }

    [Fact]
    public async Task Detect_304_KeepsCachedRecord_AndBumpsFreshness()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("HAS"));
        var stale = new VideoAssociation("spotify:track:HAS", true, "spotify:track:VID",
            new[] { new VideoFileRef("abcd", 0, 2560, 1440) }, "v1", DateTimeOffset.UtcNow.AddDays(-1), 2592000);
        store.UpsertVideoAssociation(stale);

        var resp = new Xm.BatchedExtensionResponse();
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.VideoAssociations };
        array.ExtensionData.Add(Entry("spotify:track:HAS", 304, "v1", null));   // not modified
        resp.ExtendedMetadata.Add(array);

        var svc = Service(store, (_, _) => new HttpResp(200, new Dictionary<string, string>(), resp.ToByteArray()));
        await svc.DetectAsync(new[] { "spotify:track:HAS" }, CT);

        var a = store.GetVideoAssociation("spotify:track:HAS");
        Assert.NotNull(a);
        Assert.True(a!.HasVideo);
        Assert.Equal("spotify:track:VID", a.CounterpartUri);
        Assert.Single(a.Files);                                                 // payload preserved (not dropped)
        Assert.True(DateTimeOffset.UtcNow - a.FetchedAt < TimeSpan.FromMinutes(1));   // freshness bumped
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────
    static SpotifyVideoService Service(IStore store, Func<HttpReq, int, HttpResp> responder)
        => new(new ExtendedMetadataSource(new FakeExchange(responder), () => "https://spclient.test", () => Ctx), store);

    static Track Trk(string id) => new(id, "spotify:track:" + id, "T " + id,
        [new ArtistRef("a", "spotify:artist:a", "A")], new AlbumRef("al", "spotify:album:al", "Al"), 1000, false, null);

    static Xm.EntityExtensionData Entry(string uri, int status, string? etag, ByteString? payload)
    {
        var hdr = new Xm.EntityExtensionDataHeader { StatusCode = status };
        if (etag != null) hdr.Etag = etag;
        var d = new Xm.EntityExtensionData { EntityUri = uri, Header = hdr };
        if (payload != null) d.ExtensionData = new Any { Value = payload };   // type_url is ignored by the source
        return d;
    }

    static ByteString VaPayload(string counterpartUri, params (string FileHex, int Variant, int W, int H)[] files)
    {
        var group = new Xm.VideoFileGroup();
        foreach (var (hex, variant, w, h) in files)
            group.File.Add(new Xm.VideoFile { FileId = ByteString.CopyFrom(Convert.FromHexString(hex)), Variant = variant, Width = w, Height = h });
        var va = new Xm.VideoAssociations { Association = new Xm.Association { AssociatedUri = counterpartUri, Files = group } };
        return va.ToByteString();
    }
}
