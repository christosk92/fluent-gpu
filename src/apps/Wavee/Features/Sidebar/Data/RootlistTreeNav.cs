using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// THE NON-MOUSE HALF OF SIDEBAR ORGANISATION (D12).
//
// WHY IT EXISTS. Reordering the rootlist used to be a DRAG AND NOTHING ELSE: `NavExtras` built Move up / Move down only
// for reorder bands and pins, the one tree verb was "Move out of {parent}" (one level, one direction), and a keyboard-
// only user could not move a playlist at all. A drag is one of several ways to reorder, never the only one (P6).
//
// THE MODEL. Everything a menu verb, an Alt+arrow accelerator and the folder picker need is a question about the SAME
// depth-first flattened tree the cue resolver already decides against (`RootlistTreeMoves`): who are my siblings, where
// am I among them, and which folders may I be filed into. Those three answers are pure, so they live here — engine-free
// (System + Wavee.Core only), source-included by `Wavee.Tests` exactly like `RootlistSlotResolver`, and driven by
// `FolderActionsTests` / `SidebarNavExtrasTests` / `RootlistFolderPickerTests` as the REAL rules rather than a copy.
//
// Every verb these answers feed commits through the ONE seam a drop uses (`WaveeResourceDrop.MoveRootlist` →
// `MoveRootlistItemAsync`), so the menu, the keyboard and the pointer cannot disagree about what a move is, what it
// announces, or what its Undo restores.

/// <summary>Where one entry sits among its SIBLINGS (the entries sharing its parent folder), and the two neighbours a
/// Move up / Move down addresses.
/// <para><see cref="Previous"/>/<see cref="Next"/> carry an empty <c>Key</c> when the run has no neighbour on that side
/// — which is exactly when the verb is ABSENT from the menu rather than present-and-dead.</para></summary>
public readonly record struct RootlistSiblingRun(int Position, int Count, RootlistItemRef Previous, RootlistItemRef Next)
{
    /// <summary>"This entry is not in the tree" — no run, and therefore no move verbs at all.</summary>
    public static readonly RootlistSiblingRun None =
        new(-1, 0, new RootlistItemRef("", false), new RootlistItemRef("", false));

    public bool IsEmpty => Position < 0;

    /// <summary>There is a previous sibling to land BEFORE.</summary>
    public bool CanMoveUp => Position > 0 && Previous.Key.Length > 0;

    /// <summary>There is a next sibling to land AFTER.</summary>
    public bool CanMoveDown => Position >= 0 && Position < Count - 1 && Next.Key.Length > 0;
}

/// <summary>Which move verbs a rootlist TREE row's menu offers. The sibling-run analogue of
/// <c>SidebarNavLayout</c> (which decides the navbar-customization extras for bands and pins) — kept separate because
/// the two answer different questions about different lists and merging them would make the pin arm depend on the
/// rootlist tree.</summary>
public readonly record struct SidebarTreeNavLayout(bool MoveUp, bool MoveDown, bool MoveToFolder)
{
    public bool IsEmpty => !MoveUp && !MoveDown && !MoveToFolder;

    /// <summary>Verbs at the ENDS of the run are absent, never disabled: "Move up" on the first sibling would be a
    /// promise the command refuses. "Move to folder…" survives at both ends — an item that cannot move within its run
    /// can still be filed somewhere else — but not when the picker would have nowhere to offer.</summary>
    public static SidebarTreeNavLayout Decide(in RootlistSiblingRun run, bool hasDestinations)
        => new(run.CanMoveUp, run.CanMoveDown, hasDestinations);
}

/// <summary>One row of the "Move to folder…" picker: a real folder, or the pinned TOP LEVEL row (<see cref="FolderId"/>
/// empty, and <see cref="Name"/> empty because its label is localized chrome the pure layer must not resolve).
/// <see cref="Depth"/> is the folder's own tree depth, which the picker renders as indentation so a nested destination
/// reads as nested.</summary>
public readonly record struct RootlistFolderChoice(string FolderId, string Name, int Depth)
{
    /// <summary>The pinned "Top level" row — Your Library's own end, the one destination that is not a folder.</summary>
    public bool IsTopLevel => FolderId.Length == 0;
}

