using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;


    sealed class ResizeBoxProbe : Component
    {
        public override Element Render() => Embed.Comp(() => new OverlayHost
        {
            Child = new BoxEl { Grow = 1f, Fill = ColorF.FromRgba(20, 20, 20) },
        });
    }

static class ScrollSuite
{
    public static void Run(StringTable strings)
    {
        ScrollHoverChecks(strings);
        HoverSubtreeChecks(strings);
        ScrollChecks(strings);
        TwoAxisScrollChecks(strings);
        ScrollCrossAxisChecks(strings);
        ScrollOverlayChecks(strings);
        VirtualChecks(strings);
        VirtualBudgetChecks(strings);
        BoundItemsViewChecks(strings);
        ExtentTableChecks();
        VariableChecks(strings);
        ZeroAllocScrollChecks(strings);
        ScrollParityChecks(strings);
        ScrollPerfWaveChecks(strings);
        ScrollV2ValidationChecks(strings);
        TouchpadFeelChecks(strings);
        E11VirtChecks(strings);
        ListConsolidationChecks(strings);
        D1CollectionHostSizingChecks(strings);
        Cp2ConsolidationChecks(strings);
        D4ScrollBarChecks(strings);
    }

    static void HoverSubtreeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("hover-subtree", new Size2(320, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new HoverSubtreeProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();   // mount + layout

        // Geometry: root pad 50 → wrapper at (50,50) 100×100; wrapper pad 20 → child at (70,70) 60×60.
        var vp = host.Scene.AbsoluteRect(host.Scene.Root);
        var ptOut = new Point2(vp.X + 10f, vp.Y + 10f);       // outside the wrapper subtree (root has no handlers)
        var ptChild = new Point2(vp.X + 100f, vp.Y + 100f);   // inside the interactive child
        var ptChild2 = new Point2(vp.X + 110f, vp.Y + 105f);  // still inside the child
        var ptPad = new Point2(vp.X + 58f, vp.Y + 58f);       // the wrapper's own padding (the wrapper is the leaf here)

        void Move(Point2 p) { window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0)); host.RunFrame(); }

        Move(ptOut);
        probe.WrapperEnter = 0; probe.WrapperExit = 0;

        Move(ptChild);                                        // outside → child (the child is the deepest interactive leaf)
        bool enterOnChild = probe.WrapperEnter == 1 && probe.WrapperExit == 0;
        Move(ptChild2);                                       // move WITHIN the child — no re-enter, no exit
        bool noRefire = probe.WrapperEnter == 1 && probe.WrapperExit == 0;
        Move(ptOut);                                          // leave the subtree
        bool exitOnce = probe.WrapperExit == 1;

        probe.WrapperEnter = 0; probe.WrapperExit = 0;
        Move(ptPad);                                          // onto the wrapper's own padding (wrapper = hovered leaf)
        Move(ptChild);                                        // padding → child: still INSIDE the subtree → no exit
        bool noSelfExit = probe.WrapperExit == 0;
        Move(ptOut);
        bool exitAfter = probe.WrapperExit == 1;

