using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

// The Wavee shell root — the WaveeMusic 4-row chrome (tab strip + caption · toolbar · sidebar + content card; player bar
// deferred). Owns the route as SIGNALS so the toolbar/sidebar/tabs all react, plus the open-tab list. Builds the frame
// ONCE and never remounts it for theming: a theme/palette switch bumps Tok.Epoch and the host's RethemeAll re-renders
// every component and re-fires every bound fill IN PLACE (there is no theme-keyed OverlayHost remount) — which is why
// chrome fills must be Prop.Of binds or render-time token reads, never values frozen into ctor args at mount. Each
// chrome piece re-renders itself from the signals it reads. Mounted by WaveeApp inside Services + PlaybackBridge.
sealed class WaveeShell : Component
{
    // One open browser-style tab: stable identity + route key, strip label/glyph, and route Arg (playlist display name).
    private sealed record OpenTab(int Id, string Key, string Label, string Glyph, string? Arg);

    readonly Signal<Route> _route = new(new Route("home"));
    readonly Signal<NavTransitionKind> _navMotion = new(NavTransitionKind.Forward);
    readonly Signal<bool> _canBack = new(false);
    readonly Signal<bool> _canForward = new(false);
    const int MaxBackStack = 200;   // bound the in-memory back/forward stacks over a long session (the persisted HistoryStore keeps its own 500-entry cap)
    readonly List<Route> _history = new();
    readonly List<Route> _forwardHistory = new();
    readonly HistoryStore _historyStore = new();
    readonly NavPreviewStore _navPreview = new();   // click→detail handoff: the card stashes its known cover/title/artist
    readonly HomeSectionPreviewStore _homeSectionPreview = new(); // Home source section → seeded drill page
    // Page-scoped shell MATERIAL: a page writes its art colour (flat tint) or Home's three washes here while active; the
    // shell paints it as the one layer between the window's base layer (live Mica) and the chrome column. Null tint +
    // null wash ⇒ the bare base. Owner-gated writes (ShellMaterialState) make A→B navigation race-free. Provided at the root via
    // ShellMaterial.Slot; rendered by ShellMaterialLayer.
    readonly Signal<ShellMaterialState> _shellMaterial = new(default);

    // Right-rail (lyrics / queue / now-playing panels) UI state — created here, provided via ShellUi.Slot, and
    // toggled from the player bar. The rail reserves inline width when it fits; otherwise it floats over the content.
    readonly ShellUi _shellUi = new();

    // The signals-first action system's ambient service bag (Actions/ActionServices.cs): ONE reference-stable instance
    // provided at the root next to NavCtx; fields are refreshed each render, Overlay is bound inside the OverlayHost
    // subtree (ActionServicesOverlayBinder). Context, not ctor args — the component-props-freeze contract.
    readonly ActionServices _actions = new();

    // ── The shell-wide "drop a file to play it" target (P4) ──────────────────────────────────────────────────────────
    // Allocated ONCE (the PlaylistInlineEdit precedent: a per-render spec would churn the scene's drop-target column) and
    // hung on the full-bleed `tinted` layer, which is an ancestor of the whole chrome column. The engine hands a drop to
    // the DEEPEST accepting target under the pointer (DragDropContext.FindTarget), so the per-row .mp4 attach targets in
    // DetailTracks still win for a drop that lands on a track row; only drops on the rest of the window reach this.
    readonly Signal<bool> _fileDropOver = new(false);
    DropTargetSpec? _fileDrop;

    int _nextTabId = 1;
    readonly List<OpenTab> _open = new() { new OpenTab(0, "home", Loc.Get(Strings.Nav.Home), Icons.Home, null) };
    readonly Signal<int> _tabsVersion = new(0);
    readonly Signal<int> _selectedTab = new(0);

    // ── the MERGED chrome row (one 48-DIP TitleBar: nav + tabs · window-centred search · identity) ────────────────────
    // The row is priority-collapsed by a pure ladder (MergedChromeLayout) held here as a band-gated signal: it is
    // recomputed on every viewport move but only PUBLISHED when a stage flips, exactly like the two-row toolbar's old
    // ToolbarLayout. Everything the bar's islands read comes from these signals, never from a frozen ctor arg.
    readonly Signal<MergedChromeLayout> _chromeLayout = new(MergedChromeLayout.FromWidth(0f, 1));
    readonly Signal<bool> _searchExpanded = new(false);     // the icon-mode search's click-expand latch
    readonly Signal<int> _searchFocusRequest = new(0);      // Ctrl+K → put the caret in the field (a monotonic ticket)
    // The strip shows only the KeepTabs tabs the ladder allows, so it has its OWN index space (the visible slice). The
    // projection effect in Render derives both from _selectedTab / _tabMru / KeepTabs; nothing else writes them.
    readonly Signal<int> _stripSelected = new(0);
    readonly Signal<int> _tabStripVersion = new(0);         // bumped when the visible SLICE changes, not just the tab set
    readonly List<int> _visibleTabs = new();                // open-list indices in the strip, in OPEN order
    readonly List<int> _tabMru = new();                     // tab IDs, most-recently-used first — who survives a squeeze
    readonly HashSet<int> _keepIds = new();                 // RebuildVisibleTabs scratch (cold path, reused)
    readonly List<ChromeTabRef> _hiddenScratch = new();     // HiddenTabs scratch — copied out by the chevron immediately
    MergedChromeRow? _chrome;
    TabStrip? _strip;                                       // the MOUNTED strip: its Min/MaxTabWidth track the ladder

    readonly Signal<string> _searchText = new("");
    // Sidebar state. Collapsed and (once pinned by a drag) width are seeded by SidebarPreferences' own constructor, from
    // the ACTIVE design's keys, so the FIRST layout already uses the saved values — no startup animation; written back on
    // change via SaveSidebar, which delegates to that service.
    readonly IAppSettings _settings;
    readonly Signal<bool> _drawerExpanded = new(false);
    Signal<bool>? _narrowShellState;
    Signal<bool>? _narrowDrawerState;
    Signal<bool>? _presentedCompactState;
    // The sidebar's PANE STATE now belongs to SidebarPreferences (one owner for all three designs, per-design remembered
    // state, and the same instance the Settings page + customizer write). These two fields are ALIASES of that service's
    // signals, kept under their historical names so every binding/read below is unchanged — the shell binds them, it no
    // longer owns them. SwitchDesign writes a new VALUE into the same signal instances, never new signals, so every bound
    // prop stays live across a design switch.
    readonly SidebarPreferences _sidebar;
    readonly Signal<bool> _sidebarCompact;                      // == _sidebar.Collapsed (the user's collapse preference)
    readonly Signal<float> _sidebarWidth;                       // == _sidebar.Width (expanded width, drag-resizable, persisted)
    readonly Signal<bool> _sidebarDragging = new(false);       // ON during a seam drag → snaps all layout transitions (1:1 resize)
    // The width-pin latch lives on the service now (per DESIGN — pinning V3's width must not freeze Classic's ladder), so
    // the effect below reads _sidebar.WidthUserSet instead of a shell field.
    bool _navPaneTierSeeded;                                   // false until the first effect run with a real viewport width
    SidebarDesign _tierDesign;                                 // which design the tier ladder is currently seeded against
    readonly Signal<float> _sidebarFade = new(1f);             // content-opacity cue as a resize nears the collapse detent
    Action<float>? _requestTheme;                              // ambient ThemeControl.Request: live animated re-theme (captured in Render)

