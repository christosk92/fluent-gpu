using System;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── surface → trait bundle, and surface → attribution, as TWO pure tables (design §2.4) ──────────────────────────────
// Before this, "which extension kinds does this screen want?" was distributed across four services' own entry points
// (adornments, play counts, video detect, publishing), each with its own caller list — which is why the album page
// asked for kind 185 twice and the show page asked for nothing at all. Making it a table means one POST can carry a
// surface's whole bundle, and adding a surface is one line rather than four call sites.

/// <summary>THE surface → <see cref="TraitSet"/> table.</summary>
/// <param name="playsColumnOn">Whether the user's Plays column is switched on. A Func, not a bool, because the setting
/// flips at runtime and the policy is constructed once at go-live.</param>
public sealed class TraitPolicy(Func<bool> playsColumnOn)
{
    readonly Func<bool> _playsColumnOn = playsColumnOn;

    public TraitSet For(TraitSurface surface) => surface switch
    {
        // An album page paints the ©/℗ line (183, album-only) and the Plays star unconditionally — the star IS the
        // album surface's identity, so it does not wait for the column setting.
        TraitSurface.AlbumOpen => TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing,

        // A list of arbitrary playables: the row bundle always, counts only when the column is actually rendered
        // (asking for 185 across a 10k playlist nobody is showing counts for is the waste this replaces).
        TraitSurface.PlaylistOpen or TraitSurface.LikedSongs =>
            TraitSet.RowBundle | (_playsColumnOn() ? TraitSet.PlayCount : TraitSet.None),

        // Episodes have no play count (185 is a TRACK trait) — the row bundle's ask-once kinds are the whole story.
        TraitSurface.ShowOpen => TraitSet.RowBundle,

        // The artist chart renders counts as its ORDERING, so they are not optional here.
        TraitSurface.ArtistPopular => TraitSet.RowBundle | TraitSet.PlayCount,

        TraitSurface.Queue or TraitSurface.Search => TraitSet.RowBundle,

        // The recents viewport is the one surface the capture attributes 178/220 to; 179 is what tints its cards
        // before an image byte arrives.
        TraitSurface.Recents => TraitSet.IdentityTraits | TraitSet.VisualIdentity,

        // Now playing wants exactly one thing the row bundle would over-fetch for: does this playable have a video?
        TraitSurface.NowPlaying => TraitSet.Video,

        // The toggle path: the column just came on for rows that already have their bundle.
        TraitSurface.PlaysToggle => TraitSet.PlayCount,

        // Everything else asks for no traits. TrackExpansion/Credits/PreRelease/UserProfiles are DISPLAY-ONLY extension
        // reads (P2's IExtensionReader owns them — they decorate a drawer, not a row); Prefetch/Context/None are
        // identity-only waves whose whole point is to cost one catalogue POST and nothing more.
        _ => TraitSet.None,
    };
}

/// <summary>THE surface → <c>client-feature-id</c> table: the attribution header the desktop client stamps per surface.
/// Kept next to <see cref="TraitPolicy"/> because it answers the sibling half of "which screen is asking".</summary>
public static class TraitSurfaces
{
    /// <summary>Null means the header is omitted — which is the pre-existing behaviour for unattributed traffic, and
    /// stays that way rather than inventing an attribution the capture never showed.</summary>
    public static string? ClientFeatureId(this TraitSurface surface) => surface switch
    {
        // The scroll-driven recents viewport hydrator: the ONE caller the census attributes 178/179/220 to.
        TraitSurface.Recents => "mdata_esperanto",
        // Display-only reads the desktop client issues without this attribution, and the unattributed default.
        TraitSurface.PreRelease or TraitSurface.UserProfiles or TraitSurface.None => null,
        _ => "track_metadata_loader",
    };
}
