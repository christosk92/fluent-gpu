using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>
/// Fills a live playlist's cover-extracted <see cref="Palette"/> so the detail page tints like the album page. Albums
/// carry their colors inline in <c>getAlbum</c>; playlists come over the Mercury proto with none, so we resolve them
/// separately via the <c>fetchExtractedColors</c> Pathfinder query on the cover URL. A resolved (or colorless) result
/// is written to the persistent <see cref="ExtractedColorCache"/>, so a cover is fetched once ever, and merged back into
/// the resident header via a read-modify-write <c>UpsertPlaylist</c> (which bumps the store so the page re-renders).
/// Best-effort throughout: any failure just leaves the neutral default, exactly as today.
/// </summary>
public sealed class PlaylistPaletteEnricher
{
    readonly PathfinderResource _pathfinder;
    readonly IStore _store;
    readonly ExtractedColorCache _cache;
    readonly WaveeLogger _log;
    readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.Ordinal);

    public PlaylistPaletteEnricher(PathfinderResource pathfinder, IStore store, ExtractedColorCache cache, WaveeLogger log = default)
    {
        _pathfinder = pathfinder;
        _store = store;
        _cache = cache;
        _log = log;
    }

    /// <summary>Ensure the playlist has a palette. No-op if it already has one, has no cover, or a fetch is in flight.
    /// Safe to fire-and-forget — swallows all failures (including cancellation) so it never surfaces as an unobserved task.</summary>
    public async Task EnsureAsync(string playlistUri, CancellationToken ct = default)
    {
        try
        {
            var pl = _store.GetPlaylist(playlistUri);
            if (pl is null || pl.Palette is not null) return;
            var url = pl.Cover?.Url;
            if (string.IsNullOrEmpty(url)) return;

            var key = ExtractedColorCache.KeyForUrl(url);
            if (_cache.TryGet(key, out var cached))
            {
                if (cached is { } hit) ApplyPalette(playlistUri, hit);
                return;   // fresh hit (palette or known-colorless) — no fetch
            }

            if (!_inflight.TryAdd(playlistUri, 0)) return;
            try
            {
                Palette? palette;
                using (var doc = await _pathfinder.QueryAsync(
                    PathfinderOps.FetchExtractedColors, PathfinderOps.FetchExtractedColorsHash,
                    w =>
                    {
                        w.WritePropertyName("imageUris");
                        w.WriteStartArray();
                        w.WriteStringValue(url);
                        w.WriteEndArray();
                    },
                    PathfinderClient.Platform.Desktop, ct, ttl: TimeSpan.Zero).ConfigureAwait(false))
                {
                    palette = doc is null ? null : SpotifyExportMapper.PaletteFromExtractedColorsResponse(doc.RootElement);
                }

                if (palette is { } p)
                {
                    _cache.Set(key, p);   // only a real palette is authoritative; transient nulls retry next read
                    ApplyPalette(playlistUri, p);
                    _log.Info($"playlist palette resolved {playlistUri} bg=0x{p.BackgroundDark:X8}");
                }
            }
            finally
            {
                _inflight.TryRemove(playlistUri, out _);
            }
        }
        catch (Exception ex)
        {
            _log.Info($"playlist palette enrich {playlistUri}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    void ApplyPalette(string playlistUri, Palette palette)
    {
        var cur = _store.GetPlaylist(playlistUri);
        if (cur is null || cur.Palette is not null) return;   // gone or already tinted (re-read under no lock; UpsertPlaylist is the guarded write)
        _store.UpsertPlaylist(cur with { Palette = palette });
    }
}
