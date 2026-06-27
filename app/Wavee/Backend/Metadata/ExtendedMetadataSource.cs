using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Lean = Wavee.Protocol.Lean;

namespace Wavee.Backend.Metadata;

// ── STEP 3 — the REAL extended-metadata source ───────────────────────────────────────────────────────────────────────
// Builds one BatchedEntityRequest per body-size chunk (gzipped), POSTs to spclient, parses the BatchedExtensionResponse,
// and projects each entity proto (Track/Album/Artist) into the Store. The Bearer + client-token are attached by the
// HttpExchange pipeline; country/catalogue come from the SessionContext. The opaque Any payload is read as raw proto bytes
// (type_url ignored), parsed by the array's ExtensionKind — exactly the real client's contract.
public sealed class ExtendedMetadataSource : IMetadataSource
{
    const string Path = "/extended-metadata/v0/extended-metadata";

    // Lean parsers that DISCARD unknown fields → the many unused Track/Album/Artist repeated fields (file[], restriction[],
    // availability[], alternative[]…) are skipped on the wire, not allocated as messages.
    static readonly MessageParser<Lean.LeanTrack> TrackParser = Lean.LeanTrack.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Lean.LeanAlbum> AlbumParser = Lean.LeanAlbum.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Lean.LeanArtist> ArtistParser = Lean.LeanArtist.Parser.WithDiscardUnknownFields(true);
    // Show carries a repeated episode[] (potentially every episode); the lean view skips it (DiscardUnknownFields), as with Track.file[] etc.
    static readonly MessageParser<Lean.LeanShow> ShowParser = Lean.LeanShow.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Lean.LeanEpisode> EpisodeParser = Lean.LeanEpisode.Parser.WithDiscardUnknownFields(true);

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;       // spclient base, e.g. https://gew4-spclient.spotify.com
    readonly Func<SessionContext> _ctx;

    public ExtendedMetadataSource(IHttpExchange http, Func<string> baseUrl, Func<SessionContext> ctx)
    {
        _http = http;
        _baseUrl = baseUrl;
        _ctx = ctx;
    }

