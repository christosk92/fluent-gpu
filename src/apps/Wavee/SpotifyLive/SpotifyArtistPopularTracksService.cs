using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// ── The artist chart's step two: artist-top-tracks-extensions ────────────────────────────────────────────────────────
// One authed SpClient GET returns the artist's FULL popular list as bare uris (~50). The uris are hydrated over the
// SHARED extended-metadata transport (MetadataService.SyncAllAsync → TrackV4 → Store) rather than a second fetcher, then
// folded onto the Pathfinder overview seed by ArtistPopularTracks.Merge — the seed keeps the head AND the play counts
// (this endpoint carries none), extension-only tracks append.
//
// Wiring is REQUIRED, not optional: every dependency is a non-nullable ctor arg, so a half-wired go-live fails at the
// composition root instead of silently degrading to a 10-row chart forever. What IS best-effort is the network: a
// non-2xx / parse failure / metadata miss returns the seed and logs a structured event, never blanks a painted chart.
sealed class SpotifyArtistPopularTracksService : IArtistPopularTracksService
{
    // Same window as SpotifyArtistStatsService. Deliberately reads the SAME Artist.FetchedAt stamp: the stats overview
    // write rewrites TopTracks back to the ~10 seed, so on the tick where stats refetches this must refetch too. The
    // "already extended" gate is therefore (count > OverviewSeedCap) AND (stats stamp still fresh).
    static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly MetadataService _metadata;
    readonly IStore _store;
    readonly WaveeLogger _log;
    // One shared load per artist. Callers await it through WaitAsync(ct), so a navigation-away cancels the AWAIT without
    // cancelling work a second page may still be joined to; the store write is uri-keyed, so a late landing is correct.
    readonly ConcurrentDictionary<string, Task<IReadOnlyList<Track>>> _inFlight = new(StringComparer.Ordinal);

    public SpotifyArtistPopularTracksService(IHttpExchange http, Func<string> baseUrl, MetadataService metadata,
                                             IStore store, WaveeLogger log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
    }

    /// <summary>Set by the go-live wiring (property, not a ctor arg — the video service is constructed AFTER this one):
    /// batch music-video detection for the merged chart. Best-effort; null offline/in tests → no detection.</summary>
    public Func<IReadOnlyList<string>, Task>? DetectVideos { get; set; }

