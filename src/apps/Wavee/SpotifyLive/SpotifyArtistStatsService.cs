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
sealed class SpotifyArtistStatsService(PathfinderResource pf, IStore store, WaveeLogger log = default) : IArtistStatsService
{
    static readonly TimeSpan Ttl = TimeSpan.FromHours(12);   // artist stats change slowly; revalidate on a generous window

    public async Task<Artist?> EnsureStatsAsync(string artistUri, CancellationToken ct = default)
    {
        var current = store.GetArtist(artistUri);
        // Fresh iff the overview already landed (TopTracks non-empty), it carries the release facets, AND the
        // freshness stamp is within the TTL. The facet check is a schema upgrade gate: CachedStore persists the
        // MAPPED Artist, so records saved before LatestRelease/PopularReleases existed deserialize with both null
        // while still stamped fresh — without the check those artists show no releases column for a whole TTL.
        // Play counts need no gate of their own: they are stored WITH the chart (ArtistOverviewDoc.TopTracks) and
        // joined back on re-fatten, so they cannot be lost to another writer of the shared track row.
        bool hasReleaseFacets = current?.LatestRelease is not null || current?.PopularReleases is { Count: > 0 };
        if (current is not null && current.TopTracks is { Count: > 0 } && hasReleaseFacets
            && DateTimeOffset.UtcNow - current.FetchedAt <= Ttl)
            return current;
        try
        {
            using var doc = await pf.QueryAsync(PathfinderOps.QueryArtistOverview, PathfinderOps.QueryArtistOverviewHash,
                // Wire-exact (artist_more.saz, omg.saz): locale is "" and preReleaseV2 is TRUE. preReleaseV2:true is
                // what makes artistUnion.preReleaseV2 (the upcoming-release card) populate at all.
                w => { w.WriteString("uri", artistUri); w.WriteString("locale", ""); w.WriteBoolean("preReleaseV2", true); },
                // WebPlayer identity: the captured desktop client serves queryArtistOverview from its web-player
                // bundle (spotify-app-version 896000000), not the desktop one.
                PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
            if (doc is not null && SpotifyExportMapper.ArtistFromOverview(doc.RootElement) is { Uri.Length: > 0 } overview)
            {
                store.UpsertArtist(overview with          // STATS-ONLY write: discography fields neutralized so the
                {                                         // StoreEntityMerge Has()/0-is-unknown rules keep the V4 values.
                    TopAlbums = null, AppearsOn = null,
                    AlbumsTotal = 0, SinglesTotal = 0, CompilationsTotal = 0,
                    FetchedAt = DateTimeOffset.UtcNow,
                });
                // Hydrate the shared track rows from the overview too: these are real titles/artists/albums/durations
                // for rows a thin cluster or library write may only know as a gid, and the merge keeps whichever
                // fields the writer actually knew. The CHART's play counts do not depend on this — they persist with
                // the chart itself (ArtistOverviewDoc) precisely so no other writer of these rows can drop them.
                int withCounts = 0;
                if (overview.TopTracks is { Count: > 0 } landed)
                    for (int i = 0; i < landed.Count; i++)
                    {
                        if (landed[i].Uri.Length == 0) continue;
                        store.UpsertTrack(landed[i]);
                        if (landed[i].PlayCount > 0) withCounts++;
                    }
                // This service used to speak ONLY when it failed, which made "did the overview run?" unanswerable from
                // a log — the exact question the missing-play-count hunt kept asking. Say so on success too.
                log.Event(WaveeLogLevel.Info, "stats.overview.ok", "artist overview landed", artistUri,
                    fields:
                    [
                        WaveeLogField.Of("topTracks", overview.TopTracks?.Count ?? 0),
                        WaveeLogField.Of("withPlayCounts", withCounts),
                    ]);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Best-effort — a stale hash / network error still returns the store artist below. But it must be VISIBLE:
            // a silently-swallowed overview failure is indistinguishable from "this artist has no stats", and it also
            // leaves the chart on its cold seed with no trace of why.
            log.Event(WaveeLogLevel.Warning, "stats.overview.fail", "queryArtistOverview failed", artistUri, ex: ex);
        }
        return store.GetArtist(artistUri);
    }
}