    // (The rail layout-defer lock that used to live here is gone — see the note in ShellUi. The projected path commits
    // the reserved width in one frame, so there is nothing to debounce, and deferring the breakpoints only guaranteed a
    // window where the wide column set was rendered into the narrow pane.)
    // Interactive grip drag owns geometry and therefore suppresses projection globally while the pointer is down. Rail
    // and collapse toggles must NOT use this gate: doing so cancels their own Reveal/FLIP tracks. Those commits use the
    // scoped SuppressDescendantTransitions contract on the projected shell containers instead.
    void SyncDragSuppression()
        => Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, _sidebarDragging.Peek());

    // Projected motion (see docs/plans/…prancy-otter). WAVEE_RAIL_BASELINE=1 is the A/B escape hatch: it selects the OLD
    // SizeMode.Reflow width-per-tick tracks (real layout every tick — the slow 16–45 ms path) so the rail probe can
    // measure the pre-fix baseline from the SAME build. Default (unset) = the projected Reveal path below.
    static readonly bool s_railBaseline = Diag.EnvFlag("WAVEE_RAIL_BASELINE");
    // WinUI SplitView's compact-inline pane spline (generic.xaml, ClosedCompactLeft <-> OpenInlineLeft). Wavee gives
    // the retained motion 300 ms rather than WinUI's 200 ms because the heavier media surface can otherwise consume
    // most of the authored duration in its commit frames and visually read as a snap.
    static readonly EasingSpec SplitViewPaneEase = EasingSpec.CubicBezier(0f, 0.35f, 0.15f, 1f);
    const float SplitViewPaneDurationMs = 300f;
    // Stable-frame anchor key for the content card's FLIP (see the row's MorphId + the card's RelativeTo). Not a Hero
    // participant key — the row never unmounts, so it never matches a mounting node and never triggers a connected fly.
    const string ContentRowMorphId = "shell.content-row";
    /// <summary>The one main-page silhouette. Route content may paint through it, but may not author another shape.</summary>
    internal static readonly CornerRadius4 ContentPaneCorners = new(Radii.Card, 0f, 0f, 0f);

    /// <summary>HOW A LEFT+TOP-ONLY BORDER IS DRAWN. The engine's <c>BorderWidth</c> is a single uniform SDF ring
    /// (<c>SceneRecorder.EmitBorderRing</c>) — there is no per-side thickness, and two 1-DIP strips cannot follow the
    /// 8-DIP top-left arc (they leave a visible notch exactly at the corner). So the bordered box is made ONE DIP LARGER
    /// on its right and bottom edges with this negative margin and parked inside a <c>ClipToBounds</c> parent of the real
    /// geometry: the ring is inset (it spans [0,bw] INSIDE the bounds), so the right and bottom strokes land entirely in
    /// the clipped-away DIP while the left and top strokes — and the full rounded corner between them — survive intact.
    /// One node, no notch, and the arc is the renderer's own.</summary>
    static readonly Edges4 StrokeOverhang = new(0f, 0f, -1f, -1f);

    /// <summary>The content region's stock separating stroke: 1px <c>Tok.StrokeCardDefault</c> on LEFT + TOP only, over
    /// the given silhouette. Paint-only and STATIC final geometry — it must not ride the content card's FLIP, or the
    /// region's edge would slide away from the sidebar it separates from.</summary>
    static Element ContentRegionStroke(CornerRadius4 corners) => new BoxEl
    {
        ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Margin = StrokeOverhang,
                BorderWidth = 1f,
                BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
                Corners = corners,
            },
        ],
    };

    // The sidebar collapse (56↔expanded) AND the content card's FLIP share ONE transition, so the pane's animating edge
    // and the card's left edge ease on identical dynamics (edge coherence). Reveal lays the subtree out at its FINAL size
    // immediately and eases only a clip window + a translate (compositor-only) — NO per-tick boundary relayout /
    // DirectWrite text re-shape (what made Reflow slow). Snapped 1:1 during a grip drag through the suppression arbiter
    // (ApplyProjections → SnapStructuralToLayout).
    static readonly LayoutTransition SidebarPaneAnim = s_railBaseline
        ? new(TransitionChannels.Size, TransitionDynamics.Tween(Motion.ControlFast, Easing.SmoothOut), SizeMode.Reflow)
        : new(TransitionChannels.Size | TransitionChannels.Position,
            TransitionDynamics.Tween(SplitViewPaneDurationMs, SplitViewPaneEase), SizeMode.Reveal,
            ExitDynamics: TransitionDynamics.Tween(SplitViewPaneDurationMs, SplitViewPaneEase),
            SuppressDescendantTransitions: true);

    // The content card FLIPs (Position|Size Reveal, SAME dynamics as the pane) so it absorbs the reserved-width shift when
    // the pane / rail spacer commit a new width. In the Reflow baseline the card carried NO transition (it re-tiled via
    // real layout every tick), so this is null there.
    static readonly LayoutTransition? ContentCardAnim = s_railBaseline ? null : new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(SplitViewPaneDurationMs, SplitViewPaneEase),
        SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(SplitViewPaneDurationMs, SplitViewPaneEase),
        SuppressDescendantTransitions: true);

    // The right-rail open/close. Projected: a Reveal slide (FLIP TranslateX + presented-width) on the rail OVERLAY, so the
    // panel slide-reveals under its own clip; the reservation spacer snaps its width 0↔RailWidth at commit (NO transition)
    // and the content card's FLIP absorbs the reserved shift. Baseline: BOTH the spacer and the overlay animated REAL
    // width via a critically-damped Reflow spring (the old double width track). Spring damping 1.0 ⇒ no overshoot.
    static readonly LayoutTransition? RailOverlayAnim = s_railBaseline
        ? new(TransitionChannels.Size, TransitionDynamics.Spring(0.22f, 1f), SizeMode.Reflow)
        : null;

    // The reservation spacer: animated (Reflow) in the baseline, SNAP (null) in the projected path.
    static readonly LayoutTransition? RailSpacerAnim = s_railBaseline
        ? new(TransitionChannels.Size, TransitionDynamics.Spring(0.22f, 1f), SizeMode.Reflow)
        : (LayoutTransition?)null;

    // The shell receives its persisted settings through the IAppSettings interface (provided by the composition root,
    // Services). It never sees the concrete store — no "ForUnpackaged"/registry/publisher detail leaks in here.
    static string HistoryFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "WaveeMusic", "history.json");

    // Stress-probe nav seam (WAVEE_NAV_PROBE only): lets the WaveeNavProbe drive REAL navigation/theme/tab churn through
    // the same signals the chrome writes — no synthetic input, no reaching into private state. Inert in normal runs.
    internal static Action<string, string?>? ProbeNav;
    internal static Action<int>? ProbeRail;   // open the right rail in a given RailMode (screenshot probes)
    internal static Action<bool>? ProbeRailOpen;   // open/close the right rail (WAVEE_RAIL_PROBE perf probe)
    internal static Action? ProbeBack, ProbeForward, ProbeTheme;
    internal static Action<string>? ProbeOpenTab;
    internal static Action<string, string?, bool>? ProbeCardNav;   // replicate a Home-card click: (key, arg, doMorph=Hero fly)
    internal static Action<Wavee.Core.Album>? ProbeOpenAlbum;      // replicate a related-album card click: DetailNav.OpenAlbum (stash preview + nav)
    internal static Action<bool>? ProbeSidebarCompact;
    internal static Action? ProbeSidebarDragBegin, ProbeSidebarDragEnd;
    internal static Action<float>? ProbeSidebarDragWidth;
    internal static Action<int>? ProbeSidebarMode;   // switch the active sidebar DESIGN (WAVEE_SIDEBAR_MODE_SHOT)
    internal static Action<int>? ProbeSidebarDesign;     // alias of ProbeSidebarMode, named for the design shots (WAVEE_SIDEBAR_MODE_SHOT)
    internal static Action<int>? ProbeSidebarV3View;     // Library-V3 presentation: a SidebarV3View ordinal (WAVEE_SIDEBAR_V3_SHOT)
    internal static Action<int>? ProbeSidebarV3Filter;   // Library-V3 kind filter: a SidebarV3Filter ordinal (WAVEE_SIDEBAR_V3_SHOT)
    internal static Action<bool>? ProbeSidebarDrawer;    // open/close the real narrow overlay drawer
    internal static Func<SidebarPaneFrameSnapshot>? ProbeSidebarPaneFrame; // settled rendered-width invariant probe
    NodeHandle _sidebarPaneNode;
    // The shell's CONTENT region (chrome rows above, docked player bar below) — the scope of the drag-drop spotlight
    // scrim. Captured at realize + re-published whenever the region is re-arranged.
    NodeHandle _contentRegionNode;
    SceneStore? _contentScene;
    InputHooks? _inputHooks;

    public WaveeShell(IAppSettings settings, SidebarPreferences sidebar)
    {
        _settings = settings;
        _sidebar = sidebar;
        // The service already seeded the ACTIVE design's pane triple in its own constructor (a dragged width verbatim, an
        // undragged one from that design's tier ladder at the pre-measure fallback), so the FIRST layout is already correct
        // and the viewport effect in Render commits the real tier without a visible step.
        _sidebarCompact = sidebar.Collapsed;
        _sidebarWidth = sidebar.Width;
        _tierDesign = sidebar.Design.Peek();

        // Inert probe (screenshot / UI iteration only): open the right rail to the Lyrics panel at startup.
        if (Diag.EnvFlag("WAVEE_LYRICS_OPEN") || Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE")) { _shellUi.RailOpen.Value = true; _shellUi.Mode.Value = RailMode.Lyrics; }
        if (Diag.EnvFlag("WAVEE_NOWPLAYING_OPEN")) { _shellUi.RailOpen.Value = true; _shellUi.Mode.Value = RailMode.Details; }

        if (Diag.EnvFlag("WAVEE_NAV_PROBE") || Diag.EnvFlag("WAVEE_RESIZE_PROBE") || Diag.EnvFlag("WAVEE_CONN_STRESS") || Diag.EnvFlag("WAVEE_TRACKLIST_SHOT") || Diag.EnvFlag("WAVEE_HERO_SHOT") || Diag.EnvFlag("WAVEE_SHELF_SHOT") || Diag.EnvFlag("WAVEE_RAIL_SHOT") || Diag.EnvFlag("WAVEE_HOME_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_RAIL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_PROBE") || Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE") || Diag.EnvFlag("WAVEE_MEM_SOAK") || Diag.EnvFlag("WAVEE_PERF_BENCH") || Diag.EnvFlag("WAVEE_SIDEBAR_MODE_SHOT") || Diag.EnvFlag("WAVEE_SIDEBAR_V3_SHOT") || Diag.EnvFlag("WAVEE_SIDEBAR_VISUAL_SHOT"))
        {
            ProbeNav = GoNav; ProbeBack = Back; ProbeForward = Forward; ProbeTheme = ToggleTheme; ProbeOpenTab = OpenNewTab;
            ProbeRail = m => { _shellUi.RailOpen.Value = true; _shellUi.Mode.Value = (RailMode)m; };
            ProbeRailOpen = open => { _shellUi.RailOpen.Value = open; };
            // Exactly the Home-card path: stash a preview (→ DetailShell mounts the PREVIEW path, not the skeleton path the
            // sidebar nav hits) + fire the Hero-fly morph, then navigate — so the probe can reproduce the card-click transition.
            ProbeCardNav = (key, arg, doMorph) =>
            {
                if (!Diag.EnvFlag("WAVEE_PB_NOPREVIEW") && key.StartsWith("pl:", System.StringComparison.Ordinal))
                    _navPreview.Set(key, DetailPreview.FromPlaylist(new Wavee.Core.PlaylistSummary(key.Substring(3), arg ?? "Playlist", "", 0, null)));
                GoNav(key, arg);
            };
            // The EXACT related-album-card path (DetailTrailing → h.OpenAlbum → DetailNav.OpenAlbum): stash the card's
            // partial model + fire the fly, then nav. Lets the probe measure album→album on the post-fix (in-place) path.
            ProbeOpenAlbum = a => DetailNav.OpenAlbum(_navPreview, GoNav, a);
            ProbeSidebarCompact = compact =>
            {
                _sidebarCompact.Value = compact;
                _sidebarFade.Value = 1f;
                SaveSidebar();
            };
            ProbeSidebarDragBegin = () =>
            {
                _sidebarDragging.Value = true;
                SyncDragSuppression();
            };
            ProbeSidebarDragWidth = width =>
            {
                _sidebarCompact.Value = false;
                _sidebarWidth.Value = Math.Clamp(width, ShellResponsiveLayout.NavPaneMinW, ShellResponsiveLayout.NavPaneMaxW);
                _sidebarFade.Value = 1f;
            };
            ProbeSidebarDragEnd = () =>
            {
                _sidebarFade.Value = 1f;
                SaveSidebar(widthUserSet: true);   // the probe replicates a real drag commit, including the width-pin edge
                _sidebarDragging.Value = false;
                SyncDragSuppression();
            };
            // Drive a real design switch through the real service (snapshot/restore + remount), not by poking a signal:
            // the mode-shot probe must exercise the same path the Settings picker does.
            ProbeSidebarMode = mode => _sidebar.SwitchDesign(SidebarDesignInfo.FromInt(mode));
            ProbeSidebarDesign = ProbeSidebarMode;   // one hook, two names: the design shots read better against SidebarDesign
            // The Library-V3 view state goes through the same PERSISTING setters the sort/view flyout and the filter chips
            // call — not a bare signal write — so a shot reflects what the mode actually renders after a real user change.
            ProbeSidebarV3View = view => _sidebar.SetV3View(view);
            ProbeSidebarV3Filter = filter => _sidebar.SetV3Filter(filter);
            ProbeSidebarDrawer = open => _narrowDrawerState?.SetIfChanged(open);
            ProbeSidebarPaneFrame = ReadSidebarPaneFrame;
        }

        _historyStore.Init(HistoryFilePath());
        _historyStore.LoadFromDisk();

        _historyStore.Add(new Route("home"));   // record this session's first visit
        if (_historyStore.Entries.Count == 1)   // only seed fake data on a fresh install (nothing loaded from disk)
            SeedFakeHistory();
    }

    /// <summary>Publish the content region's absolute rect as the engine's drop-spotlight scrim scope. Idempotent and
    /// cheap (one scalar write); called from realize + every re-arrange of the region.</summary>
    void PublishScrimClip()
    {
        if (_contentScene is not { } sc || _contentRegionNode.IsNull || !sc.IsLive(_contentRegionNode)) return;
        RectF r = sc.AbsoluteRect(_contentRegionNode);
        sc.SpotlightScrimClip = r.IsEmpty ? null : r;
    }

    SidebarPaneFrameSnapshot ReadSidebarPaneFrame()
    {
        bool presented = _presentedCompactState?.Peek() ?? _sidebarCompact.Peek();
        float rendered = 0f;
        if (!_sidebarPaneNode.IsNull && _inputHooks?.GetNodeRect is { } rectOf)
            rendered = rectOf(_sidebarPaneNode).W;
        return new SidebarPaneFrameSnapshot(
            _sidebar.Design.Peek(),
            _sidebarCompact.Peek(),
            presented,
            _sidebarWidth.Peek(),
            rendered,
            presented ? 0f : 1f,
            presented ? 1f : 0f,
            ExpandedHitTestVisible: !presented,
            RailHitTestVisible: presented);
    }

    void SeedFakeHistory()
    {
        var now = DateTime.Now;
        void At(Route r, int daysAgo, int hour, int min)
            => _historyStore.AddAt(r, now.Date.AddDays(-daysAgo).AddHours(hour).AddMinutes(min));

        // Earlier (5-7 days ago)
        At(new Route("artists"),                     7, 14, 23);
        At(new Route("albums"),                      7, 14, 45);
        At(new Route("pl:local:1", "Deep Focus"),    6,  9, 30);
        At(new Route("search", "Daft Punk"),         5, 20, 12);
        At(new Route("liked"),                       5, 18,  5);
        // This week (2-3 days ago)
        At(new Route("podcasts"),                    3,  8, 45);
        At(new Route("pl:local:2", "Morning Run"),   3,  7, 15);
        At(new Route("search", "Taylor Swift"),      2, 16, 30);
        At(new Route("home"),                        2, 16, 35);
        At(new Route("artists"),                     2, 16, 40);
        At(new Route("albums"),                      2, 17,  0);
        // Yesterday
        At(new Route("pl:local:3", "Chill Vibes"),   1, 10, 20);
        At(new Route("liked"),                       1, 11,  5);
        At(new Route("search", "Radiohead"),         1, 14, 33);
        At(new Route("home"),                        1, 19,  0);
        At(new Route("podcasts"),                    1, 21, 10);
        // Today
        At(new Route("albums"),                      0,  9, 15);
        At(new Route("pl:local:1", "Deep Focus"),    0,  9, 30);
        At(new Route("artists"),                     0, 10,  0);
        At(new Route("search", "Stromae"),           0, 10, 20);
    }

    // widthUserSet is the DRAG-COMMIT edge only: it pins the width as a preference so the responsive tier ladder stops
    // writing it. The collapse toggle also persists through here and must leave the flag alone — collapsing the pane is
    // not a width choice, and pinning on it would freeze every user at whatever tier they happened to collapse from.
    // Both arms now DELEGATE to SidebarPreferences, which owns the per-design keys and the clamp; the shell no longer
    // touches the (legacy, migration-only) global sidebar.width / sidebar.collapsed keys.
    void SaveSidebar(bool widthUserSet = false)
    {
        if (widthUserSet) _sidebar.CommitWidthDrag(_sidebarWidth.Peek());
        else _sidebar.SetCollapsed(_sidebarCompact.Peek());
    }

    void CommitSidebarDrag() => SaveSidebar(widthUserSet: true);

    void ToggleSidebar()
    {
        if (_narrowShellState?.Peek() == true)
        {
            if (_narrowDrawerState is { } drawer) drawer.Value = !drawer.Peek();
            return;
        }

        bool compact = !_sidebarCompact.Peek();
        _sidebarCompact.Value = compact;
        _presentedCompactState?.SetIfChanged(compact);
    }

    void CloseNarrowDrawer() => _narrowDrawerState?.SetIfChanged(false);

    public override Element Render()
    {
        _inputHooks = UseContext(InputHooks.Current);
        _contentScene = Context.Scene;   // captured at render: PublishScrimClip runs from layout, outside any render context
        _requestTheme = UseContext(ThemeControl.Request);   // host's live re-theme trigger (animated in-place; no remount)
        // Float the auto-mounted toast lane ABOVE the fixed bottom player bar (idempotent static write — the
        // ToastHost-registration idiom): reserve the player-bar height on the docked (bottom) edge.
        Toast.EdgeInset = WaveeSize.PlayerBarH;
        // The shell's content lives in the OverlayHost ZStack, which deliberately lets its child OVERFLOW (a tall popup must
        // not be clipped to the window). For the page CONTENT that means a tall page (a Detail rail is ~600px and does not
        // scroll) sizes the whole column to its content (~827px) and overflows the 760px window — shoving the fixed player
        // bar off the bottom (the "player bar disappears / slides" glitch). Pin the column's height to the LIVE viewport (a
        // BOUND prop, not a stale literal — it re-fires on resize) so the column is exactly window-tall and its Shrink=1 /
        // MinHeight=0 content region yields instead of overflowing. UI-thread signal; the binding re-lays-out on resize.
        var vpSig = UseContextSignal(Viewport.Size);
        var narrowShell = UseSignal(ShellResponsiveLayout.NarrowFor(
            vpSig.Peek().Width, current: false, initialized: false));
        var narrowDrawer = UseSignal(false);
        var presentedCompact = UseSignal(narrowShell.Peek() || _sidebarCompact.Peek());
        _narrowShellState = narrowShell;
        _narrowDrawerState = narrowDrawer;
        _presentedCompactState = presentedCompact;

        // Width is hot while the OS resize loop is active, but these structural signals only write when a hysteretic
        // band flips. Interactive window sizing suppresses layout projection, so the rail tracks the pointer 1:1.
        UseSignalEffect(() =>
        {
            bool current = narrowShell.Peek();
            bool next = ShellResponsiveLayout.NarrowFor(vpSig.Value.Width, current, initialized: true);
            if (next == current) return;
            narrowShell.Value = next;
            if (!next) narrowDrawer.SetIfChanged(false);
        });
        // The nav-pane's responsive DEFAULT width (stock OpenPaneLength-per-window-class). Same band discipline as the
        // narrow-shell effect above: it reads the hot viewport width but only writes when a hysteretic tier flips, and it
        // is never debounced (a deferred structural width is a frame rendered at the wrong tier — ShellUi's note).
        // Three writers must never fight over the pane width, so this one yields to both of the others:
        //   • a pinned width — SidebarPreferences.WidthUserSet (per design), latched by the drag commit, is a permanent opt-out;
        //   • a live drag — Peek-gated (no subscription): OnMove owns the width 1:1 while the pointer is down. A drag ends
        //     with SaveSidebar(widthUserSet: true), so after any committed drag this effect is inert anyway.
        // vpSig AND Design are read FIRST and unconditionally: an early return before either read would drop that
        // subscription and the tier would never update again. Subscribing to Design is what makes a design switch RE-SEED
        // the ladder against the incoming design's tier set (Classic 240/280/320 · V3 300/340/380 · Curated 280/320/360)
        // instead of holding the outgoing design's tier. The pane's SidebarPaneAnim animates whatever this commits, for free.
        UseSignalEffect(() =>
        {
            float w = vpSig.Value.Width;
            var design = _sidebar.Design.Value;
            if (design != _tierDesign) { _tierDesign = design; _navPaneTierSeeded = false; }
            _sidebar.SetViewportWidth(w);   // the service needs the live viewport to tier-seed an incoming design
            if (_sidebar.WidthUserSet || _sidebarDragging.Peek()) return;
            float next = ShellResponsiveLayout.NavPaneDefaultFor(w, _sidebarWidth.Peek(), _navPaneTierSeeded, _sidebar.Tiers);
            if (w > 0f) _navPaneTierSeeded = true;
            _sidebar.SetResponsiveWidth(next);
        });
        UseSignalEffect(() =>
            presentedCompact.SetIfChanged(narrowShell.Value || _sidebarCompact.Value));
        bool compact = _sidebarCompact.Value;    // subscribe → re-persist on a collapse/expand toggle (infrequent)
        bool dragging = _sidebarDragging.Value;  // subscribe → snap all layout transitions while resizing the sidebar
        // Persist the collapse toggle here; the grip's drag-end (OnReleased → CommitSidebarDrag) persists the width AND
        // pins it as a user choice. This path must stay on the flag-free overload. The initial values are seeded at field
        // construction (below) so the first layout is already correct. Defensive: storage failures never touch the UI.
        UseEffect(() => SaveSidebar(), compact);
        // Keep an owner-safe suppression edge in the reactive lifecycle as cleanup insurance. This snaps geometry only;
        // the user's reduced-motion preference and non-layout feedback remain untouched.
        UseEffect(() => SyncDragSuppression(), dragging);

        // Rail viewport-fit + layout-defer (off-render, auto-tracking effects — the render body stays subscription-free
        // so the shell isn't re-run on every resize pixel; only the rail band / pages re-solve from the signals below).
        var post = UsePost();

        // Refresh the (reference-stable) ActionServices bag — plain field writes on the same instance, so the
        // Ctx.Provide below never churns its consumers. Overlay is bound by ActionServicesOverlayBinder (inside the
        // OverlayHost subtree, where the REAL service lives).
        _actions.Playback = UseContext(PlaybackBridge.Slot);
        _actions.Library = UseContext(LibraryBridge.Slot);
        _actions.Svc = UseContext(Services.Slot);
        _actions.Store = UseContext(LibraryStore.Slot);
        _actions.Clipboard = UseContext(InputHooks.Current).Clipboard;
        _actions.Go = GoNav;
        _actions.Post = post;
        _actions.VideoOverrides = _actions.Svc?.VideoOverrides;
        _actions.Sidebar = _sidebar;   // the pin store behind Pin/Unpin to sidebar (reference-stable → never churns consumers)
        _actions.CurrentRoute ??= () => _route.Peek().Name;   // the ActiveRoute target-mode resolver (Peek: invoke-time read, not a render dep)
        _actions.CurrentDestination ??= CurrentSidebarDestination;
        if (_actions.Extensions is null)
        {
            // The extension registry, built ONCE per shell: the first-party "wavee" actions + the nine sidebar data
            // sources land in one enumerable catalog (the customizer's action/section pickers read it in order).
            var registry = WaveeExtensionRegistry.Build(_actions);
            _actions.Svc?.RegisterSidebarSources(registry);
            _actions.Extensions = registry;
        }
        // The row indicator / "Videos only" filter read the association plane + the curation through a process-wide
        // probe rather than context, because they run per ROW (a context read or a signal subscription per row is not
        // affordable there). Both halves of the has-video answer are attached here, and nothing else answers it.
        VideoPresence.Attach(_actions.VideoOverrides, _actions.Svc?.RealStore);
        // The two override toasts' "Manage" button + the Settings roster deep-link: bump the request counter (the
        // PlaybackRuntimeBanner precedent — Settings has no route-arg tab deep-link) and navigate.
        if (_actions.Playback is { } pb && pb.OpenVideoOverrideManager is null)
            pb.OpenVideoOverrideManager = _ =>
            {
                pb.OpenVideoOverrides.Value = pb.OpenVideoOverrides.Peek() + 1;
                GoNav("settings", null);
            };
        // Maintain ShellUi.RailFits from the live viewport/sidebar/rail widths. The rail no longer auto-closes on a
        // fits-flip — it switches between inline (spacer reserves width) and floating (overlay only). Peek-guarded so
        // this never re-triggers.
        UseSignalEffect(() =>
        {
            float vpW = vpSig.Value.Width;
            float sbW = presentedCompact.Value ? ShellResponsiveLayout.CompactRailW : _sidebarWidth.Value;
            bool fits = ShellUi.CanFitRail(vpW, sbW, _shellUi.RailWidth.Value);
            _shellUi.RailFits.SetIfChanged(fits);
        });

        // ── the merged chrome row's two derivations ──────────────────────────────────────────────────────────────────
        // (1) The priority ladder. Band-gated exactly like the retired ToolbarLayout: it reads the HOT viewport width
        //     but only publishes when a stage actually flips, so a resize does not re-render the bar per pixel. The tab
        //     count is a dependency because KeepTabs is clamped to it.
        UseSignalEffect(() =>
        {
            float w = vpSig.Value.Width;
            _ = _tabsVersion.Value;
            _chromeLayout.SetIfChanged(MergedChromeLayout.Resolve(w, _open.Count, _chromeLayout.Peek()));
        });
        // (2) The visible-tab projection — the ONE place the strip's index space is derived, and deliberately OFF-render
        //     (computing it inside ItemsSource would mean writing _stripSelected from inside a render: a backwards
        //     write). Re-runs on a tab-set change, a selection change and a KeepTabs change.
        UseSignalEffect(() =>
        {
            int keep = _chromeLayout.Value.KeepTabs;
            int sel = _selectedTab.Value;
            _ = _tabsVersion.Value;
            RebuildVisibleTabs(keep, sel);
            _stripSelected.SetIfChanged(Math.Max(0, _visibleTabs.IndexOf(sel)));
            _tabStripVersion.Value = _tabStripVersion.Peek() + 1;
        });
        // The row's island builders. Constructed once (it owns the measured-extent signals behind the centring guard);
        // its three ambient services are PLAIN FIELDS refreshed per render — a slot builder runs inside the BAR's render
        // and can never call UseContext itself, so the shell resolves them here.
        var chrome = _chrome ??= new MergedChromeRow(
            _canBack, _canForward, GoNav, Back, Forward,
            _searchText, ToggleTheme, _history, _forwardHistory,
            _chromeLayout, _searchExpanded, _searchFocusRequest,
            TabStripHost, TabStripItemsVersion, HiddenTabs, ActivateTab);
        chrome.Bridge = _actions.Playback;
        chrome.Ui = _shellUi;
        chrome.Acts = _actions;

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f, Height = Prop.Of(() => vpSig.Value.Height),   // window-tall → content yields, never overflows the player bar
            Children =
            [
                // Zero-size, renders nothing: owns the ambient-cadence policy's two subscriptions (window activation +
                // the debounced power poll). It lives here because the shell is the one always-mounted host in the tree.
                Embed.Comp(() => new AmbientPowerPolicy.Watcher()),
                // Shell-wide chords. InputHooks.KeyPreview is modifier-BLIND (Func<int,bool>), so the two shell verbs
                // ride the engine's KeyAccelerator seam instead — the dispatcher matches key+mods against any live,
                // visible, enabled node AFTER focused routing declines it (the WinUI ProcessKeyboardAccelerators
                // order). Zero-size + hit-test-free ⇒ pure keyboard surface, and it composes with the narrow drawer's
                // own Escape KeyPreview rather than fighting it for the single preview slot.
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = NewTabChord, OnClick = () => OpenNewTab("home"),
                },
                new BoxEl
                {
                    Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false,
                    Accelerator = FocusSearchChord, OnClick = FocusSearch,
                },
                // THE chrome row. One 48-DIP TitleBar in merged mode: the tabs island carries Wavee's nav cluster and
                // the text-first strip, the flexible centre column carries the window-centred omnibar, and the trailing
                // island carries identity. ContentVersion is mandatory here — see MergedChromeRow.ContentVersion.
                Embed.Comp(() =>
                {
                    var bar = new TitleBar
                    {
                        IconGlyph = "", ShowBackButton = false, ShowCaptionButtons = true,
                        // The HAMBURGER is the bar's own pane-toggle built-in, not a child of the tabs island. Two
                        // reasons, both from the first feel pass: it lands in the fixed lead column (centred where the
                        // 56-DIP compact rail's icons are, which a child of the island cannot reach — the island starts
                        // after a 16-DIP lead and clips anything shifted left of it), and it is reported as its OWN
                        // Client region, so the header pad between it and the tabs island stays real window-drag band.
                        ShowPaneToggle = true, OnPaneToggle = ToggleSidebar,
                        Parts = ChromeParts,               // the four-DIP nudge that lines it up with the rail's icons
                        ShowRailBaseline = false,          // this chrome has no rail; the seam below is the app's own
                        Tabs = chrome.Tabs,
                        TabsVersion = TitleBarTabsVersion,
                        Trailing = chrome.Trailing,
                        ContentVersion = chrome.ContentVersion,
                    };
                    // Hand the island the bar's LIVE centre-column measurement (the merged-mode mirror of ContentAvail)
                    // so an expanding field can clamp itself without the bar re-rendering.
                    bar.CenterContent = _ => chrome.Center(bar.CenterAvail);
                    return bar;
                }),
                // NO chrome↔content seam hairline here. Stock Win11 (WinUI NavigationView + the WinUI-Gallery shell) draws
                // no bar-wide divider under the title bar: the separation IS the content region's own left+top stroke
                // (see ContentRegionStroke below), which starts exactly where the page starts. A full-width hairline
                // stacked a SECOND rule against that stroke over the page, ran straight across the sidebar band (which
                // owns no such edge in stock), and put the tab underline 6 DIP above a line it never belonged to.
                Ui.ZStack(
                    // The sidebar + content row. The sidebar PANE (SidebarPane) is the row's DIRECT child, so ITS width
                    // is what the row distributes — the content column re-solves and tiles against it gap-free. The width
                    // is signal-bound + drag-resizable (the grip overlay below); the pane animates the collapse toggle.
                    // No Fill on the row: the PLATE is painted once per band (the sidebar pane and the content-side
                    // backing below each paint it), so a row-level slab would sit UNDER those and double-tint them —
                    // the exact hazard the coincident content-pane double-composite used to have. Real-layout tiling
                    // keeps the seam gap-free with nothing behind.
                    new BoxEl
                    {
                        // ClipToBounds (Task B5): a settle-frame safety net so a page's content can never paint past the
                        // content card into the fixed rail band during the rail reveal. The card + page wrappers already
                        // clip; this bounds the row itself while the flex-shrink chain re-solves.
                        // MorphId (stable-frame anchor for the content card's FLIP): the row never moves on a sidebar
                        // toggle, so the content card FLIPs its slide RELATIVE to this frame (Element.RelativeTo below)
                        // instead of its own parent (which absorbs the reserved-width shift → a zero delta → the snap).
                        MorphId = ContentRowMorphId,
                        Direction = 0, Grow = 1f, ClipToBounds = true,
                        Children =
                        [
                            // The sidebar pane — a LITERAL row child (NOT a component): an Embed.Comp root mirrors its Grow
                        // onto the host node and grows HORIZONTALLY in the row (gap), whereas a literal child cross-
                        // stretches to full row height for free. Width is bound (compact rail / draggable expanded); the
                        // Reflow animates the collapse toggle but is null mid-drag so the pane tracks the cursor 1:1.
                        new BoxEl
                        {
                            // NO fill: the sidebar band is a paint-site OMISSION over the window's BASE LAYER (live
                            // Mica), exactly like the merged chrome row and the player dock. One uninterrupted base
                            // under all chrome is what kills the title-bar-vs-toolbar seam the translucent plate
                            // produced — and the reason the content region needs a stroke rather than a fill step to
                            // separate itself from this band.
                            Direction = 1, Shrink = 0f, ClipToBounds = true,
                            OnRealized = h => _sidebarPaneNode = h,
                            Width = Prop.Of(() => presentedCompact.Value
                                ? ShellResponsiveLayout.CompactRailW : _sidebarWidth.Value),
                            // SidebarPaneAnim eases the COLLAPSE toggle (56↔expanded) as a clip+translate reveal — the pane
                            // is ClipToBounds so the reveal scissors its content. During a drag the suppression arbiter
                            // snaps every layout transition (this pane AND the sidebar sections) to the laid-out width 1:1.
                            Animate = SidebarPaneAnim,
                            Children =
                            [
                                // Content fades (compositor-only) toward the collapse detent; the chrome fill stays solid.
                                // Column wrapper so the mode component's Grow=1f fills our HEIGHT (its ScrollView needs a
                                // definite one). SidebarHost is the ONE mount seam: it re-renders on a design switch and
                                // remounts the selected mode under a design-derived Key.
                                new BoxEl
                                {
                                    Direction = 1, Grow = 1f,
                                    Opacity = Prop.Of(() => _sidebarFade.Value),
                                    // LAYOUT FIREWALL for the whole sidebar. Every realized row wraps its content in a
                                    // ToolTip, so a sidebar publish marks dozens of layout-dirty nodes that each escape
                                    // to a full-tree relayout from the scene root — the dominant escape source in the
                                    // app. This box is the right level: it is INSIDE the pane, which owns the chrome
                                    // fill and the animated collapse reveal (a boundary must never sit on a box whose
                                    // own size animates), and it fills that pane exactly, so the clip Boundary() implies
                                    // is redundant with the pane's existing ClipToBounds and changes nothing. Its width
                                    // cross-stretches from the pane and its height is Grow=1 in the pane's column, so
                                    // neither axis can be content-sized by a descendant. Sidebar flyouts, context menus
                                    // and drag ghosts are not in this subtree (overlay host / hoisted bands).
                                    IsolateLayout = true, ClipToBounds = true,
                                    Children = [ Embed.Comp(() => new SidebarHost(_route, GoNav, presentedCompact, _sidebarWidth)) ],
                                },
                            ],
                        },
                            // Content side: the STOCK Win11 content region — ONE rectangle, flush on all four sides, filled
                            // with the LayerFillColorDefault rung and PAIRED with a 1px stroke on LEFT+TOP only, corner
                            // 8,0,0,0, and NO shadow. That triple (flush · one corner · stroke, no shadow) is what WinUI's
                            // own NavigationView content grid and the WinUI-Gallery shell paint over bare Mica; the
                            // separation comes from the stroke, never from a gutter or an elevation.
                            new BoxEl
                            {
                                // MinHeight=0 at every flex level of the content chain (see the card below) so a tall page
                                // can shrink/clip instead of overflowing the column and covering the docked player bar.
                                // Shrink=1 + MinWidth=0 are the HORIZONTAL analogue: FlexShrink defaults to 0 (Yoga-style),
                                // so without them this Grow=1 content region floors at its page's intrinsic min-width and
                                // CANNOT yield when the row (sidebar + content + right rail) overruns a narrow window — the
                                // fixed-width rail (Shrink=0) is then shoved off the right window edge and the Lyrics panel
                                // is clipped by a per-page amount (wide pages push it further ⇒ the "rail changes size / gets
                                // cut off depending on the page" instability). With them the content page is the ONE region
                                // that gives (it clips/scrolls), so the rail keeps its full RailWidth on every page.
                                Direction = 1, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
                                Children =
                                [
                                    // Static final-geometry underlay — the CONTENT LAYER of the Windows 11 Mica model
                                    // (learn.microsoft.com system-backdrops: Base Layer = bare Mica chrome, Content
                                    // Layer = a translucent smoke OVER the base). This is the stock WinUI
                                    // LayerFillColorDefault rung (WaveeColors.FileArea: #4C3A3A3A dark / #80FFFFFF
                                    // light) — it LIGHTENS the content region over live Mica so the page always reads
                                    // one step ABOVE the base, never darker (an opaque #282828 pane inverted the model
                                    // on light-tinted wallpapers: the page read as a black slab inside brighter
                                    // chrome). It is the only FILL painted in this region: the rounded top-left
                                    // cut-away is a paint-site omission that shows the base. Do not add a
                                    // sidebar-coloured seam strip here: it reads through the page as a full-height rail
                                    // and squares off the corner.
                                    //
                                    // NO SHADOW (stock). A drop shadow cast under a ~30%-alpha fill bleeds THROUGH it
                                    // and muddies the top and left of the page; zero stock Win11 shells elevate the
                                    // content layer. The region's separation is ContentRegionStroke below.
                                    // NO MARGIN either — the permanent trailing Spacing.S gutter is gone; the rail's
                                    // 8-DIP breathing room is now the bound gap box below, which exists only while the
                                    // rail is inline. A closed rail leaves the page flush to the window edge.
                                    new BoxEl
                                    {
                                        Grow = 1f,
                                        Fill = Prop.Of(() => WaveeColors.FileArea),
                                        Corners = ContentPaneCorners,
                                    },
                                    new BoxEl
                                    {
                                        // MinHeight=0 (the flex `min-height:0` override): this card CLIPS its content, so it
                                        // must be allowed to shrink BELOW the page's natural min-height. Without it, a tall
                                        // page (a Detail RAIL is ~600px and does not scroll) forces the content region past
                                        // the column height and PUSHES THE FIXED PLAYER BAR off the bottom for a frame on
                                        // navigation — the "player bar animates away then back" glitch. With it the card
                                        // shrinks to the available space and clips/scrolls, so the player bar stays docked.
                                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                                        // Flush on ALL FOUR sides (stock): against the navigation pane, the chrome row,
                                        // the player dock and — when it is open — the rail's own reservation gap.
                                        // NO fill: the static underlay above owns the pane surface. This card and that
                                        // underlay are coincident at rest (both Grow=1, both flush, same corner), so a
                                        // fill here composited the translucent FileArea TWICE — the pane
                                        // read a rung too light (#333333 instead of #303030 dark) and mid-slide the
                                        // moving card banded against the static surface. The card keeps the corner,
                                        // the clip and the FLIP; the paint and the stroke are both static siblings now.
                                        // (Regression tell: a dark pane sampling #333333 means this fill came back.)
                                        Fill = ColorF.Transparent,
                                        // NO border here either. The region's stroke is a STATIC final-geometry overlay
                                        // (ContentRegionStroke below) so the edge does not slide with the FLIPping card,
                                        // and so it can be LEFT+TOP only — a uniform ring on this node drew a stroke
                                        // along the player-dock seam and the rail seam that stock has no line on.
                                        // Stock NavigationViewContentGridCornerRadius = 8,0,0,0: only the corner facing
                                        // the nav pane rounds.
                                        Corners = ContentPaneCorners,
                                        ClipToBounds = true,
                                        // Layout firewall (#5): this card is Grow=1 (its size is the shell's content region,
                                        // parent-determined) and clips — so a re-render deep inside a page re-solves only this
                                        // subtree (RunSubtree) instead of a full-tree layout from the root on every nav.
                                        IsolateLayout = true,
                                        // Projected motion: the card carries a Position|Size Reveal so that when the sidebar
                                        // pane commits a new width, the card FLIP-translates from its OLD left edge to the new
                                        // one AND presented-size-reveals its width from old→new (CaptureProjections/ApplyProjections),
                                        // sliding the content sheet in lock-step with the pane's revealing edge. Size is required
                                        // alongside Position: Grow=1 makes the card's final width layout-driven, so a Position-only
                                        // FLIP would snap the width to final on frame 1 while only the left edge eased (the visible
                                        // "card width snaps" tear). Same dynamics as the pane (edge coherence); mirrors SidebarPaneAnim.
                                        // RelativeTo (CRITICAL): FLIP against the stable ROW frame, NOT the card's layout parent.
                                        // The card fills the content region, and that region absorbs the ENTIRE reserved-width
                                        // shift — so the card's PARENT-relative rect never changes (zero delta) and the default
                                        // FLIP would produce no slide: the sheet SNAPS to its final X while only the (occluded)
                                        // pane reveal eases behind it. Anchoring the FLIP to the row (which does not move on a
                                        // toggle) restores the real delta, so the card slides while the static L1/L2/L3 underlays
                                        // stay put to fill the trailing gap (the WinUI ContentTransform.TranslateX choreography).
                                        // Grow=1 ⇒ the final rect is layout-driven. Null in the Reflow baseline (real-layout tile).
                                        Animate = ContentCardAnim,
                                        RelativeTo = s_railBaseline ? null : ContentRowMorphId,
                                        Children = [ Embed.Comp(() => new ContentHost(_route, _navMotion, ActiveTabId, _settings)) ],
                                    },
                                    // The region's ONE separating treatment, topmost so a page can never paint over it:
                                    // a 1px Tok.StrokeCardDefault on LEFT + TOP only, following the 8-DIP top-left arc.
                                    ContentRegionStroke(ContentPaneCorners),
                                ],
                            },
                            // The rail's breathing room, and the ONLY gap in the row. It is part of the rail's
                            // RESERVATION, not a permanent trailing gutter on the page: with the rail closed the page
                            // runs flush to the window edge (stock), and opening the rail widens this from 0 to 8 in the
                            // same commit as the spacer below (both snap; the content card's FLIP absorbs the shift).
                            new BoxEl
                            {
                                Shrink = 0f, HitTestVisible = false,
                                Width = Prop.Of(() => _shellUi.RailOpen.Value && _shellUi.RailFits.Value ? Spacing.S : 0f),
                            },
                            // Right rail RESERVATION spacer — the WaveeMusic-style lyrics / now-playing band. A literal row
                            // child (Shrink=0, bound width) so the content card re-tiles against it. Projected motion: this
                            // width flips 0<->RailWidth at commit with NO Animate — it SNAPS to the reserved extent, and the
                            // content card's FLIP (SidebarReveal) absorbs the shift while the rail overlay slide-reveals into
                            // the reserved band. (Animating both the spacer AND the overlay was the old double width track.)
                            //
                            // Docked band coats collapse to ONE (content-pane twin): the underlay child below paints the
                            // single FileArea coat for the reserved band, and RightRail's own surface goes Transparent
                            // while docked and draws on it — instead of Toolbar + FileArea + RightRail's FileArea, three
                            // coats become one. Floating keeps FileArea over FloatingChrome (B2). The band's one stroke
                            // rides RightRail's own topmost edge, not this underlay.
                            new BoxEl
                            {
                                // NO fill on the spacer itself: the rail's rounded top-left wedge (and the 8-DIP gap
                                // before it) read against the window's BASE LAYER — live Mica — like every other
                                // paint-site omission in the chrome. The band's own rung is painted by its child below.
                                Shrink = 0f,
                                Width = Prop.Of(() => _shellUi.RailOpen.Value && _shellUi.RailFits.Value ? _shellUi.RailWidth.Value : 0f),
                                Animate = RailSpacerAnim,   // null (snap) in the projected path; Reflow spring in the baseline
                                Children =
                                [
                                    // Static final-geometry underlay — the rail-side twin of the content card's underlay
                                    // above, and the ONE coat of the docked band. The reserved width SNAPS at commit
                                    // while RightRail translates its panel in over 300ms; without this, that band is
                                    // bare Mica for the whole slide and the open reads as "the content jumps away from a
                                    // dark hole, then a panel arrives". Painting the rail's own surface here (same Fill +
                                    // same top-left card corner) means the band IS the rail from frame 0 and only the
                                    // panel's CONTENT is seen to arrive — which is exactly why RightRail paints
                                    // Transparent while docked: two coats of the same smoke is one rung too dark.
                                    // Paint-only: never a hit target, no opacity track (a fading full-height rail surface
                                    // is what produced the old "ghost rail").
                                    // The same Mica CONTENT-LAYER rung as the content card's underlay above — the rail
                                    // band and the page must be the SAME rung, or the seam between them reappears the
                                    // moment one composites over Mica and the other over flat paint.
                                    // NO stroke here: the band's single left+top hairline rides RightRail's own topmost
                                    // `edge` (an underlay stroke would double-draw against it). A stroke-less band for
                                    // the 300ms slide-in is fine — the edge lands with the panel.
                                    new BoxEl
                                    {
                                        Grow = 1f, HitTestPassThrough = true, ClipToBounds = true, ZStack = true,
                                        Children =
                                        [
                                            new BoxEl
                                            {
                                                Margin = StrokeOverhang,
                                                Fill = Prop.Of(() => WaveeColors.FileArea),
                                                Corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f),
                                            },
                                        ],
                                    },
                                ],
                            },
                        ],
                    },
                    // Resize-grip overlay: a narrow strip translated to the pane↔content seam. The overlay's own hit
                    // bounds are only the grip column, so sidebar wheel/hover routing and its scrollbar thumb still hit
                    // the sidebar ScrollView instead of a non-scrollable overlay branch.
                    new BoxEl
                    {
                        Width = Prop.Of(() => narrowShell.Value ? 0f : 16f), Direction = 1, ClipToBounds = true,
                        Transform = Prop.Of(() => Affine2D.Translation(presentedCompact.Value
                            ? ShellResponsiveLayout.CompactRailW : _sidebarWidth.Value, 0f)),
                        Children =
                        [
                            // The strip is entirely on the content side of the seam to avoid covering the sidebar's
                            // 12-DIP scrollbar lane; SidebarResizeGrip's root Grow=1 fills this definite-height column.
                            Embed.Comp(() => new SidebarResizeGrip(_sidebarCompact, _sidebarWidth, _sidebarDragging, _sidebarFade, CommitSidebarDrag)),
                        ],
                    },
                    new BoxEl
                    {
                        Grow = 1f, Direction = 0, Justify = FlexJustify.End, HitTestPassThrough = true,
                        Children =
                        [
                            new BoxEl
                            {
                                // The rail overlay hosts RightRail. Projected motion: its width flips 0<->RailWidth as the
                                // single commit; RailReveal (Position|Size Reveal) slide-reveals it (FLIP TranslateX +
                                // presented-width) under its own ClipToBounds instead of animating REAL layout width per
                                // tick. Floating mode (!RailFits) has no spacer, so this overlays the content without
                                // resizing it — the reveal is purely the panel sliding in.
                                Direction = 1, Shrink = 0f, ClipToBounds = true, ZStack = true,
                                // Projected path: the overlay keeps its final width and RightRail translates its retained
                                // subtree through this clip. The baseline retains the old animated 0↔width layout path.
                                Width = Prop.Of(() => s_railBaseline
                                    ? (_shellUi.RailOpen.Value ? _shellUi.RailWidth.Value : 0f)
                                    : _shellUi.RailWidth.Value),
                                Animate = RailOverlayAnim,
                                HitTestPassThrough = true,
                                Children =
                                [
                                    // Backing band for the FLOATING overlay only — FloatingChrome (the shell GROUND), not
                                    // FloatingPane: RightRail paints the content surface on top, and pane-then-surface was
                                    // a double-coat that made the floating rail one rung darker than docked. Docked stays
                                    // transparent so the rail's rounded TL wedge shows the ground behind it.
                                    new BoxEl
                                    {
                                        // Paint-only closed-rail backing: never become the deepest hit in this retained
                                        // overlay. The interactive RightRail subtree above owns input while open.
                                        Grow = 1f, HitTestPassThrough = true,
                                        Fill = Prop.Of(() => _shellUi.RailOpen.Value && !_shellUi.RailFits.Value
                                            ? WaveeColors.FloatingChrome : ColorF.Transparent),
                                    },
                                    new BoxEl
                                    {
                                        // The panel stays mounted at its final width so close can translate it out without
                                        // relayout. When RightRail marks its root non-hit-testable, this wrapper must also
                                        // yield or the invisible retained 340-DIP strip covers the page scrollbar.
                                        Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, HitTestPassThrough = true,
                                        Corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f),
                                        // LAYOUT FIREWALL for the rail panel (lyrics, queue, friends): a re-render in
                                        // there must not re-solve the whole window. This INNER host is the right level —
                                        // the overlay above owns the reveal and the floating backing band — and on the
                                        // projected (default) path that overlay holds a CONSTANT RailWidth while
                                        // RightRail translates its retained subtree, so this box's width is stable
                                        // rather than animating. It already clips, so Boundary() adds only IsolateLayout.
                                        IsolateLayout = true,
                                        Children = [ Embed.Comp(() => new RightRail()) ],
                                    },
                                ],
                            },
                        ],
                    },
                    // In narrow mode the 56-DIP rail remains inline. Hamburger opens this separately-retained full pane
                    // over the page, so the saved desktop expanded/collapsed preference is never overwritten.
                    Embed.Comp(() => new ShellNarrowDrawer(
                        narrowShell, narrowDrawer, vpSig, _sidebarWidth, _drawerExpanded, _route, GoNav))
                // Bounded fill: Grow=1 takes the free space, Shrink=1 makes this the ONE region that yields when the
                // window is shorter than the column's natural height. MinHeight=0 (the flex `min-height:0` override on the
                // SHRINKING element itself — the engine otherwise floors a flex item at its CONTENT's natural min) is what
                // actually lets it yield below a tall page (a Detail rail is ~600px and does not scroll); without it the
                // region overflows the column and shoves the fixed PlayerBar ~67px off the bottom for a frame on nav (the
                // "player bar disappears then slides back" glitch). The merged chrome row (one TitleBar + its 1px seam)
                // and the PlayerBar host keep the default Shrink=0, so the player bar stays a fixed 72px slot docked at the
                // window bottom and only the middle gives — its bounded height then lets the sidebar ScrollView scroll.
                //
                // ClipToBounds: this region's OWN box is clamped to the dock every frame (Shrink=1 yields → the player bar
                // never moves), but it is a ZStack and a ZStack deliberately lets children OVERFLOW (a popup must escape the
                // window). So while a page's content settles, the content-sized child chain can extend past this box down
                // into the docked player-bar band, where the translucent bar reveals it. Clip the region to its own box so
                // its bottom edge IS the player bar's top — content can never paint into the reserved dock slot. (The engine
                // RunSubtree fix keeps the IsolateLayout card's own box flush at rest; this clip covers the settle window
                // and is correct composition regardless. The Hero fly draws in a separate top band; popups live in the
                // OUTER OverlayHost ZStack — neither is affected.)
                ) with
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
                    // Scope the drag-drop spotlight scrim to THIS region: the title bar and the docked player bar stay
                    // fully lit while the page dims behind a drag (the bar keeps showing what is playing, and the
                    // caption buttons keep reading as live chrome). Re-published on every re-arrange, so a resize or a
                    // chrome-height change can never leave a stale rect behind.
                    OnRealized = h => { _contentRegionNode = h; PublishScrimClip(); },
                    OnBoundsChanged = _ => PublishScrimClip(),
                },
                Embed.Comp(() => new PlayerBar()),
            ],
        };

        _fileDrop ??= new DropTargetSpec(
            [DropKinds.Files],
            OnEnter: _ => _fileDropOver.Value = true,
            OnLeave: _ => _fileDropOver.Value = false,
            OnDrop: s =>
            {
                _fileDropOver.Value = false;
                if (s.Payload is FileDropData { Count: > 0 } files) LocalFileActions.PlayDropped(_actions, files.Paths);
            });
        // The shell BACKDROP — the material stack the whole authenticated shell sits on, bottom-up:
        //   1. LIVE MICA — the DWM window material itself (Program.cs asks for it: CustomFrame + MicaAlt). This box
        //      paints NOTHING, so the backdrop reads straight through every paint-site omission in the chrome (the
        //      title-bar drag bands, the sidebar band, the player dock).
        //   2. MATERIAL — ShellMaterialLayer, driven by the page-published signal: a flat tint (detail/artist pages
        //      publish their art colour at A=0.14) or Home's three clipped radial washes. A LOW-ALPHA scrim between
        //      Mica and the chrome, so the window material carries the album/artist hue instead of replacing it.
        //   3. the chrome column itself.
        // A ZStack, not a fill + one child: the material has to sit BETWEEN the backdrop and the column. The column
        // keeps its Grow=1 + viewport-bound height, so it fills the stack exactly as it filled the old parent.
        //
        // NOTE — the WINDOWS 11 MICA LAYERING (learn.microsoft.com system-backdrops + style/mica, explicit product
        // decision after two rejected alternatives). BASE LAYER: this root paints NOTHING — the merged row / sidebar /
        // player bands are paint-site omissions over live Mica, and a page's low-alpha tint composites over the
        // material. CONTENT LAYER: the content pane and rail band paint the stock WinUI LayerFillColorDefault rung
        // (WaveeColors.FileArea), a translucent smoke that keeps the page one step ABOVE the base on any wallpaper —
        // an opaque pane (both the deterministic #282828 and the "Files hybrid") inverted the model on light-tinted
        // wallpapers and read as a black slab. The merged single-row chrome is what makes bare Mica safe: no plates
        // remain, so the two-material seam the deterministic ground was invented for (D19) cannot recur.
        // ShellGround/#EDEDED and ContentSurface survive as the no-Mica fallbacks and floating-surface flatten bases.
        var tinted = new BoxEl
        {
            Grow = 1f, ZStack = true,
            Fill = ColorF.Transparent,
            DropTarget = _fileDrop,
            Children = [Embed.Comp(() => new ShellMaterialLayer(_shellMaterial, vpSig)), column],
        };

        // Transient toasts are now the engine's auto-mounted Toast host (a top-Z lane inside OverlayHost, InfoBar-chromed,
        // HostTimerQueue-driven with hover-pause). The bespoke Wavee ToastHost + its bottom-centre-above-the-bar positioner
        // were deleted in G6b; the engine host docks bottom-right (Toast.Placement) 24px from the window edge.
        // The local-playback setup banner FLOATS over the content (top-centre, just below the toolbar) instead of
        // inserting into the chrome column — a persistent offer must never reflow the page. Same pass-through positioner
        // pattern as the toast layer; the wrapper adds the overlay elevation the InfoBar itself doesn't carry. Sits
        // BELOW the toast layer so transient toasts stack above it.
        var runtimeBannerLayer = new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.Center,
            Padding = new Edges4(0f, 48f + 8f, 0f, 0f),   // clear the ONE merged chrome row (48) + a breathing gap
            Children =
            [
                new BoxEl { MaxWidth = 560f, Children = [ Embed.Comp(() => new PlaybackRuntimeChrome(_settings)) ] },
            ],
        };
        // The zero-size binder leaf lives INSIDE the OverlayHost subtree so it can capture the real overlay service
        // into the stable ActionServices bag (invoke-time dialogs: confirm / rename / add-to-playlist picker).
        // The in-window picture-in-picture video surface: a top-Z, pass-through floating layer (draggable + resizable),
        // visible only when the derived placement is the in-window PiP (VideoActive × VideoPlacement.InWindowPip). Reads
        // the resolved PopOutVideoSource + the backend-owned player (PlaybackBridge.VideoPlayer) from the bridge (provided at
        // the app root) and PRESENTS that player via PopOutVideoStage — no surface ever builds one, which is why moving
        // between the PiP and the pop-out does not restart the video. Sits above the content/banner layers so it floats over
        // the page + player bar; engine popups (in the
        // outer OverlayHost ZStack) still stack above it. VideoPlacementHost is the sibling CONTROLLER leaf (renders
        // empty) that owns the detached pop-out window's lifecycle off the same derived placement state.
        // The shell drop cue: a pass-through pill that fades in while a file drag is over the window (BOUND opacity, so
        // showing/hiding it is compositor-only — the shell is never re-rendered by a drag hover).
        var fileDropLayer = new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Opacity = Prop.Of(() => _fileDropOver.Value ? 1f : 0f),
            Children =
            [
                new BoxEl
                {
                    Padding = new Edges4(18f, 10f, 18f, 10f), Corners = CornerRadius4.All(Radii.Control),
                    Fill = Tok.FillSolidBase, BorderColor = Tok.AccentDefault, BorderWidth = 1f, Shadow = Elevation.Dialog,
                    Children = [new TextEl(Loc.Get(Strings.LocalFile.DropHint)) { Size = 14f, Color = Tok.TextPrimary }],
                },
            ],
        };
        // The IMMERSIVE LYRICS surface — a full-bleed layer directly above the chrome column (so it covers the content
        // card, the sidebar and the rail) and below every layer after it here, which is where the persistent banner,
        // the drop cue and the engine's auto-mounted toast / teaching-tip lane live. Flow.Show, not a `.Value` read in
        // this render body: the boundary mounts/unmounts the surface reactively without re-rendering the whole shell,
        // and its anchor is hit-test-transparent (MirrorParticipation), so the surface's own pass-through caption and
        // player-bar bands still reach the chrome underneath. Enter/Exit terminals come from the surface itself (they
        // read the reduced-motion VALUE at access time).
        var immersiveLyricsLayer = Flow.Show(
            () => _shellUi.ImmersiveLyrics.Value,
            new BoxEl
            {
                Grow = 1f, HitTestPassThrough = true,
                Enter = ImmersiveLyricsSurface.EnterTerminal,
                Exit = ImmersiveLyricsSurface.ExitTerminal,
                Children = [Embed.Comp(() => new ImmersiveLyricsSurface())],
            });
        var shellWithOverlays = Ui.ZStack(tinted, immersiveLyricsLayer, runtimeBannerLayer, fileDropLayer,
            Embed.Comp(() => new ActionServicesOverlayBinder(_actions)),
            // Zero-size chrome INSIDE the OverlayHost subtree (so UseContext(Overlay.Service) resolves the real service —
            // the same reason ActionServicesOverlayBinder lives here): opens the one-time design chooser once per install,
            // after the first painted frame.
            Embed.Comp(() => new SidebarOnboardingChrome(_settings)),
            // The sidebar projection binder's pump — zero-size, always-mounted, BELOW the HistoryStore provide so the
            // visited feed resolves (see SidebarProjectionBinder remarks). Nothing rebuilds the projection without it.
            _actions.Svc?.SidebarBinder.MountPoint() ?? new BoxEl { HitTestVisible = false, Shrink = 0f },
            Embed.Comp(() => new Wavee.Features.Video.InWindowVideoPip { Settings = _settings }),
            Embed.Comp(() => new Wavee.Features.Video.VideoPlacementHost { Settings = _settings }),
            DragPreviewLayer.Of(WaveeResourceDrag.Preview)) with { Grow = 1f };

        return Ctx.Provide(ShellUi.Slot, _shellUi,
               Ctx.Provide(ShellMaterial.Slot, _shellMaterial,
               Ctx.Provide(HistoryStore.BackCtx, (Action)Back,
               Ctx.Provide(HistoryStore.NavCtx, (Action<string, string?>)GoNav,
               Ctx.Provide(HistoryStore.Slot, _historyStore,
               Ctx.Provide(NavPreviewStore.Slot, _navPreview,
               Ctx.Provide(HomeSectionPreviewStore.Slot, _homeSectionPreview,
               Ctx.Provide(SearchQuery.Slot, _searchText,
               Ctx.Provide(ActionServices.Slot, _actions,
               Ctx.Provide(WaveeExtensionRegistry.Slot, _actions.Extensions,
               OverlayHost.Create(shellWithOverlays)))))))))));
    }

    /// <summary>The strip, plus the per-render push of the ladder's tab metrics onto the MOUNTED instance. A component's
    /// plain fields freeze at mount, so <c>MinTabWidth</c>/<c>MaxTabWidth</c> cannot be re-passed through the factory;
    /// this host runs inside the TITLE BAR's render — i.e. before the strip's own — so the mutation always lands in the
    /// same frame as the <see cref="TabStripItemsVersion"/> bump that makes the strip re-read them.</summary>
    Element TabStripHost()
    {
        if (_strip is { } s)
        {
            float cap = _chromeLayout.Peek().TabMaxWidth;
            s.MaxTabWidth = cap;
            s.MinTabWidth = MathF.Min(ShellResponsiveLayout.ChromeTabMinW, cap);
        }
        return Embed.Comp(BuildTabStrip);
    }

    TabStrip BuildTabStrip()
    {
        var strip = new TabStrip
        {
            // TEXT-FIRST (Zune) tabs: no plate, no flare, no separators, no rail — weight + opacity carry selection and
            // one strip-owned sliding 2-DIP accent underline marks it. That is what lets the tab strip share a 48-DIP
            // row with the search and the identity cluster; SelectedFill/TabWidth are Chrome-grammar and ignored here.
            Appearance = TabStripAppearance.Text,
            TextFontSize = 13f,
            // The "+" is back, but HOVER-ONLY: at rest it paints nothing (Ctrl+T stays the power affordance), and it
            // cross-fades in whenever the pointer is over the strip. Its 32-DIP slot is RESERVED at every moment
            // regardless — the strip hugs, and TitleBar reports that hug wholesale as one TitleBarHit.Client region, so
            // a mount-on-hover would move the reported rect on every pointer entry and would have to join
            // MergedChromeRow.ContentVersion. A permanently reserved slot inside an island that is already entirely
            // client-hit-tested keeps the region rect (and this fold) completely still.
            IsAddTabButtonVisible = true,
            AddButtonVisibility = TabStripAddButtonVisibility.OnStripPointerOver,
            OnAddTabButtonClick = () => { OpenNewTab("home"); return null; },
            IndicatorFill = Prop.Of(() => Tok.AccentDefault),
            MinTabWidth = ShellResponsiveLayout.ChromeTabMinW,
            MaxTabWidth = ShellResponsiveLayout.ChromeTabMaxW,
            ItemsSource = BuildTabItems,
            ItemsVersion = TabStripItemsVersion,
            // STRIP index space (the visible slice), not the open list — both handlers map back through _visibleTabs.
            SelectedIndex = _stripSelected,
            OnSelectionChanged = i =>
            {
                if ((uint)i >= (uint)_visibleTabs.Count) return;
                int open = _visibleTabs[i];
                if ((uint)open >= (uint)_open.Count) return;
                TouchTab(open);
                _selectedTab.SetIfChanged(open);
                Go(_open[open].Key, _open[open].Arg, NavTransitionKind.Neutral);
            },
            OnTabCloseRequested = i => { if ((uint)i < (uint)_visibleTabs.Count) CloseTab(_visibleTabs[i]); },
        };
        // Embed.Comp runs its factory once and the reconciler mounts THAT instance, so the first one built is the live
        // one (and a defensive extra call still leaves this pointing at the mounted instance).
        _strip ??= strip;
        return strip;
    }

    int TitleBarTabsVersion()
    {
        int version = _tabsVersion.Value;
        int selected = _selectedTab.Value;
        int slice = _tabStripVersion.Value;
        return unchecked((version * 397 ^ selected) * 397 ^ slice);
    }

    /// <summary>The strip's own revision: the tab set, the visible SLICE, and the ladder's per-tab cap (which the strip
    /// re-reads off its mutated fields). Read from the strip's render, so each of these subscribes it.</summary>
    int TabStripItemsVersion()
    {
        int version = _tabsVersion.Value;
        int slice = _tabStripVersion.Value;
        int cap = (int)_chromeLayout.Value.TabMaxWidth;
        return unchecked((version * 397 ^ slice) * 397 ^ cap);
    }

    /// <summary>Which open tabs are in the strip right now. The ACTIVE tab never leaves; the remaining slots go to the
    /// most-recently-used tabs, and any slot the (short) MRU cannot fill goes in open order. Pure — it writes only the
    /// plain scratch lists, so the off-render projection effect and the ItemsSource fallback can both call it.</summary>
    void RebuildVisibleTabs(int keep, int selected)
    {
        _visibleTabs.Clear();
        int n = _open.Count;
        if (n == 0) return;
        if (keep >= n) { for (int i = 0; i < n; i++) _visibleTabs.Add(i); return; }
        if (keep < 1) keep = 1;
        if ((uint)selected >= (uint)n) selected = 0;

        _keepIds.Clear();
        _keepIds.Add(_open[selected].Id);
        for (int i = 0; i < _tabMru.Count && _keepIds.Count < keep; i++)
            if (IndexOfTabId(_tabMru[i]) >= 0) _keepIds.Add(_tabMru[i]);
        for (int i = 0; i < n && _keepIds.Count < keep; i++) _keepIds.Add(_open[i].Id);
        for (int i = 0; i < n; i++) if (_keepIds.Contains(_open[i].Id)) _visibleTabs.Add(i);
    }

    /// <summary>The tabs the ladder folded out of the strip, MOST-RECENTLY-USED first (the order the user thinks in),
    /// with anything the MRU has not seen appended in open order. Returns a reused list — callers copy out at once.</summary>
    List<ChromeTabRef> HiddenTabs()
    {
        _hiddenScratch.Clear();
        int n = _open.Count;
        if (_visibleTabs.Count == 0) RebuildVisibleTabs(_chromeLayout.Peek().KeepTabs, _selectedTab.Peek());
        if (_visibleTabs.Count >= n) return _hiddenScratch;
        for (int i = 0; i < _tabMru.Count; i++)
        {
            int idx = IndexOfTabId(_tabMru[i]);
            if (idx >= 0 && !_visibleTabs.Contains(idx)) AddHidden(idx);
        }
        for (int i = 0; i < n; i++) if (!_visibleTabs.Contains(i)) AddHidden(i);
        return _hiddenScratch;

        void AddHidden(int idx)
        {
            for (int k = 0; k < _hiddenScratch.Count; k++) if (_hiddenScratch[k].Index == idx) return;
            var t = _open[idx];
            _hiddenScratch.Add(new ChromeTabRef(idx, t.Label, t.Glyph));
        }
    }

    int IndexOfTabId(int id)
    {
        for (int i = 0; i < _open.Count; i++) if (_open[i].Id == id) return i;
        return -1;
    }

    /// <summary>Mark a tab as just-used. The LRU squeeze reads this list, so every path that CHANGES which tab is
    /// active has to go through it (strip click, spring-load, overflow pick, new tab, close).</summary>
    void TouchTab(int index)
    {
        if ((uint)index >= (uint)_open.Count) return;
        int id = _open[index].Id;
        _tabMru.Remove(id);
        _tabMru.Insert(0, id);
    }

    IReadOnlyList<TabViewItem> BuildTabItems()
    {
        // Fallback for the very first pass, before the projection effect has run: never render an empty strip.
        if (_visibleTabs.Count == 0) RebuildVisibleTabs(_chromeLayout.Peek().KeepTabs, _selectedTab.Peek());
        var items = new TabViewItem[_visibleTabs.Count];
        for (int k = 0; k < items.Length; k++)
        {
            int i = _visibleTabs[k];
            var tab = _open[i];
            var destination = DestinationOf(tab);
            // The closures below capture the OPEN-list index, so the drag payload, the spring-load activation and the
            // context menu keep pointing at the right tab even though the strip's own index is `k`.
            int index = i;
            items[k] = new TabViewItem
            {
                Header = tab.Label,
                Icon = tab.Glyph,
                IsClosable = _open.Count > 1,
                ContextMenu = destination is null ? null : () => TabMenu(tab),
                // A tab is CLICK-PRIMARY: switching tabs is the constant intent, dragging one out the rare one. At the
                // base 4px box a click landed while the mouse is still travelling promotes to a drag and its click is
                // suppressed — the tab silently fails to select. WinUI widens the mouse box ×2 on list items for exactly
                // this reason (LISTVIEWBASEITEM_MOUSE_DRAG_THRESHOLD_MULTIPLIER).
                Drag = destination is { } d
                    ? Drag.Source(WaveeDragKinds.Resource,
                        () => WaveeResourceDragPayload.FromDestination(d, _actions),
                        thresholdMultiplier: Drag.ClickPrimaryThresholdMultiplier)
                    : null,
                // Spring-load NAVIGATION on every tab. Holding a drag over a tab switches to it, so the destination the
                // user actually wants can be reached mid-gesture instead of forcing them to cancel, navigate and start
                // over. On a tab that is NOT a deposit destination, SpringLoadOnly is what keeps that honest: the tab
                // never accepts a drop and is never published as a refusal either, so merely travelling ACROSS the strip
                // on the way to the sidebar leaves the chip silent rather than flashing a not-allowed glyph at each tab.
                //
                // A tab standing for an EDITABLE PLAYLIST is also a real destination — dropping tracks on it appends
                // them to that playlist without ever leaving the page you are on, the cross-tab deposit. The two
                // coexist by construction: FindTarget resolves the spring host BEFORE acceptance, so the same spec both
                // opens the tab on a dwell and takes the drop on a release.
                DropTarget = TabDropTarget(destination, index),
            };
        }
        return items;
    }

    /// <summary>One tab's drop target: always the spring-load waypoint, plus a track deposit when the tab stands for a
    /// playlist this user can write to (see <see cref="TabDropRules"/> for the decision and why the same-playlist cases
    /// are REFUSED rather than left to no-op inside the deposit).</summary>
    DropTargetSpec TabDropTarget(SidebarDestination? destination, int index)
    {
        var acts = _actions;
        if (destination is { Kind: SidebarPinKind.Playlist } d && WaveeRootlist.CanEditPlaylist(acts, d.Uri))
        {
            string uri = d.Uri, name = d.Name;
            return Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
                accepts: p => TabDropRules.AcceptsDeposit(uri, targetEditable: true, p.CanCopyTracks,
                                                          p.SourcePlaylistUri, p.Uri),
                caption: _ => Strings.Drag.AddTo(name),
                // insertionIndex null = APPEND: a tab has no slot to speak of, exactly like the page-body target.
                // DepositTracksAsync owns the toast and the error path.
                onDrop: (p, _) => WaveeResourceDrop.DepositTracks(acts, uri, name, p, insertionIndex: null),
                // The deposited feel: the lifted visual snaps home rather than gliding into a list it never joined.
                settleOnDrop: false,
                springLoadMs: WaveeResourceDrag.SpringLoadMs,
                onSpringLoad: (_, _) => ActivateTab(index));
        }
        return Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
            springLoadMs: WaveeResourceDrag.SpringLoadMs,
            onSpringLoad: (_, _) => ActivateTab(index),
            springLoadOnly: true);
    }

    SidebarDestination? CurrentSidebarDestination()
    {
        var route = _route.Peek();
        var (title, _) = string.Equals(route.Name, "search", StringComparison.Ordinal)
            ? ShellNav.Dest("search")
            : ShellNav.Dest(route);
        return SidebarDestination.FromRoute(route.Name, route.Arg, title);
    }

    static SidebarDestination? DestinationOf(OpenTab tab)
    {
        string title = string.Equals(tab.Key, "search", StringComparison.Ordinal)
            ? ShellNav.Dest("search").Title
            : tab.Label;
        return SidebarDestination.FromRoute(tab.Key, tab.Arg, title);
    }

    ContextMenuModel? TabMenu(OpenTab tab)
    {
        if (DestinationOf(tab) is not { } destination
            || PinActions.RowForDestination(_actions, in destination) is not { } row) return null;
        return new ContextMenuModel([row], new ContextMenuHeader(null, tab.Label));
    }

    int ActiveTabId()
    {
        _ = _tabsVersion.Value;
        int i = _selectedTab.Value;
        return (uint)i < (uint)_open.Count ? _open[i].Id : -1;
    }

    // ── navigation (the single source of truth the chrome reads) ─────────────────────────────────
    void Go(string key, string? arg, NavTransitionKind motion = NavTransitionKind.Forward)
    {
        CloseNarrowDrawer();
        _history.Add(_route.Peek());
        if (_history.Count > MaxBackStack) _history.RemoveAt(0);   // bound the in-memory back-stack
        _forwardHistory.Clear();
        _canForward.Value = false;
        _navMotion.Value = motion;
        _route.Value = new Route(key, arg);
        _canBack.Value = _history.Count > 0;
        _historyStore.Add(_route.Peek());
        RecordRecentSurface(_route.Peek());
        SyncActiveTab(_route.Peek());
    }

    // Addendum A5 — the `recent_surfaces` pin reason. Hooked to FORWARD navigation only: Back/Forward re-visit a surface
    // through history, which is not a new "open" and must not churn the 50-slot LRU (the surface is already in it).
    // Runs on the UI thread, so nothing here may touch SQLite: RecordRecentSurface updates the in-memory pin mirror
    // synchronously (the write gate has to see the new pin on the very next upsert) and lanes the row + the entity flush
    // onto CachedStore's background writer. Null RealStore (fake/offline backend) ⇒ no-op.
    void RecordRecentSurface(Route r)
    {
        if (!Wavee.Backend.Persistence.RecentSurfaceRoute.TryClassify(r.Name, out var uri, out var kind)) return;
        if (_actions.Svc?.RealStore is Wavee.Backend.Persistence.CachedStore store) store.RecordRecentSurface(uri, (int)kind);
    }

    void Back()
    {
        if (_history.Count == 0) return;
        CloseNarrowDrawer();
        _forwardHistory.Add(_route.Peek());
        if (_forwardHistory.Count > MaxBackStack) _forwardHistory.RemoveAt(0);
        _canForward.Value = true;
        _navMotion.Value = NavTransitionKind.Back;
        _route.Value = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _canBack.Value = _history.Count > 0;
        _historyStore.Add(_route.Peek());
        SyncActiveTab(_route.Peek());
    }

    void Forward()
    {
        if (_forwardHistory.Count == 0) return;
        CloseNarrowDrawer();
        _history.Add(_route.Peek());
        _canBack.Value = true;
        _navMotion.Value = NavTransitionKind.Forward;
        _route.Value = _forwardHistory[^1];
        _forwardHistory.RemoveAt(_forwardHistory.Count - 1);
        _canForward.Value = _forwardHistory.Count > 0;
        _historyStore.Add(_route.Peek());
        SyncActiveTab(_route.Peek());
    }

    void Home() => Go("home", null);

    // History always opens in its own tab (global view — same as browser convention).
    void GoNav(string key, string? arg)
    {
        // A search route carries the query in Arg → sync the omnibar text so the box + the SearchPage (which reads
        // SearchQuery.Slot live, as-you-type) agree, whether the nav came from the box, a history entry, or a suggestion.
        if (key == "search" && arg is { Length: > 0 }) _searchText.Value = arg;
        if (key == "history") OpenNewTab(key);
        else Go(key, arg);
    }

    void SyncActiveTab(Route r)
    {
        int i = _selectedTab.Peek();
        if ((uint)i >= (uint)_open.Count) return;
        var (title, glyph) = ShellNav.Dest(r);
        _open[i] = _open[i] with { Key = r.Name, Label = title, Glyph = glyph, Arg = r.Arg };
        _tabsVersion.Value = _tabsVersion.Peek() + 1;
    }

    /// <summary>Select tab <paramref name="i"/> AND follow its route — the pair every tab activation needs (writing the
    /// selection signal alone moves the highlight without navigating, which is what makes a spring-load useless).</summary>
    void ActivateTab(int i)
    {
        if ((uint)i >= (uint)_open.Count || _selectedTab.Peek() == i) return;
        TouchTab(i);
        _selectedTab.Value = i;
        var t = _open[i];
        Go(t.Key, t.Arg, NavTransitionKind.Neutral);
    }

    void OpenNewTab(string key)
    {
        var (title, glyph) = ShellNav.Dest(key, null);
        _open.Add(new OpenTab(_nextTabId++, key, title, glyph, null));
        _selectedTab.Value = _open.Count - 1;
        TouchTab(_open.Count - 1);
        _tabsVersion.Value = _tabsVersion.Peek() + 1;
        Go(key, null, NavTransitionKind.Neutral);
    }

    void CloseTab(int i)
    {
        if (_open.Count <= 1 || (uint)i >= (uint)_open.Count) return;
        _tabMru.Remove(_open[i].Id);
        _open.RemoveAt(i);
        int sel = _selectedTab.Peek();
        if (i < sel) sel--;
        else if (i == sel) sel = Math.Min(i, _open.Count - 1);
        sel = Math.Clamp(sel, 0, _open.Count - 1);
        _selectedTab.Value = sel;
        TouchTab(sel);
        _tabsVersion.Value = _tabsVersion.Peek() + 1;
        var t = _open[sel];
        Go(t.Key, t.Arg, NavTransitionKind.Neutral);
    }

    /// <summary>Template overrides for the merged chrome row. The bar lays its pane toggle out at x=4 (root padding 2 +
    /// the button's own margin 2) and it is 40 wide, so its centre lands at 24 — while the 56-DIP compact rail centres
    /// the sidebar's icon column at 28. Nudge the painted slot four DIP right and take the four back on the trailing
    /// side, so the hamburger lines up with the icons directly beneath it and NOTHING after it moves (the idiom the
    /// two-row toolbar used on the same button, restored now that the button lives in the bar's own lead column).
    /// <para>Margin is not an owned property of <c>PartPaneToggle</c> (the control owns OnClick/Role/OnRealized/Children
    /// and re-applies exactly those after the part function), so overriding it here is inside the template contract.
    /// Built ONCE: mutating a TemplateParts bumps its Epoch and invalidates the engine's apply-once prototype cache.</para></summary>
    static readonly TemplateParts ChromeParts = BuildChromeParts();

    static TemplateParts BuildChromeParts()
    {
        var parts = new TemplateParts();
        parts[TitleBar.PartPaneToggle] = b => b with { Margin = new Edges4(6f, 2f, -2f, 2f) };
        return parts;
    }

    // Shell-wide keyboard chords (see the accelerator hosts in the column). Ctrl+T opens a new Home tab; Ctrl+K puts the
    // caret in the omnibar, EXPANDING it first when the ladder has collapsed it to the magnifier.
    static readonly KeyAccelerator NewTabChord = new(Keys.T, KeyModifiers.Ctrl);
    static readonly KeyAccelerator FocusSearchChord = new(Keys.K, KeyModifiers.Ctrl);

    void FocusSearch()
    {
        if (_chromeLayout.Peek().SearchMode == MergedSearchMode.Icon) _searchExpanded.SetIfChanged(true);
        _searchFocusRequest.Value = _searchFocusRequest.Peek() + 1;
    }

    void ToggleTheme()
    {
        var next = Theme.Dark ? ThemeKind.Light : ThemeKind.Dark;
        Tok.Use(WaveeTheme.ResolvePalette(_settings.Get(WaveeSettings.PaletteId)), next);
        _settings.Set(WaveeSettings.ThemeMode, next == ThemeKind.Dark ? 2 : 1);
        _requestTheme?.Invoke(250f);
    }
}

