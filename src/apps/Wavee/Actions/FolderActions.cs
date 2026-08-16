using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Input;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// Rootlist FOLDER commands — create, rename, move out, delete — behind the P3 seam
/// (<c>CreateFolderAsync</c> / <c>RenameFolderAsync</c> / <c>DeleteFolderAsync</c> / the existing
/// <c>MoveRootlistItemAsync</c>). Registered ONCE, in the shared row menus, so Classic, Library V3 and Wavee Curated
/// all get the same verbs from the same builder.
///
/// <para>Folder CRUD was locked out of the UI (the old "locked decision 9") for as long as the wire for it did not
/// exist. It does now, so the lock is <b>lifted</b>: the rootlist is written through the resource-drop seam and through
/// these five commands, and through nothing else.</para>
///
/// <para><b>Identity survives every edit.</b> Expansion state (<c>SidebarPreferences</c>) and pins
/// (<c>SidebarPinId.ForFolder</c>) are keyed by the client-minted groupId, which a rename never changes — so a renamed
/// folder keeps its open/closed state and its pin with no migration at all. A pin to a folder that was DELETED keeps
/// rendering, visible-but-disabled with a reason (the sidebar's standing "never auto-remove a user's row" rule).</para>
///
/// <para>Rootlist structural ops are online-only by seam contract (they are index ops against a live revision), so an
/// offline call fails fast and lands here as the mapped <c>Offline</c> sentence rather than queueing into a tree that
/// has moved on. No activity-log entry is recorded: the log's kinds are playlist-scoped and none of them describes a
/// folder, and filing a folder delete under <c>PlaylistDelete</c> would claim a playlist was destroyed.</para>
/// </summary>
static class FolderActions
{
    /// <summary>"New playlist in this folder" — the one create path, placed inside <paramref name="folderId"/>.</summary>
    public static void NewPlaylistIn(ActionServices s, string folderId)
    {
        if (folderId.Length == 0) return;
        PlaylistCreateFlow.Create(s, new RootlistPlacement(folderId), navigate: true);
    }

    /// <summary>"New folder" / "New folder inside {folder}" — names it up front (an unnamed folder in a list of folders
    /// is indistinguishable), then creates it EXPANDED so the thing the user just made is open and can be filled.</summary>
    public static void NewFolder(ActionServices s, string? parentFolderId)
    {
        if (s.Library is not { } lib || s.Overlay is not { } overlay) return;
        ContainerActions.RenameDialog(overlay, Loc.Get(Strings.Sidebar.CreateFolder),
            Loc.Get(Strings.Sidebar.NewFolder),
            name => RunCreate(s, lib, name, parentFolderId),
            primaryText: Loc.Get(Strings.Sidebar.CreateFolder));
    }

    static void RunCreate(ActionServices s, LibraryBridge lib, string name, string? parentFolderId)
    {
        _ = Run();
        async Task Run()
        {
            string groupId;
            try { groupId = await lib.CreateFolderAsync(name, new RootlistPlacement(parentFolderId)).ConfigureAwait(false); }
            catch (Exception ex) { Post(s, () => PlaylistEditErrors.Toast(ex)); return; }
            Post(s, () =>
            {
                // Created OPEN: the folder exists to hold things, and one that opens closed reads as a failed create.
                s.Sidebar?.SetFolderExpanded(groupId, true);
                Announce(Strings.Sidebar.FolderCreated, name);
            });
        }
    }

    /// <summary>Rename a folder in place. The groupId is unchanged, so expansion and pins ride through untouched.</summary>
    public static void Rename(ActionServices s, string groupId, string current)
    {
        if (s.Library is not { } lib || s.Overlay is not { } overlay || groupId.Length == 0) return;
        ContainerActions.RenameDialog(overlay, Loc.Get(Strings.Sidebar.RenameFolder), current, next =>
        {
            if (string.Equals(next, current, StringComparison.Ordinal)) return;
            _ = RunRename(s, lib, groupId, next);
        });
    }

