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
    /// <summary>The band the ACTIVE disclosure discloses, once it has resolved — the rows a disclosure edge re-skins
    /// (with the header) instead of the whole realized window.</summary>
    ItemDisclosureRange? _activeDisclosureBand;
    /// <summary>Next-frame dispatch, captured in <c>Render</c> (<c>UsePost</c> consumes no hook cell). Used to keep the
    /// coalesced preference write off the click frame.</summary>
    Action<Action>? _post;
    Action? _flushPrefsCommit;
    Action? _queuedDisclosure;

    /// <summary>The virtualized list's public handle. Rootlist drop placement reads its viewport/offset, and selection
    /// motion uses the realized window to mirror WinUI's "both indicators exist" animation guard.</summary>
    readonly ItemsViewController _listController = new();

    /// <summary>THE ONE published drop slot: which plan row is armed, what a drop there MEANS, at what depth, or why it
    /// is refused. Written exactly once per hover (<c>ResourceDropSpec.Hover</c>) and consumed by three readers — the
    /// row's insertion line, the row's Into plate, and <c>CommitDrop</c>, which takes the published slot rather than
    /// recomputing one (a cue and a mutation computed twice are a cue and a mutation that can disagree).
    /// <para>It replaced a bare <c>Signal&lt;int&gt;</c> row index: the index alone said "something is armed here" and
    /// nothing about WHICH of the three outcomes it was, which is why Before, Inside and After all drew the same
    /// accent plate.</para></summary>
    readonly Signal<SidebarDropSlot> _dropSlot = new(SidebarDropSlot.None);

    /// <summary>DRAG PEEK: a TRANSIENT expansion of a collapsed pane, held only for the duration of one drag.
    /// <para>The reported bug (2026-08-10) is that dragging a song with the sidebar collapsed dims the whole app to 55%
    /// and cuts out nothing but the player bar: the 56-DIP rail declares no drop targets at all, and the expanded rows
    /// that DO are correctly pruned from both hit-testing and the scrim by the engine's reachability guard
    /// (<c>SceneStore.IsHitReachable</c>) because the hidden layer is <c>HitTestVisible = false</c>. So the app makes the
    /// scrim's promise — "these cutouts are your options" — and then points at nothing. Dwelling on the rail slides the
    /// pane open for the rest of the gesture, which turns every playlist back into a real, labelled destination.</para>
    /// <para>Deliberately NOT a write to <c>SidebarPreferences.Collapsed</c>: the user asked for a collapsed sidebar and a
    /// drag must not silently redecide that. It is cleared when the SESSION ends (not on leave — once peeked, travelling
    /// right onto the rows must not collapse the pane out from under the pointer), by
    /// <see cref="SidebarDragPeekWatcher"/>.</para></summary>
    readonly Signal<bool> _dragPeek = new(false);

    /// <summary>The rail TILE currently armed as a drop destination, by playlist uri (null = none). Separate from
    /// <see cref="_dropSlot"/> because a rail tile has no row in the expanded plan. Read through a BOUND prop
    /// (<c>SidebarRailItem.Art</c>) — the rail subtree is memoized, so a cue that needed a render would never appear.</summary>
    readonly Signal<string?> _railDropUri = new(null);

    /// <summary>Is this rail tile the armed drop destination? Called from a bound prop, so it must stay a plain signal
    /// read — no allocation, no interpolation (it runs inside the 0-alloc frame region while a drag is live).</summary>
    internal bool IsRailDropActive(string uri)
        => string.Equals(_railDropUri.Value, uri, StringComparison.Ordinal);

    /// <summary>One <c>Reorderable</c> per in-place-reorderable section id (§C5.1). Created lazily and kept for the
    /// component's life — a Reorderable holds gesture state and must not be rebuilt.</summary>
    readonly Dictionary<string, Reorderable> _reorder = new(StringComparer.Ordinal);

    /// <summary>The contiguous plan-row runs those sections own, rebuilt with every plan (see <see cref="SidebarPaneBand"/>).</summary>
    readonly List<SidebarPaneBand> _bands = new();

    // ── PHASE 2 / Decision B — the customize canvas ───────────────────────────────────────────────────────────────────
    /// <summary>The section-card drag band: ONE run of <c>SectionCard</c> rows at ONE uniform pitch
    /// (<c>SidebarPaneMetrics.EditCardHeight</c>). Empty <c>Count</c> = not armed, which is the normal sidebar and also
    /// edit mode with a section expanded (see <c>SidebarEditPlan.SectionsReorderable</c>). It is deliberately NOT in
    /// <see cref="_bands"/>: those are keyed by section id and resolve through <see cref="ReorderFor"/> to a per-section
    /// ITEM reorderable of a different drag kind, and one lookup answering two questions is how the wrong slot gets
    /// moved.</summary>
    SidebarPaneBand _sectionBand;
    Reorderable? _sectionReorder;

    /// <summary>The drag kind of the section-card band. Its OWN kind, never <c>WaveeDragKinds.Resource</c>: a document
    /// SECTION is not a pinnable entity, and letting a pin band or a playlist row accept one (or vice versa) would be a
    /// drop that silently does nothing.
    /// <para>PHASE 3 — the literal moved to <see cref="SidebarEditPlan.SectionDragKind"/> so the companion page's
    /// palette chips can carry the SAME kind without the string being typed twice (a drag kind spelled twice is a drop
    /// that silently accepts nothing). This alias stays because every use site in this file reads it.</para></summary>
    const string SectionDragKind = SidebarEditPlan.SectionDragKind;
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

    /// <summary>The pane's context-menu SHIELD — see the note at the end of <c>Render</c>.</summary>
    const string ContextShieldKey = "sidebar:context-shield";

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

    /// <summary>PHASE 2 — the edit session THE PUBLISHED PLAN WAS BUILT FROM, published as a plain field beside
    /// <see cref="Plan"/> for the same reason (a bound slot is a frozen child that reads these at ITS render time, and a
    /// signal write from Render would be a backwards write). Keeping it in lockstep with the plan is load-bearing: a
    /// card slot must never draw an "expanded" chevron for a plan that has no body rows under it.</summary>
    internal SidebarEditState? Edit;

    Func<int, (float dx, float dy)>? _displacement;
    bool _countSeeded;

    sealed record PlanStage(SidebarCustomLayout Document, SidebarRowPlan Pane, SidebarRowPlan Rail,
                            string EffectiveSearch, bool UsesA, int Epoch, SidebarEditState? Edit);

    /// <summary>THE MID-DRAG FREEZE. While a rootlist filing is live, a re-projection is parked here instead of
    /// published — the rows a drag is aiming at must not move under the pointer (<see cref="SidebarStageHold{TStage}"/>
    /// carries the reasoning). <see cref="SidebarDragPeekWatcher"/> flushes it on session end.</summary>
    readonly SidebarStageHold<PlanStage> _deferredStage = new();

    /// <summary>One-shot: let the NEXT stage through the freeze. A reorder the pane itself just committed is not a
    /// foreign projection — it is the direct result of the gesture that is still settling, and holding it would snap the
    /// dropped row home for the whole ~250 ms settle window before it jumped to where it was released.</summary>
    bool _publishThroughFreeze;

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
    /// it is created ONCE per pane instance and never per render.
    ///
    /// <para>ANALYTIC, not a flat estimate. Every planned row's height is already a pure function of (kind × section
    /// display options × previous row kind) — <see cref="SidebarRowExtents"/> — so the host seeds each row at its REAL
    /// extent instead of claiming 44 DIP for all thirteen kinds. That is what makes a folder expansion still: the
    /// inserted band and the rows below it land at their final geometry BEFORE anything realizes, so the content extent
    /// does not jump as they measure and the scroll anchor never re-pins against a stale offset. Measurement still
    /// corrects the two kinds the ladder cannot predict exactly (a GridStrip, a wrapping chip strip).</para></summary>
    readonly RepeatLayout _rowLayout;

    /// <summary>A reorderable row's placement transition: Revision 2 assigns reordering <c>MotionTok.ItemPlacement</c>.
    /// A <c>Reorderable.Item</c>-wrapped row must not ALSO carry an authored offset hint (one position owner per node).</summary>
    static readonly LayoutTransition RowPlacement = new(
        TransitionChannels.Position, MotionTok.ItemPlacement.ToDynamics());

    public SidebarPane(SidebarPaneConfig config, Signal<Route> route, Action<string, string?> go, Signal<bool> compact,
                       Signal<float> expandedWidth, bool inDrawer = false)
    {
        Config = config; _route = route; _go = go; _compact = compact; ExpandedWidth = expandedWidth; _inDrawer = inDrawer;
        // Built here (not as a field initializer) because the seed reads THIS pane's published plan. The delegate is
        // allocated once per pane and is part of the layout object's identity — never rebuilt per render.
        _rowLayout = RepeatLayout.Extents(RowExtentSeed, estimatedExtent: SidebarRowGeometry.ClassicHeight);
    }

    public override Element Render()
    {
        Prefs = UseContext(SidebarPreferences.Slot);
        _post = UsePost();                       // consumes no hook cell — safe beside the context reads
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

        // The drawer always renders the EXPANDED pane (§C5.3). A live DRAG PEEK also presents expanded — see _dragPeek:
        // reading it here is the pane's subscription, and it flips at most twice per drag (spring-load, session end).
        bool compact = !_inDrawer && _compact.Value && !_dragPeek.Value;
        string search = _search.Value;                  // subscribe → the pinned head re-plans as you type

        // PHASE 2 / Decision B — the live edit session, or null. Invoked UNCONDITIONALLY and outside every hook, on both
        // arms: edit mode must not change this component's hook sequence (the branch is on the VALUE, never around the
        // hooks). A mode with no `Edit` delegate — Classic, Library V3 — simply gets null here, and every path below
        // reduces to the landed behaviour byte for byte.
        var edit = Config.Edit?.Invoke();

        var stage = UseMemo(() => BuildStage(sourceDoc, search, edit), PlanDep(search, in edit));
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
        //
        // PHASE 1 removed a fourth dep, `(LayoutVersion, TopBar.Count)`. It existed because `Config.RailHead` drew the
        // shortcut band from a PLAIN property outside the rail plan. The band is now the document's first SECTION, so a
        // shortcut edit bumps `LayoutVersion`, which is in `PlanDep`, which re-plans, which moves `planVersion` — the
        // dep that was already here. Re-adding a band dep would be the same artifact keyed twice.
        Element compactRail = UseMemo(
            () => _inDrawer
                ? (Element)new BoxEl { Height = 0f, Shrink = 0f }
                : ScrollView(SidebarPaneRail.Build(this, _railPlan)) with
                {
                    Grow = 1f, AutoEdgeFade = true, SuppressScrollBar = true,
                },
            DepKey.Combine(
                DepKey.From(planVersion, Tok.Epoch, Localization.CultureEpoch.Value,
                            // …and the binder's PRESENCE, which the "no driver yet ⇒ shimmer the whole rail" fallback
                            // reads directly. A binder arriving after the first frame need not move any of the versions
                            // above (they all start at 0), and the rail must not stay on skeletons if it does.
                            (_inDrawer ? 1 : 0) | (Prefs?.Binder is null ? 0 : 2)),
                SelectedRoutePeek));

        var children = new List<Element>(3) { expanded };
        if (!_inDrawer)
            children.Add(new BoxEl
            {
                Key = "compact-layer", Direction = 1, Grow = 1f, Shrink = 0f, Width = 56f,
                Opacity = compact ? 1f : 0f, HitTestVisible = compact,
                // DRAG PEEK: dwelling anywhere on the rail opens the pane for the rest of the gesture. springLoadOnly is
                // exactly the right shape — the dnd contract makes such a node a pure WAYPOINT: never a destination,
                // never a refusal — so this band can span the whole rail without ever swallowing a deposit that belongs
                // to a tile beneath it. 250ms rather than the 500ms container convention: this is not "open a folder you
                // might not have meant", it is "show me the labels", and the pane it opens is itself full of targets.
                DropTarget = RailPeekDropSpec(),
                // As Classic always did: the rail's overlay scrollbar occupies the same gutter as the shell's resize seam
                // and reads as a page-spanning border, so wheel/touch scrolling stays and the bar does not paint.
                Children = [compactRail],
            });
        // Zero-size, renders nothing: it exists to own the one UseDragState() subscription that ends a peek, so the PANE
        // subscribes only to _dragPeek (2 flips per drag) instead of to the drag epoch (which bumps on every target and
        // caption change) — the same "one reader on behalf of many" split the pane already uses for playback and route.
        if (!_inDrawer) children.Add(Embed.Comp(() => new SidebarDragPeekWatcher(this)) with { Key = "drag-peek" });

        var root = new BoxEl
        {
            // No Fill and no Corners: the shell's sidebar pane owns the chrome fill and the sidebar is flush frame chrome.
            Grow = 1f, Direction = 1, ZStack = true, ClipToBounds = true,
            Children = [.. children],
        };

        // §3.1.5 / §C6.4 — the pane's own background context menu opens the quick layout menu. Row menus still WIN:
        // ContextMenu.Attach dispatches to the nearest self-or-ancestor handler, so only empty pane chrome reaches this.
        //
        // …but it may NOT hang off the pane root, and this is the same defect the immersive stage's identity column
        // documents (StageIdentity.ContextScope). `OnContextRequested` sets InteractionInfo.ContextBit, and ContextBit
        // is in InputDispatcher.Hit's hit-anywhere mask — an element with a context flyout is a hit-test target in its
        // own right (the WinUI rule). So a press on any DEAD SPOT of the sidebar (the rail's separator rule, the gap
        // between tiles, the pane's own padding) resolved the ROOT as the press/hover owner, and every engine cascade
        // that starts at the hit node then started at a node whose subtree is the ENTIRE sidebar.
        //
        // The fix is the stage's, verbatim: the menu goes on a ZStack SHELL plus a CHILDLESS full-bleed SHIELD beneath
        // the content. Hit takes the LAST matching child, so the content layers still win wherever they hit and the
        // shield takes everything else — and a cascade from the shield reaches exactly nothing, because it has no
        // children. The shell keeps ContextBit as an ANCESTOR, which is all right-click-anywhere ever needed (the
        // context funnel walks self-or-ancestors) and which no hover/press cascade ever starts from: HoverWithin is
        // published only for Pointer/Click/Pressed bits, and the press target is the deepest HIT node — the shield.
        if (MenuOverlay is { } svc && Prefs is { } prefs)
        {
            Func<ContextMenuModel?> menu = () => SidebarLayoutMenu.Model(prefs, _go);
            root = new BoxEl
            {
                Grow = 1f, Direction = 1, ZStack = true, ClipToBounds = true,
                Children =
                [
                    // MUST STAY CHILDLESS — that is the whole contract. SidebarPaneInvariantTests pins the literal.
                    new BoxEl { Key = ContextShieldKey }.WithContextMenu(svc, menu),
                    root with { Grow = 0f, Shrink = 0f },
                ],
            }.WithContextMenu(svc, menu);
        }
        return root;
    }

    // ── the expanded body ────────────────────────────────────────────────────────────────────────────────────────────

    Element[] ExpandedChildren(int rows)
    {
        // PHASE 1 — the shortcut band is no longer chrome at position 0. It is the FIRST SECTION of `Doc`
        // (`SidebarShortcutsSection`), so it lives inside the virtualized plan list below: it scrolls with the document,
        // it takes the pane's ONE inset instead of carrying a duplicate of it, and it joins the pane's route-keyed
        // selection transaction instead of drawing a static mark that had to opt out of it.
        // 1 — the mode's own fixed chrome (V3's header band / toolbar / chips), then the optional library-only search head.
        //     The head is rendered ONLY when the document actually contains an EntityList section (a library-only search
        //     over a pane with no library list would filter nothing).
        Element? modeHead = Config.Head?.Invoke();
        // PHASE 2: no search head while the pane is the canvas. The head filters EntityList/PlaylistTree BODIES, and in
        // edit mode at most one section's body is on screen — a search box that visibly filters nothing is exactly the
        // affordance-that-does-nothing this rework exists to remove. Not a Design branch: it reads the session, which
        // arrives through the config delegate like everything else mode-specific.
        Element? searchHead = Config.SearchHead && Edit is null && HasEntityList(Doc)
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
    DepKey PlanDep(string search, in SidebarEditState? edit)
    {
        var prefs = Prefs;
        int layoutVer = prefs?.LayoutVersion.Value ?? 0;
        int entriesVer = prefs?.Entries.Version.Value ?? 0;
        int pinsVer = prefs?.PinsVersion.Value ?? 0;
        int folderVer = prefs?.FolderVersion.Value ?? 0;
        // NOT a signal (the binder is a plain service); it moves in lockstep with Entries.Version, which IS one.
        int revision = prefs?.Binder?.Revision ?? 0;
        int mode = Config.ModeEpoch?.Invoke() ?? 0;
        // PHASE 2: entering/leaving edit mode, expanding another card and flipping "Show section contents" all change
        // which rows exist, so the session folds into the plan key exactly like the MODE epoch beside it. The fold is
        // never 0 for a live session, so "no session" cannot collide with one (SidebarEditPlan.Fold).
        return DepKey.Combine(DepKey.From(layoutVer, entriesVer, pinsVer, folderVer),
                              DepKey.Combine(DepKey.From(revision, mode, SidebarEditPlan.Fold(in edit), 0), search));
    }

    PlanStage BuildStage(SidebarCustomLayout document, string search, SidebarEditState? edit)
    {
        var input = Input(search);
        bool useA = !_planPublished || !_presentedUsesA;
        var paneBuffers = useA ? _paneBuffersA : _paneBuffersB;
        var railBuffers = useA ? _railBuffersA : _railBuffersB;
        // The EDIT PROJECTION of the same document, through the same buffers, into the same one flat row list (iron
        // rule 3). Not a second renderer and not a substituted column: one `SidebarRow[]`, one `ItemsView.CreateBound`.
        var pane = edit is { } session
            ? SidebarRowPlanner.BuildEdit(document, input, in session, paneBuffers)
            : SidebarRowPlanner.Build(document, input, paneBuffers);
        // The 56-DIP rail is NEVER the canvas: it has no room for a card and nothing about it is editable, so it keeps
        // planning the real document. A user who collapses the pane mid-edit sees their actual rail, which is honest.
        var rail = SidebarRowPlanner.BuildRail(document, input, railBuffers);
        return new PlanStage(document, pane, rail, SidebarSearch.Normalize(input.Search), useA, ++_nextPlanEpoch, edit);
    }

    void TryPublishStage(PlanStage stage)
    {
        // A collapse keeps the expanded model presented until the close reaches zero. Expansion is the exception: its
        // inserted generation must publish before ItemsView can resolve and arm the opening range.
        bool preparedExpansion = _activeDisclosureOpen
            && (_pendingExpandSection is not null || _pendingExpandFolder is not null);
        if (_activeDisclosureKey is not null && !preparedExpansion) return;
        if (_planPublished && stage.UsesA == _presentedUsesA && ReferenceEquals(stage.Pane.Rows, Plan.Rows)) return;
        // ── THE MID-DRAG FREEZE ──────────────────────────────────────────────────────────────────────────────────────
        // A rootlist filing is aiming at these very rows; a projection published now re-keys them under the pointer and
        // the drop lands somewhere the user did not aim. Park the newest stage and apply it on session end.
        //
        // A DISCLOSURE in flight is deliberately exempt: spring-loading a collapsed folder mid-drag exists precisely to
        // reveal its children, and that expansion has to reach the plan. `_activeDisclosureKey is null` is therefore the
        // gate — the two arms above have already let a prepared expansion through.
        //
        // Non-rootlist drags (a track set looking for a playlist) are not frozen: they aim at a row's IDENTITY, not at
        // its position, so a re-projection under them changes nothing about where the deposit lands.
        if (_publishThroughFreeze) _publishThroughFreeze = false;
        else if (_activeDisclosureKey is null && _deferredStage.TryHold(WaveeResourceDrag.LiveRootlistDrag(), stage))
            return;
        PublishStage(stage, notify: true);
    }

    /// <summary>Publish whatever the freeze parked, exactly once. Called from <see cref="SidebarDragPeekWatcher"/>'s
    /// LAYOUT EFFECT on session end — drop, cancel and Escape alike, so no path strands a deferred projection.</summary>
    internal void FlushDeferredStage()
    {
        // The latch is a same-gesture affordance; a session end retires it whether or not the commit it was set for ever
        // produced a stage, so it can never carry into the next drag.
        _publishThroughFreeze = false;
        if (_deferredStage.TryFlush(out var stage) && stage is not null) PublishStage(stage, notify: true);
    }

    /// <summary>Drop a parked stage without publishing it — the pane is unmounting mid-gesture, so nobody will ever
    /// flush it (the <c>PlaylistReorderDefer.Discard</c> discipline).</summary>
    internal void DiscardDeferredStage() => _deferredStage.Discard();

    void PublishStage(PlanStage stage, bool notify)
    {
        // Any publish — the first one, a disclosure's, the flush's own — makes a parked stage stale: it was built into a
        // buffer set this publish is about to swap. Emptying the bay here is what keeps "flushed exactly once" true
        // however the publish was reached.
        _deferredStage.Discard();
        // Captured BEFORE the swap: the outgoing plan is what the realized rows are still drawing. The A/B plan buffers
        // are what make holding these safe, since the incoming plan was built into the other buffer set and cannot have
        // overwritten them underneath the diff.
        var oldRows = Plan.Rows;
        var oldEntries = Plan.Entries;
        // A new document or a new effective query changes what a row draws without necessarily changing the row record
        // (section titles, empty-state copy and inline controls all hang off them), so those edges bump wholesale.
        // PHASE 2: the session is in the same class of edge — a card's chevron, its dimming and its affordance set all
        // hang off it without necessarily changing the row RECORD (entering edit mode changes every row's kind, but
        // expanding a card leaves the surviving cards' records identical while their expanded-state changes).
        bool wholesale = !_planPublished
                         || !ReferenceEquals(stage.Document, Doc)
                         || !string.Equals(stage.EffectiveSearch, _effectiveSearch, StringComparison.Ordinal)
                         || !Nullable.Equals(stage.Edit, Edit);

        Edit = stage.Edit;
        Doc = stage.Document;
        Plan = stage.Pane;
        _railPlan = stage.Rail;
        _effectiveSearch = stage.EffectiveSearch;
        _presentedUsesA = stage.UsesA;
        _planPublished = true;
        RebuildIndex(Plan);
        ConfigureReorder();
        EnsureRowSlots(Plan.Rows.Count);
        // The seeds read the plan, so a wholesale change (which invalidates the index→row mapping outright) re-derives
        // them all. An incremental publish deliberately does NOT: its surviving rows keep the extents they measured.
        if (wholesale) ReseedRowExtents();
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
        RebuildSectionBand(plan);
        var rows = plan.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (SectionOf(row.SectionId)?.Kind != SidebarSectionKind.Pinned) continue;
            // In the canvas the CARD stands in for the header, so it is what registers the section's base depth — without
            // this a Pinned section expanded over an open folder would not disable its own item band and a descendant
            // row would be treated as an independent pin (the `_pinnedSubtrees` guard).
            if (row.Kind is SidebarRowKind.SectionHeader or SidebarRowKind.SectionCard)
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

    /// <summary>PHASE 2 — resolve the section-card drag band, or leave it empty (= disarmed).
    ///
    /// <para>STRUCTURAL DRAG IS ARMED ONLY IN EDIT MODE (the Discord lesson: an always-armed structural drag is chronic
    /// accidental reorders). Outside the canvas this returns immediately and nothing about pin/item reorder changes.</para>
    ///
    /// <para>Inside it, the band is armed only while every card is a card — <c>SidebarEditPlan.SectionsReorderable</c>
    /// owns that rule and its reasoning. The band additionally SKIPS the pinned Shortcuts head: the sentinel is not in
    /// <c>Sections</c>, so it can neither move nor be moved past, and covering it would let a drop above it map onto
    /// index 0 — which is BELOW it — i.e. the cue and the outcome disagreeing. Excluding it makes the band's slots line
    /// up 1:1 with the cards the reducer can actually address.</para>
    ///
    /// <para>Verified contiguous rather than assumed: any card whose body is showing terminates the run, so a stale
    /// state can only ever shrink the band, never mis-address it.</para></summary>
    void RebuildSectionBand(SidebarRowPlan plan)
    {
        _sectionBand = default;
        if (Edit is not { } edit || !SidebarEditPlan.SectionsReorderable(in edit)) return;

        var rows = plan.Rows;
        int start = -1, count = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind != SidebarRowKind.SectionCard) { if (start >= 0) break; continue; }
            if (SidebarEditPlan.IsPinnedCard(rows[i].SectionId)) { if (start >= 0) break; continue; }
            if (start < 0) start = i;
            count++;
        }
        if (start < 0 || count < 2) return;   // a single card has nothing to reorder against
        _sectionBand = new SidebarPaneBand(SectionDragKind, start, count, SidebarPaneMetrics.EditCardHeight);
    }

    /// <summary>The section-card band for a plan row, when this row is one of its cards.</summary>
    internal bool TryEditSectionBand(int planIndex, out SidebarPaneBand band)
    {
        band = _sectionBand;
        return band.Contains(planIndex);
    }

    /// <summary>The section-card <c>Reorderable</c> — created once and kept for the component's life (it holds gesture
    /// state and must never be rebuilt mid-drag).</summary>
    internal Reorderable SectionReorder => _sectionReorder ??= new Reorderable(SectionDragKind)
    {
        // Same two reasons as the pane's item bands: a live projection would swap content under the lifted node over a
        // POSITIONALLY recycling virtualized list, and the built-in insertion line's geometry is measured from the
        // `List(...)` wrapper's origin — which the pane never mounts, because each band is a RUN inside the one
        // virtualized plan list. Displacement (the ItemsView channel) is the cue.
        LiveProject = false,
        ShowInsertionLine = false,
        // DELIBERATELY NOT `RequireDropOnList`: that flag needs the `List(...)` wrapper to observe the release, and the
        // pane mounts no wrapper — setting it here would make `_selfDrop` permanently false and CANCEL every reorder.
        // Nothing else accepts this private kind, so a release away from the pane already commits nothing meaningful.
        //
        // DragStyle stays NULL — i.e. the engine's ghost lift, where the card itself is the moving visual. The app's
        // chip resolver answers only `WaveeDragKinds.Resource` (`WaveeResourceDrag.Chip`), so a Stationary lift here
        // would dim the card in place and draw NOTHING that moves: one gesture, zero visuals (the dnd skill's
        // "two visuals for one gesture" pitfall, in its other direction — "a Stationary source in an app with no
        // preview layer mounted has no moving visual at all"). The customizer outline's own section list makes the same
        // call for the same reason.
        AnnounceAssertive = true,
    };

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
        => SidebarRowGeometry.FolderHeaderIndexOf(plan.Rows, plan.Entries, folderId);

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
        // Record the band the host actually armed, so the settle edge re-skins exactly those rows (and the header)
        // rather than the whole realized window.
        _activeDisclosureBand = range;
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
        // ONE resolver, in the pure layer, so the disclosure's band and the tests that pin it cannot drift.
        if (!SidebarRowGeometry.TryFolderDescendantRange(Plan.Rows, Plan.Entries, folderId, out int first, out int count))
        {
            range = default;
            return false;
        }
        range = new ItemDisclosureRange("folder:" + folderId, first, count);
        return true;
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
            SchedulePrefsCommit();
            // PUBLISH ON THE CLICK FRAME. The expanded model must reach the plan before `ItemsView` can resolve and arm
            // the opening band, and the pane's ordinary publish rides a LAYOUT EFFECT — a whole frame later, which is
            // why the chevron used to rotate about two frames before any row moved. This runs in the INPUT phase, so
            // the signals it writes are read by the flush that follows: a forward write, not a backwards one.
            RepublishNow();
            _activeDisclosureBand = PendingExpandRange();
            _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
            BumpDisclosureEpochs(id, folder, _activeDisclosureBand);
            return;
        }
        if (!hasRange)
        {
            commit();
            SchedulePrefsCommit();
            DisclosureSettled(key);
            return;
        }

        _pendingExpandSection = null;
        _pendingExpandFolder = null;
        _activeDisclosureBand = range;
        _listController.BeginDisclosure(range,
            open ? ItemDisclosureDirection.Expand : ItemDisclosureDirection.Collapse,
            collapseCommit: open ? null : WithPrefsCommit(commit),
            settled: () => DisclosureSettled(key));
        _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
        BumpDisclosureEpochs(id, folder, range);
    }

    void DisclosureSettled(string key)
    {
        if (!string.Equals(_activeDisclosureKey, key, StringComparison.Ordinal)) return;
        string? id = _activeDisclosureId;
        bool folder = _activeDisclosureIsFolder;
        var band = _activeDisclosureBand;
        _activeDisclosureKey = null;
        _activeDisclosureId = null;
        _activeDisclosureBand = null;
        _pendingExpandSection = null;
        _pendingExpandFolder = null;
        _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
        if (id is not null) BumpDisclosureEpochs(id, folder, band);
    }

    /// <summary>The plan rows a disclosure edge actually re-skins: the folder/section HEADER (its chevron reads the
    /// disclosure state) plus the disclosed BAND (whose rows fade/rise in or out). Every other row in the pane draws
    /// exactly the same thing before and after, so bumping them all — three times per toggle, which is what this
    /// replaced — re-rendered the whole realized window for nothing.</summary>
    void BumpDisclosureEpochs(string id, bool folder, ItemDisclosureRange? band)
    {
        int header = folder ? FolderIndexOf(Plan, id) : SectionHeaderIndexOf(Plan, id);
        if (header >= 0) BumpRowEpoch(header);
        if (band is not { Count: > 0 } b) return;
        int end = Math.Min(b.FirstIndex + b.Count, Plan.Rows.Count);
        for (int i = Math.Max(0, b.FirstIndex); i < end; i++) BumpRowEpoch(i);
    }

    static int SectionHeaderIndexOf(SidebarRowPlan plan, string sectionId)
    {
        var rows = plan.Rows;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Kind == SidebarRowKind.SectionHeader
                && string.Equals(rows[i].SectionId, sectionId, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Rebuild and publish the plan from an INPUT handler (never from render — that would be a backwards
    /// write). Used by the disclosure's prepared-expansion arm so the inserted rows exist on the click frame; every
    /// other path keeps the layout-effect publish.</summary>
    void RepublishNow()
    {
        if (!_planPublished) return;
        TryPublishStage(BuildStage(Config.Document(), _search.Peek(), Config.Edit?.Invoke()));
    }

    /// <summary>Wrap a commit so the coalesced preference write is drained after it (a collapse commits at settle).</summary>
    Action WithPrefsCommit(Action commit) => () => { commit(); SchedulePrefsCommit(); };

    /// <summary>Drain <c>SidebarPreferences</c>' coalesced document write on the NEXT frame. Toggling a folder used to
    /// serialize the whole layout document synchronously inside the click, which is pure latency on the very frame the
    /// expansion has to plan, publish, realize and arm on.</summary>
    void SchedulePrefsCommit()
    {
        if (Prefs is null) return;
        if (_post is { } post) post(_flushPrefsCommit ??= FlushPrefsCommit);
        else FlushPrefsCommit();
    }

    void FlushPrefsCommit() => Prefs?.FlushPendingCommit();

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

    /// <summary>The ANALYTIC extent the virtualizing host seeds row <paramref name="index"/> at, straight from the
    /// published plan (<see cref="SidebarRowExtents.HeightOf"/>). Called only when the host seeds/resizes/splices its
    /// extent table — never per frame — and allocation-free. <c>NaN</c> means "not analytic" (a GridStrip), which the
    /// host reads as "use the estimate and correct on measure".</summary>
    float RowExtentSeed(int index)
    {
        if (!_planPublished) return SidebarRowGeometry.ClassicHeight;
        var rows = Plan.Rows;
        if ((uint)index >= (uint)rows.Count) return SidebarRowGeometry.ClassicHeight;
        return SidebarRowExtents.HeightOf(rows, index, SectionOf(rows[index].SectionId), !Config.ReadOnly);
    }

    /// <summary>Re-derive EVERY seeded extent from the plan just published. Only for a WHOLESALE change (a new document,
    /// a new effective query, an edit-session edge) — the incremental publishes carry their extents across, and a
    /// disclosure's insert/remove is spliced by the host at its known band.</summary>
    void ReseedRowExtents()
    {
        if (_rowLayout.CustomLayout is MeasuredStackVirtualLayout measured)
            measured.Reseed(Plan.Rows.Count);
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
        ConfigureSectionReorder();
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

    /// <summary>PHASE 2 — wire the section-card band. Called from <see cref="ConfigureReorder"/> on every publish, like
    /// the item bands: the Reorderable's state is set as PLAIN FIELDS each render (its documented shape), so a
    /// disarmed band simply reports zero items and lifts nothing.</summary>
    void ConfigureSectionReorder()
    {
        if (_sectionReorder is null && _sectionBand.Count == 0) return;   // never armed — allocate nothing
        var ro = SectionReorder;
        ro.Scene = Context.Scene;
        ro.RequestRender = BumpDisplacement;
        ro.ItemCount = _sectionBand.Count;
        ro.ItemExtent = SidebarPaneMetrics.EditCardHeight;   // ONE height for every card — the uniform pitch the slot math wants
        ro.Spacing = 0f;                                     // plan rows are contiguous inside the virtualized list
        ro.ItemOf = null;                                    // the band never leaves its own list (no cross-list payload)
        ro.OnReorder = CommitSectionMove;
        // The a11y channel for a POINTER-FREE reorder (Phase 4's `Reorderable.AnnounceText`). Without it the keyboard
        // lift — Space to pick up, arrows to place, Space to drop, Escape to cancel, all already built into the control
        // and deliberately not disabled — has NO feedback at all: the displacement is purely visual. Composition is the
        // app's because only the app can name the section and owns the locale; delivery is coalesced for us, so a held
        // arrow key speaks once per ~100 ms rather than per repeat.
        ro.AnnounceText = SectionAnnounce;
    }

    /// <summary>One reorder milestone as a sentence. Runs on the EDGE that triggered it (a lift, a slot change, a
    /// commit) — never per frame — so resolving a localized string here is fine.</summary>
    string? SectionAnnounce(ReorderAnnounce a)
    {
        string name = SectionTitleAtCardSlot(a.Index);
        if (name.Length == 0) return null;
        string where = Loc.Format(SidebarPaneLoc.ReorderPosition, ("index", a.Slot + 1), ("count", a.Count));
        return a.Kind switch
        {
            ReorderAnnounceKind.Grab => Loc.Format(SidebarPaneLoc.ReorderGrabbed, ("name", name), ("position", where)),
            ReorderAnnounceKind.Move => Loc.Format(SidebarPaneLoc.ReorderMoved, ("name", name), ("position", where)),
            ReorderAnnounceKind.Drop => Loc.Format(SidebarPaneLoc.ReorderDropped, ("name", name), ("position", where)),
            _ => Loc.Format(SidebarPaneLoc.ReorderCancelled, ("name", name)),
        };
    }

    string SectionTitleAtCardSlot(int slot)
    {
        string id = SidebarEditPlan.SectionIdAt(Plan.Rows, _sectionBand.Start, _sectionBand.Count, slot);
        var section = id.Length == 0 ? null : SectionOf(id);
        return section is null ? "" : SidebarPaneText.TitleOf(section);
    }

    /// <summary>Commit a section-card drag as the undoable <c>MoveSection</c>. The band-slot → document-index
    /// translation is the pure, unit-tested <c>SidebarEditPlan.ToMoveSection</c> — it is computed against the PERSISTED
    /// document (<c>Prefs.Layout</c>), never against <see cref="Doc"/>, which carries the materialised Shortcuts section
    /// at index 0 and would therefore make every index one too high.</summary>
    void CommitSectionMove(int from, int to)
    {
        if (Config.ReadOnly || Prefs is not { } prefs) return;
        var command = SidebarEditPlan.ToMoveSection(prefs.Layout, Plan.Rows,
            _sectionBand.Start, _sectionBand.Count, from, to);
        if (command is not null) prefs.Dispatch(command);
    }

    /// <summary>The ItemsView's displacement channel, in PLAN-row space: a lifted section's siblings part to make room
    /// while every other row stays put. Stable delegate — <c>ListOptions</c> freezes at mount.</summary>
    (float dx, float dy) Displacement(int planIndex)
    {
        // PHASE 2 — the section-card band first: it is not in `_bands` (see the field note), and while it is lifted no
        // item band can be, because expanding a section disarms it.
        if (_sectionBand.Contains(planIndex) && _sectionReorder is { IsLifted: true } sro)
            return (0f, sro.OffsetFor(planIndex - _sectionBand.Start));
        if (!TryBandOf(planIndex, out var band)) return (0f, 0f);
        var ro = ReorderFor(band.SectionId);
        if (!ro.IsLifted) return (0f, 0f);
        int slot = planIndex - band.Start;
        int from = ro.Core.DraggedIndex;
        int shown = ro.TargetIndex;
        int reachable = ClampReorderSlot(band.SectionId, from, shown);
        // The unclamped case is the whole of Classic, Curated and every pin band: pass the engine's own hint through.
        if (reachable == shown) return (0f, ro.OffsetFor(slot));
        return (0f, SidebarReorderClamp.Offset(slot, from, reachable, band.Extent));
    }

    /// <summary>The slot a live gesture may actually reach — the mode's clamp, or the requested slot when it has none.
    /// One chokepoint for BOTH consumers (the displacement gap and the commit), so the gap can never open where the
    /// drop will not land.</summary>
    int ClampReorderSlot(string sectionId, int from, int to)
    {
        if (Config.ClampReorderSlot is not { } clamp || from < 0 || to < 0) return to;
        return SectionOf(sectionId) is { } section ? clamp(section.Kind, from, to) : to;
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

    /// <summary>Explicit Move up / Move down from a row's context menu (P6: drag is one way, never the only way).
    /// Uses the same commit as an in-place drop when the reorder band is armed, and the pin store directly when that
    /// band is disarmed (an expanded folder in Pinned — the same case the section-card menu already covers).</summary>
    internal void MoveRowByKey(string sectionId, string key, int delta)
    {
        if (delta == 0 || key.Length == 0) return;
        var section = SectionOf(sectionId);
        if (section is null) return;

        var band = BandFor(sectionId);
        if (band.Count > 0)
        {
            int from = -1;
            for (int s = 0; s < band.Count; s++)
                if (string.Equals(KeyAt(sectionId, s), key, StringComparison.Ordinal)) { from = s; break; }
            if (from < 0) return;
            int to = from + delta;
            if ((uint)to >= (uint)band.Count) return;
            Commit(sectionId, from, to);
            return;
        }

        if (section.Kind != SidebarSectionKind.Pinned || Prefs is not { } prefs) return;
        string id = SidebarPinId.Canonical(key) ?? key;
        int pinFrom = prefs.Pins.IndexOf(id);
        int pinTo = pinFrom + delta;
        if (pinFrom < 0 || (uint)pinTo >= (uint)prefs.Pins.Count) return;
        prefs.MovePin(pinFrom, pinTo);
    }

    /// <summary>A same-list drop. WHERE the order lives is the mode's business, so this hands the whole context to
    /// <see cref="SidebarPaneConfig.CommitReorder"/> (default: pin store for Pinned, the undoable <c>MoveItem</c> command
    /// for every other reorderable kind).</summary>
    void Commit(string sectionId, int from, int to)
    {
        var section = SectionOf(sectionId);
        if (section is null) return;
        // The SAME clamp the gap was drawn with (`ClampReorderSlot`): the release point is `ReorderList`'s latest pending
        // slot, which is raw pointer geometry and can sit outside the reachable run even though the shown gap never did.
        // Committing the clamped slot is what makes the drop land where the user was looking.
        to = ClampReorderSlot(sectionId, from, to);
        if (from == to) return;
        // The re-plan this commit is about to trigger must NOT be frozen: it is this gesture's own result (see the
        // latch's remarks), and the drag session stays live across the settle window that follows.
        _publishThroughFreeze = true;
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
        string pinId = SidebarPinId.Canonical(p.Id) ?? p.Id;

        int at = prefs.Pins.IndexOf(pinId);
        if (at >= 0)
        {
            // Removing then inserting shifts every later index down by one, so a downward move lands one slot short
            // without this correction (the Classic rule).
            prefs.MovePin(at, slot > at ? slot - 1 : slot);
            return;
        }
        PinActions.Pin(prefs, pinId, pinKind, p.Uri, p.Name);   // append + the toast whose action unpins
        int now = prefs.Pins.IndexOf(pinId);
        if (now > slot) prefs.MovePin(now, slot);
    }

    /// <summary>One resource target can be both a playlist deposit and a pinned-band insertion. Tracks/albums/playlists
    /// route to the playlist mutation seam; pinnable resources route to the shared pin store.
    ///
    /// <para><b>The rootlist arm publishes a SLOT, not a boolean.</b> <paramref name="rootFacts"/> carries the row's
    /// structural facts (depth, folder state, the next visible row's depth, whether the centre deposits); the
    /// payload-dependent ones (self, ancestor) are folded in at HOVER, because that is the first moment the payload
    /// exists. <see cref="RootlistSlotResolver"/> turns those plus the pointer into one <see cref="SidebarDropSlot"/>,
    /// this publishes it to <see cref="_dropSlot"/>, and the row's line/plate and <c>CommitDrop</c> both CONSUME it.</para></summary>
    internal DropTargetSpec ResourceDropSpec(string sectionId, int slot, string? playlistUri, string? playlistName,
                                             WaveeResourceDragPayload? rootTarget = null,
                                             int rootPlanIndex = -1,
                                             Action? onSpringLoad = null,
                                             string? railCueUri = null,
                                             bool isPlaylistRow = false,
                                             SidebarRowFacts rootFacts = default)
    {
        // "Is this row a playlist AT ALL" — deliberately separate from `playlistUri`, which the callers set only for an
        // EDITABLE one. The distinction is the whole refusal-vs-transparent split below: a playlist you cannot write to
        // owes the user a reason, an album owes them silence.
        bool IsPlaylistRow = isPlaylistRow || playlistUri is { Length: > 0 };
        bool canDepositHere = playlistUri is { Length: > 0 };

        // Is this payload a rootlist FILING (a playlist or folder of the user's own being re-ordered), as opposed to a
        // track set looking for a playlist? The whole slot machinery below is for the first question only.
        bool Filing(WaveeResourceDragPayload source)
            => rootTarget is not null && source.RootlistItem
               && source.Kind is WaveeResourceKind.Playlist or WaveeResourceKind.Folder;

        // The payload-only half of the refusal table — the arms that do not depend on WHERE in the row the pointer is,
        // and therefore the ones that can drive `accepts` (and so the engine's not-allowed cue) rather than a caption.
        SidebarDropRefusal PayloadRefusal(WaveeResourceDragPayload source)
        {
            if (rootTarget is not { } root || !Filing(source)) return SidebarDropRefusal.None;
            if (!RootlistLoaded) return SidebarDropRefusal.NotLoaded;
            // Identity is checked on BOTH keys: rootlist payloads also arrive from outside the sidebar (a tab, a card, a
            // detail hero), where the Id is the entity's uri rather than a sidebar pin id — the Id compare alone would
            // let a playlist be filed relative to its own row.
            if (string.Equals(source.Id, root.Id, StringComparison.Ordinal)
                || (source.Uri.Length > 0 && string.Equals(source.Uri, root.Uri, StringComparison.Ordinal)))
                return root.Kind == WaveeResourceKind.Folder
                    ? SidebarDropRefusal.IntoItself
                    : SidebarDropRefusal.Self;
            if (SourceContainsRow(source, rootPlanIndex)) return SidebarDropRefusal.IntoDescendant;
            return SidebarDropRefusal.None;
        }

        bool Compatible(WaveeResourceDragPayload source)
        {
            if (Filing(source)) return PayloadRefusal(source) == SidebarDropRefusal.None;
            if (canDepositHere && source.CanCopyTracks) return true;
            return slot >= 0 && source.CanPin;
        }

        // The row's structural facts, completed with the two the payload decides. Built per hover because the payload is
        // not knowable earlier; it is a struct copy of ten fields, so it costs nothing inside the 0-alloc frame region.
        SidebarRowFacts FactsFor(WaveeResourceDragPayload source) => rootFacts with
        {
            CenterAccepts = rootFacts.IsFolder || (canDepositHere && source.CanCopyTracks),
            SourceIsSelf = PayloadRefusal(source) is SidebarDropRefusal.Self or SidebarDropRefusal.IntoItself,
            SourceIsAncestorOfRow = PayloadRefusal(source) == SidebarDropRefusal.IntoDescendant,
            RootlistLoaded = RootlistLoaded,
        };

        // WHAT this row would do with this payload, at this pointer position. ONE answer, published once, consumed by
        // the line, the plate, the caption and the commit.
        SidebarDropSlot SlotFor(WaveeResourceDragPayload p, DragSession s)
        {
            if (!Compatible(p)) return SidebarDropSlot.None;
            if (rootTarget is { } root && Filing(p) && rootPlanIndex >= 0)
                return RootlistSlotFor(rootPlanIndex, FactsFor(p), root, p, s.Position);
            // Every other accepted destination — a pin-band insertion, a track deposit, a rail tile — is a whole-row
            // INTO, which is exactly the accent plate this surface has always drawn for them.
            return rootPlanIndex >= 0
                ? new SidebarDropSlot(rootPlanIndex, SidebarDropKind.Into, 0, SidebarDropRefusal.None)
                : SidebarDropSlot.None;
        }

        // The CAPTION is written here rather than through the facade's caption hook because this row's outcome depends
        // on WHERE in it the pointer is, and only the session carries that. Hover runs on both Enter and Over, which is
        // exactly the refresh cadence a pointer-dependent cue needs.
        void Hover(WaveeResourceDragPayload p, DragSession s)
        {
            var cue = SlotFor(p, s);
            _dropSlot.SetIfChanged(cue);
            // The rail's cue is keyed by URI, not by plan index: a rail TILE has no row in the expanded plan.
            if (railCueUri is { Length: > 0 }) _railDropUri.Value = Compatible(p) ? railCueUri : null;
            s.Caption = CaptionFor(p, s, in cue);
        }

        string? CaptionFor(WaveeResourceDragPayload p, DragSession s, in SidebarDropSlot cue)
        {
            // A same-band reorder is this section's own gesture: its feedback is the displacement, and naming it "Pin X"
            // would claim a pin that already exists (Reorderable's own rule, applied to the row-level target too).
            if (s.Payload is ReorderPayload own && ReferenceEquals(own.Owner, ReorderFor(sectionId))) return null;
            if (cue.Refusal != SidebarDropRefusal.None) return RefusalSentence(cue.Refusal);
            if (rootTarget is { } root && Filing(p))
                return RootlistCaption(in cue, root, playlistName);
            if (canDepositHere && p.CanCopyTracks) return Strings.Drag.AddTo(playlistName ?? "");
            if (slot >= 0 && p.CanPin) return Strings.Drag.Pin(p.Name);
            return null;
        }

        void Leave(DragSession _)
        {
            if (rootPlanIndex >= 0 && _dropSlot.Peek().PlanIndex == rootPlanIndex)
                _dropSlot.Value = SidebarDropSlot.None;
            if (railCueUri is { Length: > 0 }
                && string.Equals(_railDropUri.Peek(), railCueUri, StringComparison.Ordinal))
                _railDropUri.Value = null;
        }

        void CommitDrop(WaveeResourceDragPayload source, DragSession s)
        {
            var cue = _dropSlot.Peek();
            Leave(s);
            // D9 — HOISTED TO THE TOP. A SAME-LIST reorder arrives as a ReorderPayload owned by this section's
            // Reorderable, and its own gesture completion commits it. This used to sit BELOW the track-deposit arm, so a
            // pin-band reorder (or a V3 custom-order drag) passing over an editable playlist copied that playlist's
            // songs into it on the way past — a bulk mutation nobody asked for, from a gesture that was a reorder.
            if (slot >= 0 && s.Payload is ReorderPayload rp && ReferenceEquals(rp.Owner, ReorderFor(sectionId))) return;

            if (rootTarget is { } root && Acts is { } rootActs && Filing(source)
                && rootPlanIndex >= 0 && cue.PlanIndex == rootPlanIndex)
            {
                // A refused or unarmed slot commits NOTHING — the reason was already on the chip. Never a fallback
                // placement: guessing is what made "after the last child" land inside the folder.
                CommitRootlistSlot(rootActs, source, root, in cue, playlistUri, playlistName, s.Payload);
                return;
            }
            if (playlistUri is { Length: > 0 } target && Acts is { } acts
                && WaveeResourceDrop.CanDepositTracks(s.Payload))
            {
                WaveeResourceDrop.DepositTracks(acts, target, playlistName ?? "", s.Payload, insertionIndex: null);
                return;
            }
            if (slot >= 0) AcceptForeign(sectionId, s.Payload, slot);
        }

        // ── the "no" is TWO different answers, and conflating them is what made this surface read as broken ────────────
        //
        // TRANSPARENT = "none of my business". A row that was never a track destination — an album, an artist, a show, an
        // app route — must sit the gesture out entirely: the drag is merely CROSSING it on the way somewhere, and a
        // not-allowed cue there is an accusation.
        //
        // REFUSED = "you aimed here, and here is why not". A playlist row the user cannot write to — someone else's, an
        // editorial one — deserves a sentence, because they aimed at a playlist and a playlist is exactly the thing that
        // normally accepts. So does every rootlist filing the tree itself forbids (into yourself, into your own
        // descendant, into a list that has not loaded) — those used to be silent `return false`s in RootlistOps.
        bool Transparent(WaveeResourceDragPayload source)
        {
            // D16 — THE RAIL. A folder drag crossing the collapsed rail's playlist tiles has nothing it could add and
            // nothing it could file there, so the tile sits the gesture out. It used to answer "Nothing to add", which
            // is an accusation aimed at a drag that was only passing through.
            if (railCueUri is { Length: > 0 }
                && SidebarRailDropRules.TileTransparent(source.RootlistItem, source.CanCopyTracks)) return true;
            // A pin-band insertion or a rootlist filing is this row's own business, so it is never transparent.
            if (slot >= 0 && source.CanPin) return false;
            if (Filing(source)) return false;
            // A track-bearing payload over a row that is not a playlist at all: not a destination, not a refusal.
            return source.CanCopyTracks && !IsPlaylistRow;
        }

        string? WhyRefused(WaveeResourceDragPayload source)
        {
            var refusal = PayloadRefusal(source);
            if (refusal != SidebarDropRefusal.None) return RefusalSentence(refusal);
            // Not writable, but it IS a playlist — the refusal that was silent.
            if (IsPlaylistRow && !canDepositHere && source.CanCopyTracks)
                return Loc.Get(Strings.Drag.CantEditPlaylist);
            return canDepositHere && !source.CanCopyTracks
                // Locked decision: an artist has no single obvious track set, so we refuse rather than guess. Future
                // work is a picker that lets the USER choose what to deposit (top tracks / a release).
                ? Loc.Get(source.Kind == WaveeResourceKind.Artist
                    ? Strings.Drag.CantAddArtist
                    : Strings.Drag.NothingToAdd)
                : null;
        }

        return Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
            accepts: Compatible, onDrop: CommitDrop, onEnter: Hover, onOver: Hover, onLeave: Leave,
            transparent: Transparent,
            visualPolicy: DropTargetVisualPolicy.Spotlight,
            refusalCaption: WhyRefused,
            // D15 — an intra-sidebar organisation drag must NOT dim the app it is happening inside. The scrim's promise
            // is "these cutouts are your options", and for a reorder the options are the rows the user is already
            // looking at. The detail page's insertion list took this exemption for same-list drops first.
            spotlightWhen: static s => WaveeResourceDrag.Unwrap(s.Payload) is not { RootlistItem: true },
            // Spring-load (a COLLAPSED folder row supplies the callback): dwelling opens the container so the user can
            // keep travelling into it. It is armed even when this row REFUSES the payload — opening a folder is
            // navigation, not a deposit, and the folder whose contents you are aiming at is often not itself a target.
            springLoadMs: onSpringLoad is null ? 0f : WaveeResourceDrag.SpringLoadMs,
            onSpringLoad: onSpringLoad is null ? null : (_, _) => onSpringLoad());
    }

    /// <summary>The collapsed rail's FOLDER tile: <b>Into, and only Into</b>. A 56-DIP strip has no room for edge bands
    /// and nothing above or below a tile to be "before" or "after" — so this is deliberately NOT the banded row spec.
    /// It exists because a rail folder tile was completely inert (D16): with the pane collapsed there was no way to file
    /// anything at all, and the pane's peek dwell was the only route back to a destination.</summary>
    internal DropTargetSpec RailFolderDropSpec(SidebarLibraryEntry folder, WaveeResourceDragPayload target)
    {
        string folderId = folder.FolderId;
        string cueKey = folder.Id;
        string name = folder.Name;

        bool Accepts(WaveeResourceDragPayload source)
            => source.RootlistItem
               && source.Kind is WaveeResourceKind.Playlist or WaveeResourceKind.Folder
               && RootlistLoaded
               && folderId.Length > 0
               && !string.Equals(source.Id, target.Id, StringComparison.Ordinal)
               && (source.Uri.Length == 0 || !string.Equals(source.Uri, target.Uri, StringComparison.Ordinal))
               && RootlistTreeMoves.Check(RootlistTree, source.Id, folder.Id, RootlistDropPlacement.Inside)
                  == SidebarDropRefusal.None;

        string? Why(WaveeResourceDragPayload source)
        {
            if (!source.RootlistItem || source.Kind is not (WaveeResourceKind.Playlist or WaveeResourceKind.Folder))
                return null;   // handled as TRANSPARENT below — a track drag is merely crossing this tile
            if (!RootlistLoaded) return Loc.Get(Strings.Drag.StillLoading);
            if (string.Equals(source.Id, target.Id, StringComparison.Ordinal))
                return Loc.Get(Strings.Drag.CantMoveIntoItself);
            return RefusalSentence(RootlistTreeMoves.Check(RootlistTree, source.Id, folder.Id,
                                                           RootlistDropPlacement.Inside));
        }

        void Commit(WaveeResourceDragPayload source, DragSession s)
        {
            _railDropUri.Value = null;
            if (Acts is not { } acts || !Accepts(source)) return;
            RootlistItemRef? undoAnchor = null;
            var undoPlacement = RootlistDropPlacement.After;
            if (RootlistUndoAnchors.TryResolve(RootlistTree, source.Id, out var anchor, out var placement))
            {
                undoAnchor = anchor;
                undoPlacement = placement;
            }
            WaveeResourceDrop.MoveRootlist(acts, s.Payload, new RootlistItemRef(folderId, IsFolder: true),
                                           RootlistDropPlacement.Inside, name, undoAnchor, undoPlacement);
        }

        return Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
            accepts: Accepts,
            transparent: static p => !p.RootlistItem,
            caption: _ => Strings.Drag.MoveInto(name),
            refusalCaption: Why,
            onEnter: (p, _) => _railDropUri.Value = Accepts(p) ? cueKey : null,
            onOver: (p, _) => _railDropUri.Value = Accepts(p) ? cueKey : null,
            onLeave: _ => { if (string.Equals(_railDropUri.Peek(), cueKey, StringComparison.Ordinal)) _railDropUri.Value = null; },
            onDrop: Commit,
            visualPolicy: DropTargetVisualPolicy.Spotlight,
            spotlightWhen: static s => WaveeResourceDrag.Unwrap(s.Payload) is not { RootlistItem: true });
    }

    /// <summary>The slot this row publishes for a live drag, or <see cref="SidebarDropSlot.None"/>. A BOUND probe: the
    /// row's insertion line and its Into plate are both single reads of this, so a cue change is a compositor-only
    /// update of two props rather than a re-render of the realized window (the <c>SidebarSelectionPill</c> discipline).
    /// <para>Peek-safe by construction — it reads ONE signal and compares two ints.</para></summary>
    internal SidebarDropSlot DropSlotFor(int planIndex)
    {
        var cue = _dropSlot.Value;
        return planIndex >= 0 && cue.PlanIndex == planIndex ? cue : SidebarDropSlot.None;
    }

    /// <summary>A row's content width: the pane's open width less the padding applied once around the virtualized list.
    /// Read from inside a BOUND prop (the insertion line's width), so it stays a plain signal read.</summary>
    internal float ContentWidth => MathF.Max(0f, ExpandedWidth.Value - SidebarPaneMetrics.PaneInsetH);

    /// <summary>Is the tree showing a non-custom SORT right now? See <see cref="SidebarPaneConfig.TreeSortedNonCustom"/>.</summary>
    internal bool TreeSortedNonCustom => Config.TreeSortedNonCustom?.Invoke() ?? false;

    /// <summary>Has the rootlist actually arrived? "Not known to be loaded" must never present as "is": a filing written
    /// against an empty tree would land at an index that means nothing.</summary>
    bool RootlistLoaded
        => Prefs?.Binder?.CurrentInput is { TreeState: SidebarSourceState.Ready, PlaylistTree.Count: > 0 };

    /// <summary>The DEPTH-FIRST FLATTENED rootlist tree the projection is currently publishing — the full one, not the
    /// expansion-filtered plan. Legality and the undo anchor are both decided against real sibling order, never against
    /// whichever rows happen to be visible.</summary>
    IReadOnlyList<SidebarLibraryEntry>? RootlistTree => Prefs?.Binder?.CurrentInput.PlaylistTree;

    /// <summary>One localized sentence per refusal — the ONE table, shared by the accept-time <c>WhyRefused</c> and the
    /// position-dependent caption, so a refusal can never be explained with a reason the test did not use.</summary>
    static string? RefusalSentence(SidebarDropRefusal refusal) => refusal switch
    {
        SidebarDropRefusal.Self => Loc.Get(Strings.Drag.CantMoveHere),
        SidebarDropRefusal.IntoItself or SidebarDropRefusal.IntoDescendant => Loc.Get(Strings.Drag.CantMoveIntoItself),
        SidebarDropRefusal.NoOp => Loc.Get(Strings.Drag.AlreadyThere),
        SidebarDropRefusal.SortedList => Loc.Get(Strings.Drag.ClearSortingToReorder),
        SidebarDropRefusal.NotLoaded => Loc.Get(Strings.Drag.StillLoading),
        SidebarDropRefusal.Unavailable => Loc.Get(Strings.Drag.CantMoveHere),
        _ => null,
    };

    /// <summary>What an ARMED rootlist slot says. Before/After AT THE ROW'S OWN DEPTH say nothing — the line is already
    /// under the pointer and naming an ordering would narrate what the user can see. The two that DO carry a sentence
    /// are the ones the line alone cannot disambiguate: an outdent, and the end of the list.</summary>
    string? RootlistCaption(in SidebarDropSlot cue, WaveeResourceDragPayload root, string? playlistName)
    {
        switch (cue.Kind)
        {
            case SidebarDropKind.Into:
                return root.Kind == WaveeResourceKind.Folder
                    ? Strings.Drag.MoveInto(root.Name)
                    : Strings.Drag.AddTo(playlistName ?? root.Name);
            case SidebarDropKind.EndOfList:
                return Loc.Get(Strings.Drag.MoveToEnd);
            case SidebarDropKind.After when TryRowEntry(cue.PlanIndex, out var entry) && cue.Depth < entry.Depth:
                return Strings.Drag.MoveOutOf(entry.ParentFolderName.Length > 0
                    ? entry.ParentFolderName
                    : Loc.Get(Strings.Sidebar.YourLibrary));
            default:
                return null;
        }
    }

    /// <summary>Pointer + row facts → the published slot. Keeps the viewport/scroll math that resolved a row-relative
    /// <c>t</c>, ADDS the x channel the old placement never read (D5), delegates the geometry to the pure resolver, and
    /// finally checks the resolved destination against real sibling order so "already there" and "into your own
    /// subtree" refuse WHERE THE CUE IS instead of silently failing three layers down.</summary>
    SidebarDropSlot RootlistSlotFor(int planIndex, SidebarRowFacts facts, WaveeResourceDragPayload root,
                                    WaveeResourceDragPayload source, Point2 pointer)
    {
        var viewport = _listController.Viewport;
        var scene = Context.Scene;
        if (planIndex < 0 || scene is null || viewport.IsNull || !scene.IsLive(viewport))
            // DEGENERATE (D17): no plan row, no viewport, no scene. Refuse with a reason — the old code guessed
            // Before/Inside here, which is a placement the user never aimed at.
            return new SidebarDropSlot(planIndex, SidebarDropKind.None, 0, SidebarDropRefusal.Unavailable);

        var rect = scene.AbsoluteRect(viewport);
        float contentY = pointer.Y - rect.Y + _listController.ScrollOffset;
        float top = SidebarRowGeometry.ContentYOf(planIndex, Plan.Rows.Count, RowExtentOf);
        float extent = MathF.Max(1f, RowExtentOf(planIndex));
        float t = Math.Clamp((contentY - top) / extent, 0f, 1f);
        // The list is the padded box's only child, so the viewport's left edge IS the row's left edge.
        float xInRow = pointer.X - rect.X;

        var cue = RootlistSlotResolver.Resolve(planIndex, t, xInRow, extent, in facts, _dropSlot.Peek());
        if (!cue.IsArmed) return cue;
        if (!TryMapSlot(in cue, root, out var target)) return cue;
        if (target.Deposit) return cue;                     // a track copy is not a rootlist ORDERING at all
        var refusal = RootlistTreeMoves.Check(RootlistTree, source.Id, target.EntryId, target.Placement);
        return refusal == SidebarDropRefusal.None
            ? cue
            : new SidebarDropSlot(planIndex, SidebarDropKind.None, cue.Depth, refusal);
    }

    /// <summary>A resolved rootlist destination: the tree entry it is expressed against, the seam ref + placement, and
    /// the folder the item will end up in (for the confirmation toast). <see cref="Deposit"/> marks the one slot that is
    /// not a rootlist move at all — the retained "drop a playlist on a playlist's centre = copy its songs" gesture.</summary>
    readonly record struct RootlistSlotTarget(string EntryId, RootlistItemRef Ref, RootlistDropPlacement Placement,
                                              string ParentName, bool Deposit);

    /// <summary>THE slot → mutation map (design §"Slot → mutation"). ONE owner, called at hover for legality and at drop
    /// for the commit, so the cue and the mutation cannot describe different destinations.</summary>
    bool TryMapSlot(in SidebarDropSlot cue, WaveeResourceDragPayload root, out RootlistSlotTarget target)
    {
        target = default;
        // The tree's end marker is CHROME: it stands for no entity, so it is resolved before the row lookup. Its anchor
        // is the last TOP-LEVEL entry, whose exclusive range end lands after a trailing folder's whole subtree — which
        // is exactly what makes "below everything" reachable at all.
        if (cue.Kind == SidebarDropKind.EndOfList)
        {
            if (!TryLastTopLevelEntry(out var last)) return false;
            target = new RootlistSlotTarget(last.Id, RefOf(in last), RootlistDropPlacement.After, "", false);
            return true;
        }
        if (!TryRowEntry(cue.PlanIndex, out var entry)) return false;
        switch (cue.Kind)
        {
            case SidebarDropKind.Into:
                if (root.Kind != WaveeResourceKind.Folder)
                {
                    target = new RootlistSlotTarget(entry.Id, default, RootlistDropPlacement.Inside, entry.Name, true);
                    return true;
                }
                target = new RootlistSlotTarget(entry.Id, RefOf(in entry), RootlistDropPlacement.Inside, entry.Name, false);
                return true;

            case SidebarDropKind.Before when cue.Depth > entry.Depth:
                // The bottom band of an EXPANDED folder header: the precise "first child" slot. The next plan row IS
                // that first child, so the filing is expressed against it.
                if (!TryRowEntry(cue.PlanIndex + 1, out var firstChild)) return false;
                target = new RootlistSlotTarget(firstChild.Id, RefOf(in firstChild), RootlistDropPlacement.Before,
                                                entry.Name, false);
                return true;

            case SidebarDropKind.Before:
                target = new RootlistSlotTarget(entry.Id, RefOf(in entry), RootlistDropPlacement.Before,
                                                entry.ParentFolderName, false);
                return true;

            case SidebarDropKind.After when cue.Depth < entry.Depth:
                // THE D2 FIX. "After the last child of a folder", aimed left, means AFTER THE FOLDER — the same shape
                // FolderActions.MoveOut builds. The old code expressed it against the child and landed back inside.
                if (!TryAncestorFolder(in entry, entry.Depth - cue.Depth, out var ancestor)) return false;
                target = new RootlistSlotTarget(ancestor.Id, RefOf(in ancestor), RootlistDropPlacement.After,
                                                ancestor.ParentFolderName, false);
                return true;

            case SidebarDropKind.After:
                target = new RootlistSlotTarget(entry.Id, RefOf(in entry), RootlistDropPlacement.After,
                                                entry.ParentFolderName, false);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Commit the PUBLISHED slot. Never recomputes one: the cue the user was shown is the mutation that is
    /// issued, which is the whole point of publishing it.</summary>
    void CommitRootlistSlot(ActionServices acts, WaveeResourceDragPayload source, WaveeResourceDragPayload root,
                            in SidebarDropSlot cue, string? playlistUri, string? playlistName, object? payload)
    {
        if (!cue.IsArmed || !TryMapSlot(in cue, root, out var target)) return;
        if (target.Deposit)
        {
            // The retained centre gesture. It still needs a WRITABLE playlist under the pointer; the row only offers the
            // centre when it has one, so this is the belt to that braces.
            if (playlistUri is { Length: > 0 } uri) WaveeResourceDrop.DepositTracks(acts, uri, playlistName ?? "", payload, null);
            return;
        }
        // Captured BEFORE the mutation: once the rootlist has moved, where the item used to be is unknowable.
        RootlistItemRef? undoAnchor = null;
        var undoPlacement = RootlistDropPlacement.After;
        if (RootlistUndoAnchors.TryResolve(RootlistTree, source.Id, out var anchor, out var placement))
        {
            undoAnchor = anchor;
            undoPlacement = placement;
        }
        WaveeResourceDrop.MoveRootlist(acts, payload, target.Ref, target.Placement, target.ParentName,
                                       undoAnchor, undoPlacement);
    }

    static RootlistItemRef RefOf(in SidebarLibraryEntry entry) => RootlistTreeNav.RefOf(in entry);

    /// <summary>The entry behind one plan row, or false for a chrome row.</summary>
    bool TryRowEntry(int planIndex, out SidebarLibraryEntry entry)
    {
        entry = default;
        var rows = Plan.Rows;
        var entries = Plan.Entries;
        if ((uint)planIndex >= (uint)rows.Count) return false;
        int at = rows[planIndex].EntryIndex;
        if ((uint)at >= (uint)entries.Count) return false;
        entry = entries[at];
        return true;
    }

    /// <summary>Walk <paramref name="levels"/> containing folders up from <paramref name="entry"/>.</summary>
    bool TryAncestorFolder(in SidebarLibraryEntry entry, int levels, out SidebarLibraryEntry folder)
    {
        folder = entry;
        for (int i = 0; i < levels; i++)
        {
            if (folder.ParentFolderId is not { Length: > 0 } parent || !TryFolderEntry(parent, out folder)) return false;
        }
        return levels > 0 && folder.IsFolder;
    }

    bool TryFolderEntry(string folderId, out SidebarLibraryEntry folder)
    {
        var entries = Plan.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.IsFolder && string.Equals(e.FolderId, folderId, StringComparison.Ordinal)) { folder = e; return true; }
        }
        folder = default;
        return false;
    }

    /// <summary>The last TOP-LEVEL tree entry — the anchor "move to the end" files against. Its exclusive range end
    /// lands after a trailing folder's whole subtree, which is precisely what makes the end of the list reachable.</summary>
    bool TryLastTopLevelEntry(out SidebarLibraryEntry entry)
    {
        var tree = RootlistTree;
        entry = default;
        if (tree is null) return false;
        for (int i = tree.Count - 1; i >= 0; i--)
            if (tree[i].Depth == 0) { entry = tree[i]; return true; }
        return false;
    }

    /// <summary>Does the dragged FOLDER contain the row at <paramref name="planIndex"/>? Walks the row's
    /// <c>ParentFolderId</c> chain — the cycle guard, lifted from three layers down in <c>RootlistOps</c> (where it could
    /// only fail silently) to where the cue is drawn.</summary>
    bool SourceContainsRow(WaveeResourceDragPayload source, int planIndex)
    {
        if (source.Kind != WaveeResourceKind.Folder) return false;
        string folderId = SidebarPinId.FolderIdOf(source.Id);
        if (folderId.Length == 0 || !TryRowEntry(planIndex, out var entry)) return false;
        for (int guard = 0; guard < 32; guard++)
        {
            if (entry.ParentFolderId is not { Length: > 0 } parent) return false;
            if (string.Equals(parent, folderId, StringComparison.Ordinal)) return true;
            if (!TryFolderEntry(parent, out entry)) return false;
        }
        return false;
    }

    // ── drag peek ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The rail's spring-load band: dwell 250ms with a sidebar-relevant payload ⇒ present the pane expanded for
    /// the rest of the gesture. A pure waypoint (<c>springLoadOnly</c>) — it accepts nothing itself, so the rail's own
    /// tiles keep every deposit.
    /// <para>Armed only for a payload that could actually LAND somewhere in the sidebar. Opening the pane for a drag it
    /// has no destination for would be motion that promises something, which is the class of bug this whole change is
    /// fixing.</para></summary>
    DropTargetSpec RailPeekDropSpec() => Drop.Target<WaveeResourceDragPayload>(
        WaveeDragKinds.Resource,
        accepts: static _ => false,          // never a destination — the tiles and the expanded rows are
        springLoadOnly: true,
        springLoadMs: DragPeekMs,
        onSpringLoad: (p, _) => { if (p.CanCopyTracks || p.CanPin) SetDragPeek(true); });

    /// <summary>Dwell before a collapsed pane peeks open. Shorter than <c>WaveeResourceDrag.SpringLoadMs</c> (500ms, the
    /// macOS/WinUI spring-loaded-container convention) because the two gestures differ: opening a FOLDER commits you to a
    /// container you may not have meant, whereas this only reveals labels for destinations that were already there.</summary>
    internal const float DragPeekMs = 250f;

    internal void SetDragPeek(bool on)
    {
        if (_dragPeek.Peek() != on) _dragPeek.Value = on;
    }

    /// <summary>Disarm every row's cue. Called on SESSION END — a drop that lands outside the pane never reaches a row's
    /// <c>OnLeave</c>, and a line left painted after the gesture is over reads as a pending action that is not pending.</summary>
    internal void ClearDropSlot()
    {
        if (_dropSlot.Peek().PlanIndex >= 0) _dropSlot.Value = SidebarDropSlot.None;
    }

    /// <summary>Owns the ONE <c>UseDragState()</c> subscription that ends a <see cref="_dragPeek"/>, disarms the drop
    /// cue and flushes the mid-drag freeze. Renders nothing.
    /// <para>All three happen on SESSION END, never on leave: once the pane is open the pointer travels off the rail and
    /// onto the rows, and collapsing there would yank every target out from under it mid-gesture. Session end covers
    /// drop, cancel and Escape alike, so there is no path that leaves the pane stuck open or a projection stranded.</para></summary>
    sealed class SidebarDragPeekWatcher(SidebarPane owner) : Component
    {
        public override Element Render()
        {
            // Active stays true across the ~250ms settle window a Stationary lift publishes, so the pane holds its peek
            // until the chip has finished animating home rather than snapping shut under it.
            bool active = UseDragState().Active;
            // A LAYOUT EFFECT, not the render body: every one of these writes a signal (the peek, the cue, the plan the
            // flush publishes), and a signal written during render is the one thing the reactive core will not tolerate.
            // The dep is the ACTIVE EDGE, so the effect — and therefore the flush — runs exactly once per session end.
            UseLayoutEffect(() =>
            {
                if (active) return;
                owner.SetDragPeek(false);
                owner.ClearDropSlot();
                owner.FlushDeferredStage();
            }, active ? 1 : 0);
            // A session that never ends within the pane's lifetime (the shell swaps designs mid-drag) would otherwise
            // leave a stage parked in a bay nobody will ever flush.
            UseEffect(() => (Action?)(() => owner.DiscardDeferredStage()), DepKey.Empty);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
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

    // ── PHASE 2 / Decision B — the canvas's own commands ─────────────────────────────────────────────────────────────

    /// <summary>The shared edit session, or null. Reached through <c>SidebarPreferences</c> — the app-root service both
    /// the canvas and the companion page hold — which IS the shared-state seam: the pane never touches the customizer
    /// page and the page never touches the renderer (iron rule 6).</summary>
    internal SidebarEditSession? EditSession => Prefs?.Edit;

    /// <summary>The host the per-section options popover hands to the customizer's generated control set. The session
    /// itself implements it, so the popover and the companion page drive one document through one mutation path.</summary>
    internal ISidebarEditHost? EditHost => Prefs?.Edit;

    /// <summary>Expand this card's section (or collapse it when it is already the expanded one). SESSION state, not
    /// document state: it is deliberately NOT <c>SetSectionCollapsed</c>, because a user opening a card to look inside
    /// it must not silently rewrite — and fill the undo ring with — the collapse state their real sidebar uses.
    /// <para>It therefore does not run the disclosure choreography either: <c>StartDisclosure</c> resolves its animated
    /// range from a <c>SectionHeader</c> row, which the canvas does not plan (the card replaces it). The re-plan rides
    /// the ordinary <c>PlanDep</c> fold.</para></summary>
    internal void ToggleEditExpanded(string sectionId) => EditSession?.ToggleExpanded(sectionId);

    /// <summary>Is this card's section currently showing its rows? Reads the session THE PUBLISHED PLAN WAS BUILT FROM,
    /// never the live signal, so the chevron can never claim "open" over a plan with no body under it.</summary>
    internal bool EditShowsBody(SidebarSectionSpec section)
        => Edit is { } edit && SidebarEditPlan.ShowsBody(in edit, section);

    /// <summary>The eye: hide or show a section. Undoable, autosaved, and instantly visible in the canvas — a hidden
    /// section keeps its card, dimmed, with an eye-off badge (P2: nothing vanishes into an invisible elsewhere).</summary>
    internal void SetSectionHidden(string sectionId, bool hidden)
    {
        if (Config.ReadOnly || SidebarEditPlan.IsPinnedCard(sectionId)) return;
        Prefs?.Dispatch(new SetSectionHidden(sectionId, hidden));
    }

    /// <summary>Explicit Move up / Move down for a card's "…" menu — P6: drag is one of several ways, never the only
    /// one. It works whatever the drag band's state is (it addresses the document, not a band slot), which is what makes
    /// a section reorderable even while another card is expanded and the band is disarmed.</summary>
    internal void MoveSectionBy(string sectionId, int delta)
    {
        if (Config.ReadOnly || delta == 0 || Prefs is not { } prefs) return;
        if (SidebarEditPlan.IsPinnedCard(sectionId)) return;   // the sentinel is not in `Sections`
        var at = prefs.Layout.Locate(sectionId);
        if (at.Index < 0) return;
        int siblings = at.Parent is null ? prefs.Layout.Sections.Count : at.Parent.ChildList.Count;
        int next = at.Index + delta;
        if (next < 0 || next >= siblings) return;
        prefs.Dispatch(new MoveSection(sectionId, at.Parent?.Id, next));
    }

    /// <summary>Remove a section from the card's "…" menu. Explicit and undoable — the only path that deletes
    /// (iron rule 9).</summary>
    internal void RemoveEditSection(string sectionId)
    {
        if (Config.ReadOnly || SidebarEditPlan.IsPinnedCard(sectionId)) return;
        if (Prefs?.Dispatch(new RemoveSection(sectionId)) != SidebarRejectReason.None) return;
        if (EditSession is { } session
            && string.Equals(session.Expanded.Peek(), sectionId, StringComparison.Ordinal))
            session.Expanded.Value = null;
    }

    /// <summary>PHASE 3 — is there room for one more section? Read by the section card's palette DROP TARGET, whose
    /// <c>CanAccept</c> runs once per opt-in target per frame while a drag is live (the dnd skill's per-frame-alloc
    /// rule), so it is a count comparison and nothing else.</summary>
    internal bool CanAcceptPaletteDrop
        => !Config.ReadOnly && Prefs is { } prefs
           && prefs.Layout.SectionCount < SidebarLayoutReducer.MaxSections;

    /// <summary>PHASE 3 — commit a palette chip dropped ON a section card as the undoable <c>AddSection</c>. The band
    /// slot → document index translation is the pure, unit-tested <c>SidebarEditPlan.ToAddSection</c>, computed against
    /// the PERSISTED document (<c>Prefs.Layout</c>) and never against <see cref="Doc"/>, which carries the materialised
    /// Shortcuts section at index 0 and would make every index one too high.</summary>
    internal void AddSectionFromPalette(string beforeSectionId, SidebarSectionDropPayload payload)
    {
        if (Config.ReadOnly || Prefs is not { } prefs) return;
        var command = SidebarEditPlan.ToAddSection(prefs.Layout, beforeSectionId, payload);
        if (command is not null) prefs.Dispatch(command);
    }

    /// <summary>Duplicate a section from the card's "…" menu. <paramref name="copyTitle"/> is the localized "{name} copy"
    /// the caller formats — Wavee.Core has no <c>Loc</c>, so the clone's literal title is supplied here.</summary>
    internal void DuplicateEditSection(string sectionId, string copyTitle)
    {
        if (Config.ReadOnly || SidebarEditPlan.IsPinnedCard(sectionId)) return;
        Prefs?.Dispatch(new DuplicateSection(sectionId, copyTitle));
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

    /// <summary>DROP TO CREATE: make a playlist out of the thing being dragged.
    /// <para>"What if I want to make a new playlist with that song?" (user report 2026-08-10). Seeding a playlist from a
    /// song is how playlists actually get born — you hear something and it is the first member of an idea that has no
    /// name yet — and the capability existed only three levels into a right-click menu, reachable from no drop target
    /// anywhere. The destination is the PlaylistTree section's existing "Create playlist" row: it is ALWAYS present and
    /// already labelled, so unlike a drag-only row materialising in the list it costs no reflow mid-gesture and no new
    /// chrome. On a collapsed pane the drag peek opens the pane within 250ms, which is what puts this row in reach.</para>
    /// <para>Create-then-add is still two operations (Spotify exposes no atomic "create with items"), so a failed add can
    /// leave an empty playlist behind — pre-existing, and surfaced through <c>PlaylistEditErrors</c> rather than hidden.
    /// The create half is no longer awaited: it is the synchronous seam, so the deposit starts in the same gesture.
    /// The confirmation spends its action on OPEN, not Undo: a brand-new playlist needs a name, and inline rename lives on
    /// its page.</para></summary>
    internal void CreatePlaylistFromDrag(object? payload)
    {
        if (Acts is not { } acts || acts.Library is null) return;
        if (WaveeResourceDrag.Unwrap(payload) is not { CanCopyTracks: true }) return;
        // The ONE create path: numbered name, optimistic row in the store before this returns, CreateFailed observed.
        if (PlaylistCreateFlow.Create(acts, default, navigate: false, out string name) is not { } created) return;
        string uri = created.Uri;
        var post = acts.Post;
        _ = Run();

        async Task Run()
        {
            // Silent deposit: this caller owns the confirmation, and two toasts for one gesture reads as a bug.
            bool ok = await WaveeResourceDrop.DepositTracksSilentAsync(acts, uri, payload, insertionIndex: null)
                                             .ConfigureAwait(false);
            if (!ok) return;   // DepositTracksAsync already toasted the mapped failure
            Post(post, () =>
            {
                Menus.RememberDeposit(acts, uri);
                Toast.Show(Strings.Detail.AddedToPlaylist(name), new ToastOptions
                {
                    Severity = InfoBarSeverity.Success,
                    ActionLabel = Loc.Get(Strings.Detail.GoToPlaylist),
                    OnAction = () => Navigate("pl:" + uri, name),
                });
            });
        }

        static void Post(Action<Action>? post, Action a) { if (post is not null) post(a); else a(); }
    }

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
