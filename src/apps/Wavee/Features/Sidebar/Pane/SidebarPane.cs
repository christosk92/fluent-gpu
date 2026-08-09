using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;   // Route
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
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
// SELECTION — ONE mechanism for every mode (R3.0.2). Row-local interaction tint and the 3×16 accent indicator both live
// in the item container, exactly like WinUI NavigationViewItem. On a route edge the two realized item indicators receive
// the same outgoing/incoming 600-ms Offset+Scale choreography as NavigationView.cpp; a recycle is a snap, never a replay.
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
    readonly SidebarPlanBuffers _paneBuffersA = new();
    readonly SidebarPlanBuffers _paneBuffersB = new();
    /// <summary>Row-plan storage for the 56-DIP rail (§C5.2) — its own instance, for the same reason.</summary>
    readonly SidebarPlanBuffers _railBuffersA = new();
    readonly SidebarPlanBuffers _railBuffersB = new();
    bool _presentedUsesA;
    bool _planPublished;
    int _nextPlanEpoch;

    /// <summary>The pane's OWN library-only search text (§C5.1's pinned head). SESSION-ONLY and PANE-OWNED: it is
    /// deliberately not <c>SidebarPreferences.V3Search</c>, which is Mode B's mode-global state — two panes must not filter
    /// each other. It reaches the planner as an override on the binder's input (<c>input with { Search = … }</c>), which is
    /// legal because the planner — not the projection — applies the search filter to EntityList/PlaylistTree.</summary>
    readonly Signal<string> _search = new("");
    /// <summary>The normalized query from the exact input used to build <see cref="Plan"/>. Usually the pane-owned search;
    /// in Library V3 it is the mode-global query supplied by <see cref="SidebarPaneConfig.Input"/>.</summary>
    string _effectiveSearch = "";

    /// <summary>The reactive row count for the frozen-at-mount ItemsView. Written in a LAYOUT EFFECT, never in Render.</summary>
    readonly Signal<int> _rowCount = new(0);
    readonly Signal<int> _planVersion = new(0);

    /// <summary>Bumped by any live reorder gesture AND by a collapse/expand choreography, so the ItemsView re-seeds its
    /// displacement / FLIP / fade tracks over the recycling window.</summary>
    readonly Signal<int> _dispVersion = new(0);
    readonly Signal<int> _disclosureVersion = new(0);

    // -- per-row epochs -----------------------------------------------------------------------------------------------
    /// <summary>One epoch signal per PLAN ROW INDEX. A bound slot is a frozen child, so it must subscribe to something to
    /// re-render at all; subscribing every slot to the pane-wide <see cref="_planVersion"/> meant every publish (a library
    /// refresh, a pin mutation, a projection tick) re-rendered every realized row even when its own content had not
    /// moved. Each slot now reads only its own index, and <see cref="PublishStage"/> bumps only the indices whose row
    /// record or backing entry actually changed (<see cref="SidebarRowDiff"/>).
    ///
    /// <para>GROW-ONLY: a slot may address an index for a frame or two after the plan shrank, and a Signal that vanished
    /// underneath it would silently unsubscribe it forever. The array only ever grows; surplus entries idle.</para></summary>
    Signal<int>[] _rowEpochs = Array.Empty<Signal<int>>();

    /// <summary>Per-row packed now-playing state (bit 0 = this row's entity is the current one, bit 1 = it is actively
    /// playing), maintained by ONE pane-level signal effect. A PLAIN array, not signals: a change bumps the row's epoch,
    /// which is the subscription the slot already holds. Reading the playback signals per realized row instead put every
    /// row on the hot Identity fanout, so one track change re-rendered the whole realized window.</summary>
    byte[] _rowPlay = Array.Empty<byte>();
    /// <summary>The row indices currently carrying a nonzero <see cref="_rowPlay"/> byte, so clearing them is O(matches)
    /// rather than a second full sweep.</summary>
    readonly List<int> _rowPlaySet = new();

    /// <summary>The plan-row indices that currently draw SELECTED, ascending — maintained by <see cref="RefreshSelection"/>
    /// exactly as <see cref="_rowPlaySet"/> is by <see cref="RefreshPlayState"/>. Selection used to be a raw
    /// <c>SelectedRoute</c> signal read inside every realized slot, every pill probe and the rail, so ONE navigation
    /// re-rendered the whole realized window; the pane now sweeps the plan once and bumps only the rows that flipped.
    /// <see cref="_rowSelNext"/> is the reused scratch the sweep fills and <see cref="_rowSelFlip"/> the reused symmetric
    /// difference of the two — the exact set of epochs a route edge bumps.</summary>
    readonly List<int> _rowSelSet = new();
    readonly List<int> _rowSelNext = new();
    readonly List<int> _rowSelFlip = new();
    /// <summary>Cached delegate for the sweep's section lookup — <c>ListOptions</c>-style stability, so a per-route sweep
    /// allocates nothing.</summary>
    Func<string, SidebarSectionSpec?>? _sectionOf;

    static readonly bool DisclosureTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("WAVEE_SIDEBAR_DISCLOSURE_TRACE"), "1", StringComparison.Ordinal);
    Action<ItemDisclosureDiagnostic>? _disclosureTrace;
    string? _activeDisclosureKey;
    string? _activeDisclosureId;
    bool _activeDisclosureIsFolder;
    bool _activeDisclosureOpen;
    string? _pendingExpandSection;
    string? _pendingExpandFolder;
    Action? _queuedDisclosure;

    /// <summary>The virtualized list's public handle. Rootlist drop placement reads its viewport/offset, and selection
    /// motion uses the realized window to mirror WinUI's "both indicators exist" animation guard.</summary>
    readonly ItemsViewController _listController = new();
    readonly Signal<int> _resourceDropRow = new(-1);

    /// <summary>One <c>Reorderable</c> per in-place-reorderable section id (§C5.1). Created lazily and kept for the
    /// component's life — a Reorderable holds gesture state and must not be rebuilt.</summary>
    readonly Dictionary<string, Reorderable> _reorder = new(StringComparer.Ordinal);

    /// <summary>The contiguous plan-row runs those sections own, rebuilt with every plan (see <see cref="SidebarPaneBand"/>).</summary>
    readonly List<SidebarPaneBand> _bands = new();
    /// <summary>Pinned sections currently showing folder descendants. Their top-level pins are no longer contiguous in
    /// plan space, so the flat reorder controller is disabled until the folder collapses rather than treating a child as
    /// an independent pin or moving the wrong store slot.</summary>
    readonly HashSet<string> _pinnedSubtrees = new(StringComparer.Ordinal);
    readonly Dictionary<string, byte> _pinnedDepths = new(StringComparer.Ordinal);

    /// <summary>sectionId → spec, rebuilt with every plan so a row resolves its section in O(1) instead of re-walking the
    /// document per row.</summary>
    readonly Dictionary<string, SidebarSectionSpec> _sections = new(StringComparer.Ordinal);

    // ── R3.1.7b — collapse/expand choreography seeds (the DetailTracks dictionaries-on-the-page precedent) ────────────
    /// <summary>New plan index → the FLIP "first" (the row's OLD visual offset relative to its new slot), so a surviving
    /// row GLIDES from where it was instead of cutting to its new Y.</summary>

    /// <summary>Playlist-tree tiles the 56-DIP rail may draw before the rest of the document gets its turn.</summary>
    const int RailTreeTiles = 20;

    /// <summary>An expanded row eases in from a slight rise (the app's add vocabulary).</summary>

    // ── published to the bound row slots each render. PLAIN FIELDS by design: a bound slot is a frozen child, so it reads
    //    these at ITS render time; writing a signal here would be a render-time signal write (see the file header).
    // Seeded with a real EMPTY plan, never `default`: a default(SidebarRowPlan) carries null lists, and a bound slot that
    // renders one frame ahead of the first plan would NRE instead of drawing nothing.
    internal SidebarRowPlan Plan = new(Array.Empty<SidebarRow>(), Array.Empty<SidebarLibraryEntry>(), 0);
    SidebarRowPlan _railPlan = new(Array.Empty<SidebarRow>(), Array.Empty<SidebarLibraryEntry>(), 0);
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

    sealed record PlanStage(SidebarCustomLayout Document, SidebarRowPlan Pane, SidebarRowPlan Rail,
                            string EffectiveSearch, bool UsesA, int Epoch);

    // ── selection travel (R3.0.2 follow-up) ──────────────────────────────────────────────────────────────────────────
    // Centralized NavigationView transaction. Realized item-owned pills register their exact retained nodes here; the
    // pane force-completes an interrupted pair before starting the next previous/current flight.
    string _selRoute = "";
    string _prevSelRoute = "";
    int _selEpoch;
    NodeHandle _selectionFlightFrom;
    NodeHandle _selectionFlightTo;
    readonly Dictionary<string, SidebarPillRegistration> _selectionPills = new(StringComparer.Ordinal);
    readonly Dictionary<int, string> _selectionRouteByNode = new();

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
        var sourceDoc = Config.Document();

        bool compact = !_inDrawer && _compact.Value;    // the drawer always renders the EXPANDED pane (§C5.3)
        string search = _search.Value;                  // subscribe → the pinned head re-plans as you type

        var stage = UseMemo(() => BuildStage(sourceDoc, search), PlanDep(search));
        if (!_planPublished) PublishStage(stage, notify: false);
        int planVersion = _planVersion.Value;
        int disclosureUiVersion = _disclosureVersion.Value;
        UseLayoutEffect(() => TryPublishStage(stage),
            DepKey.From(HashCode.Combine(stage.Epoch, disclosureUiVersion)));
        // AFTER the plan is published: resolving the travel direction needs the plan the rows are about to render from.
        // This also SUBSCRIBES the pane to the route, so a navigation re-renders it (and therefore re-renders the row
        // indicators, which read these fields) without re-planning — PlanDep deliberately excludes the route.
        TrackSelection(_route.Value.Name);
        // The pane's ONE read of the hot playback signals, on behalf of every row (the MediaCard rule). It writes the
        // per-row now-playing bytes and bumps only the rows that flipped, so a track change re-renders the two rows it
        // concerns instead of the whole realized window.
        UseSignalEffect(RefreshPlayState);
        // …and the pane's ONE read of the live ROUTE on behalf of every row: it bumps the epoch of the row that lost the
        // pill and the row that gained it, so a navigation re-renders two rows instead of the whole realized window.
        UseSignalEffect(RefreshSelection);
        int selectionEpoch = _selEpoch;
        UseLayoutEffect(RunSelectionTransaction, selectionEpoch);
        int rows = Plan.Rows.Count;
        UseLayoutEffect(() =>
        {
            if (_activeDisclosureKey is { } active && _activeDisclosureOpen
                && (_pendingExpandSection is not null || _pendingExpandFolder is not null)
                && PendingExpandRange() is null)
            {
                DisclosureSettled(active);
                return;
            }
            if (_activeDisclosureKey is not null || _queuedDisclosure is not { } queued) return;
            _queuedDisclosure = null;
            queued();
        }, DepKey.From(HashCode.Combine(disclosureUiVersion, rows)));
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

        // THE COMPACT RAIL IS MEMOIZED. Classic's preservation contract keeps BOTH layers mounted at all times (the
        // cross-fade needs both mid-transition), so the 56-DIP rail was rebuilt inside EVERY pane render even at
        // Opacity 0 — 26 `ToolTip.Wrap` targets, each handing the reused ToolTip core a FRESH target element, which
        // defeated ToolTipSlots' ReferenceEquals short-circuit and put ToolTip×26 in nearly every idle flush. The rail
        // is a pure function of the RAIL PLAN and the SELECTED ROUTE, so memoizing it on those makes the short-circuit
        // fire and the whole subtree reconcile as one reference-equal child.
        //
        // THE DEP SET IS THE AUDIT (everything `SidebarPaneRail.Build`/`Tile` reads that can change what it draws):
        //   • the plan version — the rail plan itself, `SectionOf`, the "no binder yet ⇒ skeleton" fallback and the
        //     mode's RailFooter tiles all move only with a publish, which bumps it;
        //   • the selected route — the rail's own selected-tile treatment (read with Peek here, since this dep IS the
        //     re-entry condition and the pane already subscribes the route through TrackSelection);
        //   • Tok.Epoch — a memo is invisible to RethemeAll, which re-renders components but cannot re-enter a memo
        //     whose key held; every tile resolves Tok.* colours by VALUE, so without this a theme switch would leave
        //     the rail on the old palette;
        //   • the culture epoch — same argument for the tile labels (`Loc.Get` inside the tooltip/`ShellNav.Dest`).
        //     Reading it here is also the subscription that re-renders this pane on a culture switch.
        //   • O3 — the shortcut band's LIST: `Config.RailHead` is invoked inside this memo, and the band is
        //     `SidebarCustomLayout.EffectiveTopBar`, a PLAIN property on the document. Its edits bump `LayoutVersion`
        //     (which is not otherwise in this key — the plan version only moves when the ROW plan republishes), and its
        //     COUNT is what decides whether the rail carries head tiles and their separating rule at all. Without both,
        //     a rail that is currently presented would keep drawing the band the user just edited.
        // Unconditional (the hooks-order rule) even in the drawer, which has no rail at all.
        int bandVersion = Prefs?.LayoutVersion.Value ?? 0;
        int bandCount = Prefs?.TopBar.Count ?? SidebarCustomLayout.DefaultTopBar.Count;
        Element compactRail = UseMemo(
            () => _inDrawer
                ? (Element)new BoxEl { Height = 0f, Shrink = 0f }
                : ScrollView(SidebarPaneRail.Build(this, _railPlan)) with
                {
                    Grow = 1f, AutoEdgeFade = true, SuppressScrollBar = true,
                },
            DepKey.Combine(
                DepKey.Combine(
                    DepKey.From(planVersion, Tok.Epoch, Localization.CultureEpoch.Value,
                                // …and the binder's PRESENCE, which the "no driver yet ⇒ shimmer the whole rail" fallback
                                // reads directly. A binder arriving after the first frame need not move any of the versions
                                // above (they all start at 0), and the rail must not stay on skeletons if it does.
                                (_inDrawer ? 1 : 0) | (Prefs?.Binder is null ? 0 : 2)),
                    SelectedRoutePeek),
                DepKey.From(bandVersion, bandCount)));

        var children = new List<Element>(2) { expanded };
        if (!_inDrawer)
            children.Add(new BoxEl
            {
                Key = "compact-layer", Direction = 1, Grow = 1f, Shrink = 0f, Width = 56f,
                Opacity = compact ? 1f : 0f, HitTestVisible = compact,
                // As Classic always did: the rail's overlay scrollbar occupies the same gutter as the shell's resize seam
                // and reads as a page-spanning border, so wheel/touch scrolling stays and the bar does not paint.
                Children = [compactRail],
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
        // 0 — O3: the customizable shortcut band, the pane's TOPMOST chrome. Invoked here (not snapshotted) so the
        //     LayoutVersion it reads subscribes this pane, and above Config.Head because it is the app's navigation
        //     band rather than mode chrome. A null return is an emptied band and costs no layout at all.
        //     The band ends in its OWN closing rule and ZERO bottom padding (SidebarNavBand.BandRule): the 8 DIP under
        //     that rule is PanePad.Top below, contributed once by PaddedList. Nothing here may add a second gap.
        Element? navBand = Config.NavBand?.Invoke();
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

        int n = 1 + (navBand is null ? 0 : 1) + (modeHead is null ? 0 : 1) + (searchHead is null ? 0 : 1);
        var kids = new Element[n];
        int k = 0;
        if (navBand is { } nb) kids[k++] = nb;
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
            Controller = _listController,
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
            Disclosure = new DisclosureOptions
            {
                Version = _planVersion,
                PendingExpand = PendingExpandRange,
                OnExpandStarted = OnExpandStarted,
                OnExpandSettled = OnExpandSettled,
                Diagnostic = DisclosureTraceEnabled ? _disclosureTrace ??= TraceDisclosure : null,
            },
        }) with { Key = "plan" };

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

    PlanStage BuildStage(SidebarCustomLayout document, string search)
    {
        var input = Input(search);
        bool useA = !_planPublished || !_presentedUsesA;
        var paneBuffers = useA ? _paneBuffersA : _paneBuffersB;
        var railBuffers = useA ? _railBuffersA : _railBuffersB;
        var pane = SidebarRowPlanner.Build(document, input, paneBuffers);
        var rail = SidebarRowPlanner.BuildRail(document, input, railBuffers);
        return new PlanStage(document, pane, rail, SidebarSearch.Normalize(input.Search), useA, ++_nextPlanEpoch);
    }

    void TryPublishStage(PlanStage stage)
    {
        // A collapse keeps the expanded model presented until the close reaches zero. Expansion is the exception: its
        // inserted generation must publish before ItemsView can resolve and arm the opening range.
        bool preparedExpansion = _activeDisclosureOpen
            && (_pendingExpandSection is not null || _pendingExpandFolder is not null);
        if (_activeDisclosureKey is not null && !preparedExpansion) return;
        if (_planPublished && stage.UsesA == _presentedUsesA && ReferenceEquals(stage.Pane.Rows, Plan.Rows)) return;
        PublishStage(stage, notify: true);
    }

    void PublishStage(PlanStage stage, bool notify)
    {
        // Captured BEFORE the swap: the outgoing plan is what the realized rows are still drawing. The A/B plan buffers
        // are what make holding these safe, since the incoming plan was built into the other buffer set and cannot have
        // overwritten them underneath the diff.
        var oldRows = Plan.Rows;
        var oldEntries = Plan.Entries;
        // A new document or a new effective query changes what a row draws without necessarily changing the row record
        // (section titles, empty-state copy and inline controls all hang off them), so those edges bump wholesale.
        bool wholesale = !_planPublished
                         || !ReferenceEquals(stage.Document, Doc)
                         || !string.Equals(stage.EffectiveSearch, _effectiveSearch, StringComparison.Ordinal);

        Doc = stage.Document;
        Plan = stage.Pane;
        _railPlan = stage.Rail;
        _effectiveSearch = stage.EffectiveSearch;
        _presentedUsesA = stage.UsesA;
        _planPublished = true;
        RebuildIndex(Plan);
        ConfigureReorder();
        EnsureRowSlots(Plan.Rows.Count);
        if (!notify) return;

        void PublishSignals()
        {
            _rowCount.Value = Plan.Rows.Count;
            _planVersion.Value = _planVersion.Peek() + 1;
            if (wholesale) BumpAllRowEpochs();
            else BumpChangedRowEpochs(oldRows, oldEntries);
        }
        if (Context.Runtime is { } runtime) runtime.Batch(PublishSignals);
        else PublishSignals();
    }

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
        _pinnedSubtrees.Clear();
        _pinnedDepths.Clear();
        var rows = plan.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (SectionOf(row.SectionId)?.Kind != SidebarSectionKind.Pinned) continue;
            if (row.Kind == SidebarRowKind.SectionHeader)
            {
                _pinnedDepths[row.SectionId] = row.Depth;
                continue;
            }
            if (row.EntryIndex >= 0 && _pinnedDepths.TryGetValue(row.SectionId, out byte sectionDepth)
                && row.Depth > sectionDepth)
                _pinnedSubtrees.Add(row.SectionId);
        }
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
            if (section is null || !IsReorderableSection(section.Kind)
                || _pinnedSubtrees.Contains(row.SectionId)) continue;
            bandId = row.SectionId;
            bandStart = i;
            bandExtent = SidebarPaneMetrics.RowHeight(section);
        }
        if (bandId is not null) _bands.Add(new SidebarPaneBand(bandId, bandStart, rows.Count - bandStart, bandExtent));
    }

    static bool IsReorderableRow(in SidebarRow row) => row.Kind
        is SidebarRowKind.EntityRow or SidebarRowKind.IconRow or SidebarRowKind.Placeholder
        or SidebarRowKind.FolderHeader;

    /// <summary>§C5.1 default: Pinned / StaticLinks / CustomGroup reorder IN PLACE. PlaylistTree and EntityList use
    /// resource-drop destinations instead; V3 may additionally opt a section into its local view-order overlay through
    /// <see cref="SidebarPaneConfig.IsReorderableSection"/>.</summary>
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
    /// <summary>Folder counterpart to section choreography. A playlist-tree folder owns one contiguous preorder band:
    /// descendants enter with the standard add rise/fade; on collapse the generic ItemsView removal seam keeps the
    /// departing realized rows alive while every survivor below FLIPs upward by their old extent.</summary>
    static int FolderIndexOf(SidebarRowPlan plan, string folderId)
    {
        var rows = plan.Rows;
        var entries = plan.Entries;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind != SidebarRowKind.FolderHeader || (uint)row.EntryIndex >= (uint)entries.Count) continue;
            if (string.Equals(entries[row.EntryIndex].FolderId, folderId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    ItemDisclosureRange? PendingExpandRange()
    {
        if (_pendingExpandSection is { } section && TrySectionBodyRange(section, out var sectionRange))
            return sectionRange;
        if (_pendingExpandFolder is { } folder && TryFolderDescendantRange(folder, out var folderRange))
            return folderRange;
        return null;
    }

    void OnExpandStarted(ItemDisclosureRange range)
    {
        if (!string.Equals(_activeDisclosureKey, range.Key, StringComparison.Ordinal)) return;
        _pendingExpandSection = null;
        _pendingExpandFolder = null;
    }

    void OnExpandSettled(ItemDisclosureRange range) => DisclosureSettled(range.Key);

    internal bool DisclosureOpen(string id, bool folder, bool fallback)
        => _activeDisclosureKey is not null
           && _activeDisclosureIsFolder == folder
           && string.Equals(_activeDisclosureId, id, StringComparison.Ordinal)
            ? _activeDisclosureOpen
            : fallback;

    bool TrySectionBodyRange(string sectionId, out ItemDisclosureRange range)
    {
        var rows = Plan.Rows;
        if (!SidebarRowGeometry.TrySectionBodyRange(rows, sectionId, out int first, out int count))
        {
            range = default;
            return false;
        }
        range = new ItemDisclosureRange("section:" + sectionId, first, count);
        return true;
    }

    bool TryFolderDescendantRange(string folderId, out ItemDisclosureRange range)
    {
        int folderIndex = FolderIndexOf(Plan, folderId);
        if (folderIndex < 0)
        {
            range = default;
            return false;
        }
        var rows = Plan.Rows;
        var entries = Plan.Entries;
        var folder = rows[folderIndex];
        if ((uint)folder.EntryIndex >= (uint)entries.Count)
        {
            range = default;
            return false;
        }
        int depth = entries[folder.EntryIndex].Depth;
        int end = folderIndex + 1;
        while (end < rows.Count)
        {
            var row = rows[end];
            if (!string.Equals(row.SectionId, folder.SectionId, StringComparison.Ordinal)
                || (uint)row.EntryIndex >= (uint)entries.Count
                || entries[row.EntryIndex].Depth <= depth) break;
            end++;
        }
        int count = end - folderIndex - 1;
        range = count > 0
            ? new ItemDisclosureRange("folder:" + folderId, folderIndex + 1, count)
            : default;
        return count > 0;
    }

    void StartDisclosure(string key, string id, bool folder, bool open, Action commit)
    {
        if (_activeDisclosureKey is not null && !string.Equals(_activeDisclosureKey, key, StringComparison.Ordinal))
        {
            _queuedDisclosure = () => StartDisclosure(key, id, folder, open, commit);
            if (_pendingExpandSection is not null || _pendingExpandFolder is not null)
            {
                string completed = _activeDisclosureKey;
                _pendingExpandSection = null;
                _pendingExpandFolder = null;
                DisclosureSettled(completed);
            }
            else _listController.CompleteDisclosure();
            return;
        }
        if (_activeDisclosureKey is not null && _activeDisclosureOpen == open) return;

        _activeDisclosureKey = key;
        _activeDisclosureId = id;
        _activeDisclosureIsFolder = folder;
        _activeDisclosureOpen = open;

        ItemDisclosureRange range;
        bool hasRange = folder
            ? TryFolderDescendantRange(id, out range)
            : TrySectionBodyRange(id, out range);
        if (open && !hasRange)
        {
            if (folder) _pendingExpandFolder = id; else _pendingExpandSection = id;
            commit();
            _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
            BumpAllRowEpochs();   // a disclosure edge re-skins the chevron plus the whole revealed/hidden range
            return;
        }
        if (!hasRange)
        {
            commit();
            DisclosureSettled(key);
            return;
        }

        _pendingExpandSection = null;
        _pendingExpandFolder = null;
        _listController.BeginDisclosure(range,
            open ? ItemDisclosureDirection.Expand : ItemDisclosureDirection.Collapse,
            collapseCommit: open ? null : commit,
            settled: () => DisclosureSettled(key));
        _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
        BumpAllRowEpochs();
    }

    void DisclosureSettled(string key)
    {
        if (!string.Equals(_activeDisclosureKey, key, StringComparison.Ordinal)) return;
        _activeDisclosureKey = null;
        _activeDisclosureId = null;
        _pendingExpandSection = null;
        _pendingExpandFolder = null;
        _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
        BumpAllRowEpochs();
    }

    static void TraceDisclosure(ItemDisclosureDiagnostic d)
    {
        string eventId = d.Kind switch
        {
            ItemDisclosureDiagnosticKind.Queued => "sidebar.disclosure.queued",
            ItemDisclosureDiagnosticKind.Starting => "sidebar.disclosure.starting",
            ItemDisclosureDiagnosticKind.Armed => "sidebar.disclosure.armed",
            ItemDisclosureDiagnosticKind.Progress => "sidebar.disclosure.progress",
            ItemDisclosureDiagnosticKind.Committing => "sidebar.disclosure.committing",
            ItemDisclosureDiagnosticKind.Settled => "sidebar.disclosure.settled",
            ItemDisclosureDiagnosticKind.Cleared => "sidebar.disclosure.cleared",
            ItemDisclosureDiagnosticKind.Recovered => "sidebar.disclosure.recovered",
            _ => "sidebar.disclosure.failed_to_arm",
        };
        WaveeLog.Instance.Event(WaveeLogLevel.Info, "sidebar", eventId,
            "Sidebar disclosure lifecycle edge.", operationId: "disclosure-" + d.OperationId,
            fields:
            [
                WaveeLogField.Of("kind", d.Kind.ToString()),
                WaveeLogField.Of("key", d.Range.Key),
                WaveeLogField.Of("direction", d.Direction.ToString()),
                WaveeLogField.Of("first", d.Range.FirstIndex),
                WaveeLogField.Of("range_count", d.Range.Count),
                WaveeLogField.Of("item_count", d.ItemCount),
                WaveeLogField.Of("source_version", d.SourceVersion),
                WaveeLogField.Of("progress", (double)d.Progress),
            ]);
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
            int h = _planVersion.Value;
            h = h * 31 + _disclosureVersion.Value;
            return h;
        }
    }

    /// <summary>The per-row form of <see cref="SubscribeEpoch"/>, and what every bound slot actually reads: it subscribes
    /// the caller to ONE row's epoch instead of to the pane-wide plan version, so a publish re-renders only the rows the
    /// diff found changed.
    ///
    /// <para>Out-of-range falls back to the pane-wide version. That is the safety valve for the transient window where a
    /// slot addresses an index the epoch array has not grown to cover yet: it subscribes to something guaranteed to be
    /// bumped by the publish which grows the array, so the slot cannot be stranded unsubscribed.</para></summary>
    internal int SubscribeRowEpoch(int index)
    {
        var epochs = _rowEpochs;
        return (uint)index < (uint)epochs.Length ? epochs[index].Value : _planVersion.Value;
    }

    /// <summary>This row's now-playing pair, maintained by <see cref="RefreshPlayState"/>. No signal read: a change to it
    /// bumped the row's epoch, which is the subscription the calling slot already holds.</summary>
    internal (bool Playing, bool Animated) RowPlayState(int index)
    {
        var play = _rowPlay;
        byte packed = (uint)index < (uint)play.Length ? play[index] : (byte)0;
        return ((packed & 1) != 0, (packed & 2) != 0);
    }

    /// <summary>Grow the per-row side tables to cover <paramref name="count"/> rows. Grow-only, doubling.</summary>
    void EnsureRowSlots(int count)
    {
        if (count <= _rowEpochs.Length) return;
        int cap = Math.Max(32, _rowEpochs.Length);
        while (cap < count) cap *= 2;
        var epochs = new Signal<int>[cap];
        Array.Copy(_rowEpochs, epochs, _rowEpochs.Length);
        for (int i = _rowEpochs.Length; i < cap; i++) epochs[i] = new Signal<int>(0);
        var play = new byte[cap];
        Array.Copy(_rowPlay, play, _rowPlay.Length);
        _rowEpochs = epochs;
        _rowPlay = play;
    }

    void BumpRowEpoch(int index)
    {
        var epochs = _rowEpochs;
        if ((uint)index < (uint)epochs.Length) epochs[index].Value = epochs[index].Peek() + 1;
    }

    /// <summary>Bump every row epoch. The escape hatch for edges whose blast radius is not a clean index range: a new
    /// document, a changed effective search, or a disclosure edge. All three are discrete user gestures or mode switches
    /// rather than the per-publish traffic this mechanism exists to cut, so paying the old whole-window re-render there
    /// keeps the change honest instead of risking a stale row for a win nobody can feel.</summary>
    void BumpAllRowEpochs()
    {
        var epochs = _rowEpochs;
        for (int i = 0; i < epochs.Length; i++) epochs[i].Value = epochs[i].Peek() + 1;
    }

    /// <summary>Bump only the rows whose record or backing entry changed between the outgoing and incoming plans.</summary>
    void BumpChangedRowEpochs(IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries)
    {
        var rows = Plan.Rows;
        var entries = Plan.Entries;
        for (int i = 0; i < rows.Count; i++)
            if (SidebarRowDiff.RowChanged(oldRows, oldEntries, rows, entries, i))
                BumpRowEpoch(i);
    }

    /// <summary>The ONE place the hot playback signals are read for the whole pane (the MediaCard rule). Runs as a
    /// pane-level signal effect: the coarse <c>HasActiveContext</c> bool is read first so an idle app never joins the
    /// <c>Identity</c> fanout at all, and a change writes the packed per-row byte and bumps only that row's epoch. Before
    /// this, every realized row read those signals itself, so one track change re-rendered the whole realized window.</summary>
    void RefreshPlayState()
    {
        _ = _planVersion.Value;   // a republish re-plans which index holds which entity, so re-resolve
        var bridge = Playback;
        bool active = bridge is not null && bridge.HasActiveContext.Value;
        PlaybackIdentity identity = default;
        bool playing = false;
        if (active)
        {
            identity = bridge!.Identity.Value;
            playing = bridge.IsPlaying.Value;
        }

        // Clear the previous matches first: at most a couple of rows are ever lit, so this is O(matches) and the sweep
        // below only has to SET. A row that stays the playing one is re-set to the same byte and never bumps.
        var play = _rowPlay;
        for (int i = 0; i < _rowPlaySet.Count; i++)
        {
            int idx = _rowPlaySet[i];
            if ((uint)idx >= (uint)play.Length || play[idx] == 0) continue;
            play[idx] = 0;
            BumpRowEpoch(idx);
        }
        _rowPlaySet.Clear();
        if (!active) return;

        byte packed = playing ? (byte)3 : (byte)1;
        var rows = Plan.Rows;
        int n = Math.Min(rows.Count, play.Length);
        for (int i = 0; i < n; i++)
        {
            string uri = RowPlayUri(i);
            if (uri.Length == 0 || !NowPlayingOverlay.Matches(uri, identity.ContextUri, identity.Track)) continue;
            _rowPlaySet.Add(i);
            if (play[i] == packed) continue;
            play[i] = packed;
            BumpRowEpoch(i);
        }
    }

    /// <summary>The entity uri row <paramref name="index"/> would play, resolved EXACTLY as the slot resolves it. Single
    /// owner on purpose: the slot reads <see cref="RowPlayState"/> rather than deriving a uri of its own, so the effect's
    /// view of which row is playing cannot drift from what the row draws.</summary>
    string RowPlayUri(int index)
    {
        var rows = Plan.Rows;
        if ((uint)index >= (uint)rows.Count) return "";
        var row = rows[index];
        var entries = Plan.Entries;
        bool resolved = row.EntryIndex >= 0 && row.EntryIndex < entries.Count;
        switch (row.Kind)
        {
            // A card draws its entity's play affordance only when the projection resolved it (SidebarPaneSlot.Card).
            case SidebarRowKind.EntityCard:
                return resolved ? entries[row.EntryIndex].Uri : "";
            // SidebarPaneSlot.ItemOrEntity's order: an ACTION item is an action row (no play state) whatever kind the
            // planner chose; then the projected entry; then a hand-placed TRACK plays from its own spec. The section
            // lookup only exists to spot that Action item, and an Action item never reaches the entry branch, so the
            // resolved (overwhelmingly common) case skips it entirely.
            case SidebarRowKind.IconRow:
            case SidebarRowKind.EntityRow:
            case SidebarRowKind.Placeholder:
            {
                if (resolved) return entries[row.EntryIndex].Uri;
                var section = SectionOf(row.SectionId);
                if (section is null) return "";
                var item = SidebarPaneText.ItemOf(section, row.Key);
                return item is { Target: SidebarItemTarget.Track } ? item.Key : "";
            }
            default:
                return "";
        }
    }

    /// <summary>The live selected route. Reading it SUBSCRIBES the caller.
    ///
    /// <para>Only <see cref="TrackSelection"/>'s read in the pane's own render uses this now (a navigation re-rendering
    /// the PANE is by design — it re-plans nothing, but it is what drives the selection transaction). Row slots, pill
    /// probes and the rail read <see cref="SelectedRoutePeek"/> instead and are re-rendered by their row epoch, which
    /// <see cref="RefreshSelection"/> bumps for exactly the rows that flipped.</para></summary>
    internal string SelectedRoute => _route.Value.Name;

    /// <summary>The selected route WITHOUT subscribing. The caller must have another reason to re-render on a route
    /// change — a realized row has its per-row epoch (<see cref="RefreshSelection"/>), and the rail's memo has the route
    /// in its dep key.</summary>
    internal string SelectedRoutePeek => _route.Peek().Name;

    /// <summary>Does row <paramref name="index"/> draw itself SELECTED for <paramref name="route"/>? Delegates to the ONE
    /// owner of that rule (<see cref="SidebarRowResolve.SelectsRoute"/>), which the pane's selection sweep also uses — so
    /// the rows the sweep bumps are exactly the rows whose skin changes. The same single-owner discipline as
    /// <see cref="RowPlayUri"/>.</summary>
    internal bool RowSelectsRoute(int index, string route)
    {
        var rows = Plan.Rows;
        if ((uint)index >= (uint)rows.Count) return false;
        var row = rows[index];
        return SidebarRowResolve.SelectsRoute(in row, Plan.Entries, SectionOf(row.SectionId), route);
    }

    /// <summary>The pane's ONE read of the live route on behalf of every row (the same shape as
    /// <see cref="RefreshPlayState"/>): it sweeps the plan for the rows that draw selected and bumps the SYMMETRIC
    /// DIFFERENCE against the previous set — the row that lost the pill and the row that gained it, and nothing else.
    /// Before this, every realized slot, its pill and the rail read the route signal directly, so a navigation
    /// re-rendered the whole realized window (Slot×52 + Pill×44) three times over.</summary>
    void RefreshSelection()
    {
        string route = _route.Value.Name;   // SUBSCRIBE — a navigation re-runs the sweep
        _ = _planVersion.Value;             // a republish re-plans which index holds which row, so re-resolve

        var next = _rowSelNext;
        next.Clear();
        SidebarRowResolve.Sweep(Plan.Rows, Plan.Entries, _sectionOf ??= SectionOf, route, next);

        var prev = _rowSelSet;
        var flipped = _rowSelFlip;
        flipped.Clear();
        SidebarRowResolve.Flipped(prev, next, flipped);
        for (int i = 0; i < flipped.Count; i++) BumpRowEpoch(flipped[i]);

        prev.Clear();
        for (int i = 0; i < next.Count; i++) prev.Add(next[i]);
    }

    // ── selection travel ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Capture one NavigationView selection edge. The layout effect starts the pair only after the route's row
    /// skins have reconciled, matching NavigationView's previous/current/active transaction ordering.</summary>
    void TrackSelection(string route)
    {
        if (string.Equals(route, _selRoute, StringComparison.Ordinal)) return;
        _prevSelRoute = _selRoute;
        _selRoute = route;
        _selEpoch++;
    }

    internal void RegisterSelectionPill(string route, NodeHandle node)
    {
        var scene = Context.Scene;
        if (route.Length == 0 || scene is null || node.IsNull || !scene.IsLive(node)) return;
        int index = (int)node.Raw.Index;
        if (_selectionRouteByNode.TryGetValue(index, out var previous)
            && !string.Equals(previous, route, StringComparison.Ordinal))
            _selectionPills.Remove(previous);
        _selectionRouteByNode[index] = route;
        _selectionPills[route] = new SidebarPillRegistration(node);
    }

    void RunSelectionTransaction()
    {
        var scene = Context.Scene;
        var anim = Context.Anim;
        if (scene is null || anim is null) return;

        // NavigationView force-completes the old pair before a rapid retarget. This is the missing interruption rule that
        // allowed several recycled pills to stay half-visible and flicker against section headers.
        if (!_selectionFlightFrom.IsNull && scene.IsLive(_selectionFlightFrom))
            NavigationSelectionMotion.SnapVertical(anim, _selectionFlightFrom, visible: false);
        if (!_selectionFlightTo.IsNull && scene.IsLive(_selectionFlightTo))
            NavigationSelectionMotion.SnapVertical(anim, _selectionFlightTo, visible: true);
        _selectionFlightFrom = _selectionFlightTo = NodeHandle.Null;

        if (!TrySelectionPill(_selRoute, scene, out var incoming)) return;
        if (!TrySelectionPill(_prevSelRoute, scene, out var outgoing))
        {
            NavigationSelectionMotion.SnapVertical(anim, incoming.Node, visible: true);
            return;
        }

        var from = scene.AbsoluteRect(outgoing.Node);
        var to = scene.AbsoluteRect(incoming.Node);
        float travel = to.Y - from.Y;
        if (!float.IsFinite(travel) || MathF.Abs(travel) <= 0.5f || outgoing.Node == incoming.Node)
        {
            NavigationSelectionMotion.SnapVertical(anim, outgoing.Node, visible: false);
            NavigationSelectionMotion.SnapVertical(anim, incoming.Node, visible: true);
            return;
        }

        // NavigationView compares the indicators' actual cross-axis positions. Section identity is irrelevant: two
        // equally-indented rows in different groups still run the continuous worm, while a real depth change scales.
        bool sameLane = MathF.Abs(to.X - from.X) < 0.5f;
        NavigationSelectionMotion.StartVertical(anim, outgoing.Node, 0f, travel,
            SidebarSelectionPill.PillH, outgoing: true, sameDepth: sameLane);
        NavigationSelectionMotion.StartVertical(anim, incoming.Node, -travel, 0f,
            SidebarSelectionPill.PillH, outgoing: false, sameDepth: sameLane);
        _selectionFlightFrom = outgoing.Node;
        _selectionFlightTo = incoming.Node;
    }

    bool TrySelectionPill(string route, SceneStore scene, out SidebarPillRegistration registration)
    {
        if (route.Length > 0 && _selectionPills.TryGetValue(route, out registration)
            && !registration.Node.IsNull && scene.IsLive(registration.Node)) return true;
        registration = default;
        return false;
    }

    float RowExtentOf(int index)
    {
        float cross = MathF.Max(1f, ExpandedWidth.Peek() - SidebarPaneMetrics.PaneInsetH);
        var layout = _rowLayout.CustomLayout;
        if (layout is null) return SidebarRowMetrics.ClassicHeight;
        float extent = layout.ItemRect(index, cross).H;
        return float.IsFinite(extent) && extent > 0f ? extent : SidebarRowMetrics.ClassicHeight;
    }

    /// <summary>The normalized query from the input that built the published plan, for rows that must name it. This is the
    /// pane-owned query in Classic/Custom and Library V3's mode-global query after its input transform.</summary>
    internal string SearchText => _effectiveSearch;

    internal SidebarSectionSpec? SectionOf(string sectionId)
        => _sections.TryGetValue(sectionId, out var s) ? s : null;

    // Memoized per (section, plan revision): a PlaylistTree section reserves TreeLeading's disclosure lane only
    // where it actually has a folder to align a leaf's art against — a folder-free library (V3's common case) reads
    // flush instead of carrying a dead chevron-width indent that visibly misaligns it against a StaticLinks row like
    // Liked Songs sitting right above it. Cached rather than scanned per realized row: `EntryRow` runs once per
    // recycle, and a naive per-row scan of the whole plan would cost O(realized rows × plan rows) every scroll frame.
    readonly Dictionary<string, (int Revision, bool HasFolder)> _sectionHasFolder = new();
    internal bool SectionHasFolder(string sectionId)
    {
        if (_sectionHasFolder.TryGetValue(sectionId, out var cached) && cached.Revision == Plan.Revision)
            return cached.HasFolder;
        var rows = Plan.Rows;
        bool has = false;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Kind == SidebarRowKind.FolderHeader && string.Equals(rows[i].SectionId, sectionId, StringComparison.Ordinal))
            { has = true; break; }
        _sectionHasFolder[sectionId] = (Plan.Revision, has);
        return has;
    }

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
        ro = new Reorderable(WaveeDragKinds.Resource)
        {
            // MANDATORY over a recycling virtualized list: a mid-drag projection would swap content under the lifted
            // node (the window diff recycles positionally). The resting order holds; displacement is the feedback.
            LiveProject = false,
            // The built-in insertion line's geometry assumes one uniform pitch from the list origin, which a FLAT plan of
            // mixed-height rows does not have — it would draw in the wrong place, so displacement is the only cue.
            ShowInsertionLine = false,
            // ONE visual per gesture. A sidebar row's ReorderPayload unwraps to a WaveeResourceDragPayload, so the
            // shell's DragPreviewLayer renders the chip for it — and the default GHOST lift would ALSO translate the
            // live row under the cursor: two moving pictures of one drag, which is exactly the S1/S4 failure this
            // campaign removed everywhere else. Opacity 0 rather than 0.4: here the vacated slot IS the origin gap, so
            // a dimmed row still sitting in it would read as a duplicate of the chip.
            DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = 0f },
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
            // The entry says what this is, so the payload can carry rootlist membership honestly even though the row
            // is being dragged inside a reorderable pin band — dropping it on a FOLDER files it (see SidebarPaneSlot).
            return WaveeResourceDragPayload.FromEntry(e, Acts?.Svc,
                e.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder);
        }
        // A hand-placed row (a route shortcut / an unresolved entity): its Key is its identity, which is also its pin id
        // for every pinnable form.
        var destination = SidebarDestination.FromRoute(row.Key, null, "");
        return destination is { } d
            ? WaveeResourceDragPayload.FromDestination(d, Acts)
            : null;
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
        if (WaveeResourceDrag.Unwrap(payload) is not { } p || p.Id.Length == 0 || !p.TryPin(out var pinKind)) return;

        int at = prefs.Pins.IndexOf(p.Id);
        if (at >= 0)
        {
            // Removing then inserting shifts every later index down by one, so a downward move lands one slot short
            // without this correction (the Classic rule).
            prefs.MovePin(at, slot > at ? slot - 1 : slot);
            return;
        }
        PinActions.Pin(prefs, p.Id, pinKind, p.Uri, p.Name);   // append + the toast whose action unpins
        int now = prefs.Pins.IndexOf(p.Id);
        if (now > slot) prefs.MovePin(now, slot);
    }

    /// <summary>One resource target can be both a playlist deposit and a pinned-band insertion. Tracks/albums/playlists
    /// route to the playlist mutation seam; pinnable resources route to the shared pin store.</summary>
    internal DropTargetSpec ResourceDropSpec(string sectionId, int slot, string? playlistUri, string? playlistName,
                                             WaveeResourceDragPayload? rootTarget = null,
                                             int rootPlanIndex = -1,
                                             Action? onSpringLoad = null)
    {
        bool Compatible(WaveeResourceDragPayload source)
        {
            if (rootTarget is not null && source.RootlistItem
                && source.Kind is WaveeResourceKind.Playlist or WaveeResourceKind.Folder)
                // Identity is checked on BOTH keys now that rootlist payloads also arrive from outside the sidebar
                // (a tab, a card, a detail hero), where the Id is the entity's uri rather than a sidebar pin id — the
                // Id compare alone would let a playlist be filed relative to its own row.
                return !string.Equals(source.Id, rootTarget.Id, StringComparison.Ordinal)
                       && !(source.Uri.Length > 0
                            && string.Equals(source.Uri, rootTarget.Uri, StringComparison.Ordinal));
            if (playlistUri is { Length: > 0 } && source.CanCopyTracks) return true;
            return slot >= 0 && source.CanPin;
        }

        // The CAPTION is written here rather than through the facade's caption hook because this row's outcome depends
        // on WHERE in it the pointer is (an editable playlist's centre deposits tracks; its edges file it in the
        // rootlist), and only the session carries that. Hover runs on both Enter and Over, which is exactly the refresh
        // cadence a pointer-dependent caption needs.
        void Hover(WaveeResourceDragPayload p, DragSession s)
        {
            _resourceDropRow.Value = Compatible(p) ? rootPlanIndex : -1;
            s.Caption = CaptionFor(p, s);
        }

        string? CaptionFor(WaveeResourceDragPayload p, DragSession s)
        {
            // A same-band reorder is this section's own gesture: its feedback is the displacement, and naming it "Pin X"
            // would claim a pin that already exists (Reorderable's own rule, applied to the row-level target too).
            if (s.Payload is ReorderPayload own && ReferenceEquals(own.Owner, ReorderFor(sectionId))) return null;
            if (rootTarget is { } root && p.RootlistItem
                && p.Kind is WaveeResourceKind.Playlist or WaveeResourceKind.Folder)
            {
                bool canDeposit = root.Kind == WaveeResourceKind.Playlist
                    && playlistUri is { Length: > 0 } && p.CanCopyTracks;
                var placement = RootlistPlacementFor(rootPlanIndex,
                    root.Kind == WaveeResourceKind.Folder, canDeposit, s.Position);
                if (placement != RootlistDropPlacement.Inside || !canDeposit)
                    // Only "inside a folder" is worth a sentence. A before/after filing is an ORDERING, and the row it
                    // lands next to is already under the pointer — captioning it would narrate what the user can see.
                    return root.Kind == WaveeResourceKind.Folder && placement == RootlistDropPlacement.Inside
                        ? Strings.Drag.MoveInto(root.Name)
                        : null;
            }
            if (playlistUri is { Length: > 0 } && p.CanCopyTracks) return Strings.Drag.AddTo(playlistName ?? "");
            if (slot >= 0 && p.CanPin) return Strings.Drag.Pin(p.Name);
            return null;
        }
        void Leave(DragSession _) { if (_resourceDropRow.Peek() == rootPlanIndex) _resourceDropRow.Value = -1; }

        void CommitDrop(WaveeResourceDragPayload source, DragSession s)
        {
            Leave(s);
            if (rootTarget is { } root && Acts is { } rootActs
                && source is { RootlistItem: true,
                    Kind: WaveeResourceKind.Playlist or WaveeResourceKind.Folder })
            {
                bool canDepositIntoPlaylist = root.Kind == WaveeResourceKind.Playlist
                    && playlistUri is { Length: > 0 } && source.CanCopyTracks;
                var placement = RootlistPlacementFor(rootPlanIndex,
                    root.Kind == WaveeResourceKind.Folder, canDepositIntoPlaylist, s.Position);
                // The centre of an editable playlist is an entity destination; its edge bands remain rootlist
                // before/after targets. This preserves both "playlist into playlist" and durable organization.
                if (placement != RootlistDropPlacement.Inside || !canDepositIntoPlaylist)
                {
                    WaveeResourceDrop.MoveRootlist(rootActs, s.Payload, root, placement);
                    return;
                }
            }
            if (playlistUri is { Length: > 0 } target && Acts is { } acts
                && WaveeResourceDrop.CanDepositTracks(s.Payload))
            {
                WaveeResourceDrop.DepositTracks(acts, target, playlistName ?? "", s.Payload, insertionIndex: null);
                return;
            }
            // A SAME-LIST reorder arrives as a ReorderPayload owned by this section's Reorderable — its own gesture
            // completion commits that, so this target must ignore it (double-applying would duplicate the move).
            if (slot >= 0 && s.Payload is ReorderPayload rp && ReferenceEquals(rp.Owner, ReorderFor(sectionId))) return;
            if (slot >= 0) AcceptForeign(sectionId, s.Payload, slot);
        }

        // The refusal cue is deliberately NARROW: only a row that IS a track destination explains itself, and only for
        // the one refusal a user can act on — a payload with no tracks behind it. Every other refusal here means "this
        // row was never a destination for this thing", where a sentence would be noise on top of the chip's glyph.
        string? WhyRefused(WaveeResourceDragPayload source)
            => playlistUri is { Length: > 0 } && !source.CanCopyTracks
                // Locked decision: an artist has no single obvious track set, so we refuse rather than guess. Future
                // work is a picker that lets the USER choose what to deposit (top tracks / a release).
                ? Loc.Get(source.Kind == WaveeResourceKind.Artist
                    ? Strings.Drag.CantAddArtist
                    : Strings.Drag.NothingToAdd)
                : null;

        return Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
            accepts: Compatible, onDrop: CommitDrop, onEnter: Hover, onOver: Hover, onLeave: Leave,
            visualPolicy: DropTargetVisualPolicy.Spotlight,
            refusalCaption: WhyRefused,
            // Spring-load (a COLLAPSED folder row supplies the callback): dwelling opens the container so the user can
            // keep travelling into it. It is armed even when this row REFUSES the payload — opening a folder is
            // navigation, not a deposit, and the folder whose contents you are aiming at is often not itself a target.
            springLoadMs: onSpringLoad is null ? 0f : WaveeResourceDrag.SpringLoadMs,
            onSpringLoad: onSpringLoad is null ? null : (_, _) => onSpringLoad());
    }

    internal bool IsResourceDropActive(int planIndex)
        => planIndex >= 0 && _resourceDropRow.Value == planIndex;

    RootlistDropPlacement RootlistPlacementFor(int planIndex, bool folder, bool allowInsidePlaylist, Point2 pointer)
    {
        if (planIndex < 0) return folder ? RootlistDropPlacement.Inside : RootlistDropPlacement.Before;
        var viewport = _listController.Viewport;
        var scene = Context.Scene;
        if (scene is null || viewport.IsNull || !scene.IsLive(viewport))
            return folder ? RootlistDropPlacement.Inside : RootlistDropPlacement.Before;
        var rect = scene.AbsoluteRect(viewport);
        float contentY = pointer.Y - rect.Y + _listController.ScrollOffset;
        float top = SidebarRowGeometry.ContentYOf(planIndex, Plan.Rows.Count, RowExtentOf);
        float extent = MathF.Max(1f, RowExtentOf(planIndex));
        float t = Math.Clamp((contentY - top) / extent, 0f, 1f);
        if (!folder && !allowInsidePlaylist)
            return t < 0.5f ? RootlistDropPlacement.Before : RootlistDropPlacement.After;
        if (t < 0.25f) return RootlistDropPlacement.Before;
        if (t > 0.75f) return RootlistDropPlacement.After;
        return RootlistDropPlacement.Inside;
    }

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
        StartDisclosure("section:" + sectionId, sectionId, folder: false, open: !collapsed,
            () => apply(sectionId, collapsed));
    }

    /// <summary>Activate a folder through the mode seam while keeping inline disclosure structurally animated. Narrow
    /// drill/grid modes bypass this path via <see cref="SidebarPaneConfig.DisclosesFoldersInline"/>; Classic, Custom and
    /// wide LibraryV3 share the same outgoing-orphan + survivor-FLIP choreography.</summary>
    internal void ActivateFolder(string folderId, string name, int planIndex)
    {
        if (folderId.Length == 0 || Prefs is not { } prefs) return;
        Action commit = Config.ActivateFolder is { } activate
            ? () => activate(folderId, name)
            : () => prefs.ToggleFolder(folderId);

        if (!(Config.DisclosesFoldersInline?.Invoke() ?? true)) { commit(); return; }

        bool expanded = prefs.IsFolderExpanded(folderId);
        _ = planIndex;
        StartDisclosure("folder:" + folderId, folderId, folder: true, open: !expanded, commit);
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
