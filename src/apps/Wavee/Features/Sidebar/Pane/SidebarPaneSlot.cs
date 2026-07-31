using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Sidebar;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// R3.0.1 — ONE bound slot of the pane's plan list, and the whole 13-kind row vocabulary behind it (extracted from
// `Modes/Curated/CuratedRowSlot.cs`; Classic, Curated and Library V3 all render through this).
//
// WHY A COMPONENT PER SLOT. `ItemsView.CreateBound` builds each slot ONCE and recycles it by writing the slot's index
// signal. A Component whose Render reads `scope.Index.Value` therefore re-renders exactly on a recycle — which is how a
// heterogeneous plan (13 row kinds) rides a signals-first list at all. It additionally subscribes the document /
// projection / pin / folder / MODE epochs (`SidebarPane.SubscribeEpoch`) and the live ROUTE, so a customizer edit, a
// library refresh, a pin mutation, a section toggle or a navigation re-skins only the realized window — never the list.
//
// SELECTION lives inside the item container, matching WinUI NavigationViewItem: the shared row ramp plus a permanently
// mounted 3×16 SelectionIndicator. The previous and next realized rows run the exact paired NavigationView timeline.
//
// NO HOOKS BELOW Render's prologue. Every builder is a plain method: hook order must be identical across every recycle.
// (The animated chevrons ARE hook-owning components, but they are CHILDREN — `Embed.Comp` — and each row kind has its own
// recycle pool via `ContentType`, so a header slot never rebinds into a shape without a chevron.)
sealed class SidebarPaneSlot : Component
{
    readonly SidebarPane _o;
    readonly RowScope _scope;

    // Cached live-state probes handed to the chevron components. They capture only THIS slot (never a section id), because
    // a child component's ctor args FREEZE at mount and the slot recycles onto a different section every scroll.
    Func<bool>? _headerOpen;
    Func<bool>? _folderOpen;
    Func<SidebarPillState>? _pillProbe;
    SidebarPillState _pillState;

    public SidebarPaneSlot(SidebarPane owner, RowScope scope) { _o = owner; _scope = scope; }

    public override Element Render()
    {
        int index = _scope.Index.Value;        // a recycle writes this → exactly this row re-renders
        _ = _o.SubscribeEpoch();              // document + projection + pin + folder + mode epochs
        string sel = _o.SelectedRoute;        // pane selection is the live ROUTE, never a list index

        var plan = _o.Plan;
        var rows = plan.Rows;
        // A slot can transiently address an index the newest plan no longer has (the count signal lands in a layout
        // effect, one hop after the plan). Render nothing rather than clamp onto a foreign row.
        if ((uint)index >= (uint)rows.Count) return Blank;
        var row = rows[index];
        var section = _o.SectionOf(row.SectionId);
        if (section is null) return Blank;

        Element content = row.Kind switch
        {
            SidebarRowKind.SectionHeader => Header(section, row, index, rows),
            SidebarRowKind.HeaderLabel => Banded(SidebarSectionHeader.Label(SidebarPaneText.TitleOf(section)), index, rows),
            // Only a Divider SECTION produces this row. Ordinary section joins are whitespace, never implicit rules.
            SidebarRowKind.Divider => SidebarSectionHeader.ExplicitDivider(),
            SidebarRowKind.IconRow or SidebarRowKind.EntityRow or SidebarRowKind.Placeholder
                => ItemOrEntity(section, row, sel, index),
            SidebarRowKind.FolderHeader => FolderRow(section, row, sel, index),
            SidebarRowKind.GridStrip => GridStrip(section, row, sel),
            SidebarRowKind.Empty => EmptyRow(section),
            SidebarRowKind.Skeleton => SidebarSkeletons.Row(index, section.Opts.Density, section.Opts.Subtitles,
                heightOverride: SidebarPaneMetrics.RowHeight(section),
                artOverride: SidebarPaneMetrics.ArtSize(section)),
            SidebarRowKind.CreateAction => CreateRow(section),
            SidebarRowKind.EntityCard => Card(section, row, sel),
            SidebarRowKind.PromptRow => Prompt(section),
            _ => Blank,
        };

        // In-place reorder (§C5.1). The Reorderable owns the row's drag source, its keyboard lift and its position
        // track — which is exactly why the row itself carries no Drag payload and no Animate when wrapped.
        if (ReorderBand(row, index) is { } pair)
            content = pair.Ro.Item(index - pair.Start, content, key: row.Key, transition: SidebarPane.Placement);
        return content;
    }

    static Element Blank => new BoxEl { Height = 0f, Shrink = 0f };