    static async Task RunRename(ActionServices s, LibraryBridge lib, string groupId, string name)
    {
        try { await lib.RenameFolderAsync(groupId, name).ConfigureAwait(false); }
        catch (Exception ex) { Post(s, () => PlaylistEditErrors.Toast(ex, PlaylistEditVerb.Rename)); return; }
        Post(s, () => Announce(Strings.Sidebar.FolderRenamed, name));
    }

    // ── rootlist ORGANISATION verbs (the non-mouse half of D12) ─────────────────────────────────────────────────────
    //
    // Move up / Move down / Move to folder… / Move out of {parent}. Four commands, ONE seam: each resolves its
    // (target, placement) from the pure tree rules in `RootlistTreeNav` and then hands the move to
    // `WaveeResourceDrop.MoveRootlist`, which is the very same call a DROP makes. That is why they await, why a failure
    // arrives as the Reorder-verb sentence instead of raw exception text, why a success announces and toasts
    // "Moved to {name}", and why each one offers Undo back to the exact pre-move anchor — none of it is written twice.
    //
    // Every verb addresses its row by ENTRY ID (the projection's stable `SidebarLibraryEntry.Id`: `pl:<uri>` /
    // `folder:<groupId>`) and re-reads the tree at INVOKE time. A menu opened before a desktop rootlist change must not
    // commit an index computed against the tree it was built from.

    /// <summary>"Move up" — land this row immediately BEFORE its previous sibling. Absent from the menu (and a silent
    /// no-op from the keyboard) at the top of its sibling run, which is the only position where there is nothing to
    /// swap with.</summary>
    public static void MoveUp(ActionServices s, string entryId) => Move(s, entryId, -1);

    /// <summary>"Move down" — land this row immediately AFTER its next sibling. A next sibling that is a FOLDER is
    /// stepped OVER, not entered: <c>RootlistOps.TryBuildMove</c> resolves After against the folder's whole span, which
    /// is what keeps a Move down from filing the item inside the folder it was trying to pass.</summary>
    public static void MoveDown(ActionServices s, string entryId) => Move(s, entryId, 1);

    /// <summary>The Alt+↑/↓ accelerator's entry point: one signed step through the sibling run
    /// (<c>-1</c> up, <c>+1</c> down), so the row spec carries ONE delegate instead of two.</summary>
    public static void Move(ActionServices s, string entryId, int delta)
    {
        if (s.Library is null) return;
        var tree = Tree(s);
        if (!RootlistTreeNav.TryEntry(tree, entryId, out var entry)) return;
        var run = RootlistTreeNav.Siblings(tree, entryId);
        RootlistItemRef target;
        RootlistDropPlacement placement;
        if (delta < 0)
        {
            if (!run.CanMoveUp) return;                       // the run's first item — nothing above it to land before
            target = run.Previous;
            placement = RootlistDropPlacement.Before;
        }
        else
        {
            if (!run.CanMoveDown) return;                     // the run's last item
            target = run.Next;
            placement = RootlistDropPlacement.After;
        }
        // The destination is the folder the row is ALREADY in — a within-run move never changes its parent, so the
        // confirmation names that folder (or Your Library at top level).
        Commit(s, in entry, target, placement, entry.ParentFolderName);
    }

    /// <summary>"Move to folder…" — the picker. Opening it is all this does; the commit is the picker's, through the
    /// same <see cref="Commit"/> chokepoint.</summary>
    public static void MoveTo(ActionServices s, string entryId) => RootlistFolderPicker.Open(s, entryId);

