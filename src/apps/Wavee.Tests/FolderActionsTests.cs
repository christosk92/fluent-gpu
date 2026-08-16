using System;
using System.IO;
using System.Runtime.CompilerServices;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// SIBLING RESOLUTION — the rule behind Move up / Move down / Alt+↑ / Alt+↓ (D12).
///
/// <para>Reordering the rootlist used to be a DRAG and nothing else: <c>NavExtras</c> built Move up/down only for
/// reorder bands and pins, and a keyboard-only user could not move a playlist at all. The verbs address the SIBLING RUN
/// — the entries sharing a parent folder — because "the rows at my depth" would fuse two different folders' children
/// into one list and walk an item out of its folder sideways.</para>
///
/// <para>The pure half is driven directly (<c>Features/Sidebar/Data</c> is source-included); the command half is pinned
/// by source scan, because <c>FolderActions</c> is engine code.</para>
/// </summary>
public class FolderActionsTests
{
    static RootlistSiblingRun Run(string id) => RootlistTreeNav.Siblings(SidebarTreeFixture.Tree(), id);

    // ── top level ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TopLevelRun_CountsFoldersAsSiblings_NotTheirContents()
    {
        // a · [Chill] · d · [Trailing] — four siblings, even though seven rows sit between the first and the last.
        var run = Run(SidebarTreeFixture.Pl("a"));
        Assert.Equal(0, run.Position);
        Assert.Equal(4, run.Count);
        Assert.False(run.CanMoveUp);                                  // the run's first item
        Assert.True(run.CanMoveDown);
        // The next sibling is the FOLDER, addressed by its group id — which is what makes Move down step OVER it
        // (RootlistOps resolves After against the folder's whole span) rather than into it.
        Assert.Equal(new RootlistItemRef("g", IsFolder: true), run.Next);
    }

    [Fact]
    public void LastTopLevelEntry_HasNoMoveDown()
    {
        var run = Run(SidebarTreeFixture.Fo("h"));
        Assert.Equal(3, run.Position);
        Assert.Equal(4, run.Count);
        Assert.True(run.CanMoveUp);
        Assert.False(run.CanMoveDown);
        Assert.Equal(new RootlistItemRef(SidebarTreeFixture.PlaylistUriPrefix + "d", IsFolder: false), run.Previous);
    }

    // ── nested ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NestedRun_IsScopedToItsFolder_NotToItsDepth()
    {
        // b's siblings are Chill's children (b · c · [Deep]) — NOT "everything at depth 1", which would also sweep in
        // Trailing's child e and let a Move down walk b into a different folder.
        var run = Run(SidebarTreeFixture.Pl("b"));
        Assert.Equal(0, run.Position);
        Assert.Equal(3, run.Count);
        Assert.Equal(new RootlistItemRef(SidebarTreeFixture.PlaylistUriPrefix + "c", IsFolder: false), run.Next);

        var middle = Run(SidebarTreeFixture.Pl("c"));
        Assert.Equal(1, middle.Position);
        Assert.True(middle.CanMoveUp);
        Assert.True(middle.CanMoveDown);
        Assert.Equal(new RootlistItemRef(SidebarTreeFixture.PlaylistUriPrefix + "b", IsFolder: false), middle.Previous);
        Assert.Equal(new RootlistItemRef("k", IsFolder: true), middle.Next);
    }

    [Fact]
    public void AnOnlyChild_HasNeitherVerb()
    {
        var run = Run(SidebarTreeFixture.Pl("f"));      // Deep's only child
        Assert.Equal(0, run.Position);
        Assert.Equal(1, run.Count);
        Assert.False(run.CanMoveUp);
        Assert.False(run.CanMoveDown);
    }

    [Fact]
    public void ARowTheTreeDoesNotShow_HasNoRunAtAll()
    {
        // Absent, never "position 0 of 0": a verb built from a run that does not exist would move the wrong row.
        Assert.True(Run("pl:spotify:playlist:ghost").IsEmpty);
        Assert.True(RootlistTreeNav.Siblings(null, SidebarTreeFixture.Pl("a")).IsEmpty);
        Assert.True(RootlistTreeNav.Siblings(SidebarTreeFixture.Tree(), "").IsEmpty);
    }

    // ── the layout the menu draws from it ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEndsOfTheRunHideTheVerb_TheyDoNotDisableIt()
    {
        var first = SidebarTreeNavLayout.Decide(Run(SidebarTreeFixture.Pl("a")), hasDestinations: true);
        Assert.False(first.MoveUp);
        Assert.True(first.MoveDown);

        var last = SidebarTreeNavLayout.Decide(Run(SidebarTreeFixture.Fo("h")), hasDestinations: true);
        Assert.True(last.MoveUp);
        Assert.False(last.MoveDown);

        // "Move to folder…" survives both ends — an item that cannot move inside its run can still be filed elsewhere —
        // but never opens an empty picker.
        Assert.True(last.MoveToFolder);
        Assert.False(SidebarTreeNavLayout.Decide(Run(SidebarTreeFixture.Pl("a")), hasDestinations: false).MoveToFolder);
        Assert.True(SidebarTreeNavLayout.Decide(RootlistSiblingRun.None, hasDestinations: false).IsEmpty);
    }

