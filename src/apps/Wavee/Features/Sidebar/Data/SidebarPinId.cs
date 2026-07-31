using System;

namespace Wavee;

// The STABLE pin identity scheme (F.5.4). Engine-free (System only), source-included by src/apps/Wavee.Tests.
//
// The pin id IS the nav route key for every navigable kind. That single choice buys three things at once:
//   * a pinned row renders its label/glyph through ShellNav.Dest(id, name) with no extra plumbing (which is why the
//     "show:" arm in ShellNav is a prerequisite — F.6),
//   * the recency join against HistoryStore is an identity lookup on Route.Name (F.7.6), and
//   * a pin survives a library refresh, because it never depends on a list index.

/// <summary>What a pin points at. Values are PERSISTED (<c>sidebar-layout.json</c> → <c>SidebarPinDto.Kind</c>) —
/// append only, never reorder or reuse.</summary>
public enum SidebarPinKind : byte { Route = 0, Playlist = 1, Album = 2, Artist = 3, Show = 4, Folder = 5 }

/// <summary>One pinned sidebar item. <see cref="Id"/> is the STABLE identity (F.5.4) and is also the nav route key for
/// every kind except <see cref="SidebarPinKind.Folder"/>. <see cref="Name"/>/<see cref="Uri"/> are a display CACHE so a
/// pinned row paints instantly offline before the library resolves; they are refreshed by the projection and are never
/// the source of truth.</summary>
public sealed record SidebarPin(string Id, SidebarPinKind Kind, string Uri, string Name, long AddedAtMs)
{
    /// <summary>§3.0 consumer alias — the pin store keys on <see cref="Id"/>.</summary>
    public string Key => Id;

    /// <summary>The nav route this pin opens; "" for a folder (it expands in place, it never navigates).</summary>
    public string RouteKey => SidebarPinId.RouteOf(Id) ?? "";
}

public static class SidebarPinId
{
    // ── prefixes (one place, so a parse and a build can never disagree) ──
    public const string PlaylistPrefix = "pl:";
    public const string AlbumPrefix = "album:";
    public const string ArtistPrefix = "artist:";
    public const string ShowPrefix = "show:";
    public const string FolderPrefix = "folder:";

    /// <summary>The pre-seeded app routes shown by pickers. Dynamic route families (browse, concerts, extension pages,
    /// future registered routes) are also pinnable when reached; they are not enumerated here because their instances
    /// only exist at runtime.</summary>
    public static readonly string[] PinnableRoutes =
        ["home", "search", "albums", "artists", "liked", "podcasts", "local", "history"];

    /// <summary>Liked Songs is a ROUTE pin, not a playlist pin — matching <c>ActionRules.RouteFor</c>'s existing special
    /// case, so a pin made from the detail page and a pin made from a sidebar row are the SAME pin.</summary>
    public const string LikedSongsUri = "spotify:collection:tracks";

    /// <summary>uri → pin id. Null = not pinnable. Tracks and episodes are NEVER pinnable (locked decision 4) and that is
    /// enforced HERE, in one function, rather than per menu.</summary>
    public static string? FromUri(string? uri) => uri switch
    {
        null or "" => null,
        LikedSongsUri => "liked",                                                       // a ROUTE pin
        var u when u.StartsWith("spotify:playlist:", StringComparison.Ordinal)
                || u.StartsWith("wavee:playlist:", StringComparison.Ordinal) => PlaylistPrefix + u,
        var u when u.StartsWith("spotify:album:", StringComparison.Ordinal) => AlbumPrefix + u,
        var u when u.StartsWith("spotify:artist:", StringComparison.Ordinal) => ArtistPrefix + u,
        var u when u.StartsWith("spotify:show:", StringComparison.Ordinal) => ShowPrefix + u,
        _ => null,                                                                      // tracks, episodes, everything else
    };

    /// <summary>Route key → pin id. Every real application route is stable enough to pin; only internal tooling/editor
    /// surfaces are refused. Entity routes retain their existing prefixed identities, so pins made from page chrome,
    /// tabs, cards and sidebar rows converge on one record.</summary>
    public static string? FromRoute(string? routeKey)
    {
        if (string.IsNullOrWhiteSpace(routeKey)) return null;
        if (string.Equals(routeKey, "settings", StringComparison.Ordinal)
            || string.Equals(routeKey, "api-console", StringComparison.Ordinal)
            || string.Equals(routeKey, "sidebar-customize", StringComparison.Ordinal)) return null;
        return routeKey;
    }

