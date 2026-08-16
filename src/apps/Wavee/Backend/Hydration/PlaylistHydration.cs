using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── the playlist ladder (design §2.3) ────────────────────────────────────────────────────────────────────────────────
// THE INVARIANT: this ladder never writes membership. The playlist plane (baseline, revision, dirty set, dealer diffs,
// mutations) is owned by the LibrarySync writer loop; the ladder only ASKS it, through IPlaylistOpener. That is why
// `sync.OnPlaylistHydrated` dies and why the ledger never TTL-seals a playlist Open on its own — LibrarySync's
// in-flight set and 5-minute window remain the freshness authority (plan §4 risk 2).
public sealed class PlaylistHydration : IKindHydration
{
    readonly IStore _store;
    readonly IPlaylistOpener _opener;
    readonly TraitPolicy _policy;
    readonly WaveeLogger _log;

    public PlaylistHydration(IStore store, IPlaylistOpener opener, TraitPolicy policy, WaveeLogger log = default)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _log = log;
    }

    public EntityKind Kind => EntityKind.Playlist;

    public HydrationLevel LevelOf(string uri)
        => HydrationLevels.Of(_store.GetPlaylist(uri), _store.HasMembership(uri));

    /// <summary>Nothing extra: a playlist's catalogue answer is LIST_METADATA_V2 (205) alone.</summary>
    public void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into) { }

    public async Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts,
                                    HydrationContext ctx, CancellationToken ct)
    {
        for (int i = 0; i < uris.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string uri = uris[i].Uri;

            // ── Identity ─────────────────────────────────────────────────────────────────────────────────────────────
            // Step 0's 205 is the general answer. A ROOTLIST member has a second, authoritative one — the header GET
            // LibrarySync already speaks — so it is the fallback for the case 205 cannot serve (a user-namespaced or
            // freshly-created list the catalogue does not carry). Asking for it only when the row is STILL unnamed is
            // what keeps the shared step-0 POST the common path instead of a per-playlist round trip.
            if (LevelOf(uri) < HydrationLevel.Identity && IsRootlistMember(uri))
            {
                try { await _opener.HeaderAsync(uri, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Warn("hydration.playlist.header", "playlist header fetch failed", uri, ex); }
            }

            if (level < HydrationLevel.Open) continue;

            // ── Open ─────────────────────────────────────────────────────────────────────────────────────────────────
            // No baseline ⇒ there is nothing to paint, so the open BLOCKS on LibrarySync's real open. With a baseline
            // it is a revalidation: enqueue and let the loop's own 5-minute/dirty gates decide whether anything fetches.
            bool hadBaseline = _store.HasMembership(uri);
            try
            {
                if (!hadBaseline) await _opener.OpenAsync(uri, ct).ConfigureAwait(false);
                else _opener.Revalidate(uri);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Warn("hydration.playlist.open", "playlist open failed", uri, ex); }

            // ── post-step: the members' traits, on the pump ───────────────────────────────────────────────────────────
            // EVERY member, episodes included — that is the whole reason the trait door is addressed by uri rather than
            // by a `spotify:track:` prefix test. Nothing on screen waits for it.
            var members = _store.Membership(uri);
            if (members.Count == 0) continue;
            var memberUris = new List<string>(members.Count);
            for (int m = 0; m < members.Count; m++)
                if (members[m].ItemUri is { Length: > 0 } item) memberUris.Add(item);
            if (memberUris.Count == 0) continue;
            var traits = _policy.For(TraitSurface.PlaylistOpen);
            if (traits == TraitSet.None) continue;
            ctx.Pump.Enqueue(opts.Priority - 1,
                pumpCt => ctx.Hydrator.EnsureTraitsAsync(memberUris, traits, TraitSurface.PlaylistOpen, pumpCt));
        }
    }

    bool IsRootlistMember(string uri)
    {
        var root = _store.Rootlist();
        for (int i = 0; i < root.Count; i++)
            if (string.Equals(root[i].Uri, uri, StringComparison.Ordinal)) return true;
        return false;
    }

    void Warn(string eventId, string message, string uri, Exception ex)
        => _log.Event(WaveeLogLevel.Warning, eventId, message, ex: ex, fields: [WaveeLogField.Of("uri", uri)]);
}