    // ── the commands ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every organisation verb resolves against the LIVE tree and commits through the ONE seam a drop uses —
    /// which is what gives it the awaited failure mapping, the announce, the "Moved to {name}" toast and the Undo,
    /// without any of it being written a second time.</summary>
    [Theory]
    [InlineData("public static void Move(ActionServices s, string entryId, int delta)")]
    [InlineData("public static void MoveOut(ActionServices s, string entryId)")]
    public void EveryOrganisationVerb_ResolvesLive_AndCommitsThroughTheOneSeam(string signature)
    {
        string body = Body(FolderActions(), signature);
        Assert.Contains("Tree(s)", body, StringComparison.Ordinal);
        Assert.Contains("RootlistTreeNav.TryEntry", body, StringComparison.Ordinal);
        Assert.Contains("Commit(s, in entry", body, StringComparison.Ordinal);
        // No second mutation path: the verb never calls the seam itself.
        Assert.DoesNotContain("MoveRootlistItemAsync", body, StringComparison.Ordinal);
    }

    /// <summary>The one commit: the pre-move Undo anchor is captured BEFORE the mutation (afterwards, where the item
    /// used to be is unknowable) and the move goes to <c>WaveeResourceDrop.MoveRootlist</c>.</summary>
    [Fact]
    public void Commit_CapturesTheUndoAnchorFirst_ThenHandsTheMoveToTheDropSeam()
    {
        string body = Body(FolderActions(), "internal static void Commit(ActionServices s");
        int anchor = body.IndexOf("RootlistUndoAnchors.TryResolve", StringComparison.Ordinal);
        int commit = body.IndexOf("WaveeResourceDrop.MoveRootlist", StringComparison.Ordinal);
        Assert.True(anchor >= 0 && commit > anchor, "the undo anchor must be resolved before the move is issued");
        Assert.Contains("WaveeResourceDragPayload.FromEntry", body, StringComparison.Ordinal);
    }

    /// <summary>"Move out of {parent}" was fire-and-forget with an error-only toast: a successful un-nest said nothing
    /// and could not be taken back (D13). It rides the shared confirm now — and it names the folder it lands IN, not
    /// the one it came out of.</summary>
    [Fact]
    public void MoveOut_AnnouncesAndOffersUndo_AndNamesTheDestination()
    {
        string body = Body(FolderActions(), "public static void MoveOut(ActionServices s, string entryId)");
        Assert.Contains("RootlistTreeNav.TryFolder", body, StringComparison.Ordinal);
        Assert.Contains("parent.ParentFolderName", body, StringComparison.Ordinal);
        // The old shape is gone, not merely bypassed.
        Assert.Equal(0, Count(FolderActions(), "async Task Run()\n            {\n                try\n                {\n                    await lib.MoveRootlistItemAsync"));
        // …and the confirm it now rides is the drop's own: announce + toast + Undo, no raw exception text anywhere.
        string drop = Source("Features/DragDrop", "WaveeResourceDrag.cs");
        Assert.Contains("PlaylistEditVerb.Reorder", drop, StringComparison.Ordinal);
        Assert.Equal(0, Count(FolderActions(), "ex.Message"));
    }

    // ── source-scan plumbing (the MenuGrammarTests precedent) ───────────────────────────────────────────────────────

    static string FolderActions() => Source("Actions", "FolderActions.cs");

    static string Source(string dir, string file)
    {
        string path = Path.Combine(AppRoot(), Path.Combine(dir.Split('/')), file);
        Assert.True(File.Exists(path), $"source not found (was it moved?): {path}");
        return File.ReadAllText(path);
    }

    internal static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"member not found (was it renamed?): {signature}");
        int block = source.IndexOf("\n    }", at, StringComparison.Ordinal);
        int expr = source.IndexOf("\n    ];", at, StringComparison.Ordinal);
        int end = block < 0 ? expr : expr < 0 ? block : System.Math.Min(block, expr);
        Assert.True(end > at, $"could not delimit the body of: {signature}");
        return source[at..end];
    }

    internal static int Count(string source, string needle)
    {
        int n = 0;
        for (int i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    internal static string AppRoot([CallerFilePath] string here = "")
    {
        string tests = Path.GetDirectoryName(here)!;                      // …/Wavee.Tests
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        Assert.True(Directory.Exists(app), $"app source root not found: {app}");
        return app;
    }
}
