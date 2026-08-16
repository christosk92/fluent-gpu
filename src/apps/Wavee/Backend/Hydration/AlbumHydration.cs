using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── The album ladder (design §2.3) ───────────────────────────────────────────────────────────────────────────────────
// This is LiveSessionHost.EnsureAlbumAsync/FetchAlbumAsync + SpotifyAlbumEnrichmentService's below-the-fold Full
// upgrade, re-expressed as rungs instead of one procedure with four early-outs. The V4-first policy is unchanged and
// deliberate: AlbumV4 (already resident from the prefetch, usually) gives the tracklist; the two facets V4 has no field
// for ride the SAME extended-metadata transport rather than a Pathfinder round trip (kind 185 play counts, kind 183
// ©/℗ — docs/plans/wavee/xm-kind-probe-overview.md §8); getAlbum survives ONLY as the V4-empty fallback and as the
// explicit Full envelope.
//
// What the rung split buys over the old procedure: "how much do we wait for" is the CALLER's word (OpenPolicy), not a
// constant baked into the helper — so the album page awaits Rich (©/℗ + the Plays star at first paint) while a sidebar
// hover asks Identity and pays nothing, and DetailTrailing asks Full without a second entry point.
//
// Batched by construction: every album in the batch shares ONE TrackV4 repair call and ONE trait POST. The old code
// was per-album, so opening a discography shelf fired N repairs and 2N adornment reads.
public sealed class AlbumHydration : IKindHydration
{
    readonly IStore _store;
    readonly IEnvelopeFetch _envelopes;
    readonly WaveeLogger _log;