    public async Task<IReadOnlyList<Track>> EnsureExtendedAsync(string artistUri, IReadOnlyList<Track> seed, CancellationToken ct = default)
    {
        seed ??= Array.Empty<Track>();
        // Spotify-catalog artists only: a local/synthetic artist uri has no play-context page to extend.
        if (!artistUri.StartsWith("spotify:artist:", StringComparison.Ordinal)) return seed;

        var current = _store.GetArtist(artistUri);
        if (current?.TopTracks is { Count: > ArtistPopularTracks.OverviewSeedCap } stored
            && DateTimeOffset.UtcNow - current.FetchedAt <= Ttl)
        {
            _log.Event(WaveeLogLevel.Debug, "popular.ensure.cache_hit", "extended top tracks served from the store",
                artistUri, fields: [WaveeLogField.Of("count", stored.Count)]);
            return stored;
        }

        var task = _inFlight.GetOrAdd(artistUri, static (uri, s) => s.self.LoadAsync(uri, s.seed), (self: this, seed));
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<Track>> LoadAsync(string artistUri, IReadOnlyList<Track> seed)
    {
        var sw = Stopwatch.StartNew();
        _log.Event(WaveeLogLevel.Debug, "popular.ensure.start", "extending the artist top-track chart",
            artistUri, fields: [WaveeLogField.Of("seedCount", seed.Count)]);
        try
        {
            var uris = await FetchExtensionUrisAsync(artistUri, sw).ConfigureAwait(false);
            if (uris.Count == 0) return Done(artistUri, seed, sw, "seed");

            await _metadata.SyncAllAsync(uris, CancellationToken.None).ConfigureAwait(false);

            var resolved = new List<Track>(uris.Count);
            for (int i = 0; i < uris.Count; i++)
                if (_store.GetTrack(uris[i]) is { } t) resolved.Add(t);
            _log.Event(WaveeLogLevel.Debug, "popular.enrich", "extension uris hydrated", artistUri,
                fields: [WaveeLogField.Of("requested", uris.Count), WaveeLogField.Of("resolved", resolved.Count), WaveeLogField.Of("missing", uris.Count - resolved.Count)]);

            // Re-read the store: stats may have landed a richer (play-count-bearing) seed while this was in flight.
            var head = _store.GetArtist(artistUri)?.TopTracks is { Count: > 0 } live ? live : seed;
            var merged = ArtistPopularTracks.Merge(head, resolved);
            _log.Event(WaveeLogLevel.Info, "popular.merge", "extended chart merged", artistUri,
                fields:
                [
                    WaveeLogField.Of("overview", head.Count), WaveeLogField.Of("extension", resolved.Count),
                    WaveeLogField.Of("merged", merged.Count), WaveeLogField.Of("appended", ArtistPopularTracks.AppendedCount(head, merged)),
                ]);

            if (merged.Count > head.Count && _store.GetArtist(artistUri) is { } artist)
                _store.UpsertArtist(artist with { TopTracks = merged });   // TopTracks ONLY — FetchedAt stays the stats stamp

            // The chart's ~60 uris are the artist page's only track list; detect their music videos off the merge (the
            // page's albums detect on their own open). Fire-and-forget — a failure never touches the painted chart.
            if (DetectVideos is { } detect && merged.Count > 0)
            {
                var detectUris = new List<string>(merged.Count);
                for (int i = 0; i < merged.Count; i++) detectUris.Add(merged[i].Uri);
                try { _ = detect(detectUris); } catch { }
            }

            return Done(artistUri, merged, sw, "network");
        }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "popular.ensure.error", "extended top tracks failed", artistUri,
                sw.ElapsedMilliseconds, ex);
            return seed;
        }
        finally
        {
            _inFlight.TryRemove(artistUri, out _);
        }
    }

    async Task<IReadOnlyList<string>> FetchExtensionUrisAsync(string artistUri, Stopwatch sw)
    {
        var url = _baseUrl() + "/artistplaycontext/v1/page/spotify/artist-top-tracks-extensions/" + Uri.EscapeDataString(artistUri);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
        using var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), CancellationToken.None).ConfigureAwait(false);
        if (resp.Status is < 200 or > 299)
        {
            _log.Event(WaveeLogLevel.Warning, "popular.http.fail", "artist-top-tracks-extensions rejected", artistUri,
                sw.ElapsedMilliseconds, fields: [WaveeLogField.Of("status", resp.Status)]);
            return Array.Empty<string>();
        }

        using var doc = await JsonDocument.ParseAsync(resp.Body, default, CancellationToken.None).ConfigureAwait(false);
        var uris = new List<string>(ArtistPopularTracks.ExtendedCap);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in tracks.EnumerateArray())
            {
                if (uris.Count >= ArtistPopularTracks.ExtendedCap) break;   // bounds the metadata batch AND the persisted uri list
                if (track.ValueKind == JsonValueKind.Object && track.TryGetProperty("uri", out var uri)
                    && uri.GetString() is { Length: > 0 } u)
                    uris.Add(u);
            }
        }
        _log.Event(WaveeLogLevel.Debug, "popular.http.ok", "artist-top-tracks-extensions fetched", artistUri,
            sw.ElapsedMilliseconds, fields: [WaveeLogField.Of("status", resp.Status), WaveeLogField.Of("uriCount", uris.Count)]);
        return uris;
    }

    IReadOnlyList<Track> Done(string artistUri, IReadOnlyList<Track> result, Stopwatch sw, string source)
    {
        _log.Event(WaveeLogLevel.Info, "popular.ensure.done", "extended top tracks ready", artistUri,
            sw.ElapsedMilliseconds, fields: [WaveeLogField.Of("count", result.Count), WaveeLogField.Of("source", source)]);
        return result;
    }
}
