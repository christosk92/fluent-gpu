using System;
using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE "MOVE TO FOLDER…" DESTINATION LIST. Move up/down walk one sibling at a time and "Move out of {parent}" climbs
/// exactly one level, so filing a playlist into a folder that is not adjacent to it used to be a drag and nothing else
/// (D12). The picker names every legal destination at once.
///
/// <para><b>The list is not its own rule.</b> Legality is <c>RootlistTreeMoves.Check</c> — the same table the drop cue
/// draws its refusals from — so the picker cannot offer a destination a drag would refuse. These tests drive the pure
/// builder; the flyout shell around it is <c>PlaylistPickerPanel</c>'s, re-used.</para>
/// </summary>
public class RootlistFolderPickerTests
{
    static List<RootlistFolderChoice> Destinations(string sourceId)
    {
        var into = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(SidebarTreeFixture.Tree(), sourceId, into);
        return into;
    }

    static string[] Names(IReadOnlyList<RootlistFolderChoice> rows)
    {
        var names = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++) names[i] = rows[i].IsTopLevel ? "<top>" : rows[i].Name;
        return names;
    }

    [Fact]
    public void TopLevelIsPinnedFirst_ThenTheFoldersInTreeOrder()
    {
        // Top level leads because it is the destination with no folder to scroll to, and the un-nest is what a user
        // reaching for this verb most often wants.
        Assert.Equal(new[] { "<top>", "Chill", "Deep", "Trailing" }, Names(Destinations(SidebarTreeFixture.Pl("a"))));
        Assert.True(Destinations(SidebarTreeFixture.Pl("a"))[0].IsTopLevel);
    }

    [Fact]
    public void FoldersCarryTheirTreeDepth_SoTheListReadsAsNested()
    {
        var rows = Destinations(SidebarTreeFixture.Pl("a"));
        Assert.Equal(0, rows[1].Depth);       // Chill, top level
        Assert.Equal(1, rows[2].Depth);       // Deep, inside Chill — indented one step by the picker
        Assert.Equal(0, rows[3].Depth);       // Trailing, back at the top
        Assert.Equal("g", rows[1].FolderId);
        Assert.Equal("", rows[0].FolderId);   // the top-level row addresses no folder at all
    }

    [Fact]
    public void ADraggedFoldersOwnSubtreeIsExcluded()
    {
        // Chill into Chill is "a folder into itself"; Chill into Deep is "into its own descendant". Both are refused by
        // the drop cue, so neither may be offered here — the picker and the drag answer to one table.
        Assert.Equal(new[] { "<top>", "Trailing" }, Names(Destinations(SidebarTreeFixture.Fo("g"))));
    }

    [Fact]
    public void TheFolderARowIsAlreadyTheLastChildOf_IsExcluded()
    {
        // e is Trailing's only (therefore last) child: "Inside Trailing" appends where it already is. That is the
        // adjacent no-op the refusal table calls AlreadyThere, and a picker row for it would do nothing visible.
        Assert.Equal(SidebarDropRefusal.NoOp,
                     RootlistTreeMoves.Check(SidebarTreeFixture.Tree(), SidebarTreeFixture.Pl("e"),
                                             SidebarTreeFixture.Fo("h"), RootlistDropPlacement.Inside));
        // …and the Top level row is absent for the same reason, not a different one: e is ALSO the flattened tree's
        // last entry, so "after the last top-level entry" is the position it already occupies. The picker inherits that
        // verdict from the shared table rather than second-guessing it — the drag refuses the identical gesture.
        Assert.Equal(new[] { "Chill", "Deep" }, Names(Destinations(SidebarTreeFixture.Pl("e"))));
    }

    [Fact]
    public void TheLastTopLevelEntry_GetsNoTopLevelRow()
    {
        // Trailing already IS the end of the top level — "after itself" is where it is. Absent, never a dead row.
        var rows = Destinations(SidebarTreeFixture.Fo("h"));
        Assert.DoesNotContain(rows, r => r.IsTopLevel);
        Assert.Equal(new[] { "Chill", "Deep" }, Names(rows));
        Assert.False(RootlistTreeNav.TryTopLevelAnchor(SidebarTreeFixture.Tree(), SidebarTreeFixture.Fo("h"), out _));
    }

    [Fact]
    public void TheTopLevelAnchorIsTheLastTopLevelENTRY_SoATrailingFolderIsPassed()
    {
        // The anchor is the trailing FOLDER, landed After — whose exclusive range end is outside it. Anchoring on the
        // folder's last CHILD instead is exactly the D2 shape that put an item back inside the folder it left.
        Assert.True(RootlistTreeNav.TryTopLevelAnchor(SidebarTreeFixture.Tree(), SidebarTreeFixture.Pl("b"), out var anchor));
        Assert.Equal(new RootlistItemRef("h", IsFolder: true), anchor);
    }

    [Fact]
    public void AnUnknownSourceOrAnEmptyTree_OffersNothingRatherThanEverything()
    {
        var into = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(null, SidebarTreeFixture.Pl("a"), into);
        Assert.Empty(into);
        RootlistTreeNav.PickerDestinations(Array.Empty<SidebarLibraryEntry>(), SidebarTreeFixture.Pl("a"), into);
        Assert.Empty(into);
    }

    [Fact]
    public void HasDestinations_AgreesWithTheListItSummarises()
    {
        foreach (string id in new[]
                 {
                     SidebarTreeFixture.Pl("a"), SidebarTreeFixture.Pl("e"), SidebarTreeFixture.Pl("f"),
                     SidebarTreeFixture.Fo("g"), SidebarTreeFixture.Fo("h"), "pl:spotify:playlist:ghost",
                 })
            Assert.Equal(Destinations(id).Count > 0,
                         RootlistTreeNav.HasDestinations(SidebarTreeFixture.Tree(), id));
    }
}
