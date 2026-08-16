using System;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE TREE ROW'S MENU EXTRAS (D12). <c>SidebarPaneSlot.NavExtras</c> used to build Move up / Move down only for
/// reorder bands and pins, so the one verb a rootlist row offered was "Move out of {parent}" — one level, one
/// direction. A right-click on a playlist could not reorder it at all, and neither could a keyboard.
///
/// <para>Two halves, pinned two ways. The DECISION (which verbs, at which position in the sibling run) is pure and is
/// driven directly. The WIRING — which labels, in which order, onto which commands, and only on tree rows — is pinned by
/// source scan, because <c>SidebarPaneSlot</c> is engine code (the <c>MenuGrammarTests</c> technique).</para>
/// </summary>
public class SidebarNavExtrasTests
{
    static SidebarTreeNavLayout Layout(string id)
    {
        var tree = SidebarTreeFixture.Tree();
        return SidebarTreeNavLayout.Decide(RootlistTreeNav.Siblings(tree, id),
                                           RootlistTreeNav.HasDestinations(tree, SidebarTreeFixture.Markers(), id));
    }

    // ── the decision ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMiddleTreeRow_OffersAllThreeVerbs()
    {
        // c sits between b and [Deep] inside Chill: both orderings are real, and there are folders to file it into.
        var mid = Layout(SidebarTreeFixture.Pl("c"));
        Assert.True(mid.MoveUp);
        Assert.True(mid.MoveDown);
        Assert.True(mid.MoveToFolder);
        Assert.False(mid.IsEmpty);
    }

    [Fact]
    public void TheEndsOfTheSiblingRunDropTheVerbTheyCannotHonour()
    {
        var first = Layout(SidebarTreeFixture.Pl("a"));       // first at top level
        Assert.False(first.MoveUp);
        Assert.True(first.MoveDown);

        var last = Layout(SidebarTreeFixture.Fo("h"));        // last at top level
        Assert.True(last.MoveUp);
        Assert.False(last.MoveDown);

        var only = Layout(SidebarTreeFixture.Pl("f"));        // Deep's only child
        Assert.False(only.MoveUp);
        Assert.False(only.MoveDown);
        Assert.True(only.MoveToFolder);                       // it can still leave the folder it is alone in
    }

    [Fact]
    public void AFolderRowGetsTheSameVerbsAsAPlaylistRow()
    {
        // ONE renderer, so Classic / Library V3 / Curated share this; and a folder is an ordinary sibling in its run.
        var folder = Layout(SidebarTreeFixture.Fo("k"));      // Deep, last among Chill's three children
        Assert.True(folder.MoveUp);
        Assert.False(folder.MoveDown);
        Assert.True(folder.MoveToFolder);
    }

    [Fact]
    public void ARowWithNoRunAndNowhereToGo_YieldsNoExtrasAtAll()
    {
        Assert.True(SidebarTreeNavLayout.Decide(RootlistSiblingRun.None, hasDestinations: false).IsEmpty);
    }

    // ── the wiring ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The three verbs, in order, onto the three commands. ORDER matters: Move up before Move down before
    /// "Move to folder…" is the near-to-far reading a positional menu owes its reader.</summary>
    [Fact]
    public void NavExtras_AddsTheThreeRootlistVerbsInOrder()
    {
        string body = FolderActionsTests.Body(Slot(), "SidebarMenuExtras NavExtras(");
        int up = body.IndexOf("FolderActions.MoveUp(", StringComparison.Ordinal);
        int down = body.IndexOf("FolderActions.MoveDown(", StringComparison.Ordinal);
        int to = body.IndexOf("FolderActions.MoveTo(", StringComparison.Ordinal);
        Assert.True(up >= 0, "Move up must reach FolderActions");
        Assert.True(down > up, "Move down follows Move up");
        Assert.True(to > down, "Move to folder… follows Move down");
        Assert.Contains("Strings.Menu.MoveToFolder", body, StringComparison.Ordinal);
        // Additive: the existing band/pin arm and its Remove row are untouched, which is what keeps MenuGrammarTests
        // and the navbar-customization suites green.
        Assert.Contains("_o.MoveRowByKey(sectionId, key, -1)", body, StringComparison.Ordinal);
        Assert.Contains("SidebarPaneLoc.ItemRemove", body, StringComparison.Ordinal);
    }

    /// <summary>A row inside a MULTI-SELECTION of two or more gets ONE positional verb instead of three: "Move up" has
    /// no meaning for N rows at once and would silently move only the one under the cursor, so the extras swap to
    /// <b>Move {n} to folder…</b>, which the picker honours as a batch through the same legality check the drag cue
    /// asks. Alt+↑/↓ deliberately stays single-row — it is a nudge, not a batch.</summary>
    [Fact]
    public void NavExtras_SwapsThePositionalVerbsForTheBatchOne_InsideASelection()
    {
        string body = FolderActionsTests.Body(Slot(), "SidebarMenuExtras NavExtras(");
        Assert.Contains("_o.TreeSelection.Count >= 2", body, StringComparison.Ordinal);
        Assert.Contains("Strings.Menu.MoveManyToFolder(", body, StringComparison.Ordinal);
        Assert.Contains("RootlistFolderPicker.Open(batchActs, _o.OrderedTreeSelection())", body, StringComparison.Ordinal);
        // The single-row arm is the ELSE of the same test, so the two can never both render.
        Assert.Contains("if (_o.Acts is { } treeActs && !batch)", body, StringComparison.Ordinal);
    }