    (Reorderable Ro, int Start)? ReorderBand(in SidebarRow row, int index)
    {
        if (row.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.IconRow or SidebarRowKind.Placeholder
                             or SidebarRowKind.FolderHeader)) return null;
        if (!_o.TryBandOf(index, out var band)) return null;
        return (_o.ReorderFor(band.SectionId), band.Start);
    }

    // ── section chrome ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 28-DIP section header inside its R3.1.3 rhythm band. Click toggles the section through the MODE's own
    /// collapse owner (<c>SidebarPane.ToggleSection</c> → the undoable <c>SetSectionCollapsed</c> command for Curated, the
    /// persisted per-section flag for Classic). The FIRST header additionally hosts the quick sidebar-layout menu (§C6.4,
    /// Classic's placement); an EDITABLE <c>EntityList</c> header carries its sort/view trigger and — with
    /// <c>Display.InlineControls</c> — its filter chip strip directly under the header band (§C1.8.6).</summary>
    Element Header(SidebarSectionSpec section, in SidebarRow row, int index, IReadOnlyList<SidebarRow> rows)
    {
        bool open = !section.Collapsed;
        string id = section.Id;
        // The trailing slot can carry BOTH affordances: an editable EntityList always gets its sort/view trigger, and the
        // pane's first header always keeps the layout menu (that entry point must never be crowded out — §C6.4).
        var affordances = new List<Element>(2);
        bool editable = !_o.Config.ReadOnly;
        if (editable && section.Kind == SidebarSectionKind.EntityList)
            affordances.Add(SidebarPaneInlineControls.SortTrigger(_o, section));
        // NO create "+" in a PlaylistTree header: the planner already emits a full CreateAction ROW at the end of that
        // section (R3.0.2 names it as part of Classic's document too), and two create affordances for one command is
        // exactly the per-mode duplication this unification removes.
        if (string.Equals(_o.MenuHostSectionId, row.SectionId, StringComparison.Ordinal) && _o.Prefs is { } prefs)
            affordances.Add(SidebarLayoutMenu.Button(prefs, _o.Navigate, box: 24f));
        Element? action = affordances.Count switch
        {
            0 => null,
            1 => affordances[0],
            _ => new BoxEl { Direction = 0, Gap = 2f, AlignItems = FlexAlign.Center, Children = [.. affordances] },
        };

        // Explicit locals rather than ternaries against null (the note SidebarSectionHeader carries): a lambda leaning on
        // target typing inside a conditional is exactly the shape that breaks when the target moves into an initializer.
        Action<bool>? toggle = null;
        Element? chevron = null;
        if (_o.Config.SetSectionCollapsed is not null)
        {
            toggle = o => _o.ToggleSection(id, !o);
            // R3.1.7a — ONE glyph whose Rotation animates, never a glyph swap.
            chevron = SidebarChevron.Section(_headerOpen ??= HeaderOpenLive);
        }

        Element header = SidebarSectionHeader.Header(SidebarPaneText.TitleOf(section), open, toggle, action,
            chevron: chevron);

        if (!editable || section.Kind != SidebarSectionKind.EntityList || !section.Opts.InlineControls
            || section.Collapsed)
            return Banded(header, index, rows);
        return Banded(new BoxEl
        {
            Direction = 1, Gap = 4f,
            Children = [header, SidebarPaneInlineControls.Chips(_o, section)],
        }, index, rows);
    }

    /// <summary>R3.1.3 — SECTION RHYTHM. The planner emits contiguous rows (zero gap), so five sections used to read as one
    /// undifferentiated column. A header band therefore carries 8 DIP of air above it and 2 DIP below (matching
    /// <c>SidebarSectionHeader.Section</c>'s own internal gap).
    ///
    /// <para>It is PADDING on a wrapper, not a Margin on the header: padding is unambiguously part of the slot's MEASURED
    /// height, so <c>RepeatLayout.VariableList</c>'s extent stays honest and scroll anchoring cannot drift.</para>
    ///
    /// <para>The air is suppressed for the pane's FIRST row (nothing to separate from) and directly after a DIVIDER or a
    /// bare HEADING row, both of which already supply the gap — doubling it there would out-space Classic's landed
    /// rule + lead-in.</para></summary>
    static Element Banded(Element header, int index, IReadOnlyList<SidebarRow> rows)
    {
        float top = SidebarPaneMetrics.SectionGap;
        if (index <= 0) top = 0f;
        else
        {
            var prev = rows[index - 1].Kind;
            if (prev is SidebarRowKind.Divider or SidebarRowKind.HeaderLabel) top = 0f;
        }
        return new BoxEl
        {
            Direction = 1, Shrink = 0f,
            Padding = new Edges4(0f, top, 0f, SidebarPaneMetrics.HeaderBodyGap),
            Children = [header],
        };
    }

    /// <summary>The header chevron's live open state. Captures only the SLOT, so it survives every recycle: it re-reads the
    /// plan row at the slot's current index and the section behind it, and its <c>SubscribeEpoch</c> read is what
    /// re-renders the chevron on a toggle.</summary>
    bool HeaderOpenLive()
    {
        int index = _scope.Index.Value;
        _ = _o.SubscribeEpoch();
        var rows = _o.Plan.Rows;
        if ((uint)index >= (uint)rows.Count) return true;
        var section = _o.SectionOf(rows[index].SectionId);
        return section is null || _o.DisclosureOpen(section.Id, folder: false, fallback: !section.Collapsed);
    }

    /// <summary>The folder chevron's live expansion state — same recycle-safe shape as <see cref="HeaderOpenLive"/>.</summary>
    bool FolderOpenLive()
    {
        int index = _scope.Index.Value;
        _ = _o.SubscribeEpoch();
        var rows = _o.Plan.Rows;
        if ((uint)index >= (uint)rows.Count) return true;
        var row = rows[index];
        var entries = _o.Plan.Entries;
        if (row.EntryIndex < 0 || row.EntryIndex >= entries.Count) return true;
        string folderId = entries[row.EntryIndex].FolderId;
        bool open = _o.Prefs?.IsFolderExpanded(folderId) ?? true;
        return _o.DisclosureOpen(folderId, folder: true, fallback: open);
    }

    // ── item / entity rows ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The three row kinds that can carry EITHER a projected entry or a hand-placed item spec, resolved in ONE
    /// place so the planner's join rules are honoured exactly once:
    /// <list type="bullet">
    /// <item>an ACTION item is an action row whatever kind the planner chose for it (the planner has no action row kind,
    /// so a bound shortcut arrives as <c>Placeholder</c> — it must never render as a missing entity);</item>
    /// <item><c>EntryIndex &gt;= 0</c> ⇒ the projected entry (a Pinned override item, when present, still supplies its
    /// alias/icon);</item>
    /// <item>a TRACK item is a play row built from its own spec (a hand-placed track is not in the library projection);</item>
    /// <item>a ROUTE item is a glyph row;</item>
    /// <item>anything else is the dimmed missing-entity retention row — never dropped (§C1.4).</item>
    /// </list></summary>
    Element ItemOrEntity(SidebarSectionSpec section, in SidebarRow row, string sel, int index)
    {
        var item = SidebarPaneText.ItemOf(section, row.Key);
        if (item is { Target: SidebarItemTarget.Action }) return ActionRow(section, item, index);

        var entries = _o.Plan.Entries;
        if (row.EntryIndex >= 0 && row.EntryIndex < entries.Count)
        {
            var entry = entries[row.EntryIndex];
            // A Pinned OVERRIDE may be keyed by uri instead of pin id (both are legal — the planner's own hidden-override
            // test accepts either), so a projected row looks for its side-table entry both ways.
            item ??= SidebarPaneText.ItemOf(section, entry.Uri);
            return EntryRow(section, in entry, row, sel, item, index);
        }
        if (item is { Target: SidebarItemTarget.Track }) return TrackItemRow(section, item, index);
        if (item is { Target: SidebarItemTarget.Route }) return RouteRow(section, item, sel, index);
        return MissingRow(section, item, row);
    }

    /// <summary>A projected entry: a playlist / album / artist / show / folder / track / app route the projection knows.</summary>
    Element EntryRow(SidebarSectionSpec section, in SidebarLibraryEntry entry, in SidebarRow row, string sel,
                     SidebarItemSpec? item, int index)
    {
        bool named = entry.Name.Length > 0;
        // "A row whose entry Name.Length == 0 renders dimmed from the uri" — the entity exists but has not resolved a
        // display name yet, which is honest as a dimmed row and never as a blank one.
        string label = item?.LabelOverride is { Length: > 0 } alias ? alias
            : named ? entry.Name
            : SidebarPaneText.ShortUri(entry.Uri);
        bool track = entry.IsTrack;
        string? route = entry.RouteKey;
        bool selected = route is { Length: > 0 } && string.Equals(route, sel, StringComparison.Ordinal);
        bool reordering = _o.TryBandOf(index, out _);
        var (playing, animated) = PlayState(entry.Uri);
        float height = SidebarPaneMetrics.RowHeight(section);
        bool treeNode = section.Kind == SidebarSectionKind.PlaylistTree;
        int treeDepth = treeNode ? entry.Depth : 0;
        int baseDepth = treeNode ? Math.Max(0, row.Depth - treeDepth) : row.Depth;

        var snapshot = entry;   // an `in` parameter cannot be captured — copy the record struct for the lazy closures
        // Explicit locals, not inline conditionals: a lambda has no natural type, so a ternary against null would lean on
        // target typing inside an object initializer (the note SidebarSectionHeader already carries).
        Action? click = null;
        if (track) click = () => _o.Play(snapshot.Uri, asTrack: true);
        else if (route is { Length: > 0 } r) click = () => _o.Navigate(r, snapshot.Name);

        Func<ContextMenuModel?>? menu = null;
        if (_o.Acts is { } acts) menu = () => Menus.SidebarEntry(acts, in snapshot);

        // A Reorderable installs its OWN drag source and position track; a second one is a documented stomp. A TRACK is
        // never a pin drag source at all (locked decision 4 is enforced by the KIND, not per surface).
        bool rootlistItem = section.Kind == SidebarSectionKind.PlaylistTree && !reordering;
        WaveeResourceDragPayload? resource = rootlistItem
            ? WaveeResourceDragPayload.FromEntry(snapshot, _o.Acts?.Svc, rootlistItem: true)
            : null;
        WaveeResourceDragPayload? drag = null;
        if (!reordering && !track)
            drag = resource ?? WaveeResourceDragPayload.FromEntry(snapshot, _o.Acts?.Svc);
        DropTargetSpec? drop = (snapshot.Kind == SidebarEntryKind.Playlist && snapshot.CanEdit) || resource is not null
            ? _o.ResourceDropSpec(row.SectionId, PinSlot(row.SectionId, index),
                snapshot.Kind == SidebarEntryKind.Playlist && snapshot.CanEdit ? snapshot.Uri : null,
                snapshot.Name, resource, index)
            : PinSpec(section, row.SectionId, index);

        var spec = new SidebarRowSpec
        {
            Key = row.Key,
            Label = label,
            Subtitle = section.Opts.Subtitles ? SidebarPaneText.SubtitleOf(in snapshot) : null,
            Selected = selected,
            Enabled = named || track,
            Depth = baseDepth,
            TreeNode = treeNode,
            TreeDepth = treeDepth,
            TreeContinuationMask = treeNode ? TreeMaskOf(row.SectionId, index, treeDepth) : (byte)0,
            Density = section.Opts.Density,
            // UNIFORM per section: a Reorderable's slot pitch and the section's own rhythm both assume one height.
            Height = height,
            ArtSize = SidebarPaneMetrics.ArtSize(section),
            Leading = LeadingArt(section, in snapshot, item),
            Glyph = section.Opts.Artwork ? null : SidebarPaneText.Glyph(item, SidebarPaneText.EntryGlyph(snapshot.Kind)),
            Trailing = TrailingBadge(section, in snapshot),
            Playing = playing,
            PlayingAnimated = animated,
            Track = track,
            Overflow = _o.Acts is not null && _o.MenuOverlay is not null,
            OnClick = click,
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
            Drag = drag,
            DropActive = drop is null ? null : () => _o.IsResourceDropActive(index),
            DropTarget = drop,
        };
        Element built = SidebarEntityRow.Create(spec);
        if (track) built = SidebarEntityRow.WithPlayTrackHint(built);
        // Tree connectors own their depth lanes; the selection indicator stays in the row's base gutter.
        return Indicator(built, selected, baseDepth, height, route, row.SectionId);
    }

    /// <summary>A rootlist FOLDER: the entity-row geometry with a disclosure chevron and the folder mark. A folder never
    /// navigates to a ROUTE (it has none) — by default it toggles its expansion, which lives in <c>SidebarPreferences</c>
    /// (shared by every design and both pane mounts). A mode may reroute that gesture through
    /// <c>SidebarPaneConfig.ActivateFolder</c> (V3's narrow drill-in level); the click and the menu verb take the same path,
    /// so they cannot disagree.</summary>
    Element FolderRow(SidebarSectionSpec section, in SidebarRow row, string sel, int index)
    {
        var entries = _o.Plan.Entries;
        if (row.EntryIndex < 0 || row.EntryIndex >= entries.Count) return Blank;
        var entry = entries[row.EntryIndex];
        string folderId = entry.FolderId;
        bool expanded = _o.Prefs?.IsFolderExpanded(folderId) ?? true;
        float height = SidebarPaneMetrics.RowHeight(section);
        int treeDepth = entry.Depth;
        int baseDepth = Math.Max(0, row.Depth - treeDepth);
        var snapshot = entry;
        _ = sel;   // a folder is never the selected ROUTE (it has none)

        // Explicit locals, never a ternary against null: a lambda has no natural type in that position.
        Action activate = () => _o.ActivateFolder(folderId, snapshot.Name, index);

        Func<ContextMenuModel?>? menu = null;
        if (_o.Acts is { } acts)
            menu = () => Menus.SidebarEntry(acts, in snapshot, activate, expanded);

        bool reordering = _o.TryBandOf(index, out _);
        bool rootlistItem = section.Kind == SidebarSectionKind.PlaylistTree && !reordering;
        var resource = WaveeResourceDragPayload.FromEntry(snapshot, _o.Acts?.Svc, rootlistItem);
        DropTargetSpec? drop = rootlistItem
            ? _o.ResourceDropSpec(row.SectionId, -1, null, null, resource, index)
            : PinSpec(section, row.SectionId, index);

        var spec = new SidebarRowSpec
        {
            Key = row.Key,
            Label = entry.Name.Length > 0 ? entry.Name : SidebarPaneText.ShortUri(entry.Id),
            Subtitle = section.Opts.Subtitles ? Strings.Sidebar.V3.ItemCount(entry.ChildCount) : null,
            Depth = baseDepth,
            TreeNode = true,
            TreeDepth = treeDepth,
            TreeContinuationMask = TreeMaskOf(row.SectionId, index, treeDepth),
            Density = section.Opts.Density,
            Height = height,
            ArtSize = SidebarPaneMetrics.ArtSize(section),
            Leading = section.Opts.Artwork
                ? SidebarCover.Folder(SidebarPaneMetrics.ArtSize(section), expanded)
                : null,
            Glyph = section.Opts.Artwork ? null : (expanded ? Icons.FolderOpen : Icons.Folder),
            // R3.1.7a — the folder disclosure rotates too (ChevronRight → 90°), so headers and folders share one motion.
            LeadingChevron = SidebarChevron.Disclosure(_folderOpen ??= FolderOpenLive),
            Trailing = section.Kind == SidebarSectionKind.PlaylistTree && section.Opts.CountBadges
                ? SidebarCounts.Badge(entry.ChildCount)
                : null,
            OnClick = activate,
            Overflow = _o.Acts is not null && _o.MenuOverlay is not null,
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
            Drag = reordering ? null : resource,
            DropActive = drop is null ? null : () => _o.IsResourceDropActive(index),
            DropTarget = drop,
        };
        return SidebarEntityRow.Create(spec);
    }

    /// <summary>Which connector columns continue below a realized tree row. The plan is preorder, so the first later
    /// tree entry whose source depth is at or above a level decides that level: equal means a sibling continues; lower
    /// means the branch ended. This stays renderer-side because it is visual chrome, not document/query semantics.</summary>
    byte TreeMaskOf(string sectionId, int index, int depth)
    {
        int levels = Math.Clamp(depth, 0, 4);
        if (levels == 0) return 0;
        int unresolved = (1 << levels) - 1;
        int mask = 0;
        var plan = _o.Plan;
        var rows = plan.Rows;
        var entries = plan.Entries;
        for (int i = index + 1; i < rows.Count && unresolved != 0; i++)
        {
            var next = rows[i];
            if (!string.Equals(next.SectionId, sectionId, StringComparison.Ordinal)) break;
            if (next.Kind is not (SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader)) break;
            if ((uint)next.EntryIndex >= (uint)entries.Count) break;
            int nextDepth = entries[next.EntryIndex].Depth;
            for (int level = 1; level <= levels; level++)
            {
                int bit = 1 << (level - 1);
                if ((unresolved & bit) == 0 || nextDepth > level) continue;
                if (nextDepth == level) mask |= bit;
                unresolved &= ~bit;
            }
        }
        return (byte)mask;
    }

    /// <summary>A hand-picked app route (CollectionShortcuts / StaticLinks). Label + glyph come from <c>ShellNav</c> so a
    /// pinned "Liked Songs" follows the UI culture instead of freezing whatever culture authored it; an unknown route key
    /// in a hand-edited document degrades to the library mark rather than crashing (§C1.8.7).</summary>
    Element RouteRow(SidebarSectionSpec section, SidebarItemSpec item, string sel, int index)
    {
        var dest = ShellNav.Dest(item.Key);
        bool selected = string.Equals(item.Key, sel, StringComparison.Ordinal);
        float height = SidebarPaneMetrics.RowHeight(section);
        string key = item.Key;
        string title = item.LabelOverride is { Length: > 0 } alias ? alias : dest.Title;

        // A route row is a pin drag source when it is a durable application destination and a Reorderable is not
        // already the drag owner. SidebarPinId centrally excludes editor/tooling routes.
        WaveeResourceDragPayload? drag = null;
        if (!_o.TryBandOf(index, out _) && SidebarPinId.FromRoute(key) is not null)
        {
            var destination = SidebarDestination.FromRoute(key, null, title);
            if (destination is { } d) drag = WaveeResourceDragPayload.FromDestination(d, _o.Acts?.Svc);
        }
        var drop = PinSpec(section, section.Id, index);

        var spec = new SidebarRowSpec
        {
            Key = key,
            Label = title,
            Selected = selected,
            Depth = 0,
            Density = section.Opts.Density,
            Height = height,
            Glyph = SidebarIcons.For(item, dest.Glyph),
            Trailing = CountBadge(section, key),
            OnClick = () => _o.Navigate(key, null),
            Overflow = _o.Acts is not null && _o.MenuOverlay is not null,
            MenuOverlay = _o.MenuOverlay,
            Menu = RouteMenu(item),
            Drag = drag,
            DropActive = drop is null ? null : () => _o.IsResourceDropActive(index),
            DropTarget = drop,
        };
        return Indicator(SidebarEntityRow.Create(spec), selected, 0, height, key, section.Id);
    }

    Func<ContextMenuModel?>? RouteMenu(SidebarItemSpec item)
    {
        if (_o.Acts is not { } acts) return null;
        string key = item.Key;
        var dest = ShellNav.Dest(key);
        var entry = SidebarLibraryEntry.ForRoute(key, dest.Title);
        return () => Menus.SidebarEntry(acts, in entry);
    }

    /// <summary>A hand-placed TRACK (§C1.8.3): click PLAYS, it never navigates, and a hover/focus play glyph replaces the
    /// chevron affordance so the behaviour is legible before the click. Tracks are never pin sources.</summary>
    Element TrackItemRow(SidebarSectionSpec section, SidebarItemSpec item, int index)
    {
        _ = index;
        string uri = item.Key;
        var (playing, animated) = PlayState(uri);
        float height = SidebarPaneMetrics.RowHeight(section);
        string label = item.LabelOverride is { Length: > 0 } alias ? alias
            : item.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(uri);

        var spec = new SidebarRowSpec
        {
            Key = uri,
            Label = label,
            Density = section.Opts.Density,
            Height = height,
            ArtSize = SidebarPaneMetrics.ArtSize(section),
            Leading = section.Opts.Artwork
                ? SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, uri, SidebarPaneMetrics.ArtSize(section))
                : null,
            Glyph = section.Opts.Artwork ? null : SidebarIcons.For(item, Icons.MusicNote),
            Playing = playing,
            PlayingAnimated = animated,
            Track = true,
            OnClick = () => _o.Play(uri, asTrack: true),
        };
        return SidebarEntityRow.WithPlayTrackHint(SidebarEntityRow.Create(spec));
    }

    /// <summary>An ACTION shortcut (<c>SidebarItemTarget.Action</c>). Resolved ONLY through the extension registry — no new
    /// UI looks up <c>AppActions.All</c> (the M3 forward-compat guardrail). An unavailable target renders
    /// VISIBLE-BUT-DISABLED with the reason as its tooltip; it never vanishes, because a vanishing row makes the user's
    /// own sidebar look broken.</summary>
    Element ActionRow(SidebarSectionSpec section, SidebarItemSpec item, int index)
    {
        _ = index;
        var binding = item.Action;
        var reg = _o.Registry;
        var acts = _o.Acts;
        float height = SidebarPaneMetrics.RowHeight(section);

        string label = item.LabelOverride ?? "";
        var icon = default(IconRef);
        bool enabled = false;
        // The default reason covers the two host-shaped cases (no registry / no action bag yet): the row still renders,
        // disabled, saying so — never silently absent.
        string? reason = Loc.Get(SidebarPaneLoc.ExtensionNotNow);
        Action? click = null;

        if (binding is null)
        {
            // An Action item with no binding at all: a half-authored or hand-edited document.
            reason = Loc.Get(SidebarPaneLoc.ExtensionMissing);
        }
        else
        {
            var bound = binding;   // non-nullable local: Execute takes it by `in`
            if (reg is not null && reg.TryGetAction(bound, out var descriptor))
            {
                label = Pick(label, descriptor.Label());
                icon = descriptor.Icon();
            }
            if (reg is not null && acts is { } services)
            {
                var resolution = reg.Resolve(services, bound);
                enabled = resolution.Available;
                reason = resolution.ReasonLocKey is { } key ? Loc.Get(key) : null;
                var registry = reg;
                if (enabled) click = () => registry.Execute(services, in bound);
            }
        }
        if (label.Length == 0) label = Loc.Get(SidebarPaneLoc.ExtensionManage);

        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = label,
            Selected = false,
            Enabled = enabled,
            Density = section.Opts.Density,
            Height = height,
            Leading = SidebarPaneIcon.Leading(item.IconOverride, icon, enabled),
            Gap = 12f,             // keep the bare-glyph rhythm even though the leading slot is authored
            OnClick = click,
        };
        Element row = SidebarEntityRow.Create(spec);
        return reason is { Length: > 0 } r ? ToolTip.Wrap(row, r) : row;
    }

    static string Pick(string authored, string fallback) => authored.Length > 0 ? authored : fallback;

    /// <summary>The missing-entity retention row (§C1.4): dimmed, from the item's last-known title/art, with a menu that
    /// offers exactly the one honest verb — remove it. NEVER auto-removed. Under a LOCKED document there is no verb at all
    /// (the row is not the user's to edit here), so it degrades to the tooltip alone.</summary>
    Element MissingRow(SidebarSectionSpec section, SidebarItemSpec? item, in SidebarRow row)
    {
        float height = SidebarPaneMetrics.RowHeight(section);
        string label = item?.LabelOverride is { Length: > 0 } alias ? alias
            : item?.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(row.Key);
        string sectionId = section.Id;

        // The context menu of a missing entity is EXACTLY one verb: remove it. Everything else would be a promise about an
        // entity we cannot reach (§C5.1's "context menu limited to Remove").
        Func<ContextMenuModel?>? menu = null;
        if (!_o.Config.ReadOnly && item is { Id.Length: > 0 } present)
        {
            string itemId = present.Id;
            menu = () => new ContextMenuModel(
            [
                new MenuFlyoutItem(Loc.Get(SidebarPaneLoc.RemoveItem), ActionIcons.Resolve(ActionIcons.Remove), true,
                    () => _o.Dispatch(new RemoveItem(sectionId, itemId))),
            ]);
        }

        var spec = new SidebarRowSpec
        {
            Key = row.Key,
            Label = label,
            Subtitle = section.Opts.Subtitles ? Loc.Get(SidebarPaneLoc.MissingEntity) : null,
            Enabled = false,
            Depth = row.Depth,
            Density = section.Opts.Density,
            Height = height,
            ArtSize = SidebarPaneMetrics.ArtSize(section),
            Leading = section.Opts.Artwork
                ? SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, row.Key, SidebarPaneMetrics.ArtSize(section))
                : null,
            Glyph = section.Opts.Artwork ? null : SidebarPaneText.Glyph(item, Icons.MusicNote),
            MenuOverlay = _o.MenuOverlay,
            Menu = menu,
        };
        return ToolTip.Wrap(SidebarEntityRow.Create(spec), Loc.Get(SidebarPaneLoc.MissingEntity));
    }

    // ── the hero card (§C1.8.2) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>EntityEmbed</c> spotlight card: cover left (circular for artists), title + subtitle, and — when
    /// <c>Display.PlayButton</c> — a circular play button revealed on hover/focus that plays the entity AS A CONTEXT
    /// through the same player verb the detail-page CTA uses. Clicking anywhere else navigates. A missing entity is still
    /// a card: dimmed, from the item's cached title/art, with the play affordance hidden.</summary>
    Element Card(SidebarSectionSpec section, in SidebarRow row, string sel)
    {
        var item = SidebarPaneText.ItemOf(section, row.Key);
        var entries = _o.Plan.Entries;
        bool resolved = row.EntryIndex >= 0 && row.EntryIndex < entries.Count;
        var entry = resolved ? entries[row.EntryIndex] : default;
        float height = SidebarPaneMetrics.CardHeight(section);
        float cover = SidebarPaneMetrics.CardCover(section);

        string title = item?.LabelOverride is { Length: > 0 } alias ? alias
            : resolved && entry.Name.Length > 0 ? entry.Name
            : item?.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(row.Key);
        string? subtitle = resolved ? SidebarPaneText.SubtitleOf(in entry) : Loc.Get(SidebarPaneLoc.MissingEntity);
        bool circular = resolved
            ? entry.Circular || entry.Kind == SidebarEntryKind.Artist
            : item?.EntityKind == SidebarEntityKind.Artist;

        Element art = resolved
            ? SidebarCover.Art(entry.Cover, entry.MosaicTiles, entry.Id, cover, circular)
            : SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, row.Key, cover, circular);

        string uri = resolved ? entry.Uri : "";
        bool track = resolved && entry.IsTrack;
        string? route = resolved ? entry.RouteKey : SidebarPinId.FromUri(uri);
        bool selected = route is { Length: > 0 } && string.Equals(route, sel, StringComparison.Ordinal);
        var (playing, animated) = PlayState(uri);
        bool canPlay = resolved && section.Opts.PlayButton && uri.Length > 0 && entry.IsPlayable;
        var snapshot = entry;

        Action? activate = null;
        if (track) activate = () => _o.Play(uri, asTrack: true);
        else if (route is { Length: > 0 } r) activate = () => _o.Navigate(r, title);

        var lines = new List<Element>(2)
        {
            new TextEl(title)
            {
                Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (subtitle is { Length: > 0 } s)
            lines.Add(new TextEl(s)
            {
                Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        var text = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 2f, Children = [.. lines],
        };

        var children = new List<Element>(3) { art, text };
        if (canPlay) children.Add(PlayButton(uri, playing && animated, section));

        var card = new BoxEl
        {
            Key = row.Key,
            Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = 12f,
            Padding = new Edges4(8f, 0f, 8f, 0f),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = selected ? Tok.FillSubtleSecondary : Tok.FillCardSecondary,
            HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            BorderWidth = selected ? 2f : 1f,
            BorderColor = selected ? Tok.AccentDefault : Tok.StrokeCardDefault,
            // R3.1.2: the pane owns the horizontal inset, so the card contributes only its vertical breathing room. Its
            // own 8-DIP left/right margin used to DOUBLE the pane's, which is why a card never lined up with the rows.
            Margin = new Edges4(0f, 2f, 0f, 2f),
            Opacity = resolved ? 1f : 0.55f,
            IsEnabled = resolved,
            Cursor = activate is null ? CursorId.Arrow : CursorId.Hand,
            OnClick = resolved ? activate : null,
            Children = [.. children],
        };
        if (_o.Acts is { } acts && _o.MenuOverlay is { } svc && resolved)
            card = card.WithContextMenu(svc, () => Menus.SidebarEntry(acts, in snapshot));
        return card;
    }

    /// <summary>The card's hover/focus-revealed circular play button. The glyph over the accent plate is the on-accent
    /// token (never a literal white — the accent plate is light in the light theme).</summary>
    Element PlayButton(string uri, bool playingNow, SidebarSectionSpec section)
    {
        float box = section.Opts.Density == SidebarDensity.Compact ? 28f : 32f;
        return new BoxEl
        {
            Opacity = playingNow ? 1f : 0f, HoverOpacity = 1f, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Width = box, Height = box, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = Radii.Circle(box),
                    Fill = Tok.AccentDefault,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand,
                    OnClick = () => _o.Play(uri, asTrack: false),
                    Children = [Icon(playingNow ? Icons.Pause : Icons.Play, 14f, Tok.TextOnAccentPrimary)],
                }.Interactive(Interaction.Subtle),
            ],
        };
    }

    // ── grid strips (§C5.1) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One row of a Grid-presentation section: <c>ItemCount</c> tiles from <c>Entries[EntryIndex..]</c>.
    ///
    /// <para>R3.1.2 — the cell edge derives from the REAL pane inset (<c>SidebarPaneMetrics.PaneInsetH</c>) rather than an
    /// assumed <c>Spacing.L</c> that nothing applied, and the strip adds no horizontal padding of its own.</para>
    ///
    /// <para>THE COLUMN COUNT IS THE PLANNER'S, NOT THIS METHOD'S. A width-reactive override here (there used to be a
    /// "≤240 DIP falls back to two columns") silently disagreed with <c>SidebarRowPlanner.EmitProjected</c>, which had
    /// already sliced the entries into GridColumns-wide strips: a 4-item section at 3 columns plans strips of 3 and 1, and
    /// re-wrapping the first at 2 columns made one strip two lines tall and the next one — a ragged grid whose ROW RHYTHM
    /// changed with the pane width. It was also unreachable in the useful case: the pane clamps at
    /// <c>ShellResponsiveLayout.NavPaneMinW</c> = 240, where even 4 columns still yields 50-DIP cells. One owner of the
    /// column count, so the planned extent and the rendered strip agree at every width.</para></summary>
    Element GridStrip(SidebarSectionSpec section, in SidebarRow row, string sel)
    {
        float pane = _o.ExpandedWidth.Value;       // subscribe → the CELL EDGE re-flows with the pane (the count does not)
        int cols = Math.Clamp(section.Opts.GridColumns, 2, 4);
        const float gap = Spacing.S;
        float edge = MathF.Min(SidebarPaneMetrics.GridCellMax,
            MathF.Max(SidebarCover.S40, (pane - SidebarPaneMetrics.PaneInsetH - gap * (cols - 1)) / cols));

        var entries = _o.Plan.Entries;
        int start = row.EntryIndex;
        int count = row.ItemCount;
        if (start < 0 || count <= 0 || start >= entries.Count) return Blank;
        if (start + count > entries.Count) count = entries.Count - start;

        var cells = new Element[count];
        for (int i = 0; i < count; i++) cells[i] = GridCell(section, entries[start + i], edge, sel);
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = gap,
            Padding = new Edges4(0f, 0f, 0f, Spacing.S),
            Children = cells,
        };
    }

    Element GridCell(SidebarSectionSpec section, SidebarLibraryEntry entry, float edge, string sel)
    {
        bool circular = entry.Circular || entry.Kind == SidebarEntryKind.Artist;
        string? route = entry.RouteKey;
        bool selected = route is { Length: > 0 } && string.Equals(route, sel, StringComparison.Ordinal);
        string label = entry.Name.Length > 0 ? entry.Name : SidebarPaneText.ShortUri(entry.Uri);
        var snapshot = entry;
        float artEdge = MathF.Max(SidebarCover.S40, edge - Spacing.S);

        var kids = new List<Element>(3)
        {
            SidebarCover.Art(entry.Cover, entry.MosaicTiles, entry.Id, artEdge, circular),
            new TextEl(label)
            {
                Size = 12f, Weight = (ushort)(selected ? 600 : 400), Color = selected ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (section.Opts.Subtitles && SidebarPaneText.SubtitleOf(in entry) is { Length: > 0 } sub)
            kids.Add(new TextEl(sub) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        Action? click = null;
        if (entry.IsTrack) click = () => _o.Play(snapshot.Uri, asTrack: true);
        else if (route is { Length: > 0 } r) click = () => _o.Navigate(r, snapshot.Name);

        var cell = new BoxEl
        {
            Key = entry.Id,
            Direction = 1, Width = edge, Shrink = 0f, Gap = Spacing.XS,
            Padding = Edges4.All(Spacing.XS),
            Corners = Radii.CardAll,
            Shadow = Elevation.Card,
            Cursor = click is null ? CursorId.Arrow : CursorId.Hand,
            OnClick = click,
            Children = [.. kids],
        }.Interactive(Interaction.Card);
        cell = cell with
        {
            BorderColor = selected ? Tok.AccentDefault : Tok.StrokeCardDefault,
            BorderWidth = selected ? 2f : 1f,
        };
        if (_o.Acts is { } acts && _o.MenuOverlay is { } svc)
            cell = cell.WithContextMenu(svc, () => Menus.SidebarEntry(acts, in snapshot));
        return cell;
    }

    // ── degraded + affordance rows ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A section that resolved to ZERO rows.
    ///
    /// <para>R3.1.5 BUG FIX — the PINNED branch runs FIRST. It used to sit after the <c>HideBody</c> test, so an authored
    /// (or future default) <c>HideBody</c> on a Pinned section silently deleted the drop zone: the one surface that teaches
    /// drop-to-pin, gone, with no way to get it back. Pinned's empty state IS the drop target and is therefore
    /// unconditional (which is also why a track drag — never carrying the sidebar entity kind — cannot pin anything
    /// anywhere).</para>
    ///
    /// <para>R3.1.6 — a dynamic feed's empty state is a QUIET 32-DIP 11f tertiary hint with per-kind copy, not a 40-DIP
    /// 12f line borrowing <c>nav.history.empty.nothingHere</c>. HideBody deliberately keeps the section header: it remains
    /// a discoverable/configurable authored section, while its live body consumes no empty billboard.</para></summary>
    Element EmptyRow(SidebarSectionSpec section)
    {
        if (section.Kind == SidebarSectionKind.Pinned)
            return Embed.Comp(() => new SidebarPinDropZone(_o.AcceptPinDrop));

        var behavior = SidebarSectionKinds.EmptyBehaviorFor(section.Kind, section.Opts.EmptyBehavior);
        if (behavior == SidebarEmptyBehavior.HideBody)
            return Blank;

        string text = section.Kind switch
        {
            SidebarSectionKind.EntityList or SidebarSectionKind.PlaylistTree => LibraryEmptyText(),
            _ => SidebarPaneText.EmptyText(section.Kind),
        };

        if (behavior == SidebarEmptyBehavior.ActionCard)
            return SidebarEntityRow.Create(new SidebarRowSpec
            {
                Key = section.Id + ":empty",
                Label = text,
                Enabled = false,
                Density = section.Opts.Density,
                Height = SidebarPaneMetrics.RowHeight(section),
                Glyph = section.Kind == SidebarSectionKind.Concerts ? Icons.Calendar : Icons.Grid,
            });

        return new BoxEl
        {
            Height = SidebarPaneMetrics.EmptyHintHeight, AlignItems = FlexAlign.Center,
            Padding = SidebarPaneMetrics.RowInset,
            Children =
            [
                new TextEl(text)
                {
                    Size = 11f, Color = Tok.TextTertiary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    string LibraryEmptyText()
    {
        string query = _o.SearchText;
        return query.Length > 0
            ? Loc.Format(SidebarPaneLoc.SearchEmpty, ("query", query))
            : Loc.Get(SidebarPaneLoc.LibraryEmpty);
    }

    /// <summary>The ACTIONABLE degraded state (§C1.8.5 / the extension failure matrix). Concerts with no location points at
    /// the hub where a location is set; an unresolvable contribution offers "Manage extension", which in M2 means the
    /// customizer — the one surface where the section can be reconfigured or removed.</summary>
    Element Prompt(SidebarSectionSpec section)
    {
        bool concerts = section.Kind == SidebarSectionKind.Concerts;
        string label = concerts ? Loc.Get(SidebarPaneLoc.ConcertsPrompt) : Loc.Get(SidebarPaneLoc.ExtensionManage);
        string? reason = concerts ? null : ExtensionReason(section.Id);
        // Explicit locals, never a ternary against null: a method group has no natural type in that position.
        Action? click = null;
        if (concerts) click = () => _o.Navigate(ConcertRoutes.Hub, null);
        else if (_o.Config.OnCustomize is not null) click = _o.OpenCustomizer;

        Element[] lines = reason is { Length: > 0 }
            ?
            [
                new TextEl(label)
                {
                    Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
                new TextEl(reason)
                {
                    Size = 11f, Color = Tok.TextTertiary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ]
            :
            [
                new TextEl(label)
                {
                    Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ];

        return new BoxEl
        {
            Key = section.Id,
            Direction = 0,
            Height = reason is { Length: > 0 } ? 56f : 48f,
            AlignItems = FlexAlign.Center,
            Gap = Spacing.S,
            // R3.1.2: vertical only — the pane owns the horizontal inset (see the card).
            Margin = new Edges4(0f, Spacing.XXS, 0f, Spacing.XXS),
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.CardAll,
            Shadow = Elevation.Card,
            Role = click is null ? AutomationRole.None : AutomationRole.Button,
            Cursor = click is null ? CursorId.Arrow : CursorId.Hand,
            Focusable = click is not null,
            OnClick = click,
            Children =
            [
                SidebarCover.Glyph(concerts ? Icons.Calendar : Icons.Settings, SidebarCover.S28),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    Gap = Spacing.XXS, Children = lines,
                },
                Icon(Icons.ChevronRight, 10f, Tok.TextTertiary),
            ],
        }.Interactive(Interaction.Card);
    }

    /// <summary>Why a contributed section cannot render, as prose. The binder's availability verdict is the authority —
    /// this never inspects an extension id (the M3 guardrail).</summary>
    string? ExtensionReason(string sectionId)
    {
        var availability = _o.Prefs?.Binder?.AvailabilityOf(sectionId) ?? SidebarContributionAvailability.Missing;
        return availability switch
        {
            SidebarContributionAvailability.Live or SidebarContributionAvailability.Cached => null,
            SidebarContributionAvailability.Missing => Loc.Get(SidebarPaneLoc.ExtensionMissing),
            _ => Loc.Get(SidebarPaneLoc.ExtensionNotNow),
        };
    }

    /// <summary>The PlaylistTree section's own create affordance, as a full row at the end of the section (§C5.1). It is
    /// authored chrome, not data, so it survives a pending source. Folder creation is deliberately absent — Spotify folder
    /// CRUD is deferred (locked decision 9) and a disabled "New folder" would promise a command we do not have.</summary>
    Element CreateRow(SidebarSectionSpec section)
    {
        Action? click = null;
        if (_o.Config.OnCreatePlaylist is not null) click = _o.CreatePlaylist;
        return SidebarEntityRow.Create(new SidebarRowSpec
        {
            Key = section.Id + ":create",
            Label = Loc.Get(Strings.Sidebar.CreatePlaylistTooltip),
            Density = section.Opts.Density,
            Height = SidebarPaneMetrics.RowHeight(section),
            Glyph = Icons.Add,
            OnClick = click,
        });
    }

    // ── shared row plumbing ──────────────────────────────────────────────────────────────────────────────────────────

    Element? LeadingArt(SidebarSectionSpec section, in SidebarLibraryEntry entry, SidebarItemSpec? item)
    {
        if (!section.Opts.Artwork) return null;
        float size = SidebarPaneMetrics.ArtSize(section);
        // An authored icon override beats the artwork slot: it is the user's explicit choice for this row.
        if (item?.IconOverride is { Length: > 0 } name)
            return SidebarCover.Glyph(SidebarIcons.Glyph(name, Icons.MusicNote), size,
                entry.Circular || entry.Kind == SidebarEntryKind.Artist);
        // A CONCERT is projected as a route whose stamp is the event date; §C1.8.5 wants that date, not a glyph tile.
        if (section.Kind == SidebarSectionKind.Concerts && entry.SortStamp > 0)
            return SidebarPaneText.DateBlock(entry.SortStamp, size);
        return SidebarCover.ForEntry(in entry, size);
    }

    /// <summary>The trailing slot for a FEED row: §C1.8.4's age badge on a new release ("3d"). Everything else has none —
    /// the count badge belongs to route rows and the now-playing equalizer is the row primitive's own slot.</summary>
    static Element? TrailingBadge(SidebarSectionSpec section, in SidebarLibraryEntry entry)
    {
        if (section.Kind == SidebarSectionKind.PlaylistTree && section.Opts.CountBadges &&
            entry.Kind == SidebarEntryKind.Playlist)
            return SidebarCounts.Badge(entry.ChildCount);
        if (section.Kind == SidebarSectionKind.NewReleases &&
            SidebarPaneText.AgeBadge(entry.SortStamp) is { Length: > 0 } age)
            return new TextEl(age) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1 };
        return null;
    }

    /// <summary>R3.1.4 — the library-shortcut count, through the ONE quiet badge (<see cref="SidebarCounts"/>). Classic's
    /// accent <c>InfoBadge.Count</c> pill is gone: this is the only count renderer left in the sidebar.</summary>
    Element? CountBadge(SidebarSectionSpec section, string routeKey)
    {
        if (!section.Opts.CountBadges) return null;
        int index = routeKey switch { "albums" => 0, "artists" => 1, "liked" => 2, "podcasts" => 3, _ => -1 };
        if (index < 0) return null;
        var store = _o.Store;
        if (store is null) return null;
        var stats = store.Stats;
        if ((LoadState)stats.State.Value != LoadState.Ready || stats.Value.Value is not { } s)
            return SidebarCounts.Badge(null);
        int count = index switch { 0 => s.Albums, 1 => s.Artists, 2 => s.LikedSongs, _ => s.Podcasts };
        return SidebarCounts.Badge(count);
    }

    /// <summary>Attach the item-owned SelectionIndicator. Keeping it shape-stable is load-bearing for recycling: a slot
    /// never inherits an animated transform from the route it represented one window earlier.</summary>
    Element Indicator(Element row, bool selected, int depth, float height, string? route, string sectionId)
    {
        if (!float.IsFinite(height) || height <= 0f) height = SidebarRowMetrics.ClassicHeight;
        _pillState = new SidebarPillState(
            Route: route,
            Selected: selected,
            Indent: SidebarRowMetrics.IndentFor(depth),
            Top: MathF.Max(0f, (height - SidebarSelectionPill.PillH) * 0.5f));
        return ZStack(row, Embed.Comp(() => new SidebarSelectionPill(_o, _pillProbe ??= PillState)));
    }

    SidebarPillState PillState()
    {
        _ = _scope.Index.Value;
        _ = _o.SubscribeEpoch();
        string selectedRoute = _o.SelectedRoute;
        var state = _pillState;
        bool selected = state.Route is { Length: > 0 }
            && string.Equals(state.Route, selectedRoute, StringComparison.Ordinal);
        return state with
        {
            Selected = selected,
        };
    }

    /// <summary>A PINNED row is also a drop target, so dragging a playlist/album/artist onto the pinned band pins it AT
    /// that position. Deliberately scoped to the band (never the whole pane): a pane-wide accept would pin an entity the
    /// moment a row was nudged, which is the accidental-pin hazard Classic avoided by scoping too.</summary>
    DropTargetSpec? PinSpec(SidebarSectionSpec section, string sectionId, int index)
    {
        if (section.Kind != SidebarSectionKind.Pinned) return null;
        int slot = PinSlot(sectionId, index);
        return slot >= 0 ? _o.ResourceDropSpec(sectionId, slot, null, null, rootPlanIndex: index) : null;
    }

    int PinSlot(string sectionId, int index)
        => _o.TryBandOf(index, out var band) && string.Equals(band.SectionId, sectionId, StringComparison.Ordinal)
            ? index - band.Start : -1;

    /// <summary>Is THIS row's entity the one playing? Gated on the coarse <c>HasActiveContext</c> bool first, so an idle
    /// app never joins the hot <c>Identity</c> fan-out (the MediaCard rule).</summary>
    (bool Playing, bool Animated) PlayState(string uri)
    {
        var bridge = _o.Playback;
        if (bridge is null || uri.Length == 0) return (false, false);
        if (!bridge.HasActiveContext.Value) return (false, false);
        var identity = bridge.Identity.Value;
        bool now = NowPlayingOverlay.Matches(uri, identity.ContextUri, identity.Track);
        return (now, now && bridge.IsPlaying.Value);
    }
}
