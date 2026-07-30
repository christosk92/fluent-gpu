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

        var none = store.GetVideoAssociation("spotify:track:NONE");
        Assert.NotNull(none);
        Assert.False(none!.HasVideo);                                 // negative cached (404), so we stop re-asking
        // The association IS what the row indicator reads (VideoPresence). Nothing is mirrored onto the track row, so
        // there is no second copy here to assert — and none that could drift out of step with this one.
    }

    [Fact]
    public async Task Detect_SendsCachedEtag_ForConditionalRevalidation()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("HAS"));
        store.UpsertTrack(Trk("NONE"));
        // A stale cached POSITIVE carrying an etag → the next detect must send it (so the server can 304 and save
        // re-shipping the payload).
        store.UpsertVideoAssociation(new VideoAssociation("spotify:track:HAS", true, "spotify:track:VID",
            new[] { new VideoFileRef("abcd", 0, 2560, 1440) }, "prevtag", DateTimeOffset.UtcNow.AddDays(-1), 2592000));
        // A stale cached NEGATIVE must send NO etag: there is no payload for a 304 to save, and a conditional would
        // only have the server confirm the very miss we are re-testing — the mechanism that used to pin a wrong
        // "no video" in place (VideoAssociation.RevalidationEtag).
        store.UpsertVideoAssociation(VideoAssociation.None("spotify:track:NONE", "negtag", DateTimeOffset.UtcNow.AddDays(-1), 0));

        HttpReq? captured = null;
        var svc = Service(store, (req, _) => { captured = req; return new HttpResp(200, new Dictionary<string, string>(), new Xm.BatchedExtensionResponse().ToByteArray()); });
        await svc.DetectAsync(new[] { "spotify:track:HAS", "spotify:track:NONE" }, CT);

        Assert.NotNull(captured);
        var req = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(captured!.Body));
        Assert.Equal(2, req.EntityRequest.Count);
        foreach (var er in req.EntityRequest)
        {
            // Each entity asks two kinds: 99 (conditional iff the cached verdict is positive) + the co-batched 182 hint.
            Assert.Equal(2, er.Query.Count);
            var query = Assert.Single(er.Query, q => q.ExtensionKind == Xm.ExtensionKind.VideoAssociations);
            Assert.Equal(er.EntityUri == "spotify:track:HAS" ? "prevtag" : "", query.Etag);
            Assert.Contains(er.Query, q => q.ExtensionKind == Xm.ExtensionKind.ConsumptionExperienceTrait);
        }
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

    // ── the ≤300-uri batch slice ─────────────────────────────────────────────────────────────────────────────────────
    // Nothing below the service bounds a batch (MetadataChunking splits by BYTES, ExtensionEtagCache takes the whole
    // list), so a big container must be sliced HERE or it goes out as one 10k-entity request body.
    [Fact]
    public async Task Detect_SlicesLargeBatches_At300UrisPerRequest()
    {
        var store = new InMemoryStore();
        var uris = new List<string>(701);
        for (int i = 0; i < 701; i++) uris.Add($"spotify:track:{i:D22}");

        var counts = new List<int>();
        var svc = Service(store, (req, _) =>
        {
            var parsed = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body));
            counts.Add(parsed.EntityRequest.Count);   // one EntityRequest per entity (kinds are grouped INSIDE it)
            return new HttpResp(200, new Dictionary<string, string>(), new Xm.BatchedExtensionResponse().ToByteArray());
        });
        await svc.DetectAsync(uris, CT);

        Assert.Equal(new[] { 300, 300, 101 }, counts);
    }

    [Fact]
    public async Task Detect_UnderTheCap_StaysOneRequest()
    {
        var store = new InMemoryStore();
        var uris = new List<string>(300);
        for (int i = 0; i < 300; i++) uris.Add($"spotify:track:{i:D22}");

        int posts = 0;
        var svc = Service(store, (_, _) => { posts++; return new HttpResp(200, new Dictionary<string, string>(), new Xm.BatchedExtensionResponse().ToByteArray()); });
        await svc.DetectAsync(uris, CT);

        Assert.Equal(1, posts);
    }

    // ── canonical-id recovery (alias/relinked ids 404 on kind 99) ─────────────────────────────────────────────────────
    const string GidHex = "3c14b1c9a7d94f0e9d2b8a6f5e4c3b2a";

    [Fact]
    public async Task Detect_AliasId_RecoversThroughCanonicalUri_AndStoresUnderTheAlias()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("ALIAS"));

        var posts = new List<HashSet<Xm.ExtensionKind>>();
        var svc = Service(store, (req, _) =>
        {
            var kinds = KindsOf(req);
            posts.Add(kinds);
            var resp = new Xm.BatchedExtensionResponse();
            if (kinds.Contains(Xm.ExtensionKind.TrackV4))
            {
                resp.ExtendedMetadata.Add(Arr(Xm.ExtensionKind.TrackV4,
                    Entry("spotify:track:ALIAS", 200, null, TrackV4Payload("spotify:track:CANON"))));
                resp.ExtendedMetadata.Add(Arr(Xm.ExtensionKind.PlaybackTrait,
                    Entry("spotify:track:ALIAS", 200, null, PlaybackTraitPayload(GidHex))));
            }
            else if (kinds.Contains(Xm.ExtensionKind.ConsumptionExperienceTrait))
            {
                resp.ExtendedMetadata.Add(Arr(Xm.ExtensionKind.VideoAssociations, Entry("spotify:track:ALIAS", 404, null, null)));
                resp.ExtendedMetadata.Add(Arr(Xm.ExtensionKind.ConsumptionExperienceTrait,
                    Entry("spotify:track:ALIAS", 200, null, CePayload(hasVideo: true))));
            }
            else   // the recovery batch: kind 99 on the CANONICAL id
            {
                resp.ExtendedMetadata.Add(Arr(Xm.ExtensionKind.VideoAssociations, Entry("spotify:track:CANON", 200, "etagCANON",
                    VaPayload("spotify:track:VID", ("ab6742d3000053b751ab106a1c8edd63fa934530", 0, 2560, 1440)))));
            }
            return new HttpResp(200, new Dictionary<string, string>(), resp.ToByteArray());
        });
        await svc.DetectAsync(new[] { "spotify:track:ALIAS" }, CT);

        Assert.Equal(3, posts.Count);   // detect (99+182) → canonical lookup (TrackV4+212) → kind 99 on the canonical id

        var a = store.GetVideoAssociation("spotify:track:ALIAS");   // keyed by the REQUESTED uri, not the canonical one
        Assert.NotNull(a);
        Assert.True(a!.HasVideo);
        Assert.Equal("spotify:track:VID", a.CounterpartUri);
        Assert.Equal("ab6742d3000053b751ab106a1c8edd63fa934530", Assert.Single(a.Files).FileIdHex);
        Assert.Equal(GidHex, a.VideoGidHex);        // kind 212 field 2 → Connect's associated_video_id
        Assert.Null(a.Etag);                        // the canonical entity's etag must never ride the alias row
        Assert.Null(store.GetVideoAssociation("spotify:track:CANON"));
        Assert.Equal("spotify:track:CANON", store.GetTrack("spotify:track:ALIAS")!.CanonicalUri);
    }

    [Fact]
    public void GetVideoAssociation_MissBridge_RetriesCanonicalUri()
    {
        var store = new InMemoryStore();
        store.UpsertVideoAssociation(new VideoAssociation("spotify:track:CANON", true, "spotify:track:VID",
            new[] { new VideoFileRef("abcd", 0, 2560, 1440) }, "etag", DateTimeOffset.UtcNow, 2592000));
        store.UpsertTrack(new Track("ALIAS", "spotify:track:ALIAS", "A", [], new AlbumRef("", "", ""), 0, false, null,
            CanonicalUri: "spotify:track:CANON"));

        Assert.Null(store.GetVideoAssociation("spotify:track:MISSING"));
        var bridged = store.GetVideoAssociation("spotify:track:ALIAS");
        Assert.NotNull(bridged);
        Assert.Equal("spotify:track:CANON", bridged!.Uri);
        Assert.True(bridged.HasVideo);
    }

    [Fact]
    public async Task Detect_404_WithoutTheConsumptionHint_DoesNotAskForACanonical()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("NONE"));

        int posts = 0;
        var svc = Service(store, (req, _) =>
        {
            posts++;
            var kinds = KindsOf(req);
            Assert.DoesNotContain(Xm.ExtensionKind.TrackV4, kinds);   // no canonical lookup may be attempted
            var resp = new Xm.BatchedExtensionResponse();
            resp.ExtendedMetadata.Add(Arr(Xm.ExtensionKind.VideoAssociations, Entry("spotify:track:NONE", 404, null, null)));
            resp.ExtendedMetadata.Add(Arr(Xm.ExtensionKind.ConsumptionExperienceTrait,
                Entry("spotify:track:NONE", 200, null, CePayload(hasVideo: false))));
            return new HttpResp(200, new Dictionary<string, string>(), resp.ToByteArray());
        });
        await svc.DetectAsync(new[] { "spotify:track:NONE" }, CT);

        Assert.Equal(1, posts);
        Assert.False(store.GetVideoAssociation("spotify:track:NONE")!.HasVideo);
    }

    // ── the ONE shared kind-99 fold ──────────────────────────────────────────────────────────────────────────────────
    // The detect batch, the single-track resolve AND the expand drawer (SpotifyTrackExpansionService) all fold their
    // fetches through SpotifyVideoService.Fold, so whichever path fetched a payload, the same record lands in the same
    // plane. This is what heals a row that showed "no video" the moment its expand fetch returns — and what makes the
    // row indicator and the drawer structurally unable to disagree.

    [Fact]
    public void Fold_WritesThePlaneFromABareResult_WithNoTrackRowInvolved()
    {
        var store = new InMemoryStore();   // deliberately NO track row upserted — the plane needs none
        var res = new ExtendedMetadataSource.ExtensionResult(200, "etag1", 2592000, ByteString.FromBase64(RealPayloadB64));

        SpotifyVideoService.Fold(store, "spotify:track:2ZTU8atPwouhoQSvxv9aQj", res, DateTimeOffset.UtcNow);

        var a = store.GetVideoAssociation("spotify:track:2ZTU8atPwouhoQSvxv9aQj");
        Assert.NotNull(a);
        Assert.True(a!.HasVideo);
        Assert.Equal("spotify:track:3dzYeVS4L1mfAdqlxYxB12", a.CounterpartUri);
        Assert.Equal(3, a.Files.Count);
        Assert.Equal("etag1", a.Etag);
    }

    [Fact]
    public void Fold_MissingResultWritesNothing_A404WritesTheNegative()
    {
        var store = new InMemoryStore();
        // default(ExtensionResult) (Status 0) is what a TryGetValue miss hands the drawer's unconditional fold call —
        // it must leave the plane untouched, never write a bogus negative.
        SpotifyVideoService.Fold(store, "spotify:track:X", default, DateTimeOffset.UtcNow);
        Assert.Null(store.GetVideoAssociation("spotify:track:X"));

        SpotifyVideoService.Fold(store, "spotify:track:X",
            new ExtendedMetadataSource.ExtensionResult(404, null, 2592000, null), DateTimeOffset.UtcNow);
        var a = store.GetVideoAssociation("spotify:track:X");
        Assert.NotNull(a);
        Assert.False(a!.HasVideo);
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

    static Xm.EntityExtensionDataArray Arr(Xm.ExtensionKind kind, Xm.EntityExtensionData entry)
    {
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        array.ExtensionData.Add(entry);
        return array;
    }

    static HashSet<Xm.ExtensionKind> KindsOf(HttpReq req)
    {
        var parsed = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body));
        var kinds = new HashSet<Xm.ExtensionKind>();
        foreach (var er in parsed.EntityRequest)
            foreach (var q in er.Query) kinds.Add(q.ExtensionKind);
        return kinds;
    }

    // Kind 182 on the wire: field 4 (length-delimited) is the experience-id blob; 0x02 = music video.
    static ByteString CePayload(bool hasVideo)
        => hasVideo
            ? ByteString.CopyFrom(new byte[] { 0x22, 0x03, 0x01, 0x02, 0x04 })
            : ByteString.CopyFrom(new byte[] { 0x22, 0x02, 0x01, 0x04 });

    // Kind 212 on the wire: field 2 (length-delimited) carries the associated video's 16-byte gid.
    static ByteString PlaybackTraitPayload(string gidHex)
    {
        var gid = Convert.FromHexString(gidHex);
        var bytes = new byte[gid.Length + 2];
        bytes[0] = 0x12;
        bytes[1] = (byte)gid.Length;
        gid.CopyTo(bytes, 2);
        return ByteString.CopyFrom(bytes);
    }

    static ByteString TrackV4Payload(string canonicalUri)
        => new Wavee.Protocol.Metadata.Track { Name = "aliased", CanonicalUri = canonicalUri }.ToByteString();

    static ByteString VaPayload(string counterpartUri, params (string FileHex, int Variant, int W, int H)[] files)
    {
        var group = new Xm.VideoFileGroup();
        foreach (var (hex, variant, w, h) in files)
            group.File.Add(new Xm.VideoFile { FileId = ByteString.CopyFrom(Convert.FromHexString(hex)), Variant = variant, Width = w, Height = h });
        var va = new Xm.VideoAssociations { Association = new Xm.Association { AssociatedUri = counterpartUri, Files = group } };
        return va.ToByteString();
    }
}
