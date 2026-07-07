using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

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

    // Right-rail (WaveeMusic-style lyrics / now-playing panel) UI state — created here, provided via ShellUi.Slot, and
    // toggled from the player bar. Independent of bridge.Expanded (the fullscreen now-playing takeover).
    readonly ShellUi _shellUi = new();

    int _nextTabId = 1;
    readonly List<OpenTab> _open = new() { new OpenTab(0, "home", Loc.Get(Strings.Nav.Home), Icons.Home, null) };
    readonly Signal<int> _tabsVersion = new(0);
    readonly Signal<int> _selectedTab = new(0);

    readonly Signal<string> _searchText = new("");
    // Sidebar state. Width + collapsed are seeded (in the ctor, from the injected settings) so the FIRST layout already
    // uses the saved width — no startup animation; written back on change via SaveSidebar.
    readonly IAppSettings _settings;
    readonly Signal<bool> _sidebarCompact;
    readonly Signal<float> _sidebarWidth;                       // expanded width (drag-resizable, persisted)
    readonly Signal<bool> _sidebarDragging = new(false);       // ON during a seam drag → snaps all layout transitions (1:1 resize)
    readonly Signal<float> _sidebarFade = new(1f);             // content-opacity cue as a resize nears the collapse detent
    Action<float>? _requestTheme;                              // ambient ThemeControl.Request: live animated re-theme (captured in Render)
    Action<string>? _morphBegin;                               // ambient SharedTransition.Begin: capture the leaving cover for the Back/Forward fly
    readonly bool _reducedMotionOS = Motion.ReducedMotion;     // OS reduced-motion setting; restored after a drag

    // Rail layout-defer lock (Task C): while the RailReflow spring re-solves content width the responsive breakpoints
    // (track-list tier, detail mode) are gated so intermediate widths don't churn multiple remounts (the open/close
    // flash). Armed on every rail toggle; a one-shot Timer clears it after the spring settles. RailLockMs must EXCEED
    // the RailReflow settle (Spring(0.22f,1f) ≈ 220ms) so the lock never clears mid-flight → an extra remount.
    bool _lastRailOpen;
    int _railLockGen;
    System.Threading.Timer? _railLockTimer;
    const int RailLockMs = 300;

    // The pane's collapse-toggle animation (56↔expanded width eases). Snapped during a drag via Motion.ReducedMotion.
    static readonly LayoutTransition SidebarReflow = new(
        TransitionChannels.Size, TransitionDynamics.Tween(Motion.ControlFast, Easing.SmoothOut), SizeMode.Reflow);

    // The right-rail open/close reflow. A critically-damped spring (damping 1.0 ⇒ NO overshoot, so the content never
    // over-shrinks) instead of the front-loaded ControlFast ease: the content column re-solves along a smooth, evenly-
    // paced trajectory, so the reflow reads as ONE unified motion rather than the per-frame text re-wraps / card resizes
    // bunching into the fast early phase (the "steppy" feel).
    static readonly LayoutTransition RailReflow = new(
        TransitionChannels.Size, TransitionDynamics.Spring(0.22f, 1f), SizeMode.Reflow);

    // The shell receives its persisted settings through the IAppSettings interface (provided by the composition root,
    // Services). It never sees the concrete store — no "ForUnpackaged"/registry/publisher detail leaks in here.
    static string HistoryFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "WaveeMusic", "history.json");

    // Stress-probe nav seam (WAVEE_NAV_PROBE only): lets the WaveeNavProbe drive REAL navigation/theme/tab churn through
    // the same signals the chrome writes — no synthetic input, no reaching into private state. Inert in normal runs.
    internal static Action<string, string?>? ProbeNav;
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
        _sidebarWidth = new(settings.Get(WaveeSettings.SidebarWidth));

        // Inert probe (screenshot / UI iteration only): open the right rail to the Lyrics panel at startup.
        if (Diag.EnvFlag("WAVEE_LYRICS_OPEN") || Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE")) { _shellUi.RailOpen.Value = true; _shellUi.Mode.Value = RailMode.Lyrics; }

        if (Diag.EnvFlag("WAVEE_NAV_PROBE") || Diag.EnvFlag("WAVEE_RESIZE_PROBE") || Diag.EnvFlag("WAVEE_CONN_STRESS") || Diag.EnvFlag("WAVEE_TRACKLIST_SHOT") || Diag.EnvFlag("WAVEE_HERO_SHOT") || Diag.EnvFlag("WAVEE_HOME_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_PROBE") || Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE") || Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE") || Diag.EnvFlag("WAVEE_MEM_SOAK") || Diag.EnvFlag("WAVEE_PERF_BENCH"))
        {
            ProbeNav = GoNav; ProbeBack = Back; ProbeForward = Forward; ProbeTheme = ToggleTheme; ProbeOpenTab = OpenNewTab;
            // Exactly the Home-card path: stash a preview (→ DetailShell mounts the PREVIEW path, not the skeleton path the
            // sidebar nav hits) + fire the Hero-fly morph, then navigate — so the probe can reproduce the card-click transition.
            ProbeCardNav = (key, arg, doMorph) =>
            {
                if (!Diag.EnvFlag("WAVEE_PB_NOPREVIEW") && key.StartsWith("pl:", System.StringComparison.Ordinal))
                    _navPreview.Set(key, DetailPreview.FromPlaylist(new Wavee.Core.PlaylistSummary(key.Substring(3), arg ?? "Playlist", "", 0, null)));
                if (doMorph && !Diag.EnvFlag("WAVEE_PB_NOMORPH")) _morphBegin?.Invoke(key);   // the Hero cover fly
                GoNav(key, arg);
            };
            // The EXACT related-album-card path (DetailTrailing → h.OpenAlbum → DetailNav.OpenAlbum): stash the card's
            // partial model + fire the fly, then nav. Lets the probe measure album→album on the post-fix (in-place) path.
            ProbeOpenAlbum = a => DetailNav.OpenAlbum(_navPreview, _morphBegin, GoNav, a);
            ProbeSidebarCompact = compact =>
            {
                _sidebarCompact.Value = compact;
                _sidebarFade.Value = 1f;
                SaveSidebar();
            };
            ProbeSidebarDragBegin = () =>
            {
                Motion.ReducedMotion = true;   // mirror SidebarResizeGrip.OnDown: snap reflow on the first move frame
                _sidebarDragging.Value = true;
            };
            ProbeSidebarDragWidth = width =>
            {
                _sidebarCompact.Value = false;
                _sidebarWidth.Value = Math.Clamp(width, 240f, 460f);
                _sidebarFade.Value = 1f;
            };
            ProbeSidebarDragEnd = () =>
            {
                _sidebarFade.Value = 1f;
                SaveSidebar();
                _sidebarDragging.Value = false;
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

    void SaveSidebar()
    {
        _settings.Set(WaveeSettings.SidebarWidth, _sidebarWidth.Peek());
        _settings.Set(WaveeSettings.SidebarCollapsed, _sidebarCompact.Peek());
    }

    public override Element Render()
    {
        _requestTheme = UseContext(ThemeControl.Request);   // host's live re-theme trigger (animated in-place; no remount)
        _morphBegin = UseContext(SharedTransition.Begin);   // connected-animation: Back/Forward capture the leaving cover before the route flips
        // The shell's content lives in the OverlayHost ZStack, which deliberately lets its child OVERFLOW (a tall popup must
        // not be clipped to the window). For the page CONTENT that means a tall page (a Detail rail is ~600px and does not
        // scroll) sizes the whole column to its content (~827px) and overflows the 760px window — shoving the fixed player
        // bar off the bottom (the "player bar disappears / slides" glitch). Pin the column's height to the LIVE viewport (a
        // BOUND prop, not a stale literal — it re-fires on resize) so the column is exactly window-tall and its Shrink=1 /
        // MinHeight=0 content region yields instead of overflowing. UI-thread signal; the binding re-lays-out on resize.
        var vpSig = UseContextSignal(Viewport.Size);
        bool compact = _sidebarCompact.Value;    // subscribe → re-persist on a collapse/expand toggle (infrequent)
        bool dragging = _sidebarDragging.Value;  // subscribe → snap all layout transitions while resizing the sidebar
        // Persist the collapse toggle here; the grip's drag-end (OnReleased → SaveSidebar) persists the width. The
        // initial values are seeded at field construction (below) so the first layout already uses the saved width
        // (no startup animation). Defensive: storage failures never touch the UI.
        UseEffect(() => SaveSidebar(), compact);
        // 1:1 resize: while dragging the seam, flip the GLOBAL reduced-motion gate ON so every layout transition SNAPS
        // (ApplyProjections rewrites each to a 1ms tween) — this pane's collapse spring AND the sidebar sections'
        // SizeMode.Reflow stop easing the per-frame width change, so the content tracks the cursor exactly. Restored to
        // the OS setting on release so the collapse toggle animates normally. (A resize drag never overlaps a collapse.)
        UseEffect(() => Motion.ReducedMotion = dragging || _reducedMotionOS, dragging);

        // Rail viewport-fit + layout-defer (off-render, auto-tracking effects — the render body stays subscription-free
        // so the shell isn't re-run on every resize pixel; only the rail band / pages re-solve from the signals below).
        var post = UsePost();
        // (1) Maintain ShellUi.RailFits from the live viewport/sidebar/rail widths and AUTO-CLOSE the rail if it no longer
        // fits (snap — no animation fight on shrink). Peek-guarded writes so this never re-triggers itself.
        UseSignalEffect(() =>
        {
            float vpW = vpSig.Value.Width;
            float sbW = _sidebarCompact.Value ? 56f : _sidebarWidth.Value;
            bool fits = ShellUi.CanFitRail(vpW, sbW, _shellUi.RailWidth.Value);
            if (_shellUi.RailFits.Peek() != fits) _shellUi.RailFits.Value = fits;
            if (!fits && _shellUi.RailOpen.Peek()) _shellUi.RailOpen.Value = false;
        });
        // (2) Arm the layout-defer lock on every rail toggle (open OR close); a one-shot Timer clears it after the
        // RailReflow spring settles. The _lastRailOpen change-guard avoids arming on an unrelated re-render; the
        // generation guard lets a rapid re-toggle cancel a stale clear. Cleared via post() (UI thread), like LyricsTicker.
        UseSignalEffect(() =>
        {
            bool open = _shellUi.RailOpen.Value;
            if (open == _lastRailOpen) return;
            _lastRailOpen = open;
            _shellUi.ArmRailLock();   // stamps the arm time — the breakpoint gates read the time-bounded RailLockActive
            int gen = ++_railLockGen;
            _railLockTimer?.Dispose();
            _railLockTimer = new System.Threading.Timer(
                _ => post(() => { if (gen == _railLockGen) _shellUi.RailLayoutLocked.Value = false; }),
                null, RailLockMs, System.Threading.Timeout.Infinite);
        });

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f, Height = Prop.Of(() => vpSig.Value.Height),   // window-tall → content yields, never overflows the player bar
            Children =
            [
                Embed.Comp(() => new TitleBar
                {
                    IconGlyph = "", ShowPaneToggle = false, ShowCaptionButtons = true,
                    Tabs = () => Embed.Comp(BuildTabStrip),
                    TabsVersion = TitleBarTabsVersion,
                }),
                Embed.Comp(() => new ShellToolbar(_route, _canBack, _canForward, GoNav, Back, Forward, Home, _searchText, _sidebarCompact, ToggleTheme, _history, _forwardHistory)),
                Ui.ZStack(
                    // The sidebar + content row. The sidebar PANE (SidebarPane) is the row's DIRECT child, so ITS width
                    // is what the row distributes — the content column re-solves and tiles against it gap-free. The width
                    // is signal-bound + drag-resizable (the grip overlay below); the pane animates the collapse toggle.
                    // No Fill on the row: it stays Mica-passthrough (a chrome slab would sit UNDER the translucent sidebar
                    // + content layers and double-tint them); real-layout tiling keeps the seam gap-free with nothing behind.
                    new BoxEl
                    {
                        // ClipToBounds (Task B5): a settle-frame safety net so a page's content can never paint past the
                        // content card into the fixed rail band during the RailReflow. The card + page wrappers already
                        // clip; this bounds the row itself while the flex-shrink chain re-solves.
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
                            Width = Prop.Of(() => _sidebarCompact.Value ? 56f : _sidebarWidth.Value),
                            // SidebarReflow eases the COLLAPSE toggle (56↔expanded). During a drag the same spring would
                            // lag the cursor, so the dragging effect (above) flips Motion.ReducedMotion ON for the drag —
                            // that snaps every layout transition (this pane AND the sidebar sections) to 1:1.
                            Animate = SidebarReflow,
                            Children =
                            [
                                // Content fades (compositor-only) toward the collapse detent; the chrome fill stays solid.
                                // Column wrapper so WaveeSidebar's Grow=1f fills our HEIGHT (its ScrollView needs a definite one).
                                new BoxEl
                                {
                                    Direction = 1, Grow = 1f,
                                    Opacity = Prop.Of(() => _sidebarFade.Value),
                                    Children = [ Embed.Comp(() => new WaveeSidebar(_route, GoNav, _sidebarCompact)) ],
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
                                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f, Fill = Prop.Of(() => WaveeColors.Toolbar),
                                Children =
                                [
                                    new BoxEl
                                    {
                                        // MinHeight=0 (the flex `min-height:0` override): this card CLIPS its content, so it
                                        // must be allowed to shrink BELOW the page's natural min-height. Without it, a tall
                                        // page (a Detail RAIL is ~600px and does not scroll) forces the content region past
                                        // the column height and PUSHES THE FIXED PLAYER BAR off the bottom for a frame on
                                        // navigation — the "player bar animates away then back" glitch. With it the card
                                        // shrinks to the available space and clips/scrolls, so the player bar stays docked.
                                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Margin = new Edges4(0f, 2f, 8f, 0f),
                                        // BOUND (not a static ColorF): this content "page" is a frozen literal inside the
                                        // OverlayHost.Child column (constructor args freeze at mount), so a re-render can't
                                        // re-read the token. As a bind it lives in the reconciler's _nodeBindings and the
                                        // host's live re-theme (RethemeAll) re-fires it → FillCardDefault follows the theme.
                                        Fill = Prop.Of(() => WaveeColors.FileArea), Corners = CornerRadius4.All(WaveeRadius.Card),
                                        Shadow = Elevation.Card, ClipToBounds = true,
                                        // Layout firewall (#5): this card is Grow=1 (its size is the shell's content region,
                                        // parent-determined) and clips — so a re-render deep inside a page re-solves only this
                                        // subtree (RunSubtree) instead of a full-tree layout from the root on every nav.
                                        IsolateLayout = true,
                                        Children = [ Embed.Comp(() => new ContentHost(_route, ActiveTabId)) ],
                                    },
                                ],
                            },
                            // Right rail — the WaveeMusic-style lyrics / now-playing panel. A literal row child (Shrink=0,
                            // bound width) so the content card re-tiles against it; the width animates 0<->RailWidth on
                            // toggle (SidebarReflow) and ClipToBounds hides the content while collapsed.
                            new BoxEl
                            {
                                Direction = 1, Shrink = 0f, ClipToBounds = true, Fill = Prop.Of(() => WaveeColors.Sidebar),
                                Width = Prop.Of(() => _shellUi.RailOpen.Value ? _shellUi.RailWidth.Value : 0f),
                                Animate = RailReflow,
                                Children = [ Embed.Comp(() => new RightRail()) ],
                            },
                        ],
                    },
                    // Resize-grip overlay: a narrow strip translated to the pane↔content seam. The overlay's own hit
                    // bounds are only the grip column, so sidebar wheel/hover routing and its scrollbar thumb still hit
                    // the sidebar ScrollView instead of a non-scrollable overlay branch.
                    new BoxEl
                    {
                        Width = 16f, Direction = 1,
                        Transform = Prop.Of(() => Affine2D.Translation(_sidebarCompact.Value ? 56f : _sidebarWidth.Value, 0f)),
                        Children =
                        [
                            // The strip is entirely on the content side of the seam to avoid covering the sidebar's
                            // 12-DIP scrollbar lane; SidebarResizeGrip's root Grow=1 fills this definite-height column.
                            Embed.Comp(() => new SidebarResizeGrip(_sidebarCompact, _sidebarWidth, _sidebarDragging, _sidebarFade, SaveSidebar)),
                        ],
                    }
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
        var tinted = new BoxEl
        {
            Grow = 1f, Direction = 1,
            Fill = Prop.Of(() => _shellTint.Value.Color ?? ColorF.Transparent),
            Children = [column],
        };

        // The full-screen now-playing view is a TOP layer over the whole shell (inside the OverlayHost so its own flyouts —
        // the device picker, the volume popup — still render above it). Gated on bridge.Expanded; zero-cost when closed.
        // CRITICAL: the layer MUST sit under a PLAIN pass-through positioner (a bare Embed.Comp wrapper node is
        // mirrored-but-NOT-passthrough and would swallow every hit, silently killing scrolling — same trap as the FPS HUD).
        // When the now-playing is open its own opaque fill captures hits; when closed the pass-through lets scroll through.
        var nowPlayingLayer = new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true,
            Children = [ Embed.Comp(() => new NowPlayingLayer()) ],
        };
        // Transient toasts (ToastHost was previously never mounted anywhere): a full-bleed PASS-THROUGH positioner that pins
        // the toast bottom-centre, just above the docked player bar. It sits ABOVE the now-playing layer so a toast is visible
        // over the fullscreen now-playing view too. Same trap as the FPS HUD / now-playing layer: the positioner MUST be a
        // plain BoxEl with HitTestPassThrough so empty space passes clicks through; the toast's own filled surface captures its.
        var toastLayer = new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
            Padding = new Edges4(0f, 0f, 0f, WaveeSize.PlayerBarH + 12f),
            Children = [ new BoxEl { MaxWidth = 560f, Children = [ Embed.Comp(() => new ToastHost()) ] } ],
        };
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
        var shellWithNowPlaying = Ui.ZStack(tinted, nowPlayingLayer, runtimeBannerLayer, toastLayer) with { Grow = 1f };

        return Ctx.Provide(ShellUi.Slot, _shellUi,
               Ctx.Provide(ShellTint.Slot, _shellTint,
               Ctx.Provide(HistoryStore.NavCtx, (Action<string, string?>)GoNav,
               Ctx.Provide(HistoryStore.Slot, _historyStore,
               Ctx.Provide(NavPreviewStore.Slot, _navPreview,
               Ctx.Provide(SearchQuery.Slot, _searchText,
               Embed.Comp(() => new OverlayHost { Child = shellWithNowPlaying })))))));
    }

    TabStrip BuildTabStrip() => new TabStrip
    {
        ItemsSource = BuildTabItems,
        ItemsVersion = () => _tabsVersion.Value,
        SelectedIndex = _selectedTab,
        OnSelectionChanged = i => { if ((uint)i < (uint)_open.Count) Go(_open[i].Key, _open[i].Arg); },
        OnTabCloseRequested = CloseTab,
        OnAddTabButtonClick = () => { OpenNewTab("home"); return null; },
        IsAddTabButtonVisible = true,
        // The selected tab uses the SAME chrome material as the toolbar (WaveeMusic TabViewItemHeaderBackgroundSelected
        // = LayerOnMicaBaseAlt, App.xaml:47) so it fuses into the toolbar, and is sized to a compact WaveeMusic tab.
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
    void Go(string key, string? arg)
    {
        _history.Add(_route.Peek());
        if (_history.Count > MaxBackStack) _history.RemoveAt(0);   // bound the in-memory back-stack
        _forwardHistory.Clear();
        _canForward.Value = false;
        _route.Value = new Route(key, arg);
        _canBack.Value = _history.Count > 0;
        _historyStore.Add(_route.Peek());
        SyncActiveTab(_route.Peek());
    }

    void Back()
    {
        if (_history.Count == 0) return;
        _morphBegin?.Invoke(_route.Peek().Name);   // reverse fly: snapshot the leaving page's cover; the like-tagged dest on the previous route flies it back
        _forwardHistory.Add(_route.Peek());
        if (_forwardHistory.Count > MaxBackStack) _forwardHistory.RemoveAt(0);
        _canForward.Value = true;
        _route.Value = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _canBack.Value = _history.Count > 0;
        _historyStore.Add(_route.Peek());
        SyncActiveTab(_route.Peek());
    }

    void Forward()
    {
        if (_forwardHistory.Count == 0) return;
        _morphBegin?.Invoke(_route.Peek().Name);   // same as Back: fly the leaving cover into the like-tagged dest on the route we redo to
        _history.Add(_route.Peek());
        _canBack.Value = true;
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
        Go(key, null);
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
        Go(t.Key, t.Arg);
    }

    void ToggleTheme()
    {
        var next = Theme.Dark ? ThemeKind.Light : ThemeKind.Dark;
        Tok.Use(WaveeTheme.ResolvePalette(_settings.Get(WaveeSettings.PaletteId)), next);
        _settings.Set(WaveeSettings.ThemeMode, next == ThemeKind.Dark ? 2 : 1);
        _requestTheme?.Invoke(250f);
    }
}
