using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// THE THREE MECHANISMS THAT STOPPED THE SIDEBAR RE-RENDERING ITSELF (Wave 3 of the perf plan).
//
// A live diag session measured, at idle and on every track boundary/navigation, the SAME three storms:
//   • SidebarPaneSlot×52 + SidebarSelectionPill×44 + SidebarChevron×6 on every publish, because
//     `SidebarPane.PublishStage`'s wholesale `!ReferenceEquals(stage.Document, Doc)` test always tripped — Classic minted
//     a fresh document on every pane render, so the landed per-row `SidebarRowDiff` was dead code;
//   • two to three publishes per boundary/nav to begin with, because `SidebarEntries.Publish` bumped its version on
//     every projection rebuild even when the rebuild reproduced the same rows;
//   • the whole realized window again on every navigation, because selection was a raw route-signal read inside every
//     slot, every pill and the rail.
//
// All three fixes are pure decisions, deliberately split into `Features/Sidebar/Data/` (and, for Classic's cache,
// alongside its already-testable built-in document) so they are pinned HERE against production code rather than a copy.
// Getting them too EAGER is only a perf regression; too LAZY is a correctness bug — a stale row, a frozen sidebar, a
// pill left on the page you navigated away from — which is exactly what each direction below asserts.
public sealed class SidebarChurnTests
{
    // ── F3a: Classic's document instance is stable while its collapse flags are ───────────────────────────────────────

    [Fact]
    public void ClassicDocument_IsTheSameInstance_WhileTheCollapseFlagsAreUnchanged()
    {
        var cache = new ClassicDocumentCache();
        var first = cache.Get(true, true, true);
        Assert.Same(first, cache.Get(true, true, true));
        Assert.Same(first, cache.Get(true, true, true));   // the pane calls this on EVERY render
    }

    [Fact]
    public void ClassicDocument_IsRebuilt_OnEveryFlagFlip()
    {
        var cache = new ClassicDocumentCache();
        var open = cache.Get(true, true, true);

        var pinnedClosed = cache.Get(false, true, true);
        Assert.NotSame(open, pinnedClosed);
        Assert.True(pinnedClosed.Find(SidebarBuiltInDocuments.PinnedId)!.Collapsed);

        var libraryClosed = cache.Get(true, false, true);
        Assert.NotSame(pinnedClosed, libraryClosed);
        Assert.True(libraryClosed.Find(SidebarBuiltInDocuments.LibraryId)!.Collapsed);

        var playlistsClosed = cache.Get(true, true, false);
        Assert.NotSame(libraryClosed, playlistsClosed);
        Assert.True(playlistsClosed.Find(SidebarBuiltInDocuments.PlaylistsId)!.Collapsed);

        // …and flipping back is a rebuild too (one slot, not a table) — the point is instance stability per STATE, not a
        // memo of every state ever seen.
        Assert.NotSame(open, cache.Get(true, true, true));
    }

    [Fact]
    public void ClassicFlagBitmask_IsOnePerFlagCombination()
    {
        // The pane's mode epoch folds the same three flags with the same bitmask. If these two ever disagreed, a toggle
        // would re-plan against a document the cache decided not to rebuild.
        var seen = new HashSet<int>();
        for (int i = 0; i < 8; i++)
            Assert.True(seen.Add(ClassicDocumentCache.FlagsOf((i & 1) != 0, (i & 2) != 0, (i & 4) != 0)));
        Assert.Equal(0, ClassicDocumentCache.FlagsOf(false, false, false));
        Assert.Equal(7, ClassicDocumentCache.FlagsOf(true, true, true));
    }

    // ── F3b: the projection publish is content-gated ──────────────────────────────────────────────────────────────────

    static SidebarEntriesMeta Meta(int state = 0, Exception? error = null, bool pending = false,
                                   bool qualifiers = false, int pinCount = 0)
        => new(state, error, pending, qualifiers, pinCount);

