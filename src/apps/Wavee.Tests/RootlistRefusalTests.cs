using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE REFUSAL TABLE. Every one of these used to be a silent <c>false</c>: <c>RootlistOps.TryBuildMove</c> rejected the
/// cycle and the no-op three layers below the pointer, and the drag showed "Move into B" and then simply did nothing
/// (D8/D2). They are decided here now — WHERE THE CUE IS DRAWN — so each one can carry its own sentence.
///
/// <para><b>The one structural invariant:</b> a refused slot always reports <see cref="SidebarDropKind.None"/>. That is
/// what makes the cue's rule total: a refusal draws neither a line nor a plate, so there is no state in which the
/// surface promises a drop it will not perform.</para>
/// </summary>
public class RootlistRefusalTests
{
    static SidebarRowFacts Row(bool folder = false, bool self = false, bool ancestor = false,
                               bool sorted = false, bool loaded = true) => new(
        IsFolder: folder, FolderExpanded: false, FolderHasChildren: false,
        Depth: 0, NextVisibleDepth: 0, CenterAccepts: folder,
        SourceIsSelf: self, SourceIsAncestorOfRow: ancestor, SortedNonCustom: sorted, RootlistLoaded: loaded);

    static SidebarDropSlot At(float t, in SidebarRowFacts f)
        => RootlistSlotResolver.Resolve(2, t, 200f, 44f, in f, SidebarDropSlot.None);

    [Fact]
    public void Self_RefusesTheRowTheDragCameFrom()
    {
        var slot = At(0.1f, Row(self: true));
        Assert.Equal(SidebarDropRefusal.Self, slot.Refusal);
        Assert.Equal(SidebarDropKind.None, slot.Kind);
    }

    [Fact]
    public void IntoItself_IsItsOwnSentence_ForAFoldersCentre()
    {
        // "Can't move a folder into itself" names what the user tried; the generic "can't move here" would leave them
        // guessing which rule they hit.
        Assert.Equal(SidebarDropRefusal.IntoItself, At(0.5f, Row(folder: true, self: true)).Refusal);
        // The same folder's EDGES are the ordinary self refusal — the user is aiming at an ordering, not at the folder.
        Assert.Equal(SidebarDropRefusal.Self, At(0.02f, Row(folder: true, self: true)).Refusal);
    }

    [Fact]
    public void IntoDescendant_RefusesARowInsideTheDraggedFolder()
    {
        Assert.Equal(SidebarDropRefusal.IntoDescendant, At(0.5f, Row(folder: true, ancestor: true)).Refusal);
        Assert.Equal(SidebarDropRefusal.IntoDescendant, At(0.1f, Row(ancestor: true)).Refusal);
    }

    [Fact]
    public void SortedList_RefusesOrderingsOnly_IntoStaysLegal()
    {
        // D10: a non-custom sort cannot SHOW a positional insert, so an ordering is refused with the existing fix
        // ("clear sorting to reorder") — but a deposit into a folder or a playlist needs no position at all.
        Assert.Equal(SidebarDropRefusal.SortedList, At(0.1f, Row(sorted: true)).Refusal);
        Assert.Equal(SidebarDropRefusal.SortedList, At(0.9f, Row(sorted: true)).Refusal);
        var into = At(0.5f, Row(folder: true, sorted: true));
        Assert.Equal(SidebarDropRefusal.None, into.Refusal);
        Assert.Equal(SidebarDropKind.Into, into.Kind);

        var end = RootlistSlotResolver.Resolve(2, 0.5f, 200f, 24f,
            Row(sorted: true) with { IsListEnd = true }, SidebarDropSlot.None);
        Assert.Equal(SidebarDropRefusal.SortedList, end.Refusal);
    }

    [Fact]
    public void NotLoaded_RefusesEverything_BeforeAnyOtherRule()
    {
        // "Not known to be loaded" must never present as "is": a filing written against an empty tree lands at an index
        // that means nothing. It is checked FIRST, so it is the reason the user sees even when another would also apply.
        Assert.Equal(SidebarDropRefusal.NotLoaded, At(0.5f, Row(folder: true, self: true, loaded: false)).Refusal);
        Assert.Equal(SidebarDropRefusal.NotLoaded, At(0.1f, Row(loaded: false)).Refusal);
    }

    [Fact]
    public void Unavailable_IsTheDegenerateGeometryRefusal()
    {
        Assert.Equal(SidebarDropRefusal.Unavailable,
            RootlistSlotResolver.Resolve(-1, 0.5f, 200f, 44f, Row(), SidebarDropSlot.None).Refusal);
    }