/// <summary>Narrow-shell light-dismissible pane. It stays mounted while closed so its sidebar state and scene nodes are
/// retained; open/close is compositor-only (scrim opacity + pane translation).</summary>
sealed class ShellNarrowDrawer : Component
{
    readonly IReadSignal<bool> _narrow;
    readonly Signal<bool> _open;
    readonly IReadSignal<Size2> _viewport;
    readonly Signal<float> _expandedWidth;
    readonly Signal<bool> _drawerCompact;
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;

    public ShellNarrowDrawer(IReadSignal<bool> narrow, Signal<bool> open, IReadSignal<Size2> viewport,
        Signal<float> expandedWidth, Signal<bool> drawerCompact, Signal<Route> route, Action<string, string?> go)
    {
        _narrow = narrow; _open = open; _viewport = viewport; _expandedWidth = expandedWidth;
        _drawerCompact = drawerCompact; _route = route; _go = go;
    }

    public override Element Render()
    {
        bool open = _narrow.Value && _open.Value;
        var hooks = UseContext(InputHooks.Current);
        var savedPreview = UseRef<Func<int, bool>?>(null);
        var escPreview = UseRef<Func<int, bool>?>(null);
        var escInstalled = UseRef(false);

        escPreview.Value ??= key =>
        {
            if ((key == Keys.Escape || key == Keys.GamepadB) && _open.Peek())
            {
                _open.Value = false;
                return true;
            }
            return savedPreview.Value?.Invoke(key) ?? false;
        };
        UseEffect(() =>
        {
            if (open && !escInstalled.Value)
            {
                escInstalled.Value = true;
                savedPreview.Value = hooks.KeyPreview;
                hooks.KeyPreview = escPreview.Value;
            }
            else if (!open && escInstalled.Value)
            {
                escInstalled.Value = false;
                if (ReferenceEquals(hooks.KeyPreview, escPreview.Value)) hooks.KeyPreview = savedPreview.Value;
                savedPreview.Value = null;
            }
        }, open);

        return new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestVisible = open,
            Children =
            [
                Embed.Comp(() => new ShellNarrowDrawerScrim(_open)),
                new BoxEl
                {
                    Grow = 1f, Direction = 0, Justify = FlexJustify.Start, HitTestPassThrough = true,
                    Children =
                    [
                        Embed.Comp(() => new ShellNarrowDrawerPane(
                            _open, _viewport, _expandedWidth, _drawerCompact, _route, _go)),
                    ],
                },
            ],
        };
    }
}

