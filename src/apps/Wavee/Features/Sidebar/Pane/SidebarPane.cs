using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;   // Route
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// R3.0.1 — THE ONE SIDEBAR PANE RENDERER. Extracted from `Modes/CuratedSidebar.cs`; every mode (Classic, Library V3,
// Curated) is now a DOCUMENT + a `SidebarPaneConfig` over this single component, so paddings, badges, rhythm and motion
// cannot triple and drift again (which is exactly what the user's screenshot review found in the three-container build).
//
// THE ONE STRUCTURAL DECISION (§C1.7): the document is not rendered section-by-section. `SidebarRowPlanner` flattens
// (document × projection) into ONE `SidebarRow[]` — headers, dividers, rows, grid strips, cards, prompts, placeholders,
// empty/skeleton rows — and this component renders it through ONE `ItemsView.CreateBound` over a MEASURED variable-extent
// layout. That is what lets a 10k-entry PlaylistTree or EntityList virtualize end-to-end: a `Grow=1` list inside an outer
// ScrollView cannot. There are therefore NO nested scrollers and no `Flow.For` over the projection anywhere in the pane.
//
// HOW A FRAME FLOWS
//   1. This component subscribes the document + projection + pin + folder + search + MODE epochs and re-plans in a UseMemo
//      keyed on their fold (the planner is pure and reuses ONE caller-owned SidebarPlanBuffers per pane — the expanded
//      pane and the rail each own their own instance, because a plan ALIASES its buffers).
//   2. The plan is published to the bound row slots as a PLAIN FIELD (never a signal write from Render — the render-purity
//      rule). Each slot is its own Component (`SidebarPaneSlot`) that reads `Index.Value` (so a recycle re-renders exactly
//      that row) plus the same epochs (so a projection rebuild or a live customizer edit re-renders the realized window
//      without the list rebuilding).
//   3. The row count is the ONE thing the frozen-at-mount ItemsView cannot read from a field: it rides `CountSignal`,
//      written in a layout effect (a render-time signal write would be a backwards write, and the DEBUG ReuseGuard
//      explicitly rejects a changed frozen ItemCount).
//
// LIVE EDITS: every Curated mutation goes through `SidebarPreferences.Dispatch(command)` → reducer → undo pre-image →
// `LayoutVersion` bump → autosave. This pane subscribes `LayoutVersion`, so the customizer never talks to the renderer: it
// dispatches, and both the live pane and its own preview re-plan from the one document.
//
// SELECTION — ONE mechanism for every mode (R3.0.2). Rows recycle inside a virtualized list, so a node-handle map keyed by
// route is not stable and Classic's MEASURED overlay pill cannot work here. The pane therefore draws selection INSIDE the
// row: the shared 4-state ramp (`SidebarEntityRow`) plus a 3×16 accent indicator over the row's own selection gutter,
// matching WinUI's per-NavigationViewItem SelectionIndicator.
//
// That indicator MOVES rather than blinking. This component owns the only fact a per-row cue cannot know on its own —
// that the selection changed, and which way it travelled through the plan (`TrackSelection` → `SelEpoch`/`SelDirection`/
// `WasDeparting`) — and `SidebarSelectionPill` turns it into a spring: the arriving row's pill stretches in from the side
// the selection came from while the departing row's contracts out the other way. See that file for why a declared
// `Transition` could never have done this.
//
// MOTION (Revision 2's token table + R3.1.7): section collapse/expand chevrons rotate on `MotionTok.ControlFast`
// (`SidebarChevron`); the rows a collapse/expand adds or displaces ride the ItemsView's entrance/FLIP seed channel (see
// `Choreograph`); reordering is `MotionTok.ItemPlacement` through `Reorderable`; the design switch is SidebarHost's
// `MotionTok.ControlFast`.
sealed class SidebarPane : Component
{
    // ── frozen-at-mount wiring (the component-props contract) ────────────────────────────────────────────────────────
    /// <summary>The ONLY mode seam. Frozen at mount; every member is a delegate or a flag, so nothing here is stale.</summary>
    internal readonly SidebarPaneConfig Config;
    /// <summary>The pane's measured open width — read by the grid strip and the search head, both of which re-flow with it.</summary>
    internal readonly Signal<float> ExpandedWidth;
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;
    readonly Signal<bool> _compact;
    readonly bool _inDrawer;

    /// <summary>Row-plan storage for the EXPANDED pane. A plan aliases its buffers, so the rail below must never share it.</summary>
    readonly SidebarPlanBuffers _paneBuffers = new();
    /// <summary>Row-plan storage for the 56-DIP rail (§C5.2) — its own instance, for the same reason.</summary>
    readonly SidebarPlanBuffers _railBuffers = new();

    /// <summary>The pane's OWN library-only search text (§C5.1's pinned head). SESSION-ONLY and PANE-OWNED: it is
    /// deliberately not <c>SidebarPreferences.V3Search</c>, which is Mode B's mode-global state — two panes must not filter
    /// each other. It reaches the planner as an override on the binder's input (<c>input with { Search = … }</c>), which is
    /// legal because the planner — not the projection — applies the search filter to EntityList/PlaylistTree.</summary>
    readonly Signal<string> _search = new("");

    /// <summary>The reactive row count for the frozen-at-mount ItemsView. Written in a LAYOUT EFFECT, never in Render.</summary>
    readonly Signal<int> _rowCount = new(0);