    public static bool IsPinnableRoute(string? routeKey) => FromRoute(routeKey) is not null;

    /// <summary>A rootlist group id → its pin id. Folders are pinnable (locked decision 4) even though they never navigate.</summary>
    public static string ForFolder(string folderId) => FolderPrefix + folderId;

    /// <summary>Prefix dispatch; no known prefix ⇒ <see cref="SidebarPinKind.Route"/> (the bare-route form).</summary>
    public static SidebarPinKind KindOf(string? pinId) =>
        pinId is null ? SidebarPinKind.Route
        : pinId.StartsWith(PlaylistPrefix, StringComparison.Ordinal) ? SidebarPinKind.Playlist
        : pinId.StartsWith(AlbumPrefix, StringComparison.Ordinal) ? SidebarPinKind.Album
        : pinId.StartsWith(ArtistPrefix, StringComparison.Ordinal) ? SidebarPinKind.Artist
        : pinId.StartsWith(ShowPrefix, StringComparison.Ordinal) ? SidebarPinKind.Show
        : pinId.StartsWith(FolderPrefix, StringComparison.Ordinal) ? SidebarPinKind.Folder
        : SidebarPinKind.Route;

    /// <summary>The nav route a pin opens. Null for <see cref="SidebarPinKind.Folder"/> (a folder expands in place; it
    /// never navigates) — every other kind's id IS its route key.</summary>
    public static string? RouteOf(string pinId) => KindOf(pinId) == SidebarPinKind.Folder ? null : pinId;

    /// <summary>The entity uri behind a pin id ("" for a route or folder pin). The inverse of <see cref="FromUri"/> for
    /// the prefixed kinds — used when a pinned row needs a play/share target and the display cache is stale.</summary>
    public static string UriOf(string pinId) => KindOf(pinId) switch
    {
        SidebarPinKind.Playlist => pinId.Substring(PlaylistPrefix.Length),
        SidebarPinKind.Album => pinId.Substring(AlbumPrefix.Length),
        SidebarPinKind.Artist => pinId.Substring(ArtistPrefix.Length),
        SidebarPinKind.Show => pinId.Substring(ShowPrefix.Length),
        SidebarPinKind.Route when string.Equals(pinId, "liked", StringComparison.Ordinal) => LikedSongsUri,
        _ => "",
    };

    /// <summary>The rootlist group id behind a folder pin ("" when the pin is not a folder).</summary>
    public static string FolderIdOf(string pinId) =>
        KindOf(pinId) == SidebarPinKind.Folder ? pinId.Substring(FolderPrefix.Length) : "";

    /// <summary>An action target → its pin id (null = not pinnable). Deriving from the target's URI rather than its
    /// <c>TargetKind</c> is deliberate: it keeps <c>ActionTarget</c>/<c>TargetKind</c>/<c>ActionRules</c> untouched, and a
    /// track/queue/now-playing target carries a track uri, which <see cref="FromUri"/> already refuses.</summary>
    public static string? FromTarget(in ActionTarget t) => FromUri(t.Uri);

    /// <summary>The pin id for a projected entry — the entry Id already IS the pin id (F.7.1), so this is the identity
    /// with the not-pinnable kinds screened out: internal routes and TRACK rows (queue / now playing / artist top
    /// tracks) are never pinnable.</summary>
    public static string? FromEntry(in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.AppRoute => FromRoute(e.Id),
        SidebarEntryKind.Track => null,
        _ => e.Id,
    };

    /// <summary>The <see cref="SidebarPinKind"/> a projected entry pins as (the two enums have different orderings on
    /// purpose — <see cref="SidebarPinKind"/> is persisted, <see cref="SidebarEntryKind"/> is not).</summary>
    public static SidebarPinKind KindOfEntry(SidebarEntryKind kind) => kind switch
    {
        SidebarEntryKind.Playlist => SidebarPinKind.Playlist,
        SidebarEntryKind.Album => SidebarPinKind.Album,
        SidebarEntryKind.Artist => SidebarPinKind.Artist,
        SidebarEntryKind.Show => SidebarPinKind.Show,
        SidebarEntryKind.Folder => SidebarPinKind.Folder,
        _ => SidebarPinKind.Route,
    };
}