sealed class ShellNarrowDrawerScrim : Component
{
    readonly Signal<bool> _open;
    public ShellNarrowDrawerScrim(Signal<bool> open) => _open = open;

    public override Element Render()
    {
        bool open = _open.Value;
        var mounted = UseRef(false);
        float ms = Motion.ReducedMotion ? 0f : WaveeMotion.Fast;
        float target = ShellResponsiveLayout.DrawerRestingOpacity(open);
        UseTransition(AnimChannel.Opacity, mounted.Value ? 1f - target : target, target,
            ms, Easing.Linear, open);
        mounted.Value = true;
        return new BoxEl
        {
            Grow = 1f, Fill = ColorF.FromRgba(0, 0, 0, 0x33), Opacity = target,
            HitTestVisible = open, OnClick = () => _open.Value = false,
        };
    }
}

sealed class ShellNarrowDrawerPane : Component
{
    readonly Signal<bool> _open;
    readonly IReadSignal<Size2> _viewport;
    readonly Signal<float> _expandedWidth;
    readonly Signal<bool> _drawerCompact;
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;

    public ShellNarrowDrawerPane(Signal<bool> open, IReadSignal<Size2> viewport, Signal<float> expandedWidth,
        Signal<bool> drawerCompact, Signal<Route> route, Action<string, string?> go)
    {
        _open = open; _viewport = viewport; _expandedWidth = expandedWidth; _drawerCompact = drawerCompact;
        _route = route; _go = go;
    }