    /// <summary>Bumped by any live reorder gesture AND by a collapse/expand choreography, so the ItemsView re-seeds its
    /// displacement / FLIP / fade tracks over the recycling window.</summary>
    readonly Signal<int> _dispVersion = new(0);

    /// <summary>One <c>Reorderable</c> per in-place-reorderable section id (§C5.1). Created lazily and kept for the
    /// component's life — a Reorderable holds gesture state and must not be rebuilt.</summary>
    readonly Dictionary<string, Reorderable> _reorder = new(StringComparer.Ordinal);

    /// <summary>The contiguous plan-row runs those sections own, rebuilt with every plan (see <see cref="SidebarPaneBand"/>).</summary>
    readonly List<SidebarPaneBand> _bands = new();

    /// <summary>sectionId → spec, rebuilt with every plan so a row resolves its section in O(1) instead of re-walking the
    /// document per row.</summary>
    readonly Dictionary<string, SidebarSectionSpec> _sections = new(StringComparer.Ordinal);

    // ── R3.1.7b — collapse/expand choreography seeds (the DetailTracks dictionaries-on-the-page precedent) ────────────
    /// <summary>New plan index → the FLIP "first" (the row's OLD visual offset relative to its new slot), so a surviving
    /// row GLIDES from where it was instead of cutting to its new Y.</summary>
    readonly Dictionary<int, (float dx, float dy)> _flip = new();
    /// <summary>New plan index → (opacity from, stagger delay ms) for a row a freshly-expanded section just added.</summary>
    readonly Dictionary<int, (float from, float delayMs)> _fade = new();
    Func<int, (float dx, float dy)?>? _flipFrom;
    Func<int, (float from, float delayMs)?>? _fadeFrom;
    string? _toggledSection;
    bool _toggledExpand;
    float _toggledExtent;

    /// <summary>Playlist-tree tiles the 56-DIP rail may draw before the rest of the document gets its turn.</summary>
    const int RailTreeTiles = 20;

    /// <summary>An expanded row eases in from a slight rise (the app's add vocabulary).</summary>
    const float EnterRise = 6f;
    /// <summary>16 ms per row, capped, so a long section reads as dealt top-down instead of blinking together.</summary>
    const float StaggerStepMs = 16f;
    const int StaggerCap = 8;

    // ── published to the bound row slots each render. PLAIN FIELDS by design: a bound slot is a frozen child, so it reads
    //    these at ITS render time; writing a signal here would be a render-time signal write (see the file header).
    // Seeded with a real EMPTY plan, never `default`: a default(SidebarRowPlan) carries null lists, and a bound slot that
    // renders one frame ahead of the first plan would NRE instead of drawing nothing.
    internal SidebarRowPlan Plan = new(Array.Empty<SidebarRow>(), Array.Empty<SidebarLibraryEntry>(), 0);
    internal SidebarCustomLayout Doc = SidebarCustomLayout.Empty;
    internal SidebarPreferences? Prefs;
    internal ActionServices? Acts;
    internal IOverlayService? MenuOverlay;
    internal WaveeExtensionRegistry? Registry;
    internal PlaybackBridge? Playback;
    internal LibraryStore? Store;
    /// <summary>The first section whose header row exists — it hosts the quick sidebar-layout menu button, which is
    /// Classic's placement (§C6.4: the switch must be reachable from the pane itself, never only from Settings). Null when
    /// the mode puts those rows in its own chrome instead (<c>Config.ShowLayoutMenu == false</c>).</summary>
    internal string? MenuHostSectionId;

    Func<int, (float dx, float dy)>? _displacement;
    bool _countSeeded;

    // ── selection travel (R3.0.2 follow-up) ──────────────────────────────────────────────────────────────────────────
    // The per-row indicator can only MOVE if it knows the selection MOVED and which way. These three plain fields are
    // that knowledge, recomputed once per real route change in Render (never a signal — the row indicators read them at
    // THEIR render time, exactly like `Plan`).
    string _selRoute = "";
    string _prevSelRoute = "";
    int _selEpoch;
    int _selDir;

    /// <summary>The variable-extent layout object. STATEFUL (a Fenwick estimate-then-correct table + scroll anchoring), so
    /// it is created ONCE per pane instance and never per render.</summary>
    readonly RepeatLayout _rowLayout = RepeatLayout.VariableList(estimatedExtent: 44f);

    /// <summary>A reorderable row's placement transition: Revision 2 assigns reordering <c>MotionTok.ItemPlacement</c>.
    /// A <c>Reorderable.Item</c>-wrapped row must not ALSO carry an authored offset hint (one position owner per node).</summary>
    static readonly LayoutTransition RowPlacement = new(
        TransitionChannels.Position, MotionTok.ItemPlacement.ToDynamics());

    public SidebarPane(SidebarPaneConfig config, Signal<Route> route, Action<string, string?> go, Signal<bool> compact,
                       Signal<float> expandedWidth, bool inDrawer = false)
    {
        Config = config; _route = route; _go = go; _compact = compact; ExpandedWidth = expandedWidth; _inDrawer = inDrawer;
    }

