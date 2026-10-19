using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// Declarative window drag regions (<c>BoxEl.WindowDrag</c>, app-authoring-ergonomics plan §1) plus the step-0 overlay
/// shape record: the host collects every reachable declared box at phase 7.8, composes it with app pushes (TitleBar) in
/// the documented priority order, pushes only on change, follows scroll and parking, flags the engine drag bands for
/// app-routed right-clicks, and falls back to the system menu when no <c>OnContextRequested</c> claims one.
/// </summary>
static class WindowDragSuite
{
    public static void Run(StringTable strings)
    {
        Console.WriteLine("\n-- WindowDrag: declarative non-client drag regions --");
        OverlayShapeRecorded();
        BasicOrderFlagAndPushOnChange(strings);
        ScrollMovesAndClips(strings);
        ParkedPageExcluded(strings);
        CoexistsWithTitleBar(strings);
        SystemMenuFallback(strings);
        InWindowPopupOverBand(strings, lightDismiss: false);
        InWindowPopupOverBand(strings, lightDismiss: true);
    }

    static (HeadlessPlatformApp App, HeadlessWindow Window, AppHost Host) Mount(StringTable strings, Component root,
        int w = 600, int h = 400, float scale = 1f)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("windowdrag", new Size2(w, h), scale, CustomFrame: true));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 6; i++) host.RunFrame();
        return (app, window, host);
    }

    static NodeHandle FindRole(SceneStore s, NodeHandle n, WindowDragRole role)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.WindowDragOf(n) == role) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindRole(s, c, role);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    static bool Same(RectF a, RectF b, float tol = 0.5f)
        => Near(a.X, b.X, tol) && Near(a.Y, b.Y, tol) && Near(a.W, b.W, tol) && Near(a.H, b.H, tol);

    static string Describe(TitleBarRegion[] regions)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in regions)
            sb.Append($"[{r.Hit}{(r.Flags != 0 ? "*" : "")} {r.RectDip.X:0},{r.RectDip.Y:0} {r.RectDip.W:0}x{r.RectDip.H:0}]");
        return sb.ToString();
    }

    // ── step 0: the headless window records the overlay shape from its WindowDesc ────────────────────────────────────
    static void OverlayShapeRecorded()
    {
        var overlay = new HeadlessWindow(new WindowDesc("overlay", new Size2(340, 300), 1f,
            CustomFrame: true, Topmost: true, ToolWindow: true, Resizable: false));
        var plain = new HeadlessWindow(new WindowDesc("plain", new Size2(340, 300), 1f));
        Check("gate.window.overlay-shape HeadlessWindow records WindowDesc Topmost/ToolWindow/Resizable (defaults = ordinary window)",
            overlay.Topmost && overlay.ToolWindow && !overlay.Resizable && overlay.CustomFrame
                && !plain.Topmost && !plain.ToolWindow && plain.Resizable,
            $"overlay=({overlay.Topmost},{overlay.ToolWindow},{overlay.Resizable}) plain=({plain.Topmost},{plain.ToolWindow},{plain.Resizable})");
    }

    // ── drag + island order, the AppContextMenu flag, push-on-change, retraction ────────────────────────────────────
    static void BasicOrderFlagAndPushOnChange(StringTable strings)
    {
        var probe = new BasicProbe();
        var (app, window, host) = Mount(strings, probe);
        using var _a = app; using var _h = host;
        var s = host.Scene;
        var drag = FindRole(s, s.Root, WindowDragRole.Drag);
        var island = FindRole(s, s.Root, WindowDragRole.NoDrag);
        var regions = window.LastTitleBarRegions;
        bool order = regions.Length == 2
            && regions[0].Hit == TitleBarHit.Client && regions[1].Hit == TitleBarHit.Caption
            && !drag.IsNull && !island.IsNull
            && Same(regions[0].RectDip, s.VisibleWindowRect(island)) && Same(regions[1].RectDip, s.VisibleWindowRect(drag))
            && Same(regions[1].RectDip, new RectF(0f, 0f, 400f, 48f));
        Check("gate.windowdrag.order a NoDrag island is reported (Client) BEFORE its enclosing Drag band (Caption), at their visible rects",
            order, Describe(regions));

        bool flags = regions.Length == 2
            && (regions[1].Flags & TitleBarRegionFlags.AppContextMenu) != 0
            && (regions[0].Flags & TitleBarRegionFlags.AppContextMenu) == 0;
        Check("gate.windowdrag.flag engine Drag bands carry TitleBarRegionFlags.AppContextMenu; islands do not",
            flags, Describe(regions));

        int pushes = window.TitleBarRegionPushCount;
        for (int i = 0; i < 6; i++) host.RunFrame();
        probe.Tick.Value++;   // a re-render that changes nothing about the regions
        for (int i = 0; i < 4; i++) host.RunFrame();
        Check("gate.windowdrag.push-on-change unchanged frames (and a no-op re-render) push nothing to the window",
            window.TitleBarRegionPushCount == pushes && pushes >= 1,
            $"pushesBefore={pushes} after={window.TitleBarRegionPushCount}");

        probe.Wide.Value = true;   // the band widens 400 → 520: re-collected with no effect of the app's
        for (int i = 0; i < 4; i++) host.RunFrame();
        var wide = window.LastTitleBarRegions;
        bool followed = wide.Length == 2 && Near(wide[1].RectDip.W, 520f, 0.5f) && window.TitleBarRegionPushCount == pushes + 1;
        Check("gate.windowdrag.resize a resized Drag box re-pushes ONCE with the new rect",
            followed, $"{Describe(wide)} pushes={window.TitleBarRegionPushCount - pushes}");

        probe.Enabled.Value = false;   // role removed → the regions retract
        for (int i = 0; i < 4; i++) host.RunFrame();
        int afterOff = window.TitleBarRegionPushCount;
        for (int i = 0; i < 4; i++) host.RunFrame();
        Check("gate.windowdrag.retract clearing the role retracts the band (island stays), then the collector goes quiet",
            window.LastTitleBarRegions.Length == 1 && window.LastTitleBarRegions[0].Hit == TitleBarHit.Client
                && window.TitleBarRegionPushCount == afterOff,
            $"{Describe(window.LastTitleBarRegions)} quiet={window.TitleBarRegionPushCount == afterOff}");
    }

    // ── a Drag box inside a ScrollView: the region follows the scroll and is clipped to the viewport ────────────────
    static void ScrollMovesAndClips(StringTable strings)
    {
        var (app, window, host) = Mount(strings, new ScrollProbe());
        using var _a = app; using var _h = host;
        var s = host.Scene;
        NodeHandle FindScroll(NodeHandle n)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (s.HasScroll(n)) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) { var r = FindScroll(c); if (!r.IsNull) return r; }
            return NodeHandle.Null;
        }
        var vp = FindScroll(s.Root);
        RectF At(float y)
        {
            host.TryGetScrollHandle(vp)?.ScrollTo(y, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            for (int i = 0; i < 3; i++) host.RunFrame();
            var r = window.LastTitleBarRegions;
            return r.Length == 1 ? r[0].RectDip : default;
        }
        RectF top = At(0f), moved = At(60f), clipped = At(150f);
        RectF gone = At(400f);
        int goneCount = window.LastTitleBarRegions.Length;
        // Viewport = (0,50)-(300,250); the band sits at content y 100..180.
        bool ok = !vp.IsNull
            && Same(top, new RectF(0f, 150f, 300f, 80f))
            && Same(moved, new RectF(0f, 90f, 300f, 80f))
            && Same(clipped, new RectF(0f, 50f, 300f, 30f))   // content y 100-150 = -50 → only 30 DIP below the viewport top
            && goneCount == 0 && gone.IsEmpty;
        Check("gate.windowdrag.scroll a scrolled Drag box moves with the content, is clipped to the viewport, and vanishes when scrolled out",
            ok, $"top={top} moved={moved} clipped={clipped} goneCount={goneCount}");
    }

    // ── a KeepAlive-parked page's Drag box is not reachable → no region ──────────────────────────────────────────────
    static void ParkedPageExcluded(StringTable strings)
    {
        var probe = new ParkProbe();
        var (app, window, host) = Mount(strings, probe);
        using var _a = app; using var _h = host;
        var first = window.LastTitleBarRegions;
        probe.Page.Value = 1;
        for (int i = 0; i < 6; i++) host.RunFrame();
        var second = window.LastTitleBarRegions;
        bool ok = first.Length == 1 && Near(first[0].RectDip.W, 200f, 0.5f)
            && second.Length == 1 && Near(second[0].RectDip.W, 320f, 0.5f);
        Check("gate.windowdrag.parked a KeepAlive-parked page's Drag box drops out; the active page's band is the only region",
            ok, $"page0={Describe(first)} page1={Describe(second)}");
    }

    // ── composition with TitleBar's push: buttons first, engine island before TitleBar's catch-all band ──────────────
    static void CoexistsWithTitleBar(StringTable strings)
    {
        var (app, window, host) = Mount(strings, new TitleBarCoexistProbe(), w: 1000, h: 400);
        using var _a = app; using var _h = host;
        var s = host.Scene;
        var regions = window.LastTitleBarRegions;
        RectF islandRect = s.VisibleWindowRect(FindRole(s, s.Root, WindowDragRole.NoDrag));
        RectF dragRect = s.VisibleWindowRect(FindRole(s, s.Root, WindowDragRole.Drag));
        int lastButton = -1, engineIsland = -1, engineDrag = -1, appCaption = -1;
        for (int i = 0; i < regions.Length; i++)
        {
            var r = regions[i];
            if (r.Hit is TitleBarHit.MinButton or TitleBarHit.MaxButton or TitleBarHit.CloseButton) lastButton = i;
            else if (r.Hit == TitleBarHit.Client && Same(r.RectDip, islandRect)) engineIsland = i;
            else if (r.Hit == TitleBarHit.Caption && (r.Flags & TitleBarRegionFlags.AppContextMenu) != 0 && Same(r.RectDip, dragRect)) engineDrag = i;
            else if (r.Hit == TitleBarHit.Caption && r.Flags == TitleBarRegionFlags.None) appCaption = i;
        }
        bool ok = lastButton >= 0 && engineIsland > lastButton && engineDrag > engineIsland
            && appCaption == regions.Length - 1 && appCaption > engineDrag;
        Check("gate.windowdrag.titlebar TitleBar's buttons first, then the engine island, the engine band, and TitleBar's catch-all band LAST",
            ok, $"buttons<={lastButton} island={engineIsland} drag={engineDrag} appCaption={appCaption} {Describe(regions)}");
    }

    // ── an app-routed caption right-click: handler wins; no handler ⇒ ShowSystemMenu at the screen point ─────────────
    static void SystemMenuFallback(StringTable strings)
    {
        var probe = new ContextProbe();
        var (app, window, host) = Mount(strings, probe, scale: 1.5f);
        using var _a = app; using var _h = host;
        window.ClientOriginPx = new Point2(100f, 50f);

        void RightClick(Point2 dip)
        {
            window.QueueInput(new InputEvent(InputKind.PointerMove, dip, 0, 0, PointerId: NonClientInput.SyntheticPointerId));
            window.QueueInput(new InputEvent(InputKind.PointerDown, dip, 1, 0, PointerId: NonClientInput.SyntheticPointerId));
            window.QueueInput(new InputEvent(InputKind.PointerUp, dip, 1, 0, PointerId: NonClientInput.SyntheticPointerId));
            for (int i = 0; i < 2; i++) host.RunFrame();
        }

        var p = new Point2(300f, 24f);
        RightClick(p);
        bool fellBack = window.ShowSystemMenuCount == 1
            && Near(window.LastSystemMenuPx.X, 100f + p.X * 1.5f, 0.5f) && Near(window.LastSystemMenuPx.Y, 50f + p.Y * 1.5f, 0.5f)
            && probe.Requests == 0;
        Check("gate.windowdrag.sysmenu-fallback an unhandled app-routed caption right-click opens the system menu at the click's screen px",
            fellBack, $"menus={window.ShowSystemMenuCount} at={window.LastSystemMenuPx} requests={probe.Requests}");

        probe.Handled.Value = true;   // now the band carries OnContextRequested
        for (int i = 0; i < 3; i++) host.RunFrame();
        RightClick(p);
        bool appWon = probe.Requests == 1 && window.ShowSystemMenuCount == 1;
        Check("gate.windowdrag.app-context-menu an OnContextRequested under the drag band claims the right-click (no system menu)",
            appWon, $"requests={probe.Requests} menus={window.ShowSystemMenuCount}");
    }

    // ── an in-window popup drawn over a Drag band is Client (ahead of the band), so its rows/buttons take the press ────
    static void InWindowPopupOverBand(StringTable strings, bool lightDismiss)
    {
        var probe = new PopupOverBandProbe(lightDismiss);
        var (app, window, host) = Mount(strings, probe);
        using var _a = app; using var _h = host;
        var s = host.Scene;
        bool closedBefore = window.LastTitleBarRegions.Length == 1 && window.LastTitleBarRegions[0].Hit == TitleBarHit.Caption;

        probe.Open.Value = true;
        for (int i = 0; i < 10; i++) host.RunFrame();
        var regions = window.LastTitleBarRegions;
        RectF body = probe.Body.IsNull || !s.IsLive(probe.Body) ? default : s.VisibleWindowRect(probe.Body);
        int caption = -1;
        for (int i = 0; i < regions.Length; i++)
            if (regions[i].Hit == TitleBarHit.Caption && (regions[i].Flags & TitleBarRegionFlags.AppContextMenu) != 0) { caption = i; break; }
        int client = -1;
        for (int i = 0; i < caption; i++)
        {
            RectF r = regions[i].RectDip;
            if (regions[i].Hit == TitleBarHit.Client && !body.IsEmpty
                && r.X <= body.X + 0.5f && r.Y <= body.Y + 0.5f && r.Right >= body.Right - 0.5f && r.Bottom >= body.Bottom - 0.5f)
            { client = i; break; }
        }
        RectF band = caption >= 0 ? regions[caption].RectDip : default;
        // Light-dismiss: the full-bleed scrim is the press target everywhere (a press on the band dismisses the menu) ⇒
        // the whole band is Client. No scrim (DismissBehavior.None): only the popup itself is carved out of the band.
        bool shape = client >= 0 && (lightDismiss ? Same(regions[client].RectDip, band) : !Same(regions[client].RectDip, band));
        string mode = lightDismiss ? "light-dismiss" : "no-scrim";
        Check($"gate.windowdrag.popup-over-band ({mode}) an open in-window popup over a WindowDragArea is a Client region BEFORE the Caption band",
            closedBefore && caption >= 0 && shape, $"closedBefore={closedBefore} body={body} {Describe(regions)}");

        probe.Open.Value = false;
        for (int i = 0; i < 12; i++) host.RunFrame();
        var after = window.LastTitleBarRegions;
        Check($"gate.windowdrag.popup-over-band.close ({mode}) closing the popup gives the whole band back to the caption",
            after.Length == 1 && after[0].Hit == TitleBarHit.Caption, Describe(after));
    }

    sealed class PopupOverBandProbe : Component
    {
        public readonly Signal<bool> Open = new(false);
        public readonly bool LightDismiss;
        public NodeHandle Body;
        public readonly Action<NodeHandle> OnBody;
        public PopupOverBandProbe(bool lightDismiss) { LightDismiss = lightDismiss; OnBody = h => Body = h; }
        public override Element Render() => OverlayHost.Create(Embed.Comp(() => new PopupOverBandInner(this)));
    }

    sealed class PopupOverBandInner : Component
    {
        readonly PopupOverBandProbe _p;
        public PopupOverBandInner(PopupOverBandProbe p) => _p = p;
        public override Element Render()
        {
            var options = new PopupOptions(
                DismissBehavior: _p.LightDismiss ? DismissBehavior.LightDismiss : DismissBehavior.None,
                Chrome: PopupChrome.Static);
            var pop = Popup.Create(
                new BoxEl { Width = 80f, Height = 24f },
                () => new BoxEl { Width = 120f, Height = 80f, OnClick = static () => { }, OnRealized = _p.OnBody },
                _p.Open, null, FlyoutPlacement.BottomLeft, options);
            return new BoxEl
            {
                Direction = 1,
                Children =
                [
                    new BoxEl
                    {
                        Width = 400f, Height = 200f, Padding = Edges4.All(8f), WindowDrag = WindowDragRole.Drag,
                        Children = [pop],
                    },
                    new BoxEl { Grow = 1f },
                ],
            };
        }
    }

    sealed class BasicProbe : Component
    {
        public readonly Signal<bool> Enabled = new(true);
        public readonly Signal<bool> Wide = new(false);
        public readonly Signal<int> Tick = new(0);
        public override Element Render()
        {
            _ = Tick.Value;
            return new BoxEl
            {
                Direction = 1,
                Children =
                [
                    new BoxEl
                    {
                        Width = Wide.Value ? 520f : 400f, Height = 48f,
                        WindowDrag = Enabled.Value ? WindowDragRole.Drag : WindowDragRole.None,
                        Children =
                        [
                            new BoxEl { Width = 40f, Height = 32f },
                            new BoxEl { Width = 60f, Height = 32f, OnClick = static () => { } }.NoWindowDrag(),
                        ],
                    },
                    new BoxEl { Grow = 1f },
                ],
            };
        }
    }

    sealed class ScrollProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl { Width = 300f, Height = 50f },   // the viewport starts at y=50, so its clip is not the window's
                Ui.ScrollView(new BoxEl
                {
                    Direction = 1, Width = 300f,
                    Children =
                    [
                        new BoxEl { Width = 300f, Height = 100f },
                        new BoxEl { Width = 300f, Height = 80f, WindowDrag = WindowDragRole.Drag },
                        new BoxEl { Width = 300f, Height = 1000f },
                    ],
                }) with { Width = 300f, Height = 200f, Grow = 0f },
            ],
        };
    }

    sealed class ParkProbe : Component
    {
        public readonly Signal<int> Page = new(0);
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                Flow.KeepAlive(() => Page.Value, p => "page" + p, p => new BoxEl
                {
                    Direction = 1,
                    Children = [new BoxEl { Width = p == 0 ? 200f : 320f, Height = 40f, WindowDrag = WindowDragRole.Drag }],
                }),
            ],
        };
    }

    sealed class TitleBarCoexistProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                Embed.Comp(() => new TitleBar { Title = "coexist", ShowCaptionButtons = true }),
                new BoxEl
                {
                    Width = 500f, Height = 60f, WindowDrag = WindowDragRole.Drag,
                    Children = [new BoxEl { Width = 80f, Height = 30f, OnClick = static () => { } }.NoWindowDrag()],
                },
                new BoxEl { Grow = 1f },
            ],
        };
    }

    sealed class ContextProbe : Component
    {
        public readonly Signal<bool> Handled = new(false);
        public int Requests;
        readonly Action<ContextRequestEventArgs> _onRequest;
        public ContextProbe() => _onRequest = _ => Requests++;
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl
                {
                    Width = 400f, Height = 48f, WindowDrag = WindowDragRole.Drag,
                    OnContextRequested = Handled.Value ? _onRequest : null,
                },
                new BoxEl { Grow = 1f },
            ],
        };
    }
}