    public override Element Render()
    {
        bool open = _open.Value;
        var mounted = UseRef(false);
        float width = ShellResponsiveLayout.DrawerWidth(_viewport.Peek().Width, _expandedWidth.Peek());
        float ms = Motion.ReducedMotion ? 0f : 300f;
        float target = ShellResponsiveLayout.DrawerRestingTranslateX(open, width);
        UseTransition(AnimChannel.TranslateX, mounted.Value ? (open ? -width : 0f) : target, target,
            ms, Easing.SmoothOut, open);
        mounted.Value = true;

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Grow = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
            // Keep the resting position coupled to the live width. A closed drawer can grow while the viewport is
            // resized inside the narrow band; retaining the old static translation would expose the newly-added strip.
            // The transition channel owns the in-flight value, then this binding supplies the exact settled geometry.
            Transform = Prop.Of(() => Affine2D.Translation(
                ShellResponsiveLayout.DrawerRestingTranslateX(
                    _open.Value,
                    ShellResponsiveLayout.DrawerWidth(_viewport.Value.Width, _expandedWidth.Value)),
                0f)),
            Width = Prop.Of(() => ShellResponsiveLayout.DrawerWidth(
                _viewport.Value.Width, _expandedWidth.Value)),
            // A stock OVERLAY PANE, not a dialog: in-app ACRYLIC + flyout elevation, exactly what WinUI's own
            // NavigationView paints for its minimal-mode pane (NavigationViewDefaultPaneBackground =
            // AcrylicInAppFillColorDefault, Shadow = Flyout — see NavigationView.cs "Overlay pane"). The old
            // dead-opaque FloatingChrome plate + Elevation.Dialog (blur 64) read as a modal slab dropped on the
            // window; the drawer is a transient light-dismiss surface and has to say so. Fill stays transparent —
            // the acrylic layer IS the surface (the OverlayHost popup-chrome idiom), and its own FallbackColor
            // covers the no-blur path, so this never degrades to a hole.
            Fill = ColorF.Transparent,
            Acrylic = Tok.AcrylicFlyout,
            BorderWidth = 1f, BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
            Corners = new CornerRadius4(0f, Radii.Card, Radii.Card, 0f),
            Shadow = Elevation.Flyout, HitTestVisible = open,
            // A SECOND, independent SidebarHost mount (its own hooks / scroll / mode-component instance) sharing the same
            // width signal and the same SidebarPreferences — one mode, one state, two mounts.
            Children = [Embed.Comp(() => new SidebarHost(_route, _go, _drawerCompact, _expandedWidth, inDrawer: true))],
        };
    }
}
