using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>The authoritative subset of a stored playlist header that can replace a shallow Home identity.</summary>
internal readonly record struct HomePlaylistHeader(
    string Title, string? Subtitle, string? OwnerName, Image? Cover, int TrackCount);

/// <summary>
/// Resolves provider-marked shallow Daylist cards before Home becomes Ready. Duplicate occurrences share one header
/// read, the Home transport is requeried at most once per distinct resolved identity (never once per read — see
/// <see cref="ClaimRequery"/>), and every overlay is applied to both presentation groups and the lossless section
/// ledger. The class owns orchestration only; its three required seams keep it engine-free and directly testable
/// without weakening live wiring.
/// </summary>
/// <remarks>
/// INSTANCE STATE: one hydrator must be held for the lifetime of the Home source it serves. A per-read instance would
/// forget which identities it has already requeried and reintroduce the once-per-read invalidation this class exists
/// to bound.
/// </remarks>
internal sealed class HomeDaylistHydrator
{
    readonly Func<string, HomePlaylistHeader?> _readHeader;
    readonly Func<string, CancellationToken, Task> _fetchHeader;
    readonly Func<CancellationToken, Task<LiveHomeResult>> _refreshHome;

    // uri → the exact title we have already spent one invalidating Home requery on. Keyed by identity rather than by
    // read, because the resident store answers every later read for free while the requery is an UNCACHED network
    // fetch. The key space is the set of hydration-marked Home cards (Spotify marks only a daylist whose name is empty
    // or equal to its daylist_pretitle), so this stays a handful of entries per session.
    readonly Dictionary<string, string> _requeried = new(StringComparer.Ordinal);

    public HomeDaylistHydrator(
        Func<string, HomePlaylistHeader?> readHeader,
        Func<string, CancellationToken, Task> fetchHeader,
        Func<CancellationToken, Task<LiveHomeResult>> refreshHome)
    {
        ArgumentNullException.ThrowIfNull(readHeader);
        ArgumentNullException.ThrowIfNull(fetchHeader);
        ArgumentNullException.ThrowIfNull(refreshHome);
        _readHeader = readHeader;
        _fetchHeader = fetchHeader;
        _refreshHome = refreshHome;
    }

    public async Task<LiveHomeResult> ResolveAsync(LiveHomeResult source, CancellationToken ct)
    {
        var shallow = ShallowCards(source);
        if (shallow.Count == 0) return source;

        var exact = new Dictionary<string, HomePlaylistHeader>(shallow.Count, StringComparer.Ordinal);
        List<KeyValuePair<string, HomeCard>>? pending = null;
        foreach (var pair in shallow)
        {
            ct.ThrowIfCancellationRequested();
            if (TryExact(pair.Value, _readHeader(pair.Key), out var resident)) exact.Add(pair.Key, resident);
            else (pending ??= new List<KeyValuePair<string, HomeCard>>(shallow.Count)).Add(pair);
        }

        if (pending is { Count: > 0 })
        {
            // The map is already keyed by URI, so the fan-out is deduplicated before it starts; issuing the misses
            // together keeps first paint off an N-round-trip serial chain. Each miss swallows its own failure — one
            // unavailable playlist must not fail or delete the rest of Home, and there is deliberately no title
            // synthesis from tags — and cancellation is re-asserted once afterwards so an abandoned batch still
            // propagates instead of surfacing as a silent partial hydration.
            if (pending.Count == 1)
            {
                await FetchQuietAsync(pending[0].Key, ct).ConfigureAwait(false);
            }
            else
            {
                var fetches = new Task[pending.Count];
                for (int i = 0; i < fetches.Length; i++) fetches[i] = FetchQuietAsync(pending[i].Key, ct);
                await Task.WhenAll(fetches).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < pending.Count; i++)
                if (TryExact(pending[i].Value, _readHeader(pending[i].Key), out var fetched))
                    exact.Add(pending[i].Key, fetched);
        }

        if (exact.Count == 0) return source;

        LiveHomeResult basis = source;
        // A resident header is already enough to RENDER the card; the Home requery only gives the transport body its own
        // chance to carry the exact identity. Because it invalidates and refetches UNCACHED, it must never ride the read
        // cadence: Home is polled on a 60 s timer, and firing per read pinned Home permanently off the Pathfinder TTL.
        if (ClaimRequery(exact) is { } claimed)
        {
            try
            {
                var refreshed = await _refreshHome(ct).ConfigureAwait(false);
                if (HasContent(refreshed)) basis = refreshed;
            }
            catch (OperationCanceledException)
            {
                Unclaim(claimed);   // the attempt never completed; do not spend this identity's one requery on it
                throw;
            }
            catch
            {
                // The exact stored headers are already authoritative. A failed Home requery must not make the successful
                // hydration disappear, and the original source ledger remains the accounting baseline. The claim stands:
                // a failing requery must not turn into a per-read retry storm either.
            }
        }

        return Overlay(basis, exact);
    }