    public override Element Render()
    {
        Prefs = UseContext(SidebarPreferences.Slot);
        Acts = UseContext(ActionServices.Slot);
        MenuOverlay = UseContext(Overlay.Service);
        Playback = UseContext(PlaybackBridge.Slot);
        Store = UseContext(LibraryStore.Slot);
        // The registry is the ONE lookup path for a bound action row (never AppActions.All — the M3 forward-compat
        // guardrail). Context first, then the action bag, so a host that provides only one of them still resolves.
        Registry = UseContext(WaveeExtensionRegistry.Slot) ?? Acts?.Extensions;
        // Every mode's dynamic sections read the same warm cells Classic always did, so the first frame paints from cache.
        Store?.EnsureStats();
        Store?.EnsurePlaylists();

        // The mode's live document. Invoked HERE so the signals it reads (the Curated LayoutVersion, Classic's three
        // section flags, V3's filter/sort state) subscribe this pane.
        Doc = Config.Document();

        bool compact = !_inDrawer && _compact.Value;    // the drawer always renders the EXPANDED pane (§C5.3)
        string search = _search.Value;                  // subscribe → the pinned head re-plans as you type

        var plan = UseMemo(() => BuildPane(search), PlanDep(search));
        Plan = plan;
        // AFTER the plan is published: resolving the travel direction needs the plan the rows are about to render from.
        // This also SUBSCRIBES the pane to the route, so a navigation re-renders it (and therefore re-renders the row
        // indicators, which read these fields) without re-planning — PlanDep deliberately excludes the route.
        TrackSelection(_route.Value.Name);
        var railPlan = UseMemo(() => BuildRailPlan(search), PlanDep(search));

        // The ONE thing the frozen ItemsView cannot read from a field. A layout effect, so the write lands after render.
        int rows = plan.Rows.Count;
        UseLayoutEffect(() => { _rowCount.Value = rows; }, rows);

        ConfigureReorder();

        Element expanded = new BoxEl
        {
            Key = "expanded-layer", Direction = 1, Grow = 1f, Shrink = 0f,
            // Measured at the persisted OPEN width even while the pane is presented compact, so its text never reflows
            // through a 56-DIP layout (the Classic contract).
            Width = Prop.Of(() => ExpandedWidth.Value), ClipToBounds = true,
            Opacity = compact ? 0f : 1f, HitTestVisible = !compact,
            Children = ExpandedChildren(rows),
        };

        var children = new List<Element>(2) { expanded };
        if (!_inDrawer)
            children.Add(new BoxEl
            {
                Key = "compact-layer", Direction = 1, Grow = 1f, Shrink = 0f, Width = 56f,
                Opacity = compact ? 1f : 0f, HitTestVisible = compact,
                // As Classic always did: the rail's overlay scrollbar occupies the same gutter as the shell's resize seam
                // and reads as a page-spanning border, so wheel/touch scrolling stays and the bar does not paint.
                Children = [ScrollView(SidebarPaneRail.Build(this, railPlan)) with
                {
                    Grow = 1f, AutoEdgeFade = true, SuppressScrollBar = true,
                }],
            });

        var root = new BoxEl
        {
            // No Fill and no Corners: the shell's sidebar pane owns the chrome fill and the sidebar is flush frame chrome.
            Grow = 1f, Direction = 1, ZStack = true, ClipToBounds = true,
            Children = [.. children],
        };

        // §3.1.5 / §C6.4 — the pane's own background context menu opens the quick layout menu. Row menus still WIN:
        // ContextMenu.Attach dispatches to the nearest self-or-ancestor handler, so only empty pane chrome reaches this.
        if (MenuOverlay is { } svc && Prefs is { } prefs)
            root = root.WithContextMenu(svc, () => SidebarLayoutMenu.Model(prefs, _go));
        return root;
    }

    // ── the expanded body ────────────────────────────────────────────────────────────────────────────────────────────

    Element[] ExpandedChildren(int rows)
    {
        // 1 — the mode's own fixed chrome (V3's header band / toolbar / chips), then the optional library-only search head.
        //     The head is rendered ONLY when the document actually contains an EntityList section (a library-only search
        //     over a pane with no library list would filter nothing).
        Element? modeHead = Config.Head?.Invoke();
        Element? searchHead = Config.SearchHead && HasEntityList(Doc)
            ? Embed.Comp(() => new SidebarPaneSearchHead(_search, ExpandedWidth)) with { Key = "head" }
            : null;

        // SEED the count ONCE, before the list exists. The updates below ride a layout effect (never a render-time signal
        // write), but the very first frame would otherwise mount the list at count 0 and paint an empty pane for a frame.
        // This one write is provably not a backwards write: nothing has read the signal yet — the consumer is created on
        // the next line.
        if (!_countSeeded) { _countSeeded = true; _rowCount.Value = rows; }

        // 2 — the plan, or the authored-empty pane state (§C4.7: a Blank document, or every section hidden).
        Element body = rows == 0
            ? EmptyPane()
            : PaddedList();

        int n = 1 + (modeHead is null ? 0 : 1) + (searchHead is null ? 0 : 1);
        var kids = new Element[n];
        int k = 0;
        if (modeHead is { } mh) kids[k++] = mh;
        if (searchHead is { } sh) kids[k++] = sh;
        kids[k] = body;
        return kids;
    }

