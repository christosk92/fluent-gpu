using System;
using FluentGpu.Dsl;
using Wavee.Core.Sidebar;

namespace Wavee;

// R3.0.1 — THE ONLY MODE SEAM.
//
// The three sidebar modes shipped as three separate pane CONTAINERS (Classic's hand-built body, V3's Index/List/Row/Rail
// stack, Curated's planner + slots) that shared only leaf primitives. Paddings, badges, section rhythm and motion
// therefore tripled and drifted, which is exactly what the user's screenshot review found. Full unification means there
// is now ONE renderer (`SidebarPane` + `SidebarPaneSlot` + `SidebarPaneRail`) and every mode is a DOCUMENT plus this
// config. Drift becomes impossible by construction: a mode cannot reach around the config, because the renderer takes
// nothing else.
//
// EVERY MEMBER IS A DELEGATE OR A FLAG, never a snapshot. The config is built ONCE per mode mount (a
// `UseMemo(..., DepKey.Empty)`) and frozen into the pane's ctor — the component-props-freeze contract — so a value member
// would pin the first frame's state forever. `Document`/`Input`/`ModeEpoch` are therefore providers the pane invokes
// inside ITS render, which is also what makes the signals they read subscribe the pane.
sealed record SidebarPaneConfig
{
    /// <summary>Which mode this is. Used for the log field and for the pane's scroll/telemetry identity only — the
    /// renderer never branches on it (that would be the drift this seam exists to prevent).</summary>
    public required SidebarDesign Design { get; init; }

    /// <summary>Scroll-restoration key prefix. The pane appends <c>".drawer"</c> for the narrow-drawer mount, so the
    /// docked pane and the drawer never fight over one saved offset.</summary>
    public required string ScrollKeyPrefix { get; init; }

    /// <summary>The live document. Classic returns the LOCKED built-in
    /// (<see cref="SidebarBuiltInDocuments.Classic"/>) rebuilt from its three persisted section flags; Curated returns
    /// <c>prefs.Layout</c>; V3 returns its synthesized ephemeral document. Invoked inside the pane's render, so any
    /// signal it reads subscribes the pane.</summary>
    public required Func<SidebarCustomLayout> Document { get; init; }

    /// <summary>Optional transform of the binder's planner input (the mode's own filter/sort/search state folded in).
    /// Null ⇒ the binder's input verbatim. The pane still applies its own search-head override on top.</summary>
    public Func<SidebarProjectionInput, SidebarProjectionInput>? Input { get; init; }

    /// <summary>Any MODE-OWNED state that changes what the plan or a realized row draws, folded into one int. It is read
    /// both in the plan's <c>DepKey</c> and in the per-row epoch, so a mode state change re-plans AND re-skins the
    /// realized window. Classic folds its three section flags; V3 folds filter/qualifier/sort/desc/view/search/drill.
    /// Read the signals with <c>.Value</c> here — that read IS the subscription.</summary>
    public Func<int>? ModeEpoch { get; init; }

    /// <summary>Toggle a section's collapse state. Curated dispatches the undoable <c>SetSectionCollapsed</c> command;
    /// Classic writes its own persisted per-section flag (its document is NOT the Curated document and must never be
    /// mutated by a header click). Null ⇒ non-collapsible headers.</summary>
    public Action<string, bool>? SetSectionCollapsed { get; init; }

    /// <summary>The document is not the user's to edit here: suppresses the inline EntityList controls (chips +
    /// sort/view, which write section specs), the missing-entity "Remove" verb, and the empty-pane customize CTA.
    /// Classic is read-only; Curated is not; V3 is (its chrome owns its state).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Render the pane's own library-only search box above the scroll surface (only when the document actually
    /// contains a visible EntityList — searching a pane with no library list would filter nothing).</summary>
    public bool SearchHead { get; init; }

    /// <summary>Arbitrary MODE CHROME above the scroll surface (V3's header band, toolbar, chips, breadcrumb). Rendered
    /// before <see cref="SearchHead"/>. Invoked in the pane's render.</summary>
    public Func<Element?>? Head { get; init; }

    /// <summary>Hang the quick sidebar-layout menu off the pane's FIRST section header (§C6.4 — the design switch must be
    /// reachable from the pane itself). V3 embeds those rows in its own overflow menu instead.</summary>
    public bool ShowLayoutMenu { get; init; } = true;

    /// <summary>Put the quick layout menu at the bottom of the 56-DIP rail too, so a collapsed pane can still switch.</summary>
    public bool RailLayoutMenu { get; init; } = true;

    /// <summary>An extra affordance appended to the rail after the plan's tiles (Classic's create-playlist button).</summary>
    public Func<Element?>? RailFooter { get; init; }

