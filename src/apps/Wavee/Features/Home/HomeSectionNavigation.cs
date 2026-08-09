using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using Wavee.Core;

namespace Wavee;

static class HomeSectionRoutes
{
    public const string Prefix = "home-section:";

    /// <summary>The scheme Home mints for a section the SERVER gave no URI for: <c>wavee:local:&lt;hash&gt;</c>. It is a
    /// purely LOCAL route identity — it addresses a <see cref="HomeSectionPreviewStore"/> entry and nothing else. It must
    /// never reach a browse endpoint: <c>browseSection</c> answers null for it, which the section page used to surface as
    /// a hard error page once the bounded preview store had evicted the seed.
    /// <para>OWNER: this const. <c>HomePage.OpenSection</c> still builds the same string as a literal — that literal is
    /// redundant and should migrate here, so the minting side and the recognising side share one definition.</para>
    /// </summary>
    public const string LocalPrefix = "wavee:local:";

    public static string Page(string sectionUri) => Prefix + sectionUri;
    public static bool Is(string route) => route.StartsWith(Prefix, StringComparison.Ordinal);
    public static string UriOf(string route) => Is(route) ? route[Prefix.Length..] : "";

    /// <summary>True for a client-minted section identity — there is no server resource behind it, so it is never a
    /// legal argument to a browse read.</summary>
    public static bool IsLocal(string? uri) =>
        uri is not null && uri.StartsWith(LocalPrefix, StringComparison.Ordinal);
}

/// <summary>Click-to-section handoff. Home already holds the returned first page, so a drill route can paint it before
/// attempting the inferred <c>browseSection</c> paging seam. The bounded entry remains available for Back/remount: a
/// synthetic section without a server URI cannot be reconstructed once its preview is consumed.</summary>
sealed class HomeSectionPreviewStore
{
    public static readonly Context<HomeSectionPreviewStore?> Slot = new(null);
    const int Capacity = 32;
    readonly Dictionary<string, HomeSection> _map = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();

    public void Set(string routeKey, HomeSection section)
    {
        if (!_map.ContainsKey(routeKey)) _order.Enqueue(routeKey);
        _map[routeKey] = section;
        while (_map.Count > Capacity && _order.TryDequeue(out var old)) _map.Remove(old);
    }

    public HomeSection? Get(string routeKey) => _map.TryGetValue(routeKey, out var section) ? section : null;
}

/// <summary>Where a home CARD goes when it is opened, and how its entity id is read out of its URI. One definition for
/// every surface that renders <see cref="HomeCard"/>s (the Home feed and the Home section page both did their own copy
/// of this switch, and drifted: one routed Liked, the other did not).
/// <para><paramref name="playTrack"/> rather than the whole <c>Services</c> graph: a Track/Episode card is the one kind
/// that PLAYS instead of navigating, and that is the only service this decision needs.</para></summary>
static class HomeCardNav
{
    /// <summary>The trailing id of a <c>scheme:kind:id</c> URI (the whole string when it carries no separator).</summary>
    public static string Id(string uri) { int i = uri.LastIndexOf(':'); return i >= 0 ? uri[(i + 1)..] : uri; }

    /// <summary>Open a card: play it (Track/Episode), or navigate to its destination — stashing the partial detail
    /// model the card already carries, so the detail page reconciles its header in place instead of flashing a
    /// full-page skeleton (see <see cref="DetailNav"/>). <paramref name="preview"/> may be null (no stash then).</summary>
    public static void Open(HomeCard card, NavPreviewStore? preview, Action<string, string?> go,
                            Action<string>? playTrack)
    {
        switch (card.Kind)
        {
            case HomeCardKind.Liked:
                go("liked", null);
                return;
            case HomeCardKind.Track:
            case HomeCardKind.Episode:
                // An episode, like a track, is a thing you PLAY rather than a destination: the feed carries a uri and
                // display metadata but no episode page of our own, and its show is one tap away from the player.
                playTrack?.Invoke(card.Uri);
                return;
            case HomeCardKind.Artist:
                go("artist:" + card.Uri, card.Title);
                return;
            case HomeCardKind.Album:
                DetailNav.OpenAlbum(preview, go,
                    new Album(Id(card.Uri), card.Uri, card.Title, card.Image, Array.Empty<ArtistRef>(), 0, 0));
                return;
            case HomeCardKind.Podcast:
            case HomeCardKind.Audiobook:
                // Both arrive under a spotify:show: uri, and the show route already renders either.
                go("show:" + card.Uri, card.Title);
                return;
            default:
                // OwnerName, not Subtitle: PlaylistSummary's third slot IS the owner, and handing it a description
                // puts the whole blurb where the detail page expects "Spotify".
                DetailNav.OpenPlaylist(preview, go,
                    new PlaylistSummary(card.Uri, card.Title, card.Meta?.OwnerName ?? "", 0, card.Image,
                        card.MosaicTiles));
                return;
        }
    }
}