    /// <summary>"Move out of {parent}" — lift one playlist or folder one level up, landing it immediately after the
    /// folder it came out of. The same rootlist MOVE a drag performs, offered as a command so a drag is never the only
    /// way to un-nest something (and so a keyboard-only user has the verb at all).
    /// <para>It used to be fire-and-forget with an error-only toast: a successful un-nest said nothing at all and could
    /// not be taken back (D13). It now rides the shared confirm — announce + "Moved to {name}" + Undo — exactly like a
    /// drop.</para></summary>
    public static void MoveOut(ActionServices s, string entryId)
    {
        if (s.Library is null) return;
        var tree = Tree(s);
        if (!RootlistTreeNav.TryEntry(tree, entryId, out var entry)) return;
        if (entry.ParentFolderId.Length == 0) return;                      // already at top level
        // The DESTINATION is the folder that CONTAINS the parent — one level up is where the row lands, so that is what
        // the confirmation must name (Your Library when the parent is itself top-level).
        string destination = RootlistTreeNav.TryFolder(tree, entry.ParentFolderId, out var parent)
            ? parent.ParentFolderName : "";
        Commit(s, in entry, new RootlistItemRef(entry.ParentFolderId, IsFolder: true),
               RootlistDropPlacement.After, destination);
    }

    /// <summary>THE ONE COMMIT for every organisation verb. Captures the pre-move Undo anchor (once the rootlist has
    /// moved, where the item used to be is unknowable), then hands the move to the drop seam — which awaits it, maps a
    /// failure by VERB (<c>PlaylistEditVerb.Reorder</c>, never raw exception text), announces the result and shows the
    /// "Moved to {name}" toast with Undo.</summary>
    internal static void Commit(ActionServices s, in SidebarLibraryEntry entry, RootlistItemRef target,
                                RootlistDropPlacement placement, string destinationName)
    {
        if (target.Key.Length == 0) return;
        var payload = WaveeResourceDragPayload.FromEntry(entry, s.Svc, rootlistItem: true);
        RootlistItemRef? undoAnchor = null;
        var undoPlacement = RootlistDropPlacement.After;
        if (RootlistUndoAnchors.TryResolve(Tree(s), entry.Id, out var anchor, out var anchorPlacement))
        {
            undoAnchor = anchor;
            undoPlacement = anchorPlacement;
        }
        WaveeResourceDrop.MoveRootlist(s, payload, target, placement, destinationName, undoAnchor, undoPlacement);
    }

    /// <summary>The DEPTH-FIRST FLATTENED rootlist tree the projection is currently publishing — the same list
    /// <c>SidebarPane</c> decides drop legality and undo anchors against, so a menu verb and a drop can never disagree
    /// about sibling order. Null in a host with no sidebar binder, which makes every verb above a no-op rather than a
    /// move against a tree nobody has.</summary>
    static IReadOnlyList<SidebarLibraryEntry>? Tree(ActionServices s) => s.Sidebar?.Binder?.CurrentInput.PlaylistTree;

    /// <summary>Delete a folder, behind the existing confirm dialog. The confirmation says the thing the user is most
    /// likely to be afraid of, in the sentence itself: the playlists inside move up a level, they are not deleted.</summary>
    public static void Delete(ActionServices s, string groupId, string name, int childCount)
    {
        if (s.Library is not { } lib || s.Overlay is not { } overlay || groupId.Length == 0) return;
        SettingsShared.Confirm(overlay,
            Loc.Get(Strings.Sidebar.DeleteFolder),
            Strings.Sidebar.DeleteFolderConfirm(name, childCount),
            Loc.Get(Strings.Sidebar.DeleteFolder),
            () => _ = RunDelete(s, lib, groupId, name));
    }

    static async Task RunDelete(ActionServices s, LibraryBridge lib, string groupId, string name)
    {
        try { await lib.DeleteFolderAsync(groupId).ConfigureAwait(false); }
        catch (Exception ex) { Post(s, () => PlaylistEditErrors.Toast(ex)); return; }
        Post(s, () => Announce(Strings.Sidebar.FolderDeleted, name));
    }

    // A folder edit is a SILENT structural change to a list the user may not be looking at, so it is announced for the
    // same reason a save is (LibraryBridge.AnnounceSaved): one call at the one chokepoint, never per surface.
    static void Announce(string key, string name)
    {
        if (!Announcer.IsAvailable) return;
        string what = Loc.Get(key);
        Announcer.SayThrottled(name.Length == 0 ? what : what + ": " + name);
    }

    static void Post(ActionServices s, Action a)
    {
        var post = s.Post;
        if (post is not null) post(a); else a();
    }
}