    /// <summary>R3.1.2 — THE PANE'S ONE INSET. Classic's <c>(8,8,8,12)</c> padding is applied HERE, around the virtualized
    /// list, and nowhere else: rows land at 8 and their content at 8+6=14, exactly as Classic's hand-built body did, while
    /// every special band inside the plan sits at the row inset. That single owner is what makes Classic and Curated line
    /// up instead of drifting four different left edges apart.</summary>
    Element PaddedList() => new BoxEl
    {
        Key = "plan-pad", Direction = 1, Grow = 1f, Padding = SidebarPaneMetrics.PanePad,
        Children = [PlanList()],
    };

    Element PlanList() => ItemsView.CreateBound(
        Plan.Rows.Count,
        scope => Embed.Comp(() => new SidebarPaneSlot(this, scope)),
        // MEASURED, not a uniform stack: a pane mixes 16-DIP explicit dividers, 28–38-DIP headers, 32–48-DIP rows,
        // 56–88-DIP cards and multi-line grid strips. The measured layout seeds every row and corrects it to the measured
        // extent on realize (with scroll anchoring), which is exactly what SidebarRow's variable-height vocabulary needs.
        _rowLayout,
        new ListOptions
        {
            // The row primitives draw their own chrome + selection cue, so the view contributes no selector and no
            // selection model (pane selection is the live route, never a list index).
            SelectionMode = ItemsSelectionMode.None,
            Selector = SelectorVisual.None,
            Overscan = 2,
            CacheExtentPx = 240f,
            Grow = 1f,
            CountSignal = _rowCount,
            // One recycle pool per row kind: a header slot never rebinds into an entity row's shape (which is also what
            // makes the per-header/per-folder animated chevron components safe under recycling).
            ContentType = ContentTypeOf,
            Scroll = new ScrollOptions
            {
                AutoEdgeFade = true,
                // Two independent mounts (docked pane + narrow drawer) must not fight over one saved offset.
                ScrollKey = _inDrawer ? Config.ScrollKeyPrefix + ".drawer" : Config.ScrollKeyPrefix,
            },
            // In-place reorder over a RECYCLING list: the resting order holds during the drag (LiveProject=false)
            // and displaced siblings glide via this displacement channel instead of a mid-drag projection.
            Reorder = new ReorderOptions
            {
                ItemDisplacement = _displacement ??= Displacement,
                DisplacementVersion = _dispVersion,
            },
            // R3.1.7b — the collapse/expand choreography rides the SAME bump: freshly-expanded rows fade+rise in behind a
            // per-row stagger, and every row the change displaced GLIDES from its old position (FLIP) instead of cutting.
            Entrance = new EntranceOptions
            {
                ItemFlipFrom = _flipFrom ??= FlipFrom,
                ItemFadeFrom = _fadeFrom ??= FadeFrom,
            },
        }) with { Key = "plan" };

    (float dx, float dy)? FlipFrom(int index) => _flip.TryGetValue(index, out var f) ? f : null;
    (float from, float delayMs)? FadeFrom(int index) => _fade.TryGetValue(index, out var f) ? f : null;

