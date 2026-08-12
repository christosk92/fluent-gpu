using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Playlists;
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
    // Lean parsers that DISCARD unknown fields → the many unused Track/Album/Artist repeated fields (file[], restriction[],
    // availability[], alternative[]…) are skipped on the wire, not allocated as messages.
    static readonly MessageParser<Lean.LeanTrack> TrackParser = Lean.LeanTrack.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Lean.LeanAlbum> AlbumParser = Lean.LeanAlbum.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Lean.LeanArtist> ArtistParser = Lean.LeanArtist.Parser.WithDiscardUnknownFields(true);
    // Show episode[] is NOW parsed (LeanShow.episode = 70); DiscardUnknownFields still skips unused show fields.
    static readonly MessageParser<Lean.LeanShow> ShowParser = Lean.LeanShow.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Lean.LeanEpisode> EpisodeParser = Lean.LeanEpisode.Parser.WithDiscardUnknownFields(true);
    // A playlist has no V4 catalogue kind: its header rides LIST_METADATA_V2 (205). Without this arm every playlist uri
    // handed to the chokepoint resolved to UnknownExtension and was DROPPED before the request was even built (see
    // GzipRequest's `continue`), which is why a surface made of playlist pointers had no way to learn a single name.
    static readonly MessageParser<Xm.ListMetadataV2> ListParser = Xm.ListMetadataV2.Parser.WithDiscardUnknownFields(true);

    const string Path = "/extended-metadata/v0/extended-metadata";

    // ── the desktop client's HEADER-TRAIT bundle (opt-in; OFF for every pre-existing caller) ──────────────────────────
    // A census of all 73 extended-metadata calls in the capture says the `client-feature-id` DECIDES the kind set, and
    // for `mdata_esperanto` — the scroll-driven recents viewport hydrator Wavee imitates — the requested kinds are
    // exactly these three:
    //     178 IDENTITY_TRAIT · 179 VISUAL_IDENTITY_TRAIT · 220 ENTITY_TYPE_TRAIT
    // Kind 220 was requested by `mdata_esperanto` and by NO other caller, over 176 entities (80 playlist, 53 album,
    // 42 artist, 1 collection). Kinds 182/212/249 belong to `list_metadata_prefetcher` — a DIFFERENT caller (the
    // upfront bulk prefetch) — and are deliberately NOT sent from the viewport hydrator. Calling {178,179,182,212,249}
    // "the canonical bundle" conflates the two callers; it is not one bundle, it is two per-feature kind sets.
    //
    // THESE PAYLOADS ARE MOSTLY DISCARDED ON PURPOSE — this is NOT dead code to delete:
    //   · 179 is the only one with a verified schema in this repo (SpotifyLive/Protos/visual_identity_trait.proto,
    //     decoded by SpotifyTrackAdornmentService). It is available as a cover/colour FALLBACK for a catalogue kind
    //     that yields none; this path does not wire that up yet (see the note in MetadataService.SyncAllConditionalAsync).
    //   · 178 and 220 have NO schema here. Their field numbers are unknown, and guessing them would be an invention.
    //     They are requested for WIRE FIDELITY and their payloads fall through ProjectParsed's `default: continue`.
    //     The REQUEST is the point; "nothing reads the response" is not a reason to stop sending it.
    //
    // DELIBERATE DIVERGENCE from the real client: we still ask for the catalogue kind (ALBUM_V4 / ARTIST_V4 / SHOW_V4 /
    // EPISODE_V4 / TRACK_V4 / LIST_METADATA_V2). The real client never requests 205 at all — it reads a playlist's name
    // straight out of 178, which we cannot decode. Matching it exactly would leave every recents row nameless, so Wavee
    // asks for BOTH: the traits for wire shape, the catalogue kind for facts it can actually read.
    internal static readonly Xm.ExtensionKind[] HeaderTraitKinds =
    [
        Xm.ExtensionKind.IdentityTrait,         // 178
        Xm.ExtensionKind.VisualIdentityTrait,   // 179
        Xm.ExtensionKind.EntityTypeTrait,       // 220
    ];

    /// <summary>Upper-bound wire cost of the trait bundle under ONE uri: per query a field tag + length byte + the
    /// kind's field tag + a 2-byte varint (178/179/220 all exceed 127). Feeds MetadataChunking so a trait-bearing
    /// request is not under-estimated into an over-large body.</summary>
    internal const int HeaderTraitBytesPerEntity = 3 * 6;

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<SessionContext> _ctx;

    public ExtendedMetadataSource(IHttpExchange http, Func<string> baseUrl, Func<SessionContext> ctx)
    {
        _http = http;
        _baseUrl = baseUrl;
        _ctx = ctx;
    }

    public async Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityRef> entities, IStore store, CancellationToken ct,
                                                              string? clientFeatureId = null, bool headerTraits = false)
    {
        var session = _ctx();
        var proj = new ProjCtx();   // memoizes repeated album/artist refs across the whole sync
        var landed = new HashSet<string>(StringComparer.Ordinal);
        var bulk = entities.Count > 1 ? store.BeginBulk() : null;   // coalesce the per-entity change signals into one
        try
        {
            foreach (var (start, count) in MetadataChunking.Ranges(entities,
                         extraBytesPerEntity: headerTraits ? HeaderTraitBytesPerEntity : 0))
            {
                var gz = GzipRequest(entities, start, count, session, headerTraits);
                if (gz is null) continue;   // the chunk had no supported entities
                using var resp = await SendAsync(gz, ct, clientFeatureId).ConfigureAwait(false);
                if (resp.Status != 200) throw new InvalidOperationException($"extended-metadata fetch failed ({resp.Status})");
                ProjectResponse(resp.Body, store, proj, landed);   // resp.Body is the response stream → parsed without an LOH byte[]
            }
        }
        finally { bulk?.Dispose(); }
        return landed;
    }

    // Serialize the BatchedEntityRequest STRAIGHT into gzip, REUSING one EntityRequest + ExtensionQuery across all entities
    // (10k entities → 3 request objects, not 20k), and no intermediate uncompressed array. Returns null for a chunk with no
    // supported entities. internal so a round-trip test can verify the hand-written framing against the generated parser.
    internal static byte[]? GzipRequest(IReadOnlyList<EntityRef> entities, int start, int count, SessionContext ctx,
                                        bool headerTraits = false)
    {
        Span<byte> taskId = stackalloc byte[16];
        RandomNumberGenerator.Fill(taskId);
        var header = new Xm.BatchedEntityRequestHeader { Country = ctx.Market, Catalogue = ctx.Catalogue, TaskId = ByteString.CopyFrom(taskId) };
        var eq = new Xm.ExtensionQuery();
        var er = new Xm.EntityRequest();
        er.Query.Add(eq);   // reused for every entity (the per-entity catalogue kind)
        // The trait bundle is CONSTANT per entity, so its queries are built ONCE and ride the same reused EntityRequest
        // — the envelope simply carries 1 + 3 kinds under each uri group ("multiple kinds under a uri group as before"),
        // and the per-entity allocation stays at zero. Only `eq` varies as the loop walks the chunk.
        if (headerTraits)
            foreach (var trait in HeaderTraitKinds) er.Query.Add(new Xm.ExtensionQuery { ExtensionKind = trait });

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

    // ── Arbitrary-kind reads (feature payloads beyond bulk Track/Album/Artist hydration) ──────────────────────────────
    // Same endpoint, auth pipeline, protobuf envelope and gzip framing as FetchAsync, but the caller chooses the
    // ExtensionKind per entity and gets the RAW extension payload back (parsed by the feature, NOT projected into the
    // Store here). E.g. an album's RECOMMENDED_PLAYLISTS (151) refs, then those playlists' LIST_METADATA_V2 (205) heroes.
    static readonly IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ByteString> NoExtensions
        = new Dictionary<(string, Xm.ExtensionKind), ByteString>();

    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ByteString>> GetExtensionsAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requests, CancellationToken ct = default, string? clientFeatureId = null)
    {
        if (requests.Count == 0) return NoExtensions;
        using var resp = await SendAsync(GzipExtensionRequest(requests, _ctx()), ct, clientFeatureId).ConfigureAwait(false);
        if (resp.Status != 200) throw new InvalidOperationException($"extended-metadata fetch failed ({resp.Status})");
        var parsed = Xm.BatchedExtensionResponse.Parser.ParseFrom(resp.Body);   // streamed, no LOH byte[]
        var result = new Dictionary<(string, Xm.ExtensionKind), ByteString>();
        foreach (var array in parsed.ExtendedMetadata)
            foreach (var data in array.ExtensionData)
                if (data.ExtensionData?.Value is { IsEmpty: false } value)   // the opaque Any's value = the raw extension bytes
                    result[(data.EntityUri, array.ExtensionKind)] = value;
        return result;
    }

    /// <summary>Convenience for a single (uri, kind) read; null when the entity carried no such extension.</summary>
    public async Task<ByteString?> GetExtensionAsync(string uri, Xm.ExtensionKind kind, CancellationToken ct = default, string? clientFeatureId = null)
    {
        var values = await GetExtensionsAsync(new[] { (uri, kind) }, ct, clientFeatureId).ConfigureAwait(false);
        return values.TryGetValue((uri, kind), out var value) ? value : null;
    }

    // ── Conditional reads (etag + 304) ────────────────────────────────────────────────────────────────────────────────
    // Like GetExtensionsAsync, but the caller passes the etag it last cached per (uri, kind) — sent as ExtensionQuery.etag
    // so the server can answer 304 (not-modified) — and gets back the per-entity status_code + (new) etag + offline TTL,
    // not just the 200 payload. This is the "cache it like a normal extended-metadata thing" path: 200 = fresh payload,
    // 304 = keep cached, 404 = no such extension. Large request lists are chunked by body size (one POST is not unbounded).
    public readonly record struct ExtensionResult(int Status, string? Etag, long OfflineTtlSeconds, ByteString? Payload);

    static readonly IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtensionResult> NoResults
        = new Dictionary<(string, Xm.ExtensionKind), ExtensionResult>();

    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtensionResult>> GetExtensionsWithHeadersAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind, string? Etag)> requests, CancellationToken ct = default, string? clientFeatureId = null)
    {
        if (requests.Count == 0) return NoResults;
        var session = _ctx();
        var result = new Dictionary<(string, Xm.ExtensionKind), ExtensionResult>(requests.Count);
        foreach (var (start, count) in ExtensionRanges(requests))
        {
            using var resp = await SendAsync(GzipExtensionRequest(requests, start, count, session), ct, clientFeatureId).ConfigureAwait(false);
            if (resp.Status != 200) throw new InvalidOperationException($"extended-metadata fetch failed ({resp.Status})");
            var parsed = Xm.BatchedExtensionResponse.Parser.ParseFrom(resp.Body);   // streamed, no LOH byte[]
            foreach (var array in parsed.ExtendedMetadata)
            {
                long arrayOfflineTtl = array.Header?.OfflineTtlInSeconds ?? 0;   // per-array fallback for the per-entity TTL
                foreach (var data in array.ExtensionData)
                {
                    var hdr = data.Header;
                    int status = hdr is { HasStatusCode: true } ? hdr.StatusCode : (data.ExtensionData is null ? 0 : 200);
                    string? etag = hdr is { HasEtag: true, Etag.Length: > 0 } ? hdr.Etag : null;
                    long offlineTtl = hdr is { HasOfflineTtlInSeconds: true } ? hdr.OfflineTtlInSeconds : arrayOfflineTtl;
                    ByteString? payload = data.ExtensionData?.Value is { IsEmpty: false } v ? v : null;
                    result[(data.EntityUri, array.ExtensionKind)] = new ExtensionResult(status, etag, offlineTtl, payload);
                }
            }
        }
        return result;
    }

    // Body-size chunking for the conditional path (the plain GzipExtensionRequest builds one POST; here a 10k-entity
    // detect must not be a single unbounded body). Estimate ≈ uri + etag + tags; never split below one request.
    static IEnumerable<(int Start, int Count)> ExtensionRanges(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind, string? Etag)> reqs,
        int maxBodyBytes = MetadataChunking.DefaultMaxBodyBytes, int headerBytes = 64)
    {
        int start = 0, size = headerBytes;
        for (int i = 0; i < reqs.Count; i++)
        {
            int cost = reqs[i].Uri.Length + (reqs[i].Etag?.Length ?? 0) + 16;
            if (i > start && size + cost > maxBodyBytes) { yield return (start, i - start); start = i; size = headerBytes; }
            size += cost;
        }
        if (reqs.Count > start) yield return (start, reqs.Count - start);
    }

    // The conditional sibling of GzipExtensionRequest(requests, ctx): builds one chunk [start, start+count) and sets
    // ExtensionQuery.etag when the caller cached one (so the server can 304). Multiple kinds under a uri group as before.
    static byte[] GzipExtensionRequest(IReadOnlyList<(string Uri, Xm.ExtensionKind Kind, string? Etag)> requests,
        int start, int count, SessionContext ctx)
    {
        Span<byte> taskId = stackalloc byte[16];
        RandomNumberGenerator.Fill(taskId);
        var request = new Xm.BatchedEntityRequest
        {
            Header = new Xm.BatchedEntityRequestHeader { Country = ctx.Market, Catalogue = ctx.Catalogue, TaskId = ByteString.CopyFrom(taskId) },
        };
        var byUri = new Dictionary<string, Xm.EntityRequest>(StringComparer.Ordinal);
        for (int i = start; i < start + count; i++)
        {
            var (uri, kind, etag) = requests[i];
            if (!byUri.TryGetValue(uri, out var er))
            {
                er = new Xm.EntityRequest { EntityUri = uri };
                byUri[uri] = er;
                request.EntityRequest.Add(er);
            }
            var query = new Xm.ExtensionQuery { ExtensionKind = kind };
            if (!string.IsNullOrEmpty(etag)) query.Etag = etag;
            er.Query.Add(query);
        }

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) request.WriteTo(gz);
        return ms.ToArray();
    }

    // One EntityRequest per uri (its kinds grouped under it), serialized straight into gzip — the same envelope
    // GzipRequest builds, but keyed by an explicit (uri, kind) list instead of the EntityRef→KindFor mapping.
    static byte[] GzipExtensionRequest(IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requests, SessionContext ctx)
    {
        Span<byte> taskId = stackalloc byte[16];
        RandomNumberGenerator.Fill(taskId);
        var request = new Xm.BatchedEntityRequest
        {
            Header = new Xm.BatchedEntityRequestHeader { Country = ctx.Market, Catalogue = ctx.Catalogue, TaskId = ByteString.CopyFrom(taskId) },
        };
        var byUri = new Dictionary<string, Xm.EntityRequest>(StringComparer.Ordinal);
        foreach (var (uri, kind) in requests)
        {
            if (!byUri.TryGetValue(uri, out var er))
            {
                er = new Xm.EntityRequest { EntityUri = uri };
                byUri[uri] = er;
                request.EntityRequest.Add(er);   // preserves first-seen uri order
            }
            er.Query.Add(new Xm.ExtensionQuery { ExtensionKind = kind });
        }

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) request.WriteTo(gz);
        return ms.ToArray();
    }

    // clientFeatureId (default null) stamps the desktop client's `client-feature-id` attribution header (e.g.
    // "mdata_esperanto" from the recents viewport hydrator). Null = header omitted = current behaviour unchanged.
    async Task<HttpResp> SendAsync(byte[] gzippedBody, CancellationToken ct, string? clientFeatureId = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/protobuf",
            ["Content-Encoding"] = "gzip",
            ["Accept"] = "application/protobuf",
            ["Accept-Encoding"] = "gzip, deflate, br",
            ["Accept-Language"] = SpotifyHeaders.NormalizeLanguage(_ctx().Locale),
        };
        if (!string.IsNullOrEmpty(clientFeatureId)) headers["client-feature-id"] = clientFeatureId;
        return await _http.SendAsync(new HttpReq("POST", _baseUrl() + Path, headers, gzippedBody), ct).ConfigureAwait(false);
    }

    static Xm.ExtensionKind KindFor(EntityKind k) => k switch
    {
        EntityKind.Track => Xm.ExtensionKind.TrackV4,
        EntityKind.Album => Xm.ExtensionKind.AlbumV4,
        EntityKind.Artist => Xm.ExtensionKind.ArtistV4,
        EntityKind.Show => Xm.ExtensionKind.ShowV4,
        EntityKind.Episode => Xm.ExtensionKind.EpisodeV4,
        // A playlist's header is LIST_METADATA_V2, not a V4 — see ProjectPlaylist. Mirrored in MetadataService.KindFor,
        // which builds the conditional (etag) request list from the same mapping.
        EntityKind.Playlist => Xm.ExtensionKind.ListMetadataV2,
        _ => Xm.ExtensionKind.UnknownExtension,
    };

    /// <summary>Parse a BatchedExtensionResponse and project each entity proto into the Store. Returns the EntityUris
    /// that successfully projected — callers seal freshness on this set only (outcome seeding). Pure — the unit test
    /// feeds crafted protobuf here, so the whole parse→project path is covered without a network.</summary>
    public static HashSet<string> ProjectResponse(byte[] responseBytes, IStore store)
    {
        var landed = new HashSet<string>(StringComparer.Ordinal);
        ProjectParsed(Xm.BatchedExtensionResponse.Parser.ParseFrom(responseBytes), store, new ProjCtx(), landed);
        return landed;
    }

    static void ProjectResponse(Stream responseStream, IStore store, ProjCtx proj, HashSet<string> landed)
        => ProjectParsed(Xm.BatchedExtensionResponse.Parser.ParseFrom(responseStream), store, proj, landed);   // streamed, no LOH byte[]

    static void ProjectParsed(Xm.BatchedExtensionResponse resp, IStore store, ProjCtx proj, HashSet<string> landed)
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
                        // A playlist header carries no gid, so unlike every arm above it must be told WHICH uri it is.
                        // It also may not always write — see ProjectPlaylist — and a non-write must stay UNSEALED so the
                        // next hydrate retries it (outcome seeding, not batch-membership seeding).
                        case Xm.ExtensionKind.ListMetadataV2:
                            if (!ProjectPlaylist(ListParser.ParseFrom(value), data.EntityUri, store)) continue;
                            break;
                        default: continue;
                    }
                    if (data.EntityUri is { Length: > 0 } uri) landed.Add(uri);
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
        string? isrc = null;   // Track.external_id (field 10) — the ISRC drives the lyrics exact-recording fast-path
        foreach (var x in t.ExternalId)
            if (string.Equals(x.Type, "isrc", StringComparison.OrdinalIgnoreCase)) { isrc = x.Id; break; }
        store.UpsertTrack(new Track(id, "spotify:track:" + id, t.Name, artists, album, t.Duration, t.Explicit, image,
            Availability: PlayabilityOf(t), AvailableAt: LiveAtOf(t), Isrc: isrc,
            CanonicalUri: CanonicalUriOf(t, id)));
    }

    /// <summary>LeanTrack.canonical_uri when it names a different playable than self; null = unknown-or-self.</summary>
    static string? CanonicalUriOf(Lean.LeanTrack t, string selfId)
    {
        if (!t.HasCanonicalUri || t.CanonicalUri.Length == 0) return null;
        string c = t.CanonicalUri.StartsWith("spotify:", StringComparison.Ordinal) ? t.CanonicalUri
                 : t.CanonicalUri.Length == 22 ? "spotify:track:" + t.CanonicalUri
                 : "";
        if (c.Length == 0) return null;
        return c == "spotify:track:" + selfId ? null : c;
    }

    /// <summary>Can this track actually play? Decided by FILES, not by <c>restriction</c>.
    ///
    /// Measured over 10,472 real TrackV4 payloads: 8,378 carry files outright; 2,050 carry NO files but a relink
    /// <c>alternative</c> that has files of its own (still playable); 44 carry neither (dead). <c>restriction</c> is
    /// present on the relink class AND the dead class, so using it as the verdict would have marked a fifth of a normal
    /// library unplayable — and an empty <c>countries_allowed</c> is an empty WHITELIST, i.e. no country gate at all.
    ///
    /// Returns null when the payload states nothing either way, so "unknown" stays distinct from "playable".</summary>
    static Availability? PlayabilityOf(Lean.LeanTrack t)
    {
        if (t.File.Count > 0) return Core.Availability.Playable;
        foreach (var alt in t.Alternative)
            if (alt.File.Count > 0) return Core.Availability.Playable;   // relinked: a different rendition plays
        // No files anywhere. Only call that unavailable when the payload is otherwise substantive — a thin projection
        // that simply omitted the file plane must not be read as a verdict.
        return t.HasEarliestLiveTimestamp || t.Restriction.Count > 0 ? Core.Availability.Unavailable : null;
    }

    /// <summary>The track's live instant (TrackV4 <c>earliest_live_timestamp</c>, unix seconds). Present on every
    /// payload in the capture; a FUTURE value is a track announced but not yet out.</summary>
    static DateTimeOffset? LiveAtOf(Lean.LeanTrack t)
        => t.HasEarliestLiveTimestamp && t.EarliestLiveTimestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(t.EarliestLiveTimestamp)
            : null;

    static void ProjectAlbum(Lean.LeanAlbum al, IStore store, ProjCtx proj)
    {
        string id = Base62.Encode(al.Gid.Span);
        var artists = new List<ArtistRef>(al.Artist.Count);
        foreach (var a in al.Artist) artists.Add(proj.Artist(a.Gid, a.Name));
        int year = al.Date is { } d ? d.Year : 0;
        var albumRef = new AlbumRef(id, "spotify:album:" + id, al.Name);
        Image? cover = PickImage(al.CoverGroup);

        // The tracklist (disc[].track[] now parses via LeanTrack). Each row carries the album cover + (its own or the
        // album's) artists; also upserted as a resident Track so playback / GetTrack resolve it.
        var tracks = new List<Track>();
        foreach (var disc in al.Disc)
            foreach (var t in disc.Track)
            {
                if (t.Gid.Length == 0) continue;
                string tid = Base62.Encode(t.Gid.Span);
                IReadOnlyList<ArtistRef> tArtists = artists;
                if (t.Artist.Count > 0)
                {
                    var list = new List<ArtistRef>(t.Artist.Count);
                    foreach (var a in t.Artist) list.Add(proj.Artist(a.Gid, a.Name));
                    tArtists = list;
                }
                var track = new Track(tid, "spotify:track:" + tid, t.Name, tArtists, albumRef, t.Duration, t.Explicit, cover);
                tracks.Add(track);
                store.UpsertTrack(track);
            }

        store.UpsertAlbum(new Album(id, albumRef.Uri, al.Name, cover, artists, year, tracks.Count, tracks,
            Kind: KindFromWire(al.Type), Hydration: AlbumHydrationLevel.Tracks));

        // Album.type (wire field 4) already distinguishes EP=4 — no track-count heuristic needed; map it straight.
        static AlbumKind KindFromWire(int type) => type switch
        {
            2 => AlbumKind.Single, 3 => AlbumKind.Compilation, 4 => AlbumKind.EP, _ => AlbumKind.Album,
        };
    }

    static void ProjectArtist(Lean.LeanArtist ar, IStore store)
    {
        string id = Base62.Encode(ar.Gid.Span);
        var artist = new Artist(id, "spotify:artist:" + id, ar.Name, PickImage(ar.PortraitGroup));

        // The whole discography rides one ArtistV4: album/single/compilation groups → the own-discography cards (facet
        // totals ARE the group counts now); appears_on groups → the appears-on shelf. All written as gid-only stubs here
        // (Name/Cover usually absent on the wire); ArtistDiscography.Assemble upgrades them to resident AlbumV4 cards.
        int own = ar.AlbumGroup.Count + ar.SingleGroup.Count + ar.CompilationGroup.Count;
        if (own > 0)
        {
            var stubs = new List<Album>(own);
            AddStubs(stubs, ar.AlbumGroup, AlbumKind.Album);
            AddStubs(stubs, ar.SingleGroup, AlbumKind.Single);
            AddStubs(stubs, ar.CompilationGroup, AlbumKind.Compilation);
            artist = artist with
            {
                TopAlbums = stubs,
                AlbumsTotal = ar.AlbumGroup.Count,          // per-facet totals = group counts (GraphQL facet parity)
                SinglesTotal = ar.SingleGroup.Count,
                CompilationsTotal = ar.CompilationGroup.Count,
            };
        }
        if (ar.AppearsOnGroup.Count > 0)
        {
            var appears = new List<Album>(ar.AppearsOnGroup.Count);
            AddStubs(appears, ar.AppearsOnGroup, AlbumKind.Album);
            artist = artist with { AppearsOn = appears };
        }
        if (ar.Biography.Count > 0 && ar.Biography[0].Text.Length > 0)
            artist = artist with { Bio = ar.Biography[0].Text };
        // Top-track gids are NOT written to Artist.TopTracks — that would trip EnsureFetchedAsync's stats gate and clobber
        // a play-count-rich overview list. They resolve to named tracks at assembly time (ArtistDiscography).
        store.UpsertArtist(artist);

        // One stub per GROUP: album[0] is the representative release (versions grouped). A gid-less head is skipped.
        static void AddStubs(List<Album> into, IEnumerable<Lean.LeanAlbumGroup> groups, AlbumKind kind)
        {
            foreach (var g in groups)
            {
                if (g.Album.Count == 0 || g.Album[0].Gid.Length == 0) continue;
                string aid = Base62.Encode(g.Album[0].Gid.Span);
                into.Add(new Album(aid, "spotify:album:" + aid, g.Album[0].Name, PickImage(g.Album[0].CoverGroup),
                    Array.Empty<ArtistRef>(), 0, 0, Kind: kind));   // Name/Cover usually empty on wire → stub; assembly upgrades
            }
        }
    }

    static void ProjectShow(Lean.LeanShow sh, IStore store)
    {
        string id = Base62.Encode(sh.Gid.Span);
        string uri = "spotify:show:" + id;
        store.UpsertShow(new Show(id, uri, sh.Name, sh.Publisher, PickImage(sh.CoverImage), Description: NullIfEmpty(sh.Description)));
        // Episode gids → generic membership plane (playlist_items keyed by show uri). Kind-blind consumers
        // (GcSweepMemberships / NoteAdopted) already key by uri string — show keys are safe; offline track search
        // filters entity.kind=Track so episode members do not pollute QueryTracks. Opened shows should land in
        // recent_surfaces so membership GC does not purge them as "foreign playlists".
        if (sh.Episode.Count == 0) return;
        var members = new List<PlaylistMember>(sh.Episode.Count);
        for (int i = 0; i < sh.Episode.Count; i++)
        {
            var ep = sh.Episode[i];
            if (ep.Gid.Length == 0) continue;
            string eid = Base62.Encode(ep.Gid.Span);
            members.Add(new PlaylistMember(eid, "spotify:episode:" + eid, null, 0));
        }
        if (members.Count > 0) store.SetMembership(uri, members, null);
    }

    static void ProjectEpisode(Lean.LeanEpisode ep, IStore store)
    {
        string id = Base62.Encode(ep.Gid.Span);
        string showName = ep.Show is { } s ? s.Name : "";   // the embedded show ref (gid+name); full show hydrates separately
        store.UpsertEpisode(new Episode(id, "spotify:episode:" + id, ep.Name, showName, PickImage(ep.CoverImage),
            ep.Duration, PublishedAt(ep.PublishTime), Description: NullIfEmpty(ep.Description)));
    }

    /// <summary>LIST_METADATA_V2 (205) → a playlist HEADER. Returns true when something was written.
    ///
    /// This is a HYDRATION write, not the header WRITER, and the difference is load-bearing.
    /// <see cref="StoreEntityMerge.Playlist"/> treats Name/Description/Cover/Capabilities as AUTHORITATIVE — absence
    /// means CLEAR, because its intended caller is <c>OpRebaseStrategy.ApplyHeaderPatch</c>, where a missing picture
    /// really is ClearPicture. A name-and-cover hydrate arriving over a playlist that <c>PlaylistFetcher</c> already
    /// filled would therefore blank its description, its capabilities and (when 205 carries no image) its cover.
    ///
    /// So the merge is done HERE, on the way in: every field this payload does not know is carried through from the
    /// resident row, which makes the authoritative merge a no-op on exactly those fields. Membership
    /// (<c>SetMembership</c>) is a separate plane and is not touched at all. The rule is the same "thin write must not
    /// downgrade a rich row" discipline <see cref="ProjectArtist"/>'s stub-fold and <c>StoreEntityGaps</c> already
    /// apply to albums and tracks.</summary>
    static bool ProjectPlaylist(Xm.ListMetadataV2 meta, string uri, IStore store)
    {
        if (uri.Length == 0) return false;
        var current = store.GetPlaylist(uri);
        string name = meta.Name.Length > 0 ? meta.Name : current?.Name ?? "";
        Image? cover = ListCover(meta) ?? current?.Cover;
        string owner = meta.Source.Length > 0 ? meta.Source : current?.OwnerName ?? "";
        // Nothing readable landed AND nothing resident to preserve ⇒ do not mint an empty header (it would seal
        // freshness on a row that still knows nothing).
        if (name.Length == 0 && cover is null && owner.Length == 0) return false;

        store.UpsertPlaylist(new Playlist(
            Id: current?.Id ?? IdOf(uri),
            Uri: uri,
            Name: name,
            Description: NullIfEmpty(meta.Description) ?? current?.Description,
            OwnerName: owner,
            Cover: cover,
            // Everything below is carried through verbatim: 205 states none of it, and the merge would otherwise read
            // this write's defaults as an authoritative clear.
            TrackCount: current?.TrackCount ?? 0,
            Tracks: current?.Tracks,
            Owner: current?.Owner,
            Capabilities: current?.Capabilities ?? default,
            // format_string is NOT mapped onto Playlist.Format: that field is the recommender format the playlist4
            // format_attributes writer owns, and equating the two without a verified sample would be a guess.
            Format: current?.Format,
            Source: current?.Source,
            Collaborators: current?.Collaborators,
            IsPublic: current?.IsPublic ?? true,
            BasePermissionRevision: current?.BasePermissionRevision,
            Tuning: current?.Tuning,
            DaylistExpiresAtMs: current?.DaylistExpiresAtMs ?? 0,
            DaylistCreatedAtMs: current?.DaylistCreatedAtMs ?? 0));
        return true;
    }

    /// <summary>205's image variants → one cover. "default"/"large" first (the balanced list render), else any variant
    /// that carries a url at all.</summary>
    static Image? ListCover(Xm.ListMetadataV2 meta)
    {
        var variants = meta.Images?.Variant;
        if (variants is null) return null;
        string? standard = null, any = null;
        foreach (var v in variants)
        {
            if (v.Url.Length == 0) continue;
            any ??= v.Url;
            if (standard is null && (v.Format == "default" || v.Format == "large")) standard = v.Url;
        }
        string? pick = standard ?? any;
        return pick is null ? null : new Image(pick);
    }

    /// <summary>The trailing id of a <c>spotify:kind:id</c> uri — a playlist header has no gid to base62-encode.</summary>
    static string IdOf(string uri)
    {
        int i = uri.LastIndexOf(':');
        return i >= 0 && i + 1 < uri.Length ? uri[(i + 1)..] : uri;
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
    // Internal so the track-expansion service can reuse it for a music video's cover rather than adding a FOURTH copy of
    // the "https://i.scdn.co/image/<hex>" construction to the tree.
    internal static Image? PickImage(Lean.LeanImageGroup? group)
    {
        if (group is null) return null;
        // Keep DEFAULT as the normal card/list source, but retain the largest rendition separately for full-width heroes.
        // This avoids paying 1024px decode/upload costs across every thumbnail while preventing a 300px cover from being
        // stretched across the immersive detail surface.
        Lean.LeanImage? chosen = null, fallback = null, largest = null;
        int largestScore = int.MinValue;
        foreach (var img in group.Image)
        {
            if (img.FileId.Length == 0) continue;
            fallback ??= img;
            if (img.Size == 0) chosen ??= img;   // 0 = Image.Size.DEFAULT (~300px)
            int score = img.HasWidth && img.Width > 0 ? img.Width * 8 : img.Size;
            if (largest is null || score > largestScore) { largest = img; largestScore = score; }
        }
        var pick = chosen ?? fallback;
        if (pick is null) return null;
        string url = "https://i.scdn.co/image/" + Convert.ToHexStringLower(pick.FileId.Span);
        string? largestUrl = largest is null
            ? null
            : "https://i.scdn.co/image/" + Convert.ToHexStringLower(largest.FileId.Span);
        return new Image(
            url,
            pick.HasWidth ? pick.Width : null,
            pick.HasHeight ? pick.Height : null,
            LargestUrl: largestUrl);
    }

    /// <summary>TrackV4 / full metadata covers still arrive as <see cref="Wavee.Protocol.Metadata.ImageGroup"/>;
    /// same DEFAULT-vs-largest pick as the lean overload.</summary>
    internal static Image? PickImage(Wavee.Protocol.Metadata.ImageGroup? group)
    {
        if (group is null) return null;
        Wavee.Protocol.Metadata.Image? chosen = null, fallback = null, largest = null;
        int largestScore = int.MinValue;
        foreach (var img in group.Image)
        {
            if (img.FileId.Length == 0) continue;
            fallback ??= img;
            if (img.Size == Wavee.Protocol.Metadata.Image.Types.Size.Default) chosen ??= img;
            int score = img.HasWidth && img.Width > 0 ? img.Width * 8 : (int)img.Size;
            if (largest is null || score > largestScore) { largest = img; largestScore = score; }
        }
        var pick = chosen ?? fallback;
        if (pick is null) return null;
        string url = "https://i.scdn.co/image/" + Convert.ToHexStringLower(pick.FileId.Span);
        string? largestUrl = largest is null
            ? null
            : "https://i.scdn.co/image/" + Convert.ToHexStringLower(largest.FileId.Span);
        return new Image(
            url,
            pick.HasWidth ? pick.Width : null,
            pick.HasHeight ? pick.Height : null,
            LargestUrl: largestUrl);
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