    static SidebarLibraryEntry Playlist(string id, string name = "n", int childCount = 0,
                                        IReadOnlyList<string>? mosaic = null)
        => new(id, SidebarEntryKind.Playlist, "spotify:playlist:" + id, name, "", null, mosaic, childCount,
               0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None);

    [Fact]
    public void FirstPublish_AlwaysCounts_AsAChange()
    {
        var shadow = new SidebarEntriesShadow();
        Assert.True(shadow.Publish(Array.Empty<SidebarLibraryEntry>(), Meta()));
        // …and an identical EMPTY republish does not (an empty library still must not storm).
        Assert.False(shadow.Publish(Array.Empty<SidebarLibraryEntry>(), Meta()));
    }

    [Fact]
    public void AnIdenticalRepublish_IsNotAChange()
    {
        var shadow = new SidebarEntriesShadow();
        var rows = new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b"), Playlist("c") };
        Assert.True(shadow.Publish(rows, Meta(pinCount: 1)));

        // The producer refills the SAME buffer in place, so the republish is a fresh list with equal content — which is
        // exactly the shape the binder pump produces on a queue/track/play-log tick.
        var again = new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b"), Playlist("c") };
        Assert.False(shadow.Publish(again, Meta(pinCount: 1)));
        Assert.False(shadow.Publish(again, Meta(pinCount: 1)));
    }