    // ── NoOp / cycle, decided against real sibling order ─────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Pl(string slug, int depth = 0, string parent = "")
        => new("pl:spotify:playlist:" + slug, SidebarEntryKind.Playlist, "spotify:playlist:" + slug, slug, "",
               null, null, 0, 0, 1, 0, 0, depth, false, SidebarPlaylistFlavor.None) { ParentFolderId = parent };

    static SidebarLibraryEntry Fold(string id, int depth = 0, string parent = "")
        => new("folder:" + id, SidebarEntryKind.Folder, "", id, "", null, null, 0, 0, 1, 0, 0, depth, false,
               SidebarPlaylistFlavor.None) { FolderId = id, ParentFolderId = parent };

    //  a · [g: b, c] · d
    static List<SidebarLibraryEntry> Tree() => [Pl("a"), Fold("g"), Pl("b", 1, "g"), Pl("c", 1, "g"), Pl("d")];

    const string A = "pl:spotify:playlist:a";
    const string B = "pl:spotify:playlist:b";
    const string C = "pl:spotify:playlist:c";
    const string D = "pl:spotify:playlist:d";
    const string G = "folder:g";

    [Fact]
    public void NoOp_RefusesBothEdgesOfTheSpanTheItemAlreadyOccupies()
    {
        var tree = Tree();
        // "Before the row right after me" and "after the row right before me" are the same place I am already in — the
        // two gestures a reorder produces most often, and the two that used to fail silently.
        Assert.Equal(SidebarDropRefusal.NoOp, RootlistTreeMoves.Check(tree, B, C, RootlistDropPlacement.Before));
        Assert.Equal(SidebarDropRefusal.NoOp, RootlistTreeMoves.Check(tree, C, B, RootlistDropPlacement.After));
        // Onto itself, either way round.
        Assert.Equal(SidebarDropRefusal.NoOp, RootlistTreeMoves.Check(tree, B, B, RootlistDropPlacement.Before));
        Assert.Equal(SidebarDropRefusal.NoOp, RootlistTreeMoves.Check(tree, B, B, RootlistDropPlacement.After));
        // The folder's LAST child, appended back into that same folder.
        Assert.Equal(SidebarDropRefusal.NoOp, RootlistTreeMoves.Check(tree, C, G, RootlistDropPlacement.Inside));
    }

    [Fact]
    public void RealMoves_AreNotRefused()
    {
        var tree = Tree();
        Assert.Equal(SidebarDropRefusal.None, RootlistTreeMoves.Check(tree, A, C, RootlistDropPlacement.After));
        Assert.Equal(SidebarDropRefusal.None, RootlistTreeMoves.Check(tree, B, G, RootlistDropPlacement.After));
        Assert.Equal(SidebarDropRefusal.None, RootlistTreeMoves.Check(tree, A, G, RootlistDropPlacement.Inside));
        Assert.Equal(SidebarDropRefusal.None, RootlistTreeMoves.Check(tree, D, A, RootlistDropPlacement.Before));
    }

    [Fact]
    public void Cycle_RefusesAFolderFiledIntoItsOwnSubtree()
    {
        var tree = Tree();
        Assert.Equal(SidebarDropRefusal.IntoDescendant, RootlistTreeMoves.Check(tree, G, B, RootlistDropPlacement.Before));
        Assert.Equal(SidebarDropRefusal.IntoDescendant, RootlistTreeMoves.Check(tree, G, B, RootlistDropPlacement.After));
        // Into the dragged folder ITSELF has its own sentence — "can't move a folder into itself".
        Assert.Equal(SidebarDropRefusal.IntoItself, RootlistTreeMoves.Check(tree, G, G, RootlistDropPlacement.Inside));
    }

    [Fact]
    public void AnUnknownRowIsNotARefusal()
    {
        // The tree may simply not be showing it (a collapsed folder, a search filter). Inventing a refusal there would
        // accuse a perfectly legal drop.
        Assert.Equal(SidebarDropRefusal.None, RootlistTreeMoves.Check(Tree(), "pl:missing", A, RootlistDropPlacement.After));
        Assert.Equal(SidebarDropRefusal.None, RootlistTreeMoves.Check(Tree(), A, "pl:missing", RootlistDropPlacement.After));
        Assert.Equal(SidebarDropRefusal.None, RootlistTreeMoves.Check(null, A, D, RootlistDropPlacement.After));
    }

    [Fact]
    public void TreeRanges_CoverAFoldersWholeSubtree()
    {
        var tree = Tree();
        Assert.True(RootlistTreeMoves.TryRange(tree, G, out int start, out int end));
        Assert.Equal((1, 4), (start, end));                    // the folder plus both children
        Assert.True(RootlistTreeMoves.TryRange(tree, B, out int leafStart, out int leafEnd));
        Assert.Equal((2, 3), (leafStart, leafEnd));
    }
}
