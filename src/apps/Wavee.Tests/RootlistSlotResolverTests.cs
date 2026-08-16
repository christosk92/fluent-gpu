using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE SIDEBAR DROP RESOLVER — the pure geometry behind every playlist/folder drag in the sidebar tree.
///
/// <para>It replaced <c>SidebarPane.RootlistPlacementFor</c>, which had <b>zero</b> tests (D18) and three defects that a
/// test would have caught on sight: the zone geometry changed size with the payload (11 vs 22 DIP for the same row), the
/// pointer's X was never read at all so depth could be neither shown nor chosen, and a degenerate viewport silently
/// guessed a placement instead of refusing. Every one of those is pinned below.</para>
/// </summary>
public class RootlistSlotResolverTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The three canonical row heights the density ladder produces (32 compact / 44 cozy+subtitle / 48
    /// comfortable+subtitle). The edge band must behave identically at all three.</summary>
    public static TheoryData<float> Heights => new() { 32f, 44f, 48f };

    static SidebarRowFacts Leaf(int depth = 0, int nextDepth = -1, bool centerAccepts = false) => new(
        IsFolder: false, FolderExpanded: false, FolderHasChildren: false,
        Depth: depth, NextVisibleDepth: nextDepth < 0 ? depth : nextDepth,
        CenterAccepts: centerAccepts,
        SourceIsSelf: false, SourceIsAncestorOfRow: false, SortedNonCustom: false, RootlistLoaded: true);

    static SidebarRowFacts FolderRow(bool expanded, bool hasChildren, int depth = 0, int nextDepth = -1) => new(
        IsFolder: true, FolderExpanded: expanded, FolderHasChildren: hasChildren,
        Depth: depth, NextVisibleDepth: nextDepth < 0 ? depth : nextDepth,
        CenterAccepts: true,
        SourceIsSelf: false, SourceIsAncestorOfRow: false, SortedNonCustom: false, RootlistLoaded: true);

    /// <summary>Resolve with the pointer parked over the row's LABEL (x far past the indent ladder) — the default
    /// position, and therefore the one that must mean "stay at this row's depth".</summary>
    static SidebarDropSlot At(float t, in SidebarRowFacts f, float h = 44f, float x = 200f)
        => RootlistSlotResolver.Resolve(3, t, x, h, in f, SidebarDropSlot.None);

    // ── the edge band ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Heights))]
    public void EdgeBand_IsThirtyPercentClampedBetweenTenAndSixteen(float h)
    {
        float edge = RootlistSlotResolver.EdgeFor(h);
        Assert.Equal(System.Math.Clamp(h * 0.30f, 10f, 16f), edge, 3);
        // It must always leave a centre: two bands can never consume the whole row.
        Assert.True(edge * 2f < h);
    }

    [Fact]
    public void EdgeBand_DependsOnTheRowAlone_NeverOnThePayload()
    {
        // D6: the OLD geometry used 11-DIP bands for one payload and 22-DIP bands for another on the SAME row, so an
        // identical pointer position meant different things depending on what you happened to be dragging. The band is
        // now a function of the row height and nothing else — there is no payload parameter to pass.
        Assert.Equal(13.2f, RootlistSlotResolver.EdgeFor(44f), 3);
        // A pathologically short row still leaves a centre: never more than half of it.
        Assert.Equal(2f, RootlistSlotResolver.EdgeFor(4f), 3);
        Assert.Equal(RootlistSlotResolver.MinEdge, RootlistSlotResolver.EdgeFor(0f), 3);
    }

    // ── zones ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Heights))]
    public void PlainRow_IsTwoZones_WithNoDeadCentre(float h)
    {
        var f = Leaf();
        Assert.Equal(SidebarDropKind.Before, At(0.49f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.After, At(0.51f, in f, h).Kind);
        // A row that cannot take a deposit must not reserve half its height for one (D4's other half).
        Assert.NotEqual(SidebarDropKind.Into, At(0.5f, in f, h).Kind);
    }

    [Theory]
    [MemberData(nameof(Heights))]
    public void EditablePlaylistWithTrackPayload_IsThreeZones_CentreDeposits(float h)
    {
        var f = Leaf(centerAccepts: true);
        float edge = RootlistSlotResolver.EdgeFor(h) / h;
        Assert.Equal(SidebarDropKind.Before, At(edge * 0.5f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.Into, At(0.5f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.After, At(1f - edge * 0.5f, in f, h).Kind);
    }

    [Theory]
    [MemberData(nameof(Heights))]
    public void CollapsedFolder_TopIsBefore_CentreIsInto_BottomIsAfter(float h)
    {
        var f = FolderRow(expanded: false, hasChildren: true);
        Assert.Equal(SidebarDropKind.Before, At(0.01f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.Into, At(0.5f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.After, At(0.99f, in f, h).Kind);
    }

    [Theory]
    [MemberData(nameof(Heights))]
    public void ExpandedFolderWithChildren_BottomBand_IsTheFirstChildSlot(float h)
    {
        // The precise "make it the folder's first item" gesture — the line indents one step and the drop lands ahead of
        // the current first child. Without it, "into a folder" always meant "append last" and the header was the only
        // anchor there was (D7).
        var f = FolderRow(expanded: true, hasChildren: true, depth: 1, nextDepth: 2);
        var slot = At(0.99f, in f, h);
        Assert.Equal(SidebarDropKind.Before, slot.Kind);
        Assert.Equal(2, slot.Depth);
    }

    [Fact]
    public void ExpandedFolderWithNoChildren_BottomBand_IsAfterTheFolder()
    {
        var f = FolderRow(expanded: true, hasChildren: false, depth: 1, nextDepth: 0);
        var slot = At(0.99f, in f, x: 200f);
        Assert.Equal(SidebarDropKind.After, slot.Kind);
        Assert.Equal(1, slot.Depth);   // the pointer is over the label ⇒ stay at this row's depth
    }

    [Fact]
    public void DroppingOnAFolder_AlwaysMeansIntoIt()
    {
        // Explorer / Finder / VS Code / Spotify all agree; this is the one convention the old build did honour, and it
        // must survive the rewrite.
        foreach (var expanded in new[] { true, false })
        foreach (var children in new[] { true, false })
            Assert.Equal(SidebarDropKind.Into, At(0.5f, FolderRow(expanded, children)).Kind);
    }

    // ── depth ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DepthRange_IsAmbiguousOnlyAfterAFoldersLastVisibleChild()
    {
        // A fixture tree, flattened exactly as the plan holds it:
        //   f1                (folder, depth 0, expanded)
        //     a               (leaf,   depth 1)
        //     f2              (folder, depth 1, expanded)
        //       b             (leaf,   depth 2)   ← LAST visible child of f2 AND of f1: ambiguous, 0..2
        //   f3                (folder, depth 0, COLLAPSED — its subtree is not visible)
        //   f4                (folder, depth 0, EMPTY)
        //   c                 (leaf,   depth 0)                     ← last row: ambiguous only by being last
        var depths = new[] { 0, 1, 1, 2, 0, 0, 0 };
        var folders = new[] { true, false, true, false, true, true, false };
        for (int i = 0; i < depths.Length; i++)
        {
            int next = i + 1 < depths.Length ? depths[i + 1] : 0;
            var f = folders[i]
                ? FolderRow(expanded: true, hasChildren: next > depths[i], depth: depths[i], nextDepth: next)
                : Leaf(depths[i], next);
            var (min, max) = RootlistSlotResolver.DepthRange(in f);
            Assert.Equal(depths[i], max);
            Assert.Equal(System.Math.Min(next, depths[i]), min);
            // Ambiguous ⇔ the next visible row is SHALLOWER than this one — i.e. this row closes one or more folders.
            Assert.Equal(next < depths[i], min < max);
        }
    }

    [Fact]
    public void DepthRange_ClampsAFolderHeaderToItsOwnDepth()
    {
        // The next visible row is the folder's own CHILD (deeper). There is no slot below a header that lands outside
        // the header, so Min may never exceed Max.
        var f = FolderRow(expanded: true, hasChildren: true, depth: 0, nextDepth: 1);
        Assert.Equal((0, 0), RootlistSlotResolver.DepthRange(in f));
    }

    [Fact]
    public void DepthPick_DefaultsToTheRowsOwnDepth()
    {
        // The pointer sits over the LABEL for almost every gesture — so the default must be "stay where you are", and
        // outdenting must be something the user does on purpose by travelling left.
        var f = Leaf(depth: 2, nextDepth: 0);
        Assert.Equal(2, At(0.9f, in f, x: 200f).Depth);
    }

    [Theory]
    [InlineData(6f, 0)]      // hard left, at the depth-0 indent
    [InlineData(18f, 1)]     // one indent step in
    [InlineData(30f, 2)]     // two steps: the row's own depth
    [InlineData(999f, 2)]    // past the ladder: clamped to Max
    [InlineData(-50f, 0)]    // before the row: clamped to Min
    public void DepthPick_ReadsTheIndentLadderFromPointerX(float x, int expected)
    {
        var f = Leaf(depth: 2, nextDepth: 0);
        var slot = At(0.9f, in f, x: x);
        Assert.Equal(SidebarDropKind.After, slot.Kind);
        Assert.Equal(expected, slot.Depth);
    }

    [Fact]
    public void DepthPick_HoldsThePreviousDepthInsideTheHysteresisBand()
    {
        var f = Leaf(depth: 2, nextDepth: 0);
        var previous = new SidebarDropSlot(3, SidebarDropKind.After, 1, SidebarDropRefusal.None);
        // The 1→2 boundary sits at RowInsetLeft + 1.5·IndentStep = 24. Inside 4 DIP of it the previous depth holds…
        Assert.Equal(1, RootlistSlotResolver.Resolve(3, 0.9f, 25f, 44f, in f, in previous).Depth);
        // …and past it the pick commits.
        Assert.Equal(2, RootlistSlotResolver.Resolve(3, 0.9f, 30f, 44f, in f, in previous).Depth);
        // With no previous slot there is nothing to hold: the raw pick wins.
        Assert.Equal(2, RootlistSlotResolver.Resolve(3, 0.9f, 25f, 44f, in f, SidebarDropSlot.None).Depth);
    }

    [Fact]
    public void DepthPick_IsInertWhenTheSlotIsUnambiguous()
    {
        var f = Leaf(depth: 1, nextDepth: 1);
        foreach (float x in new[] { -10f, 0f, 6f, 18f, 300f })
            Assert.Equal(1, At(0.9f, in f, x: x).Depth);
    }

    [Fact]
    public void DepthPick_IsNotReadForBeforeOrInto()
    {
        var leaf = Leaf(depth: 2, nextDepth: 0);
        Assert.Equal(2, At(0.1f, in leaf, x: 0f).Depth);                 // Before stays at the row's depth
        var folder = FolderRow(expanded: false, hasChildren: true, depth: 2, nextDepth: 0);
        Assert.Equal(2, At(0.5f, in folder, x: 0f).Depth);               // Into is the row itself
    }

    // ── the tree's end marker ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TreeEndRow_IsOneWholeRowSlotAtDepthZero()
    {
        var f = Leaf() with { IsListEnd = true };
        foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            var slot = RootlistSlotResolver.Resolve(9, t, 200f, SidebarRowGeometry.TreeEndHeight, in f,
                                                    SidebarDropSlot.None);
            Assert.Equal(SidebarDropKind.EndOfList, slot.Kind);
            Assert.Equal(0, slot.Depth);
        }
    }

    // ── degenerate geometry (D17) ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1, 0.5f, 44f)]      // no plan row
    [InlineData(3, 0.5f, 0f)]        // no measured extent
    [InlineData(3, float.NaN, 44f)]  // no viewport ⇒ no resolvable t
    public void DegenerateGeometry_RefusesWithAReason_NeverGuesses(int planIndex, float t, float h)
    {
        var slot = RootlistSlotResolver.Resolve(planIndex, t, 100f, h, Leaf(), SidebarDropSlot.None);
        Assert.Equal(SidebarDropKind.None, slot.Kind);
        Assert.Equal(SidebarDropRefusal.Unavailable, slot.Refusal);
    }

    // ── the undo anchor ──────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Pl(string slug, int depth = 0, string parent = "")
        => new("pl:spotify:playlist:" + slug, SidebarEntryKind.Playlist, "spotify:playlist:" + slug, slug, "",
               null, null, 0, 0, 1, 0, 0, depth, false, SidebarPlaylistFlavor.None) { ParentFolderId = parent };

    static SidebarLibraryEntry Fold(string id, int depth = 0, string parent = "")
        => new("folder:" + id, SidebarEntryKind.Folder, "", id, "", null, null, 0, 0, 1, 0, 0, depth, false,
               SidebarPlaylistFlavor.None) { FolderId = id, ParentFolderId = parent };

    static List<SidebarLibraryEntry> Tree() =>
    [
        Pl("a"),
        Fold("g"),
        Pl("b", 1, "g"),
        Pl("c", 1, "g"),
        Pl("d"),
    ];

    [Fact]
    public void UndoAnchor_IsThePreviousSiblingWhereverOneExists()
    {
        Assert.True(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:c", out var anchor, out var placement));
        Assert.Equal(new RootlistItemRef("spotify:playlist:b", false), anchor);
        Assert.Equal(RootlistDropPlacement.After, placement);

        // A folder's previous sibling is the folder itself, addressed by group id — never one of its children.
        Assert.True(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:d", out var afterFolder, out _));
        Assert.Equal(new RootlistItemRef("g", true), afterFolder);
    }

    [Fact]
    public void UndoAnchor_FallsToTheNextSiblingForTheFirstChild()
    {
        Assert.True(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:b", out var anchor, out var placement));
        Assert.Equal(new RootlistItemRef("spotify:playlist:c", false), anchor);
        Assert.Equal(RootlistDropPlacement.Before, placement);

        // Top level, first: the next TOP-LEVEL sibling, which is the folder (its whole subtree is skipped).
        Assert.True(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:a", out var first, out var before));
        Assert.Equal(new RootlistItemRef("g", true), first);
        Assert.Equal(RootlistDropPlacement.Before, before);
    }

    [Fact]
    public void UndoAnchor_IsTheParentFolderForAnOnlyChild_AndAbsentWhenThereIsNothingToAnchorAgainst()
    {
        List<SidebarLibraryEntry> only = [Fold("g"), Pl("b", 1, "g")];
        Assert.True(RootlistUndoAnchors.TryResolve(only, "pl:spotify:playlist:b", out var anchor, out var placement));
        Assert.Equal(new RootlistItemRef("g", true), anchor);
        Assert.Equal(RootlistDropPlacement.Inside, placement);

        // The only top-level item: there is no move to undo, and inventing one would land it somewhere else.
        List<SidebarLibraryEntry> lone = [Pl("a")];
        Assert.False(RootlistUndoAnchors.TryResolve(lone, "pl:spotify:playlist:a", out _, out _));
        Assert.False(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:missing", out _, out _));
        Assert.False(RootlistUndoAnchors.TryResolve(null, "pl:spotify:playlist:a", out _, out _));
    }
}