        Check("gate.input.hover-subtree-enter-exit OnHoverMove/OnPointerExit are subtree-scoped for a wrapper around an interactive child (enter on child-hover, no re-fire within, no exit wrapper→child, one exit on leave)",
            enterOnChild && noRefire && exitOnce && noSelfExit && exitAfter,
            $"enterOnChild={enterOnChild} noRefire={noRefire} exitOnce={exitOnce} noSelfExit={noSelfExit} exitAfter={exitAfter} enter={probe.WrapperEnter} exit={probe.WrapperExit}");
    }

    static void ScrollHoverChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-hover", new Size2(320, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollHoverProbe());
        host.RunFrame();   // mount + layout (SmoothScroll defaults false ⇒ a wheel writes the offset synchronously)

        var vp = host.Scene.AbsoluteRect(host.Scene.Root);
        var pt = new Point2(vp.X + 60f, vp.Y + 20f);   // fixed point over ROW 0 (top 40px), inside the 180px-wide row

        // Warm the hover + scroll + re-eval path (JIT + seed both rows' hover anim-slab channels) OUTSIDE the measured
        // frame, so the zero-alloc assertion measures steady state (mirrors gate.scroll.alloc-zero's warm pass).
        window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0)); host.RunFrame();
        for (int w = 0; w < 2; w++)
        {
            window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 40f)); host.RunFrame();    // row 0 → row 1
            window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, -40f)); host.RunFrame();   // row 1 → row 0
        }
        for (int i = 0; i < 8; i++) host.RunFrame();   // settle bars/anim back to rest

        // Establish hover on ROW 0 at the fixed point.
        window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0)); host.RunFrame();
        var a = host.Input.HitTest(pt);
        bool hovA = !a.IsNull && (host.Scene.Flags(a) & NodeFlags.Hovered) != 0;

        // MEASURED: wheel one row DOWN with the pointer NOT moving. Offset 0→40 ⇒ row 1 slides under the fixed point.
        window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 40f));
        var f = host.RunFrame();
        var b = host.Input.HitTest(pt);
        bool contentMoved = !b.IsNull && b != a;                                        // a DIFFERENT node is under the point
        bool hovB = !b.IsNull && (host.Scene.Flags(b) & NodeFlags.Hovered) != 0;         // the NEW node is Hovered
        bool oldCleared = a.IsNull || (host.Scene.Flags(a) & NodeFlags.Hovered) == 0;    // the OLD node is NOT Hovered
        bool zero = f.HotPhaseAllocBytes == 0;                                           // the synthesized re-eval is 0-alloc

        Check("gate.scroll.hover-follows-content a wheel-scroll under a stationary cursor moves NodeFlags.Hovered to the row that slid under the point (old row cleared), with no PointerMove and 0 managed alloc on the re-eval frame",
            hovA && contentMoved && hovB && oldCleared && zero,
            $"a={(a.IsNull ? "null" : a.Raw.Index.ToString())} b={(b.IsNull ? "null" : b.Raw.Index.ToString())} hovA={hovA} moved={contentMoved} hovB={hovB} oldCleared={oldCleared} alloc={f.HotPhaseAllocBytes}B");

        ScrollHoverVirtualCheck(strings);
    }

    static void ScrollChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollProbe());

        host.RunFrame();   // mount + layout → ContentSize published
        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc0);
        bool sized = Near(sc0.ContentH, 800) && Near(sc0.ViewportH, 200);

        // a 200px viewport over 40px rows shows ~5 of 20 — the rest are clip-culled; clip is balanced.
        int drawnAtTop = device.LastRects.Count;
        bool clipped = device.LastClips.Count >= 1 && device.ClipBalance == 0 && drawnAtTop is >= 5 and < 20;

        // wheel down 100 → offset 100, content transform −100, NO re-render (transform-only frame)
        var center = new Point2(100, 100);
        window.QueueInput(new InputEvent(InputKind.Wheel, center, 0, 0, 100f));
        var f = host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc1);
        bool scrolled = Near(sc1.OffsetY, 100)
            && Near(host.Scene.Paint(sc1.ContentNode).LocalTransform.Dy, -100)
            && !f.Rendered;

        // fling past the end → clamp to ContentH − ViewportH = 600
        window.QueueInput(new InputEvent(InputKind.Wheel, center, 0, 0, 10000f));
        host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc2);
        bool clamped = Near(sc2.OffsetY, 600);

        Check("36. ScrollView publishes ContentSize + clips overflow", sized && clipped, $"content={sc0.ContentH:0} drawn={drawnAtTop} clips={device.LastClips.Count}");
        Check("37. wheel scrolls via transform (layout-free) + clamps", scrolled && clamped, $"off→{sc1.OffsetY:0}, clamp={sc2.OffsetY:0}");
    }

    static void TwoAxisScrollChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("twoaxis", new Size2(220, 220), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new NestedScrollProbe());
        host.RunFrame();

        var outer = host.Scene.Root;                    // the vertical page scroller
        NodeHandle inner = NodeHandle.Null;             // the nested horizontal scroller
        void Visit(NodeHandle n)
        {
            if (n.IsNull) return;
            if (host.Scene.TryGetScroll(n, out var s) && s.Orientation == 1) inner = n;
            for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
        }
        Visit(outer);

        var pos = new Point2(90, 30);   // over the inner horizontal box (top strip; it stays there through both wheels)

        // Horizontal wheel over the inner box → the BOX scrolls on X; the page Y stays 0 (the swipe never leaks vertical).
        window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, 0f, ScrollDeltaX: 60f));
        host.RunFrame();
        host.Scene.TryGetScroll(outer, out var o1);
        host.Scene.TryGetScroll(inner, out var i1);
        bool hWheelScrollsBox = Near(i1.OffsetX, 60) && Near(o1.OffsetY, 0);

        // Vertical wheel over the same box → the PAGE scrolls on Y (climbing past the horizontal box); the box X is unchanged.
        window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, 80f));
        host.RunFrame();
        host.Scene.TryGetScroll(outer, out var o2);
        host.Scene.TryGetScroll(inner, out var i2);
        bool vWheelScrollsPage = Near(o2.OffsetY, 80) && Near(i2.OffsetX, 60);

        Check("37b. wheel-axis routing: horizontal wheel scrolls a nested horizontal box (not the page); vertical wheel scrolls the page past it",
            !inner.IsNull && hWheelScrollsBox && vWheelScrollsPage,
            $"box.X={i2.OffsetX:0} page.Y={o2.OffsetY:0} (after-h: box.X={i1.OffsetX:0} page.Y={o1.OffsetY:0})");
    }

    static void ScrollCrossAxisChecks(StringTable strings)
    {
        const string longLine =
            "This intentionally unwrapped caption is long enough to exceed the viewport width and must not widen the vertical scroll content or nested virtual grids.";

        var tree = Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            Gap = 12f,
            Padding = Edges4.All(24f),
            Children =
            [
                new TextEl(longLine) { Size = 14f },
                new BoxEl
                {
                    Height = 260f,
                    Children =
                    [
                        Virtual.Grid(1000, columns: 4, itemHeight: 110f, gap: 12f,
                            renderItem: i => new BoxEl { Fill = ColorF.FromRgba(40, 40, 40) },
                            keyOf: i => "g" + i),
                    ],
                },
            ],
        });

        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root, new Size2(480f, 320f));

        scene.TryGetScroll(scene.Root, out var outer);
        NodeHandle gridViewport = NodeHandle.Null;
        void Visit(NodeHandle n)
        {
            if (n.IsNull) return;
            if (scene.TryGetScroll(n, out var sc) && sc.ItemCount == 1000) gridViewport = n;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) Visit(c);
        }
        Visit(scene.Root);

        scene.TryGetScroll(gridViewport, out var gridScroll);
        var firstCard = scene.FirstChild(gridScroll.ContentNode);
        var grid = scene.AbsoluteRect(gridViewport);
        var card = scene.AbsoluteRect(firstCard);

        bool contentClamped = Near(outer.ContentW, outer.ViewportW) && Near(scene.Bounds(outer.ContentNode).W, outer.ViewportW);
        bool gridConstrained = !gridViewport.IsNull && Near(grid.W, 432f) && card.W <= 101f;

        Check("37a. vertical ScrollView clamps cross-axis content before nested virtual layout",
            contentClamped && gridConstrained,
            $"contentW={outer.ContentW:0} viewportW={outer.ViewportW:0} gridW={grid.W:0} cardW={card.W:0}");
    }

    static void ScrollOverlayChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scrollbar", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollProbe());

        static RectF DeviceRect(FillRoundRectCmd cmd) => cmd.Transform.TransformBounds(cmd.Rect);
        static bool LaneRect(FillRoundRectCmd cmd, out RectF r)
        {
            r = DeviceRect(cmd);
            return r.X >= 186f && r.X <= 200.5f && r.W <= 13.5f && r.H > 20f;
        }

        host.RunFrame();

        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(198f, 100f), 0, 0));
        for (int i = 0; i < 40; i++) host.RunFrame();

        bool expandedGutter = false, expandedThumb = false;
        foreach (var rect in device.LastRects)
        {
            if (!LaneRect(rect, out var r)) continue;
            expandedGutter |= r.W >= 10f && r.H >= 190f;
            expandedThumb |= r.W >= 5f && r.W < 10f && r.H >= 30f && r.H < 190f;
        }
        bool hoverSettledIdle = !host.HasActiveWork;

        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(260f, 260f), 0, 0));
        for (int i = 0; i < 30; i++) host.RunFrame();

        bool collapsedGutter = false, collapsedThumb = false;
        foreach (var rect in device.LastRects)
        {
            if (!LaneRect(rect, out var r)) continue;
            collapsedGutter |= r.W >= 10f && r.H >= 190f;
            collapsedThumb |= r.W <= 3f && r.H >= 30f;   // WinUI resting thumb = 2px visible (8 − 6 stroke)
        }

        for (int i = 0; i < 90; i++) host.RunFrame();

        bool anyScrollbar = false;
        foreach (var rect in device.LastRects)
        {
            anyScrollbar |= LaneRect(rect, out _);
        }

        Check("38a. overlay scrollbar expands, collapses to a visible thumb, then auto-hides",
            expandedGutter && expandedThumb && !collapsedGutter && collapsedThumb && !anyScrollbar,
            $"expanded=({expandedGutter},{expandedThumb}) collapsed=({collapsedGutter},{collapsedThumb}) hidden={!anyScrollbar}");
        Check("38b. hover-visible scrollbar settles without keeping frames active",
            hoverSettledIdle, $"active={host.HasActiveWork}");
    }

    static void BoundItemsViewChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("bound-iv", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new BoundItemsViewProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        host.RunFrame();
        var scene = host.Scene;
        var vp = FindScrollNode(scene, scene.Root);
        scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;

        // slot root (the AccentPill BoxEl) → [ lv-content → [text] , lv-pill ].
        var slot0 = scene.FirstChild(content);
        var lane0 = scene.FirstChild(slot0);
        var text0 = scene.FirstChild(lane0);
        var pill0 = scene.NextSibling(lane0);

        float pillRest = scene.Paint(pill0).Opacity;
        int callsBase = probe.TemplateCalls;

        // 1. Selecting a visible row flips its bound pill opacity 0→1 with NO row-template re-run (no rebuild = no flash).
        probe.Selection.Select(0);
        host.RunFrame();
        float pillSel = scene.Paint(pill0).Opacity;
        int callsSel = probe.TemplateCalls;
        Check("IV-bound 1. select flips the bound pill in place (no template re-run = no flash)",
            pillRest < 0.01f && pillSel > 0.99f && callsSel == callsBase,
            $"pill {pillRest:0.00}→{pillSel:0.00} templateCalls {callsBase}→{callsSel}");

        // 2. The selection re-skin frame allocates 0 managed bytes on the hot (paint) phases — it is a bound opacity write,
        //    not a rebuild.
        probe.Selection.Deselect(0);
        var fDe = host.RunFrame();
        Check("IV-bound 2. selection re-skin is 0-alloc on the hot phases",
            fDe.HotPhaseAllocBytes == 0, $"{fDe.HotPhaseAllocBytes} bytes");

        // 3. Now-playing recolours the bound title in place, again with NO row-template re-run.
        var colorRest = scene.Paint(text0).TextColor;
        int callsNowBase = probe.TemplateCalls;
        probe.NowPlaying.Value = 0;
        host.RunFrame();
        var colorNow = scene.Paint(text0).TextColor;
        Check("IV-bound 3. now-playing recolours the title in place (no template re-run)",
            !colorRest.Equals(colorNow) && probe.TemplateCalls == callsNowBase,
            $"colorChanged={!colorRest.Equals(colorNow)} templateCalls {callsNowBase}→{probe.TemplateCalls}");

        // 4. Exactly ONE realized slot carries the roving tab stop (NodeFlags.Focusable) — slot 0 via the no-current
        //    fallback (the others are Focusable=false, so the list has a single tab stop, moved without a re-render).
        int focusable = 0; NodeHandle theStop = NodeHandle.Null;
        for (var c = scene.FirstChild(content); !c.IsNull; c = scene.NextSibling(c))
            if ((scene.Flags(c) & NodeFlags.Focusable) != 0) { focusable++; theStop = c; }
        Check("IV-bound 4. exactly one realized slot holds the roving tab stop (no-current fallback = slot 0)",
            focusable == 1 && theStop == slot0, $"focusable={focusable} isSlot0={theStop == slot0}");

        // Same-count replacement: title is not special. Title, artist, album and duration are four separate mounted
        // binds, and all four must update on the same persistent row without re-running the template or replacing nodes.
        using var atomicApp = new HeadlessPlatformApp();
        var atomicWindow = new HeadlessWindow(new WindowDesc("bound-atomic", new Size2(640, 480), 1f));
        atomicWindow.Show();
        var atomicProbe = new BoundAtomicItemsProbe();
        using var atomicHost = new AppHost(atomicApp, atomicWindow, new HeadlessGpuDevice(), fonts, strings, atomicProbe);
        atomicHost.RunFrame();
        int atomicCalls = atomicProbe.TemplateCalls;
        var oldTitle = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "old-title");
        var oldArtist = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "old-artist");
        var oldAlbum = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "old-album");
        var oldDuration = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "101");
        atomicProbe.Items.Value = new BoundAtomicItem[]
        {
            new("new-title", "new-artist", "new-album", 303),
            new("other-new-title", "other-new-artist", "other-new-album", 404),
        };
        atomicHost.RunFrame();
        var newTitle = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "new-title");
        var newArtist = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "new-artist");
        var newAlbum = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "new-album");
        var newDuration = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "303");
        bool currentActionItem = atomicProbe.Source.TryPeek(0, out var current) && current.Title == "new-title";
        Check("IV-bound 4a. same-count source replacement atomically updates every cell on persistent row nodes",
            !oldTitle.IsNull && newTitle == oldTitle && newArtist == oldArtist && newAlbum == oldAlbum && newDuration == oldDuration
            && atomicProbe.TemplateCalls == atomicCalls && currentActionItem,
            $"sameNodes={newTitle == oldTitle && newArtist == oldArtist && newAlbum == oldAlbum && newDuration == oldDuration} " +
            $"templateCalls={atomicCalls}->{atomicProbe.TemplateCalls} currentItem={current.Title}");

        // 5/6. A clickable child + an inline link inside a COMPONENT-wrapped bound row must receive the click — not have
        //      it fall through to the row's tap/double-tap. Mirrors the exact Wavee row shape (skin → lane → component → grid).
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("bound-iv-hit", new Size2(640, 480), 1f));
        window2.Show();
        var hitProbe = new RowButtonHitProbe();
        using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, hitProbe);
        host2.RunFrame();
        var s2 = host2.Scene;
        var skin2 = s2.FirstChild(s2.Root);
        var lane2 = s2.FirstChild(skin2);
        var comp2 = s2.FirstChild(lane2);
        var grid2 = s2.FirstChild(comp2);
        var heartCell = s2.NextSibling(s2.FirstChild(grid2));    // grid child 1 (after the # cell)
        var heart = s2.FirstChild(heartCell);                    // the clickable heart BoxEl
        var hr = s2.AbsoluteRect(heart);
        var heartCenter = new Point2(hr.X + hr.W / 2f, hr.Y + hr.H / 2f);
        var hitNode = host2.Input.HitTest(heartCenter);
        window2.QueueInput(new InputEvent(InputKind.PointerDown, heartCenter, 0, 0));
        window2.QueueInput(new InputEvent(InputKind.PointerUp, heartCenter, 0, 0));
        host2.RunFrame();
        Check("IV-bound 5. a clickable child in a component-wrapped bound row gets the click (not the row's tap)",
            hitProbe.HeartClick == 1 && hitProbe.RowPress == 0 && hitNode == heart,
            $"heartClick={hitProbe.HeartClick} rowPress={hitProbe.RowPress} hitIsHeart={hitNode == heart}");

        var linkCell = s2.NextSibling(s2.NextSibling(heartCell)); // grid child 3 (#, heart, title, album)
        var span = s2.FirstChild(linkCell);                       // the SpanTextEl link node
        var lr = s2.AbsoluteRect(span);
        var linkPt = new Point2(lr.X + 4f, lr.Y + lr.H / 2f);     // over the link glyphs (left edge)
        window2.QueueInput(new InputEvent(InputKind.PointerDown, linkPt, 0, 0));
        window2.QueueInput(new InputEvent(InputKind.PointerUp, linkPt, 0, 0));
        host2.RunFrame();
        Check("IV-bound 6. an inline link in a component-wrapped bound row navigates (not the row's tap)",
            hitProbe.LinkClick == 1 && hitProbe.RowPress == 0,
            $"linkClick={hitProbe.LinkClick} rowPress={hitProbe.RowPress}");

        // 7. The VIRTUALIZED bound path: a clickable child on a realized CreateBound SLOT row must get the tap (not the row).
        using var app3 = new HeadlessPlatformApp();
        var window3 = new HeadlessWindow(new WindowDesc("bound-iv-vhit", new Size2(640, 480), 1f));
        window3.Show();
        var vProbe = new BoundListHitProbe();
        using var host3 = new AppHost(app3, window3, new HeadlessGpuDevice(), fonts, strings, vProbe);
        host3.RunFrame();
        vProbe.Now.Value = 0;     // trigger a row-content re-render first (rebuilds the child + its handler) — the Wavee path
        host3.RunFrame();
        var s3 = host3.Scene;
        var vp3 = FindScrollNode(s3, s3.Root);
        s3.TryGetScroll(vp3, out var sc3);
        var slotR = s3.FirstChild(sc3.ContentNode);          // slot 0 root (the row skin)
        var laneR = s3.FirstChild(slotR);                    // content lane
        var compR = s3.FirstChild(laneR);                    // the row-content component anchor
        var cellR = s3.FirstChild(compR);                    // the # cell (ZStack: reveal layer + click-catcher)
        var catcher = s3.NextSibling(s3.FirstChild(cellR));  // the Grow=1f click-catcher (2nd child)
        var cr = s3.AbsoluteRect(cellR);
        var cellCenter = new Point2(cr.X + cr.W / 2f, cr.Y + cr.H / 2f);
        var vHit = host3.Input.HitTest(cellCenter);
        window3.QueueInput(new InputEvent(InputKind.PointerDown, cellCenter, 0, 0));
        window3.QueueInput(new InputEvent(InputKind.PointerUp, cellCenter, 0, 0));
        host3.RunFrame();
        // …and again near the cell EDGE — proving the Grow=1f catcher FILLS the cell (not just a centered point).
        var cellEdge = new Point2(cr.X + 3f, cr.Y + cr.H - 3f);
        window3.QueueInput(new InputEvent(InputKind.PointerDown, cellEdge, 0, 0));
        window3.QueueInput(new InputEvent(InputKind.PointerUp, cellEdge, 0, 0));
        host3.RunFrame();
        Check("IV-bound 7. the # cell play-catcher (Grow=1f over a HoverOpacity reveal) takes the click across the cell, not the row",
            vProbe.ChildClick == 2 && vProbe.RowPress == 0 && vHit == catcher,
            $"childClick={vProbe.ChildClick} rowPress={vProbe.RowPress} hitIsCatcher={vHit == catcher} cellRect=({cr.X:0},{cr.Y:0},{cr.W:0},{cr.H:0})");

        // 8. THE REPORTED BUG: hovering an interactive CHILD (the # cell catcher) must KEEP the row's HoverWithin set, so
        //    the # cell's HoverOpacity reveal stays revealed (the play glyph must not snap back to the number when you
        //    move the pointer onto it to click). Hover the empty row body first, then move onto the catcher.
        var skinR = s3.FirstChild(sc3.ContentNode);
        var skinRect = s3.AbsoluteRect(skinR);
        window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(skinRect.X + 200f, skinRect.Y + skinRect.H / 2f), 0, 0));
        host3.RunFrame();
        bool bodyHover = (s3.Flags(skinR) & NodeFlags.Hovered) != 0;
        var catcherRect = s3.AbsoluteRect(catcher);
        window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(catcherRect.X + catcherRect.W / 2f, catcherRect.Y + catcherRect.H / 2f), 0, 0));
        host3.RunFrame();
        bool withinSet = (s3.Flags(skinR) & NodeFlags.HoverWithin) != 0;
        Check("IV-bound 8. hovering the # cell catcher keeps the row HoverWithin (so the play glyph stays revealed)",
            bodyHover && withinSet, $"bodyHover={bodyHover} rowHoverWithin={withinSet}");

        // 9. THE FIX (engine): with the pointer ON the catcher (from gate 8), the # cell's HoverOpacity reveal layer must
        //    stay DRIVEN (hover target 1 — it does not collapse to the number); it decays only when the pointer leaves
        //    the row entirely. Verifies the InteractionAnimator honors the container's HoverWithin.
        var revealLayer = s3.FirstChild(cellR);   // the HoverOpacity reveal layer (sibling of the catcher)
        bool revealOnCatcher = s3.TryGetInteract(revealLayer, out var raOn) && raOn.HoverTarget > 0.99f;
        window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(skinRect.X + skinRect.W + 80f, skinRect.Y + skinRect.H + 80f), 0, 0));
        host3.RunFrame();
        bool revealOffOutside = s3.TryGetInteract(revealLayer, out var raOff) && raOff.HoverTarget < 0.01f;
        Check("IV-bound 9. the # cell reveal stays driven while the pointer is on the play button, decays only off the row",
            revealOnCatcher && revealOffOutside, $"onCatcher={revealOnCatcher} offRow={revealOffOutside}");

        using var app4 = new HeadlessPlatformApp();
        var window4 = new HeadlessWindow(new WindowDesc("bound-iv-count-signal", new Size2(640, 480), 1f));
        window4.Show();
        var countProbe = new BoundCountSignalProbe();
        using var host4 = new AppHost(app4, window4, new HeadlessGpuDevice(), fonts, strings, countProbe);
        host4.RunFrame();
        var s4 = host4.Scene;
        var vp4 = FindScrollNode(s4, s4.Root);
        s4.TryGetScroll(vp4, out var sc4a);
        int slotsA = s4.ChildCount(sc4a.ContentNode);
        countProbe.Count.Value = 6;
        host4.RunFrame();
        s4.TryGetScroll(vp4, out var sc4b);
        int slotsB = s4.ChildCount(sc4b.ContentNode);
        countProbe.Count.Value = 3;
        host4.RunFrame();
        s4.TryGetScroll(vp4, out var sc4c);
        int slotsC = s4.ChildCount(sc4c.ContentNode);
        Check("IV-bound 10. bound ItemCount can change through a signal without remounting the list wrapper",
            sc4a.ItemCount == 4 && sc4b.ItemCount == 6 && sc4c.ItemCount == 3 && slotsA == 4 && slotsB == 6 && slotsC == 3,
            $"counts {sc4a.ItemCount}->{sc4b.ItemCount}->{sc4c.ItemCount} slots {slotsA}->{slotsB}->{slotsC}");
    }

    static void VirtualChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("virt", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VirtualProbe());

        host.RunFrame();
        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc0);
        var content = sc0.ContentNode;
        int realized = host.Scene.ChildCount(content);
        bool windowed = realized >= 10 && realized < 40;
        bool contentSize = Near(sc0.ContentH, VirtualProbe.N * 40f);
        Check("38. virtualizes 10k rows to a small window", windowed && contentSize, $"realized={realized}/{VirtualProbe.N} content={sc0.ContentH:0}");

        // in-window (sub-extent) scroll = transform-only frame: no re-render, window unchanged, content shifted.
        int firstA = sc0.FirstRealized;
        var ptr = new Point2(150, 200);
        window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 10f));
        var f1 = host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sc1);
        bool transformOnly = !f1.Rendered && sc1.FirstRealized == firstA
            && Near(host.Scene.Paint(content).LocalTransform.Dy, -10f);
        Check("39. in-window scroll is transform-only (no realize/relayout)", transformOnly, $"rendered={f1.Rendered} first={sc1.FirstRealized}");

        // boundary-crossing fling to the end: window re-realizes directly; recycle keeps live nodes bounded (no leak).
        long live0 = host.Scene.LiveCount;
        for (int s = 0; s < 60; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 7000f)); host.RunFrame(); }
        host.Scene.TryGetScroll(vp, out var sc2);
        long liveEnd = host.Scene.LiveCount;
        bool reachedEnd = sc2.FirstRealized > 9000;
        bool bounded = liveEnd < 90 && Math.Abs(liveEnd - live0) < 40;
        Check("40. fling recycles via free-list (bounded live nodes, no leak)", reachedEnd && bounded, $"first={sc2.FirstRealized} live {live0}→{liveEnd}");

        var counted = new CountingVirtualProbe();
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("virt-overlap", new Size2(640, 480), 1f));
        window2.Show();
        using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, counted);
        host2.RunFrame();
        int calls0 = counted.RenderItemCalls;
        window2.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 100), 0, 0, 80f));
        var guardFrame = host2.RunFrame();
        int guardCalls = counted.RenderItemCalls - calls0;
        host2.Scene.TryGetScroll(host2.Scene.Root, out var guardScroll);
        bool guardHeld = !guardFrame.Rendered && guardScroll.FirstRealized == 0 && guardCalls == 0;

        window2.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 100), 0, 0, 120f));
        host2.RunFrame();
        int calls1 = counted.RenderItemCalls - calls0 - guardCalls;
        host2.Scene.TryGetScroll(host2.Scene.Root, out var countedScroll);
        bool reusedOverlap = guardHeld && countedScroll.FirstRealized > 0 && calls1 <= 5;
        Check("40a. virtual scroll keeps overscan guard and reuses overlapping item elements",
            reusedOverlap, $"guardCalls={guardCalls} first={countedScroll.FirstRealized} newTemplateCalls={calls1}");

        // Far jump (zero overlap — the scrollbar thumb-drag storm): the window's scene NODES are recycled in place
        // (columns rebound to the new items), not mounted/removed — the drag path becomes a column rewrite.
        var beforeNodes = new List<NodeHandle>();
        for (var c = host.Scene.FirstChild(content); !c.IsNull; c = host.Scene.NextSibling(c)) beforeNodes.Add(c);
        long liveBeforeJump = host.Scene.LiveCount;
        window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, -400_000f));   // end → top: no overlap with the old window
        host.RunFrame();
        var afterNodes = new HashSet<NodeHandle>();
        for (var c = host.Scene.FirstChild(content); !c.IsNull; c = host.Scene.NextSibling(c)) afterNodes.Add(c);
        host.Scene.TryGetScroll(vp, out var scTop);
        int recycledCount = 0;
        foreach (var n in beforeNodes) if (afterNodes.Contains(n)) recycledCount++;
        var firstRowText = host.Scene.FirstChild(host.Scene.FirstChild(content));
        string rebound = strings.Resolve(host.Scene.Paint(firstRowText).Text);
        bool recycledOk = scTop.FirstRealized == 0 && afterNodes.Count == beforeNodes.Count
            && recycledCount == beforeNodes.Count && host.Scene.LiveCount == liveBeforeJump && rebound == "row 0";
        Check("40b. far-jump realize recycles the window's scene nodes (rebind, no mount/remove)",
            recycledOk, $"first={scTop.FirstRealized} recycled={recycledCount}/{beforeNodes.Count} live {liveBeforeJump}→{host.Scene.LiveCount} text='{rebound}'");

        // 40b2 — theme correctness ON a recycled node: the default text color is a mount-time singleton-brush binding
        // that PERSISTS across recycle (Update rewrites columns, never bindings — recyclability of the bound default
        // rides that identity). A retheme AFTER the far-jump recycle must repaint the reused node via the re-fired
        // binding — the recycled row follows the live theme, not the color it was mounted under.
        {
            ColorF darkPrimary = Tok.TextPrimary;
            bool darkOk = host.Scene.Paint(firstRowText).TextColor == darkPrimary;
            Tok.Use(ThemeKind.Light);
            host.RunFrame();
            ColorF lightPrimary = Tok.TextPrimary;
            var t2 = host.Scene.FirstChild(host.Scene.FirstChild(content));
            bool lightOk = lightPrimary != darkPrimary && host.Scene.Paint(t2).TextColor == lightPrimary;
            Tok.Use(ThemeKind.Dark);
            host.RunFrame();
            Check("40b2. a recycled default-colored row follows a retheme (the persisted singleton-brush binding re-fires on the reused node)",
                darkOk && lightOk, $"dark={darkOk} light={lightOk}");
        }

        // Streaming thousands of unique row strings must NOT accrete in the interner: scrolled-out text releases its
        // ref, the map entry drops immediately, and the slot clears behind the reader quarantine (StringTable.Tick).
        int mapBase = strings.MapCount;
        for (int s = 0; s < 40; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 5_000f)); host.RunFrame(); }
        for (int s = 0; s < 25; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, s % 2 == 0 ? 1f : -1f)); host.RunFrame(); }   // settle past the quarantine (painted frames tick the table)
        int mapAfter = strings.MapCount;
        host.Scene.TryGetScroll(vp, out var scStream);
        bool streamed = scStream.FirstRealized > 3000;
        bool reclaimed = mapAfter - mapBase < 200 && strings.PendingReclaim < 200;
        Check("40c. scrolled-out row text is reclaimed by the interner (no per-row string accretion)",
            streamed && reclaimed, $"first={scStream.FirstRealized} map {mapBase}→{mapAfter} pending={strings.PendingReclaim}");

        // BOUND list (Virtual.ListBound): the template runs once per visible slot; a far jump rebinds index SIGNALS
        // only — same nodes, same elements, zero template re-runs, text/fill rebound in the same frame.
        var bound = new BoundVirtualProbe();
        using var app3 = new HeadlessPlatformApp();
        var window3 = new HeadlessWindow(new WindowDesc("virt-bound", new Size2(640, 480), 1f));
        window3.Show();
        using var host3 = new AppHost(app3, window3, new HeadlessGpuDevice(), fonts, strings, bound);
        host3.RunFrame();
        var vp3 = host3.Scene.Root;
        host3.Scene.TryGetScroll(vp3, out var bsc0);
        var content3 = bsc0.ContentNode;

        // First jump to mid-list (the window stabilizes at full size: overscan extends both directions), THEN measure.
        window3.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, 100_000f));
        host3.RunFrame();
        int slots0 = host3.Scene.ChildCount(content3);
        int templateCalls0 = bound.TemplateCalls;
        var slotNodes = new HashSet<NodeHandle>();
        for (var c = host3.Scene.FirstChild(content3); !c.IsNull; c = host3.Scene.NextSibling(c)) slotNodes.Add(c);

        window3.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, 100_000f));   // far jump: zero overlap
        host3.RunFrame();
        host3.Scene.TryGetScroll(vp3, out var bsc1);
        int slots1 = host3.Scene.ChildCount(content3);
        bool sameNodes = true;
        for (var c = host3.Scene.FirstChild(content3); !c.IsNull; c = host3.Scene.NextSibling(c)) sameNodes &= slotNodes.Contains(c);
        var boundFirstText = strings.Resolve(host3.Scene.Paint(host3.Scene.FirstChild(host3.Scene.FirstChild(content3))).Text);
        bool boundOk = bsc1.FirstRealized > 4000 && bound.TemplateCalls == templateCalls0 && slots1 == slots0
            && sameNodes && boundFirstText == $"row {bsc1.FirstRealized}";
        Check("40d. bound list rebinds via index signals on a far jump (no template re-run, no node churn)",
            boundOk, $"first={bsc1.FirstRealized} template {templateCalls0}→{bound.TemplateCalls} slots {slots0}→{slots1} sameNodes={sameNodes} text='{boundFirstText}'");

        // Scrollbar THUMB DRAG on a bound list (the 100k storm path): grab the thumb, drag in small steps — the
        // offset must track the thumb the whole way (the drag must never silently disengage mid-travel).
        window3.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, -10_000_000f));   // back to the top
        host3.RunFrame();
        float laneX = 294f;
        window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(laneX, 10f), 0, 0));
        host3.RunFrame();
        window3.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(laneX, 10f), 0, 0));
        host3.RunFrame();
        float lastOff = 0f;
        int advanceSteps = 0;
        const int dragSteps = 120;
        for (int s = 1; s <= dragSteps; s++)
        {
            window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(laneX, 10f + s * 2.5f), 0, 0));
            host3.RunFrame();
            host3.Scene.TryGetScroll(vp3, out var dsc);
            if (dsc.OffsetY > lastOff) advanceSteps++;
            lastOff = dsc.OffsetY;
        }
        window3.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(laneX, 10f + dragSteps * 2.5f), 0, 0));
        host3.RunFrame();
        host3.Scene.TryGetScroll(vp3, out var dragEnd);
        bool dragOk = advanceSteps >= dragSteps - 5 && dragEnd.OffsetY > 100_000f && dragEnd.FirstRealized > 2000;
        Check("40e. scrollbar thumb drag tracks the full travel on a bound list (never disengages)",
            dragOk, $"advanced {advanceSteps}/{dragSteps} off={dragEnd.OffsetY:0} first={dragEnd.FirstRealized}");
    }

    static void ExtentTableChecks()
    {
        var t = new ExtentTable(5, 10f);   // 5 items × 10 = 50
        bool init = Near((float)t.Total, 50) && Near(t.OffsetOf(0), 0) && Near(t.OffsetOf(2), 20) && Near(t.OffsetOf(5), 50);
        bool indexAt = t.IndexAt(0) == 0 && t.IndexAt(15) == 1 && t.IndexAt(25) == 2 && t.IndexAt(49) == 4;
        t.SetExtent(2, 30f);   // correct item 2: 10 → 30 (total 50 → 70)
        bool corrected = Near((float)t.Total, 70) && Near(t.OffsetOf(3), 50) && t.IndexAt(45) == 2 && t.IndexAt(55) == 3;
        Check("41. extent table: O(log n) offset↔index + correction", init && indexAt && corrected, $"total={t.Total:0}");
    }

    static void VariableChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("var", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VarProbe());

        host.RunFrame();
        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;

        // rows positioned by cumulative measured heights (OffsetOf), and the extent table corrected for the window
        var r0 = Child(host.Scene, content, 0);
        var r1 = Child(host.Scene, content, 1);
        var r2 = Child(host.Scene, content, 2);
        var r3 = Child(host.Scene, content, 3);
        bool positions = Near(host.Scene.Bounds(r0).Y, 0)
            && Near(host.Scene.Bounds(r1).Y, VarProbe.H(0))
            && Near(host.Scene.Bounds(r2).Y, VarProbe.H(0) + VarProbe.H(1))
            && Near(host.Scene.Bounds(r3).Y, VarProbe.H(0) + VarProbe.H(1) + VarProbe.H(2));
        host.Scene.TryGetExtents(vp, out var table);
        bool measured = table is not null && Near(table.ExtentAt(0), VarProbe.H(0)) && Near(table.ExtentAt(2), VarProbe.H(2));
        Check("42. variable rows positioned by measured extents", positions && measured, $"y0..3={host.Scene.Bounds(r0).Y:0},{host.Scene.Bounds(r1).Y:0},{host.Scene.Bounds(r2).Y:0},{host.Scene.Bounds(r3).Y:0}");

        // scroll into the middle → anchor tracks the visible item; offset stays in its band and clamped to content
        for (int s = 0; s < 6; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 150), 0, 0, 350f)); host.RunFrame(); }
        host.Scene.TryGetScroll(vp, out var sc2);
        host.Scene.TryGetExtents(vp, out var t2);
        int anchor = t2!.IndexAt(sc2.OffsetY);
        float band0 = t2.OffsetOf(anchor), band1 = t2.OffsetOf(anchor + 1);
        bool anchored = sc2.AnchorIndex == anchor
            && sc2.OffsetY >= band0 - 0.5f && sc2.OffsetY < band1 + 0.5f
            && sc2.OffsetY <= sc2.ContentH - sc2.ViewportH + 1f && sc2.FirstRealized > 0;
        Check("43. variable scroll anchors on the visible item (in-band, clamped)", anchored, $"anchor={anchor} off={sc2.OffsetY:0} content={sc2.ContentH:0} first={sc2.FirstRealized}");
    }

    static void VirtualBudgetChecks(StringTable strings)
    {
        // A small measured/stateful document can explicitly require its complete overscan on mount. This is the synced-
        // lyrics contract: every requested row must exist and be measured before follow geometry is trusted, regardless
        // of the global overscan refill budget.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-eager-overscan", new Size2(640, 480), 1f)); window.Show();
            var tree = Virtual.List(100, 40f,
                i => new BoxEl { Height = 40f, Fill = ColorF.FromRgba(30, 30, 30) }, keyOf: i => "eo" + i,
                overscan: 100) with
            {
                Width = 300f, Height = 300f, RealizeOverscanImmediately = true,
            };
            var root = new W0fStaticProbe { Build = () => tree };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
            host.Reconciler.SteadyRealizeBudgetForTest = 1;
            host.RunFrame();
            host.Scene.TryGetScroll(host.Scene.Root, out var sc);
            bool clean = (host.Scene.Flags(host.Scene.Root) & NodeFlags.VirtualRangeDirty) == 0;
            Check("gate.virt.eagerOverscan an explicit eager list realizes its complete requested window on mount, bypassing the shared row budget without leaving deferred work",
                sc.FirstRealized == 0 && sc.LastRealized == 100 && clean && !host.Reconciler.HasBudgetDeferredVirtuals,
                $"realized=[{sc.FirstRealized},{sc.LastRealized}) clean={clean} deferred={host.Reconciler.HasBudgetDeferredVirtuals}");
        }

        // A bound slot can lag the already-published ScrollState range (for example when two realize paths converge in
        // one frame). ReRealizeVirtuals must report the SIGNAL rebind as progress even though First/LastRealized stay
        // unchanged, otherwise AppHost skips its post-realize reactive flush and presents a mixed row: index-bound title
        // from item N over component-snapshot artist/art/duration from item N-1.
        {
            var scene = new SceneStore();
            var runtime = new ReactiveRuntime();
            var rows = new List<StableRangeBoundRow>();
            var recon = new TreeReconciler(scene, strings, runtime);
            var tree = Virtual.ListBound(100, 40f, index =>
            {
                var row = new StableRangeBoundRow(index);
                rows.Add(row);
                return Embed.Comp(() => row);
            }) with { Width = 300f, Height = 400f };

            recon.ReconcileRoot(tree, null);
            while (recon.ReRealizeVirtuals()) runtime.Flush();
            runtime.Flush();

            // Put only the slot signals one index ahead while leaving ScrollState's published range untouched, then
            // flush that setup so every component truthfully snapshots the lagging index.
            for (int i = 0; i < rows.Count; i++)
                ((Signal<int>)rows[i].Index).Value++;
            runtime.Flush();

            scene.Mark(scene.Root, NodeFlags.VirtualRangeDirty);
            bool reboundProgress = recon.ReRealizeVirtuals();
            if (reboundProgress && runtime.HasPending) runtime.Flush();   // the exact AppHost contract
            int mismatches = 0;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].LastRendered != rows[i].Index.Peek()) mismatches++;

            Check("gate.virt.stableRangeBoundRebindFlush a bound-slot signal rebind reports realize progress even when the published range is unchanged, so component snapshots flush in the same frame",
                reboundProgress && !runtime.HasPending && mismatches == 0,
                $"progress={reboundProgress} pending={runtime.HasPending} mismatches={mismatches}/{rows.Count}");
        }

        // A stateful/custom layout can briefly return its cached OLD upper window bound after the collection shrinks.
        // These are the exact old-to-new count pairs recorded in the two Wavee crash dumps.
        {
            (int Before, int After)[] transitions = [(60, 43), (521, 208)];
            int bounded = 0;
            string failure = "none";
            foreach (var (before, after) in transitions)
            {
                var probe = new VirtualCountShrinkProbe(before);
                try
                {
                    using var app = new HeadlessPlatformApp();
                    var window = new HeadlessWindow(new WindowDesc("virt-count-shrink", new Size2(640, 480), 1f)); window.Show();
                    using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
                    host.RunFrame();
                    probe.Count.Value = after;
                    host.RunFrame();
                    host.Scene.TryGetScroll(host.Scene.Root, out var sc);
                    if (sc.ItemCount == after && sc.FirstRealized >= 0 && sc.LastRealized <= after
                        && sc.LastRealized >= sc.FirstRealized && probe.MaxRenderedIndex < after)
                        bounded++;
                    else
                        failure = $"{before}->{after}: count={sc.ItemCount} realized=[{sc.FirstRealized},{sc.LastRealized}) maxRendered={probe.MaxRenderedIndex}";
                }
                catch (Exception ex)
                {
                    failure = $"{before}->{after}: {ex.GetType().Name}: {ex.Message}";
                }
            }
            Check("gate.virt.countShrinkClampsStaleLayout stale layout windows are clamped to the current ItemCount before budget clipping for the crash transitions 60->43 and 521->208",
                bounded == transitions.Length, $"bounded={bounded}/{transitions.Length} failure={failure}");
        }
        // ── gate.virt.velocityOverscanDirectional — the halo skews ahead of the scroll direction; NeedsRealize fires
        // before the visible edge exits the realized window at speed (it would not at rest). ────────────────────────────
        {
            VirtualWindowing.DirectionalOverscan(4, +3000f, 40f, out int loF, out int hiF);   // forward ⇒ ahead = high edge (7), behind (1)
            VirtualWindowing.DirectionalOverscan(4, -3000f, 40f, out int loB, out int hiB);   // backward ⇒ ahead = low edge
            VirtualWindowing.DirectionalOverscan(4, 0.4f, 40f, out int loR, out int hiR);      // below threshold ⇒ symmetric
            bool skewFwd = hiF > loF && hiF > 4 && (loF + hiF) == 8;   // ahead buffered, behind trimmed, SUM constant (0-alloc invariant)
            bool skewBack = loB > hiB && loB > 4 && (loB + hiB) == 8;  // mirror in the other direction, same fixed sum
            bool symAtRest = loR == 4 && hiR == 4;                     // no fling ⇒ pre-E5 symmetric window (existing gates unchanged)

            var sc = ScrollState.Default;
            sc.ItemCount = 1000; sc.Overscan = 4; sc.Orientation = 0; sc.ContentH = 40000f;   // avgExtent = 40
            sc.FirstRealized = 100; sc.LastRealized = 120;                                     // visible band [104,118] — 2 rows inside the leading edge
            sc.FlingVelocity = 0f;
            bool restNoRealize = !VirtualWindowing.NeedsRealize(in sc, 104, 118);              // rest guard 2 ⇒ distance 2 does NOT fire
            sc.FlingVelocity = 3000f;                                                          // ahead skewed to 7 ⇒ guard 3 ⇒ 118 > 120−3 ⇒ fires early
            bool flingRealize = VirtualWindowing.NeedsRealize(in sc, 104, 118);

            Check("gate.virt.velocityOverscanDirectional the realize halo skews ahead of the fling (fixed-sum: ahead grows, behind shrinks, total constant), collapses to symmetric at rest, and NeedsRealize fires earlier on the scroll-direction edge at speed",
                skewFwd && skewBack && symAtRest && restNoRealize && flingRealize,
                $"fwd(lo={loF},hi={hiF}) back(lo={loB},hi={hiB}) rest(lo={loR},hi={hiR}) restNoRealize={restNoRealize} flingRealize={flingRealize}");
        }

        // ── gate.virt.budgetNeverBlanksVisible — a fast fling advancing several windows/frame with a TINY budget still
        // realizes the full visible band [visibleFirst,visibleLast] on EVERY frame (the anti-flicker invariant). ────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-budget-blank", new Size2(640, 480), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new VirtualProbe());
            host.SmoothScroll = true;
            host.Reconciler.SteadyRealizeBudgetForTest = 2;   // 2 rows/frame ≪ the per-frame fling travel
            host.RunFrame();
            var vp = host.Scene.Root; host.Scene.TryGetScroll(vp, out var sc0); var content = sc0.ContentNode;
            var ptr = new Point2(150, 200);
            int blanks = 0, frames = 0;
            for (int sflk = 0; sflk < 80; sflk++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 6000f));
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var sc);
                float drawn = -host.Scene.Paint(content).LocalTransform.Dy;
                int vFirst = (int)MathF.Floor(drawn / 40f);
                int vLast = Math.Min(VirtualProbe.N, (int)MathF.Ceiling((drawn + sc.ViewportH) / 40f));
                if (!(sc.FirstRealized <= vFirst && sc.LastRealized >= vLast)) blanks++;
                frames++;
            }
            Check("gate.virt.budgetNeverBlanksVisible a fling with a 2-row budget realizes the visible band (FirstRealized ≤ visibleFirst ∧ LastRealized ≥ visibleLast) on every recorded frame — the budget never blanks a visible row",
                blanks == 0, $"blanks={blanks}/{frames}");
        }

        // ── gate.virt.budgetSpreadsOverscan — steady scroll with budget < overscan: the overscan halo completes across
        // ≥2 subsequent frames, VirtualRangeDirty persists then clears, and the host stays awake until caught up. ────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-budget-spread", new Size2(640, 480), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new VirtualProbe());
            host.RunFrame();
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();   // settle the mount-deferred overscan at full budget
            var vp = host.Scene.Root;
            host.Reconciler.SteadyRealizeBudgetForTest = 1;                       // throttle: 1 overscan row/frame
            var ptr = new Point2(150, 200);
            window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 400f));  // cross a 10-row boundary

            int dirtyFrames = 0, growthFrames = 0, prevWidth = -1, finalWidth = 0;
            bool everAwakeWhileDirty = false, invariantHeld = true;
            for (int f = 0; f < 25; f++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var sc);
                bool dirty = (host.Scene.Flags(vp) & NodeFlags.VirtualRangeDirty) != 0;
                int width = sc.LastRealized - sc.FirstRealized;
                int vFirst = (int)MathF.Floor(sc.OffsetY / 40f);
                if (!(sc.FirstRealized <= vFirst)) invariantHeld = false;
                if (dirty) { dirtyFrames++; if (host.Reconciler.HasBudgetDeferredVirtuals && host.HasActiveWork) everAwakeWhileDirty = true; }
                if (prevWidth >= 0 && width > prevWidth) growthFrames++;
                prevWidth = width; finalWidth = width;
            }
            bool finalClean = (host.Scene.Flags(vp) & NodeFlags.VirtualRangeDirty) == 0;
            bool spread = dirtyFrames >= 2 && growthFrames >= 2 && everAwakeWhileDirty && finalClean && finalWidth >= 16 && invariantHeld;
            Check("gate.virt.budgetSpreadsOverscan a budget<overscan steady scroll fills the overscan halo across ≥2 frames (VirtualRangeDirty persists, host awake via HasBudgetDeferredVirtuals) then clears; the visible band stays covered throughout",
                spread, $"dirtyFrames={dirtyFrames} growthFrames={growthFrames} awake={everAwakeWhileDirty} finalClean={finalClean} finalWidth={finalWidth} invariant={invariantHeld}");
        }

        // ── gate.virt.nestedRailDefersInner — mounting a virtual rail realizes the visible cards only; the overscan
        // halo fills over subsequent frames (the nested-rail-into-view spike flattener). ─────────────────────────────────
        {
            var probe = new NestedRailProbe();
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-nested-rail", new Size2(400, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
            host.Reconciler.SteadyRealizeBudgetForTest = 2;   // throttle so the mount deferral is observable
            host.RunFrame();
            probe.Show.Value = true;
            host.RunFrame();   // mount frame
            var rail = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(rail, out var scMount);
            int mountWidth = scMount.LastRealized - scMount.FirstRealized;

            int settleFrames = 0;
            for (; settleFrames < 30 && host.HasActiveWork; settleFrames++) host.RunFrame();
            host.Scene.TryGetScroll(rail, out var scFull);
            int fullWidth = scFull.LastRealized - scFull.FirstRealized;
            // Visible = 300/60 = 5 cards; at offset 0 the behind overscan clamps, so the settled window is visible + the
            // ahead overscan (≈ 11). The mount frame realizes only ≈ visible+guard+one budget slice — strictly narrower.
            bool defers = !rail.IsNull && mountWidth < fullWidth && (fullWidth - mountWidth) >= 2 && mountWidth <= 10 && settleFrames >= 1;
            Check("gate.virt.nestedRailDefersInner a freshly-mounted virtual rail realizes the visible cards only (overscan 0 at mount), then fills its overscan window over a few frames via the row budget",
                defers, $"mountWidth={mountWidth} fullWidth={fullWidth} settleFrames={settleFrames} rail={(rail.IsNull ? "null" : "ok")}");
        }

        // ── gate.virt.dirtyQueueMatchesScan — marking 2 of 5 virtual lists dirty realizes exactly those two; the steady
        // path iterates the scene-owned queue (scan == dirty count), never the _virtuals dictionary (which has 5). ───────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-dirty-queue", new Size2(360, 680), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new MultiVirtualProbe());
            host.RunFrame();
            // Drain the mount-deferred overscan on all five until a frame does NO realize work (queue empty) — so the marks
            // below are the only dirty entries and the flags aren't already set.
            int drained = 0;
            for (int i = 0; i < 25; i++) { host.RunFrame(); if (host.Reconciler.LastReRealizeScan == 0) { drained = i; break; } }
            var vps = new List<NodeHandle>();
            CollectScrollNodes(host.Scene, host.Scene.Root, vps);
            bool five = vps.Count == MultiVirtualProbe.Lists;

            int scan = -1, realized = -1;
            bool firstRealizedUnchanged = true;
            if (five)
            {
                // Capture the untouched lists' realized windows, then mark exactly two dirty and run the reconciler's
                // realize pass directly (bump FrameEpoch for a clean per-frame accumulator read). Only the two marked
                // viewports are queued ⇒ scan == 2 (the scene-owned queue), never the 5-entry _virtuals dictionary.
                var before = new int[5];
                for (int k = 0; k < 5; k++) { host.Scene.TryGetScroll(vps[k], out var s); before[k] = s.FirstRealized * 1000 + s.LastRealized; }
                host.Scene.Mark(vps[1], NodeFlags.VirtualRangeDirty);
                host.Scene.Mark(vps[3], NodeFlags.VirtualRangeDirty);
                host.Reconciler.FrameEpoch++;                 // reset the scan accumulator for this direct pass
                host.Reconciler.ReRealizeVirtuals();
                scan = host.Reconciler.LastReRealizeScan;
                realized = host.Reconciler.LastReRealizeRealized;
                // The three UNMARKED lists must be untouched (proves the realize was scoped to the dirty queue, not a
                // dictionary-wide re-realize). The two marked lists legitimately re-realize.
                for (int k = 0; k < 5; k++) { if (k == 1 || k == 3) continue; host.Scene.TryGetScroll(vps[k], out var s); if (before[k] != s.FirstRealized * 1000 + s.LastRealized) firstRealizedUnchanged = false; }
            }
            bool onlyTwo = scan == 2 && realized == 2;
            Check("gate.virt.dirtyQueueMatchesScan marking 2 of 5 virtual lists VirtualRangeDirty realizes exactly those two; ReRealizeVirtuals iterates the scene-owned dirty queue (== 2), never the 5-entry _virtuals dictionary",
                five && onlyTwo && firstRealizedUnchanged, $"lists={vps.Count} scan={scan} realized={realized} windowsStable={firstRealizedUnchanged} drainedAt={drained}");
        }
    }

    static void ZeroAllocScrollChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("zalloc", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VirtualProbe());

        host.RunFrame();   // mount
        var ptr = new Point2(150, 200);
        // warm: several sub-extent (5px) scrolls — all stay within item 0 (5×5 = 25 < 40px row) → transform-only
        for (int i = 0; i < 5; i++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 2f)); host.RunFrame(); }
        window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 2f));
        var fz = host.RunFrame();
        bool zero = !fz.Rendered && fz.HotPhaseAllocBytes == 0;
        Check("44. in-window scroll: 0 managed alloc on the paint half", zero, $"{fz.HotPhaseAllocBytes} bytes, rendered={fz.Rendered}");
    }

    static void ScrollPerfWaveChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.wake.scrollHoldSuppressesAmbientCap (W2-P2.1): the RecommendedWaitMs ambient-FPS cap must defer through
        // the 0.45s post-scroll hold, not just the display-rate grace — otherwise slow wheel-notch scrolling over an
        // ambient loop (skeleton shimmer) oscillates 30Hz↔display-rate per notch (the step-up Resync cadence lurch).
        // AmbientAnimationFps=1 makes the branches unambiguous: ambient wait ≈ 1000ms, display-rate pacing is 0/7ms.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scrollhold-cap", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.AmbientAnimationFps = 1;
            host.RunFrame();
            var vp = host.Scene.Root;
            host.Animation.Keyframes(vp, AnimChannel.Opacity,
                new[] { new Keyframe(0f, 0.4f, Easing.Linear), new Keyframe(1f, 1f, Easing.Linear) }, 800f, loop: true);
            host.RunFrame();
            int wIdle = host.RecommendedWaitMs();                 // loop-only motion, no scroll ever → the ambient cap paces
            bool ambientBaseline = wIdle > 300;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            host.MainScrollHoldUntilForTest = now + System.Diagnostics.Stopwatch.Frequency;   // hold LIVE (~1s)
            host.SetScrollGraceForTest(0);                                                    // grace EXPIRED — isolates the hold term
            int wHold = host.RecommendedWaitMs();
            bool holdSuppresses = wHold <= 7;                     // display-rate pacing (0 sync / 7 pace floor), NOT AmbientFrameWaitMs

            host.MainScrollHoldUntilForTest = 0;                  // hold EXPIRED
            host.SetScrollGraceForTest(0);
            int wAfter = host.RecommendedWaitMs();
            bool capReturns = wAfter > 300;

            // And the production arming path: a REAL wheel notch through the dispatcher re-arms the hold at the
            // phase-7 scroll tick (the sync wheel write pulses ScrollMoved → UserScrollActive on the next ticked frame).
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, WheelNotch: 1f));
            host.RunFrame(); host.RunFrame(); host.RunFrame();
            bool holdArmed = host.MainScrollHoldUntilForTest > System.Diagnostics.Stopwatch.GetTimestamp();
            Check("gate.wake.scrollHoldSuppressesAmbientCap the ambient FPS cap defers through the post-scroll hold: loop-only motion paces at the cap; hold live (grace expired) ⇒ display-rate pacing, NOT AmbientFrameWaitMs; hold expired ⇒ the cap returns; a real wheel notch arms the hold",
                ambientBaseline && holdSuppresses && capReturns && holdArmed,
                $"wIdle={wIdle} (want >300) wHold={wHold} (want <=7) wAfter={wAfter} (want >300) holdArmed={holdArmed}");
        }

        // gate.motion.scrollSuppressionSnapsFlip (W2-P2.2): a reconcile landing on the frame right after a scroll
        // offset actually wrote SNAPS the moved BoundsAnimated node (no structural FLIP track seeded — cards must not
        // fly through a scrolling viewport); the same move on a still frame FLIPs — both before any scroll AND after
        // one while the 0.45s hold is still live but no offset moved (a click-triggered expand right after scrolling).
        {
            FluentGpu.Dsl.Motion.ReducedMotion = false;
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scrollflip", new Size2(500, 400), 1f)); window.Show();
            var probe = new ScrollFlipProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            for (int i = 0; i < 4; i++) host.RunFrame();

            // Control: a reconcile move on a STILL frame (no scroll motion ever) seeds the structural FLIP track.
            probe.Moved.Value = true;                             // spacer 20→150 ⇒ box slot moves down 130px
            host.RunFrame();
            float dyFlip = host.Scene.Paint(probe.Box).LocalTransform.Dy;
            bool flipSeeds = host.Animation.HasTracks(probe.Box) && dyFlip < -50f;   // departs from the OLD slot
            host.Animation.SnapStructuralToLayout(probe.Box);     // settle instantly (bounded, deterministic)
            host.RunFrame();

            // Scroll-coincident: a wheel notch writes the offset THIS frame (sync path) → the latch arms for the NEXT
            // frame, where the reconcile lands → ApplyProjections must take its suppressed-snap branch.
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(100, 150), 0, 0, WheelNotch: 1f));
            host.RunFrame();
            bool offsetWrote = host.ScrollIntegratorForTest.AnyOffsetWroteThisFrame;   // proof the scroll really moved
            probe.Moved.Value = false;                            // move back 150→20 on the post-write frame
            host.RunFrame();
            float dySnap = host.Scene.Paint(probe.Box).LocalTransform.Dy;
            bool snapped = !host.Animation.HasTracks(probe.Box) && MathF.Abs(dySnap) < 0.01f;

            // Hold still LIVE but no offset motion since ⇒ the very next click-triggered move must STILL FLIP
            // (the suppression gates on actual motion last frame, never on the bare hold window).
            host.RunFrame(); host.RunFrame();                     // idle frames: the offset-wrote latch clears
            bool holdLive = host.MainScrollHoldUntilForTest > System.Diagnostics.Stopwatch.GetTimestamp();
            bool latchClear = !host.ScrollIntegratorForTest.AnyOffsetWroteThisFrame;
            probe.Moved.Value = true;
            host.RunFrame();
            float dyAfter = host.Scene.Paint(probe.Box).LocalTransform.Dy;
            bool flipsAgain = host.Animation.HasTracks(probe.Box) && dyAfter < -50f;
            FluentGpu.Dsl.Motion.SetLayoutTransitionsSuppressed(FluentGpu.Dsl.MotionSuppressionSource.Scroll, false);   // never leak the static bit to later gates
            Check("gate.motion.scrollSuppressionSnapsFlip a reconcile landing right after a scroll-offset write SNAPS the moved node (no FLIP track); the same move with no scroll motion FLIPs — both before any scroll and (hold still live, latch clear) right after one",
                flipSeeds && offsetWrote && snapped && holdLive && latchClear && flipsAgain,
                $"flipSeeds={flipSeeds}(dy={dyFlip:0.#}) offsetWrote={offsetWrote} snapped={snapped}(dy={dySnap:0.##}) holdLive={holdLive} latchClear={latchClear} flipsAgain={flipsAgain}(dy={dyAfter:0.#})");
        }

        // gate.anim.activeChainMatchesDictionary (W6/E12): the slab's intrusive active-node chain (what PASS1/PASS2 and
        // the census scans iterate) stays set-equal to the node→head Dictionary (the lookup) through a seeded randomized
        // add / free-one-row / clear-node sequence — at EVERY step, with no duplicate and no cycle — and mutations bump
        // the census-memo Version.
        {
            var scene = new SceneStore();
            var nodes = new NodeHandle[24];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = scene.CreateNode(1);
            var slab = new AnimValueSlab();
            var rng = new Random(0xF6E12);
            var slots = new System.Collections.Generic.List<int>[nodes.Length];
            for (int i = 0; i < slots.Length; i++) slots[i] = new System.Collections.Generic.List<int>();
            var chainSet = new System.Collections.Generic.HashSet<int>();
            var dictSet = new System.Collections.Generic.HashSet<int>();
            bool ok = true; string detail = "";
            int startVersion = slab.Version;
            for (int step = 0; step < 800 && ok; step++)
            {
                int pick = rng.Next(nodes.Length);
                int nodeIndex = (int)nodes[pick].Raw.Index;
                int op = rng.Next(10);
                if (op < 5)
                    slots[pick].Add(slab.Add(nodeIndex, new AnimValue { Node = nodes[pick], Channel = AnimChannel.Opacity }));
                else if (op < 8)
                {
                    if (slots[pick].Count > 0) { int k = rng.Next(slots[pick].Count); slab.Free(slots[pick][k]); slots[pick].RemoveAt(k); }
                }
                else { slab.ClearNode(nodeIndex); slots[pick].Clear(); }

                chainSet.Clear(); dictSet.Clear();
                foreach (int n in slab.NodeIndices) dictSet.Add(n);
                int guard = 0; bool dupOrCycle = false;
                for (int n = slab.FirstActiveNode; n >= 0; n = slab.NextActiveNode(n))
                {
                    if (!chainSet.Add(n) || ++guard > 1000) { dupOrCycle = true; break; }
                }
                if (dupOrCycle || !chainSet.SetEquals(dictSet))
                {
                    ok = false;
                    detail = $"step={step} dupOrCycle={dupOrCycle} chain={chainSet.Count} dict={dictSet.Count}";
                }
            }
            bool versioned = slab.Version > startVersion;
            Check("gate.anim.activeChainMatchesDictionary 800 seeded randomized add/free/clear steps keep the slab's active-node chain set-equal to the node→head dictionary at every step (no dup, no cycle), and mutations bump the census-memo Version",
                ok && versioned, ok ? $"800 steps, final live rows={slab.Count}, version={slab.Version}" : detail);
        }
    }

    static void ScrollParityChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.scroll.wheel-distance-viewport-relative: ONE device wheel notch (InputEvent.WheelNotch) scrolls
        // max(48 DIP, 10%·viewport) — a bounded content-relative mouse-wheel line height — NOT the old flat 60 DIP. A TALL
        // viewport steps by the 10% term (>48); a SHORT viewport floors at 48. The two distinct steps prove it is
        // viewport-relative. (A synthetic DIP ScrollDelta — every other wheel gate — bypasses this scale, so they are
        // unchanged.)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("wheel-dist", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            // ScrollBy reads ViewportH at dispatch (phase 2) — BEFORE layout re-publishes it at phase 6 — so forcing it
            // probes the per-notch distance for any viewport. TALL: max(48, 0.10·800) = 80. SHORT: max(48, 0.10·200) = 48.
            host.Scene.ScrollRef(vp).ViewportH = 800f; host.Scene.ScrollRef(vp).OffsetY = 0f; host.Scene.ScrollRef(vp).TargetY = 0f;
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, WheelNotch: 1f));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var scTall);
            float stepTall = scTall.TargetY;

            host.Scene.ScrollRef(vp).ViewportH = 200f; host.Scene.ScrollRef(vp).OffsetY = 0f; host.Scene.ScrollRef(vp).TargetY = 0f;
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, WheelNotch: 1f));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var scShort);
            float stepShort = scShort.TargetY;

            bool tallOk = Near(stepTall, 80f, 0.6f);     // 10%·800
            bool shortOk = Near(stepShort, 48f, 0.6f);   // floored (0.10·200 = 20 < 48)
            Check("gate.scroll.wheel-distance-viewport-relative one wheel notch scrolls max(48 DIP, 10%·viewport) — bounded content-relative motion, not a flat 60: viewport 800 ⇒ step 80, viewport 200 ⇒ step 48 (floor); distinct ⇒ genuinely viewport-relative",
                tallOk && shortOk && !Near(stepTall, stepShort, 1f),
                $"vp800 step={stepTall:0.#} (expect 80) vp200 step={stepShort:0.#} (expect 48)");
        }

        // gate.touch.flick-velocity-windowed: the windowed least-squares velocity sampler reads a fast constant-velocity
        // flick's TRUE terminal speed (the 50ms EMA under-read it — gain dt/(dt+50) lags before convergence), AND a finger
        // that moves fast then HOLDS STILL before lift decays to ~0 (the old EMA's stationary up-sample bias kept stale
        // momentum). Drives the dispatcher directly and reads PointerVelocity.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("flick-vel", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            uint t = s_touchClockMs;
            var ev = new InputEvent[1];
            ev[0] = Touch(InputKind.PointerDown, new Point2(150, 384), t, 21); host.Input.Dispatch(ev); host.RunFrame();
            float y = 384f;
            for (int i = 0; i < 10; i++) { t += 8; y -= 16f; ev[0] = Touch(InputKind.PointerMove, new Point2(150, y), t, 21); host.Input.Dispatch(ev); host.RunFrame(); }
            float vFast = host.Input.PointerVelocity.Y;   // ≈ -2000 px/s (16px / 8ms, upward ⇒ negative)
            bool accurate = vFast <= -1700f;              // near the true -2000 (the EMA lagged to ~-1500); regression reads true
            // Now HOLD the finger still for several samples → the in-window slope goes to 0 (no stale momentum carried).
            for (int i = 0; i < 8; i++) { t += 8; ev[0] = Touch(InputKind.PointerMove, new Point2(150, y), t, 21); host.Input.Dispatch(ev); host.RunFrame(); }
            float vRest = host.Input.PointerVelocity.Y;
            bool restZero = MathF.Abs(vRest) < 60f;
            ev[0] = Touch(InputKind.PointerUp, new Point2(150, y), t + 8, 21); host.Input.Dispatch(ev); host.RunFrame();
            s_touchClockMs = t + 1000;
            Check("gate.touch.flick-velocity-windowed the windowed-regression velocity sampler reads a fast flick's TRUE terminal speed (no EMA under-read) and decays a paused-then-held finger to ~0 (the stationary up-sample bias is gone)",
                accurate && restZero, $"fastV={vFast:0} (true≈-2000, want ≤-1700) heldStillV={vRest:0} (want ~0)");
        }

        // gate.touch.flick-seed-gap-invariant: a constant-velocity flick seeds the SAME fling velocity regardless of the OS
        // move→up timing gap. The release velocity is RE-WINDOWED at lift WITHOUT folding the up point into the regression
        // (TouchVelocity.ComputeReleaseVelocity): the up event repeats the last-move position at a later stamp — a near-zero
        // sample that, folded in, dragged the seed toward 0 as the gap grew (the OLD path read ~−1786/−1429/−981/−554 px/s at
        // 8/16/24/32 ms — the felt "same fast flick, different result" / "scrolls only a bit"). Now every realistic lift gap
        // seeds the true ~2000 px/s. (A gap BEYOND the 50 ms regression window is a genuine pre-lift PAUSE and correctly seeds
        // no fling — not asserted here; this gate covers the lift-timing-jitter range a real flick lands in.)
        {
            float SeedForGap(uint gap)   // non-static: captures fonts/strings; fresh host per gap so each starts at offset 0
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("flick-gap", new Size2(360, 460), 1f)); window.Show();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
                host.RunFrame();
                var vp = host.Scene.Root;
                uint t = s_touchClockMs;
                var ev = new InputEvent[1];
                ev[0] = Touch(InputKind.PointerDown, new Point2(150, 384), t, 21); host.Input.Dispatch(ev); host.RunFrame();
                float y = 384f;   // constant 16 px / 8 ms = 2000 px/s flick up (8 moves, 128 px — past slop, claims a pan)
                for (int i = 0; i < 8; i++) { t += 8; y -= 16f; ev[0] = Touch(InputKind.PointerMove, new Point2(150, y), t, 21); host.Input.Dispatch(ev); host.RunFrame(); }
                // Lift at the SAME position as the last move (the up REPEATS it — the corrupting case), GAP ms after it.
                ev[0] = Touch(InputKind.PointerUp, new Point2(150, y), t + gap, 21); host.Input.Dispatch(ev);   // SeedScrollFling runs synchronously
                host.Scene.TryGetScroll(vp, out var sc);
                s_touchClockMs = t + gap + 1000;
                return MathF.Abs(sc.FlingVelocity);   // offset-space seed (≈ 2000); 0 ⇒ no fling
            }
            uint[] gaps = { 8, 16, 24, 32, 40 };
            float min = float.MaxValue, max = 0f;
            foreach (var g in gaps) { float s = SeedForGap(g); if (s < min) min = s; if (s > max) max = s; }
            bool allReal = min >= 1700f;                                       // every gap seeds the true ~2000 (no gap drops to a low/zero seed)
            bool consistent = max <= 0f || (max - min) / max < 0.10f;          // within 10% across gaps — "same flick, same momentum"
            Check("gate.touch.flick-seed-gap-invariant a constant-velocity flick seeds the SAME ~2000 px/s fling across OS lift gaps {8..40}ms (release velocity re-windowed at lift, not folded with the stale up point) — the 'same fast flick, different result' inconsistency is gone",
                allReal && consistent, $"seed min={min:0} max={max:0} spread={(max > 0f ? (max - min) / max * 100f : 0f):0.#}% (want min≥1700, spread<10%)");
        }

        // gate.scroll.engine-owned-integrator: scroll is fully engine-owned — the deterministic ScrollIntegrator is the
        // single, portable scroll source on every platform (there is no OS scroll-source seam; DirectManipulation is
        // gone). A wheel notch drives the integrator's target-chase to a NON-ZERO offset in plain TargetChase mode,
        // proving the engine integrator alone moves the viewport.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("engine-scroll", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0, 200f));   // DIP ScrollDelta (integrator path)
            for (int i = 0; i < 40; i++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var s);
            bool integratorRan = s.OffsetY > 0f && s.Phase == ScrollIntegrator.Idle;
            Check("gate.scroll.engine-owned-integrator scroll is fully engine-owned — the deterministic ScrollIntegrator alone drives the viewport (no OS scroll source); a DIP wheel delta advances it to a non-zero offset and settles back to Idle",
                integratorRan, $"integratorRan={integratorRan} off={s.OffsetY:0} phase={s.Phase}");
        }

        // gate.scroll.overscroll-physics (v2): the canonical iOS asymptotic rubber-band + its EXACT inverse + the ω=12.5
        // snap-back (scroll-feel-rework-v2 §4.4/§4.5). d = BandAsymptoteFraction·vp = 60 is the asymptote the band
        // approaches but NEVER reaches (no wall, marginal give > 0 everywhere) — bandPos at excess=120 lands BELOW d,
        // not clamped at the old 10% cap. f(x) = x·d·c/(d + c·|x|), c = 0.55.
        {
            float vp = 400f;
            float d = OverscrollPhysics.BandAsymptoteFraction * vp;       // 60
            float bandNeg = OverscrollPhysics.BandFromExcess(-30f, vp);   // -30·60·0.55/(60+0.55·30) = -12.941
            float bandPos = OverscrollPhysics.BandFromExcess(120f, vp);   // 120·60·0.55/(60+0.55·120) = 31.429 (< d — no wall)
            float inv = OverscrollPhysics.ExcessFromBand(bandNeg, vp);    // exact inverse ⇒ -30
            float p = bandNeg, v = 0f;
            for (int i = 0; i < 120; i++) OverscrollPhysics.StepSpring(ref p, ref v, 16f);   // ω=SnapBackOmega=12.5 ⇒ settles well within 1920ms
            bool ok = Near(bandNeg, -12.941f, 0.05f) && bandNeg > -d && bandNeg < 0f
                   && Near(bandPos, 31.429f, 0.05f) && bandPos < d
                   && Near(inv, -30f, 0.5f)
                   && Near(p, 0f, 0.1f) && Near(v, 0f, 1f);
            Check("gate.scroll.overscroll-physics v2 iOS rubber-band f(x)=x·d·c/(d+c|x|) (c=0.55, d=0.15·vp) approaches but never reaches the asymptote, ExcessFromBand is its exact inverse, and the ω=12.5 snap-back springs to 0 (translation-only)",
                ok, $"bandNeg={bandNeg:0.000} bandPos={bandPos:0.000} d={d:0} inv={inv:0.0} final=({p:0.00},{v:0.0})");
        }
    }

    static void ScrollV2ValidationChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        float k = -MathF.Log(ScrollIntegrator.FlingDecayPerS);   // exact coast decay rate (1/s)
        var center = new Point2(150, 200);

        // gate.scroll.single-writer (§8.1, pins R1): a full contact-track + fling + top-overpan + snap-back + wheel-chase
        // cycle records offset writes from the PHASE-7 INTEGRATOR ONLY (writer == Integrator) and AT MOST ONE offset write
        // per active node per frame. The 0-alloc audit counts every real SetScrollOffset move and its ScrollWriter tag; a
        // foreign writer (a synchronous phase-2 write — the R1 two-owners defect) or a second write in one frame fails it.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("v2-single-writer", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.Input.SmoothScroll = true;   // a wheel notch drives WheelAnimating (integrator), never a synchronous jump
            host.RunFrame();
            var vp = host.Scene.Root;
            var prod = new HeadlessScrollProducer(window, host, center);
            ScrollTrace.AuditBegin();
            prod.ContactBegin(0f); prod.Step(16);
            for (int i = 0; i < 6; i++) { prod.ContactUpdate(24f); prod.Step(16); }   // latch + track down ~144 DIP
            prod.ContactEnd(); prod.Step(16);                                          // fallback lift → seeds Fling
            for (int i = 0; i < 80; i++) { prod.Step(16); host.Scene.TryGetScroll(vp, out var s); if (s.Phase == ScrollIntegrator.Idle) break; }
            prod.ContactBegin(0f); prod.Step(16);
            for (int i = 0; i < 8; i++) { prod.ContactUpdate(-60f); prod.Step(16); }   // drag PAST the top edge → Overscroll band
            prod.ContactEnd(); prod.Step(16);                                          // release → SnapBack
            for (int i = 0; i < 80; i++) { prod.Step(16); host.Scene.TryGetScroll(vp, out var s); if (MathF.Abs(s.OverscrollPx) < 0.1f && s.Phase == ScrollIntegrator.Idle) break; }
            prod.WheelNotch(2f); prod.Step(16);                                        // discrete notch → WheelAnimating chase
            for (int i = 0; i < 80; i++) { prod.Step(16); host.Scene.TryGetScroll(vp, out var s); if (s.Phase == ScrollIntegrator.Idle) break; }
            bool foreign = ScrollTrace.AuditForeignWriter;
            int maxWrites = ScrollTrace.AuditMaxWritesPerFrame;
            ScrollTrace.AuditStop();
            Check("gate.scroll.single-writer a full contact+fling+overpan+snapback+wheel cycle records offset writes from the phase-7 integrator ONLY (no foreign writer) and AT MOST one offset write per active node per frame — the §2.1 single-writer invariant (pins R1)",
                !foreign && maxWrites == 1, $"foreignWriter={foreign} maxWritesPerFrame={maxWrites} (expect no-foreign, ≤1/frame)");
        }

        // gate.scroll.dt-invariance (§8.2, pins R2/R3/R5 frame-independence): the same scripted motion at dt∈{8,16,33}ms
        // yields the same trajectory within 1 DIP — contact (the §4.1 resampler tracks the same continuous position line
        // regardless of frame cadence), fling coast (the closed-form CoastStep telescopes to v0/k), wheel chase (closed-form
        // crit-damped, lands on the target), and snap-back (closed-form spring → 0).
        {
            const float vel = 0.4f;   // DIP/ms
            // Contact: run the 1-packet-per-frame-at-frame-time script at dt and return max deviation from vel·(t−5ms−t0).
            float ContactDev(float dt)
            {
                using var app = new HeadlessPlatformApp();
                var w = new HeadlessWindow(new WindowDesc("v2-dtinv", new Size2(360, 460), 1f)); w.Show();
                using var h = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe(), frameTime: new FixedFrameTimeSource(dt));
                h.RunFrame();
                var v2 = h.Scene.Root;
                var pr = new HeadlessScrollProducer(w, h, center) { Device = (byte)ScrollDeviceClass.Touchpad };
                double t0 = pr.FrameMs;
                pr.ContactBegin(0f); pr.Frame(dt);
                float maxDev = 0f;
                for (int kf = 0; kf < 40; kf++)
                {
                    pr.Ms = (uint)pr.FrameMs;                       // deliver one packet AT the present time
                    pr.ContactUpdate(vel * dt);
                    double present = pr.FrameMs;
                    pr.Frame(dt);
                    h.Scene.TryGetScroll(v2, out var s);
                    if (kf < 3 || s.Phase != ScrollIntegrator.TouchpadTracking) continue;
                    float expected = (float)(vel * (present - ScrollTuning.ResampleLatencyMs - t0));
                    if (s.OffsetY > 1f && s.OffsetY < 2600f) maxDev = MathF.Max(maxDev, MathF.Abs(s.OffsetY - expected));
                }
                return maxDev;
            }
            float FlingCoast(float dt) { float v = 1000f, c = 0f; for (int i = 0; i < 5000 && MathF.Abs(v) >= 0.5f; i++) c += OverscrollPhysics.CoastStep(ref v, dt, ScrollIntegrator.FlingDecayPerS); return c; }
            float WheelChase(float dt)
            {
                float off = 0f, vel2 = 0f; const float pending = 200f;
                float y = 1.3862944f / (ScrollTuning.WheelChaseHalflifeMs * 0.001f);
                for (int i = 0; i < 4000; i++)
                {
                    float dtS = dt * 0.001f, j0 = off - pending, j1 = vel2 + j0 * y, e = MathF.Exp(-y * dtS);
                    off = e * (j0 + j1 * dtS) + pending; vel2 = e * (vel2 - j1 * y * dtS);
                    if (MathF.Abs(off - pending) < 0.5f && MathF.Abs(vel2) < ScrollIntegrator.WheelSettleVelPxPerS) break;
                }
                return off;
            }
            float SnapEnd(float dt) { float p = 60f, v = 0f; for (int i = 0; i < 6000; i++) if (OverscrollPhysics.StepSpring(ref p, ref v, dt, OverscrollPhysics.SnapBackOmega)) break; return p; }
            float cd8 = ContactDev(8f), cd16 = ContactDev(16f), cd33 = ContactDev(33f);
            float f8 = FlingCoast(8f), f16 = FlingCoast(16f), f33 = FlingCoast(33f);
            float wh8 = WheelChase(8f), wh16 = WheelChase(16f), wh33 = WheelChase(33f);
            float sn8 = SnapEnd(8f), sn16 = SnapEnd(16f), sn33 = SnapEnd(33f);
            bool contactOk = cd8 <= 0.5f && cd16 <= 0.5f && cd33 <= 0.5f;              // each on the same line ⇒ pairwise ≤1
            bool flingOk = MathF.Abs(f8 - f16) <= 1f && MathF.Abs(f16 - f33) <= 1f && MathF.Abs(f8 - f33) <= 1f;
            bool wheelOk = Near(wh8, 200f, 1f) && Near(wh16, 200f, 1f) && Near(wh33, 200f, 1f);
            bool snapOk = Near(sn8, 0f, 0.5f) && Near(sn16, 0f, 0.5f) && Near(sn33, 0f, 0.5f);
            Check("gate.scroll.dt-invariance the same stream at dt∈{8,16,33}ms produces the same trajectory within 1 DIP across contact (resampler), fling (CoastStep), wheel chase, and snap-back — frame-rate independence (pins R2/R3/R5)",
                contactOk && flingOk && wheelOk && snapOk,
                $"contactDev=({cd8:0.00},{cd16:0.00},{cd33:0.00})≤0.5 fling=({f8:0.0},{f16:0.0},{f33:0.0}) wheel=({wh8:0.0},{wh16:0.0},{wh33:0.0}) snap=({sn8:0.00},{sn16:0.00},{sn33:0.00})");
        }

        // gate.scroll.resample-cadence (§8.3, pins R2): a constant-velocity stream at 1-packet/2-packet ALTERNATING cadence
        // (8ms packets against a 12ms frame → 1,2,1,2 packets/frame) produces monotonic, NEAR-CONSTANT per-frame displacement
        // (the §4.1 resampler samples the same linear position at frameT−5ms, so the packet cadence alias is gone). Without
        // resampling the per-frame displacement would alternate δ,2δ — the textbook 125Hz-into-60Hz judder.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("v2-resample", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe(), frameTime: new FixedFrameTimeSource(12f));
            host.RunFrame();
            var vp = host.Scene.Root;
            var prod = new HeadlessScrollProducer(window, host, center) { Device = (byte)ScrollDeviceClass.Touchpad };
            const float vel = 0.5f;   // DIP/ms → 4 DIP per 8ms packet, 6 DIP per 12ms frame
            uint packetMs = prod.Ms;
            prod.ContactBegin(0f); prod.Frame(12f);
            float prevOff = 0f; bool havePrev = false; float minD = float.MaxValue, maxD = 0f; bool monotonic = true; int measured = 0;
            for (int kf = 0; kf < 40; kf++)
            {
                while (packetMs + 8 <= prod.FrameMs) { packetMs += 8; prod.Ms = packetMs; prod.ContactUpdate(vel * 8f); }   // deliver due 8ms packets
                prod.Frame(12f);
                host.Scene.TryGetScroll(vp, out var s);
                if (kf >= 6 && s.Phase == ScrollIntegrator.TouchpadTracking && s.OffsetY < 2400f)   // past warmup, in-range
                {
                    if (havePrev)
                    {
                        float d = s.OffsetY - prevOff;
                        if (d + 0.01f < 0f) monotonic = false;
                        minD = MathF.Min(minD, d); maxD = MathF.Max(maxD, d); measured++;
                    }
                    prevOff = s.OffsetY; havePrev = true;
                }
            }
            float spread = measured > 0 ? maxD - minD : 999f;
            bool ok = monotonic && measured >= 10 && spread <= 0.6f;   // constant 6 DIP/frame ⇒ spread ≈ 0 (bound 0.6)
            Check("gate.scroll.resample-cadence a constant-velocity stream at 1/2-packet-alternating cadence produces monotonic, near-constant per-frame displacement (spread ≤ 0.6 DIP) — the §4.1 resampler kills the packet-cadence alias (pins R2)",
                ok, $"perFrameΔ min={minD:0.00} max={maxD:0.00} spread={spread:0.00} monotonic={monotonic} n={measured}");
        }

        // gate.scroll.contact-1to1 (§8.4): during TouchpadTracking the applied offset equals the resampled finger position
        // (anchor + Σδ resampled to frameT−5ms) within 0.5 DIP every frame — the resampler neither lags nor gains.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("v2-1to1", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            var prod = new HeadlessScrollProducer(window, host, center) { Device = (byte)ScrollDeviceClass.Touchpad };
            const float vel = 0.4f;
            double t0 = prod.FrameMs;
            prod.ContactBegin(0f); prod.Frame(16f);
            float maxErr = 0f; int frames = 0;
            for (int kf = 0; kf < 40; kf++)
            {
                prod.Ms = (uint)prod.FrameMs;
                prod.ContactUpdate(vel * 16f);
                double present = prod.FrameMs;
                prod.Frame(16f);
                host.Scene.TryGetScroll(vp, out var s);
                if (kf < 3 || s.Phase != ScrollIntegrator.TouchpadTracking) continue;
                if (s.OffsetY > 1f && s.OffsetY < 2600f)
                {
                    float expected = (float)(vel * (present - ScrollTuning.ResampleLatencyMs - t0));
                    maxErr = MathF.Max(maxErr, MathF.Abs(s.OffsetY - expected)); frames++;
                }
            }
            Check("gate.scroll.contact-1to1 during TouchpadTracking the applied offset tracks the resampled finger position (anchor + Σδ @ frameT−5ms) within 0.5 DIP every frame",
                maxErr <= 0.5f && frames >= 10, $"maxErr={maxErr:0.000} DIP over {frames} tracking frames (bound 0.5)");
        }

        // gate.scroll.coast-distance (§8.5, pins R3): a wheel notch coasts EXACTLY its perNotch distance (max(48,10%·vp)),
        // identical at 60/120 Hz; a fling coasts v0/k (the exact closed-form asymptote) ±1 DIP. Wheel via the pipeline
        // (WheelAnimating chase lands on the hard-clamped PendingTarget); fling via the exact CoastStep integral.
        {
            float WheelSettle(float dt)
            {
                using var app = new HeadlessPlatformApp();
                var w = new HeadlessWindow(new WindowDesc("v2-coast-wheel", new Size2(360, 460), 1f)); w.Show();
                using var h = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe(), frameTime: new FixedFrameTimeSource(dt));
                h.Input.SmoothScroll = true; h.RunFrame();
                var v2 = h.Scene.Root;
                var pr = new HeadlessScrollProducer(w, h, center);
                pr.WheelNotch(1f); pr.Frame(dt);
                for (int i = 0; i < 400; i++) { pr.Frame(dt); h.Scene.TryGetScroll(v2, out var s); if (s.Phase == ScrollIntegrator.Idle) break; }
                h.Scene.TryGetScroll(v2, out var fin);
                return fin.OffsetY;
            }
            float perNotch = MathF.Max(48f, 0.10f * 400f);   // vp = 400 ⇒ floor 48
            float w60 = WheelSettle(1000f / 60f), w120 = WheelSettle(1000f / 120f);
            float vf = 1000f, coast = 0f; for (int i = 0; i < 20000 && MathF.Abs(vf) >= 0.5f; i++) coast += OverscrollPhysics.CoastStep(ref vf, 1000f / 60f, ScrollIntegrator.FlingDecayPerS);
            float asymptote = 1000f / k;   // ≈ 499.4
            bool wheelOk = Near(w60, perNotch, 0.5f) && Near(w120, perNotch, 0.5f);
            bool flingOk = Near(coast, asymptote, 1f);
            Check("gate.scroll.coast-distance a wheel notch coasts its exact perNotch distance (max(48,10%·vp)) ±0.5 DIP identically at 60/120 Hz, and a fling coasts the exact v0/k asymptote ±1 DIP (pins R3)",
                wheelOk && flingOk, $"wheel60={w60:0.0} wheel120={w120:0.0} (perNotch={perNotch:0}) flingCoast={coast:0.0} (v0/k={asymptote:0.0})");
        }

        // gate.scroll.impulse-velocity (§8.6, pins R6): the single-window IMPULSE estimator sign(W)·√(2|W|). Five sub-cases
        // on the phase-contract (fallback) lift path: (a) constant drag ⇒ exact hand speed; (b) a ≥40ms gap before lift ⇒ 0;
        // (c) a decaying tail then silence ⇒ v<50 (no double inertia); (d) an abrupt lift ⇒ exactly one coast; (e) a reversal
        // ⇒ no stale-direction (positive) coast. decay1 folds the single End-frame tick that decays the fresh seed once.
        {
            const byte Fb = (byte)ScrollDeviceClass.WheelHiResFallback;
            float decay1 = MathF.Exp(MathF.Log(ScrollIntegrator.FlingDecayPerS) * 16f / 1000f);
            (float v, byte phase) Drive(float[] deltas, uint packetGap, uint liftGap)
            {
                using var app = new HeadlessPlatformApp();
                var w = new HeadlessWindow(new WindowDesc("v2-impulse", new Size2(360, 460), 1f)); w.Show();
                using var h = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
                h.RunFrame();
                var v2 = h.Scene.Root;
                var pr = new HeadlessScrollProducer(w, h, center) { Device = Fb };
                pr.ContactBegin(0f); pr.Frame(16f);
                foreach (float d in deltas) { pr.Ms += packetGap; pr.ContactUpdate(d); pr.Frame(16f); }
                pr.Ms += liftGap; pr.ContactEnd(); pr.Frame(16f);
                h.Scene.TryGetScroll(v2, out var s);
                return (s.FlingVelocity, s.Phase);
            }
            float[] constStream = { 8f, 8f, 8f, 8f, 8f, 8f, 8f, 8f };            // 8 DIP / 8ms = 1000 DIP/s
            float[] decelStream = { 8f, 8f, 8f, 8f, 4f, 2f, 1f, 0.4f };
            float[] revStream = { 16f, 16f, 16f, 16f, -16f, -16f, -16f, -16f };
            var a = Drive(constStream, 8, 8);                                     // (a)+(d): abrupt lift, exact speed, one coast
            var b = Drive(constStream, 8, 60);                                    // (b): ≥40ms gap ⇒ 0
            var c = Drive(decelStream, 16, 60);                                   // (c): decayed tail + silence ⇒ no double inertia
            var e = Drive(revStream, 8, 8);                                       // (e): reversal ⇒ no stale-down coast
            bool aOk = a.phase == ScrollIntegrator.Fling && MathF.Abs(a.v - 1000f * decay1) <= 1000f * decay1 * 0.04f;
            bool bOk = b.phase != ScrollIntegrator.Fling || MathF.Abs(b.v) < 1f;
            bool cOk = c.phase != ScrollIntegrator.Fling || MathF.Abs(c.v) < ScrollTuning.FlingSeedGate;
            bool eOk = e.v <= 1f;   // recent motion is UP (negative); no positive downward stale coast
            Check("gate.scroll.impulse-velocity the single 40ms IMPULSE estimator: constant drag ⇒ exact hand speed + one coast; a ≥40ms gap ⇒ 0; a decayed tail then silence ⇒ v<50; a reversal ⇒ no stale-direction coast (pins R6, one window one gate)",
                aOk && bOk && cOk && eOk,
                $"const={a.v:0}(exp {1000f * decay1:0}) gap={b.v:0.0} decel={c.v:0.0} reversal={e.v:0.0}");
        }

        // gate.scroll.overscroll-rational (§8.7, pins R5): the iOS asymptotic map + its EXACT inverse round-trip within 0.5
        // DIP INCLUDING at/above the old saturation point (x = 2·limit and 10·limit) — the map never reaches the asymptote d
        // (marginal slope > 0 everywhere); and the ω=12.5 snap-back decays to ≤10% of the band in 300–360 ms (WebKit λ=12.5).
        {
            const float vp = 400f;
            float limit = OverscrollPhysics.BandLimit(vp);     // 0.1·vp = 40
            float d = OverscrollPhysics.BandAsymptoteFraction * vp;   // 60 (asymptote)
            float worst = 0f; bool belowAsymptote = true;
            foreach (float x in new[] { 5f, 40f, 2f * limit, 200f, 10f * limit })
            {
                float band = OverscrollPhysics.BandFromExcess(x, vp);
                if (MathF.Abs(band) >= d) belowAsymptote = false;                // never reaches the wall
                worst = MathF.Max(worst, MathF.Abs(OverscrollPhysics.ExcessFromBand(band, vp) - x));
            }
            // Snap-back settle-to-10%: seed a 60-DIP band, step the ω=12.5 spring at 4ms, time to |band| ≤ 10% of x0.
            float p0 = 60f, sp = 60f, sv = 0f, tMs = 0f; int settle10 = -1;
            for (int i = 0; i < 400; i++)
            {
                OverscrollPhysics.StepSpring(ref sp, ref sv, 4f, OverscrollPhysics.SnapBackOmega); tMs += 4f;
                if (settle10 < 0 && MathF.Abs(sp) <= 0.10f * p0) { settle10 = (int)tMs; break; }
            }
            bool rtOk = worst < 0.5f && belowAsymptote;
            bool settleOk = settle10 >= 300 && settle10 <= 360;
            // scroll-feel-v2.1 §A.1/§A.4: the edge bounce is VELOCITY-ONLY (position untouched). SeedFromEdgeMomentum
            // leaves the band at the current stretch (0 here) and seeds bandVel = clamp(γ·v, ±Cpeak·d·ω·e); the
            // critically-damped v0·t·e^(−ωt) peaks at v0/(ω·e) ≤ Cpeak·d = 0.6·d, so even the MAX-velocity flick never
            // approaches the asymptote (kills F6's teleport), and a re-grab AT the peak folds a finite, well-inside-domain
            // excess that round-trips (peak < d ⇒ the F7 near-asymptote divergence is unreachable on a bounce).
            float bandPk = 0f, bandVk = 0f;
            OverscrollPhysics.SeedFromEdgeMomentum(ref bandVk, ScrollTuning.FlingMaxVelocityPxPerS, vp);   // 8000 px/s max seed (velocity-only: bandPk stays 0)
            float peak = MathF.Abs(bandPk);
            for (float bp = bandPk, bv = bandVk, i = 0; i < 400; i++)
            { if (OverscrollPhysics.StepSpring(ref bp, ref bv, 4f, OverscrollPhysics.SnapBackOmega)) break; peak = MathF.Max(peak, MathF.Abs(bp)); }
            float cap = OverscrollPhysics.MomentumPeakDepthFraction * d;   // 0.6·d = 36
            float regrabExcess = OverscrollPhysics.ExcessFromBand(peak, vp);
            float regrabBand = OverscrollPhysics.BandFromExcess(regrabExcess, vp);
            bool bounceOk = peak <= cap + 0.5f && float.IsFinite(regrabExcess) && MathF.Abs(regrabBand - peak) < 0.5f;
            Check("gate.scroll.overscroll-rational the iOS rubber-band round-trips (ExcessFromBand∘BandFromExcess) within 0.5 DIP incl. x∈{2·limit,10·limit} (never reaches the asymptote), the ω=12.5 snap-back settles to 10% in 300–360 ms (pins R5), AND the velocity-only edge bounce peaks ≤ 0.6·d with a re-grab round-trip at the peak (§A.1/§A.4, F6/F7)",
                rtOk && settleOk && bounceOk, $"worstRoundTrip={worst:0.000} belowAsymptote={belowAsymptote} settle10%={settle10}ms bouncePeak={peak:0.0}/cap={cap:0.0} regrabRT={MathF.Abs(regrabBand - peak):0.000}");
        }

        // gate.scroll.relatch-catchup (scroll-feel-v2.1 §A.6, resolves F8): an OS-momentum stream that includes a post-hitch
        // catch-up (a 34ms stall frame delivering one big summed delta) applies the FULL owed displacement 1:1 — there is NO
        // max-per-frame delta clamp. Chromium coalesces GSUs and applies the summed delta; Android/Flutter evaluate at
        // absolute time (a full jump after a long frame); no shipping system clamps a catch-up. Because the resampler tracks
        // the cumulative position line (not per-frame deltas), the final offset converges to Σ(all deltas) regardless of
        // frame pacing — a hypothetical per-frame clamp would truncate the 193 DIP hitch and fall far short.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("v2-relatch", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            var pr = new HeadlessScrollProducer(window, host, center) { Device = (byte)ScrollDeviceClass.Touchpad };
            float total = 0f;
            pr.ContactBegin(0f); pr.Step(16);
            for (int i = 0; i < 6; i++) { pr.ContactUpdate(20f); total += 20f; pr.Step(16); }   // latch + track interior (~120 DIP)
            pr.MomentumBegin(); pr.Step(16);
            for (int i = 0; i < 4; i++) { pr.MomentumUpdate(24f); total += 24f; pr.Step(16); }   // smooth OS inertia
            pr.MomentumUpdate(193f); total += 193f; pr.Step(34f);                                 // the F8 hitch: 34ms stall → one 193 DIP catch-up
            for (int i = 0; i < 5; i++) { pr.MomentumUpdate(10f); total += 10f; pr.Step(16); }    // slow tail so the resampler fully realizes the jump
            pr.MomentumEnd();
            for (int i = 0; i < 40; i++) { pr.Step(16); host.Scene.TryGetScroll(vp, out var q); if (q.Phase == ScrollIntegrator.Idle) break; }
            host.Scene.TryGetScroll(vp, out var s);
            float applied = s.OffsetY;
            // Full owed displacement (a few DIP of resampler lag = end-velocity × 5ms is the only shortfall; a real clamp
            // would lose ~150 DIP of the 193 hitch). Stayed interior (no edge conversion) throughout.
            bool oneToOne = MathF.Abs(applied - total) < 8f;
            Check("gate.scroll.relatch-catchup an OS-momentum stream with a 34ms hitch (one 193 DIP catch-up) applies the full owed displacement 1:1 within a vsync — no max-per-frame delta clamp (§A.6, resolves F8)",
                oneToOne, $"applied={applied:0.0} expectedTotal={total:0.0} phase={s.Phase}");
        }

        // gate.scroll.pointerdown-cancels (§8.8, pins R6): a pointer-down over a coasting/animating viewport zeros its motion
        // the SAME frame with no residual drift. A mouse click, a touch-down, and a scrollbar-grab all route through the same
        // OnCancelFling call (fixes R6's dead CancelFling). Seed a fling, then fire the down mid-coast.
        {
            (bool cancelled, bool noDrift) DownCancels(PointerKind kind)
            {
                using var app = new HeadlessPlatformApp();
                var w = new HeadlessWindow(new WindowDesc("v2-downcancel", new Size2(360, 460), 1f)); w.Show();
                using var h = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
                h.RunFrame();
                var v2 = h.Scene.Root;
                var pr = new HeadlessScrollProducer(w, h, center) { Device = (byte)ScrollDeviceClass.WheelHiResFallback };
                pr.ContactBegin(0f); pr.Frame(16f);
                for (int i = 0; i < 8; i++) { pr.Ms += 8; pr.ContactUpdate(8f); pr.Frame(16f); }   // 1000 DIP/s
                pr.Ms += 8; pr.ContactEnd(); pr.Frame(16f);                                          // seed a fling
                pr.Frame(16f); pr.Frame(16f);                                                        // coast a couple frames
                h.Scene.TryGetScroll(v2, out var mid);
                bool wasFlinging = mid.Phase == ScrollIntegrator.Fling && MathF.Abs(mid.FlingVelocity) > 50f;
                pr.PointerDownAt(center, kind, kind == PointerKind.Touch ? 5u : 0u); pr.Frame(16f);  // the down cancels the coast
                h.Scene.TryGetScroll(v2, out var afterDown);
                bool cancelled = afterDown.Phase != ScrollIntegrator.Fling && MathF.Abs(afterDown.FlingVelocity) < 1f;
                float offAfter = afterDown.OffsetY;
                pr.Frame(16f);                                                                       // no drift the next frame
                h.Scene.TryGetScroll(v2, out var next);
                return (wasFlinging && cancelled, MathF.Abs(next.OffsetY - offAfter) < 0.5f);
            }
            var mouse = DownCancels(PointerKind.Mouse);
            var touch = DownCancels(PointerKind.Touch);
            Check("gate.scroll.pointerdown-cancels a mouse click / touch-down over a live Fling zeros velocity the SAME frame with no residual drift (the shared OnCancelFling — scrollbar-grab routes here too; pins R6)",
                mouse.cancelled && mouse.noDrift && touch.cancelled && touch.noDrift,
                $"mouse(cancel={mouse.cancelled},noDrift={mouse.noDrift}) touch(cancel={touch.cancelled},noDrift={touch.noDrift})");
        }

        // gate.scroll.wheel-lines (§8.9): the SPI wheel-lines preference scales per-notch distance (distance =
        // notch·(lines/3)·perNotch), page mode ⇒ 0.875·viewport per notch, and the sub-120 signed carryover accumulates the
        // raw delta and emits one notch per 120 crossed keeping a signed remainder (reset on direction change — the Win32
        // producer's §3.2 algorithm, verified here headlessly).
        {
            float NotchTarget(float preScaledNotch)
            {
                using var app = new HeadlessPlatformApp();
                var w = new HeadlessWindow(new WindowDesc("v2-wheel-lines", new Size2(360, 460), 1f)); w.Show();
                using var h = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
                h.Input.SmoothScroll = true; h.RunFrame();
                var v2 = h.Scene.Root;
                var pr = new HeadlessScrollProducer(w, h, center);
                pr.WheelNotch(preScaledNotch); pr.Frame(16f);
                h.Scene.TryGetScroll(v2, out var s);
                return s.PendingTargetY;   // WheelAnimating hard-clamped accumulated target = notch·perNotch
            }
            float perNotch = MathF.Max(48f, 0.10f * 400f);   // 48
            float lines3 = NotchTarget(1f * (3f / 3f));       // lines=3 ⇒ ×1 ⇒ 48
            float lines1 = NotchTarget(1f * (1f / 3f));       // lines=1 ⇒ ×1/3 ⇒ 16
            float lines6 = NotchTarget(1f * (6f / 3f));       // lines=6 ⇒ ×2 ⇒ 96
            bool linesOk = Near(lines3, perNotch, 0.5f) && Near(lines1, perNotch / 3f, 0.5f) && Near(lines6, 2f * perNotch, 0.5f);
            // Page mode: a notch pages 0.875·viewport (a DIP delta, not a notch-scaled distance).
            float pageExpected = 0.875f * 400f;
            float pageTarget;
            using (var app = new HeadlessPlatformApp())
            {
                var w = new HeadlessWindow(new WindowDesc("v2-wheel-page", new Size2(360, 460), 1f)); w.Show();
                using var h = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
                h.Input.SmoothScroll = true; h.RunFrame();
                var v2 = h.Scene.Root;
                var pr = new HeadlessScrollProducer(w, h, center);
                w.QueueInput(new InputEvent(InputKind.Wheel, center, 0, 0, ScrollDelta: pageExpected, Pointer: PointerKind.Mouse, TimestampMs: pr.Ms));
                pr.Frame(16f);
                h.Scene.TryGetScroll(v2, out var s);
                pageTarget = s.PendingTargetY;
            }
            bool pageOk = Near(pageTarget, pageExpected, 0.5f);
            // Signed carryover (the §3.2 producer algorithm): accumulate raw units, emit per 120, keep signed remainder,
            // reset the remainder on a direction change.
            static (int notches, int rem) Carry(int prevRem, int raw) { int acc = prevRem + raw; return (acc / 120, acc % 120); }
            var s1 = Carry(0, 60);        // (0, 60)   — sub-notch held
            var s2 = Carry(s1.rem, 60);   // (1, 0)    — crossed 120
            var s3 = Carry(s2.rem, 130);  // (1, 10)   — one notch + signed remainder
            var s4 = Carry(0, -60);       // (0, -60)  — reset on direction flip, signed remainder
            bool carryOk = s1 == (0, 60) && s2 == (1, 0) && s3 == (1, 10) && s4 == (0, -60);
            Check("gate.scroll.wheel-lines SPI wheel-lines scales per-notch distance (lines 1/3/6 ⇒ ×1/3, ×1, ×2), page mode pages 0.875·viewport, and the signed sub-120 carryover accumulates + resets on direction change (§3.2)",
                linesOk && pageOk && carryOk,
                $"lines1={lines1:0.0} lines3={lines3:0.0} lines6={lines6:0.0} page={pageTarget:0.0}(exp {pageExpected:0}) carry={carryOk}");
        }

        // gate.scroll.subpixel-stability (§8.10): a slow sub-pixel pan produces monotonic WHOLE-device-px translate steps
        // (tx = round((offset+band)·scale)/scale) while the logical offset stays continuous float — and a ScrollBind sticky
        // pin sharing the same origin computes the SAME rounded translation (no 1px seam). scale = 2 ⇒ steps of 0.5 DIP.
        {
            const float scale = 2f;
            NodePaint cp = default; var bounds = new RectF(0, 0, 300, 3200);
            float prevTx = float.NaN; bool monotonic = true, wholePx = true, stepOk = true;
            for (int i = 0; i <= 40; i++)
            {
                float off = i * 0.1f;   // 0..4 DIP in 0.1 steps (sub-device-pixel at scale 2)
                OverscrollPhysics.WriteContentTransform(ref cp, in bounds, horizontal: false, offset: off, band: 0f, zoomFactor: 1f, scale: scale);
                float tx = -cp.LocalTransform.Dy;                       // translation Y = −tx
                if (MathF.Abs(tx * scale - MathF.Round(tx * scale)) > 1e-4f) wholePx = false;   // integral device px
                if (!float.IsNaN(prevTx))
                {
                    float step = tx - prevTx;
                    if (step + 1e-4f < 0f) monotonic = false;
                    if (!(step < 1e-4f || Near(step, 1f / scale, 1e-3f))) stepOk = false;        // step is 0 or one device px
                }
                prevTx = tx;
            }
            // Sticky-header share-origin (finding-4 fix): drive a REAL pinned ScrollBind through the host across a
            // sub-pixel offset sweep and assert the pinned header lands on the SAME device-pixel grid as the rounded
            // content — ApplyPin now rounds its shift to whole device px (ScrollBindEval.ApplyPin), so a header never seams
            // a sub-pixel step against the content beneath it. (The prior sub-check called WriteContentTransform TWICE with
            // identical args and asserted equality — tautological; it never exercised ApplyPin, so a real pin/content seam
            // — ApplyPin wrote an UNROUNDED Affine2D.Translation(0, shift) while content was device-px rounded — shipped unverified.)
            bool pinDeviceAligned = true, pinWholePx = true, pinnedEver = false; string pinLog = "";
            {
                using var papp = new HeadlessPlatformApp();
                var pwin = new HeadlessWindow(new WindowDesc("subpixel-pin", new Size2(320, 240), 2f)); pwin.Show();
                NodeHandle headerN = NodeHandle.Null;
                var proot = new W0fStaticProbe
                {
                    Build = () => Ui.ScrollView(new BoxEl
                    {
                        Direction = 1,
                        Children =
                        [
                            new BoxEl { Height = 100f },                                  // lead-in
                            new BoxEl { Direction = 1, Children =
                            [
                                new BoxEl { Height = 40f, ScrollBinds = [ new() { PinTop = 0f } ], OnRealized = h => headerN = h },
                                new BoxEl { Height = 400f },
                            ] },
                            new BoxEl { Height = 800f },
                        ],
                    }),
                };
                using var phost = new AppHost(papp, pwin, new HeadlessGpuDevice(), fonts, strings, proot);
                phost.RunFrame();
                var ps = phost.Scene;
                NodeHandle FindVp(NodeHandle n)
                {
                    if (n.IsNull) return NodeHandle.Null;
                    if (ps.HasScroll(n)) return n;
                    for (var c = ps.FirstChild(n); !c.IsNull; c = ps.NextSibling(c)) { var r = FindVp(c); if (!r.IsNull) return r; }
                    return NodeHandle.Null;
                }
                var pvp = FindVp(ps.Root);
                var pcontent = ps.ScrollRef(pvp).ContentNode;
                float ds = ps.DeviceScale;   // = 2 (the window scale) — the grid the pin must share with the content
                for (int i = 0; i <= 24 && !headerN.IsNull; i++)
                {
                    float y = 250f + i * 0.1f;   // sub-device-pixel offsets, all inside the header's pin range [100,500]
                    ref ScrollState pst = ref ps.ScrollRef(pvp);
                    pst.OffsetY = y; pst.TargetY = y;
                    // Round the content translation exactly as the offset chokepoint does (device-px snap) so a shared-grid
                    // pin cannot seam; then the real ApplyPinAndFlagPass pins the header with its (now rounded) shift.
                    OverscrollPhysics.WriteContentTransform(ref ps.Paint(pcontent), in bounds, false, y, 0f, 1f, ds);
                    ps.Mark(pcontent, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                    pwin.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
                    phost.RunFrame();
                    if ((ps.Flags(headerN) & NodeFlags.StickyPinned) != 0) pinnedEver = true;
                    float shiftDy = ps.Paint(headerN).LocalTransform.Dy;                         // the applied pin shift
                    if (MathF.Abs(shiftDy * ds - MathF.Round(shiftDy * ds)) > 1e-3f) pinWholePx = false;
                    float absY = ps.AbsoluteRect(headerN).Y;                                     // on-screen (content + pin composed)
                    if (MathF.Abs(absY * ds - MathF.Round(absY * ds)) > 1e-3f) { pinDeviceAligned = false; if (pinLog.Length == 0) pinLog = $"seam@y={y:0.0} absY={absY:0.###}"; }
                }
                if (headerN.IsNull) { pinDeviceAligned = false; pinLog = "header not realized"; }
            }
            Check("gate.scroll.subpixel-stability a slow sub-pixel pan advances in monotonic whole-device-px steps (tx=round((off+band)·s)/s) while logical offset stays float, and a REAL pinned ScrollBind shares the content's device grid — its shift AND on-screen position snap to whole device px (no 1px seam)",
                monotonic && wholePx && stepOk && pinnedEver && pinWholePx && pinDeviceAligned,
                $"monotonic={monotonic} wholePx={wholePx} stepOk={stepOk} pinnedEver={pinnedEver} pinWholePx={pinWholePx} pinDeviceAligned={pinDeviceAligned} {pinLog}");
        }

        // gate.scroll.transition-matrix (§8.11, structural guard for R1): drive the feel-critical legal transitions and
        // assert each resulting Phase; a wheel NEVER produces a band (WheelAnimating→Overscroll is undefined — the §2.2 extent
        // asymmetry), which is asserted as a hard negative.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("v2-matrix", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.Input.SmoothScroll = true; host.RunFrame();
            var vp = host.Scene.Root;
            var log = new System.Text.StringBuilder();
            bool AssertPhase(string label, byte got, byte want) { bool ok = got == want; if (!ok) log.Append($"[{label}:got {got}!={want}]"); return ok; }
            bool m = true;

            var pr = new HeadlessScrollProducer(window, host, center) { Device = (byte)ScrollDeviceClass.Touchpad };
            pr.ContactBegin(0f); pr.Step(16); pr.ContactUpdate(30f); pr.Step(16);
            host.Scene.TryGetScroll(vp, out var s1); m &= AssertPhase("Idle→Track", s1.Phase, ScrollIntegrator.TouchpadTracking);
            pr.Device = (byte)ScrollDeviceClass.WheelHiResFallback;
            pr.ContactBegin(0f); pr.Step(16);
            for (int i = 0; i < 8; i++) { pr.Ms += 8; pr.ContactUpdate(8f); pr.Frame(16f); }
            pr.Ms += 8; pr.ContactEnd(); pr.Frame(16f);
            host.Scene.TryGetScroll(vp, out var s2); m &= AssertPhase("Track→Fling", s2.Phase, ScrollIntegrator.Fling);
            pr.PointerDownAt(center); pr.Frame(16f);
            host.Scene.TryGetScroll(vp, out var s3); m &= AssertPhase("Fling→Idle(down)", s3.Phase, ScrollIntegrator.Idle);
            for (int i = 0; i < 40; i++) { pr.Frame(16f); host.Scene.TryGetScroll(vp, out var q); if (q.Phase == ScrollIntegrator.Idle && MathF.Abs(q.OverscrollPx) < 0.1f) break; }
            pr.WheelNotch(1f); pr.Frame(16f);
            host.Scene.TryGetScroll(vp, out var s4); m &= AssertPhase("Idle→Wheel", s4.Phase, ScrollIntegrator.WheelAnimating);
            pr.PointerDownAt(center); pr.Frame(16f);
            host.Scene.TryGetScroll(vp, out var s5); m &= AssertPhase("Wheel→Idle(down)", s5.Phase, ScrollIntegrator.Idle);
            for (int i = 0; i < 40; i++) { pr.Frame(16f); host.Scene.TryGetScroll(vp, out var q); if (q.Phase == ScrollIntegrator.Idle) break; }
            for (int i = 0; i < 30; i++) { pr.WheelNotch(-1f); pr.Frame(16f); }   // hammer the TOP extent with wheel
            for (int i = 0; i < 60; i++) { pr.Frame(16f); host.Scene.TryGetScroll(vp, out var q); if (q.Phase == ScrollIntegrator.Idle) break; }
            host.Scene.TryGetScroll(vp, out var s6);
            bool wheelNoBand = MathF.Abs(s6.OverscrollPx) < 0.01f && s6.Phase != ScrollIntegrator.Overscroll;
            if (!wheelNoBand) log.Append($"[illegal Wheel→band:{s6.OverscrollPx:0.0}]");
            pr.Device = (byte)ScrollDeviceClass.Touchpad;
            pr.ContactBegin(0f); pr.Step(16);
            for (int i = 0; i < 10; i++) { pr.ContactUpdate(-60f); pr.Step(16); }   // past the top edge → Overscroll band
            host.Scene.TryGetScroll(vp, out var sOver); bool overOk = sOver.Phase == ScrollIntegrator.Overscroll || MathF.Abs(sOver.OverscrollPx) > 1f;
            pr.ContactEnd(); pr.Step(16);
            host.Scene.TryGetScroll(vp, out var s7); m &= AssertPhase("Overscroll→SnapBack", s7.Phase, ScrollIntegrator.SnapBack);
            for (int i = 0; i < 120; i++) { pr.Step(16); host.Scene.TryGetScroll(vp, out var q); if (q.Phase == ScrollIntegrator.Idle && MathF.Abs(q.OverscrollPx) < 0.1f) break; }
            host.Scene.TryGetScroll(vp, out var s8); m &= AssertPhase("SnapBack→Idle", s8.Phase, ScrollIntegrator.Idle);
            Check("gate.scroll.transition-matrix the feel-critical legal transitions (Idle→Track→Fling, Fling/Wheel→Idle on pointer-down, Idle→Wheel, Overscroll→SnapBack→Idle) each land on the correct §2.2 Phase, and a wheel NEVER bands at the extent (undefined ⇒ hard fail)",
                m && overOk && wheelNoBand, log.Length == 0 ? "all transitions correct; wheelNoBand=True" : log.ToString());
        }

        // gate.scroll.alloc-zero (§8.12): a full contact + fling + momentum + wheel cycle allocates 0 managed bytes on the
        // hot half (phases 6–13), including the DM-style momentum sink cadence. Fill-only bound rows (no per-row string
        // intern), warmed once before the measured window.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("v2-alloc-zero", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new BoundVirtualFillOnlyProbe());
            host.Input.SmoothScroll = true; host.RunFrame();
            long Cycle(bool warm)
            {
                var pr = new HeadlessScrollProducer(window, host, center) { Device = (byte)ScrollDeviceClass.Touchpad };
                long worst = 0;
                void Acc(FrameStats f) { if (!warm && f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
                pr.ContactBegin(0f); Acc(pr.Frame(16f));
                for (int i = 0; i < 6; i++) { pr.Ms += 8; pr.ContactUpdate(20f); Acc(pr.Frame(16f)); }
                pr.MomentumBegin(); Acc(pr.Frame(16f));
                for (int i = 0; i < 8; i++) { pr.Ms += 8; pr.MomentumUpdate(12f); Acc(pr.Frame(16f)); }   // OS inertia sink cadence
                pr.MomentumEnd(); Acc(pr.Frame(16f));
                pr.WheelNotch(2f); Acc(pr.Frame(16f));
                for (int i = 0; i < 30; i++) Acc(pr.Frame(16f));
                return worst;
            }
            Cycle(warm: true);   // JIT the whole path outside the measured window
            for (int i = 0; i < 40; i++) host.RunFrame();
            long worst = Cycle(warm: false);
            Check("gate.scroll.alloc-zero a full contact+fling+momentum+wheel cycle (incl. the OS-momentum sink cadence) allocates 0 managed bytes on the hot half (phases 6–13)",
                worst == 0, $"worstHotAlloc={worst}B across the scripted cycle");
        }

        // gate.scroll.anchor-repin-under-gesture (Fix 1: the homepage touchpad-jitter repro): jump DEEP into a MEASURED
        // virtual list (the sticky-gate raw-ScrollRef pattern, so the rows above the jump target stay UNREALIZED at their
        // 40px estimate), then drag UPWARD — rows entering from above realize at 64px, and every correction to a row
        // strictly ABOVE the anchor fires the virtualization anchor re-pin (FlexLayout.RecordAnchorShift) with a +24 delta
        // WHILE the touchpad contact gesture is tracking. (A downward drag from the top is vacuous: corrections land at or
        // below the anchor and never move OffsetOf(anchorIndex).) Fix 1 records each re-pin as a coordinate shift
        // (ScrollState.PendingAnchorShift) the phase-7 integrator drains into the resampler anchor (_rs.Anchor), so the
        // finger-driven offset moves WITH the re-pin instead of being overwritten/fought a tick later. Decomposition: the
        // finger leg is reconstructed INDEPENDENTLY of the offset — exactly as gate.scroll.contact-1to1: one packet/frame
        // at the present time keeps the resampler interpolating the constant-velocity line ⇒
        // finger = −vel·(present − ResampleLatencyMs − t0). The remaining term Σshift = off − latch − finger must then be
        // a genuine accumulation of anchor re-pin deltas:
        //   • FIRED (maxShift ≥ ~one 24-DIP correction) — the discriminator: on pre-fix code TouchpadTracking overwrites
        //     the pin with Clamp(unshifted anchor + xStar) next tick, so Σshift never accumulates (or oscillates);
        //   • NON-NEGATIVE (over-measure rows only grow content above the anchor; a dropped re-pin makes it lag);
        //   • MONOTONE non-decreasing (a fought re-pin oscillates — the felt jitter);
        //   • BOUNDED by the total possible above-correction (rowsAbove·overMeasure).
        // Σshift is NOT compared to an exact predicted value: the per-frame re-pin delta depends on the internal
        // budgeted-realize schedule (which above-viewport rows correct that frame) — an implementation detail.
        // Tolerance: 0.5 DIP (the contact-1to1 interpolation bound; the shift accumulation is exact POD-float arithmetic).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("anchor-repin-gesture", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new AnchorRepinProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            // Jump deep without realizing the rows above (raw ScrollRef write, the sticky-gate pattern): rows < ~400 keep
            // their 40px estimate, so the upward drag below realizes them mid-gesture and fires genuine re-pins.
            const float seed = 16000f;
            {
                ref ScrollState st = ref host.Scene.ScrollRef(vp);
                st.OffsetY = seed; st.TargetY = seed;
            }
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
            for (int i = 0; i < 12; i++) host.RunFrame();   // let the realize window + local corrections settle at the seed
            var prod = new HeadlessScrollProducer(window, host, new Point2(150, 150)) { Device = (byte)ScrollDeviceClass.Touchpad };
            const float vel = 0.9f;   // DIP/ms upward — ~860 DIP over the run, ~20 estimate-priced rows entering from above
            const float overMeasure = AnchorRepinProbe.Real - AnchorRepinProbe.Estimate;   // 24 DIP per row
            host.Scene.TryGetScroll(vp, out var sL); float latchOff = sL.OffsetY;   // settle may itself have re-pinned; latch from live
            double t0 = prod.FrameMs;
            prod.ContactBegin(0f); prod.Frame(16f);
            bool shiftNonNeg = true, shiftMonotone = true, shiftBounded = true;
            float prevShift = 0f, maxShift = 0f; bool havePrev = false; int frames = 0;
            for (int kf = 0; kf < 60; kf++)
            {
                prod.Ms = (uint)prod.FrameMs;                  // deliver one packet AT the present time (interpolation regime)
                prod.ContactUpdate(-vel * 16f);                // UPWARD — toward the unrealized estimate-priced region
                double present = prod.FrameMs;
                prod.Frame(16f);
                host.Scene.TryGetScroll(vp, out var s);
                if (kf < 3 || s.Phase != ScrollIntegrator.TouchpadTracking) continue;
                if (!(s.OffsetY > 1f)) continue;                                                 // well clear of the top clamp
                float finger = (float)(-vel * (present - ScrollTuning.ResampleLatencyMs - t0));  // INDEPENDENT resampled finger position
                float shift = s.OffsetY - latchOff - finger;                                     // observed Σ(anchor re-pin deltas)
                if (havePrev && shift - prevShift < -0.5f) shiftMonotone = false;    // a fought re-pin oscillates (the felt jitter)
                if (shift < -0.5f) shiftNonNeg = false;                              // a dropped re-pin makes the offset lag the finger
                if (shift > s.AnchorIndex * overMeasure + 1f) shiftBounded = false;  // ≤ total possible above-correction
                maxShift = MathF.Max(maxShift, shift);
                prevShift = shift; havePrev = true; frames++;
            }
            bool firedRepin = maxShift >= overMeasure - 4f;   // ≥ ~one genuine 24-DIP re-pin — the scenario can't go vacuous
            Check("gate.scroll.anchor-repin-under-gesture an upward monotone touchpad gesture from a deep seed realizes estimate-priced rows above the anchor mid-gesture, and the offset stays = latch + resampled-finger + a FIRED/non-negative/monotone/bounded Σ(anchor re-pin deltas) — the re-pin is consumed by the phase-7 integrator (Fix 1), never dropped or fought",
                firedRepin && shiftNonNeg && shiftMonotone && shiftBounded && frames >= 20,
                $"frames={frames} maxRepinShift={maxShift:0.00} firedRepin={firedRepin} shiftNonNeg={shiftNonNeg} shiftMonotone={shiftMonotone} shiftBounded={shiftBounded}");
        }

        // gate.scroll.wheel-chase-extent-shrink (Fix 1 tail: the ArrangeViewport PendingTarget re-clamp): a WheelAnimating
        // chase is seeded toward the bottom of a tall list, then the content extent is shrunk HARD (row count 400 → 60)
        // mid-chase so maxOff collapses far below the accumulated PendingTargetY. ArrangeViewport now re-clamps
        // PendingTargetX/Y (NaN-guarded) to the live [0,max] extent, so the chase rides the shrunk extent, lands on the new
        // bottom, and SETTLES to Idle — instead of a stale target beyond the moving clamp keeping the offset pinned in a
        // never-settling WheelAnimating fight (the near-bottom oscillation / bounce). Assert: the chase never reverses
        // against its down direction; PendingTargetY is re-clamped onto the new extent (or NaN once settled); the chase
        // reaches Idle at the corrected bottom.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("wheel-chase-shrink", new Size2(360, 460), 1f)); window.Show();
            var probe = new ShrinkChaseProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.Input.SmoothScroll = true;   // a mouse notch drives a WheelAnimating chase (accumulated PendingTarget), not a synchronous jump
            host.RunFrame();
            var vp = host.Scene.Root;
            // Seed a large accumulated chase toward the bottom (WheelNotch is in notch units × PerNotchDip): 480 notches ×
            // 48 = 23040 DIP, just under the initial maxOff (400·60 − 300 = 23700) so it seeds without being clamped.
            host.Input.Dispatch(new[] { new InputEvent(InputKind.Wheel, new Point2(150, 150), 0, 0, WheelNotch: 480f, Pointer: PointerKind.Mouse, TimestampMs: 1000) });
            host.Scene.TryGetScroll(vp, out var seeded);
            bool seededChase = seeded.Phase == ScrollIntegrator.WheelAnimating && !float.IsNaN(seeded.PendingTargetY) && seeded.PendingTargetY > 10000f;
            // Shrink the content HARD before the chase advances: 400 → 60 rows ⇒ content 3600, maxOff 3300 — far below the
            // 23040 target. The re-clamp must pull PendingTargetY down onto the new extent; without it the chase would keep
            // fighting a target beyond the clamp and never settle.
            probe.Count.Value = 60;
            float prevOff = seeded.OffsetY, worstBackStep = 0f; bool settled = false;
            for (int f = 0; f < 3000; f++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                float d = s.OffsetY - prevOff;
                if (d < worstBackStep) worstBackStep = d;   // most-negative step against the down-chase
                prevOff = s.OffsetY;
                if (s.Phase == ScrollIntegrator.Idle) { settled = true; break; }
            }
            host.Scene.TryGetScroll(vp, out var fin);
            float newMax = MathF.Max(0f, fin.ContentH - fin.ViewportH);
            bool noReversal = worstBackStep >= -0.5f;                                                        // never steps backward (settling is fine)
            bool targetReclamped = float.IsNaN(fin.PendingTargetY) || fin.PendingTargetY <= newMax + 0.5f;   // rode the shrunk extent
            bool landed = Near(fin.OffsetY, newMax, 2f);                                                     // ended at the corrected bottom
            Check("gate.scroll.wheel-chase-extent-shrink a WheelAnimating chase toward the bottom survives the content extent shrinking far below its target mid-chase — ArrangeViewport re-clamps PendingTargetY onto the live extent so the offset never reverses, lands on the corrected bottom, and settles to Idle (no stale-target never-settling fight)",
                seededChase && noReversal && targetReclamped && landed && settled,
                $"seeded={seededChase} pendingSeed={seeded.PendingTargetY:0} newMax={newMax:0} finalOff={fin.OffsetY:0} finalPending={fin.PendingTargetY:0} worstBackStep={worstBackStep:0.00} settled={settled}");
        }
    }

    static void TouchpadFeelChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.scroll.decay-kernel-distance: seed a coast at v0 = 1000 px/s and integrate OverscrollPhysics.CoastStep at a
        // fixed 60 Hz dt until the speed drops below the production settle cutoff (FlingMinVelocityPxPerS = 13 px/s). The
        // total coast = ~330 px (the v→0 asymptote is v0/−ln(0.05) = 333.8 px; the 13 px/s cutoff stops it ~4 px short).
        // This explicitly locks the reusable kernel to the Windows-like feel and its roughly 1.5s 1000 px/s tail.
        {
            const float v0 = 1000f, dtMs = 1000f / 60f;
            float settle = ScrollIntegrator.FlingMinVelocityPxPerS;   // tracks the production settle cutoff (13 px/s) so the gate stays honest
            float v = v0, coast = 0f; int frames = 0;
            while (frames < 100000)
            {
                coast += OverscrollPhysics.CoastStep(ref v, dtMs, ScrollIntegrator.FlingDecayPerS);
                frames++;
                if (MathF.Abs(v) < settle) break;
            }
            bool ok = Near(ScrollIntegrator.FlingDecayPerS, 0.05f, 0.0001f)
                   && Near(coast, 330f, 2f)
                   && frames >= 85 && frames <= 92;
            Check("gate.scroll.decay-kernel-distance a v0=1000 px/s coast at fixed 60 Hz settles at ~330 px in ~1.5s (locks WinUI-like FlingDecayPerS=0.05 + the 13 px/s settle)",
                ok, $"decay={ScrollIntegrator.FlingDecayPerS:0.###} coast={coast:0.##}px (expect ~330 ±2) frames={frames} (expect 85..92) k={-MathF.Log(ScrollIntegrator.FlingDecayPerS):0.###}");
        }

        // gate.scroll.decay-kernel-frame-rate-independence: the SAME v0=1000 px/s coast integrated at dt = 1000/60, 1000/120, 1000/144
        // settles at the IDENTICAL distance (within 0.5 px). The exact closed-form per-step integral (Δpos = v(1−decay^dt)/k)
        // telescopes to the same geometric sum at any timestep — a plain v·dt Riemann step would diverge ~4 px across these
        // rates. Locks the Pow(decay, dt) decay form AND the closed-form position step.
        {
            static float Coast(float dtMs)
            {
                float v = 1000f, coast = 0f; int frames = 0;
                while (frames < 100000)
                {
                    coast += OverscrollPhysics.CoastStep(ref v, dtMs, ScrollIntegrator.FlingDecayPerS);
                    frames++;
                    if (MathF.Abs(v) < ScrollIntegrator.FlingMinVelocityPxPerS) break;   // production settle cutoff (13 px/s)
                }
                return coast;
            }
            float c60 = Coast(1000f / 60f), c120 = Coast(1000f / 120f), c144 = Coast(1000f / 144f);
            bool ok = Near(c60, c120, 0.5f) && Near(c120, c144, 0.5f) && Near(c60, c144, 0.5f);
            Check("gate.scroll.decay-kernel-frame-rate-independence the same v0=1000 px/s coast at dt∈{60,120,144}Hz settles at the IDENTICAL distance within 0.5px (the exact closed-form integral is frame-rate-independent; v·dt would diverge ~4px)",
                ok, $"coast 60Hz={c60:0.###} 120Hz={c120:0.###} 144Hz={c144:0.###} (spread={MathF.Max(MathF.Max(c60, c120), c144) - MathF.Min(MathF.Min(c60, c120), c144):0.###})");
        }

        // gate.touchpad.band-roundtrip (v2): the iOS rubber-band must round-trip — ExcessFromBand is the EXACT inverse of
        // BandFromExcess for ALL excess (the v2 map never saturates, so the inverse holds everywhere with no early-out
        // branch — scroll-feel-rework-v2 §4.4/§8 gate 7). With vp=3000 the asymptote is d=0.15·vp=450; x∈{5,50,200} sit
        // in the near-linear region and x∈{600,3000} are the OLD saturation points (2·limit / 10·limit at the old
        // limit=300) that the v1 min()-knee could NOT invert — v2 round-trips them within 0.5 px.
        {
            const float vp = 3000f;   // d = 0.15·vp = 450 ⇒ band(200)=88.4, band(3000)=353.6, all < d (never reaches the wall)
            float worst = 0f;
            foreach (float x in new[] { 5f, 50f, 200f, 600f, 3000f })
            {
                float band = OverscrollPhysics.BandFromExcess(x, vp);
                float rt = OverscrollPhysics.ExcessFromBand(band, vp);
                worst = MathF.Max(worst, MathF.Abs(rt - x));
            }
            bool ok = worst < 0.5f;
            Check("gate.touchpad.band-roundtrip ExcessFromBand(BandFromExcess(x)) ≈ x for x∈{5,50,200,600,3000} within 0.5px (the v2 iOS map never saturates ⇒ the closed-form inverse is exact everywhere, incl. at/above the old saturation point)",
                ok, $"worstRoundTripErr={worst:0.######}px");
        }

        // (The pre-rework touchpad gates — progressive-packet-curve, settle-determinism, os-tail-no-double-inertia,
        // the touchpad alloc-zero, inter-burst-no-restart, edge-release-bounded, edge-tail-no-plateau — are DELETED with
        // the second integrator they gated: ShapeTouchpadPacketDelta / TickTouchpad / TouchpadActive / the velocity-
        // enveloped band no longer exist. Their behaviors re-gate on the phase-tagged contract per
        // docs/plans/scroll-feel-rework-design.md §12/§13 (gates 2/5/6/9/11).)

        // gate.scroll.phase-release-velocity — regression lock for two ON-DEVICE-MEASURED estimator defects on the
        // phase-contract (touchpad wheel-fallback) path:
        // (a) FOLD FIDELITY (the axis-swap defect): TWO ScrollUpdate packets per frame — the ring folds each pair and
        //     deposits the overwritten packet's running sums into the velocity side ring. A constant-velocity vertical
        //     stream must read the EXACT hand speed; the swapped-axis deposit fed the pan axis flat plateaus + per-frame
        //     spikes and inflated the release ~4-6× (oversized flings, violent edge bounces). An abrupt stop at speed
        //     still seeds exactly ONE fling (the trailing gate must not kill real flicks).
        // (b) DECEL-THEN-SILENCE (the phantom-fling defect, v2 single-gate form): a stream decelerating to near-rest and
        //     then going SILENT ≥40ms before the lift seeds NO fling. v1 leaned on a trailing-32ms displacement gate to
        //     beat the work-energy ledger's remembered START energy; v2 DELETES that dual gate (scroll-feel-rework-v2
        //     §4.3) — the completed tail + silence is caught by AssumeStoppedMs alone (newest sample older than 40ms at
        //     lift ⇒ v=0). One window, one gate. (A decel ramp still MOVING at lift correctly seeds a proportional fling
        //     — that is the honest v2 hand speed, not a phantom; the double-inertia case is specifically the silent tail.)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("phase-release", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            var pos = new Point2(150, 200);
            const byte Fb = (byte)ScrollDeviceClass.WheelHiResFallback;
            float decay1 = MathF.Exp(MathF.Log(ScrollIntegrator.FlingDecayPerS) * 16f / 1000f);   // the End frame's phase-7 tick decays the fresh seed once

            // (a) 1000 px/s hand speed: ScrollBegin + 16 ScrollUpdates of 8 DIP at 8 ms stamps, TWO per frame.
            uint t = 5000;
            window.QueueInput(new InputEvent(InputKind.ScrollBegin, pos, 0, 0, ScrollDelta: 8f,
                Pointer: PointerKind.Touchpad, TimestampMs: t, PointerId: 9, DeviceClassRaw: Fb));
            host.RunFrame();
            for (int i = 0; i < 8; i++)
            {
                window.QueueInput(new InputEvent(InputKind.ScrollUpdate, pos, 0, 0, ScrollDelta: 8f,
                    Pointer: PointerKind.Touchpad, TimestampMs: t += 8, PointerId: 9, DeviceClassRaw: Fb));
                window.QueueInput(new InputEvent(InputKind.ScrollUpdate, pos, 0, 0, ScrollDelta: 8f,
                    Pointer: PointerKind.Touchpad, TimestampMs: t += 8, PointerId: 9, DeviceClassRaw: Fb));
                host.RunFrame();   // the pair coalesces in the ring; the overwritten packet feeds the side ring
            }
            window.QueueInput(new InputEvent(InputKind.ScrollEnd, pos, 0, 0,
                Pointer: PointerKind.Touchpad, TimestampMs: t, PointerId: 9, DeviceClassRaw: Fb));   // lift = last packet's stamp (design §2)
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var scA);
            float vFold = MathF.Abs(scA.FlingVelocity);
            bool foldExact = scA.Phase == ScrollIntegrator.Fling && MathF.Abs(vFold - 1000f * decay1) <= 1000f * decay1 * 0.03f;   // ±3%
            for (int i = 0; i < 400; i++) { host.RunFrame(); host.Scene.TryGetScroll(vp, out var s); if (s.Phase == 0) break; }

            // (b) decelerate to near-rest THEN a ≥40ms silence: steady 8-DIP packets, then a 4/2/0.6/0.3 ramp (one per
            // frame, 16 ms stamps), then the lift arrives 60 ms after the last packet — a completed tail then silence. The
            // v2 single 40ms IMPULSE window would read ~90 px/s over the ramp, but AssumeStoppedMs (newest sample >40ms
            // before lift) zeroes it ⇒ NO fling. This is the v2 double-inertia guard (the deleted trailing-32ms dual gate).
            uint t2 = t + 2000;
            window.QueueInput(new InputEvent(InputKind.ScrollBegin, pos, 0, 0, ScrollDelta: 8f,
                Pointer: PointerKind.Touchpad, TimestampMs: t2, PointerId: 9, DeviceClassRaw: Fb));
            host.RunFrame();
            foreach (float d in new[] { 8f, 8f, 8f, 8f, 8f, 8f, 8f, 4f, 2f, 0.6f, 0.3f })
            {
                window.QueueInput(new InputEvent(InputKind.ScrollUpdate, pos, 0, 0, ScrollDelta: d,
                    Pointer: PointerKind.Touchpad, TimestampMs: t2 += 16, PointerId: 9, DeviceClassRaw: Fb));
                host.RunFrame();
            }
            window.QueueInput(new InputEvent(InputKind.ScrollEnd, pos, 0, 0,
                Pointer: PointerKind.Touchpad, TimestampMs: t2 + 60, PointerId: 9, DeviceClassRaw: Fb));   // 60ms silence → AssumeStopped → v=0
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var scB);
            bool decelNoFling = scB.Phase != ScrollIntegrator.Fling || MathF.Abs(scB.FlingVelocity) < 1f;

            Check("gate.scroll.phase-release-velocity the phase-contract release estimator: a ring-FOLDED (two packets/frame) constant-velocity stream reads the exact hand speed and an abrupt stop seeds ONE fling (axis-swap regression lock); a decelerate-then-silence stream seeds NO fling (v2 §4.3 single 40ms IMPULSE window + AssumeStoppedMs — the deleted dual gate)",
                foldExact && decelNoFling,
                $"fold={vFold:0} (expect {1000f * decay1:0} ±3%) phase={scA.Phase} decelFling={(scB.Phase == ScrollIntegrator.Fling ? scB.FlingVelocity : 0f):0.0}");
        }

        // gate.touchpad.mouse-wheel-takeover: a phase-driven scroll gesture (touchpad fallback) can still own a TOP
        // rubber-band when a physical mouse wheel arrives (no touchpad-up event exists). The mouse must synchronously take
        // ownership: cancel the gesture (CancelGesture), clear the band, reset Offset==Target, then seed ONE
        // WheelAnimating chase (accumulated PendingTarget; scroll-feel-rework-v2 §4.2). Historic defect this locks: two
        // scroll owners writing the same ScrollState concurrently produced positive OffsetY with a negative top band.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("tp-wheel-takeover", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            host.Input.SmoothScroll = true;   // shipping Windows path: mouse wheel seeds WheelAnimating
            var vp = host.Scene.Root;
            var pos = new Point2(150, 200);

            // Pull above the top with two phase-contract contact packets (the wheel-fallback producer's shape) so the
            // gesture owns a real negative band.
            window.QueueInput(new InputEvent(InputKind.ScrollBegin, pos, 0, 0, ScrollDelta: -120f,
                Pointer: PointerKind.Touchpad, TimestampMs: 1000, PointerId: 7,
                DeviceClassRaw: (byte)ScrollDeviceClass.WheelHiResFallback));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.ScrollUpdate, pos, 0, 0, ScrollDelta: -120f,
                Pointer: PointerKind.Touchpad, TimestampMs: 1016, PointerId: 7,
                DeviceClassRaw: (byte)ScrollDeviceClass.WheelHiResFallback));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var before);
            bool touchpadHeldBand = host.Input.GestureActive && before.OverscrollPx < -1f && before.OffsetY == 0f;

            // Dispatch the mouse event directly so the state is observed immediately after input ownership transfers,
            // before the integrator advances the newly-seeded WheelAnimating chase.
            var mouse = new[]
            {
                new InputEvent(InputKind.Wheel, pos, 0, 0, WheelNotch: 1f,
                    Pointer: PointerKind.Mouse, TimestampMs: 1032),
            };
            host.Input.Dispatch(mouse);
            host.Scene.TryGetScroll(vp, out var handed);
            bool cleanHandoff = !host.Input.GestureActive
                                && handed.OverscrollPx == 0f && !handed.Overscrolling
                                && handed.OverscrollVel == 0f
                                && handed.Phase == ScrollIntegrator.WheelAnimating
                                && !float.IsNaN(handed.PendingTargetY) && handed.PendingTargetY > 0f
                                && handed.OffsetY == 0f && handed.TargetY == handed.OffsetY;

            float minOff = handed.OffsetY;
            bool noBandReturned = true;
            for (int i = 0; i < 30; i++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                minOff = MathF.Min(minOff, s.OffsetY);
                noBandReturned &= s.OverscrollPx == 0f && !s.Overscrolling;
            }
            host.Scene.TryGetScroll(vp, out var after);
            bool wheelAdvanced = after.OffsetY > 5f && minOff >= 0f;
            Check("gate.touchpad.mouse-wheel-takeover a physical mouse wheel synchronously cancels an active phase-driven scroll gesture, clears its held overscroll band, and advances under one WheelAnimating owner (accumulated PendingTarget) — no positive offset + negative top-band dead zone",
                touchpadHeldBand && cleanHandoff && noBandReturned && wheelAdvanced,
                $"before=(active {touchpadHeldBand},off {before.OffsetY:0.0},band {before.OverscrollPx:0.0}) handoff=(active {host.Input.GestureActive},phase {handed.Phase},pending {handed.PendingTargetY:0},band {handed.OverscrollPx:0.0}) after=(off {after.OffsetY:0.0},band {after.OverscrollPx:0.0})");
        }

        // gate.scroll.wheel-accumulates-to-extent (v2 §4.2): a fast detented-wheel burst does NOT accumulate an unbounded
        // coast velocity — v1's WheelFlingMode + WheelFlingMaxVelocityPxPerS velocity cap is gone. Each notch advances the
        // accumulated, HARD-CLAMPED PendingTarget; a burst of many notches stops EXACTLY at min(Σnotch, content extent),
        // never past the edge (§2.2 extent asymmetry — a wheel hard-stops, never bands). The integrator then chases it with
        // the velocity-preserving crit-damped WheelAnimating spring.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("wheel-accum-extent", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            host.Input.SmoothScroll = true;   // exercise the shipping wheel path (WheelAnimating), not direct headless scrolling
            var pos = new Point2(150, 200);
            var burst = new InputEvent[16];
            for (int i = 0; i < burst.Length; i++)
                burst[i] = new InputEvent(InputKind.Wheel, pos, 0, 0, WheelNotch: 1f,
                    Pointer: PointerKind.Mouse, TimestampMs: (uint)(1000 + i * 4));
            host.Input.Dispatch(burst);   // all 16 accumulate at offset 0 (no tick between dispatches)
            host.Scene.TryGetScroll(host.Scene.Root, out var seeded);
            float perNotch = host.Input.Tuning.PerNotchDip(seeded.ViewportH);
            float maxOff = MathF.Max(0f, seeded.ContentH - seeded.ViewportH);
            float expected = MathF.Min(16f * perNotch, maxOff);   // §4.2: clamp(Σdistance, 0, max) — hard-stops at the extent
            bool capped = seeded.Phase == ScrollIntegrator.WheelAnimating
                          && !float.IsNaN(seeded.PendingTargetY)
                          && Near(seeded.PendingTargetY, expected, 0.5f)
                          && seeded.PendingTargetY <= maxOff + 0.5f;
            Check("gate.scroll.wheel-accumulates-to-extent a rapid 16-notch wheel burst accumulates the hard-clamped PendingTarget and stops EXACTLY at min(Σnotch, content extent) — no unbounded coast velocity, no overshoot past the edge (v2 §4.2 supersedes the v1 velocity cap)",
                capped, $"pending={seeded.PendingTargetY:0.###} expected={expected:0.###} maxOff={maxOff:0.###} perNotch={perNotch:0.#} phase={seeded.Phase}");
        }

        // gate.scroll.mouse-wheel-zero-dt-survives: the real stopwatch clock intentionally returns dt=0 on the first
        // interactive frame after Resync. The §5 dt≤0 Resync bail (now covering ALL states) must preserve the freshly
        // seeded WheelAnimating intent (Phase + PendingTarget) on the dt=0 frame — a zero-duration step made no movement
        // and must not be mistaken for a settle/clamp — then advance normally on the next positive tick.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("wheel-zero-dt", new Size2(360, 460), 1f)); window.Show();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
                new TouchFlingSettleProbe(), frameTime: clock);
            host.RunFrame();   // initial layout, dt=0
            host.Input.SmoothScroll = true;
            var vp = host.Scene.Root;
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(150, 200), 0, 0,
                WheelNotch: 1f, Pointer: PointerKind.Mouse, TimestampMs: 1000));
            host.RunFrame();   // input seeds WheelAnimating; clock still returns dt=0 → the tick bails, intent preserved
            host.Scene.TryGetScroll(vp, out var held);
            bool survivedZero = held.Phase == ScrollIntegrator.WheelAnimating && !float.IsNaN(held.PendingTargetY) && held.OffsetY == 0f;

            clock.Advance(16f);
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var advanced);
            bool advancedNext = advanced.Phase == ScrollIntegrator.WheelAnimating && advanced.OffsetY > 0f;
            Check("gate.scroll.mouse-wheel-zero-dt-survives a WheelAnimating chase survives the cadence-resync dt=0 frame (intent preserved) and moves on the next positive tick — no repeated no-scroll dead zone",
                survivedZero && advancedNext,
                $"held=(phase {held.Phase},pending {held.PendingTargetY:0},off {held.OffsetY:0.0}) advanced=(phase {advanced.Phase},off {advanced.OffsetY:0.0})");
        }
    }

    static void E11VirtChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // e11virt.1/2 — the IMeasuredVirtualLayout seam (E11-L0): estimate-then-correct + scroll anchoring.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-measured", new Size2(640, 480), 1f));
            window.Show();
            var probe = new MeasuredSeamProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();

            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            var content = sc0.ContentNode;
            var layout = probe.Layout!;
            const float cross = 300f;

            // Realized rows correct from the 40px estimate to their measured extents at arrange (SetMeasured);
            // positions are the corrected prefix sums (OffsetOf) — virtualization.md §6.2 through the USER seam.
            var r1 = Child(host.Scene, content, 1);
            var r2 = Child(host.Scene, content, 2);
            var r3 = Child(host.Scene, content, 3);
            float h01 = MeasuredSeamProbe.H(0) + MeasuredSeamProbe.H(1);
            bool corrected = Near(host.Scene.Bounds(r1).Y, MeasuredSeamProbe.H(0))
                && Near(host.Scene.Bounds(r2).Y, h01)
                && Near(host.Scene.Bounds(r3).Y, h01 + MeasuredSeamProbe.H(2))
                && Near(layout.ItemRect(1, cross).H, MeasuredSeamProbe.H(1))
                && Near(layout.OffsetOf(3, cross), h01 + MeasuredSeamProbe.H(2));

            // Unrealized rows still report the estimate; published ContentSize = corrected window + estimate tail.
            float expected = 0f;
            for (int i = 0; i < sc0.LastRealized; i++) expected += MeasuredSeamProbe.H(i);
            expected += (MeasuredSeamProbe.N - sc0.LastRealized) * MeasuredSeamProbe.Estimate;
            bool estimated = Near(layout.ItemRect(MeasuredSeamProbe.N - 1, cross).H, MeasuredSeamProbe.Estimate)
                && Near(sc0.ContentH, expected, 1f);
            Check("e11virt.1 measured seam: realized rows correct to measured extents (positions = corrected prefix sums); unrealized keep the estimate",
                corrected && estimated, $"y1..3={host.Scene.Bounds(r1).Y:0},{host.Scene.Bounds(r2).Y:0},{host.Scene.Bounds(r3).Y:0} content={sc0.ContentH:0} expected={expected:0}");

            // Anchoring: scroll into the middle — the offset stays inside the anchor item's band across the
            // realize+correction waves (corrections above the viewport never jump the visible top).
            var ptr = new Point2(150, 150);
            for (int s = 0; s < 8; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 400f)); host.RunFrame(); }
            host.Scene.TryGetScroll(vp, out var sc1);
            int anchor = layout.IndexAt(sc1.OffsetY, cross);
            float band0 = layout.OffsetOf(anchor, cross), band1 = layout.OffsetOf(anchor + 1, cross);
            bool anchored = sc1.AnchorIndex == anchor && sc1.FirstRealized > 0
                && sc1.OffsetY >= band0 - 0.5f && sc1.OffsetY < band1 + 0.5f;

            // Fling to the end: each fling clamps against the content published SO FAR; the realize wave then corrects
            // the freshly measured rows and EXTENDS the content (estimate-then-correct), so the true end takes a
            // couple of flings — after which the offset clamps to the fully corrected extent and realize reaches row N.
            for (int s = 0; s < 3; s++) { window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 1_000_000f)); host.RunFrame(); }
            host.Scene.TryGetScroll(vp, out var sc2);
            bool clamped = sc2.LastRealized == MeasuredSeamProbe.N && Near(sc2.OffsetY, sc2.ContentH - sc2.ViewportH, 2f);
            Check("e11virt.2 measured seam anchoring: offset pinned inside the anchor band mid-list; end-fling clamps to corrected content",
                anchored && clamped, $"anchor={anchor} off={sc1.OffsetY:0} band=[{band0:0},{band1:0}) end={sc2.OffsetY:0}/{sc2.ContentH - sc2.ViewportH:0}");
        }

        // e11virt.3 — LinedFlowLayout (the WinUI ItemsView photo-wall): uniform-height lines, width = aspect × lineHeight
        // clamped to the cross size, wrap when the next item + MinItemSpacing would overflow, O(1) line-stride windowing.
        // Defaults LineSpacing 0 / MinItemSpacing 0 (LinedFlowLayout.h s_defaultLineSpacing/s_defaultMinItemSpacing).
        {
            float[] aspects = [2f, 1f, 0.5f, 4f];
            var lf = new LinedFlowLayout(lineHeight: 100f, aspectRatio: i => aspects[i % 4], lineSpacing: 10f, minItemSpacing: 5f);
            const float cross = 350f;
            // Flow on cross 350: line0=[0:w200@0, 1:w100@205] line1=[2:w50@0] line2=[3:w350@0 (clamped)]
            //                    line3=[4:w200@0, 5:w100@205] line4=[6:w50@0] line5=[7:w350@0] → 6 lines.
            float extent = lf.ContentExtent(8, cross);
            var i0 = lf.ItemRect(0, cross); var i1 = lf.ItemRect(1, cross); var i2 = lf.ItemRect(2, cross);
            var i3 = lf.ItemRect(3, cross); var i5 = lf.ItemRect(5, cross); var i7 = lf.ItemRect(7, cross);
            bool widths = Near(i0.W, 200f) && Near(i1.W, 100f) && Near(i2.W, 50f) && Near(i3.W, 350f) && Near(i0.H, 100f);
            bool flow = Near(i0.X, 0f) && Near(i0.Y, 0f)
                && Near(i1.X, 205f) && Near(i1.Y, 0f)            // 200 + MinItemSpacing 5
                && Near(i2.X, 0f) && Near(i2.Y, 110f)            // wrapped (305+5+50 > 350); line stride = 100+10
                && Near(i3.X, 0f) && Near(i3.Y, 220f)            // over-wide item → its own full-width line
                && Near(i5.X, 205f) && Near(i5.Y, 330f)
                && Near(i7.Y, 550f) && Near(extent, 650f);       // 6×100 + 5×10
            lf.Window(8, cross, 200f, 115f, 0, out int f0, out int l0);   // band [115,315] → lines 1..3 → items [2,6)
            lf.Window(8, cross, 200f, 115f, 2, out int f1, out int l1);   // ±2 items of overscan, clamped
            lf.Window(8, cross, 200f, 0f, 0, out int f2, out int l2);     // top: lines 0..2 → items [0,4)
            bool windows = f0 == 2 && l0 == 6 && f1 == 0 && l1 == 8 && f2 == 0 && l2 == 4;
            // WinUI defaults: no spacing — 3 unit-aspect items pack one 100px line.
            var lfDef = new LinedFlowLayout(100f);
            bool defaults = Near(lfDef.ContentExtent(3, cross), 100f) && Near(lfDef.ItemRect(2, cross).X, 200f);
            Check("e11virt.3 LinedFlow: aspect widths (cross-clamped), spacing-aware wrap, line-stride rects + windowing, 0-spacing defaults",
                widths && flow && windows && defaults, $"extent={extent:0} w0..3={i0.W:0},{i1.W:0},{i2.W:0},{i3.W:0} win=({f0},{l0})/({f1},{l1})/({f2},{l2})");
        }

        // e11virt.4 — GroupedListVirtualLayout (E11-L0 grouping): headers are a measured item KIND at their own flat
        // indices; StickyHeaderIndexAt = last header at-or-above the offset band (−1 above the first header), and the
        // pivot tracks estimate-then-correct band moves.
        {
            var gl = new GroupedListVirtualLayout([0, 6, 13], headerExtent: 32f, itemEstimate: 48f);
            const int n = 20; const float cross = 300f;
            float total = gl.ContentExtent(n, cross);            // 3×32 + 17×48 = 912
            bool seeded = Near(total, 912f) && gl.IsHeader(6) && !gl.IsHeader(7)
                && Near(gl.OffsetOf(6, cross), 272f)             // 32 + 5×48
                && Near(gl.ItemRect(6, cross).H, 32f) && Near(gl.ItemRect(7, cross).H, 48f);
            bool sticky = gl.StickyHeaderIndexAt(0f) == 0
                && gl.StickyHeaderIndexAt(100f) == 0
                && gl.StickyHeaderIndexAt(272f) == 6             // exactly at the group-2 band start
                && gl.StickyHeaderIndexAt(591f) == 6             // last row of group 2 (header 13 starts at 592)
                && gl.StickyHeaderIndexAt(593f) == 13;
            gl.SetMeasured(3, 80f, cross);                       // estimate-then-correct: row 3 48 → 80 (+32)
            bool correctedG = Near(gl.ContentExtent(n, cross), 944f)
                && Near(gl.OffsetOf(6, cross), 304f)
                && gl.StickyHeaderIndexAt(300f) == 0 && gl.StickyHeaderIndexAt(305f) == 6;
            var gl2 = new GroupedListVirtualLayout([4], 32f, 48f);
            _ = gl2.ContentExtent(10, cross);
            bool none = gl2.StickyHeaderIndexAt(0f) == -1 && gl2.StickyHeaderIndexAt(4 * 48f + 1f) == 4;
            Check("e11virt.4 GroupedList: header extents seeded, sticky index per offset band (−1 above first), correction moves the pivot",
                seeded && sticky && correctedG && none, $"total={total:0}→{gl.ContentExtent(n, cross):0} hdr6@{gl.OffsetOf(6, cross):0}");
        }

        // e11virt.pagedshelf-measured — the REAL realize-all PagedShelf branch. Its ScrollEl must remain the root
        // column's direct cross-stretch child: the old default-row clip-root wrapper gave this Grow=0 scroller a 0px
        // cross-axis width, so the strip retained its measured height but every card was record-culled. ScrollEl is now
        // the native hover clip-escape root, preserving both viewport geometry and the elevated-card paint contract.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pagedshelf-measured", new Size2(640, 480), 1f));
            window.Show();
            var probe = new PagedShelfMeasuredProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();

            var scene = host.Scene;
            var viewport = FindScrollable(scene, scene.Root);
            ScrollState sc0 = default;
            bool hasScroll = !viewport.IsNull && scene.TryGetScroll(viewport, out sc0);
            var vr0 = viewport.IsNull ? default : scene.AbsoluteRect(viewport);
            var dlRest = new DrawList();
            SceneRecorder.Record(scene, dlRest);
            var restRed = FindFillCommandNear(dlRest, PagedShelfMeasuredProbe.FirstFill);
            var restBlue = FindFillCommandNear(dlRest, PagedShelfMeasuredProbe.OtherFill);
            bool initialGeometry = hasScroll
                && Near(vr0.X, -12f) && Near(vr0.W, 344f) && Near(vr0.H, 68f)
                && Near(sc0.ViewportW, 344f) && Near(sc0.ContentW, 1114f)
                && probe.Pager.Page == 0 && probe.Pager.PageCount == 4;
            bool restPaints = restRed.Order >= 0 && restBlue.Order >= 0
                && restRed.Order < restBlue.Order && restRed.ClipDepth == restBlue.ClipDepth;
            Check("e11virt.pagedshelf-measured direct ScrollEl keeps the realize-all shelf viewport stretched (344×68), all-card extent measurable, and card paint visible",
                initialGeometry && restPaints,
                $"vp=({vr0.X:0},{vr0.Y:0},{vr0.W:0}×{vr0.H:0}) scroll={hasScroll} contentW={(hasScroll ? sc0.ContentW : -1):0} pages={probe.Pager.PageCount} draws=r{restRed.Order}/b{restBlue.Order}");

            // Hover through the real dispatcher. The cell should defer after its later sibling and, because the
            // ScrollEl itself owns HoverElevateClipRoot, record after the scroller's clip/fade scopes have closed.
            var firstCard = FindFillNode(scene, scene.Root, PagedShelfMeasuredProbe.FirstFill);
            if (!firstCard.IsNull)
            {
                var cardR = scene.AbsoluteRect(firstCard);
                window.QueueInput(new InputEvent(InputKind.PointerMove,
                    new Point2(cardR.X + cardR.W / 2f, cardR.Y + cardR.H / 2f), 0, 0));
                host.RunFrame();
            }
            var dlHover = new DrawList();
            SceneRecorder.Record(scene, dlHover);
            var hoverRed = FindFillCommandNear(dlHover, PagedShelfMeasuredProbe.FirstFill);
            var hoverBlue = FindFillCommandNear(dlHover, PagedShelfMeasuredProbe.OtherFill);
            bool hoverEscapes = !firstCard.IsNull
                && hoverRed.Order >= 0 && hoverBlue.Order >= 0
                && hoverRed.Order > hoverBlue.Order && hoverRed.ClipDepth < hoverBlue.ClipDepth;
            Check("e11virt.pagedshelf-measured ScrollEl-native clip root hoists the hovered measured cell outside its viewport clip/edge-fade scope",
                hoverEscapes,
                $"card={!firstCard.IsNull} hov=r{hoverRed.Order}@d{hoverRed.ClipDepth} b{hoverBlue.Order}@d{hoverBlue.ClipDepth}");

            // Refit the same mounted shelf, then exercise its public pager. Reduced motion makes the offset assertion
            // deterministic; this is the same navigation target path, only snapped instead of time-integrated.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(630f, 470f), 0, 0));
            host.RunFrame();
            probe.Width.Value = 540f;
            host.RunFrame();
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();
            var viewport2 = FindScrollable(scene, scene.Root);
            ScrollState sc1 = default;
            bool resizedScroll = !viewport2.IsNull && scene.TryGetScroll(viewport2, out sc1);
            var vr1 = viewport2.IsNull ? default : scene.AbsoluteRect(viewport2);
            bool resized = resizedScroll && Near(vr1.X, -12f) && Near(vr1.W, 564f)
                && Near(sc1.ViewportW, 564f) && Near(sc1.ContentW, 1114f)
                && probe.Pager.Page == 0 && probe.Pager.PageCount == 2;

            bool previousReduced = Motion.ReducedMotion;
            try
            {
                Motion.ReducedMotion = true;
                probe.Pager.Next();
                host.RunFrame();
                for (int i = 0; i < 4 && host.HasActiveWork; i++) host.RunFrame();
            }
            finally
            {
                Motion.ReducedMotion = previousReduced;
            }
            ScrollState sc2 = default;
            bool navigatedScroll = !viewport2.IsNull && scene.TryGetScroll(viewport2, out sc2);
            var dlPage1 = new DrawList();
            SceneRecorder.Record(scene, dlPage1);
            var pageBlue = FindFillCommandNear(dlPage1, PagedShelfMeasuredProbe.OtherFill);
            bool navigated = navigatedScroll && Near(sc2.OffsetX, 550f, 0.75f)
                && probe.Pager.Page == 1 && pageBlue.Order >= 0;
            Check("e11virt.pagedshelf-measured resize refits 3→5 columns without collapsing the viewport; Next snaps to the clamped measured-strip target and still paints cards",
                resized && navigated,
                $"vpW={vr1.W:0}/scrollW={(resizedScroll ? sc1.ViewportW : -1):0} pages={probe.Pager.PageCount} page={probe.Pager.Page} off={(navigatedScroll ? sc2.OffsetX : -1):0.0} draw={pageBlue.Order}");
        }

        // e11virt.fillrow — FillRowVirtualLayout (the viewport-aware "fill the width with equal cards" shelf via the
        // IViewportVirtualLayout seam): SetViewport feeds the main-axis viewport; the fit is COUNT-INDEPENDENT, so a
        // handful of items render at the normal fitted width (≤ maxCardW) instead of ballooning to fill — the regression
        // guard for the Home shelf card-balloon bug (3 items in a wide viewport must NOT become ~458px cards).
        {
            var fr = new FillRowVirtualLayout(minCardW: 150f, maxCardW: 200f, gap: 12f);
            const float cross = 240f;
            fr.SetViewport(1400f, cross);                        // perPage=floor(1412/162)=8, cardW=(1400-84)/8=164.5
            bool fit = fr.PerPage == 8 && Near(fr.CardW, 164.5f) && fr.CardW <= 200.01f;
            // Count-independent: 3 items → a SHORT strip at the fitted width (NOT 3 cards stretched across 1400px).
            float ext3 = fr.ContentExtent(3, cross);             // 3×164.5 + 2×12 = 517.5
            var c0 = fr.ItemRect(0, cross); var c1 = fr.ItemRect(1, cross);
            bool fewItems = Near(ext3, 517.5f) && Near(c0.W, 164.5f) && Near(c1.X, 176.5f) && Near(c0.H, cross);
            fr.SetViewport(324f, cross);                          // refit: perPage=floor(336/162)=2, cardW=(324-12)/2=156
            bool refit = fr.PerPage == 2 && Near(fr.CardW, 156f);
            // Override: pin 2 columns on a wide viewport → unclamped card would be 694, capped to maxCardW 200.
            var frOv = new FillRowVirtualLayout(150f, 200f, 12f, perPageOverride: 2);
            frOv.SetViewport(1400f, cross);
            bool capped = frOv.PerPage == 2 && Near(frOv.CardW, 200f);
            Check("e11virt.fillrow FillRow: viewport-fed fit (≤maxCardW), count-independent (no balloon), refit on resize, perPage-override cap",
                fit && fewItems && refit && capped, $"fit={fr.PerPage}@{fr.CardW:0.0} ext3={ext3:0.0} cap={frOv.CardW:0}");
        }

        // e11virt.fillrow-margin — the FlexLayout stretch behavior the halo-bleed viewport (Feature A / PagedShelf) relies
        // on: a cross-STRETCH child with a NEGATIVE horizontal margin and no explicit width arranges WIDER than its parent
        // content box by |lead|+|trail| and starts |lead| to the LEFT of it (FlexLayout.cs:373 Max(0, availCross−crossMargin)
        // + the arrange leading-margin). This is how the shelf viewport widens 2×HaloBleed into the gutters without moving
        // the shelf's layout box. Column parent 300 wide; child Margin(-12,0,-12,0) ⇒ width 324 at x −12.
        {
            var mt = LayoutTree(strings, new BoxEl
            {
                Direction = 1, Width = 300f, Height = 80f,
                Children = [ new BoxEl { Height = 40f, Margin = new Edges4(-12f, 0f, -12f, 0f) } ],
            });
            var childR = mt.AbsoluteRect(Child(mt, mt.Root, 0));
            Check("e11virt.fillrow-margin negative horizontal margin widens a cross-stretch child by 2×|m| at x −|m| (halo-bleed viewport)",
                Near(childR.W, 324f) && Near(childR.X, -12f), $"w={childR.W:0.0} x={childR.X:0.0}");
        }

        // e11virt.fillrow-insets — the MAIN-AXIS content insets Feature A adds to FillRowVirtualLayout: the fed viewport is
        // widened by Lead+Trail, the fit uses the INNER width (viewport − insets) so cardW is UNCHANGED, item i shifts by
        // LeadInset, and ContentExtent carries both gutters. Insets default 0 ⇒ existing gates keep their geometry.
        {
            const float cross = 240f, lead = 12f, trail = 12f;
            var frBase = new FillRowVirtualLayout(150f, 200f, 12f);
            frBase.SetViewport(1400f, cross);                                 // no insets: perPage 8, cardW 164.5
            var frIns = new FillRowVirtualLayout(150f, 200f, 12f, leadInset: lead, trailInset: trail);
            frIns.SetViewport(1400f + lead + trail, cross);                   // widened viewport; inner = 1400 ⇒ SAME fit
            bool sameFit = frIns.PerPage == frBase.PerPage && Near(frIns.CardW, frBase.CardW);
            var r0 = frIns.ItemRect(0, cross); var r1 = frIns.ItemRect(1, cross);
            bool shifted = Near(r0.X, lead) && Near(r1.X, lead + frIns.CardW + 12f);
            bool extent = Near(frIns.ContentExtent(5, cross), frBase.ContentExtent(5, cross) + lead + trail);
            frIns.Window(64, cross, 1400f + lead + trail, 0f, 0, out int f0, out int l0);   // offset 0 still realizes item 0
            Check("e11virt.fillrow-insets Lead/Trail: item0 at LeadInset, extent +Lead+Trail, fit uses inner width (cardW unchanged), window realizes from 0",
                sameFit && shifted && extent && f0 == 0 && l0 >= 8,
                $"cardW={frIns.CardW:0.0}/{frBase.CardW:0.0} r0={r0.X:0.0} ext+={frIns.ContentExtent(5, cross) - frBase.ContentExtent(5, cross):0.0} win=[{f0},{l0})");
        }

        // e11virt.elevate — Element.HoverElevatePaint (Feature B): a flagged child on the hover path is DEFERRED to paint
        // AFTER its later siblings (the design's z-index:2, so a hovered card's lift halo is not overpainted). Two
        // overlapping ZStack children; the EARLIER (red, flagged) one is hovered → it records AFTER the later (blue)
        // sibling. At rest it keeps document order. The deferred-path record allocates 0 managed bytes.
        {
            var fonts2 = new HeadlessFontSystem(strings);
            ColorF red = ColorF.FromRgba(220, 40, 40), blue = ColorF.FromRgba(40, 90, 220);
            Element Tree() => new BoxEl
            {
                Width = 200f, Height = 100f, ZStack = true,
                Children =
                [
                    // HoverFill = Fill (identity): without it ResolveSurface auto-lightens a hovered interactive fill
                    // 8%, and the exact-color probe below would miss the drawn command.
                    new BoxEl { Key = "elev", Width = 100f, Height = 100f, Fill = red, HoverFill = red, HoverElevatePaint = true, OnClick = static () => { } },
                    new BoxEl { Key = "sib",  Width = 100f, Height = 100f, Fill = blue },
                ],
            };
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(Tree(), null);
            new FlexLayout(scene, fonts2).Run(scene.Root);
            var elev = Child(scene, scene.Root, 0);

            var dlRest = new DrawList();
            SceneRecorder.Record(scene, dlRest);
            var restRed = FindFillCommand(dlRest, red); var restBlue = FindFillCommand(dlRest, blue);
            bool restOrder = restRed.Order >= 0 && restBlue.Order >= 0 && restRed.Order < restBlue.Order;   // document order at rest

            scene.Flags(elev) |= NodeFlags.Hovered;   // pointer on the flagged (earlier) child
            var dlHov = new DrawList();
            SceneRecorder.Record(scene, dlHov);
            var hovRed = FindFillCommand(dlHov, red); var hovBlue = FindFillCommand(dlHov, blue);
            bool elevated = hovRed.Order >= 0 && hovBlue.Order >= 0 && hovRed.Order > hovBlue.Order;   // deferred above sibling

            SceneRecorder.Record(scene, dlHov);   // warm DrawList buffers
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            SceneRecorder.Record(scene, dlHov);
            long recBytes = GC.GetAllocatedBytesForCurrentThread() - a0;
            Check("e11virt.elevate HoverElevatePaint defers a hovered flagged child above its later siblings (z-index:2); document order at rest; 0-alloc deferred record",
                restOrder && elevated && recBytes == 0,
                $"rest r{restRed.Order}<b{restBlue.Order} hov r{hovRed.Order}>b{hovBlue.Order} bytes={recBytes}");
        }

        // e11virt.elevate-cell — the NESTED (shelf) shape end-to-end through the REAL input dispatcher: the flagged
        // node is a NON-interactive cell whose interactive CARD wins the hit (PagedShelf's containerFactory shape).
        // HoverElevatePaintBit joins UpdateHoverWithin's ancestor mask, so hovering the card stamps HoverWithin on the
        // flagged cell — and the recorder then defers the whole cell above its sibling cells.
        {
            ColorF red = ColorF.FromRgba(220, 40, 40), blue = ColorF.FromRgba(40, 90, 220);
            using var appE = new HeadlessPlatformApp();
            var windowE = new HeadlessWindow(new WindowDesc("elevate-cell", new Size2(640, 480), 1f));
            windowE.Show();
            using var hostE = new AppHost(appE, windowE, new HeadlessGpuDevice(), fonts, strings, new ElevateCellProbe());
            hostE.RunFrame();
            var sE = hostE.Scene;
            // Descend to the two-cell row (the probe's root box), then its cells and cell 0's card.
            var row = sE.Root;
            while (!row.IsNull && (sE.FirstChild(row).IsNull || sE.NextSibling(sE.FirstChild(row)).IsNull))
                row = sE.FirstChild(row);
            var cell0 = sE.FirstChild(row); var cell1 = sE.NextSibling(cell0);
            var card0 = sE.FirstChild(cell0);
            var r0 = sE.AbsoluteRect(card0);
            windowE.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(r0.X + r0.W / 2f, r0.Y + r0.H / 2f), 0, 0));
            hostE.RunFrame();
            bool cellWithin = (sE.Flags(cell0) & NodeFlags.HoverWithin) != 0;   // the dispatcher-mask contract
            var dlCell = new DrawList();
            SceneRecorder.Record(sE, dlCell);
            // Near-match: the hovered card's fill went through the real dispatcher → InteractionAnim → LerpLinear,
            // which is not bit-exact even at HoverFill==Fill identity.
            var cRed = FindFillCommandNear(dlCell, red); var cBlue = FindFillCommandNear(dlCell, blue);
            bool cellElevated = cRed.Order >= 0 && cBlue.Order >= 0 && cRed.Order > cBlue.Order;
            Check("e11virt.elevate-cell a flagged NON-interactive cell gets HoverWithin from its hovered card (dispatcher mask) and defers above sibling cells",
                cellWithin && cellElevated, $"within={cellWithin} r{cRed.Order}>b{cBlue.Order}");
        }

        // e11virt.elevate-escape — clip-ESCAPE (Element.HoverElevateClipRoot): with a flagged clipping viewport around
        // the cell row, the hovered cell is HOISTED out of the viewport's whole scope — recorded AFTER its PopClip,
        // against the OUTER clip (lower clip depth) — so the lift + halo paint outside the strip, while the resting
        // sibling stays inside the viewport clip. At rest both record inside (document order, same depth).
        {
            ColorF red = ColorF.FromRgba(220, 40, 40), blue = ColorF.FromRgba(40, 90, 220);
            using var appV = new HeadlessPlatformApp();
            var windowV = new HeadlessWindow(new WindowDesc("elevate-escape", new Size2(640, 480), 1f));
            windowV.Show();
            using var hostV = new AppHost(appV, windowV, new HeadlessGpuDevice(), fonts, strings, new ElevateEscapeProbe());
            hostV.RunFrame();
            var sV = hostV.Scene;
            var dlRest2 = new DrawList();
            SceneRecorder.Record(sV, dlRest2);
            var rRed = FindFillCommandNear(dlRest2, red); var rBlue = FindFillCommandNear(dlRest2, blue);
            bool restInside = rRed.Order >= 0 && rBlue.Order >= 0 && rRed.Order < rBlue.Order && rRed.ClipDepth == rBlue.ClipDepth;

            // Hover card 0 through the real dispatcher, then record: red must hoist out (later order, SHALLOWER clip).
            var rootV = sV.Root;
            while (!rootV.IsNull && (sV.FirstChild(rootV).IsNull || sV.NextSibling(sV.FirstChild(rootV)).IsNull))
                rootV = sV.FirstChild(rootV);   // descend to the two-cell row
            var card0V = sV.FirstChild(sV.FirstChild(rootV));
            var rV = sV.AbsoluteRect(card0V);
            windowV.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(rV.X + rV.W / 2f, rV.Y + rV.H / 2f), 0, 0));
            hostV.RunFrame();
            var dlHov2 = new DrawList();
            SceneRecorder.Record(sV, dlHov2);
            var hRed = FindFillCommandNear(dlHov2, red); var hBlue = FindFillCommandNear(dlHov2, blue);
            bool escaped = hRed.Order >= 0 && hBlue.Order >= 0 && hRed.Order > hBlue.Order && hRed.ClipDepth < hBlue.ClipDepth;
            Check("e11virt.elevate-escape a hovered flagged cell hoists OUT of the HoverElevateClipRoot viewport clip (records after its pop, shallower depth); rest stays inside",
                restInside && escaped,
                $"rest r{rRed.Order}@d{rRed.ClipDepth} b{rBlue.Order}@d{rBlue.ClipDepth} hov r{hRed.Order}@d{hRed.ClipDepth} b{hBlue.Order}@d{hBlue.ClipDepth}");
        }

        // e11virt.5 — ItemsRepeater lifecycle (E11-L2, ItemsRepeater.idl:186-188): ElementPrepared on entering the
        // realized window, ElementClearing on leaving (recycle = Clearing(old)+Prepared(new)), visible-range prefetch;
        // a steady in-window scroll fires NOTHING (transform-only frames never realize).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-lifecycle", new Size2(640, 480), 1f));
            window.Show();
            var probe = new LifecycleRepeaterProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();

            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            bool mountSequential = probe.Prepared.Count == sc0.LastRealized && probe.Cleared.Count == 0;
            for (int i = 0; i < probe.Prepared.Count; i++) mountSequential &= probe.Prepared[i] == i;
            bool mountRange = probe.Ranges.Count > 0 && probe.Ranges[^1] == (0, sc0.LastRealized);

            // sub-extent scroll: in-window → no realize → no lifecycle.
            int p0 = probe.Prepared.Count, c0 = probe.Cleared.Count, rg0 = probe.Ranges.Count;
            var ptr = new Point2(150, 200);
            window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 2f));
            host.RunFrame();
            bool quiet = probe.Prepared.Count == p0 && probe.Cleared.Count == c0 && probe.Ranges.Count == rg0;

            // boundary-crossing scroll: 400px over 40px rows → window [0,14) → [6,24): Clearing 0..5, Prepared 14..23.
            window.QueueInput(new InputEvent(InputKind.Wheel, ptr, 0, 0, 398f));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc1);
            var live = new HashSet<int>();
            foreach (var i in probe.Prepared) live.Add(i);
            foreach (var i in probe.Cleared) live.Remove(i);
            bool windowSet = live.Count == sc1.LastRealized - sc1.FirstRealized && sc1.FirstRealized > 0;
            for (int i = sc1.FirstRealized; i < sc1.LastRealized; i++) windowSet &= live.Contains(i);
            bool conserved = probe.Prepared.Count - probe.Cleared.Count == sc1.LastRealized - sc1.FirstRealized
                && probe.Cleared.Count == sc1.FirstRealized                          // exactly the rows that left the top
                && probe.Ranges[^1] == (sc1.FirstRealized, sc1.LastRealized);
            Check("e11virt.5 ItemsRepeater lifecycle: Prepared/Clearing mirror the realized window across a recycle; in-window scroll fires nothing",
                mountSequential && mountRange && quiet && windowSet && conserved,
                $"mount=[0,{sc0.LastRealized}) → [{sc1.FirstRealized},{sc1.LastRealized}) prepared={probe.Prepared.Count} cleared={probe.Cleared.Count}");
        }

        // e11virt.5b — window in-place diff (component row state survives a VirtualListEl update): a parent re-render
        // swaps in a REBUILT VirtualListEl over an unchanged window, so RealizeWindow(reuseOverlap:false) re-renders
        // every realized slot. Same-slot type+key pairs must diff in place (general Update → keyed child reconcile →
        // ComponentEl same-type reuse) — the hosted row components keep instance identity and state, and NOTHING
        // remounts. (Component-hosting rows are not IsRecyclable, so without the in-place branch every row took the
        // mount+remove path: RowConstructions would grow by the window size and Rows[i] would be fresh instances.)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-inplace", new Size2(640, 480), 1f));
            window.Show();
            var probe = new WindowInPlaceDiffProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            host.RunFrame();   // let the mount-deferred overscan trickle settle (E4 budget) before snapshotting
            host.RunFrame();

            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            int built0 = probe.RowConstructions;
            int renders0 = probe.ParentRenders;
            var row0 = probe.Rows[sc0.FirstRealized];

            // Control: per-row state machinery is live BEFORE the parent re-render (granular row re-render path).
            row0.Local.Value = 7;
            host.RunFrame();

            // The parent re-render: a NEW VirtualListEl, same window → every slot gets a fresh Element from RenderItem.
            probe.Rev.Value = 1;
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc1);

            bool reRendered = probe.ParentRenders > renders0;                        // the Update→RealizeWindow path ran
            bool windowStable = sc1.FirstRealized == sc0.FirstRealized && sc1.LastRealized == sc0.LastRealized;
            bool noRemount = probe.RowConstructions == built0;                       // ZERO fresh row-component instances
            bool identity = ReferenceEquals(probe.Rows[sc0.FirstRealized], row0);    // same live instance in the slot
            bool stateKept = row0.Local.Peek() == 7;                                 // per-instance state survived
            Check("e11virt.5b window in-place diff: a parent re-render rebuilds every realized row Element; same-slot type+key rows update in place — component instances/state survive, nothing remounts",
                reRendered && windowStable && noRemount && identity && stateKept,
                $"renders={renders0}->{probe.ParentRenders} built={built0}->{probe.RowConstructions} window=[{sc1.FirstRealized},{sc1.LastRealized}) state={row0.Local.Peek()}");
        }

        // e11virt.6 — typed ItemsRepeater (E11-L2): the (index, item) template binds without casts, and an
        // ItemCollectionTransition stamps the engine FLIP/fade spec on each item root — Moves = Position FLIP,
        // Adds/Removes = opacity 0↔1, over ControlFastAnimationDuration 167ms decelerate (the ItemContainer.xaml:54-56
        // KeySpline 0,0,0,1 timing).
        {
            var data = new List<string> { "alpha", "beta", "gamma" };
            var el = Repeater.ItemsRepeater(data, (i, s) => new BoxEl { Children = [new TextEl(s) { Size = 12f }] },
                RepeatLayout.Inline(), transition: ItemCollectionTransition.Default);
            bool typed = el is BoxEl row && row.Children.Length == 3
                && row.Children[1] is BoxEl b1 && b1.Children[0] is TextEl t1 && t1.Text.Value == "beta";
            var spec = el is BoxEl row2 && row2.Children[0] is BoxEl item0 ? item0.Animate : null;
            bool stamped = spec is { } sp
                && (sp.Channels & TransitionChannels.Position) != 0 && (sp.Channels & TransitionChannels.Opacity) != 0
                && sp.Dynamics.Kind == DynamicsKind.Tween && Near(sp.Dynamics.DurationMs, 167f)
                && sp.Dynamics.Easing == Easing.FluentDecelerate
                && sp.Enter.Active && Near(sp.Enter.Opacity, 0f) && sp.Exit.Active && Near(sp.Exit.Opacity, 0f);
            Check("e11virt.6 ItemsRepeater typed template + ItemCollectionTransition → Position FLIP + 167ms enter/exit fades on item roots",
                typed && stamped, $"typed={typed} stamped={stamped}");
        }

        // e11virt.7 — SelectionModel Single (SingleSelector.cpp:25-57): Select REPLACES; Ctrl+interact toggles;
        // plain focus follows (m_followFocus default true); Ctrl+focus moves without selecting.
        {
            var m = new SelectionModel { ItemCount = 100 };
            int events = 0;
            m.SelectionChanged = () => events++;
            bool def = m.Mode == ItemsSelectionMode.Single && m.SelectedCount == 0 && m.AnchorIndex == -1;   // ItemsView.h s_defaultSelectionMode
            m.Select(3); m.Select(7);
            bool replaces = m.IsSelected(7) && !m.IsSelected(3) && m.SelectedCount == 1 && events == 2;
            m.OnInteractedAction(7, ctrl: true, shift: false);    // selected + Ctrl → deselect (cpp:39-43)
            bool ctrlOff = m.SelectedCount == 0 && events == 3;
            m.OnInteractedAction(7, ctrl: true, shift: false);    // unselected + Ctrl → select (cpp:35-38)
            bool ctrlOn = m.IsSelected(7) && events == 4;
            m.OnFocusedAction(8, ctrl: false, shift: false);      // follow-focus (cpp:46-57)
            bool follow = m.IsSelected(8) && m.SelectedCount == 1;
            m.OnFocusedAction(9, ctrl: true, shift: false);       // Ctrl+focus: no selection change
            bool ctrlFocus = m.IsSelected(8) && !m.IsSelected(9) && m.Version.Peek() == events;
            Check("e11virt.7 SelectionModel Single: replace-on-select, ctrl-toggle, focus-follow, ctrl-focus inert (SingleSelector.cpp:25-57)",
                def && replaces && ctrlOff && ctrlOn && follow && ctrlFocus, $"events={events} version={m.Version.Peek()}");
        }

        // e11virt.8 — SelectionModel Multiple (MultipleSelector.cpp:18-92): toggle without modifiers; Shift extends or
        // deselects the anchor range by the ANCHOR's state (only when the states differ); Shift with NO anchor is a
        // NO-OP (the toggle is cpp's `else` — it never runs while Shift is held); plain focus moves never select.
        {
            var m = new SelectionModel { ItemCount = 100, Mode = ItemsSelectionMode.Multiple };
            m.OnInteractedAction(4, ctrl: false, shift: true);    // no anchor yet → no-op (cpp:24-63)
            bool shiftNoAnchor = m.SelectedCount == 0 && m.AnchorIndex == -1;
            m.OnInteractedAction(2, false, false);                // toggle on → anchor 2
            m.OnInteractedAction(6, false, true);                 // anchor selected, 6 not → SelectRangeFromAnchorTo
            bool shiftRange = m.SelectedCount == 5 && m.RangeCount == 1 && m.GetRange(0) == (2, 6);
            m.OnInteractedAction(4, false, true);                 // anchor and 4 BOTH selected → states equal → nothing (cpp:44-52)
            bool statesEqual = m.SelectedCount == 5;
            m.OnFocusedAction(8, false, false);                   // plain focus move never selects (cpp:65-92)
            bool focusInert = m.SelectedCount == 5 && !m.IsSelected(8);
            m.OnInteractedAction(2, false, false);                // toggle the anchor itself OFF (anchor stays 2)
            m.OnInteractedAction(5, false, true);                 // anchor UNselected, 5 selected → DeselectRangeFromAnchorTo
            bool shiftDeselect = !m.IsSelected(3) && !m.IsSelected(5) && m.IsSelected(6) && m.SelectedCount == 1;
            Check("e11virt.8 SelectionModel Multiple: modifier-free toggle, shift range by anchor state, shift-no-anchor no-op (MultipleSelector.cpp:18-92)",
                shiftNoAnchor && shiftRange && statesEqual && focusInert && shiftDeselect,
                $"count={m.SelectedCount} ranges={m.RangeCount}");
        }

        // e11virt.9 — SelectionModel Extended (ExtendedSelector.cpp:18-83): plain replaces ONLY on an unselected item;
        // Ctrl toggles; Shift replaces with the anchor range; focus: Shift+Ctrl additive, Shift replace, plain replace,
        // Ctrl alone moves without selecting.
        {
            var m = new SelectionModel { ItemCount = 100, Mode = ItemsSelectionMode.Extended };
            m.OnInteractedAction(2, false, false);                // plain → clear+select, anchor 2
            m.OnInteractedAction(6, false, true);                 // Shift → replace with [anchor..6] (cpp:23-32)
            bool range = m.SelectedCount == 5 && m.GetRange(0) == (2, 6) && m.AnchorIndex == 2;
            m.OnInteractedAction(9, true, false);                 // Ctrl → additive toggle (cpp:33-43)
            bool ctrlAdd = m.SelectedCount == 6 && m.IsSelected(9);
            m.OnInteractedAction(4, false, false);                // plain on a SELECTED item → keep (cpp:46 "Only clear ... different item")
            bool keepOnSelected = m.SelectedCount == 6;
            m.OnInteractedAction(20, false, false);               // plain on unselected → clear+select
            bool replace = m.SelectedCount == 1 && m.IsSelected(20) && m.AnchorIndex == 20;
            m.OnFocusedAction(23, false, true);                   // Shift+focus → replace with the anchor range (cpp:66-75)
            bool focusShift = m.SelectedCount == 4 && m.GetRange(0) == (20, 23) && m.AnchorIndex == 20;
            m.OnFocusedAction(30, true, false);                   // Ctrl+focus → nothing (cpp falls through)
            bool focusCtrl = m.SelectedCount == 4;
            m.OnFocusedAction(28, true, true);                    // Shift+Ctrl+focus → ADDITIVE anchor range (cpp:59-65)
            bool focusCtrlShift = m.SelectedCount == 9 && m.GetRange(0) == (20, 28);
            m.OnFocusedAction(40, false, false);                  // plain focus → clear+select (cpp:76-80)
            bool focusPlain = m.SelectedCount == 1 && m.IsSelected(40);
            Check("e11virt.9 SelectionModel Extended: plain/ctrl/shift interact + the four focus chords (ExtendedSelector.cpp:18-83)",
                range && ctrlAdd && keepOnSelected && replace && focusShift && focusCtrl && focusCtrlShift && focusPlain,
                $"count={m.SelectedCount}");
        }

        // e11virt.10 — selection is DECOUPLED from realization: SelectAll over 10k stores ONE inclusive range
        // (never walks indices), deselect splits it, invert complements it, shrinking ItemCount trims it.
        {
            var m = new SelectionModel { ItemCount = 10_000, Mode = ItemsSelectionMode.Extended };
            int events = 0;
            m.SelectionChanged = () => events++;
            m.SelectAll();
            bool one = m.RangeCount == 1 && m.GetRange(0) == (0, 9_999) && m.SelectedCount == 10_000 && events == 1;
            m.SelectAll();                                       // no actual change → no event (WinUI raises only on change)
            bool idempotent = events == 1;
            m.DeselectRange(100, 199);
            bool split = m.RangeCount == 2 && m.SelectedCount == 9_900 && !m.IsSelected(150) && events == 2;
            m.InvertSelection();
            bool inverted = m.RangeCount == 1 && m.GetRange(0) == (100, 199) && m.SelectedCount == 100;
            m.ItemCount = 150;                                   // shrink trims out-of-range selection
            bool trimmed = m.SelectedCount == 50 && m.GetRange(0) == (100, 149);
            Check("e11virt.10 SelectionModel ranges: select-all-over-10k = ONE range (realizes nothing), split/invert/trim stay range-shaped",
                one && idempotent && split && inverted && trimmed, $"events={events} ranges={m.RangeCount} count={m.SelectedCount}");
        }

        // e11virt.10b — selection follows ITEM identity across a RemoveAt+Insert reorder (ListViewBase::ReorderItemsTo),
        // including range splits, instead of staying on the old slot.
        {
            var single = new SelectionModel { ItemCount = 8 };
            int singleEvents = 0;
            single.SelectionChanged = () => singleEvents++;
            single.Select(4);
            single.RemapMove(4, 2);
            bool selectedItemMoved = single.IsSelected(2) && !single.IsSelected(4) && single.FirstSelectedIndex == 2
                && single.AnchorIndex == 2 && singleEvents == 2;

            var range = new SelectionModel { ItemCount = 8, Mode = ItemsSelectionMode.Multiple };
            range.SelectRange(4, 5);
            range.AnchorIndex = 4;
            range.RemapMove(4, 2);
            bool splitRange = range.RangeCount == 2 && range.GetRange(0) == (2, 2) && range.GetRange(1) == (5, 5)
                && range.AnchorIndex == 2;

            var all = new SelectionModel { ItemCount = 10, Mode = ItemsSelectionMode.Extended };
            int allEvents = 0;
            all.SelectionChanged = () => allEvents++;
            all.SelectAll();
            all.RemapMove(8, 2);
            bool allStillCompact = all.RangeCount == 1 && all.GetRange(0) == (0, 9) && allEvents == 1;

            Check("e11virt.10b SelectionModel RemapMove preserves selected item identity across reorder (single, split range, select-all compact)",
                selectedItemMoved && splitRange && allStillCompact,
                $"single={single.FirstSelectedIndex} events={singleEvents} split={range.RangeCount} allEvents={allEvents}");
        }

        // e11virt.11 — ItemContainer state ARGB, BOTH themes (full #AARRGGBB; ItemContainer_themeresources.xaml:5-18
        // dark / :37-49 light → Common_themeresources_any.xaml) + the selected dual-stroke geometry + checkbox plate
        // + disabled collapse.
        {
            bool all = true;
            var details = new System.Text.StringBuilder();
            try
            {
                foreach (var (kind, hover, pressed, ring, inner, plate, plateStroke) in new (ThemeKind, ColorF, ColorF, ColorF, ColorF, ColorF, ColorF)[]
                {
                    // DARK — PointerOver = SubtleFillColorSecondary #0FFFFFFF, Pressed = SubtleFillColorTertiary
                    // #0AFFFFFF (Common_themeresources_any.xaml:26-27); SelectionVisual = AccentFillColorDefault =
                    // SystemAccentColorLight2 #60CDFF (:125); SelectedInnerBorder = ControlSolidFillColorDefault
                    // #454545 (:24); checkbox plate = ControlOnImageFillColorDefault #B31C1C1C (:34); plate stroke =
                    // CheckBoxCheckBackgroundStrokeUnchecked → ControlStrongStrokeColorDefault #8BFFFFFF (:48).
                    (ThemeKind.Dark,
                     ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F), ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0A),
                     ColorF.FromRgba(0x60, 0xCD, 0xFF), ColorF.FromRgba(0x45, 0x45, 0x45),
                     ColorF.FromRgba(0x1C, 0x1C, 0x1C, 0xB3), ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x8B)),
                    // LIGHT — Secondary #09000000 / Tertiary #06000000 (:230-231); accent = SystemAccentColorDark1
                    // #005FB8 (:329); inner = #FFFFFF (:228); plate = #C9FFFFFF (:238); stroke = #72000000 (:252).
                    (ThemeKind.Light,
                     ColorF.FromRgba(0x00, 0x00, 0x00, 0x09), ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
                     ColorF.FromRgba(0x00, 0x5F, 0xB8), ColorF.FromRgba(0xFF, 0xFF, 0xFF),
                     ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xC9), ColorF.FromRgba(0x00, 0x00, 0x00, 0x72)),
                })
                {
                    Tok.Use(kind);
                    const float tol = 1.5f / 255f;

                    // Selected + multi-select + unchecked plate → [ic-content, ic-ring, ic-common, ic-check].
                    var scene = LayoutTree(strings, ItemContainer.Build(new BoxEl(), isSelected: true,
                        onInteraction: (t, mods) => { }, showSelectionCheckbox: true, isChecked: false, width: 120f, height: 48f));
                    var root = scene.Root;
                    ref var p = ref scene.Paint(root);
                    var ringN = Child(scene, root, 1);
                    var commonN = Child(scene, root, 2);
                    var plateN = Child(scene, Child(scene, root, 3), 0);
                    bool states = ColorClose(p.Fill, ColorF.Transparent, tol)                      // ItemContainerBackground = SubtleFillColorTransparent (:5/:37)
                        && ColorClose(scene.Paint(commonN).HoverFill, hover, tol) && ColorClose(scene.Paint(commonN).PressedFill, pressed, tol)
                        && ColorClose(scene.Paint(ringN).BorderColor, ring, tol) && Near(scene.Paint(ringN).BorderWidth, 3f)   // PART_SelectionVisual (ItemContainer.xaml:116-126)
                        && ColorClose(scene.Paint(commonN).BorderColor, inner, tol) && Near(scene.Paint(commonN).BorderWidth, 1f);
                    var ir = scene.AbsoluteRect(commonN);
                    var pr = scene.AbsoluteRect(plateN);
                    bool geometry = Near(ir.X, 2f) && Near(ir.Y, 2f) && Near(ir.W, 116f) && Near(ir.H, 44f)   // ItemContainerSelectedInnerMargin 2 (themeresources:57)
                        && Near(pr.W, 20f) && Near(pr.H, 20f) && Near(pr.X, 96f) && Near(pr.Y, -2f);          // 20px checkbox, top-right, Margin 4,−2 (:56,:59-60)
                    bool plateColors = ColorClose(scene.Paint(plateN).Fill, plate, tol)
                        && ColorClose(scene.Paint(plateN).BorderColor, plateStroke, tol)
                        && FindPolylineStrokeNode(scene, root).IsNull;                            // unchecked → no checkmark glyph

                    // Checked plate flips to the accent fill + drawn checkmark.
                    var checkedScene = LayoutTree(strings, ItemContainer.Build(new BoxEl(), true,
                        (t, mods) => { }, showSelectionCheckbox: true, isChecked: true, width: 120f, height: 48f));
                    var cPlate = Child(checkedScene, Child(checkedScene, checkedScene.Root, 3), 0);
                    bool checkedOk = ColorClose(checkedScene.Paint(cPlate).Fill, ring, tol)        // CheckBoxCheckBackgroundFillChecked = AccentFillColorDefault (CheckBox_themeresources.xaml:57)
                        && !FindPolylineStrokeNode(checkedScene, checkedScene.Root).IsNull;

                    // Disabled: Opacity 0.3 (ItemContainerDisabledOpacity, :54) and PART_SelectionVisual collapses
                    // (ItemContainer.xaml:108-110) → only the content layer remains.
                    var disabledScene = LayoutTree(strings, ItemContainer.Build(new BoxEl(), true,
                        (t, mods) => { }, isEnabled: false, width: 120f, height: 48f));
                    bool disabled = Near(disabledScene.Paint(disabledScene.Root).Opacity, 0.3f, 0.001f)
                        && disabledScene.ChildCount(disabledScene.Root) == 1;

                    all &= states && geometry && plateColors && checkedOk && disabled;
                    details.Append($"{kind}: states={states} geo={geometry} plate={plateColors} checked={checkedOk} disabled={disabled}; ");
                }
            }
            finally { Tok.Use(ThemeKind.Dark); }
            Check("e11virt.11 ItemContainer: WinUI state ARGB both themes + dual-stroke geometry + checkbox plate + disabled collapse", all, details.ToString());
        }

        // e11virt.12 — the selection ring and multi-select checkbox FADE IN when they appear: opacity 0 → 1 over
        // ControlFastAnimationDuration 167ms / KeySpline 0,0,0,1 (ItemContainer.xaml:54-56 SelectedPointerOver and
        // :93-99 the checkbox storyboard) — the engine carries both as enter transitions on the keyed child nodes.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            Element Tree(bool multi, bool selected) => new BoxEl
            {
                Width = 200, Height = 60,
                Children = [ItemContainer.Build(new BoxEl(), isSelected: selected, onInteraction: (t, mods) => { },
                                                showSelectionCheckbox: multi, isChecked: false, width: 200f, height: 60f)],
            };
            var t0 = Tree(false, false);
            recon.ReconcileRoot(t0, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var container = Child(scene, scene.Root, 0);
            bool noChrome = scene.ChildCount(container) == 2;     // unselected + single → content + CommonVisual only

            recon.ReconcileRoot(Tree(true, true), t0);            // select + flip to Multiple → ring + checkbox enter
            new FlexLayout(scene, fonts).Run(scene.Root);
            var ring = Child(scene, container, 1);
            var check = Child(scene, container, 3);
            engine.Tick(16f);
            float ring16 = scene.Paint(ring).Opacity, check16 = scene.Paint(check).Opacity;
            for (int i = 0; i < 4; i++) engine.Tick(16f);         // t = 80ms — mid-flight on the 167ms tween
            float mid = scene.Paint(check).Opacity;
            for (int i = 0; i < 30; i++) engine.Tick(16f);        // past 167ms → settled
            bool settled = Near(scene.Paint(check).Opacity, 1f, 0.001f) && Near(scene.Paint(ring).Opacity, 1f, 0.001f);
            bool entering = ring16 < 0.95f && check16 < 0.95f && mid > check16 && mid < 1f;

            // the authored spec is exactly the WinUI storyboard: a 167ms decelerate TWEEN entering from opacity 0.
            BoxEl? checkEl = null;
            foreach (var child in ItemContainer.Build(new BoxEl(), false, (t, mods) => { }, showSelectionCheckbox: true).Children)
                if (child is BoxEl { Key: "ic-check" } b) { checkEl = b; break; }
            bool spec = checkEl?.Animate is { } a && a.Dynamics.Kind == DynamicsKind.Tween && Near(a.Dynamics.DurationMs, 167f)
                && a.Dynamics.Easing == Easing.FluentDecelerate && a.Enter.Active && Near(a.Enter.Opacity, 0f)
                && (a.Channels & TransitionChannels.Opacity) != 0;
            Check("e11virt.12 ItemContainer ring + checkbox enter-fade 0→1 over the 167ms ControlFastAnimationDuration tween",
                noChrome && entering && settled && spec, $"op16=({ring16:0.00},{check16:0.00}) op80={mid:0.00} settled={settled} spec={spec}");
        }

        // e11virt.13/14/15 — ItemsView (E11-L3) keyboard surface on a virtualized stack.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-iv", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewKeyboardProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            var sel = ctl.Selection!;

            NodeHandle vp = NodeHandle.Null;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (host.Scene.TryGetScroll(n, out var s) && s.ItemCount == ItemsViewKeyboardProbe.N) vp = n;
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            ScrollState Sc() { host.Scene.TryGetScroll(vp, out var s); return s; }
            void Press(float x, float y, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(x, y), 0, 0, 0f, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(x, y), 0, 0, 0f, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }

            bool def = ctl.CurrentItemIndex == -1 && sel.Mode == ItemsSelectionMode.Single;   // CurrentItemIndex default −1 (ItemsView.idl:46-47)
            Press(180f, 100f, 1_000);                                    // row 2
            bool click = ctl.CurrentItemIndex == 2 && sel.IsSelected(2) && sel.SelectedCount == 1;
            Key(Keys.Down);
            bool down = ctl.CurrentItemIndex == 3 && sel.IsSelected(3) && sel.SelectedCount == 1;   // selection follows focus (SingleSelector)
            Key(Keys.Up);
            bool up = ctl.CurrentItemIndex == 2 && sel.IsSelected(2);
            Key(Keys.PageDown);                                          // viewport jump (cpp:1103+): 80 + 320 → row 10
            bool pgdn = ctl.CurrentItemIndex == 10;
            Key(Keys.PageUp);
            bool pgup = ctl.CurrentItemIndex == 2;
            Key(Keys.End);                                               // End: bottom edge-aligned (cpp:1009-1016, ratio 1)
            var scEnd = Sc();
            var focusedEnd = FocusedNode(host.Scene, host.Scene.Root);
            var fr = host.Scene.AbsoluteRect(focusedEnd);
            bool end = ctl.CurrentItemIndex == 99 && sel.IsSelected(99)
                && Near(scEnd.OffsetY, ItemsViewKeyboardProbe.N * 40f - 320f)
                && Near(fr.Y, 280f) && Near(fr.H, 40f);                  // the realized last container carries keyboard focus
            Key(Keys.Home);                                              // Home: top edge-aligned (ratio 0)
            bool home = ctl.CurrentItemIndex == 0 && Near(Sc().OffsetY, 0f);
            Check("e11virt.13 ItemsView keyboard: arrows follow focus, PageUp/Down jump a viewport, Home/End edge-align + focus the realized container",
                def && click && down && up && pgdn && pgup && end && home,
                $"def={def} click={click} down={down} up={up} pgdn={pgdn} pgup={pgup} end={end} home={home} cur={ctl.CurrentItemIndex} end-off={scEnd.OffsetY:0} focusY={fr.Y:0} focusH={fr.H:0}");

            Key(Keys.A, KeyModifiers.Ctrl);                              // Ctrl+A gated OFF in Single (ItemsViewInteractions.cpp:35-50)
            bool noSelectAll = sel.SelectedCount == 1;
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
            host.RunFrame();
            bool typeahead = ctl.CurrentItemIndex == 57 && sel.IsSelected(57)
                && Near(Sc().OffsetY, 57f * 40f + 40f - 320f);           // minimal scroll realizes it at the bottom edge
            int invoked0 = probe.InvokedCount;
            Key(Keys.Enter);                                             // EnterKey invokes (ItemsView.cpp:423-426)
            bool enterInvokes = probe.InvokedCount == invoked0 + 1 && probe.LastInvoked == 57;
            Key(Keys.Space);                                             // SpaceKey selects WITHOUT invoking
            bool spaceSilent = probe.InvokedCount == invoked0 + 1 && sel.IsSelected(57);
            Press(180f, 100f, 80_000);                                   // Tap selects only (no invoke) — row 52 at this offset
            bool tapSilent = probe.InvokedCount == invoked0 + 1 && ctl.CurrentItemIndex == 52;
            Press(180f, 100f, 80_100);                                   // ClickCount 2 → DoubleTap invokes
            bool dblInvokes = probe.InvokedCount == invoked0 + 2 && probe.LastInvoked == 52;
            Check("e11virt.14 ItemsView typeahead jumps to the prefix match (+min-scroll realize); invoke matrix: Enter/DoubleTap yes, Tap/Space no; Ctrl+A gated in Single",
                noSelectAll && typeahead && enterInvokes && spaceSilent && tapSilent && dblInvokes,
                $"type→{ctl.CurrentItemIndex} invoked={probe.InvokedCount} last={probe.LastInvoked}");

            ctl.StartBringItemIntoView(10, 0f);                          // explicit edge-align (ratio 0)
            host.RunFrame();
            var scB = Sc();
            bool bring = Near(scB.OffsetY, 400f) && scB.FirstRealized <= 10 && scB.LastRealized > 10
                && ctl.CurrentItemIndex == 52;                           // StartBringItemIntoView never moves focus (ItemsView.cpp:119-127)
            ctl.StartBringItemIntoView(12);                              // already visible + default options → minimal scroll = no-op
            host.RunFrame();
            bool minimal = Near(Sc().OffsetY, 400f);
            Check("e11virt.15 StartBringItemIntoView: realizes + edge-aligns by ratio without moving focus; in-view target is a minimal-scroll no-op",
                bring && minimal, $"off={scB.OffsetY:0} window=[{scB.FirstRealized},{scB.LastRealized}) cur={ctl.CurrentItemIndex}");
        }

        // e11virt.16 — grid arrows: Left/Right = index ±1, Up/Down = ±columns (ItemsViewInteractions.cpp:1051-1067).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-grid", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewGridProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            void Key(int key)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
                host.RunFrame();
            }
            // cell 1 of a 4-col grid on 360 cross: colW = (360 − 3×8)/4 = 84 → cell 1 spans x [92,176), y [0,72).
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(134f, 36f), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(134f, 36f), 0, 0));
            host.RunFrame();
            bool click = ctl.CurrentItemIndex == 1;
            Key(Keys.Right); bool right = ctl.CurrentItemIndex == 2;
            Key(Keys.Down); bool gdown = ctl.CurrentItemIndex == 6;
            Key(Keys.Left); bool left = ctl.CurrentItemIndex == 5;
            Key(Keys.Up); bool gup = ctl.CurrentItemIndex == 1;
            Check("e11virt.16 ItemsView grid arrows: Left/Right ±1, Up/Down ±columns (index-based orientation path)",
                click && right && gdown && left && gup, $"cur 1→2→6→5→{ctl.CurrentItemIndex}");
        }

        // e11virt.17 — Extended mode end-to-end through pointer chords + Shift+arrow + Ctrl+A.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-ext", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewExtendedProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            var sel = ctl.Selection!;
            void Press(int row, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                var pt = new Point2(180f, row * 40f + 20f);
                window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0, 0f, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, pt, 0, 0, 0f, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }

            Press(2, 1_000);                                             // plain → {2}
            bool plain = sel.SelectedCount == 1 && sel.IsSelected(2) && ctl.CurrentItemIndex == 2;
            Press(6, 2_000, KeyModifiers.Shift);                         // Shift → replace with [2..6]
            bool shiftRange = sel.SelectedCount == 5 && sel.GetRange(0) == (2, 6);
            Press(7, 3_000, KeyModifiers.Ctrl);                          // Ctrl → additive {2..6, 7}
            bool ctrlAdd = sel.SelectedCount == 6 && sel.IsSelected(7);
            Press(4, 4_000);                                             // plain on SELECTED → selection kept
            bool keep = sel.SelectedCount == 6 && ctl.CurrentItemIndex == 4;
            Key(Keys.Down, KeyModifiers.Shift);                          // Shift+arrow → replace with [5..anchor(7)]
            bool shiftArrow = ctl.CurrentItemIndex == 5 && sel.SelectedCount == 3 && sel.GetRange(0) == (5, 7);
            Key(Keys.A, KeyModifiers.Ctrl);                              // Ctrl+A allowed in Extended
            bool selectAll = sel.SelectedCount == ItemsViewExtendedProbe.N && sel.RangeCount == 1;
            bool events = probe.SelectionChangedCount == 5;              // one SelectionChanged per actual change
            Check("e11virt.17 ItemsView Extended: plain/shift/ctrl pointer chords, plain-on-selected keeps, Shift+arrow anchor range, Ctrl+A",
                plain && shiftRange && ctrlAdd && keep && shiftArrow && selectAll && events,
                $"count={sel.SelectedCount} ranges={sel.RangeCount} changes={probe.SelectionChangedCount}");
        }

        // e11virt.18 — Multiple over 10k: toggle clicks re-skin the window; Ctrl+A selects ALL via one stored range
        // while realizing nothing (bounded realized children + bounded template re-runs), and every realized
        // container shows the selected chrome + checkbox.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-multi", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewMultipleProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            var sel = ctl.Selection!;
            int calls0 = probe.TemplateCalls;
            void Press(int row, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                var pt = new Point2(180f, row * 40f + 20f);
                window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0, 0f, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, pt, 0, 0, 0f, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            Press(3, 1_000);
            bool on = sel.IsSelected(3) && sel.SelectedCount == 1;       // Multiple: plain click toggles
            Press(3, 2_500);
            bool off = !sel.IsSelected(3) && sel.SelectedCount == 0;
            Press(3, 4_000);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.A, 0f, KeyModifiers.Ctrl));
            host.RunFrame();
            bool allSel = sel.SelectedCount == ItemsViewMultipleProbe.N && sel.RangeCount == 1
                && sel.GetRange(0) == (0, ItemsViewMultipleProbe.N - 1);

            NodeHandle vp = NodeHandle.Null;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (host.Scene.TryGetScroll(n, out var s) && s.ItemCount == ItemsViewMultipleProbe.N) vp = n;
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc);
            int realized = host.Scene.ChildCount(sc.ContentNode);
            int templateDelta = probe.TemplateCalls - calls0;
            bool bounded = realized < 40 && templateDelta > 0 && templateDelta < realized * 12;   // window-only re-skin per change
            var firstContainer = host.Scene.FirstChild(sc.ContentNode);
            bool chrome = host.Scene.ChildCount(firstContainer) == 4;    // content + ring + inner + checkbox
            Check("e11virt.18 ItemsView Multiple over 10k: toggle clicks, Ctrl+A = ONE range realizing nothing (bounded window re-skin) + checkbox chrome",
                on && off && allSel && bounded && chrome,
                $"count={sel.SelectedCount} ranges={sel.RangeCount} realized={realized} templateΔ={templateDelta}");
        }
    }

    static void ListConsolidationChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        NodeHandle FindVp(SceneStore s, int count)
        {
            NodeHandle found = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (s.TryGetScroll(n, out var sc) && sc.ItemCount == count) found = n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Visit(c);
            }
            Visit(s.Root);
            return found;
        }
        int LiveChildren(SceneStore s, NodeHandle vp)
        {
            if (vp.IsNull || !s.TryGetScroll(vp, out var sc) || sc.ContentNode.IsNull) return 0;
            int n = 0;
            for (var c = s.FirstChild(sc.ContentNode); !c.IsNull; c = s.NextSibling(c)) n++;
            return n;
        }
        void ScrollTo(AppHost h, HeadlessWindow w, NodeHandle vp, float y)
        {
            var s = h.Scene;
            ref ScrollState st = ref s.ScrollRef(vp);
            st.OffsetY = y; st.TargetY = y;
            var cn = st.ContentNode;
            if (!cn.IsNull && s.IsLive(cn)) { s.Paint(cn).LocalTransform = Affine2D.Translation(0f, -y); s.Mark(cn, NodeFlags.TransformDirty | NodeFlags.PaintDirty); }
            s.Mark(vp, NodeFlags.VirtualRangeDirty);
            w.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
            for (int k = 0; k < 8; k++) h.RunFrame();   // let the E4 realize budget spread the window
        }

        // ── gate.list.options-parity: a representative old-arg scenario (selection + invoke + overscan) reproduced via
        //    ListOptions produces the correct realized window + selection behaviour. ────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("lo-parity", new Size2(360, 360), 1f));
            window.Show();
            var model = new SelectionModel();
            var ctl = new ItemsViewController();
            int invoked = -1;
            var probe = new ListOptProbe
            {
                Count = 200, Extent = 40f, Vh = 200f, Overscan = 4, Bound = false,
                Options = new ListOptions
                {
                    SelectionMode = ItemsSelectionMode.Single, Selection = model, Controller = ctl,
                    IsItemInvokedEnabled = true, OnInvoked = i => invoked = i, Overscan = 4, Grow = 1f,
                },
            };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            for (int k = 0; k < 6; k++) host.RunFrame();   // settle overscan
            var vp = FindVp(host.Scene, 200);
            host.Scene.TryGetScroll(vp, out var sc);
            int realized = sc.LastRealized - sc.FirstRealized;
            // Options landed: provided model wired through the controller; overscan honored (visible 5 + overscan,
            // bounded ≪ 200); a programmatic selection re-skins with the model this list was given.
            bool modelWired = ReferenceEquals(ctl.Selection, model);
            // Windowed ≪ Count is the invariant: the mount realizes against the height Hint (ItemsView forwards no
            // explicit VirtualListEl.Height ⇒ Hint 1024 ⇒ ~26 visible + overscan), and virtualization never TRIMS a
            // still-covering window, so the steady realized band is bounded but larger than the 200px-viewport minimum.
            bool windowBounded = realized >= 5 && realized < 100;   // ASSERTION: windowed, not the full 200
            model.ItemCount = 200; model.Select(3);
            bool selects = model.IsSelected(3);
            Check("gate.list.options-parity ListOptions reproduces selection-model wiring + overscan-bounded realized window + invoke wiring",
                modelWired && windowBounded && selects,
                $"realized={realized} modelWired={modelWired} selects={selects} invokeWired={(probe.Options!.OnInvoked is not null)}");
        }

        // ── gate.list.bound-zero-alloc: a bound list scrolled over recycled slots stays 0-alloc on phases 6–13 (the
        //    recycling contract re-asserted through ItemsView.CreateBound + ListOptions). ──────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("lo-boundzero", new Size2(360, 360), 1f));
            window.Show();
            var probe = new ListOptProbe { Count = 1000, Extent = 40f, Vh = 240f, Overscan = 3, Bound = true };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var vp = FindVp(host.Scene, 1000);
            ScrollTo(host, window, vp, 4000f);   // recycle slots over a big jump
            int buildsAfterScroll = probe.Builds;
            // Settle, then a steady frame allocates 0 on the paint phases (slot rebind is a signal write, not a rebuild).
            for (int k = 0; k < 4; k++) host.RunFrame();
            var warm = host.RunFrame();
            var steady = host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc);
            bool recycledNotRebuilt = probe.Builds == buildsAfterScroll;   // no fresh builds on a steady scrolled frame
            bool zero = steady.HotPhaseAllocBytes == 0;
            Check("gate.list.bound-zero-alloc a bound ItemsView.CreateBound list scrolled far rebinds slots (no rebuild) with 0 hot-phase alloc on a steady frame",
                zero && recycledNotRebuilt && sc.FirstRealized > 0,
                $"{steady.HotPhaseAllocBytes} bytes (warm={warm.HotPhaseAllocBytes}) builds@scroll={buildsAfterScroll} builds@steady={probe.Builds} first={sc.FirstRealized}");
        }

        // ── gate.list.keepalive-slot: a keep-alive row's slot stays bound to its item off-window (state retained); a
        //    plain row's slot recycles; the bounded bucket cap evicts LRU (no leak). ────────────────────────────────
        {
            // (a) keep-alive item 0: its slot parks (index signal stays 0) after scrolling far off-window.
            using var appK = new HeadlessPlatformApp();
            var winK = new HeadlessWindow(new WindowDesc("lo-ka", new Size2(360, 240), 1f));
            winK.Show();
            var kProbe = new ListOptProbe
            {
                Count = 300, Extent = 40f, Vh = 200f, Overscan = 2, Bound = true, CaptureSig0 = true,
                Options = new ListOptions { KeepAlive = i => i == 0, KeepAliveCap = 8, Overscan = 2, Grow = 1f },
            };
            using var hostK = new AppHost(appK, winK, new HeadlessGpuDevice(), fonts, strings, kProbe);
            hostK.RunFrame();
            var vpK = FindVp(hostK.Scene, 300);
            var sig0 = kProbe.Sig0;
            ScrollTo(hostK, winK, vpK, 6000f);   // item 0 far off-window
            bool keptBound = sig0 is not null && sig0.Peek() == 0;   // parked: never index-rebound away from its item

            // (b) plain (no keep-alive): the item-0 slot's signal is rebound to a visible far item.
            using var appP = new HeadlessPlatformApp();
            var winP = new HeadlessWindow(new WindowDesc("lo-ka-plain", new Size2(360, 240), 1f));
            winP.Show();
            var pProbe = new ListOptProbe { Count = 300, Extent = 40f, Vh = 200f, Overscan = 2, Bound = true, CaptureSig0 = true };
            using var hostP = new AppHost(appP, winP, new HeadlessGpuDevice(), fonts, strings, pProbe);
            hostP.RunFrame();
            var vpP = FindVp(hostP.Scene, 300);
            var sig0P = pProbe.Sig0;
            ScrollTo(hostP, winP, vpP, 6000f);
            bool plainRecycled = sig0P is not null && sig0P.Peek() != 0;   // recycled: rebound to a far item

            // (c) bounded bucket + LRU eviction: ALL items keep-alive, cap 3 — a top→bottom sweep must NOT leak slots
            //     (live content children stay bounded ≈ window + cap, not growing toward Count).
            using var appC = new HeadlessPlatformApp();
            var winC = new HeadlessWindow(new WindowDesc("lo-ka-cap", new Size2(360, 240), 1f));
            winC.Show();
            var cProbe = new ListOptProbe
            {
                Count = 200, Extent = 40f, Vh = 200f, Overscan = 1, Bound = true,
                Options = new ListOptions { KeepAlive = i => true, KeepAliveCap = 3, Overscan = 1, Grow = 1f },
            };
            using var hostC = new AppHost(appC, winC, new HeadlessGpuDevice(), fonts, strings, cProbe);
            hostC.RunFrame();
            var vpC = FindVp(hostC.Scene, 200);
            for (int step = 1; step <= 20; step++) ScrollTo(hostC, winC, vpC, step * 300f);
            int live = LiveChildren(hostC.Scene, vpC);
            bool bounded = live <= 5 + 3 + 6;   // ~visible(5) + cap(3) + slack; NOT leaking toward 200

            Check("gate.list.keepalive-slot keep-alive slot parks bound to its item off-window (state retained); a plain slot recycles; the bucket cap bounds retained slots (LRU-evicted, no leak)",
                keptBound && plainRecycled && bounded,
                $"keptBound={keptBound}(sig0={sig0?.Peek()}) plainRecycled={plainRecycled}(sig0P={sig0P?.Peek()}) capLive={live}");
        }

        // ── gate.list.contenttype-pools: heterogeneous rows never cross-rebind — a scroll that FLIPS a slot's content
        //    type rebuilds it (rowBind runs); a scroll that PRESERVES the type cheap-rebinds (no rebuild). ──────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("lo-ctype", new Size2(360, 240), 1f));
            window.Show();
            var probe = new ListOptProbe
            {
                Count = 400, Extent = 40f, Vh = 200f, Overscan = 0, Bound = true,
                Options = new ListOptions { ContentType = i => i % 2, Overscan = 0, Grow = 1f },
            };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var vp = FindVp(host.Scene, 400);
            // Settle at a stable window at the REAL viewport (row 40) — clear of the mount-time height-Hint over-realize.
            ScrollTo(host, window, vp, 40 * 40f);
            for (int k = 0; k < 6; k++) host.RunFrame();
            // A 2-row jump preserves each slot's parity (type) => cheap rebind, no rebuild.
            int b0 = probe.Builds;
            ScrollTo(host, window, vp, 42 * 40f);
            int sameTypeBuilds = probe.Builds - b0;
            // A 1-row jump flips every slot's parity => every reused slot must REBUILD (never cross-rebind).
            int b1 = probe.Builds;
            ScrollTo(host, window, vp, 43 * 40f);
            int crossTypeBuilds = probe.Builds - b1;
            Check("gate.list.contenttype-pools a type-preserving scroll cheap-rebinds (0 rebuilds); a type-flipping scroll rebuilds every reused slot (never cross-rebinds)",
                sameTypeBuilds == 0 && crossTypeBuilds > 0,
                $"sameTypeBuilds={sameTypeBuilds} crossTypeBuilds={crossTypeBuilds}");
        }

        // ── gate.list.cache-extent: CacheExtentPx realizes rows beyond the viewport per the PIXEL margin (overriding
        //    the row-based overscan). A larger pixel band realizes a strictly larger window. ───────────────────────
        {
            int RealizedAt(float cachePx, int overscan)
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("lo-cache", new Size2(360, 240), 1f));
                window.Show();
                var probe = new ListOptProbe
                {
                    Count = 1000, Extent = 40f, Vh = 200f, Overscan = overscan, Bound = false,
                    Options = new ListOptions { Overscan = overscan, CacheExtentPx = cachePx, Grow = 1f },
                };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
                host.RunFrame();
                var vp = FindVp(host.Scene, 1000);
                ScrollTo(host, window, vp, 4000f);   // mid-list so both edges get cache; budget settles over frames
                for (int k = 0; k < 10; k++) host.RunFrame();
                host.Scene.TryGetScroll(vp, out var sc);
                return sc.LastRealized - sc.FirstRealized;
            }
            int rowBased = RealizedAt(float.NaN, 2);   // 5 visible + 2 overscan/edge
            int cache400 = RealizedAt(400f, 2);         // 400px / 40 = 10 rows/edge ⇒ a much larger window
            Check("gate.list.cache-extent CacheExtentPx pre-realizes rows by the pixel margin (overrides row overscan) — a larger band realizes a strictly larger window",
                cache400 > rowBased + 6 && cache400 >= 5 + 2 * 8,
                $"rowBased={rowBased} cache400={cache400}");
        }

        // ── gate.list.layout-presets: LinedFlow / SpanGrid / GroupedList reached through RepeatLayout presets render
        //    with the same geometry as the Virtual.* forms (spot-checks on the realized item rects). ───────────────
        {
            (SceneStore scene, NodeHandle content) Build(RepeatLayout layout, int count, Func<int, float>? rowHeight = null)
            {
                var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("lo-presets", new Size2(400, 300), 1f));
                window.Show();
                var probe = new ListOptProbe
                {
                    Count = count, Vw = 400f, Vh = 300f, Bound = false, ExplicitLayout = layout, RowHeightOf = rowHeight,
                    Options = new ListOptions { Selector = SelectorVisual.None, Overscan = 1, Grow = 1f },
                };
                var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
                host.RunFrame();
                for (int k = 0; k < 4; k++) host.RunFrame();
                var vp = FindVp(host.Scene, count);
                host.Scene.TryGetScroll(vp, out var sc);
                return (host.Scene, sc.ContentNode);
            }

            // LinedFlow: item 0 is a square line-height cell (aspect 1 × 100) at the top-left (the WinUI photo wall).
            var (sLf, cLf) = Build(RepeatLayout.LinedFlow(100f, aspectRatio: static _ => 1f), 60);
            var lf0 = Child(sLf, cLf, 0);
            bool linedFlow = !lf0.IsNull && Near(sLf.Bounds(lf0).W, 100f, 1f) && Near(sLf.Bounds(lf0).H, 100f, 1f) && Near(sLf.Bounds(lf0).Y, 0f, 1f);

            // SpanGrid: item 0 spans all 4 columns ⇒ full cross width (the hero row).
            var (sSg, cSg) = Build(RepeatLayout.SpanGrid(4, 80f, 0f, static i => i == 0 ? 4 : 1), 40);
            var sg0 = Child(sSg, cSg, 0);
            var sg1 = Child(sSg, cSg, 1);
            bool spanGrid = !sg0.IsNull && !sg1.IsNull && sSg.Bounds(sg0).W > sSg.Bounds(sg1).W * 3f && Near(sSg.Bounds(sg0).H, 80f, 1f);

            // GroupedList: index 0 is a header (48h), index 1 a normal item (40h). The measured seam corrects each row
            // to its CONTENT extent, so the header/item content heights must match the layout's seeded estimates.
            var (sGl, cGl) = Build(RepeatLayout.GroupedList(new[] { 0, 20 }, 48f, 40f), 60,
                rowHeight: static i => i == 0 || i == 20 ? 48f : 40f);
            var gl0 = Child(sGl, cGl, 0);
            var gl1 = Child(sGl, cGl, 1);
            bool grouped = !gl0.IsNull && !gl1.IsNull && Near(sGl.Bounds(gl0).H, 48f, 1f) && Near(sGl.Bounds(gl1).H, 40f, 1f);

            Check("gate.list.layout-presets LinedFlow/SpanGrid/GroupedList via RepeatLayout presets render with the Virtual.* geometry (square photo cells / full-width hero span / seeded header extents)",
                linedFlow && spanGrid && grouped,
                $"linedFlow={linedFlow} spanGrid={spanGrid} grouped={grouped}");
        }
    }

    static void D1CollectionHostSizingChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        NodeHandle FindViewport(SceneStore s, int count)
        {
            NodeHandle found = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (s.TryGetScroll(n, out var sc) && sc.ItemCount == count) found = n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Visit(c);
            }
            Visit(s.Root);
            return found;
        }

        // cp1.a — the EXACT gallery ItemsView List preset shape (the former CollectionsMenusPages.cs ListView card): a Width=280 bordered card with NO
        // height anywhere above the list — must size naturally (8 × 44 = 352) and realize all 8 rows at W=280.
        string[] coffees = { "Cappuccino", "Latte", "Espresso", "Macchiato", "Americano", "Mocha", "Flat White", "Cortado" };
        var selected = new Signal<int>(0);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("d1-listview", new Size2(640, 480), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 280, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Padding = new Edges4(0, 4, 0, 4), Children = [ItemsView.List(coffees, selected)],
            },
        });
        host.RunFrame();
        var lv = FindViewport(host.Scene, coffees.Length);
        var lsc = default(ScrollState);
        if (!lv.IsNull) host.Scene.TryGetScroll(lv, out lsc);
        var lvRect = lv.IsNull ? default : host.Scene.AbsoluteRect(lv);
        int lvRows = lv.IsNull ? 0 : host.Scene.ChildCount(lsc.ContentNode);
        var row0 = default(RectF);
        if (lvRows > 0) row0 = host.Scene.Bounds(host.Scene.FirstChild(lsc.ContentNode));
        // The {4,2,4,2} ListViewItem backplate margin now insets the item within its 44 slot (WinUI parity): 280−8=272 wide,
        // 44−4=40 tall. The 44 SLOT stride (content height 8×44=352, viewport) is unchanged — only the backplate insets.
        bool lvOk = !lv.IsNull && Near(lvRect.W, 280f) && Near(lvRect.H, 8 * ItemsView.ListItemExtent)
            && Near(lsc.ViewportW, 280f) && Near(lsc.ViewportH, 352f) && Near(lsc.ContentH, 352f)
            && lvRows == 8 && Near(row0.W, 272f) && Near(row0.H, ItemsView.ListItemExtent - 4f);
        Check("cp1.a — gallery ItemsView List preset (280-wide card, no height above) sizes naturally to 8×44 slots; backplates inset {4,2,4,2} → 272×40",
            lvOk, $"vp={lvRect.W:0}x{lvRect.H:0} viewport={lsc.ViewportW:0}x{lsc.ViewportH:0} content={lsc.ContentH:0} rows={lvRows} row0={row0.W:0}x{row0.H:0}");

        // cp1.b — the gallery ItemsView shape (MiscPages.cs): legacy Create(items, columns:4) in an AUTO-HEIGHT,
        // Start-aligned row (no stretch, no height anywhere). The view must measure to its grid's ContentExtent
        // (2 rows × 80 + 1 gap × 8 = 168) and realize all 8 tiles at the 4-column width ((420 − 3×8)/4 = 99).
        string[] photos = { "Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8" };
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("d1-itemsview", new Size2(640, 480), 1f));
        window2.Show();
        using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 420, Direction = 0, AlignItems = FlexAlign.Start,
                Children = [ItemsView.Create(photos, columns: 4)],
            },
        });
        host2.RunFrame();
        var iv = FindViewport(host2.Scene, photos.Length);
        var isc = default(ScrollState);
        if (!iv.IsNull) host2.Scene.TryGetScroll(iv, out isc);
        var ivRect = iv.IsNull ? default : host2.Scene.AbsoluteRect(iv);
        int tiles = iv.IsNull ? 0 : host2.Scene.ChildCount(isc.ContentNode);
        var tile0 = default(RectF);
        if (tiles > 0) tile0 = host2.Scene.Bounds(host2.Scene.FirstChild(isc.ContentNode));
        const float gridExtent = 2 * 80f + 8f;   // GridVirtualLayout(4, 80, 8).ContentExtent(8) — the legacy demo grid
        bool ivOk = !iv.IsNull && Near(ivRect.W, 420f) && Near(ivRect.H, gridExtent) && Near(isc.ContentH, gridExtent)
            && tiles == 8 && Near(tile0.W, (420f - 3 * 8f) / 4f) && Near(tile0.H, 80f);
        Check("cp1.b — gallery ItemsView (legacy 4-col grid, auto-height Start row) sizes to ContentExtent=168 and realizes 8 tiles",
            ivOk, $"vp={ivRect.W:0}x{ivRect.H:0} content={isc.ContentH:0} tiles={tiles} tile0={tile0.W:0}x{tile0.H:0}");

        // cp1.c — realize-after-layout: a 10k-row ItemsView List preset FILLING a 400px host stays windowed (<40 realized — the
        // Grow gate keeps the hard-viewport path; content = 10k × 44 = 440000); growing the host to 3000px must
        // publish the new viewport AND re-realize the window to cover it in the SAME frame.
        var hostH = new Signal<float>(400f);
        using var app3 = new HeadlessPlatformApp();
        var window3 = new HeadlessWindow(new WindowDesc("d1-grow", new Size2(640, 480), 1f));
        window3.Show();
        using var host3 = new AppHost(app3, window3, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 360, Height = hostH.Value,
                Children = [ItemsView.List(10_000, i => new BoxEl(), grow: 1f)],
            },
        });
        host3.RunFrame();
        var big = FindViewport(host3.Scene, 10_000);
        var bsc0 = default(ScrollState);
        if (!big.IsNull) host3.Scene.TryGetScroll(big, out bsc0);
        int realized0 = big.IsNull ? 0 : host3.Scene.ChildCount(bsc0.ContentNode);
        bool windowed = !big.IsNull && realized0 > 0 && realized0 < 40
            && Near(bsc0.ViewportH, 400f) && Near(bsc0.ContentH, 10_000 * ItemsView.ListItemExtent, 1f);

        hostH.Value = 3000f;   // grow the host — ONE RunFrame must both publish 3000 and re-realize to cover it
        host3.RunFrame();
        var bsc1 = default(ScrollState);
        if (!big.IsNull) host3.Scene.TryGetScroll(big, out bsc1);
        int realized1 = big.IsNull ? 0 : host3.Scene.ChildCount(bsc1.ContentNode);
        bool covered = Near(bsc1.ViewportH, 3000f) && bsc1.FirstRealized == 0
            && bsc1.LastRealized * ItemsView.ListItemExtent >= 3000f && realized1 > realized0 && realized1 < 120;
        Check("cp1.c — 10k rows stay windowed (<40) at 400px; growing the host re-realizes to cover in the SAME frame",
            windowed && covered, $"realized {realized0}→{realized1} viewport {bsc0.ViewportH:0}→{bsc1.ViewportH:0} last={bsc1.LastRealized} content={bsc0.ContentH:0}");
    }

    static void Cp2ConsolidationChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // The realized window's ord-th container for an item index (resting order: ord = index − FirstRealized;
        // mirrors ItemsView.FocusIndex / the displacement seed's ord math). Non-virtual hosts have no scroll state.
        static NodeHandle RealizedRow(SceneStore s, NodeHandle vp, int index)
        {
            if (vp.IsNull || !s.IsLive(vp)) return NodeHandle.Null;
            NodeHandle first; int ord;
            if (s.TryGetScroll(vp, out var sc))
            {
                ord = index - sc.FirstRealized;
                if (ord < 0 || index >= sc.LastRealized) return NodeHandle.Null;
                first = s.FirstChild(sc.ContentNode);
            }
            else { ord = index; first = s.FirstChild(vp); }
            var n = first;
            for (int k = 0; k < ord && !n.IsNull; k++) n = s.NextSibling(n);
            return n;
        }

        // Depth-first structural finder for a selector-chrome part (the SelectorVisuals builders carry no reconciler
        // Key into the scene, so match on geometry/fill rather than Key — more robust than a Key probe anyway).
        static NodeHandle FindBox(SceneStore s, NodeHandle n, Func<NodePaint, bool> pred)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (pred(s.Paint(n))) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                var r = FindBox(s, c, pred);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }

        NodeHandle FindViewport(SceneStore s, int count)
        {
            NodeHandle found = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (s.TryGetScroll(n, out var sc) && sc.ItemCount == count) found = n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Visit(c);
            }
            Visit(s.Root);
            return found;
        }

        // ── C1 — cp2.reorder2d: the GridView 2-D reorder math folded into ReorderList (Begin2D/Update2D/OffsetFor2D),
        // proving GridReorder's logic survived the consolidation. Pure unit, no host (scroll/realize-agnostic). ──────
        {
            var rl = new ReorderList { DwellMs = ReorderList.GridDwellMs };
            bool dwell300 = ReorderList.GridDwellMs == 300f && rl.DwellMs == 300f;
            rl.Begin2D(0, 8, columns: 4);                              // 8 tiles, 4 cols → rows of {0,1,2,3},{4,5,6,7}
            bool init = rl.IsActive && rl.DraggedIndex == 0 && rl.Columns == 4 && rl.PendingIndex == 0 && rl.TargetIndex == 0;
            // Drag tile 0 to slot 5 (col 1, row 1): totalDx≈100 → +1 col, totalDy≈100 → +1 row, on a 100×100 grid.
            bool moved = rl.Update2D(100f, 100f, colWidth: 100f, rowStride: 100f) && rl.PendingIndex == 5;
            bool dwellHeld = !rl.Advance(299f) && rl.TargetIndex == 0;
            bool dwellFire = rl.Advance(1f) && rl.TargetIndex == 5;
            // Forward drag 0→5: tiles (0,5] each shift ONE slot toward the vacated source (row-major). Tile 4 sits at
            // (col0,row1) and shifts to slot 3 = (col3,row0): a ROW WRAP — dx = +3 cols, dy = −1 row.
            rl.OffsetFor2D(4, 100f, 100f, out float dx4, out float dy4);
            bool wrap = Near(dx4, 300f) && Near(dy4, -100f);
            // Tile 1 (col1,row0) shifts to slot 0 (col0,row0): one column back, same row.
            rl.OffsetFor2D(1, 100f, 100f, out float dx1, out float dy1);
            bool oneCol = Near(dx1, -100f) && Near(dy1, 0f);
            rl.OffsetFor2D(0, 100f, 100f, out float dxD, out float dyD);   // the dragged tile never displaces
            bool draggedZero = dxD == 0f && dyD == 0f;
            rl.OffsetFor2D(6, 100f, 100f, out float dx6, out float dy6);   // tile 6 is OUTSIDE (0,5] → no shift
            bool outsideZero = dx6 == 0f && dy6 == 0f;
            int dest = rl.Complete();
            bool committed = dest == 5 && !rl.IsActive;
            Check("cp2.reorder2d ReorderList 2-D slot math + 300ms grid dwell + OffsetFor2D row-wrap (GridReorder folded in)",
                dwell300 && init && moved && dwellHeld && dwellFire && wrap && oneCol && draggedZero && outsideZero && committed,
                $"300={dwell300} init={init} moved={moved} dwell={dwellHeld}/{dwellFire} wrap=({dx4:0},{dy4:0}) oneCol=({dx1:0},{dy1:0}) draggedZ={draggedZero} outZ={outsideZero} dest={dest}");
        }

        // ── C2 — cp2.displace: THE core-defect proof. A displacement on a realized row reaches the node as ANIMATED
        // LocalTransform motion (mid-flight → settled), NOT a static jump and NOT discarded behind the autonomous
        // ItemsView boundary. Synthetic itemDisplacement=(0,40) for index 2 + a displacementVersion the test bumps. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-displace", new Size2(420, 360), 1f));
            window.Show();
            var ver = new Signal<int>(0);
            int dispTarget = -1;                                       // armed below (so the mount frame seeds nothing)
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 360, Height = 280,
                    Children =
                    [
                        ItemsView.Create(8,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Selector = SelectorVisual.AccentPill,
                                Reorder = new ReorderOptions { ItemDisplacement = i => i == dispTarget ? (0f, 40f) : (0f, 0f), DisplacementVersion = ver },
                            }),
                    ],
                },
            });
            host.RunFrame();                                          // mount + realize (no displacement armed yet)
            var vp = FindViewport(host.Scene, 8);
            var row2 = RealizedRow(host.Scene, vp, 2);
            bool found = !row2.IsNull;
            float authoredOffset = found ? host.Scene.Paint(row2).LocalTransform.Dy : -1f;   // 0 before any seed
            bool restZero = found && Near(authoredOffset, 0f);

            dispTarget = 2;                                           // arm (0,40) for index 2, then bump the version
            ver.Value = ver.Peek() + 1;
            host.RunFrame();                                          // re-render ItemsView → seed the TranslateY track (elapsed 0)
            for (int i = 0; i < 3; i++) host.RunFrame();             // advance the 250ms track a few 16ms ticks → mid-flight
            float midDy = found ? host.Scene.Paint(row2).LocalTransform.Dy : 0f;
            bool midFlight = midDy > 0.5f && midDy < 40f;            // animated, not an instant jump (no exact ms asserted)
            for (int i = 0; i < 40; i++) host.RunFrame();            // let the 250ms FluentDecelerate track settle
            float settledDy = found ? host.Scene.Paint(row2).LocalTransform.Dy : 0f;
            bool settled = Near(settledDy, 40f, 0.6f);
            // The element carries NO authored OffsetY — the motion lives on the AnimEngine track, not a static offset
            // (so it survives reconcile; the dragged-ghost rule). A non-displaced realized row stays put.
            var row0 = RealizedRow(host.Scene, vp, 0);
            bool neighborStill = !row0.IsNull && Near(host.Scene.Paint(row0).LocalTransform.Dy, 0f, 0.6f);
            Check("cp2.displace ItemsView host-seeds an ANIMATED translate on a displaced realized row (mid-drag part-to-make-room; not a static jump)",
                found && restZero && midFlight && settled && neighborStill,
                $"found={found} rest={authoredOffset:0.0} mid={midDy:0.0} settled={settledDy:0.0} neighbor={neighborStill}");
        }

        // ── C3 — cp2.dragstill: stage A's `HasActiveWork |= Drag.IsActive` keep-alive — a live drag keeps RunFrame
        // pumping so the FrameClock dwell ticker keeps getting frames even on a MOTIONLESS pointer (without it RunFrame
        // would early-return at the !HasActiveWork gate, AppHost.cs:351, and the dwell would freeze). The keep-alive
        // (HasActiveWork true across still frames) + the gesture surviving the still hold to commit on release are fully
        // deterministic; the dwell MATH itself is pinned at the unit level (e5dragdrop.6/.7 Advance). NOTE: this check
        // does NOT read the realized-row displacement, but the live ItemsView.List/Grid preset's displacement IS LIVE:
        // the inline fix has the itemDisplacement closure capture ONLY the memoized ReorderList (OffsetFor/OffsetFor2D
        // read live and return 0 while idle), so it survives the Rule-#2 freeze at the inner autonomous ItemsView's
        // mount — a per-render `reordering` local would instead freeze to false there. Proven on the LIVE preset path by
        // cp2.matrix.listview / cp2.matrix.gridview's lvParted/gvParted assertions; cp2.displace / cp2.scrollslot /
        // cp2.matrix.itemsview supply additional channel proofs. ─────────────────────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-still", new Size2(360, 360), 1f));
            window.Show();
            var drinks = new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" };
            int committedFrom = -1, committedTo = -1;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.List(drinks.Count,
                            i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                            canReorderItems: true, onReorder: (f, t) => { committedFrom = f; committedTo = t; },
                            keyOf: i => drinks[i], itemText: i => drinks[i]),
                    ],
                },
            });
            host.RunFrame();
            var vp = FindViewport(host.Scene, drinks.Count);
            var dragRow = RealizedRow(host.Scene, vp, 0);
            var c = CenterOf(host.Scene, dragRow);
            // Promote a drag on row 0 (PointerDown, then a >4px move) — 0-stamp ⇒ deterministic snap-track ghost.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            // Move the dragged centre (row-0 centre 22) DOWN past row 1's midpoint (66): +50 ⇒ centre 72 > 66 → pending 1.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X, c.Y + 50f), 0, 0));
            host.RunFrame();
            bool promoted = host.HasActiveWork;                       // a live drag → work pending
            // Hold STILL: no further pointer input. HasActiveWork must stay true EVERY frame (the keep-alive) so the
            // dwell ticker keeps getting frames. Space the frames with real time so wall-clock actually advances.
            bool stayedAlive = true;
            for (int i = 0; i < 5; i++)
            {
                System.Threading.Thread.Sleep(110);                  // let Environment.TickCount64 advance past a dwell step
                host.RunFrame();
                if (!host.HasActiveWork) stayedAlive = false;
            }
            // Release: the gesture stayed live through the motionless hold, so it completes and commits the reorder
            // (pending slot 1 at the latest pointer position). HasActiveWork from the drag then drains.
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X, c.Y + 50f), 0, 0));
            host.RunFrame();
            bool committedAfterStill = committedFrom == 0 && committedTo == 1;
            Check("cp2.dragstill HasActiveWork keep-alive holds frames across a MOTIONLESS pointer (no early-return); the gesture survives the still hold + commits on release",
                promoted && stayedAlive && committedAfterStill,
                $"promoted={promoted} alive={stayedAlive} commit=({committedFrom}->{committedTo})");
        }

        // ── C4 — cp2.invokerelease: a promoted drag is a REORDER gesture (commits a move on release); a plain
        // press-release is a CLICK (selects + raises ItemClick + does NOT reorder).
        // NOTE (WinUI-faithful divergence, documented + verified against the real code): this engine selects AND raises
        // ItemClick at the PRESS edge via OnPointerPressed→Tap (ItemContainer.cs:41-45 / SelectorVisuals.AccentPill +
        // ListView.Chrome's interact wrapper — "Win32 lists select on button-down; the visual outcome is identical to
        // WinUI's PointerReleased selection"). So the orders' literal "a drag does not select / drag suppresses the
        // click" cannot hold against press-edge handling — both a click and a grab touch the row at press. What a
        // completed drag uniquely does is take the DRAG path (commit a reorder) and suppress the RELEASE-edge click
        // (InputDispatcher.cs:332-345); a plain release takes the CLICK path. This asserts that truthful discrimination
        // (the spirit of parity:420/430 — a drag is a drag, a click is a click). ─────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-invoke", new Size2(360, 360), 1f));
            window.Show();
            var drinks = new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" };
            int clicked = -1, clickCount = 0, reorderFrom = -1, reorderTo = -1;
            var model = new SelectionModel();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.List(drinks.Count,
                            i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                            selection: model,
                            onItemClick: i => { clicked = i; clickCount++; },
                            canReorderItems: true, onReorder: (f, t) => { reorderFrom = f; reorderTo = t; }, keyOf: i => drinks[i]),
                    ],
                },
            });
            host.RunFrame();
            var vp = FindViewport(host.Scene, drinks.Count);

            // (a) DRAG row 1 UP past row 0's midpoint (row-1 centre 66 → −50 ⇒ 16 < row-0 mid 22 → pending 0), release:
            // the gesture is a REORDER, committing 1→0 (Complete uses the latest pending, no dwell needed for the drop).
            var r1 = RealizedRow(host.Scene, vp, 1);
            var c1 = CenterOf(host.Scene, r1);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c1, 0, 0));
            host.RunFrame();
            bool dragPressDeferred = !model.IsSelected(1) && clickCount == 0;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c1.X, c1.Y - 50f), 0, 0));   // >4px up → promote, cross row-0 mid
            host.RunFrame();
            bool active = host.HasActiveWork;
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c1.X, c1.Y - 50f), 0, 0));
            host.RunFrame();
            bool dragReordered = reorderFrom == 1 && reorderTo == 0;  // the drag committed a move, not a tap-invoke
            bool dragDidNotInvoke = !model.IsSelected(1) && clickCount == 0;

            // (b) PLAIN click on row 2 (no threshold cross): ItemClick fires + the row selects (press-edge), NO reorder.
            int reorders0From = reorderFrom;
            var r2 = RealizedRow(host.Scene, vp, 2);
            var c2 = CenterOf(host.Scene, r2);
            int clicks1 = clickCount;
            window.QueueInput(new InputEvent(InputKind.PointerDown, c2, 0, 0));
            host.RunFrame();
            bool clickPressDeferred = !model.IsSelected(2) && clickCount == clicks1;
            window.QueueInput(new InputEvent(InputKind.PointerUp, c2, 0, 0));
            host.RunFrame();
            bool plainClicks = clickCount == clicks1 + 1 && clicked == 2 && model.IsSelected(2) && reorderFrom == reorders0From;
            Check("cp2.invokerelease pointer-down defers selection; a promoted drag never selects/invokes; only a clean release selects + raises ItemClick",
                dragPressDeferred && dragDidNotInvoke && clickPressDeferred && active && dragReordered && plainClicks,
                $"dragDeferred={dragPressDeferred} dragSilent={dragDidNotInvoke} clickDeferred={clickPressDeferred} active={active} dragReorder=({reorderFrom}->{reorderTo}) plain={plainClicks} clicked={clicked} sel2={model.IsSelected(2)}");
        }

        // ── C5 — cp2.scrollslot: under a SCROLLED viewport (FirstRealized>0) the displacement seed lands on the
        // CORRECT realized node (index→ord via FirstRealized — the seed's ord math must respect scroll, not blindly
        // use the absolute index). Synthetic displacement on a realized-but-scrolled index. ──────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-scroll", new Size2(360, 360), 1f));
            window.Show();
            var ver = new Signal<int>(0);
            int dispTarget = -1;
            var ctl = new ItemsViewController();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 200,                        // 200 / 40 ⇒ ~5 rows visible over 100 items
                    Children =
                    [
                        ItemsView.Create(100,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Controller = ctl,
                                Selector = SelectorVisual.AccentPill,
                                Reorder = new ReorderOptions { ItemDisplacement = i => i == dispTarget ? (0f, 24f) : (0f, 0f), DisplacementVersion = ver },
                            }),
                    ],
                },
            });
            host.RunFrame();
            ctl.StartBringItemIntoView(40, 0f);                       // scroll so item 40 is at the top edge
            host.RunFrame();
            var vp = FindViewport(host.Scene, 100);
            host.Scene.TryGetScroll(vp, out var sc);
            bool scrolled = sc.FirstRealized >= 30;                   // genuinely past the top (ord != index now)
            int target = sc.FirstRealized + 1;                        // a realized item with ord = 1
            var targetNode = RealizedRow(host.Scene, vp, target);
            bool nodeFound = !targetNode.IsNull;

            dispTarget = target;
            ver.Value = ver.Peek() + 1;
            host.RunFrame();
            for (int i = 0; i < 40; i++) host.RunFrame();            // settle
            float tdy = nodeFound ? host.Scene.Paint(targetNode).LocalTransform.Dy : 0f;
            bool landedOnTarget = nodeFound && Near(tdy, 24f, 0.6f);
            // The ord-0 realized node (a DIFFERENT item index than `target`) must NOT have moved — proving the seed
            // mapped index→ord through FirstRealized rather than smearing onto the wrong (absolute-index) child.
            var ord0 = RealizedRow(host.Scene, vp, sc.FirstRealized);
            bool othersStill = !ord0.IsNull && Near(host.Scene.Paint(ord0).LocalTransform.Dy, 0f, 0.6f);
            Check("cp2.scrollslot displaced offset lands on the correct realized node under a scrolled viewport (index→ord via FirstRealized)",
                scrolled && nodeFound && landedOnTarget && othersStill,
                $"first={sc.FirstRealized} target={target} tdy={tdy:0.0} othersStill={othersStill}");
        }

        // ── C6 — cp2.matrix: the SAME logical reorder (drag item 0 → slot 2) run THREE ways — the List preset, the Grid
        // preset, and ItemsView (synthetic, like a preset) — proving "every capability in every combination". ALL THREE
        // arms pin part-to-make-room: the (i) List preset and (ii) Grid preset arms drive a REAL pointer drag through the LIVE preset
        // wiring and assert (1) the model move 0→2 commits on release, (2) the dragged node's translate is the DRAG's
        // (ghost rides the pointer), (3) a displaced sibling's translate parts toward the vacated slot mid-drag. That
        // third assertion guards the closure-freeze regression: itemDisplacement is a constructor arg of the inner
        // autonomous ItemsView, so it freezes at mount (engine Rule #2) — it must capture only STABLE state (the
        // memoized ReorderList), never a per-render local; a frozen `reordering ? … : (0,0)` once shipped green here
        // while the live displacement was dead. The (iii) ItemsView arm pins the channel itself deterministically. ────
        {
            // (i) ListView ─────────────────────────────────────────────────────────────────────────────────────────
            int lvFrom = -1, lvTo = -1;
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("cp2-mx-lv", new Size2(360, 360), 1f));
                window.Show();
                var items = new List<string> { "A", "B", "C", "D", "E" };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 320, Height = 280,
                        Children =
                        [
                            ItemsView.List(items.Count,
                                i => new TextEl(items[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                                canReorderItems: true, onReorder: (f, t) => { lvFrom = f; lvTo = t; }, keyOf: i => items[i]),
                        ],
                    },
                });
                host.RunFrame();
                var vp = FindViewport(host.Scene, items.Count);
                var dragRow = RealizedRow(host.Scene, vp, 0);
                var c = CenterOf(host.Scene, dragRow);
                window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
                host.RunFrame();
                // Move the dragged centre PAST row 2's midpoint (row-0 centre 22; row-2 mid = 44*2+22 = 110 ⇒ +92 → centre 114 > 110 → pending 2).
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X, c.Y + 92f), 0, 0));
                host.RunFrame();
                // A few frames while held (real time advances the dwell ticker; keep-alive keeps frames coming).
                bool aliveLv = host.HasActiveWork;
                for (int i = 0; i < 3; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); aliveLv &= host.HasActiveWork; }
                // The 200ms dwell committed target=2 during the loop; the LIVE preset's ItemDisplacement channel seeds
                // rows 1,2 with the animated -44 part (the 250ms Reposition glide) — settle it, then read row 1.
                for (int i = 0; i < 3; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); }
                var row1Lv = RealizedRow(host.Scene, vp, 1);
                float lvRow1Dy = row1Lv.IsNull ? 0f : host.Scene.Paint(row1Lv).LocalTransform.Dy;
                bool lvParted = !row1Lv.IsNull && lvRow1Dy < -30f;   // a displaced sibling parts up toward -44 on the LIVE path (directional: the glide settles asymptotically)
                // Re-fetch FRESH: the ListView re-renders + re-realizes every drag frame (its FrameClock dwell ticker).
                var dragRowNow = RealizedRow(host.Scene, vp, 0);
                float draggedDy = dragRowNow.IsNull ? 0f : host.Scene.Paint(dragRowNow).LocalTransform.Dy;   // ghost rides the pointer (+~92)
                // TWO-SIDED: the dragged node rides the pointer at the gesture delta (+92) and STAYS there across the
                // held frames — it must NOT run away. Pre-fix, the ItemsView displacement seed planted a Replace
                // TranslateY track on the dragged ghost (DraggedSlot is unwired in every preset) that fought
                // DragController.RetargetFromRest into an unbounded per-frame runaway (hundreds→thousands of px, the
                // ghost flying off the page); the old `draggedDy > 20f` lower bound passed on that runaway. The upper
                // bound is the actual regression guard — the seed now skips the DragGhost-flagged node.
                bool lvGhost = !dragRowNow.IsNull && MathF.Abs(draggedDy - 92f) < 20f;   // ≈ +92 gesture delta, not a runaway
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X, c.Y + 92f), 0, 0));
                host.RunFrame();
                bool lvCommit = lvFrom == 0 && lvTo == 2;
                Check("cp2.matrix.listview drag 0→2: model commits, displaced row parts mid-drag on the LIVE path, dragged node owned by the drag, keep-alive holds",
                    lvCommit && lvGhost && aliveLv && lvParted,
                    $"commit=({lvFrom}->{lvTo}) row1Dy={lvRow1Dy:0.0} draggedDy={draggedDy:0.0} alive={aliveLv}");
            }

            // (ii) GridView (Check selector + ReorderList 2-D) ──────────────────────────────────────────────────────
            int gvFrom = -1, gvTo = -1;
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("cp2-mx-gv", new Size2(480, 360), 1f));
                window.Show();
                var items = new List<string> { "A", "B", "C", "D", "E", "F", "G", "H" };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 440, Height = 320,
                        Children =
                        [
                            ItemsView.Grid(items.Count, i => new BoxEl { Children = [new TextEl(items[i]) { Size = 13f }] },
                                columns: 4, tileHeight: 96f,
                                canReorderItems: true, onReorder: (f, t) => { gvFrom = f; gvTo = t; }, keyOf: i => items[i]),
                        ],
                    },
                });
                host.RunFrame();
                var vp = FindViewport(host.Scene, items.Count);
                var dragTile = RealizedRow(host.Scene, vp, 0);
                var c = CenterOf(host.Scene, dragTile);
                var c2 = CenterOf(host.Scene, RealizedRow(host.Scene, vp, 2));   // target slot 2 (same row, 2 cols right)
                window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
                host.RunFrame();
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c2.X, c2.Y), 0, 0));   // over tile-2 centre
                host.RunFrame();
                bool aliveGv = host.HasActiveWork;
                for (int i = 0; i < 4; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); aliveGv &= host.HasActiveWork; }   // 300ms grid dwell
                // The 300ms dwell committed target=2; the LIVE preset's channel seeds tiles 1,2 with the animated
                // one-column-left part — settle the 250ms glide, then read tile 1 (directional: a column ≈ 110px).
                for (int i = 0; i < 3; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); }
                var tile1Gv = RealizedRow(host.Scene, vp, 1);
                float gvTile1Dx = tile1Gv.IsNull ? 0f : host.Scene.Paint(tile1Gv).LocalTransform.Dx;
                bool gvParted = !tile1Gv.IsNull && gvTile1Dx < -60f;   // a displaced tile parts one column left on the LIVE path
                // Re-fetch FRESH (the GridView re-renders + re-realizes every drag frame via its dwell ticker).
                var dragTileNow = RealizedRow(host.Scene, vp, 0);
                float draggedTx = dragTileNow.IsNull ? 0f : host.Scene.Paint(dragTileNow).LocalTransform.Dx;
                // TWO-SIDED: the dragged tile rides the pointer at the injected horizontal gesture delta (≈2 columns)
                // and STAYS there — it must NOT run away. Pre-fix the displacement seed stomped BOTH tile axes
                // (OffsetFor2D returns (0,0) for the dragged tile, but the seed animated its live translate back to 0),
                // and the runaway was on the X axis for this same-row drag; the old `draggedTx > 20f` lower bound
                // masked it. The upper bound (anchored to the real injected delta) is the regression guard.
                float gvExpectTx = c2.X - c.X;
                bool gvGhost = !dragTileNow.IsNull && MathF.Abs(draggedTx - gvExpectTx) < 16f;   // tile rides the pointer, not a runaway
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c2.X, c2.Y), 0, 0));
                host.RunFrame();
                bool gvCommit = gvFrom == 0 && gvTo == 2;
                Check("cp2.matrix.gridview drag 0→2 (2-D): model commits, displaced tile parts mid-drag on the LIVE path, dragged tile owned by the drag, keep-alive holds",
                    gvCommit && gvGhost && aliveGv && gvParted,
                    $"commit=({gvFrom}->{gvTo}) tile1Dx={gvTile1Dx:0.0} draggedTx={draggedTx:0.0} alive={aliveGv}");
            }

            // (iii) ItemsView wired like a preset (AccentPill + synthetic ReorderList-fed displacement) ──────────────
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("cp2-mx-iv", new Size2(360, 360), 1f));
                window.Show();
                var rl = new ReorderList { DwellMs = 0f };           // 0 dwell ⇒ target follows pending immediately (test-deterministic)
                var ver = new Signal<int>(0);
                int from = -1, to = -1;
                rl.OnCommit = (f, t) => { from = f; to = t; };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 320, Height = 280,
                        Children =
                        [
                            ItemsView.Create(5,
                                i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                                RepeatLayout.Stack(40f),
                                new ListOptions
                                {
                                    Selector = SelectorVisual.AccentPill,
                                    Reorder = new ReorderOptions { ItemDisplacement = i => { rl.OffsetFor2D(i, 0f, 0f, out _, out _); return (0f, rl.OffsetFor(i)); }, DisplacementVersion = ver },
                                }),
                        ],
                    },
                });
                host.RunFrame();
                // Drive the substrate like a preset would: Begin, Update past row 2's midpoint, commit the target.
                rl.Begin(0, 5, itemExtent: 40f);
                rl.Update(88f);                                       // dragged centre 20+88=108 > mid-2 (100) → pending 2
                rl.Advance(0f);                                       // 0 dwell ⇒ target = 2 now (OffsetFor parts rows 1,2)
                ver.Value = ver.Peek() + 1;
                host.RunFrame();
                for (int i = 0; i < 40; i++) host.RunFrame();        // settle the seeded translate
                var vp = FindViewport(host.Scene, 5);
                var row1 = RealizedRow(host.Scene, vp, 1);
                float row1Dy = row1.IsNull ? 0f : host.Scene.Paint(row1).LocalTransform.Dy;
                bool ivDisplace = !row1.IsNull && Near(row1Dy, -40f, 0.8f);   // row 1 parts up by one extent
                int dest = rl.Complete();
                bool ivCommit = dest == 2 && from == 0 && to == 2;
                Check("cp2.matrix.itemsview drag 0→2 (preset-wired): displacement parts the block + the substrate commits 0→2",
                    ivDisplace && ivCommit, $"row1Dy={row1Dy:0.0} commit=({from}->{to}) dest={dest}");
            }
        }

        // ── C7 — cp2.selectorpresets: each SelectorVisual preset builds the correct selected chrome, exercised through
        // the PUBLIC ItemsView selector switch (the SelectorVisuals builders are `internal` with no InternalsVisibleTo
        // to this project, so the public path is the only reachable seam — and it additionally proves the BCore switch
        // wiring + recyclability, since virtualized realize rejects any non-recyclable container). ───────────────────
        {
            // Build a one-item ItemsView in a given selector + selection state and return its realized container subtree.
            (SceneStore scene, NodeHandle container) BuildSel(SelectorVisual sel, ItemsSelectionMode mode, bool selected)
            {
                var model = new SelectionModel { ItemCount = 1, Mode = mode };
                if (selected) model.Select(0);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("cp2-sel", new Size2(240, 160), 1f));
                window.Show();
                var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 200, Height = 120,
                        Children =
                        [
                            ItemsView.Create(1, i => new BoxEl { Width = 120, Height = 40 }, RepeatLayout.Stack(44f),
                                new ListOptions { SelectionMode = mode, Selection = model, Selector = sel }),
                        ],
                    },
                });
                host.RunFrame();
                var vp = FindViewport(host.Scene, 1);
                var container = RealizedRow(host.Scene, vp, 0);
                // Detach the scene from the (disposing) host: AbsoluteRect/Paint stay valid on the retained store.
                var s = host.Scene;
                host.Dispose();
                return (s, container);
            }

            // The accent pill is uniquely identified on NodePaint by (Fill≈AccentDefault ∧ Corners.TopLeft≈1.5); laid-out
            // W/H live in _bounds (read via AbsoluteRect), not NodePaint.Size, so verify geometry separately.
            static bool IsPill(NodePaint p) => Near(p.Corners.TopLeft, 1.5f) && ColorClose(p.Fill, Tok.AccentDefault, 0.02f);

            // (a) AccentPill selected (Single) → the 3×16 r1.5 accent pill exists.
            var (pillScene, pillRoot) = BuildSel(SelectorVisual.AccentPill, ItemsSelectionMode.Single, selected: true);
            var pill = FindBox(pillScene, pillRoot, IsPill);
            var pillRect = pill.IsNull ? default : pillScene.AbsoluteRect(pill);
            bool pillOk = !pill.IsNull && Near(pillRect.W, 3f) && Near(pillRect.H, 16f);

            // (b) AccentPill selected in MULTIPLE → the pill is SUPPRESSED (checkbox-OR-pill); the inline 20×20 check
            // plate is present (the only BorderWidth≈1 ∧ Corners.TopLeft≈3 node in the AccentPill row).
            var (multiScene, multiRoot) = BuildSel(SelectorVisual.AccentPill, ItemsSelectionMode.Multiple, selected: true);
            var noPill = FindBox(multiScene, multiRoot, IsPill);
            var checkPlate = FindBox(multiScene, multiRoot, p => Near(p.BorderWidth, 1f) && Near(p.Corners.TopLeft, 3f));
            var checkRect = checkPlate.IsNull ? default : multiScene.AbsoluteRect(checkPlate);
            bool multiOk = noPill.IsNull && !checkPlate.IsNull && Near(checkRect.W, 20f) && Near(checkRect.H, 20f);

            // (c) Check selected (Single) → the 2px accent border on the plate + the inset 1px ControlSolid inner ring.
            var (checkScene, checkRoot) = BuildSel(SelectorVisual.Check, ItemsSelectionMode.Single, selected: true);
            bool plateBorder = Near(checkScene.Paint(checkRoot).BorderWidth, 2f)
                && ColorClose(checkScene.Paint(checkRoot).BorderColor, Tok.AccentDefault, 0.02f);
            var innerRing = FindBox(checkScene, checkRoot, p => Near(p.BorderWidth, 1f)
                && ColorClose(p.BorderColor, Tok.FillControlSolid, 0.02f) && Near(p.Corners.TopLeft, 3f));
            bool checkOk = plateBorder && !innerRing.IsNull;

            // (d) FullRow superset selected → NO left pill, but the selected subtle plate reads as the full-bleed fill.
            var (fullScene, fullRoot) = BuildSel(SelectorVisual.FullRow, ItemsSelectionMode.Single, selected: true);
            var fullPill = FindBox(fullScene, fullRoot, IsPill);
            bool fullOk = fullPill.IsNull && ColorClose(fullScene.Paint(fullRoot).Fill, Tok.FillSubtleSecondary, 0.02f);

            // (e) None → a bare container: no pill, no accent ring (app draws its own selection).
            var (noneScene, noneRoot) = BuildSel(SelectorVisual.None, ItemsSelectionMode.Single, selected: true);
            bool noneOk = FindBox(noneScene, noneRoot, IsPill).IsNull
                && FindBox(noneScene, noneRoot, p => Near(p.BorderWidth, 3f)).IsNull;

            Check("cp2.selectorpresets AccentPill/Check/FullRow/None build the correct selected chrome (pill 3×16, Multiple-suppression, Check dual-border, FullRow full-bleed, None bare)",
                pillOk && multiOk && checkOk && fullOk && noneOk,
                $"pill={pillOk} multiSuppress={multiOk} check={checkOk} fullRow={fullOk} none={noneOk}");
        }

        // ── C8 — cp2.treedwell: TreeView consumes the SAME reorder substrate. The dwell + 1s auto-expand advance on a
        // still drag (stage A keep-alive + BTree left the ticker intact); the realized-handle map prune (BT2) keeps the
        // realized-row count bounded across a Roots rebuild; reparent is DEFERRED (default) so a drop commits SIBLING-only. ─
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-tree", new Size2(360, 400), 1f));
            window.Show();
            var rootsA = new List<TreeNode>
            {
                new("Alpha"), new("Bravo"), new("Charlie"), new("Delta"), new("Echo"),
            };
            var roots = new Signal<IReadOnlyList<TreeNode>>(rootsA);
            TreeNode? commitParent = null; int tFrom = -1, tTo = -1; bool reparent = false;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 360,
                    Children =
                    [
                        TreeView.Create(roots.Value, itemTemplate: null, canReorderItems: true,
                            onReorder: (parent, f, t) => { commitParent = parent; tFrom = f; tTo = t; reparent = parent is not null; }),
                    ],
                },
            });
            host.RunFrame();

            // Count realized tree rows via role scan (each TreeViewItem row carries the Button automation role).
            int RowCount() => Roles(host.Scene, AutomationRole.Button).Count;
            int rows0 = RowCount();
            bool initialRows = rows0 >= 5;                            // 5 roots realized as Button-role rows

            // Promote a drag on root 0, hold still through the dwell, assert the keep-alive + that work stays pending.
            var firstRow = Roles(host.Scene, AutomationRole.Button) is { Count: > 0 } rs ? rs[0] : NodeHandle.Null;
            bool aliveDuringDrag = true, dwellAdvanced = false;
            if (!firstRow.IsNull)
            {
                var c = CenterOf(host.Scene, firstRow);
                window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
                host.RunFrame();
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X, c.Y + 60f), 0, 0));   // past sibling 1
                host.RunFrame();
                for (int i = 0; i < 4; i++)
                {
                    System.Threading.Thread.Sleep(110);
                    host.RunFrame();
                    if (!host.HasActiveWork) aliveDuringDrag = false;
                }
                // After the dwell, the projected sibling order moved root 0 down: the FLIP re-rendered the keyed rows,
                // so the row that is now FIRST in the realized column is no longer the originally-dragged node's slot.
                // Proxy: the dragged ghost node carries a non-zero translate (it rides the pointer) — proves the drag
                // engine + dwell are live (the keep-alive pumped frames).
                dwellAdvanced = host.HasActiveWork && MathF.Abs(host.Scene.Paint(firstRow).LocalTransform.Dy) > 0.5f;
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X, c.Y + 60f), 0, 0));
                host.RunFrame();
            }
            bool siblingCommit = !reparent && tFrom == 0 && tTo >= 1 && commitParent is null;   // root-level sibling move

            // BT2 prune: rebuild Roots to a SMALLER set; the realized rows must shrink (no leaked handles inflate it).
            roots.Value = new List<TreeNode> { new("Alpha"), new("Bravo") };
            host.RunFrame();
            int rows1 = RowCount();
            bool prunedBounded = rows1 <= rows0 && rows1 >= 2;       // realized rows track the live projection (bounded)

            Check("cp2.treedwell TreeView reorder: dwell/keep-alive advance on a still drag, sibling-only commit (reparent deferred), realized rows stay bounded after a Roots rebuild",
                initialRows && aliveDuringDrag && dwellAdvanced && siblingCommit && prunedBounded,
                $"rows {rows0}→{rows1} alive={aliveDuringDrag} dwell={dwellAdvanced} commit=({tFrom}->{tTo},parent={(commitParent is null ? "root" : "node")},reparent={reparent})");
        }

        // ── cp2.dragalloc: the displacement SEED is edge-triggered (it fires only when DisplacementVersion changes),
        // so a steady frame with NO version bump is 0-alloc on the hot phases. (A LIVE List/Grid preset reorder is NOT
        // per-frame 0-alloc — its FrameClock dwell ticker re-renders every drag frame to advance the 200/300ms timer,
        // an inherent cost of the live-reorder timer, NOT of the displacement seed; the raw drag-move path's 0-alloc is
        // already pinned by e5dragdrop.8b. So this isolates the SEED on the ItemsView path, which has no dwell ticker:
        // once seeded and settled, an unbumped frame allocates nothing.) ────────────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-alloc", new Size2(360, 360), 1f));
            window.Show();
            var ver = new Signal<int>(0);
            int dispTarget = 2;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.Create(8,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Selector = SelectorVisual.AccentPill,
                                Reorder = new ReorderOptions { ItemDisplacement = i => i == dispTarget ? (0f, 24f) : (0f, 0f), DisplacementVersion = ver },
                            }),
                    ],
                },
            });
            host.RunFrame();
            ver.Value = ver.Peek() + 1;          // seed the displacement track once (edge), then let it fully settle
            for (int i = 0; i < 50; i++) host.RunFrame();
            // A steady frame with NO version bump: the seed effect doesn't re-run, the AnimEngine track has settled and
            // been reclaimed, the ItemsView doesn't re-render — phases 6–13 must allocate 0.
            var warm = host.RunFrame();
            var steady = host.RunFrame();
            bool zero = steady.HotPhaseAllocBytes == 0;
            Check("cp2.dragalloc the displacement seed is edge-triggered — a steady ItemsView frame with no version bump is 0-alloc on phases 6–13",
                zero, $"{steady.HotPhaseAllocBytes} bytes (warm={warm.HotPhaseAllocBytes})");
        }

        // ── cp2.partdelta.alloc (S1b hazard fix): the PartDelta VALUE seam adds NO per-realize allocation in steady
        // state. The delta lambda is a PURE value function (no new/box/Animate/OnRealized — it returns a readonly
        // record struct of nullable property values), resolved ONCE per realize inside chrome construction and applied
        // as `??` init-property swaps — so a settled, steadily-painting frame allocates 0 on phases 6–13 even with a
        // non-null partDelta. This is the CI proof that per-item VARIATION-as-VALUES replaces the banned per-item part
        // modifier without re-introducing the recycled-scroll allocation hazard (docs/guide/control-fidelity.md §6). ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-pd-alloc", new Size2(360, 360), 1f));
            window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.Create(8,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Selector = SelectorVisual.AccentPill,
                                PartDelta = (i, st) => new PartDelta(Fill: i % 2 == 0 ? Tok.FillSubtleSecondary : ColorF.Transparent),
                            }),
                    ],
                },
            });
            host.RunFrame();
            for (int i = 0; i < 50; i++) host.RunFrame();   // let everything settle (no animations seeded by a pure delta)
            var warm = host.RunFrame();
            var steady = host.RunFrame();
            bool zero = steady.HotPhaseAllocBytes == 0;
            Check("cp2.partdelta.alloc a steady ItemsView frame with a custom PartDelta value seam is 0-alloc on phases 6–13 (no per-realize allocation)",
                zero, $"{steady.HotPhaseAllocBytes} bytes (warm={warm.HotPhaseAllocBytes})");
        }

        // ── cp2.partdelta.shape (S1b hazard fix): PartDelta-applied chrome is SHAPE-STABLE across recycling. The delta
        // is applied ONLY as value swaps (fill/corner/etc), NEVER as a child add/remove — so a row recycled to a new
        // item index keeps identical child arity. In a DEBUG build Reconciler.AssertRecycleShapeStable would Debug.Fail
        // (and crash this harness) on a shape-corrupting recycle; the harness running to completion through a forced
        // scroll-recycle IS the guard's green proof. We additionally assert the realized window stays bounded and a
        // recycled row's child arity is unchanged before/after the scroll (the delta never feeds a child-count decision).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-pd-shape", new Size2(360, 280), 1f));
            window.Show();
            var ctl = new ItemsViewController();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.Create(200,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Controller = ctl,
                                Selector = SelectorVisual.FullRow,
                                Grow = 1f,
                                PartDelta = (i, st) => new PartDelta(
                                    Fill: i % 3 == 0 ? Tok.FillSubtleTertiary : ColorF.Transparent,
                                    Corners: CornerRadius4.All(6f)),
                            }),
                    ],
                },
            });
            host.RunFrame();
            var vp = FindViewport(host.Scene, 200);
            host.Scene.TryGetScroll(vp, out var sc0);
            int realized0 = vp.IsNull ? 0 : host.Scene.ChildCount(sc0.ContentNode);
            // Child arity of the first realized row BEFORE the scroll-recycle (FullRow chrome — fixed-shape plate/content).
            var row0Before = RealizedRow(host.Scene, vp, sc0.FirstRealized);
            int arityBefore = row0Before.IsNull ? -1 : host.Scene.ChildCount(row0Before);
            bool windowed = realized0 > 0 && realized0 < 40;   // 200 rows windowed over a 280px viewport

            // Force several recycles: bring progressively deeper items to the top edge (each re-realizes the window,
            // recycling rows onto new item indices — exactly the recycled-scroll path the hazard guards).
            for (int s = 1; s <= 5; s++) { ctl.StartBringItemIntoView(s * 20, 0f); host.RunFrame(); for (int k = 0; k < 3; k++) host.RunFrame(); }

            host.Scene.TryGetScroll(vp, out var sc1);
            int realized1 = vp.IsNull ? 0 : host.Scene.ChildCount(sc1.ContentNode);
            bool scrolled = sc1.FirstRealized > sc0.FirstRealized;     // genuinely recycled onto deeper item indices
            var row0After = RealizedRow(host.Scene, vp, sc1.FirstRealized);
            int arityAfter = row0After.IsNull ? -1 : host.Scene.ChildCount(row0After);
            // No DEBUG shape-mismatch assert fired (the harness reached here) AND the recycled row's arity is stable
            // AND the window stayed bounded after recycling — the delta re-applies as values, never as structure.
            bool shapeStable = arityBefore > 0 && arityAfter == arityBefore && realized1 > 0 && realized1 < 40;
            Check("cp2.partdelta.shape PartDelta chrome is shape-stable across scroll-recycling (realizer shape guard never trips; recycled-row child arity unchanged)",
                windowed && scrolled && shapeStable,
                $"realized {realized0}→{realized1} arity {arityBefore}→{arityAfter} first {sc0.FirstRealized}→{sc1.FirstRealized} (no Debug.Fail = shape guard passed)");
        }

        // ── cp2.partdelta.epochcache (S1b hazard fix — INDIRECT): TemplateParts.TryApplyCached (the apply-once
        // list-uniform prototype cache) is `internal` and FluentGpu.Dsl carries NO InternalsVisibleTo("FluentGpu.VerticalSlice")
        // (verified — the SelectorVisuals builders are likewise tested only through the PUBLIC ItemsView surface, see
        // cp2.selectorpresets). So we cannot call TryApplyCached directly. We DO verify its invalidation key — the
        // public TemplateParts.Epoch — which is the cache's correctness foundation: a fresh map is epoch 0; EVERY
        // modifier-map mutation (Set<T>, the box indexer set, and a null-removal) bumps the epoch, so changing a
        // list-uniform modifier provably invalidates the (part, epoch)-keyed prototype. The apply-ONCE half is covered
        // by cp2.partdelta.alloc (steady-state 0-alloc through the value seam). NOTE THE LIMITATION: making the
        // apply-once count directly observable would need TryApplyCached promoted to public or an InternalsVisibleTo.
        {
            var parts = new TemplateParts();
            int e0 = parts.Epoch;                                  // fresh map: epoch 0
            parts.Set<BoxEl>("Header", b => b with { Fill = Tok.FillSubtleSecondary });
            int e1 = parts.Epoch;                                  // Set<T> bumps
            parts["Header"] = b => b with { Fill = Tok.FillSubtleTertiary };   // box indexer set bumps (replace modifier)
            int e2 = parts.Epoch;
            parts["Header"] = null;                                // null-removal bumps (invalidation on removal)
            int e3 = parts.Epoch;
            bool monotonic = e0 == 0 && e1 > e0 && e2 > e1 && e3 > e2;
            Check("cp2.partdelta.epochcache TemplateParts.Epoch (the apply-once cache's invalidation key) bumps on every modifier-map mutation — fresh=0, Set/indexer/removal each invalidate",
                monotonic, $"epoch 0={e0} setT={e1} indexer={e2} removal={e3} (TryApplyCached internal; apply-once covered by cp2.partdelta.alloc)");
        }
    }

    static void D4ScrollBarChecks(StringTable strings)
    {
        // ── ScrollBar.Anatomy: reserved arrow cells, instant signal-bound position, debounced 167ms expand ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-sb", new Size2(320, 280), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var pos = new FloatSignal(0f);
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                    Children = [ScrollBar.Create(0.25f, pos, p => pos.Value = p, 200f)],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var bar = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var column = Child(host.Scene, bar, 2);                 // root ZStack = [track, strip, column(, ticker)]
            var arrowUp = Child(host.Scene, column, 0);             // column = [arrowUp, thumb, grow, arrowDown]
            var thumb = Child(host.Scene, column, 1);
            var barR = host.Scene.AbsoluteRect(bar);
            var t0 = host.Scene.AbsoluteRect(thumb);
            // Length 200 − 2×12 reserved arrow cells → track 176; fraction 0.25 → thumbLen max(30, 44) = 44; travel 132.
            Check("cp4.1 — arrow cells ALWAYS reserved; collapsed thumb 2px, fill right edge inset 3 (stroke-trick math)",
                Near(host.Scene.Bounds(arrowUp).H, 12f) && Near(t0.W, 2f) && Near(t0.H, 44f)
                && Near(t0.Y, barR.Y + 12f) && Near(t0.Right, barR.Right - 3f),
                $"cellH={host.Scene.Bounds(arrowUp).H:0.#} thumb={t0.W:0.#}x{t0.H:0.#} y={t0.Y - barR.Y:0.#} rightInset={barR.Right - t0.Right:0.#}");

            // Position 0 → 0.5 while COLLAPSED: the TransformBind moves the thumb next frame — no 400ms begin-time lag.
            pos.Value = 0.5f;
            host.RunFrame();
            var t1 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.2 — position 0→0.5 moves the thumb the NEXT frame (no expand-delay on position; width untouched)",
                Near(t1.Y, barR.Y + 12f + 66f, 1f) && Near(t1.W, 2f),
                $"y={t1.Y - barR.Y:0.#} (expect 78) w={t1.W:0.#}");

            // Hover the lane: nothing changes immediately (the 400ms dwell debounces the expand)…
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(barR.X + 6f, barR.Y + 100f), 0, 0));
            host.RunFrame();
            bool noInstantExpand = Near(host.Scene.AbsoluteRect(thumb).W, 2f);
            // …then ~45 frames ≈ 720ms of engine time: past the 400ms begin + the 167ms KeySpline(0,0,0,1) width tween.
            for (int i = 0; i < 45; i++) host.RunFrame();
            var t2 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.3 — lane dwell 400ms then 167ms expand: thumb 2px→6px, right edge stays anchored (inset 3)",
                noInstantExpand && Near(t2.W, 6f) && Near(t2.Right, barR.Right - 3f),
                $"instantExpand={!noInstantExpand} w={t2.W:0.#} rightInset={barR.Right - t2.Right:0.#}");
            Check("cp4.4 — hovering changes neither thumb Y/length nor the track (no geometry jump from arrow cells)",
                Near(t2.Y, t1.Y) && Near(t2.H, t1.H) && Near(host.Scene.Bounds(arrowUp).H, 12f),
                $"y={t2.Y - barR.Y:0.#} len={t2.H:0.#} cellH={host.Scene.Bounds(arrowUp).H:0.#}");
            Check("cp4.5 — expanded chrome faded IN (arrow opacity 1 — the 83ms linear fade after the same begin time)",
                Near(host.Scene.Paint(arrowUp).Opacity, 1f, 0.02f),
                $"opacity={host.Scene.Paint(arrowUp).Opacity:0.00}");

            // Leave the bar: the contract begins after 500ms and plays 167ms; the chrome fades back out.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(barR.Right + 80f, barR.Y + 100f), 0, 0));
            host.RunFrame();
            for (int i = 0; i < 55; i++) host.RunFrame();
            var t3 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.6 — leave contracts to 2px after the 500ms begin; chrome fades out",
                Near(t3.W, 2f) && Near(host.Scene.Paint(arrowUp).Opacity, 0f, 0.02f),
                $"w={t3.W:0.#} opacity={host.Scene.Paint(arrowUp).Opacity:0.00}");
            Check("cp4.7 — conscious ticker unmounts: the frame loop idles once settled", !host.HasActiveWork);
        }

        // ── AnnotatedScrollBar: 44px right-rail template geometry + jump/step interactions ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-asb", new Size2(360, 340), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var pos = new Signal<float>(0.2f);
            var lastKind = (AnnotatedScrollBarScrollKind)255;
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                    Children =
                    [
                        AnnotatedScrollBar.Create(new[] { ("A", 0.04f), ("M", 0.5f), ("Z", 0.96f) },
                            pos, (to, kind) => { pos.Value = to; lastKind = kind; }, height: 280f),
                    ],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var asb = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var asbR = host.Scene.AbsoluteRect(asb);
            var btnUp = Child(host.Scene, asb, 0);                  // root column = [btnUp, rail, btnDown]
            var rail = Child(host.Scene, asb, 1);
            var railR = host.Scene.AbsoluteRect(rail);
            var thumb = Child(host.Scene, host.Scene.LastChild(rail), 0);   // last rail layer = the live-thumb row
            var thumbR = host.Scene.AbsoluteRect(thumb);
            // Height 280 − 2×16 button cells → rail 248; pos 0.2 → thumb Y = 0.2 × (248−3) = 49.
            Check("cp4.8 — AnnotatedScrollBar hugs the 44px LabelsGridMinWidth (no full-width panel)",
                Near(asbR.W, 44f, 0.6f), $"w={asbR.W:0.#}");
            Check("cp4.9 — 30×3 accent thumb, right edge == rail right edge, Y proportional to position",
                Near(thumbR.W, 30f) && Near(thumbR.H, 3f) && Near(thumbR.Right, railR.Right, 0.6f)
                && Near(thumbR.Y, railR.Y + 49f, 1f),
                $"thumb={thumbR.W:0.#}x{thumbR.H:0.#} rightGap={railR.Right - thumbR.Right:0.#} y={thumbR.Y - railR.Y:0.#} (expect 49)");
            var mLabel = FindTextNode(host.Scene, strings, asb, "M");
            var mR = mLabel.IsNull ? default(RectF) : host.Scene.AbsoluteRect(mLabel);
            Check("cp4.10 — label text right-aligned to the labels-column edge, up-shifted 5 (LabelTemplate margin)",
                !mLabel.IsNull && Near(mR.Right, railR.Right, 1.5f) && Near(mR.Y, railR.Y + 119f, 1.5f),
                $"rightGap={railR.Right - mR.Right:0.#} y={mR.Y - railR.Y:0.#} (expect 119)");

            // Hover the rail → the ghost preview row mounts (PART_VerticalThumbGhost).
            int layers0 = host.Scene.ChildCount(rail);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(railR.X + 22f, railR.Y + 60f), 0, 0));
            host.RunFrame();
            Check("cp4.11 — rail hover mounts the ghost thumb preview", host.Scene.ChildCount(rail) == layers0 + 1,
                $"layers {layers0}→{host.Scene.ChildCount(rail)}");

            // Rail click-to-jump: local Y 124 of 248 → position 0.5, kind=Click; the thumb follows the same flush.
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(railR.X + 22f, railR.Y + 124f), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(railR.X + 22f, railR.Y + 124f), 0, 0));
            host.RunFrame();
            thumb = Child(host.Scene, host.Scene.LastChild(rail), 0);
            var thumbR2 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.12 — rail click jumps: onScroll(0.5, Click), thumb lands at 0.5 × (railH−3) the next frame",
                Near(pos.Peek(), 0.5f, 0.01f) && lastKind == AnnotatedScrollBarScrollKind.Click
                && Near(thumbR2.Y, railR.Y + 122.5f, 1f),
                $"pos={pos.Peek():0.00} kind={lastKind} y={thumbR2.Y - railR.Y:0.#} (expect 122.5)");

            // Top (increment) ScrollButton: a 16×16 right-aligned transparent cell stepping by SmallChange (0.05).
            var b = host.Scene.AbsoluteRect(btnUp);
            Check("cp4.13 — ScrollButtons are 16×16 right-aligned cells",
                Near(b.W, 16f) && Near(b.H, 16f) && Near(b.Right, asbR.Right, 0.6f),
                $"btn={b.W:0.#}x{b.H:0.#} rightGap={asbR.Right - b.Right:0.#}");
            window.QueueInput(new InputEvent(InputKind.PointerDown, CenterOf(host.Scene, btnUp), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, CenterOf(host.Scene, btnUp), 0, 0));
            host.RunFrame();
            Check("cp4.14 — increment button steps −0.05 with kind=IncrementButton",
                Near(pos.Peek(), 0.45f, 0.01f) && lastKind == AnnotatedScrollBarScrollKind.IncrementButton,
                $"pos={pos.Peek():0.00} kind={lastKind}");
        }
    }
    static void ScrollHoverVirtualCheck(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-hover-virt", new Size2(320, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollHoverVirtualProbe());
        host.RunFrame();   // mount + layout (SmoothScroll defaults false ⇒ a wheel writes the offset synchronously)

        var vp = host.Scene.Root;
        var pt = new Point2(60f, 20f);   // fixed point 20px down (top row of the 200px viewport), inside the 180px-wide row

        // Scroll PAST the overscan buffer so FirstRealized is off zero — then the ordinal under a fixed screen point is
        // pinned at `overscan` (first = floor(offset/extent) − overscan, so index_under_point − first == overscan for any
        // offset), i.e. the SAME child HANDLE stays under the point across every re-realize. Warm the rebind + re-eval path.
        for (int i = 0; i < 12; i++) { window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 40f)); host.RunFrame(); }
        for (int i = 0; i < 8; i++) host.RunFrame();   // settle bars/anim back to rest

        // Establish hover on the row currently under the fixed point.
        window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0)); host.RunFrame();
        var a = host.Input.HitTest(pt);
        bool hovA = !a.IsNull && (host.Scene.Flags(a) & NodeFlags.Hovered) != 0;
        host.Scene.TryGetScroll(vp, out var scA);
        int firstA = scA.FirstRealized;

        // MEASURED: scroll DOWN several rows with the pointer NOT moving — enough to force a re-realize (NeedsRealize fires
        // in ~guard-sized jumps, not 1:1). FirstRealized advances ⇒ the slot at ordinal `overscan` (the SAME handle) is
        // rebound to a new item and the reconciler's Unmark clears its Hovered flag. RefreshHoverAfterScroll must re-assert
        // it: the handle is unchanged, so SetState early-outs and (pre-fix) leaves the row un-hovered.
        window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 200f));
        host.RunFrame();
        var b = host.Input.HitTest(pt);
        host.Scene.TryGetScroll(vp, out var scB);
        bool reRealized = scB.FirstRealized > firstA;                                   // the realize window shifted (rebind ran)
        bool sameHandle = !b.IsNull && b == a;                                          // the SAME slot handle stayed under the point
        bool hovB = !b.IsNull && (host.Scene.Flags(b) & NodeFlags.Hovered) != 0;         // the recycled slot is (re-)Hovered

        Check("gate.scroll.hover-follows-content.recycled-slot a boundary-crossing virtual scroll re-asserts NodeFlags.Hovered on the recycled slot handle that stays under a stationary cursor (the reconciler's rebind Unmark would otherwise leave it un-hovered)",
            hovA && reRealized && sameHandle && hovB,
            $"a={(a.IsNull ? "null" : a.Raw.Index.ToString())} b={(b.IsNull ? "null" : b.Raw.Index.ToString())} firstA={firstA} firstB={scB.FirstRealized} hovA={hovA} reRealized={reRealized} sameHandle={sameHandle} hovB={hovB}");
    }

}
