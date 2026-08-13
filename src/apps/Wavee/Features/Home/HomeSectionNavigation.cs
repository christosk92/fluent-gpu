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
    /// never reach a paging endpoint: neither <c>homeSection</c> nor <c>browseSection</c> can resolve it, which the
    /// section page used to surface as a hard error page once the bounded preview store had evicted the seed.
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
/// the <c>homeSection</c> read lands. The bounded entry remains available for Back/remount: a synthetic section without
/// a server URI cannot be reconstructed once its preview is consumed.</summary>
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
                // puts the whole blurb where the detail page expects "Spotify". The daylist window rides along so the
                // detail countdown paints with the header even if playlist4 omits the format attributes.
                DetailNav.OpenPlaylist(preview, go,
                    new PlaylistSummary(card.Uri, card.Title, card.Meta?.OwnerName ?? "", 0, card.Image,
                        card.MosaicTiles,
                        DaylistExpiresAtMs: card.Meta?.ExpiresAtMs ?? 0,
                        DaylistCreatedAtMs: card.Meta?.CreatedAtMs ?? 0,
                        Accent: card.Meta?.Accent ?? 0));
                return;
        }
    }
}

// HomeSectionPaging — the cursor arithmetic — lives in its own engine-free file (HomeSectionPaging.cs) so
// Wavee.Tests can source-include and drive it without this file's Context/Hooks dependencies.