    [Fact]
    public void AnyRowDelta_IsAChange()
    {
        var shadow = new SidebarEntriesShadow();
        var rows = new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") };
        Assert.True(shadow.Publish(rows, Meta()));

        Assert.True(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a") }, Meta()));                 // count
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta()));  // count back
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("b"), Playlist("a") }, Meta()));  // order
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("b"), Playlist("a", name: "renamed"),
        }, Meta()));                                                                                          // a member
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("b"), Playlist("a", name: "renamed", childCount: 12),
        }, Meta()));                                                                                          // another
        // …and the settled content is quiet again.
        Assert.False(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("b"), Playlist("a", name: "renamed", childCount: 12),
        }, Meta()));
    }

    [Fact]
    public void AnyMetaDelta_IsAChange_EvenWithIdenticalRows()
    {
        var shadow = new SidebarEntriesShadow();
        var rows = new List<SidebarLibraryEntry> { Playlist("a") };
        Assert.True(shadow.Publish(rows, Meta()));

        Assert.True(shadow.Publish(rows, Meta(state: 1)));                 // LoadState
        Assert.True(shadow.Publish(rows, Meta(state: 1, pending: true)));  // AnyContributingKindPending
        Assert.True(shadow.Publish(rows, Meta(state: 1, pending: true, qualifiers: true)));
        Assert.True(shadow.Publish(rows, Meta(state: 1, pending: true, qualifiers: true, pinCount: 3)));
        Assert.False(shadow.Publish(rows, Meta(state: 1, pending: true, qualifiers: true, pinCount: 3)));

        // Error compares by REFERENCE: a new failure is a new publish even when its message repeats.
        var boom = new InvalidOperationException("boom");
        Assert.True(shadow.Publish(rows, Meta(state: 2, error: boom, pending: true, qualifiers: true, pinCount: 3)));
        Assert.False(shadow.Publish(rows, Meta(state: 2, error: boom, pending: true, qualifiers: true, pinCount: 3)));
        Assert.True(shadow.Publish(rows, Meta(state: 2, error: new InvalidOperationException("boom"),
                                              pending: true, qualifiers: true, pinCount: 3)));
    }

    [Fact]
    public void MosaicTiles_CompareByValue_SoAFolderyLibraryStillSettles()
    {
        // SidebarProjection materializes a folder's tile list FRESH on every rebuild, so a reference compare would
        // report every folder row as changed and the gate would never fire for a library that has folders.
        var shadow = new SidebarEntriesShadow();
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: new List<string> { "u1", "u2" }),
        }, Meta()));
        Assert.False(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: new List<string> { "u1", "u2" }),   // equal by value, different instance
        }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: new List<string> { "u1", "u3" }),   // a real cover change
        }, Meta()));
        Assert.True(shadow.Publish(new List<SidebarLibraryEntry>
        {
            Playlist("a", mosaic: null),                             // and losing the mosaic entirely
        }, Meta()));
    }

    [Fact]
    public void ThePublishedShadow_MirrorsTheLastAcceptedRebuild()
    {
        var shadow = new SidebarEntriesShadow();
        shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta());
        Assert.Equal(2, shadow.Published.Count);
        Assert.Equal("a", shadow.Published[0].Id);

        // A skipped bump must not corrupt the snapshot the NEXT compare runs against.
        shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta());
        Assert.Equal(2, shadow.Published.Count);
        Assert.False(shadow.Publish(new List<SidebarLibraryEntry> { Playlist("a"), Playlist("b") }, Meta()));
    }

    // ── F3c: the selection sweep ──────────────────────────────────────────────────────────────────────────────────────

    const string Sec = "sec";

    static SidebarRow Row(SidebarRowKind kind, string key, int entryIndex = -1, int itemCount = 0)
        => new(kind, Sec, 0, entryIndex, itemCount, key);

    static SidebarSectionSpec Section(params SidebarItemSpec[] items)
        => new(Sec, SidebarSectionKind.CustomGroup, Items: items);

    static List<int> Sweep(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries,
                           SidebarSectionSpec section, string route)
    {
        var into = new List<int>();
        SidebarRowResolve.Sweep(rows, entries, _ => section, route, into);
        return into;
    }

    static List<int> Flipped(IReadOnlyList<int> previous, IReadOnlyList<int> next)
    {
        var into = new List<int>();
        SidebarRowResolve.Flipped(previous, next, into);
        return into;
    }

    /// <summary>An ENTITY ROW, a GRID STRIP over three cells, a hand-placed ROUTE row, and chrome — the shapes the pane
    /// actually draws a selection cue on, plus the ones it must never bump.</summary>
    static (SidebarRow[] Rows, SidebarLibraryEntry[] Entries, SidebarSectionSpec Section) Plan()
    {
        var entries = new[]
        {
            Playlist("pl:one"),      // 0 — the entity row
            Playlist("pl:two"),      // 1 ┐
            Playlist("pl:three"),    // 2 ├ the grid strip
            Playlist("pl:four"),     // 3 ┘
        };
        var rows = new[]
        {
            Row(SidebarRowKind.SectionHeader, Sec),                          // 0 — chrome, never selected
            Row(SidebarRowKind.EntityRow, "pl:one", entryIndex: 0),          // 1
            Row(SidebarRowKind.IconRow, "liked"),                            // 2 — a hand-placed route
            Row(SidebarRowKind.GridStrip, Sec, entryIndex: 1, itemCount: 3), // 3 — pl:two/three/four
            Row(SidebarRowKind.Divider, Sec),                                // 4 — chrome
        };
        var section = Section(new SidebarItemSpec("i:liked", SidebarItemTarget.Route, "liked"));
        return (rows, entries, section);
    }

    [Fact]
    public void TheSweep_FindsTheRowThatDrawsTheCue_ForEveryKindThatHasOne()
    {
        var (rows, entries, section) = Plan();
        Assert.Equal(new[] { 1 }, Sweep(rows, entries, section, "pl:one"));
        Assert.Equal(new[] { 2 }, Sweep(rows, entries, section, "liked"));
        // A grid strip is ONE plan row over several cells, so any cell in range selects the row (that is the unit a
        // per-row epoch can address).
        Assert.Equal(new[] { 3 }, Sweep(rows, entries, section, "pl:two"));
        Assert.Equal(new[] { 3 }, Sweep(rows, entries, section, "pl:four"));
        Assert.Empty(Sweep(rows, entries, section, "somewhere-else"));
        Assert.Empty(Sweep(rows, entries, section, ""));
    }

    [Fact]
    public void ARouteChange_FlipsExactlyTheOldAndNewMatchingRows()
    {
        var (rows, entries, section) = Plan();

        var atEntity = Sweep(rows, entries, section, "pl:one");
        var atGrid = Sweep(rows, entries, section, "pl:three");
        var atRail = Sweep(rows, entries, section, "liked");

        Assert.Equal(new[] { 1, 3 }, Flipped(atEntity, atGrid));   // entity row out, grid strip in
        Assert.Equal(new[] { 2, 3 }, Flipped(atGrid, atRail));     // grid strip out, route row in
        Assert.Equal(new[] { 1, 2 }, Flipped(atRail, atEntity));   // route row out, entity row in

        // Navigating somewhere the sidebar does not show only retires the outgoing row; arriving only lights the new one.
        Assert.Equal(new[] { 1 }, Flipped(atEntity, new List<int>()));
        Assert.Equal(new[] { 1 }, Flipped(new List<int>(), atEntity));

        // A row that stays selected is NOT bumped — a republish that did not move it must not re-render it.
        Assert.Empty(Flipped(atEntity, Sweep(rows, entries, section, "pl:one")));

        // …and moving WITHIN one grid strip is invisible: the strip row draws both cells, so nothing flipped at the row
        // level even though the selected cell moved.
        Assert.Empty(Flipped(Sweep(rows, entries, section, "pl:two"), Sweep(rows, entries, section, "pl:four")));
    }

    [Fact]
    public void ARowThatCanNeverBeSelected_IsNeverSwept()
    {
        var entries = new[] { Playlist("pl:one") };
        var section = Section(
            new SidebarItemSpec("i:act", SidebarItemTarget.Action, "wavee.play"),
            new SidebarItemSpec("i:trk", SidebarItemTarget.Track, "spotify:track:t"));

        // An ACTION item never selects even when the planner resolved an entry onto its row (the slot returns an
        // ActionRow before it ever reaches the entity branch).
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.IconRow, "wavee.play", entryIndex: 0) }, entries, section, "pl:one"));
        // A hand-placed TRACK plays; it has no route to be selected by.
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.IconRow, "spotify:track:t") }, entries, section, "spotify:track:t"));
        // Folders, chrome and unresolved retention rows draw no cue.
        foreach (var kind in new[]
        {
            SidebarRowKind.FolderHeader, SidebarRowKind.SectionHeader, SidebarRowKind.HeaderLabel,
            SidebarRowKind.Divider, SidebarRowKind.Empty, SidebarRowKind.Skeleton,
            SidebarRowKind.CreateAction, SidebarRowKind.PromptRow,
        })
            Assert.Empty(Sweep(new[] { Row(kind, "pl:one", entryIndex: 0) }, entries, section, "pl:one"));
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.EntityRow, "pl:missing") }, entries, Section(), "pl:missing"));
    }

    [Fact]
    public void AGridStripOutOfRange_IsClampedNotThrown()
    {
        // A slot can transiently address a plan the entries no longer back (the count signal lands one hop after the
        // plan), and the sweep runs on the pane's thread against whatever pair is current.
        var entries = new[] { Playlist("pl:one") };
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.GridStrip, Sec, entryIndex: 0, itemCount: 9) },
                           entries, Section(), "pl:nine"));
        Assert.Equal(new[] { 0 }, Sweep(new[] { Row(SidebarRowKind.GridStrip, Sec, entryIndex: 0, itemCount: 9) },
                                        entries, Section(), "pl:one"));
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.GridStrip, Sec, entryIndex: 4, itemCount: 2) },
                           entries, Section(), "pl:one"));
    }

    [Fact]
    public void AnEntityCard_SelectsOnItsEntityRoute()
    {
        var entries = new[] { Playlist("pl:card") };
        Assert.Equal(new[] { 0 },
            Sweep(new[] { Row(SidebarRowKind.EntityCard, "pl:card", entryIndex: 0) }, entries, Section(), "pl:card"));
        // An UNRESOLVED card falls back to the pin route derived from its (empty) uri — i.e. nothing.
        Assert.Empty(Sweep(new[] { Row(SidebarRowKind.EntityCard, "pl:card") }, entries, Section(), "pl:card"));
    }
}
