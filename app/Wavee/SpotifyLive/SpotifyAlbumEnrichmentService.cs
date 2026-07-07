using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

/// <summary>Spotify's below-the-fold album reads. Every method is best-effort and independently consumable by the UI;
/// no failure here can invalidate the already-loaded album or track list. The Pathfinder ops carry the rich JSON
/// (about-artist / merch / similar albums); the recommended-playlist hydration rides the SHARED
/// <see cref="ExtendedMetadataSource"/> (kinds 151 → 205) rather than a second metadata transport.</summary>
sealed class SpotifyAlbumEnrichmentService : IAlbumEnrichmentService
{
    readonly PathfinderResource _pathfinder;
    readonly ExtendedMetadataSource _metadata;
    readonly ExtensionEtagCache? _extensions;
    readonly IStore _store;
    readonly Action<string>? _log;

    public SpotifyAlbumEnrichmentService(PathfinderResource pathfinder, ExtendedMetadataSource metadata, IStore store,
        Action<string>? log = null, ExtensionEtagCache? extensions = null)
    {
        _pathfinder = pathfinder;
        _metadata = metadata;
        _extensions = extensions;
        _store = store;
        _log = log;
    }

    public async Task<Artist?> GetAboutArtistAsync(string artistUri, string leadTrackUri, CancellationToken ct = default)
    {
        if (artistUri.Length == 0 || leadTrackUri.Length == 0) return _store.GetArtist(artistUri);
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.QueryNpvArtist, PathfinderOps.QueryNpvArtistHash,
            w =>
            {
                w.WriteString("artistUri", artistUri);
                w.WriteString("trackUri", leadTrackUri);
                w.WriteNumber("contributorsLimit", 10);
                w.WriteNumber("contributorsOffset", 0);
                w.WriteBoolean("enableRelatedVideos", true);
                w.WriteBoolean("enableRelatedAudioTracks", true);
            }, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        if (doc is null) return _store.GetArtist(artistUri);

        var mapped = SpotifyExportMapper.ArtistFromNpv(doc.RootElement);
        if (mapped is null) return _store.GetArtist(artistUri);
        // NPV is a thinner artist shape than the full overview — keep any richer facets we already cached.
        mapped = mapped with
        {
            Bio = Excerpt(mapped.Bio),
        };
        _store.UpsertArtist(mapped);
        return _store.GetArtist(artistUri) ?? mapped;
    }