    /// <summary>What ACTIVATING a folder disclosure row does. Null ⇒ toggle the SHARED folder-expansion state
    /// (<c>SidebarPreferences.ToggleFolder</c>), which is what a pane that discloses folders inline wants — Classic and
    /// Curated both leave it null.
    ///
    /// <para>A mode whose pane can instead NAVIGATE into a folder (a narrow pane's session-only drill level, Library V3's
    /// Revision-2 amendment) supplies this: it receives the folder's id and its display name, so the handler can push a
    /// breadcrumb without re-reading the projection, and it decides — per width, per view — whether that gesture discloses,
    /// navigates or switches view. The renderer itself therefore never learns what a drill level is.</para>
    ///
    /// <para>It replaces the row's click AND the expand/collapse verb in its context menu, so the two can never disagree.</para></summary>
    public Action<string, string>? ActivateFolder { get; init; }

    /// <summary>Whether a folder activation currently expands/collapses descendants inside this pane. Null means true
    /// (Classic and Custom). A mode with alternate navigation, such as LibraryV3's narrow drill stack, supplies a live
    /// probe so the shared renderer animates only genuine inline structural changes.</summary>
    public Func<bool>? DisclosesFoldersInline { get; init; }

    /// <summary>Which section kinds reorder IN PLACE. Default: Pinned / StaticLinks / CustomGroup (§C5.1). PlaylistTree
    /// uses generic resource-drop destinations unless a mode explicitly opts into a local view-order overlay.</summary>
    public Func<SidebarSectionKind, bool>? IsReorderableSection { get; init; }

    /// <summary>Commit a same-list reorder. Null ⇒ <see cref="SidebarPaneReorderCommit.Default"/> (Pinned through the
    /// SHARED pin store, every other reorderable kind through the undoable <c>MoveItem</c> command). V3's local custom
    /// order supplies its own.</summary>
    public Action<SidebarPaneReorder>? CommitReorder { get; init; }

    /// <summary>Open the customizer (the empty-pane CTA and the unresolvable-contribution prompt row). Null ⇒ those
    /// surfaces render without their action rather than with a dead one.</summary>
    public Action? OnCustomize { get; init; }

    /// <summary>Create a playlist (the PlaylistTree section's create row + the rail footer). Null ⇒ the create row is
    /// still planned but inert, which is the honest shape for a mount with no library bridge.</summary>
    public Action? OnCreatePlaylist { get; init; }
}

/// <summary>
/// One committed reorder inside a reorderable band, in BAND-SLOT space (0..<see cref="SlotCount"/>-1). The renderer knows
/// the geometry; only the mode knows where the order LIVES, so this carries everything a commit could need and nothing
/// about the widget: the section spec, the two slots, and a resolver from slot → the row's stable key (a pin id, an item
/// key, an entry id).
/// </summary>
readonly record struct SidebarPaneReorder(
    SidebarSectionSpec Section,
    int FromSlot,
    int ToSlot,
    int SlotCount,
    Func<int, string> KeyAt);

/// <summary>The built-in reorder commit — Classic and Curated both use it verbatim, which is why it is not duplicated in
/// either mode component.</summary>
static class SidebarPaneReorderCommit
{
    /// <summary>Pinned commits through the SHARED pin store (the order every design sees); every other reorderable kind
    /// goes through the undoable, autosaved <c>MoveItem</c> command.</summary>
    public static void Default(SidebarPreferences? prefs, in SidebarPaneReorder r)
    {
        if (prefs is null || r.FromSlot == r.ToSlot) return;

        if (r.Section.Kind == SidebarSectionKind.Pinned)
        {
            // Map through the pin IDS rather than trusting band positions: a Pinned section may carry hidden overrides,
            // which shift the visible band relative to the store.
            int pf = prefs.Pins.IndexOf(r.KeyAt(r.FromSlot));
            int pt = prefs.Pins.IndexOf(r.KeyAt(r.ToSlot));
            if (pf < 0 || pt < 0) { prefs.MovePin(r.FromSlot, r.ToSlot); return; }
            prefs.MovePin(pf, pt);
            return;
        }

        int itemFrom = ItemIndexAt(r.Section, r.FromSlot);
        int itemTo = ItemIndexAt(r.Section, r.ToSlot);
        if (itemFrom < 0 || itemTo < 0) return;
        prefs.Dispatch(new MoveItem(r.Section.Id, itemFrom, r.Section.Id, itemTo));
    }

    /// <summary>Band position → index in the section's ITEM list. The planner skips hidden items in order, so the n-th
    /// band row is the n-th VISIBLE item.</summary>
    static int ItemIndexAt(SidebarSectionSpec section, int slot)
    {
        var items = section.ItemList;
        int seen = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Hidden) continue;
            if (seen == slot) return i;
            seen++;
        }
        return items.Count == 0 ? -1 : items.Count - 1;
    }
}