    /// <summary>The authored-empty pane (§C4.7). Never a blank rectangle: it names the state and offers the one action
    /// that fixes it, and it keeps the layout menu reachable when there is no section header to host it. A LOCKED document
    /// (Classic) has no customizer entry, so the CTA is simply absent rather than dead.</summary>
    Element EmptyPane()
    {
        var kids = new List<Element>(4)
        {
            Icon(Icons.SplitView, 24f, Tok.TextTertiary),
            new TextEl(Loc.Get(SidebarPaneLoc.PaneEmpty))
            {
                Size = 14f, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2,
            },
            new TextEl(Loc.Get(SidebarPaneLoc.PaneEmptySub))
            {
                Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3,
            },
        };
        if (Config.OnCustomize is { } customize)
            kids.Add(new BoxEl
            {
                Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(12f, 0f, 12f, 0f), Corners = Radii.ControlAll,
                Fill = Tok.AccentDefault, Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                OnClick = customize,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Sidebar.Layout.Customize))
                    {
                        Size = 13f, Weight = 600, Color = Tok.TextOnAccentPrimary, MaxLines = 1,
                    },
                ],
            }.Interactive(Interaction.Subtle));

        return new BoxEl
        {
            Key = "empty", Direction = 1, Grow = 1f, Gap = Spacing.S,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(16f, 24f, 16f, 24f),
            Children = [.. kids],
        };
    }

    int ContentTypeOf(int index)
    {
        var rows = Plan.Rows;
        return (uint)index < (uint)rows.Count ? (int)rows[index].Kind : 0;
    }

    // ── planning ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything outside the document the plan depends on, folded into one 16-byte key: the document version, the
    /// projection revision, the pin/folder versions, the search text and the MODE epoch. Reading them here is ALSO the
    /// subscription that re-renders this pane (and therefore re-plans) when any of them moves.</summary>
    DepKey PlanDep(string search)
    {
        var prefs = Prefs;
        int layoutVer = prefs?.LayoutVersion.Value ?? 0;
        int entriesVer = prefs?.Entries.Version.Value ?? 0;
        int pinsVer = prefs?.PinsVersion.Value ?? 0;
        int folderVer = prefs?.FolderVersion.Value ?? 0;
        // NOT a signal (the binder is a plain service); it moves in lockstep with Entries.Version, which IS one.
        int revision = prefs?.Binder?.Revision ?? 0;
        int mode = Config.ModeEpoch?.Invoke() ?? 0;
        return DepKey.Combine(DepKey.From(layoutVer, entriesVer, pinsVer, folderVer),
                              DepKey.Combine(DepKey.From(revision, mode), search));
    }

    SidebarRowPlan BuildPane(string search)
    {
        var plan = SidebarRowPlanner.Build(Doc, Input(search), _paneBuffers);
        RebuildIndex(plan);
        Choreograph(plan);
        return plan;
    }

    SidebarRowPlan BuildRailPlan(string search)
        => SidebarRowPlanner.BuildRail(Doc, Input(search), _railBuffers);

    /// <summary>The planner input. Its lists ALIAS the binder's buffers, so the returned plan is valid exactly until the
    /// next rebuild — which is the UseMemo lifetime it is built for.</summary>
    SidebarProjectionInput Input(string search)
    {
        var input = Prefs?.Binder?.CurrentInput ?? default;
        if (Prefs?.Binder is null)
        {
            // No driver at all (a probe / headless mount): every dynamic source is honestly PENDING, so the pane plans
            // skeletons instead of claiming an empty library.
            input = input with
            {
                LibraryState = SidebarSourceState.Pending,
                TreeState = SidebarSourceState.Pending,
                RecentsState = SidebarSourceState.Pending,
                NewReleasesState = SidebarSourceState.Pending,
                ConcertsState = SidebarSourceState.Pending,
            };
        }
        // Bound the tree's RAIL tiles so a 200-playlist rootlist cannot eat the whole 40-tile rail budget and push the
        // sections after it (Classic's DevTools link, a mode's utility band) out of the rail entirely.
        input = input with { RailTreeCap = RailTreeTiles };
        // The MODE's own transform first (V3 folds its filter/sort/search state), then the pane's search head on top —
        // the head is the pane's, so it always wins.
        if (Config.Input is { } shape) input = shape(input);
        if (search.Length > 0) input = input with { Search = search };
        return input;
    }

    /// <summary>Rebuilt with every plan: the sectionId→spec map, the quick-menu host, and the reorder bands.</summary>
    void RebuildIndex(SidebarRowPlan plan)
    {
        _sections.Clear();
        var sections = Doc.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            _sections[sections[i].Id] = sections[i];
            var kids = sections[i].ChildList;
            for (int j = 0; j < kids.Count; j++) _sections[kids[j].Id] = kids[j];
        }

        MenuHostSectionId = null;
        _bands.Clear();
        var rows = plan.Rows;
        string? bandId = null;
        int bandStart = 0;
        float bandExtent = SidebarRowMetrics.ClassicHeight;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (Config.ShowLayoutMenu && MenuHostSectionId is null && row.Kind == SidebarRowKind.SectionHeader)
                MenuHostSectionId = row.SectionId;

            bool item = IsReorderableRow(row);
            if (item && bandId is not null && string.Equals(bandId, row.SectionId, StringComparison.Ordinal)) continue;

            if (bandId is not null)
            {
                _bands.Add(new SidebarPaneBand(bandId, bandStart, i - bandStart, bandExtent));
                bandId = null;
            }
            if (!item) continue;

            var section = SectionOf(row.SectionId);
            if (section is null || !IsReorderableSection(section.Kind)) continue;
            bandId = row.SectionId;
            bandStart = i;
            bandExtent = SidebarPaneMetrics.RowHeight(section);
        }
        if (bandId is not null) _bands.Add(new SidebarPaneBand(bandId, bandStart, rows.Count - bandStart, bandExtent));
    }

    static bool IsReorderableRow(in SidebarRow row) => row.Kind
        is SidebarRowKind.EntityRow or SidebarRowKind.IconRow or SidebarRowKind.Placeholder
        or SidebarRowKind.FolderHeader;

    /// <summary>§C5.1 default: Pinned / StaticLinks / CustomGroup reorder IN PLACE. PlaylistTree and EntityList do NOT —
    /// the rootlist is never written (locked decision 9) and V3's custom order is its own local overlay, which V3 opts
    /// into through <see cref="SidebarPaneConfig.IsReorderableSection"/>.</summary>
    bool IsReorderableSection(SidebarSectionKind kind)
        => Config.IsReorderableSection is { } test
            ? test(kind)
            : kind is SidebarSectionKind.Pinned or SidebarSectionKind.StaticLinks or SidebarSectionKind.CustomGroup;

    static bool HasEntityList(SidebarCustomLayout doc)
    {
        var sections = doc.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Kind == SidebarSectionKind.EntityList && !sections[i].Hidden) return true;
            var kids = sections[i].ChildList;
            for (int j = 0; j < kids.Count; j++)
                if (kids[j].Kind == SidebarSectionKind.EntityList && !kids[j].Hidden) return true;
        }
        return false;
    }

    // ── R3.1.7b — collapse / expand choreography ─────────────────────────────────────────────────────────────────────

    /// <summary>Turn a section toggle into per-row entrance seeds. Called once per PLAN (inside the memo, so exactly once
    /// per change), and only a toggle produces seeds — an unrelated re-plan (library refresh, pin mutation, keystroke)
    /// clears them so a later reorder bump cannot replay a phantom fade on rows that never changed (the ReDeal lesson).
    ///
    /// <para>Reduced motion is NOT branched on here: the seeds go through <c>AnimScheduler.SeedValue</c> under a named
    /// token, which reads the preference as a VALUE and snaps transforms itself. Branching on the mutable
    /// <c>Motion.ReducedMotion</c> global from authoring code is a hook-order hazard.</para></summary>
    void Choreograph(SidebarRowPlan plan)
    {
        _flip.Clear();
        _fade.Clear();
        string? id = _toggledSection;
        _toggledSection = null;
        if (id is null) return;

        var rows = plan.Rows;
        if (_toggledExpand)
        {
            // EXPANDED: the section's body rows are NEW in this plan — fade + rise them in behind a capped stagger, and
            // glide everything below down from where it used to sit.
            int first = -1, last = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!string.Equals(rows[i].SectionId, id, StringComparison.Ordinal)) continue;
                if (rows[i].Kind == SidebarRowKind.SectionHeader) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0) return;

            int ord = 0;
            for (int i = first; i <= last; i++)
            {
                _flip[i] = (0f, -EnterRise);
                _fade[i] = (0f, MathF.Min(ord, StaggerCap) * StaggerStepMs);
                ord++;
            }
            var section = SectionOf(id);
            float grown = (last - first + 1) * (section is null ? SidebarRowMetrics.ClassicHeight
                                                               : SidebarPaneMetrics.RowHeight(section));
            for (int i = last + 1; i < rows.Count; i++) _flip[i] = (0f, -grown);
        }
        else
        {
            // COLLAPSED: the body rows are gone. Everything after the retained header glides UP from its old, lower Y.
            if (_toggledExtent <= 0f) return;
            int after = -1;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Kind == SidebarRowKind.SectionHeader
                    && string.Equals(rows[i].SectionId, id, StringComparison.Ordinal)) { after = i + 1; break; }
            if (after < 0) return;
            for (int i = after; i < rows.Count; i++) _flip[i] = (0f, _toggledExtent);
        }

        // The ItemsView is a CHILD and renders after this, so it seeds in the SAME frame (the DetailTracks.Choreograph
        // precedent). Nothing has read this signal earlier in the pass, so it is provably not a backwards write.
        _dispVersion.Value = _dispVersion.Peek() + 1;
    }

    /// <summary>How much vertical space a section's BODY rows occupy in the CURRENT plan — measured before the collapse so
    /// the glide-up distance is the real one.</summary>
    float BodyExtentOf(string sectionId)
    {
        var section = SectionOf(sectionId);
        if (section is null) return 0f;
        float h = SidebarPaneMetrics.RowHeight(section);
        var rows = Plan.Rows;
        int n = 0;
        for (int i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].SectionId, sectionId, StringComparison.Ordinal)
                && rows[i].Kind != SidebarRowKind.SectionHeader) n++;
        return n * h;
    }

    // ── reads the bound row slots make ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Subscribe the CALLING computation (a row slot's render, a chevron's render) to every epoch that can change
    /// what a row draws: the document, the projection, the pin list, folder expansion, the live search text and the MODE
    /// epoch. Returns their fold so the call cannot be optimised away.
    ///
    /// <para>This is load-bearing, not defensive. A bound row is a FROZEN child: re-planning in this component does not
    /// re-render it. Without these reads a realized row would keep drawing the previous plan's content after a library
    /// refresh, a customizer edit, a section toggle or a keystroke in the search box.</para></summary>
    internal int SubscribeEpoch()
    {
        unchecked
        {
            int h = StringComparer.Ordinal.GetHashCode(_search.Value);
            h = h * 31 + (Config.ModeEpoch?.Invoke() ?? 0);
            var prefs = Prefs;
            if (prefs is null) return h;
            h = h * 31 + prefs.LayoutVersion.Value;
            h = h * 31 + prefs.Entries.Version.Value;
            h = h * 31 + prefs.PinsVersion.Value;
            h = h * 31 + prefs.FolderVersion.Value;
            return h;
        }
    }

    /// <summary>The live selected route. Reading it SUBSCRIBES the caller — a row slot, or this pane for the rail.</summary>
    internal string SelectedRoute => _route.Value.Name;

    // ── selection travel ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bumped once per REAL selection change. A row indicator compares it against the epoch it last reacted to,
    /// which is what separates "the selection moved" from "this slot recycled onto another row" — the two cases a
    /// recycling list makes indistinguishable from the row's point of view.</summary>
    internal int SelEpoch => _selEpoch;

    /// <summary>Which way the selection travelled through the plan (+1 down / -1 up / 0 unknown).</summary>
    internal int SelDirection => _selDir;

    /// <summary>Was <paramref name="route"/> the selection immediately BEFORE the current change? That row owns the
    /// outgoing half of the travel.</summary>
    internal bool WasDeparting(string? route)
        => route is { Length: > 0 } && string.Equals(route, _prevSelRoute, StringComparison.Ordinal);

    /// <summary>Fold a route change into (previous, epoch, direction). Called from Render with the live route: a plain
    /// field write during a reactive computation, exactly like <see cref="Plan"/> — the row indicators are frozen
    /// children that read it at their own render time, so no signal is involved.
    ///
    /// <para>Direction is resolved against the CURRENT plan, so a selection arriving from off-plan (a deep link, a row
    /// inside a collapsed section, the first paint) yields 0 and the indicator honestly fades instead of sliding from an
    /// invented side.</para></summary>
    void TrackSelection(string route)
    {
        if (string.Equals(route, _selRoute, StringComparison.Ordinal)) return;
        _prevSelRoute = _selRoute;
        _selRoute = route;
        _selEpoch++;
        _selDir = SidebarRowGeometry.DirectionOf(IndexOfRoute(_prevSelRoute), IndexOfRoute(route));
    }

    /// <summary>The plan index of the row that navigates to <paramref name="route"/>, or -1. Mirrors the slot's own
    /// resolution order (a projected entry's RouteKey first, then a hand-placed Route item's key) so the direction can
    /// never disagree with which row actually draws as selected. O(rows), once per navigation, zero allocation.</summary>
    int IndexOfRoute(string route)
    {
        if (route.Length == 0) return -1;
        var rows = Plan.Rows;
        var entries = Plan.Entries;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.EntryIndex >= 0 && row.EntryIndex < entries.Count)
            {
                if (string.Equals(entries[row.EntryIndex].RouteKey, route, StringComparison.Ordinal)) return i;
                continue;
            }
            var section = SectionOf(row.SectionId);
            if (section is null) continue;
            if (SidebarPaneText.ItemOf(section, row.Key) is { Target: SidebarItemTarget.Route } item
                && string.Equals(item.Key, route, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>The live search text, for the rows that must NAME it (an empty EntityList says which query matched
    /// nothing). <c>Peek</c>: the subscription belongs to <see cref="SubscribeEpoch"/>, once per row, not once per read.</summary>
    internal string SearchText => _search.Peek();

    internal SidebarSectionSpec? SectionOf(string sectionId)
        => _sections.TryGetValue(sectionId, out var s) ? s : null;

    internal bool TryBandOf(int planIndex, out SidebarPaneBand band)
    {
        for (int i = 0; i < _bands.Count; i++)
            if (_bands[i].Contains(planIndex)) { band = _bands[i]; return true; }
        band = default;
        return false;
    }

    internal Reorderable ReorderFor(string sectionId)
    {
        if (_reorder.TryGetValue(sectionId, out var ro)) return ro;
        ro = new Reorderable(SidebarDragKinds.Entity)
        {
            // MANDATORY over a recycling virtualized list: a mid-drag projection would swap content under the lifted
            // node (the window diff recycles positionally). The resting order holds; displacement is the feedback.
            LiveProject = false,
            // The built-in insertion line's geometry assumes one uniform pitch from the list origin, which a FLAT plan of
            // mixed-height rows does not have — it would draw in the wrong place, so displacement is the only cue.
            ShowInsertionLine = false,
        };
        _reorder[sectionId] = ro;
        return ro;
    }

    /// <summary>The row's placement transition, or null when a Reorderable owns the row's position track.</summary>
    internal static LayoutTransition Placement => RowPlacement;

    // ── reorder wiring ───────────────────────────────────────────────────────────────────────────────────────────────

    void ConfigureReorder()
    {
        for (int i = 0; i < _bands.Count; i++)
        {
            var band = _bands[i];
            var section = SectionOf(band.SectionId);
            if (section is null) continue;
            var ro = ReorderFor(band.SectionId);
            ro.Scene = Context.Scene;
            ro.RequestRender = BumpDisplacement;
            ro.ItemCount = band.Count;
            ro.ItemExtent = band.Extent;
            ro.Spacing = 0f;               // plan rows are contiguous inside the virtualized list — no inter-row gap
            string id = band.SectionId;
            ro.ItemOf = slot => PayloadAt(id, slot);
            ro.OnReorder = (from, to) => Commit(id, from, to);
            ro.OnCrossCommit = (payload, _, _, _, slot) => AcceptForeign(id, payload, slot);
        }
    }

    void BumpDisplacement()
    {
        _dispVersion.Value = _dispVersion.Peek() + 1;
        Context.RequestRerender();
    }

    /// <summary>The ItemsView's displacement channel, in PLAN-row space: a lifted section's siblings part to make room
    /// while every other row stays put. Stable delegate — <c>ListOptions</c> freezes at mount.</summary>
    (float dx, float dy) Displacement(int planIndex)
    {
        if (!TryBandOf(planIndex, out var band)) return (0f, 0f);
        var ro = ReorderFor(band.SectionId);
        if (!ro.IsLifted) return (0f, 0f);
        return (0f, ro.OffsetFor(planIndex - band.Start));
    }

    object? PayloadAt(string sectionId, int slot)
    {
        var band = BandFor(sectionId);
        if (band.Count == 0) return null;
        var rows = Plan.Rows;
        int index = band.Start + slot;
        if ((uint)index >= (uint)rows.Count) return null;
        var row = rows[index];
        if (row.EntryIndex >= 0 && row.EntryIndex < Plan.Entries.Count)
        {
            var e = Plan.Entries[row.EntryIndex];
            return new SidebarDragPayload(SidebarPinId.KindOfEntry(e.Kind), e.Id, e.Uri, e.Name);
        }
        // A hand-placed row (a route shortcut / an unresolved entity): its Key is its identity, which is also its pin id
        // for every pinnable form.
        return new SidebarDragPayload(SidebarPinId.KindOf(row.Key), row.Key, SidebarPinId.UriOf(row.Key), "");
    }

    SidebarPaneBand BandFor(string sectionId)
    {
        for (int i = 0; i < _bands.Count; i++)
            if (string.Equals(_bands[i].SectionId, sectionId, StringComparison.Ordinal)) return _bands[i];
        return default;
    }

    /// <summary>A same-list drop. WHERE the order lives is the mode's business, so this hands the whole context to
    /// <see cref="SidebarPaneConfig.CommitReorder"/> (default: pin store for Pinned, the undoable <c>MoveItem</c> command
    /// for every other reorderable kind).</summary>
    void Commit(string sectionId, int from, int to)
    {
        var section = SectionOf(sectionId);
        if (section is null || from == to) return;
        var band = BandFor(sectionId);
        var ctx = new SidebarPaneReorder(section, from, to, band.Count, slot => KeyAt(sectionId, slot));
        if (Config.CommitReorder is { } commit) commit(ctx);
        else SidebarPaneReorderCommit.Default(Prefs, in ctx);
    }

    string KeyAt(string sectionId, int slot)
    {
        var band = BandFor(sectionId);
        if (band.Count == 0) return "";
        var rows = Plan.Rows;
        int index = band.Start + slot;
        return (uint)index < (uint)rows.Count ? rows[index].Key : "";
    }

    /// <summary>A FOREIGN entity dropped onto a reorderable section. Only Pinned accepts one (the pin store is the shared,
    /// unlimited list every design writes); an authored item list is the customizer's to edit, never a drop target here.</summary>
    void AcceptForeign(string sectionId, object? payload, int slot)
    {
        var section = SectionOf(sectionId);
        if (section is null || section.Kind != SidebarSectionKind.Pinned) return;
        AcceptPinDrop(payload, slot);
    }

    /// <summary>Drop-to-pin, from the pinned band or from the empty-pinned drop zone. An already-pinned payload is a MOVE,
    /// not a duplicate.</summary>
    internal void AcceptPinDrop(object? payload, int slot)
    {
        if (Prefs is not { } prefs) return;
        if (SidebarPaneDrag.Unwrap(payload) is not { } p || p.Id.Length == 0) return;

        int at = prefs.Pins.IndexOf(p.Id);
        if (at >= 0)
        {
            // Removing then inserting shifts every later index down by one, so a downward move lands one slot short
            // without this correction (the Classic rule).
            prefs.MovePin(at, slot > at ? slot - 1 : slot);
            return;
        }
        PinActions.Pin(prefs, p.Id, p.Kind, p.Uri, p.Name);   // append + the toast whose action unpins
        int now = prefs.Pins.IndexOf(p.Id);
        if (now > slot) prefs.MovePin(now, slot);
    }

    /// <summary>The drop target a PINNED row carries (§C5.1's drop-to-pin, scoped to the pinned band so dragging a
    /// playlist row across the pane can never pin it by accident).</summary>
    internal DropTargetSpec PinDropSpec(string sectionId, int slot) => new(
        [SidebarDragKinds.Entity],
        OnDrop: s =>
        {
            // A SAME-LIST reorder arrives as a ReorderPayload owned by this section's Reorderable — its own gesture
            // completion commits that, so this target must ignore it (double-applying would duplicate the move).
            if (s.Payload is ReorderPayload rp && ReferenceEquals(rp.Owner, ReorderFor(sectionId))) return;
            AcceptForeign(sectionId, s.Payload, slot);
        });

    // ── commands + navigation the rows raise ──────────────────────────────────────────────────────────────────────────

    /// <summary>A document command. No-op under a LOCKED document — a read-only mode's rows never offer an editing verb,
    /// and this is the belt to that braces.</summary>
    internal void Dispatch(SidebarCommand command)
    {
        if (Config.ReadOnly) return;
        Prefs?.Dispatch(command);
    }

    /// <summary>Collapse/expand a section. The MODE decides where that state lives (Curated: the undoable
    /// <c>SetSectionCollapsed</c> command; Classic: its own persisted per-section flag), and this records the toggle so
    /// the next plan can choreograph the rows it added or removed.</summary>
    internal void ToggleSection(string sectionId, bool collapsed)
    {
        if (Config.SetSectionCollapsed is not { } apply) return;
        _toggledSection = sectionId;
        _toggledExpand = !collapsed;
        _toggledExtent = collapsed ? BodyExtentOf(sectionId) : 0f;
        apply(sectionId, collapsed);
    }

    internal void Navigate(string routeKey, string? arg) => _go(routeKey, arg);

    internal void OpenCustomizer() => Config.OnCustomize?.Invoke();

    internal void CreatePlaylist() => Config.OnCreatePlaylist?.Invoke();

    /// <summary>Play a single track (a Track item row / a track feed row / a rail track tile). A track has no detail route
    /// — this is the whole reason tracks are excluded from pins and navigation (§C1.8.3).</summary>
    internal void PlayTrack(string uri) => Play(uri, asTrack: true);

    internal void Play(string uri, bool asTrack)
    {
        if (uri.Length == 0) return;
        var player = Acts?.Svc?.Player;
        if (player is null) return;
        if (asTrack) _ = player.PlayTrackAsync(uri);
        else _ = player.PlayAsync(uri);
    }
}

/// <summary>One contiguous run of reorderable plan rows owned by one section (§C5.1). <see cref="Extent"/> is that
/// section's UNIFORM row height — a Reorderable's slot pitch assumes one height per list, which is exactly why every row
/// of a section pins its height from <see cref="SidebarPaneMetrics.RowHeight"/>.</summary>
readonly record struct SidebarPaneBand(string SectionId, int Start, int Count, float Extent)
{
    public bool Contains(int planIndex) => Count > 0 && planIndex >= Start && planIndex < Start + Count;
}
