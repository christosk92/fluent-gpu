using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Pb = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

// The REAL extended-metadata source, exercised end-to-end (build request → parse response → project) over crafted
// protobuf — the same wire shape spclient returns. No network needed; the live POST is the only unverifiable part.
public class ExtendedMetadataSourceTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    static byte[] Bytes(byte fill) { var a = new byte[16]; Array.Fill(a, fill); return a; }
    static ByteString Gid(byte fill) => ByteString.CopyFrom(Bytes(fill));

    static byte[] CraftTrackResponse(string name, int durationMs, bool isExplicit, bool includeAlbumCover = false)
    {
        var track = new Pb.Track { Gid = Gid(0x11), Name = name, Duration = durationMs, Explicit = isExplicit };
        track.Artist.Add(new Pb.Artist { Gid = Gid(0xAA), Name = "Artist One" });
        track.Album = new Pb.Album { Gid = Gid(0xBB), Name = "Album One" };
        track.ExternalId.Add(new Pb.ExternalId { Type = "isrc", Id = "USRC17607839" });   // field 10 → projected Track.Isrc
        if (includeAlbumCover)
        {
            track.Album.CoverGroup = new Pb.ImageGroup();
            track.Album.CoverGroup.Image.Add(new Pb.Image
            {
                FileId = Gid(0xCC),
                Size = Pb.Image.Types.Size.Default,
                Width = 300,
                Height = 300,
            });
        }

        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.TrackV4 };
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:track:x", ExtensionData = Any.Pack(track) });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);
        return resp.ToByteArray();
    }

    [Fact]
    public void ProjectResponse_ParsesTrackProto_IntoDomain()
    {
        var store = new InMemoryStore();
        ExtendedMetadataSource.ProjectResponse(CraftTrackResponse("Real Song", 234000, true), store);

        var t = Assert.Single(store.QueryTracks());   // uri is base62(gid), so look it up via the store
        Assert.Equal("Real Song", t.Title);
        Assert.Equal(234000, t.DurationMs);
        Assert.True(t.IsExplicit);
        Assert.Equal("Artist One", t.Artists[0].Name);
        Assert.StartsWith("spotify:artist:", t.Artists[0].Uri);
        Assert.Equal("Album One", t.Album.Name);
        Assert.StartsWith("spotify:track:", t.Uri);
    }

    [Fact]
    public void ProjectResponse_ExtractsIsrc_FromExternalId()
    {
        var store = new InMemoryStore();
        ExtendedMetadataSource.ProjectResponse(CraftTrackResponse("Real Song", 234000, true), store);
        // The LeanTrack parser now reads Track.external_id (field 10) instead of discarding it.
        Assert.Equal("USRC17607839", Assert.Single(store.QueryTracks()).Isrc);
    }

    [Fact]
    public void ProjectResponse_ProjectsTrackAlbumCover()
    {
        var store = new InMemoryStore();
        ExtendedMetadataSource.ProjectResponse(CraftTrackResponse("Cover Song", 234000, false, includeAlbumCover: true), store);

        var t = Assert.Single(store.QueryTracks());
        Assert.NotNull(t.Image);
        Assert.StartsWith("https://i.scdn.co/image/", t.Image!.Url);
        Assert.Equal(300, t.Image.Width);
        Assert.Equal(300, t.Image.Height);
    }

    [Fact]
    public void ProjectResponse_ProjectsAlbumAndArtist()
    {
        var store = new InMemoryStore();

        var album = new Pb.Album { Gid = Gid(0x01), Name = "The Album", Date = new Pb.Date { Year = 2021 } };
        album.Artist.Add(new Pb.Artist { Gid = Gid(0x02), Name = "AA" });
        var albumArray = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.AlbumV4 };
        albumArray.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:album:x", ExtensionData = Any.Pack(album) });

        var artist = new Pb.Artist { Gid = Gid(0x03), Name = "The Artist" };
        var artistArray = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.ArtistV4 };
        artistArray.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:artist:x", ExtensionData = Any.Pack(artist) });

        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(albumArray);
        resp.ExtendedMetadata.Add(artistArray);
        ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);

        var al = store.GetAlbum("spotify:album:" + Base62.Encode(Bytes(0x01)));   // uri = base62(gid), the source's scheme
        Assert.NotNull(al);
        Assert.Equal("The Album", al!.Name);
        Assert.Equal(2021, al.Year);
        Assert.Equal("The Artist", store.GetArtist("spotify:artist:" + Base62.Encode(Bytes(0x03)))!.Name);
    }

    [Fact]
    public async Task FetchAsync_BuildsBatchedPost_AndProjects()
    {
        var store = new InMemoryStore();
        var respBytes = CraftTrackResponse("Fetched Track", 1000, false);
        HttpReq? captured = null;
        var http = new FakeExchange((req, _) => { captured = req; return new HttpResp(200, new Dictionary<string, string>(), respBytes); });
        var src = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);

        await src.FetchAsync([EntityRef.Parse("spotify:track:x")], store, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("POST", captured!.Method);
        Assert.EndsWith("/extended-metadata/v0/extended-metadata", captured.Url);
        Assert.Equal("gzip", captured.Headers["Content-Encoding"]);       // request body gzipped
        Assert.Equal("application/protobuf", captured.Headers["Content-Type"]);
        Assert.NotNull(captured.Body);
        Assert.Equal("Fetched Track", Assert.Single(store.QueryTracks()).Title);
    }

    [Fact]
    public void GzipRequest_RoundTripsAValidBatchedRequest()
    {
        var entities = new[]
        {
            EntityRef.Parse("spotify:track:a"),
            EntityRef.Parse("spotify:album:b"),
            EntityRef.Parse("spotify:episode:c"),       // now a supported extended-metadata kind (EPISODE_V4)
            EntityRef.Parse("spotify:playlist:skip"),   // unsupported (playlists use playlist4, not extended-metadata) → skipped
        };
        var gz = ExtendedMetadataSource.GzipRequest(entities, 0, 4, Ctx);
        Assert.NotNull(gz);

        var req = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(gz!));   // hand-framing → generated parser
        Assert.Equal("US", req.Header.Country);
        Assert.Equal("premium", req.Header.Catalogue);
        Assert.Equal(16, req.Header.TaskId.Length);
        Assert.Equal(3, req.EntityRequest.Count);
        Assert.Equal("spotify:track:a", req.EntityRequest[0].EntityUri);
        Assert.Equal(Xm.ExtensionKind.TrackV4, req.EntityRequest[0].Query[0].ExtensionKind);
        Assert.Equal("spotify:album:b", req.EntityRequest[1].EntityUri);
        Assert.Equal(Xm.ExtensionKind.AlbumV4, req.EntityRequest[1].Query[0].ExtensionKind);
        Assert.Equal("spotify:episode:c", req.EntityRequest[2].EntityUri);
        Assert.Equal(Xm.ExtensionKind.EpisodeV4, req.EntityRequest[2].Query[0].ExtensionKind);
    }

    [Fact]
    public void ProjectResponse_DedupesRepeatedArtistRef_AcrossTracks()
    {
        var store = new InMemoryStore();
        var sharedArtist = Gid(0x55);
        var t1 = new Pb.Track { Gid = Gid(0x01), Name = "One" }; t1.Artist.Add(new Pb.Artist { Gid = sharedArtist, Name = "Shared" });
        var t2 = new Pb.Track { Gid = Gid(0x02), Name = "Two" }; t2.Artist.Add(new Pb.Artist { Gid = sharedArtist, Name = "Shared" });
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.TrackV4 };
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:track:1", ExtensionData = Any.Pack(t1) });
        array.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:track:2", ExtensionData = Any.Pack(t2) });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);

        ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);

        var tracks = store.QueryTracks();
        Assert.Equal(2, tracks.Count);
        Assert.Same(tracks[0].Artists[0], tracks[1].Artists[0]);   // memoized → the SAME ArtistRef instance, not two copies
    }

    [Fact]
    public void ProjectResponse_ProjectsShowAndEpisode()
    {
        var store = new InMemoryStore();

        var show = new Pb.Show { Gid = Gid(0x21), Name = "My Show", Publisher = "Acme Media", Description = "A show" };
        show.CoverImage = new Pb.ImageGroup();
        show.CoverImage.Image.Add(new Pb.Image { FileId = Gid(0x99) });   // Size defaults to DEFAULT(0) → PickImage picks it
        var showArray = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.ShowV4 };
        showArray.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:show:x", ExtensionData = Any.Pack(show) });

        var ep = new Pb.Episode { Gid = Gid(0x22), Name = "Ep 1", Duration = 5000, Description = "first",
            PublishTime = new Pb.Date { Year = 2024, Month = 3, Day = 2 } };
        ep.Show = new Pb.Show { Gid = Gid(0x21), Name = "My Show" };   // embedded show → ShowName
        var epArray = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.EpisodeV4 };
        epArray.ExtensionData.Add(new Xm.EntityExtensionData { EntityUri = "spotify:episode:x", ExtensionData = Any.Pack(ep) });

        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(showArray);
        resp.ExtendedMetadata.Add(epArray);
        ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);

        var sh = store.GetShow("spotify:show:" + Base62.Encode(Bytes(0x21)));
        Assert.NotNull(sh);
        Assert.Equal("My Show", sh!.Name);
        Assert.Equal("Acme Media", sh.Publisher);
        Assert.NotNull(sh.Cover);                                  // PickImage projected the cover_image

        var epi = store.GetEpisode("spotify:episode:" + Base62.Encode(Bytes(0x22)));
        Assert.NotNull(epi);
        Assert.Equal("Ep 1", epi!.Title);
        Assert.Equal(5000, epi.DurationMs);
        Assert.Equal("My Show", epi.ShowName);                     // from the embedded show ref
        Assert.Equal(2024, epi.PublishedAt.Year);
        Assert.Equal(3, epi.PublishedAt.Month);
    }
}