/// <summary>Sibling runs, folder destinations and the top-level anchor — the three pure questions behind the sidebar's
/// keyboard/menu organisation verbs.</summary>
public static class RootlistTreeNav
{
    /// <summary>Where <paramref name="entryId"/> sits among its siblings, and the neighbours on either side.
    /// <para>Siblings are the entries sharing this one's <c>ParentFolderId</c> ("" at top level) in tree order — NOT
    /// "the entries at the same depth", which would fuse two different folders' children into one run.</para></summary>
    public static RootlistSiblingRun Siblings(IReadOnlyList<SidebarLibraryEntry>? tree, string entryId)
    {
        if (tree is null || tree.Count == 0 || string.IsNullOrEmpty(entryId)) return RootlistSiblingRun.None;

        string parent = "";
        bool found = false;
        for (int i = 0; i < tree.Count; i++)
        {
            if (!string.Equals(tree[i].Id, entryId, StringComparison.Ordinal)) continue;
            parent = tree[i].ParentFolderId;
            found = true;
            break;
        }
        if (!found) return RootlistSiblingRun.None;

        int position = -1, count = 0;
        var previous = new RootlistItemRef("", false);
        var next = new RootlistItemRef("", false);
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (!string.Equals(e.ParentFolderId, parent, StringComparison.Ordinal)) continue;
            if (string.Equals(e.Id, entryId, StringComparison.Ordinal)) { position = count; }
            else if (position < 0) previous = RefOf(in e);            // the last sibling seen BEFORE us
            else if (next.Key.Length == 0) next = RefOf(in e);        // the first sibling seen after us
            count++;
        }
        return position < 0 ? RootlistSiblingRun.None : new RootlistSiblingRun(position, count, previous, next);
    }

    /// <summary>EVERY destination the "Move to folder…" picker offers, in render order: the pinned <b>Top level</b> row
    /// first (<see cref="RootlistFolderChoice.IsTopLevel"/>), then the legal folders in tree order.
    /// <para>Top level leads because it is the destination with no folder to scroll to and the one a user reaching for
    /// this verb most often wants — it is the un-nest.</para></summary>
    public static void PickerDestinations(IReadOnlyList<SidebarLibraryEntry>? tree, string sourceId,
                                          List<RootlistFolderChoice> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (TryTopLevelAnchor(tree, sourceId, out _)) into.Add(new RootlistFolderChoice("", "", 0));
        FolderChoices(tree, sourceId, into);
    }

    /// <summary>Is there anywhere at all to file <paramref name="sourceId"/>? The allocation-free question behind the
    /// menu's "Move to folder…" row — a verb that would open an EMPTY picker must be absent, not present and useless.</summary>
    public static bool HasDestinations(IReadOnlyList<SidebarLibraryEntry>? tree, string sourceId)
    {
        if (TryTopLevelAnchor(tree, sourceId, out _)) return true;
        if (tree is null || string.IsNullOrEmpty(sourceId)) return false;
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (!e.IsFolder || e.FolderId.Length == 0) continue;
            if (RootlistTreeMoves.Check(tree, sourceId, e.Id, RootlistDropPlacement.Inside) == SidebarDropRefusal.None)
                return true;
        }
        return false;
    }

    /// <summary>The folders <paramref name="sourceId"/> may be filed into, in tree order, APPENDED to
    /// <paramref name="into"/> (caller-owned, so the picker's list costs no allocation per keystroke).
    ///
    /// <para>Legality is <see cref="RootlistTreeMoves.Check"/> — the SAME table the drop cue draws its refusals from —
    /// so the picker cannot offer a destination a drag would refuse: the source's own subtree (a folder into itself or
    /// its descendant) and the folder it is already the last child of both drop out, without this file re-deriving a
    /// second copy of the cycle rule.</para></summary>
    static void FolderChoices(IReadOnlyList<SidebarLibraryEntry>? tree, string sourceId,
                              List<RootlistFolderChoice> into)
    {
        if (tree is null || tree.Count == 0 || string.IsNullOrEmpty(sourceId)) return;
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (!e.IsFolder || e.FolderId.Length == 0) continue;
            if (RootlistTreeMoves.Check(tree, sourceId, e.Id, RootlistDropPlacement.Inside) != SidebarDropRefusal.None)
                continue;
            into.Add(new RootlistFolderChoice(e.FolderId, e.Name, e.Depth));
        }
    }

    /// <summary>The anchor behind the picker's pinned <b>Top level</b> row: the LAST top-level entry, landed After.
    /// <para>False when there is no such move to make — the tree is empty, or the source is itself the last top-level
    /// entry (landing after itself is where it already is). The row is then absent rather than dead.</para></summary>
    public static bool TryTopLevelAnchor(IReadOnlyList<SidebarLibraryEntry>? tree, string sourceId,
                                         out RootlistItemRef anchor)
    {
        anchor = new RootlistItemRef("", false);
        if (tree is null || tree.Count == 0 || string.IsNullOrEmpty(sourceId)) return false;
        int last = -1;
        for (int i = 0; i < tree.Count; i++)
            if (tree[i].Depth == 0) last = i;
        if (last < 0) return false;
        var entry = tree[last];
        if (RootlistTreeMoves.Check(tree, sourceId, entry.Id, RootlistDropPlacement.After) != SidebarDropRefusal.None)
            return false;
        anchor = RefOf(in entry);
        return anchor.Key.Length > 0;
    }

    /// <summary>The entry with this id, or false. One linear scan — the tree is the projection's own flattened list, and
    /// a menu open is not a hot path.</summary>
    public static bool TryEntry(IReadOnlyList<SidebarLibraryEntry>? tree, string entryId, out SidebarLibraryEntry entry)
    {
        entry = default;
        if (tree is null || string.IsNullOrEmpty(entryId)) return false;
        for (int i = 0; i < tree.Count; i++)
        {
            if (!string.Equals(tree[i].Id, entryId, StringComparison.Ordinal)) continue;
            entry = tree[i];
            return true;
        }
        return false;
    }

    /// <summary>The FOLDER entry with this group id, or false — the "which folder am I in" lookup behind Move out of.</summary>
    public static bool TryFolder(IReadOnlyList<SidebarLibraryEntry>? tree, string folderId, out SidebarLibraryEntry folder)
    {
        folder = default;
        if (tree is null || string.IsNullOrEmpty(folderId)) return false;
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (!e.IsFolder || !string.Equals(e.FolderId, folderId, StringComparison.Ordinal)) continue;
            folder = e;
            return true;
        }
        return false;
    }

    /// <summary>THE rootlist reference one entry moves AS — a folder by its group id, a playlist by its uri. The ONE
    /// owner for the entry form: <c>RootlistUndoAnchors</c> resolves through this rather than re-deriving it.
    /// (<c>WaveeResourceDrop.RootRef</c> is the same rule over a drag PAYLOAD — a different input shape — and
    /// <c>SidebarPane</c> still carries a private copy for its slot mapper.)</summary>
    public static RootlistItemRef RefOf(in SidebarLibraryEntry entry)
        => entry.IsFolder
            ? new RootlistItemRef(entry.FolderId, IsFolder: true)
            : new RootlistItemRef(entry.Uri, IsFolder: false);
}