    async Task FetchQuietAsync(string uri, CancellationToken ct)
    {
        try { await _fetchHeader(uri, ct).ConfigureAwait(false); }
        catch { /* per-URI: the raw provider card stays the truthful fallback; ct is re-checked by the caller */ }
    }

    /// <summary>Reserves the single Home requery owed to any identity in <paramref name="exact"/> that has not had one
    /// yet, returning the newly claimed URIs, or null when every identity is already accounted for. A daylist retitles
    /// through the day, so a NEW exact title for a known URI earns one more requery — an unchanged one earns none.</summary>
    List<string>? ClaimRequery(Dictionary<string, HomePlaylistHeader> exact)
    {
        List<string>? claimed = null;
        lock (_requeried)
        {
            foreach (var pair in exact)
            {
                if (_requeried.TryGetValue(pair.Key, out var seen)
                    && string.Equals(seen, pair.Value.Title, StringComparison.Ordinal)) continue;
                _requeried[pair.Key] = pair.Value.Title;
                (claimed ??= new List<string>(exact.Count)).Add(pair.Key);
            }
        }
        return claimed;
    }

    void Unclaim(List<string> claimed)
    {
        lock (_requeried)
            for (int i = 0; i < claimed.Count; i++) _requeried.Remove(claimed[i]);
    }

    static Dictionary<string, HomeCard> ShallowCards(LiveHomeResult source)
    {
        var result = new Dictionary<string, HomeCard>(StringComparer.Ordinal);
        for (int g = 0; g < source.Groups.Count; g++) Add(source.Groups[g].Cards, result);
        if (source.Sections is { } sections)
            for (int s = 0; s < sections.Count; s++) Add(sections[s].Cards, result);
        return result;

        static void Add(IReadOnlyList<HomeCard> cards, Dictionary<string, HomeCard> target)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card.Meta?.NeedsHydration != true || card.Uri.Length == 0) continue;
                target.TryAdd(card.Uri, card);
            }
        }
    }

    static bool TryExact(HomeCard shallow, HomePlaylistHeader? candidate, out HomePlaylistHeader exact)
    {
        if (candidate is { } header
            && !string.IsNullOrWhiteSpace(header.Title)
            && !string.Equals(header.Title, shallow.Title, StringComparison.Ordinal)
            && !string.Equals(header.Title, shallow.Meta?.GenericTitle, StringComparison.Ordinal))
        {
            exact = header;
            return true;
        }

        exact = default;
        return false;
    }

    static bool HasContent(LiveHomeResult result) => result.Groups.Count > 0 || result.Sections is { Count: > 0 };

    static LiveHomeResult Overlay(LiveHomeResult source, IReadOnlyDictionary<string, HomePlaylistHeader> exact)
    {
        bool changed = false;
        var groups = new HomeGroup[source.Groups.Count];
        for (int i = 0; i < groups.Length; i++)
        {
            var group = source.Groups[i];
            var cards = OverlayCards(group.Cards, exact, ref changed);
            groups[i] = ReferenceEquals(cards, group.Cards) ? group : group with { Cards = cards };
        }

        IReadOnlyList<HomeSection>? sections = source.Sections;
        if (source.Sections is { } sourceSections)
        {
            var mapped = new HomeSection[sourceSections.Count];
            for (int i = 0; i < mapped.Length; i++)
            {
                var section = sourceSections[i];
                var cards = OverlayCards(section.Cards, exact, ref changed);
                mapped[i] = ReferenceEquals(cards, section.Cards) ? section : section with { Cards = cards };
            }
            sections = mapped;
        }

        return changed ? source with { Groups = groups, Sections = sections } : source;
    }

    static IReadOnlyList<HomeCard> OverlayCards(IReadOnlyList<HomeCard> source,
        IReadOnlyDictionary<string, HomePlaylistHeader> exact, ref bool changed)
    {
        HomeCard[]? mapped = null;
        for (int i = 0; i < source.Count; i++)
        {
            var card = source[i];
            if (card.Meta?.NeedsHydration != true || !exact.TryGetValue(card.Uri, out var header)) continue;
            mapped ??= Copy(source);
            mapped[i] = OverlayCard(card, header);
            changed = true;
        }
        return mapped ?? source;
    }

    static HomeCard[] Copy(IReadOnlyList<HomeCard> source)
    {
        var copy = new HomeCard[source.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
        return copy;
    }

    static HomeCard OverlayCard(HomeCard card, HomePlaylistHeader header)
    {
        var meta = card.Meta!;
        return card with
        {
            Title = header.Title,
            Subtitle = header.Subtitle ?? card.Subtitle,
            Image = header.Cover ?? card.Image,
            Meta = meta with
            {
                TrackCount = header.TrackCount > 0 ? header.TrackCount : meta.TrackCount,
                OwnerName = string.IsNullOrWhiteSpace(header.OwnerName) ? meta.OwnerName : header.OwnerName,
                NeedsHydration = false,
            },
        };
    }
}
