using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
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
    // Page-scoped Mica tint: a detail page writes its art colour here while active; the shell paints it as a low-alpha
    // scrim BEHIND the chrome (which is translucent over Mica), so the window material carries the colour. Null ⇒ plain
    // Mica. Owner-gated writes (ShellTintState) make A→B navigation race-free. Provided at the root via ShellTint.Slot.
    readonly Signal<ShellTintState> _shellTint = new(default);

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

    readonly Signal<string> _searchText = new("");
    // Sidebar state. Collapsed and (once pinned by a drag) width are seeded in the ctor from the injected settings so the
    // FIRST layout already uses the saved values — no startup animation; written back on change via SaveSidebar.
    readonly IAppSettings _settings;
    readonly Signal<bool> _drawerExpanded = new(false);
    Signal<bool>? _narrowShellState;
    Signal<bool>? _narrowDrawerState;
    Signal<bool>? _presentedCompactState;
    readonly Signal<bool> _sidebarCompact;
    readonly Signal<float> _sidebarWidth;                       // expanded width (drag-resizable, persisted)
    readonly Signal<bool> _sidebarDragging = new(false);       // ON during a seam drag → snaps all layout transitions (1:1 resize)
    // Latches true on the first seam-drag commit (mirrors WaveeSettings.SidebarWidthUserSet). While false the pane width is
    // a responsive default owned by the viewport effect below; once true, the persisted width is the user's and nothing
    // else may write it. Plain field, not a signal: only the effect reads it, and it never needs to re-trigger a render.
    bool _sidebarWidthUserSet;
    bool _navPaneTierSeeded;                                   // false until the first effect run with a real viewport width
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

    public WaveeShell(IAppSettings settings)
    {
        _settings = settings;
        _sidebarCompact = new(settings.Get(WaveeSettings.SidebarCollapsed));
        _sidebarWidthUserSet = settings.Get(WaveeSettings.SidebarWidthUserSet);
        // A dragged width is a preference and seeds verbatim. An undragged one is only the last responsive default, so it
        // seeds from the tier ladder instead — the viewport is not known here (no bounds callback yet), so this takes the
        // pre-measure fallback and the viewport effect in Render (which runs at mount, before the first layout) commits
        // the real tier without a visible step.
        _sidebarWidth = new(_sidebarWidthUserSet
            ? Math.Clamp(settings.Get(WaveeSettings.SidebarWidth), ShellResponsiveLayout.NavPaneMinW, ShellResponsiveLayout.NavPaneMaxW)
            : ShellResponsiveLayout.InitialNavPaneDefaultForViewport(0f));

        // Inert probe (screenshot / UI iteration only): open the right rail to the Lyrics panel at startup.
        if (Diag.EnvFlag("WAVEE_LYRICS_OPEN") || Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE")) { _shellUi.RailOpen.Value = true; _shellUi.Mode.Value = RailMode.Lyrics; }
        if (Diag.EnvFlag("WAVEE_NOWPLAYING_OPEN")) { _shellUi.RailOpen.Value = true; _shellUi.Mode.Value = RailMode.Details; }

        if (Diag.EnvFlag("WAVEE_NAV_PROBE") || Diag.EnvFlag("WAVEE_RESIZE_PROBE") || Diag.EnvFlag("WAVEE_CONN_STRESS") || Diag.EnvFlag("WAVEE_TRACKLIST_SHOT") || Diag.EnvFlag("WAVEE_HERO_SHOT") || Diag.EnvFlag("WAVEE_SHELF_SHOT") || Diag.EnvFlag("WAVEE_RAIL_SHOT") || Diag.EnvFlag("WAVEE_HOME_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_RAIL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_PROBE") || Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE") || Diag.EnvFlag("WAVEE_MEM_SOAK") || Diag.EnvFlag("WAVEE_PERF_BENCH"))
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
        }

        _historyStore.Init(HistoryFilePath());
        _historyStore.LoadFromDisk();

        _historyStore.Add(new Route("home"));   // record this session's first visit
        if (_historyStore.Entries.Count == 1)   // only seed fake data on a fresh install (nothing loaded from disk)
            SeedFakeHistory();
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
    void SaveSidebar(bool widthUserSet = false)
    {
        _settings.Set(WaveeSettings.SidebarWidth, _sidebarWidth.Peek());
        _settings.Set(WaveeSettings.SidebarCollapsed, _sidebarCompact.Peek());
        if (!widthUserSet || _sidebarWidthUserSet) return;
        _sidebarWidthUserSet = true;
        _settings.Set(WaveeSettings.SidebarWidthUserSet, true);
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
        // Three writers must never fight over _sidebarWidth, so this one yields to both of the others:
        //   • a pinned width — _sidebarWidthUserSet, latched by the drag commit, is a permanent opt-out;
        //   • a live drag — Peek-gated (no subscription): OnMove owns the width 1:1 while the pointer is down. A drag ends
        //     with SaveSidebar(widthUserSet: true), so after any committed drag this effect is inert anyway.
        // vpSig is read FIRST and unconditionally: an early return before the read would drop the subscription and the
        // tier would never update again. The pane's SidebarPaneAnim animates whatever this commits, for free.
        UseSignalEffect(() =>
        {
            float w = vpSig.Value.Width;
            if (_sidebarWidthUserSet || _sidebarDragging.Peek()) return;
            float next = ShellResponsiveLayout.NavPaneDefaultFor(w, _sidebarWidth.Peek(), _navPaneTierSeeded);
            if (w > 0f) _navPaneTierSeeded = true;
            _sidebarWidth.SetIfChanged(next);
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

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f, Height = Prop.Of(() => vpSig.Value.Height),   // window-tall → content yields, never overflows the player bar
            Children =
            [
                // Zero-size, renders nothing: owns the ambient-cadence policy's two subscriptions (window activation +
                // the debounced power poll). It lives here because the shell is the one always-mounted host in the tree.
                Embed.Comp(() => new AmbientPowerPolicy.Watcher()),
                Embed.Comp(() => new TitleBar
                {
                    IconGlyph = "", ShowPaneToggle = false, ShowCaptionButtons = true,
                    Tabs = () => Embed.Comp(BuildTabStrip),
                    TabsVersion = TitleBarTabsVersion,
                }),
                Embed.Comp(() => new ShellToolbar(_route, _canBack, _canForward, GoNav, Back, Forward, Home,
                    _searchText, ToggleSidebar, ToggleTheme, _history, _forwardHistory)),
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
                            Direction = 1, Shrink = 0f, ClipToBounds = true, Fill = Prop.Of(() => WaveeColors.Sidebar),
                            Width = Prop.Of(() => presentedCompact.Value
                                ? ShellResponsiveLayout.CompactRailW : _sidebarWidth.Value),
                            // SidebarPaneAnim eases the COLLAPSE toggle (56↔expanded) as a clip+translate reveal — the pane
                            // is ClipToBounds so the reveal scissors its content. During a drag the suppression arbiter
                            // snaps every layout transition (this pane AND the sidebar sections) to the laid-out width 1:1.
                            Animate = SidebarPaneAnim,
                            Children =
                            [
                                // Content fades (compositor-only) toward the collapse detent; the chrome fill stays solid.
                                // Column wrapper so WaveeSidebar's Grow=1f fills our HEIGHT (its ScrollView needs a definite one).
                                new BoxEl
                                {
                                    Direction = 1, Grow = 1f,
                                    Opacity = Prop.Of(() => _sidebarFade.Value),
                                    Children = [ Embed.Comp(() => new WaveeSidebar(_route, GoNav, presentedCompact, _sidebarWidth)) ],
                                },
                            ],
                        },
                            // Content side: an inset, rounded, shadowed "page" over the Toolbar-chrome backing.
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
                                    // The app-body PLATE behind the translucent page and its rounded top-left cut-away
                                    // (rung 1 = LayerOnMicaBaseAlt, the same bound fill as the toolbar / nav pane /
                                    // player dock) — so the page's corner cut-away and the trailing gap show the plate,
                                    // not bare Mica. Do not add a sidebar-colored seam strip here: it remains visible
                                    // through the page surface as a full-height opaque rail and squares off the corner.
                                    new BoxEl { Grow = 1f, Fill = Prop.Of(() => WaveeColors.Toolbar) },
                                    // Static final-geometry underlay — and, at rest, THE content-pane surface itself
                                    // (rung 2): the animated card above paints no fill, so this one layer of FileArea
                                    // is the pane and it is never double-composited. WinUI translates ContentRoot but
                                    // does not animate its width; while that translated card moves, this matching
                                    // surface prevents the trailing side from exposing plate chrome (the false "right
                                    // rail ghost") — and closer to WinUI, where ContentRoot translates and the surface
                                    // beneath does not.
                                    new BoxEl
                                    {
                                        Grow = 1f, Margin = new Edges4(0f, 0f, Spacing.S, 0f),
                                        Fill = Prop.Of(() => WaveeColors.FileArea),
                                        Corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f),
                                        // Wino/WinUI content zones sit one elevation step above commanding chrome.
                                        // Keep the shadow on this STATIC final-geometry pane: putting it on the animated
                                        // transparent card would make the elevation itself slide during sidebar FLIP.
                                        Shadow = Elevation.Card,
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
                                        // Flush against the navigation pane and player dock; only the trailing edge is inset.
                                        Margin = new Edges4(0f, 0f, Spacing.S, 0f),
                                        // NO fill: the static underlay above owns the pane surface. This card and that
                                        // underlay are coincident at rest (both Grow=1, same trailing margin, same
                                        // corner), so a fill here composited the translucent FileArea TWICE — the pane
                                        // read a rung too light (#333333 instead of #303030 dark) and mid-slide the
                                        // moving card banded against the static surface. The card keeps the stroke, the
                                        // corner, the clip and the FLIP; only the paint moved down one node.
                                        // (Regression tell: a dark pane sampling #333333 means this fill came back.)
                                        Fill = ColorF.Transparent,
                                        BorderWidth = 1f,
                                        BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
                                        // Stock NavigationViewContentGridCornerRadius = 8,0,0,0: only the corner facing
                                        // the nav pane rounds. (The seam stroke is uniform 1px — per-side borders are
                                        // unsupported, so the ring is an accepted deviation from the top+left-only rule.)
                                        Corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f),
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
                                ],
                            },
                            // Right rail RESERVATION spacer — the WaveeMusic-style lyrics / now-playing band. A literal row
                            // child (Shrink=0, bound width) so the content card re-tiles against it. Projected motion: this
                            // width flips 0<->RailWidth at commit with NO Animate — it SNAPS to the reserved extent, and the
                            // content card's FLIP (SidebarReveal) absorbs the shift while the rail overlay slide-reveals into
                            // the reserved band. (Animating both the spacer AND the overlay was the old double width track.)
                            new BoxEl
                            {
                                Shrink = 0f,
                                // CHROME backing: the rail's rounded top-left wedge (underlay + overlay above) reads against this.
                                Fill = Prop.Of(() => WaveeColors.Toolbar),
                                Width = Prop.Of(() => _shellUi.RailOpen.Value && _shellUi.RailFits.Value ? _shellUi.RailWidth.Value : 0f),
                                Animate = RailSpacerAnim,   // null (snap) in the projected path; Reflow spring in the baseline
                                Children =
                                [
                                    // Static final-geometry underlay — the rail-side twin of the content card's underlay
                                    // above. The reserved width SNAPS at commit while RightRail translates its panel in
                                    // over 300ms; without this, that band is raw Toolbar chrome for the whole slide and
                                    // the open reads as "the content jumps away from a dark hole, then a panel arrives".
                                    // Painting the rail's own surface here (same Fill + same top-left card corner as
                                    // RightRail's surface) means the band IS the rail from frame 0 and only the panel's
                                    // CONTENT is seen to arrive. Paint-only: never a hit target, no opacity track (a
                                    // fading full-height rail surface is what produced the old "ghost rail").
                                    new BoxEl
                                    {
                                        Grow = 1f, HitTestPassThrough = true,
                                        Fill = Prop.Of(() => WaveeColors.FileArea),
                                        Corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f),
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
                                    // Opaque backing band for the FLOATING overlay only. Docked stays transparent: the
                                    // rail's rounded top-left wedge must show the chrome behind it, exactly like the
                                    // card's rounded top-right on the other side of the gap.
                                    new BoxEl
                                    {
                                        // Paint-only closed-rail backing: never become the deepest hit in this retained
                                        // overlay. The interactive RightRail subtree above owns input while open.
                                        Grow = 1f, HitTestPassThrough = true,
                                        Fill = Prop.Of(() => _shellUi.RailOpen.Value && !_shellUi.RailFits.Value
                                            ? WaveeColors.FloatingPane : ColorF.Transparent),
                                    },
                                    new BoxEl
                                    {
                                        // The panel stays mounted at its final width so close can translate it out without
                                        // relayout. When RightRail marks its root non-hit-testable, this wrapper must also
                                        // yield or the invisible retained 340-DIP strip covers the page scrollbar.
                                        Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, HitTestPassThrough = true,
                                        Corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f),
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
                // "player bar disappears then slides back" glitch). The chrome rows (TitleBar, ShellToolbar) and the
                // PlayerBar host keep the default Shrink=0, so the player bar stays a fixed 72px slot docked at the
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
                ) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true },
                Embed.Comp(() => new PlayerBar()),
            ],
        };

        // The Mica-tint scrim: a full-bleed layer BEHIND the 4-row chrome whose Fill is the (bound) page tint. The root
        // stays Mica-passthrough when the tint is null (Transparent); when a detail page sets it, the low-alpha colour
        // sits between DWM Mica and the translucent chrome, so the visible Mica regions carry the album/playlist hue.
        _fileDrop ??= new DropTargetSpec(
            [DropKinds.Files],
            OnEnter: _ => _fileDropOver.Value = true,
            OnLeave: _ => _fileDropOver.Value = false,
            OnDrop: s =>
            {
                _fileDropOver.Value = false;
                if (s.Payload is FileDropData { Count: > 0 } files) LocalFileActions.PlayDropped(_actions, files.Paths);
            });
        var tinted = new BoxEl
        {
            Grow = 1f, Direction = 1,
            Fill = Prop.Of(() => _shellTint.Value.Color ?? ColorF.Transparent),
            DropTarget = _fileDrop,
            Children = [column],
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
            Padding = new Edges4(0f, 48f + 48f + 8f, 0f, 0f),   // clear the TitleBar (48) + ShellToolbar (48) rows
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
        var shellWithOverlays = Ui.ZStack(tinted, runtimeBannerLayer, fileDropLayer,
            Embed.Comp(() => new ActionServicesOverlayBinder(_actions)),
            Embed.Comp(() => new Wavee.Features.Video.InWindowVideoPip { Settings = _settings }),
            Embed.Comp(() => new Wavee.Features.Video.VideoPlacementHost { Settings = _settings })) with { Grow = 1f };

        return Ctx.Provide(ShellUi.Slot, _shellUi,
               Ctx.Provide(ShellTint.Slot, _shellTint,
               Ctx.Provide(HistoryStore.NavCtx, (Action<string, string?>)GoNav,
               Ctx.Provide(HistoryStore.Slot, _historyStore,
               Ctx.Provide(NavPreviewStore.Slot, _navPreview,
               Ctx.Provide(SearchQuery.Slot, _searchText,
               Ctx.Provide(ActionServices.Slot, _actions,
               OverlayHost.Create(shellWithOverlays))))))));
    }

    TabStrip BuildTabStrip() => new TabStrip
    {
        ItemsSource = BuildTabItems,
        ItemsVersion = () => _tabsVersion.Value,
        SelectedIndex = _selectedTab,
        OnSelectionChanged = i => { if ((uint)i < (uint)_open.Count) Go(_open[i].Key, _open[i].Arg, NavTransitionKind.Neutral); },
        OnTabCloseRequested = CloseTab,
        OnAddTabButtonClick = () => { OpenNewTab("home"); return null; },
        IsAddTabButtonVisible = true,
        // Wavee's commanding plate is translucent over live Mica. Using that RAW material here (rather than an opaque
        // reference-Mica flatten) makes the tab and toolbar follow the same wallpaper hue/luminance and fuse exactly.
        SelectedFill = Prop.Of(() => WaveeColors.Toolbar), TabWidth = 200f, MinTabWidth = 120f, MaxTabWidth = 240f,
    };

    int TitleBarTabsVersion()
    {
        int version = _tabsVersion.Value;
        int selected = _selectedTab.Value;
        return unchecked(version * 397 ^ selected);
    }

    IReadOnlyList<TabViewItem> BuildTabItems()
    {
        var items = new TabViewItem[_open.Count];
        for (int i = 0; i < items.Length; i++)
            items[i] = new TabViewItem { Header = _open[i].Label, Icon = _open[i].Glyph, IsClosable = _open.Count > 1 };
        return items;
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

    void OpenNewTab(string key)
    {
        var (title, glyph) = ShellNav.Dest(key, null);
        _open.Add(new OpenTab(_nextTabId++, key, title, glyph, null));
        _selectedTab.Value = _open.Count - 1;
        _tabsVersion.Value = _tabsVersion.Peek() + 1;
        Go(key, null, NavTransitionKind.Neutral);
    }

    void CloseTab(int i)
    {
        if (_open.Count <= 1 || (uint)i >= (uint)_open.Count) return;
        _open.RemoveAt(i);
        int sel = _selectedTab.Peek();
        if (i < sel) sel--;
        else if (i == sel) sel = Math.Min(i, _open.Count - 1);
        sel = Math.Clamp(sel, 0, _open.Count - 1);
        _selectedTab.Value = sel;
        _tabsVersion.Value = _tabsVersion.Peek() + 1;
        var t = _open[sel];
        Go(t.Key, t.Arg, NavTransitionKind.Neutral);
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
            // FloatingChrome, not Sidebar: the docked pane's fill is the TRANSLUCENT plate, but this drawer slides OVER
            // the page — a translucent fill would let the content read through it. FloatingChrome is that same plate
            // made opaque (rung 1), so the drawer matches the docked pane it stands in for rather than the content pane.
            Fill = Prop.Of(() => WaveeColors.FloatingChrome),
            BorderWidth = 1f, BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
            Corners = new CornerRadius4(0f, Radii.Card, Radii.Card, 0f),
            Shadow = Elevation.Dialog, HitTestVisible = open,
            Children = [Embed.Comp(() => new WaveeSidebar(_route, _go, _drawerCompact, _expandedWidth))],
        };
    }
}