    /// <param name="envelopes">The Pathfinder arm. Required, not nullable: a half-wired go-live must fail at the
    /// composition root, not silently degrade to "albums never reach Open" (wiring-discipline).</param>
    public AlbumHydration(IStore store, IEnvelopeFetch envelopes, WaveeLogger log = default)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
        _log = log;
    }

    public EntityKind Kind => EntityKind.Album;

    public HydrationLevel LevelOf(string uri) => HydrationLevels.Of(_store.GetAlbum(uri));

    /// <summary>Nothing extra, deliberately. Rich's ©/℗ (kind 183) rides the ONE trait POST below — the album's own uri
    /// is the first entry in <c>traitUris</c>, so <c>PublishingProjector</c> sees it there. Fusing it into step 0 as
    /// well would ask the wire for the same payload twice per open (design §2.4: 183 rides the trait POST).</summary>
    public void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into) { }

    public async Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts,
                                    HydrationContext ctx, CancellationToken ct)
    {
        if (uris.Count == 0) return;

        // Sub-asks inherit the caller's priority (a prefetch's repair must not jump an open's queue) but never its
        // surface: a TrackV4 repair is identity work, not an AlbumOpen trait ask.
        var sub = new HydrationOptions(HydrationMode.Blocking, opts.Revalidate, TraitSurface.None, opts.Priority);
        var fetched = level >= HydrationLevel.Open ? new HashSet<string>(StringComparer.Ordinal) : null;

        // ── (a) TrackV4 repair: ONE batched call for every unnamed disc row in the whole batch ───────────────────────
        // AlbumV4's disc rows are gid-only for tracks the album entity did not carry names for. Without this an album
        // opens with a list of blank rows; with it, the SAME transport that fetched the album fills them and the album
        // is rebuilt from the resident rows (port of LiveSessionHost.EnsureAlbumAsync).
        if (level >= HydrationLevel.Open)
        {
            List<string>? unnamed = null;
            for (int i = 0; i < uris.Count; i++)
            {
                if (LevelOf(uris[i].Uri) >= HydrationLevel.Open) continue;
                if (_store.GetAlbum(uris[i].Uri)?.Tracks is not { Count: > 0 } tracks) continue;
                for (int t = 0; t < tracks.Count; t++)
                    if (HydrationLevels.TrackUnnamed(tracks[t]) && tracks[t].Uri.Length > 0)
                        (unnamed ??= new List<string>()).Add(tracks[t].Uri);
            }
            if (unnamed is { Count: > 0 })
            {
                try
                {
                    await ctx.Hydrator.EnsureManyAsync(unnamed, HydrationLevel.Identity, sub, ct).ConfigureAwait(false);
                    RebuildTracklists(uris);
                    _log.Event(WaveeLogLevel.Debug, "hydration.album.repair", "unnamed disc rows repaired",
                        fields: [WaveeLogField.Of("albums", uris.Count), WaveeLogField.Of("rows", unnamed.Count)]);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Best-effort: the getAlbum fallback below is exactly the answer for a failed repair.
                    _log.Event(WaveeLogLevel.Warning, "hydration.album.repair.fail", "TrackV4 disc-row repair failed", ex: ex);
                }
            }

            // ── (b) getAlbum fallback — ONLY for albums V4 could not make openable ───────────────────────────────────
            for (int i = 0; i < uris.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (LevelOf(uris[i].Uri) >= HydrationLevel.Open) continue;
                if (await FetchEnvelopeAsync(uris[i].Uri, "fallback", ctx, ct).ConfigureAwait(false)) fetched!.Add(uris[i].Uri);
            }
        }

        // ── (c) Rich: ONE awaited trait POST for the album + every disc row ──────────────────────────────────────────
        // RowBundle|PlayCount|Publishing through ONE door replaces the old plays + publishing + FillAlbumAdornments +
        // the second 185 read. Awaited on purpose: the ©/℗ line and the top-track star are FIRST-PAINT content, and
        // both answers are a few hundred etag-cached bytes.
        if (level >= HydrationLevel.Rich)
        {
            var traitUris = new List<string>(uris.Count * 12);
            for (int i = 0; i < uris.Count; i++)
            {
                traitUris.Add(uris[i].Uri);
                AppendTrackUris(uris[i].Uri, traitUris);
            }
            try
            {
                await ctx.Traits.EnsureAsync(traitUris, TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing,
                    TraitSurface.AlbumOpen, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Traits are polish by contract — a mapper throw must never turn a renderable album into a failed open.
                // But it IS the step the Rich rung is made of: ©/℗ and the row bundle come from here and nowhere else,
                // so a swallowed failure that left the album short of Rich must seal on the short window. Otherwise one
                // blip cost the publishing line and the RowBundle for a full day (ExhaustedAlbumRichTtl).
                for (int i = 0; i < uris.Count; i++) ctx.ReportTransient(uris[i].Uri);
                _log.Event(WaveeLogLevel.Warning, "hydration.album.traits.fail", "album trait pass failed", ex: ex);
            }

            // RE-JOIN, for exactly the reason step (a) does after its repair: the trait pass writes onto the shared
            // TRACK plane (kind 185 → Track.PlayCount, 222 → TempoBpm, 6 → Tags), while an album's `Tracks` is a
            // DENORMALIZED copy the album page reads verbatim (DetailPage.MapAlbum → DetailTracks.TrackAt/TopTrack).
            // Without this the awaited Rich pass buys nothing the user can see: every Plays cell paints "—" and no
            // track gets the star until the below-the-fold getAlbum lands its own tracklist a round trip later. Before
            // the façade the counts arrived INSIDE the envelope's tracklist, which is why nothing had to re-join them.
            // The sibling ladder does the same thing for the same reason (ArtistHydration's "read them back off the
            // rows"), and the rebuild is free — it re-upserts only when a row instance actually changed.
            RebuildTracklists(uris);
        }

        // ── (d) Full: the getAlbum envelope (label / OtherVersions / ArtistsDetailed / playability / MoreBy) ─────────
        // The below-the-fold upgrade SpotifyAlbumEnrichmentService.GetRecommendedPlaylistsAsync used to trigger, now an
        // explicit rung the trailing surface asks for. Skips albums (b) already fetched this pass.
        if (level >= HydrationLevel.Full)
        {
            for (int i = 0; i < uris.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (fetched is not null && fetched.Contains(uris[i].Uri)) continue;
                if (LevelOf(uris[i].Uri) >= HydrationLevel.Full) continue;
                await FetchEnvelopeAsync(uris[i].Uri, "full", ctx, ct).ConfigureAwait(false);
            }
        }

        // ── (e) Post-step: an Open-only ask still wants its row facets, just not on the critical path ────────────────
        // (Identity never gets here — nothing painted a row list to decorate.)
        else if (level == HydrationLevel.Open)
        {
            var rowUris = new List<string>(uris.Count * 12);
            for (int i = 0; i < uris.Count; i++) AppendTrackUris(uris[i].Uri, rowUris);
            if (rowUris.Count > 0)
                ctx.Pump.Enqueue(opts.Priority, async pct =>
                {
                    await ctx.Traits.EnsureAsync(rowUris, TraitSet.RowBundle, TraitSurface.AlbumOpen, pct).ConfigureAwait(false);
                    RebuildTracklists(uris);   // same re-join as the Rich path — the facets land on the ROWS
                });
        }
    }

    /// <summary>Re-read every album's disc rows from the store and write the tracklist back. The repair wrote TRACK
    /// entities; an album's embedded <c>Tracks</c> list is a denormalized copy, so it only heals if it is rebuilt from
    /// the rows the repair just landed (LiveSessionHost.EnsureAlbumAsync did exactly this).</summary>
    void RebuildTracklists(IReadOnlyList<EntityUri> uris)
    {
        for (int i = 0; i < uris.Count; i++)
        {
            if (_store.GetAlbum(uris[i].Uri) is not { } album || album.Tracks is not { Count: > 0 } tracks) continue;
            var rebuilt = new List<Track>(tracks.Count);
            bool changed = false;
            for (int t = 0; t < tracks.Count; t++)
            {
                var row = _store.GetTrack(tracks[t].Uri);
                if (row is not null && !ReferenceEquals(row, tracks[t])) changed = true;
                rebuilt.Add(row ?? tracks[t]);
            }
            if (changed) _store.UpsertAlbum(album with { Tracks = rebuilt });
        }
    }

    /// <summary>getAlbum → store, byte-for-byte the write LiveSessionHost.FetchAlbumAsync performed: the detailed
    /// artists first, then the tracklist AS ENTITIES (CachedStore.PersistAlbum strips <c>Tracks</c>, so a verdict
    /// carried only on the in-memory album is forgotten across a restart — routing each row through
    /// <c>UpsertTrack</c> puts it on the same merge and pin rules every other adornment uses), then the album.</summary>
    async Task<bool> FetchEnvelopeAsync(string uri, string why, HydrationContext ctx, CancellationToken ct)
    {
        Album? album;
        try { album = await _envelopes.AlbumAsync(uri, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // TRANSIENT, not "this album has no envelope": a null answer below is the real absence. Without the
            // distinction a 503 here sealed the rung as exhausted on the long window.
            ctx.ReportTransient(uri);
            _log.Event(WaveeLogLevel.Warning, "hydration.album.envelope.fail", "getAlbum failed", uri, ex: ex,
                fields: [WaveeLogField.Of("why", why)]);
            return false;
        }
        if (album is null) return false;

        if (album.ArtistsDetailed is { Count: > 0 } detailed)
            for (int i = 0; i < detailed.Count; i++) _store.UpsertArtist(detailed[i]);
        if (album.Tracks is { Count: > 0 } rows)
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Uri.Length > 0) _store.UpsertTrack(rows[i]);
        _store.UpsertAlbum(album);

        _log.Event(WaveeLogLevel.Info, "hydration.album.envelope", "getAlbum landed", uri,
            fields: [WaveeLogField.Of("why", why), WaveeLogField.Of("tracks", album.Tracks?.Count ?? 0)]);
        return true;
    }

    void AppendTrackUris(string albumUri, List<string> into)
    {
        if (_store.GetAlbum(albumUri)?.Tracks is not { Count: > 0 } tracks) return;
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Uri.Length > 0) into.Add(tracks[i].Uri);
    }
}
