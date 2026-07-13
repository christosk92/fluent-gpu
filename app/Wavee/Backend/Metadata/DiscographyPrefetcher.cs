using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Metadata;

// ── Sign-in discography prefetch (tiered waves) ──────────────────────────────────────────────────────────────────────
// NOT a SyncKind on the sync loop — that loop is the single serialized writer that also serves OpenPlaylist, and a
// minutes-long prefetch there would starve every interactive playlist open. It runs as a background task, gated on the
// session cts.Token, started once InitialHydrate completes (LiveSessionHost §7d). Batching/gzip/etag/SWR-skip all come
// free from md.SyncAllAsync; everything persists via CachedStore → SQLite, so next launch paints the discography offline.
public static class DiscographyPrefetcher
{
    const int BatchSize = 500;            // uris per SyncAllAsync call (it body-chunks further); keeps HTTP fair
    static readonly TimeSpan Breather = TimeSpan.FromMilliseconds(250);   // between batches — never starve on-open fetches

    public static async Task RunAsync(MetadataService md, IStore store, WaveeLogger log, CancellationToken ct)
    {
        var artists = store.SavedUris("artists");
        if (artists.Count == 0) return;

        // Wave 1 — ArtistV4 for every saved artist: stub TopAlbums + facet totals + appears-on stubs + bio.
        await SyncBatchesAsync(md, artists, "wave1", log, ct).ConfigureAwait(false);

        // Wave 2 — AlbumV4 cards for the OWN discography only (TopAlbums; appears-on is never prefetched), deduped.
        var albumUris = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in artists)
            foreach (var stub in store.GetArtist(uri)?.TopAlbums ?? Array.Empty<Album>())
                if (seen.Add(stub.Uri)) albumUris.Add(stub.Uri);
        await SyncBatchesAsync(md, albumUris, "wave2", log, ct).ConfigureAwait(false);
        foreach (var uri in artists) ArtistDiscography.Assemble(store, uri);
        log.Info($"discography prefetch: {artists.Count} artists, {albumUris.Count} releases assembled");

        // Wave 3 — TrackV4 for gid-only disc tracks (named tracklists offline), lowest priority; rebuild each album.
        var trackUris = new List<string>(); var byAlbum = new List<string>();
        foreach (var albumUri in albumUris)
            if (store.GetAlbum(albumUri)?.Tracks is { Count: > 0 } tracks && tracks.Any(t => t.Title.Length == 0))
            { byAlbum.Add(albumUri); foreach (var t in tracks) if (t.Title.Length == 0) trackUris.Add(t.Uri); }
        await SyncBatchesAsync(md, trackUris, "wave3", log, ct).ConfigureAwait(false);
        foreach (var albumUri in byAlbum)
            if (store.GetAlbum(albumUri) is { Tracks: { Count: > 0 } tracks } album)
                store.UpsertAlbum(album with { Tracks = tracks.Select(t => store.GetTrack(t.Uri) ?? t).ToList() });
        log.Info($"discography prefetch: {trackUris.Count} tracks enriched across {byAlbum.Count} albums");
    }

    // Per-batch isolation: one failed POST must not abort the whole run — a dead wave 3 is exactly what strands the
    // store full of gid-only (title-less) tracks. Failed batches are logged and skipped; the on-open EnsureAlbumAsync
    // path (unnamed-track gate) heals whatever a skipped batch left behind.
    static async Task SyncBatchesAsync(MetadataService md, IReadOnlyList<string> uris, string wave, WaveeLogger log, CancellationToken ct)
    {
        int failed = 0;
        foreach (var batch in Batches(uris))
        {
            ct.ThrowIfCancellationRequested();
            try { await md.SyncAllAsync(batch, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { if (failed++ == 0) log.Info($"discography prefetch {wave}: batch failed ({ex.Message}), continuing"); }
            await Task.Delay(Breather, ct).ConfigureAwait(false);
        }
        if (failed > 1) log.Info($"discography prefetch {wave}: {failed} batches failed");
    }

    static IEnumerable<IReadOnlyList<string>> Batches(IReadOnlyList<string> uris)
    {
        for (int i = 0; i < uris.Count; i += BatchSize)
            yield return uris.Skip(i).Take(BatchSize).ToList();
    }
}
