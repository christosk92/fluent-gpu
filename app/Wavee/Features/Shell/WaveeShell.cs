using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

// The Wavee shell root — the WaveeMusic 4-row chrome (tab strip + caption · toolbar · sidebar + content card; player bar
// deferred). Owns the route as SIGNALS so the toolbar/sidebar/tabs all react, plus the open-tab list. Builds the frame
// ONCE (Render only re-runs on a theme toggle, which re-keys the OverlayHost to remount with the new tokens); each chrome
// piece re-renders itself from the signals it reads. Mounted by WaveeApp inside the Services + PlaybackBridge providers.
sealed class WaveeShell : Component
{
    // One open browser-style tab: its route key, the strip label/glyph, and the route Arg (playlist display name).
    private sealed record OpenTab(string Key, string Label, string Glyph, string? Arg);

    readonly Signal<Route> _route = new(new Route("home"));
    readonly Signal<bool> _canBack = new(false);
    readonly List<Route> _history = new();

    readonly List<OpenTab> _open = new() { new OpenTab("home", "Home", Icons.Home, null) };
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
    readonly bool _reducedMotionOS = Motion.ReducedMotion;     // OS reduced-motion setting; restored after a drag

    // The pane's collapse-toggle animation (56↔expanded width eases). Snapped during a drag via Motion.ReducedMotion.
    static readonly LayoutTransition SidebarReflow = new(
        TransitionChannels.Size, TransitionDynamics.Spring(0.30f, 1f), SizeMode.Reflow);

    // The shell receives its persisted settings through the IAppSettings interface (provided by the composition root,
    // Services). It never sees the concrete store — no "ForUnpackaged"/registry/publisher detail leaks in here.
    public WaveeShell(IAppSettings settings)
    {
        _settings = settings;
        _sidebarCompact = new(settings.Get(WaveeSettings.SidebarCollapsed));
        _sidebarWidth = new(settings.Get(WaveeSettings.SidebarWidth));
    }

    void SaveSidebar()
    {
        _settings.Set(WaveeSettings.SidebarWidth, _sidebarWidth.Peek());
        _settings.Set(WaveeSettings.SidebarCollapsed, _sidebarCompact.Peek());
    }

    public override Element Render()
    {
        _requestTheme = UseContext(ThemeControl.Request);   // host's live re-theme trigger (animated in-place; no remount)
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

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f,
            Children =
            [
                Embed.Comp(() => new TitleBar
                {
                    IconGlyph = "", ShowPaneToggle = false, ShowCaptionButtons = true,
                    Tabs = () => Embed.Comp(BuildTabStrip),
                    TabsVersion = () => _tabsVersion.Value,
                }),
                Embed.Comp(() => new ShellToolbar(_route, _canBack, Go, Back, Home, _searchText, _sidebarCompact, ToggleTheme)),
                Ui.ZStack(
                    // The sidebar + content row. The sidebar PANE (SidebarPane) is the row's DIRECT child, so ITS width
                    // is what the row distributes — the content column re-solves and tiles against it gap-free. The width
                    // is signal-bound + drag-resizable (the grip overlay below); the pane animates the collapse toggle.
                    // No Fill on the row: it stays Mica-passthrough (a chrome slab would sit UNDER the translucent sidebar
                    // + content layers and double-tint them); real-layout tiling keeps the seam gap-free with nothing behind.
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f,
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
                                    Children = [ Embed.Comp(() => new WaveeSidebar(_route, Go, _sidebarCompact)) ],
                                },
                            ],
                        },
                            // Content side: an inset, rounded, shadowed "page" over the Toolbar-chrome backing.
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Fill = Prop.Of(() => WaveeColors.Toolbar),
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Grow = 1f, Margin = new Edges4(0f, 2f, 8f, 0f),
                                        // BOUND (not a static ColorF): this content "page" is a frozen literal inside the
                                        // OverlayHost.Child column (constructor args freeze at mount), so a re-render can't
                                        // re-read the token. As a bind it lives in the reconciler's _nodeBindings and the
                                        // host's live re-theme (RethemeAll) re-fires it → FillCardDefault follows the theme.
                                        Fill = Prop.Of(() => WaveeColors.FileArea), Corners = CornerRadius4.All(WaveeRadius.Card),
                                        Shadow = Elevation.Card, ClipToBounds = true,
                                        Children = [ Embed.Comp(() => new ContentHost(_route)) ],
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
                // window is shorter than the column's natural height. The chrome rows (TitleBar, ShellToolbar) and the
                // PlayerBar host keep the default Shrink=0, so the player bar stays a fixed 72px slot docked at the
                // window bottom and only the middle gives — its bounded height then lets the sidebar ScrollView scroll.
                ) with { Grow = 1f, Shrink = 1f },
                Embed.Comp(() => new PlayerBar()),
            ],
        };

        return Embed.Comp(() => new OverlayHost { Child = column });
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

    IReadOnlyList<TabViewItem> BuildTabItems()
    {
        var items = new TabViewItem[_open.Count];
        for (int i = 0; i < items.Length; i++)
            items[i] = new TabViewItem { Header = _open[i].Label, Icon = _open[i].Glyph, IsClosable = _open.Count > 1 };
        return items;
    }

    // ── navigation (the single source of truth the chrome reads) ─────────────────────────────────
    void Go(string key, string? arg)
    {
        _history.Add(_route.Peek());
        _route.Value = new Route(key, arg);
        _canBack.Value = _history.Count > 0;
        SyncActiveTab(_route.Peek());
    }

    void Back()
    {
        if (_history.Count == 0) return;
        _route.Value = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _canBack.Value = _history.Count > 0;
        SyncActiveTab(_route.Peek());
    }

    void Home() => Go("home", null);

    void SyncActiveTab(Route r)
    {
        int i = _selectedTab.Peek();
        if ((uint)i >= (uint)_open.Count) return;
        var (title, glyph) = ShellNav.Dest(r);
        _open[i] = new OpenTab(r.Name, title, glyph, r.Arg);
        _tabsVersion.Value = _tabsVersion.Peek() + 1;
    }

    void OpenNewTab(string key)
    {
        var (title, glyph) = ShellNav.Dest(key, null);
        _open.Add(new OpenTab(key, title, glyph, null));
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
        Theme.Dark = !Theme.Dark;                                    // swap the token set (bumps Tok.Epoch)
        _settings.Set(WaveeSettings.ThemeMode, Theme.Dark ? 2 : 1);  // an explicit pick → stop following the OS
        _requestTheme?.Invoke(250f);                                 // animate the in-place re-theme (250ms WinUI ControlNormal); no remount
    }
}
