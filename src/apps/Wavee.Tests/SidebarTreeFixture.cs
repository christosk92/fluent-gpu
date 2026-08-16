using System.Collections.Generic;
using Wavee;

namespace Wavee.Tests;

/// <summary>
/// The ONE rootlist-tree fixture the non-mouse organisation suites share — the depth-first FLATTENED shape
/// <c>SidebarProjectionInput.PlaylistTree</c> publishes and every rule in <c>RootlistTreeNav</c> /
/// <c>RootlistTreeMoves</c> / <c>RootlistUndoAnchors</c> decides against.
///
/// <code>
///   a                       depth 0
///   [Chill]      folder g   depth 0
///       b                   depth 1
///       c                   depth 1
///       [Deep]   folder k   depth 1
///           f               depth 2
///   d                       depth 0
///   [Trailing]   folder h   depth 0
///       e                   depth 1
/// </code>
///
/// <para>It is deliberately awkward in the two places the landed defects lived: a folder NESTED inside a folder (so
/// "my siblings" cannot be "the rows at my depth"), and a TRAILING folder at the end of the top level (so "after
/// everything" must land after its end marker rather than inside it).</para>
/// </summary>
static class SidebarTreeFixture
{
    public const string PlaylistUriPrefix = "spotify:playlist:";

    /// <summary>A playlist row. The id is the projection's own (<c>pl:</c> + uri), which is what every verb addresses.</summary>
    public static SidebarLibraryEntry Playlist(string slug, int depth, string parentId = "", string parentName = "")
        => new(Id: SidebarPinId.PlaylistPrefix + PlaylistUriPrefix + slug,
               Kind: SidebarEntryKind.Playlist,
               Uri: PlaylistUriPrefix + slug,
               Name: slug, Creator: "", Cover: null, MosaicTiles: null,
               ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0,
               Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { ParentFolderId = parentId, ParentFolderName = parentName };

    /// <summary>A folder row. <c>FolderId</c> is its OWN group id; <c>ParentFolderId</c> is the folder containing it.</summary>
    public static SidebarLibraryEntry Folder(string groupId, string name, int depth,
                                             string parentId = "", string parentName = "")
        => new(Id: SidebarPinId.FolderPrefix + groupId,
               Kind: SidebarEntryKind.Folder,
               Uri: "", Name: name, Creator: "", Cover: null, MosaicTiles: null,
               ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0,
               Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = groupId, FolderName = name, ParentFolderId = parentId, ParentFolderName = parentName };

    public static IReadOnlyList<SidebarLibraryEntry> Tree() =>
    [
        Playlist("a", 0),
        Folder("g", "Chill", 0),
        Playlist("b", 1, "g", "Chill"),
        Playlist("c", 1, "g", "Chill"),
        Folder("k", "Deep", 1, "g", "Chill"),
        Playlist("f", 2, "k", "Deep"),
        Playlist("d", 0),
        Folder("h", "Trailing", 0),
        Playlist("e", 1, "h", "Trailing"),
    ];

    /// <summary>The entry id of a playlist row, as the verbs address it.</summary>
    public static string Pl(string slug) => SidebarPinId.PlaylistPrefix + PlaylistUriPrefix + slug;

    /// <summary>The entry id of a folder row.</summary>
    public static string Fo(string groupId) => SidebarPinId.FolderPrefix + groupId;
}
