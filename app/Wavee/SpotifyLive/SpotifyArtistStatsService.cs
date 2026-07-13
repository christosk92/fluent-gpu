using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// ── Standalone artist-page header stats (queryArtistOverview) ────────────────────────────────────────────────────────
// The ONLY remaining discography-adjacent GraphQL call, and it is stats-only: monthly listeners / followers / world rank
// / top-track play counts / related / pinned / header image / palette. Absorbs the old LiveSessionHost.FetchArtistAsync
// + the 12h TTL that used to live in StoreLibrarySource. Deliberately NOT hung off GetArtistAsync (the shared read the
// Library master-detail also uses) — only the standalone ArtistPage calls this, so the Library surface stays 100% V4.
sealed class SpotifyArtistStatsService(PathfinderResource pf, IStore store) : IArtistStatsService
{
    static readonly TimeSpan Ttl = TimeSpan.FromHours(12);   // artist stats change slowly; revalidate on a generous window

    public async Task<Artist?> EnsureStatsAsync(string artistUri, CancellationToken ct = default)
    {
        var current = store.GetArtist(artistUri);
        // Fresh iff the overview already landed (TopTracks non-empty) AND the freshness stamp is within the TTL.
        if (current is not null && current.TopTracks is { Count: > 0 } && DateTimeOffset.UtcNow - current.FetchedAt <= Ttl)
            return current;
        try
        {
            using var doc = await pf.QueryAsync(PathfinderOps.QueryArtistOverview, PathfinderOps.QueryArtistOverviewHash,
                w => { w.WriteString("uri", artistUri); w.WriteString("locale", ""); w.WriteBoolean("preReleaseV2", false); },
                PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
            if (doc is not null && SpotifyExportMapper.ArtistFromOverview(doc.RootElement) is { Uri.Length: > 0 } overview)
                store.UpsertArtist(overview with          // STATS-ONLY write: discography fields neutralized so the
                {                                         // StoreEntityMerge Has()/0-is-unknown rules keep the V4 values.
                    TopAlbums = null, AppearsOn = null,
                    AlbumsTotal = 0, SinglesTotal = 0, CompilationsTotal = 0,
                    FetchedAt = DateTimeOffset.UtcNow,
                });
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort — a stale hash / network error still returns the store artist below */ }
        return store.GetArtist(artistUri);
    }
}