    /// <summary>CHECK MODE has exactly one pointer entry point — the row menu's <b>Select</b> — and it is absent once
    /// the lane is already up. A permanently visible checkbox lane would cost every tree row 24 DIP for a gesture most
    /// sessions never use, and there is no chord a user discovers.</summary>
    [Fact]
    public void NavExtras_OffersSelect_OnlyWhileTheLaneIsDown()
    {
        string body = FolderActionsTests.Body(Slot(), "SidebarMenuExtras NavExtras(");
        Assert.Contains("!_o.TreeSelection.CheckLaneVisible", body, StringComparison.Ordinal);
        Assert.Contains("Strings.Sidebar.Select", body, StringComparison.Ordinal);
        Assert.Contains("_o.BeginTreeCheckMode(entryId)", body, StringComparison.Ordinal);
    }

    /// <summary>THE DRAG LIFTS THE SELECTION ONLY FROM INSIDE IT. Dragging a row that is IN the selection carries the
    /// whole normalised selection; dragging one OUTSIDE it carries just that row — the detail page's rule, and the
    /// only shape that cannot move things the user never aimed at. Both tree row kinds go through the ONE pane-side
    /// payload builder.</summary>
    [Fact]
    public void ATreeRowsDragPayload_ComesFromTheOnePaneBuilder()
    {
        Assert.Equal(2, FolderActionsTests.Count(Slot(), "_o.TreeDragPayload(in snapshot)"));

        string builder = FolderActionsTests.Body(Pane(),
            "internal WaveeResourceDragPayload TreeDragPayload(in SidebarLibraryEntry entry)");
        Assert.Contains("TreeSelection.Count >= 2 && TreeSelection.Contains(entry.Id)", builder, StringComparison.Ordinal);
        Assert.Contains("RootlistSelection.Normalize(RootlistTree, TreeSelection.Ids)", builder, StringComparison.Ordinal);
        Assert.Contains("WaveeResourceDragPayload.FromEntries(ordered", builder, StringComparison.Ordinal);
        Assert.Contains("WaveeResourceDragPayload.FromEntry(entry", builder, StringComparison.Ordinal);
    }

    /// <summary>The verbs are decided against the PUBLISHED TREE, not the expansion-filtered plan — "my previous
    /// sibling" must be the real one even when the row above it on screen belongs to a collapsed folder — and never on a
    /// <c>Reorderable</c>-wrapped row, whose ordering belongs to the wrapper.</summary>
    [Fact]
    public void TreeMoves_ReadsTheTree_AndStandsDownInsideAReorderBand()
    {
        string body = FolderActionsTests.Body(Slot(), "(SidebarTreeNavLayout Layout, string EntryId) TreeMoves(");
        Assert.Contains("SidebarSectionKind.PlaylistTree", body, StringComparison.Ordinal);
        Assert.Contains("_o.TryBandOf(planIndex, out _)", body, StringComparison.Ordinal);
        Assert.Contains("Binder?.CurrentInput.PlaylistTree", body, StringComparison.Ordinal);
        Assert.Contains("RootlistTreeNav.Siblings(tree, entry.Id)", body, StringComparison.Ordinal);
        // Playlists and folders only — a projected album/artist/show row in a tree section has no rootlist position.
        Assert.Contains("SidebarEntryKind.Playlist or SidebarEntryKind.Folder", body, StringComparison.Ordinal);
    }

    /// <summary>Alt+↑/↓ — the keyboard half. The accelerator is supplied only for a TREE row and never for a
    /// <c>Reorderable</c>-wrapped one, and it lands on the same <c>FolderActions.Move</c> the menu verbs use.</summary>
    [Fact]
    public void AltArrows_AreSuppliedForTreeRowsOnly_AndShareTheMenusCommand()
    {
        string slot = Slot();
        Assert.Contains("treeRow && rootlistItem && _o.Acts is { } moveActs", slot, StringComparison.Ordinal);
        Assert.Contains("FolderActions.Move(moveActs, snapshot.Id, d)", slot, StringComparison.Ordinal);
        Assert.Equal(2, FolderActionsTests.Count(slot, "OnMove = move,"));   // the entity row and the folder row

        string row = FolderActionsTests.Body(Row(), "static Action<KeyEventArgs> KeyHandler(");
        Assert.Contains("Keys.F2", row, StringComparison.Ordinal);
        Assert.Contains("e.Mods != KeyModifiers.Alt", row, StringComparison.Ordinal);
        Assert.Contains("move(-1)", row, StringComparison.Ordinal);
        Assert.Contains("move(1)", row, StringComparison.Ordinal);
        // Supplying either verb makes the row a focus stop — a key handler on a node nothing can focus is dead code.
        Assert.Contains("spec.OnRename is not null || spec.OnMove is not null", Row(), StringComparison.Ordinal);
    }

    static string Slot() => System.IO.File.ReadAllText(System.IO.Path.Combine(
        FolderActionsTests.AppRoot(), "Features", "Sidebar", "Pane", "SidebarPaneSlot.cs"));

    static string Row() => System.IO.File.ReadAllText(System.IO.Path.Combine(
        FolderActionsTests.AppRoot(), "Features", "Sidebar", "Shared", "SidebarEntityRow.cs"));

    static string Pane() => System.IO.File.ReadAllText(System.IO.Path.Combine(
        FolderActionsTests.AppRoot(), "Features", "Sidebar", "Pane", "SidebarPane.cs"));
}