/// <summary>The cursor arithmetic behind the Home section page's "Show all". Pure over <see cref="HomeSection"/> — no
/// services, no elements — because the whole defect class here is arithmetic.
/// <para>The load-bearing distinction is RAW vs DEDUPED. <see cref="HomeSection.Cards"/> is deduplicated (the ledger
/// contract in <c>HomeFeed.cs</c>: <c>RawItemCount == Cards.Count + UnsupportedCount + DuplicateCount</c>), while the
/// server's cursor counts everything it sent. Paging by <c>Cards.Count</c> therefore walks the cursor BACKWARDS by
/// exactly the number of items we dropped, re-fetching what we just discarded; a page that is entirely already-seen
/// URIs does not advance <c>Cards.Count</c> at all, so the offset — and the whole request — repeats forever while
/// <c>TotalCount &gt; Cards.Count</c> keeps the button armed. Every quantity below is the RAW one.</para></summary>
static class HomeSectionPaging
{
    /// <summary>The offset to request next: the raw number of items the endpoint has already handed us, duplicates and
    /// unsupported entries included. Floored at <c>Cards.Count</c> so a section whose source under-reported its raw
    /// count (or left it at zero) still asks for the page AFTER what it is showing rather than re-reading page one.
    /// </summary>
    public static int NextOffset(HomeSection section) => Math.Max(section.RawItemCount, section.Cards.Count);

    /// <summary>Whether the server still has items past our cursor. Compared against the RAW count, so it agrees with
    /// <see cref="NextOffset"/>: measuring the server's total against our deduped count claims there is more to fetch
    /// for as long as we have dropped anything, which is what armed the no-progress loop.</summary>
    public static bool HasMore(HomeSection section) => section.TotalCount > NextOffset(section);

    /// <summary>Fold a fetched page into the section: append the URIs we have not seen, and advance the raw cursor by
    /// the FULL page — duplicates included. That is the no-progress guard: a page that contributes zero new cards still
    /// moves the cursor, so the next click asks for the following page instead of re-issuing the same request. (A page
    /// with zero items cannot advance anything; the caller latches "exhausted" for that case, since the section carries
    /// no such flag and <see cref="HomeSection.TotalCount"/> is the server's number, not ours to rewrite.)</summary>
    public static HomeSection Append(HomeSection current, IReadOnlyList<HomeCard> pageCards, int pageTotal)
    {
        int raw = NextOffset(current);
        var seen = new HashSet<string>(current.Cards.Count + pageCards.Count, StringComparer.OrdinalIgnoreCase);
        var cards = new List<HomeCard>(current.Cards.Count + pageCards.Count);
        foreach (var card in current.Cards) { seen.Add(card.Uri); cards.Add(card); }
        int duplicates = 0;
        for (int i = 0; i < pageCards.Count; i++)
        {
            var card = pageCards[i];
            if (seen.Add(card.Uri)) cards.Add(card); else duplicates++;
        }
        return current with
        {
            Cards = cards,
            TotalCount = Math.Max(current.TotalCount, pageTotal),
            RawItemCount = raw + pageCards.Count,
            DuplicateCount = current.DuplicateCount + duplicates,
        };
    }
}
