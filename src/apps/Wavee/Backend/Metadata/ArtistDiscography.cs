using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Metadata;

// ── Artist discography: V4 ensure + assemble ─────────────────────────────────────────────────────────────────────────
// The single writer of hydrated discography cards, shared by the on-open path (LiveSessionHost.OnDemandFetch) and the
// sign-in prefetch (DiscographyPrefetcher). ArtistV4 carries the whole discography as gid-only stubs (facet totals =
// group counts); AlbumV4 upgrades each stub to a resident card; assembly folds the resident cards back onto the Artist
// row (DATE_DESC, tracklists stripped). Uses only MetadataService + IStore — no Pathfinder, no GraphQL.
public static class ArtistDiscography
{
    const int AppearsOnHydrateCap = 20;   // the on-open shelf slice; the full appears-on set is never bulk-hydrated

    /// <summary>V4 ensure: ArtistV4 (stubs + totals) → AlbumV4 for un-hydrated own-discography stubs → assemble.
    /// Cheap when fresh — MetadataService SWR/etag skips resident entities, so calling on every open is fine.</summary>
    public static async Task EnsureAsync(MetadataService md, IStore store, string artistUri, CancellationToken ct,
        bool hydrateAppearsOn = false)
    {
        await md.SyncAllAsync(new[] { artistUri }, ct).ConfigureAwait(false);
        var artist = store.GetArtist(artistUri);
        if (artist?.TopAlbums is not { Count: > 0 } stubs) return;

        var need = new List<string>();
        foreach (var s in stubs)
            if (s.Name.Length == 0 || store.GetAlbum(s.Uri) is null) need.Add(s.Uri);
        if (hydrateAppearsOn && artist.AppearsOn is { } appears)
            for (int i = 0; i < appears.Count && i < AppearsOnHydrateCap; i++)
                if (appears[i].Name.Length == 0) need.Add(appears[i].Uri);
        if (need.Count > 0) await md.SyncAllAsync(need, ct).ConfigureAwait(false);
        Assemble(store, artistUri);
    }

    /// <summary>Upgrade stub cards to resident AlbumV4 cards, sorted DATE_DESC, tracklists STRIPPED (an Artist row must
    /// not embed hundreds of tracklists into its persisted JSON). Idempotent; the store merge (MergeAlbumCards) makes it
    /// clobber-safe.</summary>
    public static void Assemble(IStore store, string artistUri)
    {
        var artist = store.GetArtist(artistUri);
        if (artist?.TopAlbums is not { Count: > 0 } stubs) return;

        var cards = new List<Album>(stubs.Count);
        foreach (var s in stubs)
            cards.Add(store.GetAlbum(s.Uri) is { Name.Length: > 0 } full
                ? full with { Kind = s.Kind, Tracks = null, MoreByArtist = null, ArtistsDetailed = null, OtherVersions = null }
                : s);
        cards.Sort(static (a, b) => b.Year.CompareTo(a.Year));   // DATE_DESC (newest first) — matches the GraphQL facet order
        var next = artist with { TopAlbums = cards };

        if (artist.AppearsOn is { Count: > 0 } appears)
        {
            var ap = new List<Album>(appears.Count);
            foreach (var s in appears)
                ap.Add(store.GetAlbum(s.Uri) is { Name.Length: > 0 } full
                    ? full with { Tracks = null, MoreByArtist = null, ArtistsDetailed = null, OtherVersions = null } : s);
            next = next with { AppearsOn = ap };
        }
        store.UpsertArtist(next);
    }
}