    public async Task<IReadOnlyList<Artist>> GetRelatedArtistsAsync(string artistUri, CancellationToken ct = default)
    {
        if (artistUri.Length == 0) return Array.Empty<Artist>();
        if (_store.GetArtist(artistUri)?.Extras?.Related is { Count: > 0 } cached)
            return Artists(cached);

        using var doc = await _pathfinder.QueryAsync(PathfinderOps.QueryArtistOverview, PathfinderOps.QueryArtistOverviewHash,
            w => { w.WriteString("uri", artistUri); w.WriteString("locale", ""); w.WriteBoolean("preReleaseV2", false); },
            PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return Array.Empty<Artist>();
        var artist = SpotifyExportMapper.ArtistFromOverview(doc.RootElement);
        if (artist is null) return Array.Empty<Artist>();
        _store.UpsertArtist(artist with { FetchedAt = DateTimeOffset.UtcNow });   // full overview → stamp SWR freshness
        return artist.Extras?.Related is { Count: > 0 } related ? Artists(related) : Array.Empty<Artist>();
    }

    public async Task<AlbumTrackContext?> GetTrackContextAsync(string trackUri, CancellationToken ct = default)
    {
        if (trackUri.Length == 0) return null;
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.GetTrack, PathfinderOps.GetTrackHash,
            w => w.WriteString("uri", trackUri), PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        return doc is null ? null : SpotifyExportMapper.TrackContextFromUnion(doc.RootElement);
    }

    public async Task<IReadOnlyList<MerchItem>> GetMerchAsync(string albumUri, CancellationToken ct = default)
    {
        if (albumUri.Length == 0) return Array.Empty<MerchItem>();
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.QueryAlbumMerch, PathfinderOps.QueryAlbumMerchHash,
            w => w.WriteString("uri", albumUri), PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        return doc is null ? Array.Empty<MerchItem>() : SpotifyExportMapper.AlbumMerch(doc.RootElement);
    }

    public async Task<IReadOnlyList<Album>> GetSimilarAlbumsAsync(string seedTrackUri, int limit = 24, CancellationToken ct = default)
    {
        if (seedTrackUri.Length == 0) return Array.Empty<Album>();
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.SimilarAlbumsBasedOnThisTrack,
            PathfinderOps.SimilarAlbumsBasedOnThisTrackHash,
            w => { w.WriteString("uri", seedTrackUri); w.WriteNumber("limit", limit); w.WriteBoolean("albumsOnly", true); },
            PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return Array.Empty<Album>();
        var albums = SpotifyExportMapper.SimilarAlbumsFromTrack(doc.RootElement);
        foreach (var a in albums) _store.UpsertAlbum(a);   // project for navigation reuse (a click opens with a hero)
        return albums;
    }

    // The recommended-playlist shelf is a TWO-STAGE extended-metadata read over the shared source: kind 151
    // (RECOMMENDED_PLAYLISTS) yields the ordered playlist refs for the album; kind 205 (LIST_METADATA_V2) hydrates each
    // ref's hero (name/cover/owner) in one batch. Both ride ExtendedMetadataSource — no bespoke transport.
    public async Task<IReadOnlyList<PlaylistSummary>> GetRecommendedPlaylistsAsync(string albumUri, CancellationToken ct = default)
    {
        if (albumUri.Length == 0) return Array.Empty<PlaylistSummary>();

        ByteString? refsPayload;
        try
        {
            refsPayload = _extensions is not null
                ? await _extensions.GetPayloadAsync(albumUri, Xm.ExtensionKind.RecommendedPlaylists, ct).ConfigureAwait(false)
                : await _metadata.GetExtensionAsync(albumUri, Xm.ExtensionKind.RecommendedPlaylists, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log?.Invoke("RECOMMENDED_PLAYLISTS fetch: " + ex.Message); return Array.Empty<PlaylistSummary>(); }
        if (refsPayload is null) return Array.Empty<PlaylistSummary>();

        Xm.RecommendedPlaylists refs;
        try { refs = Xm.RecommendedPlaylists.Parser.ParseFrom(refsPayload); }
        catch (InvalidProtocolBufferException) { return Array.Empty<PlaylistSummary>(); }

        var uris = refs.Recommendation.Select(x => x.Uri).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).Take(12).ToArray();
        if (uris.Length == 0) return Array.Empty<PlaylistSummary>();

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ByteString> payloads;
        try
        {
            var requests = Array.ConvertAll(uris, u => (u, Xm.ExtensionKind.ListMetadataV2));
            payloads = _extensions is not null
                ? await _extensions.GetPayloadsAsync(requests, ct).ConfigureAwait(false)
                : await _metadata.GetExtensionsAsync(requests, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log?.Invoke("LIST_METADATA_V2 fetch: " + ex.Message); return Array.Empty<PlaylistSummary>(); }

        var result = new List<PlaylistSummary>(uris.Length);
        foreach (var uri in uris)   // preserve the recommended order
        {
            if (!payloads.TryGetValue((uri, Xm.ExtensionKind.ListMetadataV2), out var payload)) continue;
            try
            {
                var meta = Xm.ListMetadataV2.Parser.ParseFrom(payload);
                if (meta.Name.Length == 0) continue;   // a metadata-less ref is not a renderable card
                Image? cover = Cover(meta);
                string owner = meta.Source.Length > 0 ? TitleCase(meta.Source) : "Spotify";
                result.Add(new PlaylistSummary(uri, meta.Name, owner, 0, cover));
                // Project a partial playlist so clicking the card opens with an immediate hero (tracks hydrate on open).
                _store.UpsertPlaylist(new Playlist(Id(uri), uri, meta.Name,
                    SpotifyExportMapper.HtmlText(meta.Description) is { Length: > 0 } desc ? desc : null, owner, cover, 0, Source: "spotify"));
            }
            catch (InvalidProtocolBufferException ex) { _log?.Invoke("LIST_METADATA_V2 parse: " + ex.Message); }
        }
        return result;
    }

    static IReadOnlyList<Artist> Artists(IReadOnlyList<RelatedArtist> related)
    {
        var result = new List<Artist>(Math.Min(8, related.Count));
        for (int i = 0; i < related.Count && result.Count < 8; i++)
            result.Add(new Artist(related[i].Id, related[i].Uri, related[i].Name, related[i].Image));
        return result;
    }

    static Image? Cover(Xm.ListMetadataV2 meta)
    {
        var variants = meta.Images?.Variant;
        if (variants is null || variants.Count == 0) return null;
        var value = variants.FirstOrDefault(x => x.Format == "default")
            ?? variants.FirstOrDefault(x => x.Format == "large")
            ?? variants.FirstOrDefault(x => x.Url.Length > 0);
        return value is null || value.Url.Length == 0 ? null : new Image(value.Url);
    }

    static string Id(string uri)
    {
        int i = uri.LastIndexOf(':');
        return i >= 0 && i + 1 < uri.Length ? uri[(i + 1)..] : uri;
    }

    static string TitleCase(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    // NPV biographies can be paragraphs; the About card shows a short excerpt (sentence-boundary-aware, ~200 chars).
    static string? Excerpt(string? bio)
    {
        if (string.IsNullOrWhiteSpace(bio)) return null;
        var text = bio.Trim();
        if (text.Length <= 200) return text;
        int period = text.LastIndexOf('.', Math.Min(219, text.Length - 1), Math.Min(220, text.Length));
        return period > 80 ? text[..(period + 1)] : text[..200] + "…";
    }
}
