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
        _ = _planVersion.Value;
        int disclosureUiVersion = _disclosureVersion.Value;
        UseLayoutEffect(() => TryPublishStage(stage),
            DepKey.From(HashCode.Combine(stage.Epoch, disclosureUiVersion)));
        // AFTER the plan is published: resolving the travel direction needs the plan the rows are about to render from.
        // This also SUBSCRIBES the pane to the route, so a navigation re-renders it (and therefore re-renders the row
        // indicators, which read these fields) without re-planning — PlanDep deliberately excludes the route.
        TrackSelection(_route.Value.Name);
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

        var children = new List<Element>(2) { expanded };
        if (!_inDrawer)
            children.Add(new BoxEl
            {
                Key = "compact-layer", Direction = 1, Grow = 1f, Shrink = 0f, Width = 56f,
                Opacity = compact ? 1f : 0f, HitTestVisible = compact,
                // As Classic always did: the rail's overlay scrollbar occupies the same gutter as the shell's resize seam
                // and reads as a page-spanning border, so wheel/touch scrolling stays and the bar does not paint.
                Children = [ScrollView(SidebarPaneRail.Build(this, _railPlan)) with
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
        Doc = stage.Document;
        Plan = stage.Pane;
        _railPlan = stage.Rail;
        _effectiveSearch = stage.EffectiveSearch;
        _presentedUsesA = stage.UsesA;
        _planPublished = true;
        RebuildIndex(Plan);
        ConfigureReorder();
        if (!notify) return;

        void PublishSignals()
        {
            _rowCount.Value = Plan.Rows.Count;
            _planVersion.Value = _planVersion.Peek() + 1;
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
    }

    void DisclosureSettled(string key)
    {
        if (!string.Equals(_activeDisclosureKey, key, StringComparison.Ordinal)) return;
        _activeDisclosureKey = null;
        _activeDisclosureId = null;
        _pendingExpandSection = null;
        _pendingExpandFolder = null;
        _disclosureVersion.Value = _disclosureVersion.Peek() + 1;
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

    /// <summary>The live selected route. Reading it SUBSCRIBES the caller — a row slot, or this pane for the rail.</summary>
    internal string SelectedRoute => _route.Value.Name;

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
            return WaveeResourceDragPayload.FromEntry(e, Acts?.Svc);
        }
        // A hand-placed row (a route shortcut / an unresolved entity): its Key is its identity, which is also its pin id
        // for every pinnable form.
        var destination = SidebarDestination.FromRoute(row.Key, null, "");
        return destination is { } d
            ? WaveeResourceDragPayload.FromDestination(d, Acts?.Svc)
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
                                             int rootPlanIndex = -1)
    {
        bool Compatible(object? payload)
        {
            var source = WaveeResourceDrag.Unwrap(payload);
            if (source is null) return false;
            if (rootTarget is not null && source.RootlistItem
                && source.Kind is WaveeResourceKind.Playlist or WaveeResourceKind.Folder)
                return !string.Equals(source.Id, rootTarget.Id, StringComparison.Ordinal);
            if (playlistUri is { Length: > 0 } && source.CanCopyTracks) return true;
            return slot >= 0 && source.CanPin;
        }

        void Hover(DragSession s) => _resourceDropRow.Value = Compatible(s.Payload) ? rootPlanIndex : -1;
        void Leave(DragSession _) { if (_resourceDropRow.Peek() == rootPlanIndex) _resourceDropRow.Value = -1; }

        void CommitDrop(DragSession s)
        {
            Leave(s);
            var source = WaveeResourceDrag.Unwrap(s.Payload);
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

        return new DropTargetSpec([WaveeDragKinds.Resource], Hover, Hover, Leave, CommitDrop)
        {
            CanAccept = s => Compatible(s.Payload),
            VisualPolicy = DropTargetVisualPolicy.Spotlight,
        };
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