    public async Task FetchAsync(IReadOnlyList<EntityRef> entities, IStore store, CancellationToken ct)
    {
        var session = _ctx();
        var proj = new ProjCtx();   // memoizes repeated album/artist refs across the whole sync
        var bulk = entities.Count > 1 ? store.BeginBulk() : null;   // coalesce the per-entity change signals into one
        try
        {
            foreach (var (start, count) in MetadataChunking.Ranges(entities))
            {
                var gz = GzipRequest(entities, start, count, session);
                if (gz is null) continue;   // the chunk had no supported entities
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = "application/protobuf",
                    ["Content-Encoding"] = "gzip",
                    ["Accept"] = "application/protobuf",
                    ["Accept-Encoding"] = "gzip, deflate, br",
                };
                using var resp = await _http.SendAsync(new HttpReq("POST", _baseUrl() + Path, headers, gz), ct).ConfigureAwait(false);
                if (resp.Status != 200) throw new InvalidOperationException($"extended-metadata fetch failed ({resp.Status})");
                ProjectResponse(resp.Body, store, proj);   // resp.Body is the response stream → parsed without an LOH byte[]
            }
        }
        finally { bulk?.Dispose(); }
    }

    // Serialize the BatchedEntityRequest STRAIGHT into gzip, REUSING one EntityRequest + ExtensionQuery across all entities
    // (10k entities → 3 request objects, not 20k), and no intermediate uncompressed array. Returns null for a chunk with no
    // supported entities. internal so a round-trip test can verify the hand-written framing against the generated parser.
    internal static byte[]? GzipRequest(IReadOnlyList<EntityRef> entities, int start, int count, SessionContext ctx)
    {
        Span<byte> taskId = stackalloc byte[16];
        RandomNumberGenerator.Fill(taskId);
        var header = new Xm.BatchedEntityRequestHeader { Country = ctx.Market, Catalogue = ctx.Catalogue, TaskId = ByteString.CopyFrom(taskId) };
        var eq = new Xm.ExtensionQuery();
        var er = new Xm.EntityRequest();
        er.Query.Add(eq);   // reused for every entity (one query each)

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            using var o = new CodedOutputStream(gz, leaveOpen: true);
            o.WriteRawTag(0x0A);    // field 1 (header), length-delimited
            o.WriteMessage(header);
            bool any = false;
            for (int i = start; i < start + count; i++)
            {
                var kind = KindFor(entities[i].Kind);
                if (kind == Xm.ExtensionKind.UnknownExtension) continue;
                er.EntityUri = entities[i].Uri;
                eq.ExtensionKind = kind;
                o.WriteRawTag(0x12);   // field 2 (entity_request, repeated), length-delimited
                o.WriteMessage(er);    // length-prefixed; er/eq reused → no per-entity allocation
                any = true;
            }
            if (!any) return null;
        }   // o flushes, then gz finalizes the gzip into ms
        return ms.ToArray();
    }

    static Xm.ExtensionKind KindFor(EntityKind k) => k switch
    {
        EntityKind.Track => Xm.ExtensionKind.TrackV4,
        EntityKind.Album => Xm.ExtensionKind.AlbumV4,
        EntityKind.Artist => Xm.ExtensionKind.ArtistV4,
        EntityKind.Show => Xm.ExtensionKind.ShowV4,
        EntityKind.Episode => Xm.ExtensionKind.EpisodeV4,
        _ => Xm.ExtensionKind.UnknownExtension,
    };

    /// <summary>Parse a BatchedExtensionResponse and project each entity proto into the Store. Pure — the unit test feeds
    /// crafted protobuf here, so the whole parse→project path is covered without a network.</summary>
    public static void ProjectResponse(byte[] responseBytes, IStore store)
        => ProjectParsed(Xm.BatchedExtensionResponse.Parser.ParseFrom(responseBytes), store, new ProjCtx());

    static void ProjectResponse(Stream responseStream, IStore store, ProjCtx proj)
        => ProjectParsed(Xm.BatchedExtensionResponse.Parser.ParseFrom(responseStream), store, proj);   // streamed, no LOH byte[]

    static void ProjectParsed(Xm.BatchedExtensionResponse resp, IStore store, ProjCtx proj)
    {
        foreach (var array in resp.ExtendedMetadata)    // outer: a few arrays grouped by ExtensionKind (a small constant)
        {
            foreach (var data in array.ExtensionData)   // inner: the entities of that kind — total work is O(N), not O(N^2)
            {
                if (data.ExtensionData is null) continue;   // entity-level 304 (null payload) is a cache concern, not here
                var value = data.ExtensionData.Value;       // ByteString — parse straight from it (NO per-entity byte[] copy)
                try
                {
                    switch (array.ExtensionKind)
                    {
                        case Xm.ExtensionKind.TrackV4: ProjectTrack(TrackParser.ParseFrom(value), store, proj); break;
                        case Xm.ExtensionKind.AlbumV4: ProjectAlbum(AlbumParser.ParseFrom(value), store, proj); break;
                        case Xm.ExtensionKind.ArtistV4: ProjectArtist(ArtistParser.ParseFrom(value), store); break;
                        case Xm.ExtensionKind.ShowV4: ProjectShow(ShowParser.ParseFrom(value), store); break;
                        case Xm.ExtensionKind.EpisodeV4: ProjectEpisode(EpisodeParser.ParseFrom(value), store); break;
                    }
                }
                catch (InvalidProtocolBufferException) { /* skip one malformed entity, keep the rest of the batch */ }
            }
        }
    }

    static void ProjectTrack(Lean.LeanTrack t, IStore store, ProjCtx proj)
    {
        string id = Base62.Encode(t.Gid.Span);   // track gids are unique → no memo benefit; encode directly
        var artists = new List<ArtistRef>(t.Artist.Count);
        foreach (var a in t.Artist) artists.Add(proj.Artist(a.Gid, a.Name));   // memoized: artists recur across tracks
        AlbumRef album = new("", "", "");
        Image? image = null;
        if (t.Album is { } al) { var (aref, cover) = proj.Album(al.Gid, al.Name, al.CoverGroup); album = aref; image = cover; }
        store.UpsertTrack(new Track(id, "spotify:track:" + id, t.Name, artists, album, t.Duration, t.Explicit, image));
    }

    static void ProjectAlbum(Lean.LeanAlbum al, IStore store, ProjCtx proj)
    {
        string id = Base62.Encode(al.Gid.Span);
        var artists = new List<ArtistRef>(al.Artist.Count);
        foreach (var a in al.Artist) artists.Add(proj.Artist(a.Gid, a.Name));
        int year = al.Date is { } d ? d.Year : 0;
        int trackCount = 0;
        foreach (var disc in al.Disc) trackCount += disc.Track.Count;
        store.UpsertAlbum(new Album(id, "spotify:album:" + id, al.Name, PickImage(al.CoverGroup), artists, year, trackCount));
    }

    static void ProjectArtist(Lean.LeanArtist ar, IStore store)
    {
        string id = Base62.Encode(ar.Gid.Span);
        store.UpsertArtist(new Artist(id, "spotify:artist:" + id, ar.Name, PickImage(ar.PortraitGroup)));
    }

    static void ProjectShow(Lean.LeanShow sh, IStore store)
    {
        string id = Base62.Encode(sh.Gid.Span);
        store.UpsertShow(new Show(id, "spotify:show:" + id, sh.Name, sh.Publisher, PickImage(sh.CoverImage), Description: NullIfEmpty(sh.Description)));
    }

    static void ProjectEpisode(Lean.LeanEpisode ep, IStore store)
    {
        string id = Base62.Encode(ep.Gid.Span);
        string showName = ep.Show is { } s ? s.Name : "";   // the embedded show ref (gid+name); full show hydrates separately
        store.UpsertEpisode(new Episode(id, "spotify:episode:" + id, ep.Name, showName, PickImage(ep.CoverImage),
            ep.Duration, PublishedAt(ep.PublishTime), Description: NullIfEmpty(ep.Description)));
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // Build a calendar date from the proto Date (year/month/day), clamped so a malformed wire value can never throw.
    static DateTimeOffset PublishedAt(Lean.LeanDate? d)
    {
        if (d is null || d.Year <= 0) return DateTimeOffset.UnixEpoch;
        int year = Math.Clamp(d.Year, 1, 9999);
        int month = d.HasMonth ? Math.Clamp(d.Month, 1, 12) : 1;
        int day = d.HasDay ? Math.Clamp(d.Day, 1, DateTime.DaysInMonth(year, month)) : 1;
        return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
    }

    // The cover_group carries SMALL/DEFAULT/LARGE/XLARGE renders, each its own file_id/URL. Pick the DEFAULT (~300px) — a
    // balanced single cover for lists/grids — over a heavy 640px+ render, and record its dimensions. (For true per-context
    // sizing — a 64px row thumbnail vs a 640px hero — the entity should carry the render SET; see the note to the user.)
    static Image? PickImage(Lean.LeanImageGroup? group)
    {
        if (group is null) return null;
        Lean.LeanImage? chosen = null, fallback = null;
        foreach (var img in group.Image)
        {
            if (img.FileId.Length == 0) continue;
            fallback ??= img;
            if (img.Size == 0) { chosen = img; break; }   // 0 = Image.Size.DEFAULT (~300px)
        }
        var pick = chosen ?? fallback;
        if (pick is null) return null;
        return new Image(
            "https://i.scdn.co/image/" + Convert.ToHexStringLower(pick.FileId.Span),   // one alloc, not ToHexString+ToLower
            pick.HasWidth ? pick.Width : null,
            pick.HasHeight ? pick.Height : null);
    }

    // Per-sync memoization: a playlist's tracks share albums/artists, so the same gid recurs many times. Dedupe the base62
    // encode, the uri strings, AND the value objects (shared immutable refs) instead of rebuilding them per track. ByteString
    // has content-based equality/hash, so it keys directly. Single-threaded per FetchAsync → no locking.
    sealed class ProjCtx
    {
        readonly Dictionary<ByteString, ArtistRef> _artists = new();
        readonly Dictionary<ByteString, (AlbumRef Ref, Image? Cover)> _albums = new();

        public ArtistRef Artist(ByteString gid, string name)
        {
            if (!_artists.TryGetValue(gid, out var a)) { var id = Base62.Encode(gid.Span); _artists[gid] = a = new ArtistRef(id, "spotify:artist:" + id, name); }
            return a;
        }

        // Also memoizes the cover Image — for a K-track album the cover is picked/hex-encoded once, not K times.
        public (AlbumRef Ref, Image? Cover) Album(ByteString gid, string name, Lean.LeanImageGroup? coverGroup)
        {
            if (!_albums.TryGetValue(gid, out var a))
            {
                var id = Base62.Encode(gid.Span);
                a = (new AlbumRef(id, "spotify:album:" + id, name), PickImage(coverGroup));
                _albums[gid] = a;
            }
            return a;
        }
    }
}
