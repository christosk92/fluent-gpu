using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// P1 of the retained tiled content layer (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.2/§A.3/§A.8): the
/// RECORDER PARTITION, driven through the real headless host (SingleThread: the UI thread records into the slice arenas,
/// builds the <see cref="Rhi.CompositeFrame"/> and runs the SliceTable per-turn flow; the headless device takes
/// <see cref="Rhi.IGpuDevice.SubmitComposite"/>). The slice list matches paint order over static + scroll + sticky +
/// effect + nested scroller; a pure scroll tick records zero bytes (every slice kept, only composite parameters move);
/// a hover in a list row invalidates exactly one tile (Content); a row realized into view invalidates by PrimCount; a
/// theme change invalidates every retained tile (BackgroundOrTheme).
/// </summary>
static class SliceSuite
{
    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        PaintOrderChecks(strings, fonts);
        ScrollTickChecks(strings, fonts);
        HoverOneTileChecks(strings, fonts);
        RealizePrimCountChecks(strings, fonts);
        ThemeChecks(strings, fonts);
        FadeDistributionChecks(strings, fonts);
        FoldKeepsFadeChecks(strings, fonts);
        BlurRowsFollowScrollChecks(strings, fonts);
        StickyClipCompositeChecks(strings, fonts);
        EdgeCueIsFeatherChecks(strings, fonts);
        ChromeOverFeatherChecks(strings, fonts);
        SliceRootLayerChecks(strings, fonts);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────

    const float W = 400f, H = 600f;

    sealed class PageProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = W, Height = H, Fill = ColorF.FromRgba(10, 10, 12),
            Children =
            [
                new BoxEl { Height = 40f, Fill = ColorF.FromRgba(30, 30, 36) },                        // static header
                new ScrollEl
                {
                    Height = 400f, Fill = ColorF.FromRgba(16, 16, 20),
                    Content = new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Children =
                        [
                            new BoxEl { Height = 60f, Fill = ColorF.FromRgba(60, 20, 20) },
                            new BoxEl { Height = 40f, Fill = ColorF.FromRgba(20, 60, 20) }.Sticky(0f),          // sticky: an effect slice
                            new ScrollEl                                                                       // nested scroller
                            {
                                Horizontal = true, Height = 80f,
                                Content = new BoxEl { Width = 1200f, Height = 80f, Fill = ColorF.FromRgba(20, 20, 60) },
                            },
                            new BoxEl { Height = 60f, Fill = ColorF.FromRgba(200, 200, 40), Opacity = 0.5f, OpacityGroup = true }, // effect
                            new BoxEl { Height = 2000f, Fill = ColorF.FromRgba(40, 40, 40) },
                        ],
                    },
                },
                new BoxEl { Height = 60f, Fill = ColorF.FromRgba(36, 30, 30) },                        // static footer
            ],
        };
    }

    sealed class ListProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = W, Height = 300f,
            Children =
            [
                ItemsView.Create(400,
                    i => new BoxEl
                    {
                        Width = W - 40f, Height = 40f, Fill = ColorF.FromRgba(24, 24, 28), HoverFill = ColorF.FromRgba(80, 80, 90),
                        OnClick = () => { },
                        Children = [new BoxEl { Width = 20f, Height = 20f, Fill = ColorF.FromRgba((byte)(i % 255), 90, 90) }],
                    },
                    RepeatLayout.Stack(40f)),
            ],
        };
    }

    static AppHost Host(StringTable strings, HeadlessFontSystem fonts, Component root, out HeadlessWindow window, out HeadlessGpuDevice device,
        out HeadlessPlatformApp app, float h = H)
    {
        app = new HeadlessPlatformApp();
        window = new HeadlessWindow(new WindowDesc("slices", new Size2(W, h), 1f));
        window.Show();
        device = new HeadlessGpuDevice();
        return new AppHost(app, window, device, fonts, strings, root);
    }

    static void Collect(SceneStore s, NodeHandle n, List<NodeHandle> scrollers)
    {
        if (n.IsNull) return;
        if (s.HasScroll(n)) scrollers.Add(n);
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Collect(s, c, scrollers);
    }

    static NodeHandle FindOpacityGroup(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).OpacityGroup) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindOpacityGroup(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    static int SumInvalidations(SliceTable t)
    {
        int n = 0;
        for (int r = 1; r < SliceTable.ReasonCount; r++) n += t.InvalidationCount((InvalidationReason)r);
        return n;
    }

    // ── gate.slices.paint-order ───────────────────────────────────────────────────────────────────────────────────
    static void PaintOrderChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new PageProbe(), out _, out var device, out var app);
        using var _app = app;
        for (int i = 0; i < 4; i++) host.RunFrame();
        var s = host.Scene;
        var scrollers = new List<NodeHandle>();
        Collect(s, s.Root, scrollers);
        NodeHandle outer = scrollers.Count > 0 ? scrollers[0] : NodeHandle.Null;
        NodeHandle nested = scrollers.Count > 1 ? scrollers[1] : NodeHandle.Null;
        NodeHandle outerContent = outer.IsNull ? default : s.ScrollRef(outer).ContentNode;
        NodeHandle nestedContent = nested.IsNull ? default : s.ScrollRef(nested).ContentNode;
        NodeHandle sticky = NodeHandle.Null;
        foreach (var kv in s.ScrollEffects) { sticky = s.HandleAt(kv.Key); break; }
        NodeHandle group = FindOpacityGroup(s, s.Root);

        Span<(int NodeIndex, uint Gen, SliceRole Role, SliceKind Kind)> order = stackalloc (int, uint, SliceRole, SliceKind)[32];
        int n = host.UiSlices.CopySliceOrder(order);
        // Painter order: the static root, the outer scroll content; inside it — in child order — the nested scroller's
        // content, then the opacity group, and the sticky header LAST (an engaged-or-not sticky child is cut where the
        // walk emits it; pinned ones paint after their siblings). Thumbs appear only while a scrollbar is visible.
        var got = new List<(int, SliceRole, SliceKind)>();
        for (int i = 0; i < n && i < order.Length; i++)
            if (order[i].Role != SliceRole.Thumb) got.Add((order[i].NodeIndex, order[i].Role, order[i].Kind));
        // Each scroller's default edge cue is its analytic edge feather: the viewport is an Effect slice (the fade on its
        // marker) around its content.
        var want = new List<(int, SliceRole, SliceKind)>
        {
            ((int)s.Root.Raw.Index, SliceRole.Main, SliceKind.Static),
            ((int)outer.Raw.Index, SliceRole.Main, SliceKind.Effect),
            ((int)outerContent.Raw.Index, SliceRole.Main, SliceKind.Scroll),
            ((int)sticky.Raw.Index, SliceRole.Main, SliceKind.Effect),
            ((int)nested.Raw.Index, SliceRole.Main, SliceKind.Effect),
            ((int)nestedContent.Raw.Index, SliceRole.Main, SliceKind.Scroll),
            ((int)group.Raw.Index, SliceRole.Main, SliceKind.Effect),
        };
        bool match = got.Count == want.Count;
        for (int i = 0; match && i < want.Count; i++) match = got[i] == want[i];
        var detail = new System.Text.StringBuilder();
        foreach (var g in got) detail.Append($"{g.Item1}:{g.Item2}/{g.Item3} ");
        Check("gate.slices.paint-order the slice list over static + scroll + sticky + nested scroller + group-opacity effect is the painter order: root(Static) → outer viewport's edge feather(Effect) → outer content(Scroll) → sticky(Effect) → nested viewport's edge feather(Effect) → nested content(Scroll) → opacity group(Effect)",
            !outer.IsNull && !nested.IsNull && !sticky.IsNull && !group.IsNull && match,
            $"got=[{detail}] want root={s.Root.Raw.Index} outer={outerContent.Raw.Index} sticky={sticky.Raw.Index} nested={nestedContent.Raw.Index} group={group.Raw.Index}");

        // The composite frame's items follow the same order (segments interleave: a parent's stream splits around each
        // child), and the headless device took the composite seam.
        var rows = host.UiSlices.LastRows;
        var items = host.UiSlices.LastItems;
        bool composited = device.CompositeFrameCount > 0 && items.Length >= want.Count && rows.Length >= want.Count;
        int wi = 0;
        for (int i = 0; i < rows.Length && wi < want.Count; i++)
            if (rows[i].NodeIndex == want[wi].Item1 && (rows[i].Sub & 7) == (int)want[wi].Item2) wi++;
        Check("gate.slices.composite-items the CompositeFrame rows/items are painter-ordered segments of the slice list (every slice appears, in slice order) and the headless device took SubmitComposite",
            composited && wi == want.Count,
            $"frames={device.CompositeFrameCount} rows={rows.Length} items={items.Length} matchedInOrder={wi}/{want.Count}");
    }

    // ── gate.slices.scroll-tick-zero-bytes ────────────────────────────────────────────────────────────────────────
    static void ScrollTickChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new PageProbe(), out _, out var device, out var app);
        using var _app = app;
        for (int i = 0; i < 4; i++) host.RunFrame();
        var scrollers = new List<NodeHandle>();
        Collect(host.Scene, host.Scene.Root, scrollers);
        var vp = scrollers[0];
        var handle = host.TryGetScrollHandle(vp)!;
        // Warm into the middle of the list (away from both ends, where the edge cues ramp) and let the scrollbar
        // reveal finish: from then on a scroll tick changes nothing the recorder reads.
        handle.ScrollTo(400.0, ScrollMove.Immediate);
        for (int i = 0; i < 90; i++) { handle.ScrollTo(400.0 + (i % 2), ScrollMove.Immediate); host.RunFrame(); }

        long bytes = 0;
        int composite = 0, ticks = 0, framesBefore = device.CompositeFrameCount;
        float firstDy = float.NaN, lastDy = float.NaN;
        for (int i = 0; i < 6; i++)
        {
            handle.ScrollTo(420.0 + 7.0 * i, ScrollMove.Immediate);
            var f = host.RunFrame();
            ticks++;
            bytes += host.LastStats.Slices.BytesRecorded;
            if (host.LastStats.CompositeOnlyTurn) composite++;
            float dy = FirstFillDy(device, ColorF.FromRgba(200, 200, 40));
            if (i == 0) firstDy = dy;
            lastDy = dy;
        }
        bool moved = !float.IsNaN(firstDy) && Math.Abs((firstDy - lastDy) - 35f) < 0.51f;
        Check("gate.slices.scroll-tick-zero-bytes a pure scroll tick records 0 bytes — every slice kept whole, a composite-only turn — while the composited frame still moves the content (and its sticky/thumb poses) by exactly the scroll",
            bytes == 0 && composite == ticks && device.CompositeFrameCount - framesBefore == ticks && moved,
            $"bytes={bytes} compositeOnly={composite}/{ticks} submits={device.CompositeFrameCount - framesBefore} groupDy={firstDy:0.##}->{lastDy:0.##}");
    }

    static float FirstFillDy(HeadlessGpuDevice d, ColorF fill)
    {
        foreach (var r in d.LastRects)
            if (Math.Abs(r.Fill.R - fill.R) < 0.01f && Math.Abs(r.Fill.G - fill.G) < 0.01f && Math.Abs(r.Fill.B - fill.B) < 0.01f)
                return r.Transform.Dy;
        return float.NaN;
    }

    // ── gate.slices.hover-one-tile ────────────────────────────────────────────────────────────────────────────────
    static void HoverOneTileChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new ListProbe(), out var window, out var device, out var app, 300f);
        using var _app = app;
        for (int i = 0; i < 6; i++) host.RunFrame();
        var t = host.UiSliceTable;
        // Rest the pointer on row 0 until its hover visuals and the viewport's scrollbar reveal settle (the reveal is a
        // real change of the viewport's own chrome), then move to row 1: every COMPOSITED frame of that hover change (row 0
        // fading out, row 1 in — both inside one tile) must invalidate exactly one tile, reason Content. (A skip-submitted
        // frame composites nothing, so the per-turn census is read only on frames that reached the device.)
        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(100f, 20f), 0, 0));
        for (int i = 0; i < 90; i++) host.RunFrame();
        int tilesBefore = t.LiveTiles;
        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(100f, 60f), 0, 0));   // over row 1
        int composited = 0, content = 0, all = 0, worst = 0;
        for (int i = 0; i < 30; i++)
        {
            int before = device.CompositeFrameCount;
            host.RunFrame();
            if (device.CompositeFrameCount == before) continue;
            composited++;
            int c = t.InvalidationCount(InvalidationReason.Content), a = SumInvalidations(t);
            if (composited == 1) { content = c; all = a; }
            if (a > worst) worst = a;
        }
        Check("gate.slices.hover-one-tile a hover in a list row invalidates exactly ONE retained tile, reason Content, on every composited frame of the hover change (the rows repainted in place; nothing else in their slice or any other)",
            tilesBefore > 0 && composited > 0 && content == 1 && all == 1 && worst == 1,
            $"tiles={tilesBefore} composited={composited} firstContent={content} firstAll={all} worstFrame={worst}");
    }

    // ── gate.slices.realize-primcount ─────────────────────────────────────────────────────────────────────────────
    static void RealizePrimCountChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new ListProbe(), out _, out var device, out var app, 300f);
        using var _app = app;
        for (int i = 0; i < 6; i++) host.RunFrame();
        var scrollers = new List<NodeHandle>();
        Collect(host.Scene, host.Scene.Root, scrollers);
        var handle = host.TryGetScrollHandle(scrollers[0])!;
        var t = host.UiSliceTable;
        int prim = 0, contentOnly = 0;
        // Walk down far enough that rows realize into (and park out of) the covered window.
        for (int i = 1; i <= 30; i++)
        {
            handle.ScrollTo(40.0 * i, ScrollMove.Immediate);
            int before = device.CompositeFrameCount;
            host.RunFrame();
            if (device.CompositeFrameCount == before) continue;
            prim += t.InvalidationCount(InvalidationReason.PrimCount);
            contentOnly += t.InvalidationCount(InvalidationReason.Content);
        }
        Check("gate.slices.realize-primcount rows realized into / parked out of a scroll slice invalidate their tiles with reason PrimCount (a primitive appeared/vanished)",
            prim > 0,
            $"primCount={prim} content={contentOnly}");
    }

    sealed class ThemedProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = W, Height = H, Fill = Tok.FillCardDefault,
            Children =
            [
                new BoxEl { Height = 40f, Fill = Tok.FillSubtleSecondary },
                new ScrollEl
                {
                    Height = 400f,
                    Content = new BoxEl { Direction = 1, Height = 2000f, Fill = Tok.FillCardDefault },
                },
            ],
        };
    }

    // ── gate.slices.theme-whole-slice ─────────────────────────────────────────────────────────────────────────────
    static void ThemeChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var kind0 = Tok.Theme;
        try
        {
            using var host = Host(strings, fonts, new ThemedProbe(), out _, out var device, out var app);
            using var _app = app;
            for (int i = 0; i < 4; i++) host.RunFrame();
            var t = host.UiSliceTable;
            int live = t.LiveTiles;
            Tok.Use(kind0 == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);
            host.RequestThemeTransition(0f);
            int before = device.CompositeFrameCount;
            host.RunFrame();
            int theme = t.InvalidationCount(InvalidationReason.BackgroundOrTheme);
            int content = t.InvalidationCount(InvalidationReason.Content);
            Check("gate.slices.theme-whole-slice a theme change invalidates EVERY retained tile of every slice with reason BackgroundOrTheme (no per-node Content claims them first)",
                live > 0 && device.CompositeFrameCount > before && theme == live && content == 0,
                $"liveTiles={live} composited={device.CompositeFrameCount - before} theme={theme} content={content}");
        }
        finally { Tok.Use(kind0); }
    }

    // ── composite fade groups (docs/plans/composite-fade-groups-implementation.md; gpu-renderer.md §13.1e) ──────────

    /// <summary>A vertical AutoEdgeFade page (no fill of its own) over three horizontal AutoEdgeFade shelves (no fill), a
    /// fourth shelf WITH a fill (its own paint overlaps its content inside the band — it can only composite as a group)
    /// and an opacity group over two overlapping children. <see cref="Hot"/> recolours the filled shelf's first card.</summary>
    sealed class FadeShelvesProbe : Component
    {
        public static readonly Signal<int> Hot = new(0);
        public const float ShelfH = 80f, CardW = 90f, Top = 300f;

        public override Element Render() => new BoxEl
        {
            Width = W, Height = H,
            Children =
            [
                new ScrollEl
                {
                    Width = W, Height = H, AutoEdgeFade = true, SuppressScrollBar = true,
                    Content = new BoxEl
                    {
                        Direction = 1, Gap = 20f, Padding = new Edges4(0f, Top, 0f, 900f), MinWidth = 0f,
                        Children =
                        [
                            Shelf(0, false), Shelf(1, false), Shelf(2, false), Shelf(3, true),
                            new BoxEl
                            {
                                Height = 60f, Opacity = 0.5f, OpacityGroup = true, ZStack = true,
                                Children =
                                [
                                    new BoxEl { AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = 200f, Height = 60f, Fill = ColorF.FromRgba(200, 60, 60) },
                                    new BoxEl { AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(100f, 0f, 0f, 0f), Width = 200f, Height = 60f, Fill = ColorF.FromRgba(60, 60, 200) },
                                ],
                            },
                        ],
                    },
                },
            ],
        };

        static Element Shelf(int s, bool fill)
        {
            var cards = new Element[10];
            for (int i = 0; i < cards.Length; i++)
                cards[i] = new BoxEl
                {
                    Width = CardW, Height = ShelfH,
                    Fill = fill && i == 0
                        ? Prop.Of(() => Hot.Value % 2 == 0 ? ColorF.FromRgba(220, 120, 40) : ColorF.FromRgba(40, 120, 220))
                        : (Prop<ColorF>)ColorF.FromRgba((byte)(60 + i * 17), (byte)(80 + s * 30), 120),
                };
            return new ScrollEl
            {
                Horizontal = true, AutoEdgeFade = true, SuppressScrollBar = true, Height = ShelfH,
                Fill = fill ? ColorF.FromRgba(40, 50, 70) : ColorF.Transparent,
                Content = new BoxEl { Direction = 0, Gap = 10f, Children = cards },
            };
        }
    }

    /// <summary>The composite item of the MAIN slice cut at <paramref name="node"/> (its first segment), or false.</summary>
    static bool TryItemOfNode(AppHost host, NodeHandle node, out CompositeItem item)
    {
        item = default;
        var rows = host.UiSlices.LastRows;
        int id = -1;
        for (int i = 0; i < rows.Length; i++)
            if (rows[i].NodeIndex == (int)node.Raw.Index && rows[i].Gen == node.Raw.Gen && (rows[i].Sub & 7) == (int)SliceRole.Main) { id = rows[i].Id; break; }
        if (id < 0) return false;
        foreach (var it in host.UiSlices.LastItems)
            if (it.SliceId == id && it.Kind is CompositeKind.Tiles or CompositeKind.Region or CompositeKind.Direct) { item = it; return true; }
        return false;
    }

    static int CountKind(AppHost host, CompositeKind kind)
    {
        int n = 0;
        foreach (var it in host.UiSlices.LastItems) if (it.Kind == kind) n++;
        return n;
    }

    // ── gate.slices.fade-leaf / fade-follows-page / group-not-rerendered ──────────────────────────────────────────
    static void FadeDistributionChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        FadeShelvesProbe.Hot.Value = 0;
        using var host = Host(strings, fonts, new FadeShelvesProbe(), out _, out var device, out var app);
        using var _app = app;
        for (int i = 0; i < 4; i++) host.RunFrame();
        var s = host.Scene;
        var scrollers = new List<NodeHandle>();
        Collect(s, s.Root, scrollers);
        bool shaped = scrollers.Count == 5;
        var page = shaped ? scrollers[0] : NodeHandle.Null;
        // Page to 400 (mid-list: both page bands live), every shelf to 200 (both shelf bands live); then settle.
        if (shaped)
        {
            host.TryGetScrollHandle(page)!.ScrollTo(400.0, ScrollMove.Immediate);
            for (int k = 1; k < 5; k++) host.TryGetScrollHandle(scrollers[k])!.ScrollTo(200.0, ScrollMove.Immediate);
        }
        FluentGpu.VerticalSlice.Harness.Asserts.ElapseFrames(host, 30);   // at rest: time passes and the slices store their spans

        // fade-leaf: the page fade and the three plain shelves composite as analytic per-item feathers — no group
        // surface; the filled shelf stays the ONE group (its item carrying the page feather); a visible plain shelf's
        // content carries BOTH feathers (the page's, then its own: the exact product); the opacity group's leaf carries
        // the page's.
        int groups = CountKind(host, CompositeKind.Group);
        CompositeItem s1 = default;
        bool shelf1 = shaped && TryItemOfNode(host, s.ScrollRef(scrollers[2]).ContentNode, out s1);
        bool shelf1Both = shelf1 && s1.Inherited == 2 && !s1.Feather.IsNone && !s1.Feather2.IsNone;
        CompositeItem grp = default;
        foreach (var it in host.UiSlices.LastItems) if (it.Kind == CompositeKind.Group) { grp = it; break; }
        bool groupCarriesPage = groups == 1 && grp.Inherited == 1 && !grp.Feather.IsNone && !grp.Feather2.IsNone;
        int feathered = 0;
        foreach (var it in host.UiSlices.LastItems) if (it.Inherited > 0) feathered++;
        var st = host.UiSlices.LastStats;
        Check("gate.slices.fade-leaf an AutoEdgeFade page over AutoEdgeFade shelves with no paint of their own composites every fade as an analytic per-item feather — no group surface; a filled shelf stays the ONE group (carrying the page feather); a visible shelf's content carries both feathers (the exact product); the distributable fades spend no effect budget",
            shaped && groups == 1 && shelf1Both && groupCarriesPage && feathered >= 5 && st.FreeFades >= 4,
            $"scrollers={scrollers.Count} groups={groups} shelf1=(found={shelf1} inherited={s1.Inherited} f1={!s1.Feather.IsNone} f2={!s1.Feather2.IsNone}) group=(inherited={grp.Inherited} f1={!grp.Feather.IsNone} f2={!grp.Feather2.IsNone}) featheredItems={feathered} freeFades={st.FreeFades} effectSlices={st.EffectSlices}");

        // fade-follows-page: a page scroll tick is composite-only (0 bytes) and moves the shelf's own feather with the
        // page while the page's feather (the viewport's) stays put.
        // The first scroll move after a programmatic jump is recorded in full (the realize catch-up): a bar nobody sees no longer ticks
        // frames after it (F238), so absorb that frame here and measure the NEXT tick, the steady page scroll the gate is about.
        host.TryGetScrollHandle(page)!.ScrollTo(393.0, ScrollMove.Immediate);
        host.RunFrame();
        TryItemOfNode(host, s.ScrollRef(scrollers[2]).ContentNode, out s1);
        float pageY0 = s1.Feather.Rect.Y, shelfY0 = s1.Feather2.Rect.Y;
        int framesBefore = device.CompositeFrameCount;
        host.TryGetScrollHandle(page)!.ScrollTo(400.0, ScrollMove.Immediate);
        host.RunFrame();
        bool compositeOnly = host.LastStats.CompositeOnlyTurn && host.LastStats.Slices.BytesRecorded == 0 && device.CompositeFrameCount > framesBefore;
        bool found2 = TryItemOfNode(host, s.ScrollRef(scrollers[2]).ContentNode, out var s1b);
        bool follows = found2 && s1b.Inherited == 2 && MathF.Abs(s1b.Feather.Rect.Y - pageY0) < 0.01f
            && MathF.Abs((s1b.Feather2.Rect.Y - shelfY0) - (-7f)) < 0.01f;
        Check("gate.slices.fade-follows-page a page scroll tick records 0 bytes (composite-only) and the distributed shelf feather rides the page by exactly the scroll while the page's own feather stays viewport-fixed",
            compositeOnly && follows,
            $"compositeOnly={host.LastStats.CompositeOnlyTurn} bytes={host.LastStats.Slices.BytesRecorded} pageFeatherY {pageY0:0.##}->{s1b.Feather.Rect.Y:0.##} shelfFeatherY {shelfY0:0.##}->{s1b.Feather2.Rect.Y:0.##}");

        // group-not-rerendered: the filled shelf's group surface is re-drawn from its retained surface on a page scroll
        // (it moved rigidly: same content key), re-rendered after its own shelf scrolls (relative placement changed) and
        // after a paint change inside it (a tile re-rastered).
        CompositeRecord? PrepareOfGroup()
        {
            foreach (var r in device.LastCompositeRecords) if (r.Kind == CompositeRecordKind.PrepareGroup) return r;
            return null;
        }
        host.TryGetScrollHandle(page)!.ScrollTo(412.0, ScrollMove.Immediate);
        host.RunFrame();
        var onPage = PrepareOfGroup();
        host.TryGetScrollHandle(scrollers[4])!.ScrollTo(230.0, ScrollMove.Immediate);
        host.RunFrame();
        var onShelf = PrepareOfGroup();
        for (int i = 0; i < 4; i++) host.RunFrame();
        FadeShelvesProbe.Hot.Value++;
        CompositeRecord? onPaint = null;
        for (int i = 0; i < 6 && onPaint is null; i++)
        {
            int before = device.CompositeFrameCount;
            host.RunFrame();
            if (device.CompositeFrameCount == before) continue;
            var r = PrepareOfGroup();
            if (r is { Hit: false }) onPaint = r;
        }
        Check("gate.slices.group-not-rerendered a group moved rigidly by a page scroll is re-drawn from its retained surface (op-log PrepareGroup Hit, a nonzero content key); its own shelf scroll and a paint change inside it re-render it (a key miss)",
            onPage is { Hit: true, Key: not 0UL } && onShelf is { Hit: false } && onPaint is { Hit: false },
            $"pageScroll={(onPage is null ? "none" : $"hit={onPage.Value.Hit} key={onPage.Value.Key:X}")} shelfScroll={(onShelf is null ? "none" : $"hit={onShelf.Value.Hit}")} paint={(onPaint is null ? "none" : "miss")} cache={device.LastCompositeCache}");
    }

    /// <summary>Twenty horizontal AutoEdgeFade shelves WITH a fill (their fades cannot distribute — they spend the effect
    /// budget), so four fold past <see cref="SliceRecorder.EffectSliceCap"/>.</summary>
    sealed class FoldProbe : Component
    {
        public const int Shelves = 20;
        public override Element Render()
        {
            var shelves = new Element[Shelves];
            for (int s = 0; s < shelves.Length; s++)
            {
                var cards = new Element[8];
                for (int i = 0; i < cards.Length; i++)
                    cards[i] = new BoxEl { Width = 80f, Height = 24f, Fill = ColorF.FromRgba((byte)(60 + i * 20), (byte)(40 + s * 9), 140) };
                shelves[s] = new ScrollEl
                {
                    Horizontal = true, AutoEdgeFade = true, SuppressScrollBar = true, Height = 24f,
                    Fill = ColorF.FromRgba(30, 34, 44),
                    Content = new BoxEl { Direction = 0, Gap = 6f, Children = cards },
                };
            }
            return new BoxEl { Width = W, Height = H, Direction = 1, Gap = 4f, Children = shelves };
        }
    }

    // ── gate.slices.fold-keeps-fade ───────────────────────────────────────────────────────────────────────────────
    // Past the effect budget a fade records INLINE (a PushLayer in the containing arena). Its scroll content used to be
    // cut as a slice anyway — the content cut never consulted the budget — so the tile replay skipped the marker and the
    // inline layer wrapped nothing: the folded shelves composited UNFEATHERED. Inside an inline group layer nothing is cut
    // now (the content records inline, pose baked), so every folded fade still feathers its content.
    static void FoldKeepsFadeChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new FoldProbe(), out _, out var device, out var app);
        using var _app = app;
        for (int i = 0; i < 4; i++) host.RunFrame();
        var scrollers = new List<NodeHandle>();
        Collect(host.Scene, host.Scene.Root, scrollers);
        foreach (var sc in scrollers) host.TryGetScrollHandle(sc)!.ScrollTo(100.0, ScrollMove.Immediate);
        for (int i = 0; i < 30; i++) host.RunFrame();
        var st = host.UiSlices.LastStats;
        bool noMarker = Harness.CompositeInvariants.NoMarkerInsideInlineLayer(host, out string detail);
        // every folded shelf's cards are INSIDE an edge-fade layer of the composed frame
        int cards = 0, fadedCards = 0;
        {
            ReadOnlySpan<byte> bytes = device.LastComposedStream.Bytes;
            var stack = new Stack<int>();
            int fades = 0, pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)System.Runtime.InteropServices.MemoryMarshal.Read<int>(bytes[pos..]);
                pos += sizeof(int);
                if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + body > bytes.Length) break;
                switch (op)
                {
                    case DrawOp.PushLayer:
                    {
                        int k = System.Runtime.InteropServices.MemoryMarshal.Read<PushLayerCmd>(bytes[pos..]).Kind;
                        stack.Push(k);
                        if (k == (int)LayerKind.EdgeFade) fades++;
                        break;
                    }
                    case DrawOp.PopLayer:
                        if (stack.Count > 0 && stack.Pop() == (int)LayerKind.EdgeFade) fades--;
                        break;
                    case DrawOp.FillRoundRect:
                    {
                        var r = System.Runtime.InteropServices.MemoryMarshal.Read<FillRoundRectCmd>(bytes[pos..]);
                        if (MathF.Abs(r.Rect.W - 80f) < 0.5f && MathF.Abs(r.Rect.H - 24f) < 0.5f) { cards++; if (fades > 0) fadedCards++; }
                        break;
                    }
                }
                pos += body;
            }
        }
        Check("gate.slices.fold-keeps-fade with 20 non-distributable fades (4 past the effect budget, folded inline) no slice marker sits inside an inline group layer, and every shelf's cards composite inside an edge fade",
            st.Folded >= 4 && st.EffectSlices <= SliceRecorder.EffectSliceCap && noMarker && cards > 0 && fadedCards == cards,
            $"folded={st.Folded} effectSlices={st.EffectSlices} {detail} cards={cards} fadedCards={fadedCards}");
    }

    /// <summary>The Wavee lyrics shape (Lyrics.UI.cs LyricsContent): a virtual list (AutoEdgeFade, no scrollbar) of rows
    /// whose inner wrapper carries a self-blur σ (the DOF lines), with a label each.</summary>
    sealed class BlurRowsProbe : Component
    {
        public const int Rows = 10;
        public const float RowH = 48f;
        public override Element Render() => new BoxEl
        {
            Width = W, Height = 300f,
            Children =
            [
                Virtual.ListBound(Rows, RowH, idx => new BoxEl
                {
                    Height = RowH, Direction = 1, Padding = new Edges4(16f, 8f, 16f, 8f),
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Blur = 3f,
                            Children = [new TextEl("") { Size = 18f, Text = Prop.Of(() => "line " + idx.Value), Color = ColorF.FromRgba(0xF0, 0xF0, 0xF4) }],
                        },
                    ],
                }) with { Width = W, Height = 300f, AutoEdgeFade = true, SuppressScrollBar = true },
            ],
        };
    }

    // ── gate.slices.blur-rows-follow-scroll (the lyrics-blur repro) ────────────────────────────────────────────────
    // Hypothesis under test: a self-blurred row's blur SOURCE is its RECORD-time RequiredSource (SceneRecorder: the
    // visible output clipped against the active clip, grown by the kernel reach), and a composite-only follow-scroll turn
    // only offsets it by the pose — so a row that scrolls further into view would blur a source clipped at the OLD
    // offset (or none: PrepareLeafBlur's placed.IsEmpty / empty-source early-outs), and part of it would not draw.
    // Asserted, on every composited turn of a scroll across the whole range: every visible blurred row's leaf item has
    // resident tile placements covering its source, and a source that covers the row's visible rect.
    static void BlurRowsFollowScrollChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new BlurRowsProbe(), out _, out var device, out var app, 300f);
        using var _app = app;
        for (int i = 0; i < 6; i++) host.RunFrame();
        var scrollers = new List<NodeHandle>();
        Collect(host.Scene, host.Scene.Root, scrollers);
        var vp = scrollers.Count > 0 ? scrollers[0] : NodeHandle.Null;
        var handle = vp.IsNull ? null : host.TryGetScrollHandle(vp);
        var placements = new TilePlacement[256];
        int turns = 0, compositeOnly = 0, blurItems = 0, bad = 0, maxRowsVisible = 0;
        string firstBad = "";
        RectF vpRect = vp.IsNull ? default : host.Scene.AbsoluteRect(vp);
        for (int step = 1; step <= 40 && handle is not null; step++)
        {
            handle.ScrollTo(step * 5.0, ScrollMove.Immediate);
            int before = device.CompositeFrameCount;
            host.RunFrame();
            if (device.CompositeFrameCount == before) continue;
            turns++;
            if (host.LastStats.CompositeOnlyTurn) compositeOnly++;
            var rows = host.UiSlices.LastRows;
            int visible = 0;
            foreach (var it in host.UiSlices.LastItems)
            {
                if (it.BlurSigma <= 0f || it.Kind is not (CompositeKind.Region or CompositeKind.Tiles or CompositeKind.Direct)) continue;
                int node = -1;
                foreach (var r in rows) if (r.Id == it.SliceId) { node = r.NodeIndex; break; }
                if (node < 0) continue;
                var h = host.Scene.HandleAt(node);
                if (h.IsNull || !host.Scene.IsLive(h)) continue;
                RectF rowRect = host.Scene.AbsoluteRect(h);
                RectF vis = rowRect.Intersect(vpRect);
                if (vis.IsEmpty) continue;
                visible++;
                blurItems++;
                int pc = host.UiSliceTable.CollectPlacements(it.SliceId, placements);
                RectF src = it.SourceClip;
                bool srcCovers = src.W > 0f && src.H > 0f && src.X <= vis.X + 1.5f && src.Y <= vis.Y + 1.5f
                    && src.Right >= vis.Right - 1.5f && src.Bottom >= vis.Bottom - 1.5f;
                // the resident tiles (slice origin = the item's translation) cover the source ∩ window
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                for (int p = 0; p < pc; p++)
                {
                    float px = it.Transform.Dx + placements[p].Key.Tx * TileGrid.W, py = it.Transform.Dy + placements[p].Key.Ty * TileGrid.H;
                    x0 = MathF.Min(x0, px); y0 = MathF.Min(y0, py); x1 = MathF.Max(x1, px + placements[p].W); y1 = MathF.Max(y1, py + placements[p].H);
                }
                RectF need = src.Intersect(vpRect);
                bool tilesCover = pc > 0 && (need.IsEmpty || (x0 <= need.X + 0.5f && y0 <= need.Y + 0.5f && x1 >= need.Right - 0.5f && y1 >= need.Bottom - 0.5f));
                if (!srcCovers || !tilesCover)
                {
                    bad++;
                    if (firstBad.Length == 0)
                        firstBad = $"step={step} row=({rowRect.X:0.#},{rowRect.Y:0.#},{rowRect.W:0.#}x{rowRect.H:0.#}) visible=({vis.Y:0.#}..{vis.Bottom:0.#}) src=({src.X:0.#},{src.Y:0.#},{src.W:0.#}x{src.H:0.#}) placements={pc} tiles=({x0:0.#},{y0:0.#})-({x1:0.#},{y1:0.#}) compositeOnly={host.LastStats.CompositeOnlyTurn}";
                }
            }
            maxRowsVisible = Math.Max(maxRowsVisible, visible);
        }
        Check("gate.slices.blur-rows-follow-scroll (lyrics-blur repro) across a scroll of a virtual list of self-blurred rows, every visible blurred row's leaf item keeps a blur source covering its visible rect and resident tiles covering that source on every composited turn (composite-only turns included)",
            turns > 0 && blurItems > 0 && bad == 0,
            $"turns={turns} compositeOnly={compositeOnly} blurItems={blurItems} maxVisibleRows={maxRowsVisible} bad={bad} {firstBad}");
    }
    /// <summary>The artist page's sticky-clip shape: a page scroller whose content is a ZStack of a sticky-clipped colour
    /// WASH (a plain fill) and a column [hero, MAGAZINE] where the magazine carries a top edge fade + a heading of its
    /// own (so it composites as a GROUP) + two AutoEdgeFade shelves, and is sticky-clipped at the same band. A tall
    /// spacer after the magazine gives the page its range while the magazine itself stays one tile area.</summary>
    sealed class StickyClipProbe : Component
    {
        public const float Inset = 56f, HeroH = 320f;   // the magazine starts on the 64-px grid: its group region stays inside the page viewport
        public static readonly ColorF WashColor = ColorF.FromRgba(96, 48, 140), HeadingColor = ColorF.FromRgba(230, 200, 60);

        public override Element Render() => new BoxEl
        {
            Width = W, Height = H,
            Children =
            [
                new ScrollEl
                {
                    // the page's own edge feather would put the wash under a page-wide fade group; this gate is the clip
                    Width = W, Height = H, SuppressScrollBar = true, EdgeCues = ScrollEdgeCues.None,
                    Content = new BoxEl
                    {
                        ZStack = true, MinWidth = 0f,
                        Children =
                        [
                            new BoxEl { Height = HeroH + 200f, Fill = WashColor, HitTestVisible = false }.StickyClip(Inset),
                            new BoxEl
                            {
                                Direction = 1, MinWidth = 0f,
                                Children =
                                [
                                    new BoxEl { Height = HeroH, Fill = ColorF.FromRgba(40, 90, 60) },
                                    new BoxEl
                                    {
                                        Direction = 1, Gap = 12f, Padding = new Edges4(0f, 8f, 0f, 8f),
                                        EdgeFade = new EdgeFadeSpec(EdgeMask.Top, 24f),
                                        Children = [new BoxEl { Width = 200f, Height = 30f, Fill = HeadingColor }, Shelf(0), Shelf(1)],
                                    }.StickyClip(Inset),
                                    new BoxEl { Height = 1200f },
                                ],
                            },
                        ],
                    },
                },
            ],
        };

        static Element Shelf(int s)
        {
            var cards = new Element[10];
            for (int i = 0; i < cards.Length; i++)
                cards[i] = new BoxEl { Width = 90f, Height = 80f, Fill = ColorF.FromRgba((byte)(60 + i * 17), (byte)(80 + s * 60), 120) };
            return new ScrollEl
            {
                Horizontal = true, AutoEdgeFade = true, SuppressScrollBar = true, Height = 80f,
                Content = new BoxEl { Direction = 0, Gap = 10f, Children = cards },
            };
        }
    }

    static NodeHandle FindFill(SceneStore s, NodeHandle n, ColorF fill)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).Fill == fill) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindFill(s, c, fill);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    // ── gate.slices.stickyclip-composite ──────────────────────────────────────────────────────────────────────────
    // A `.StickyClip` is a viewport-fixed clip on a node that rides the page's translation — exactly the item band's
    // shape. It used to be a PAINT channel (EffectChannel.ClipTop → NodePaint.ClipRect, a PushClip in the recorded
    // stream), so every page-scroll turn re-recorded the clipped subtree, re-rastered its tiles and re-rendered the group
    // around it. As a composite-time clip on the slice marker, a page scroll moves only composite parameters: the turn
    // records nothing, rasters nothing, and the clipped column's group is re-drawn from its retained surface.
    static void StickyClipCompositeChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new StickyClipProbe(), out _, out var device, out var app);
        using var _app = app;
        for (int i = 0; i < 4; i++) host.RunFrame();
        var s = host.Scene;
        var scrollers = new List<NodeHandle>();
        Collect(s, s.Root, scrollers);
        var page = scrollers.Count > 0 ? scrollers[0] : NodeHandle.Null;
        var handle = page.IsNull ? null : host.TryGetScrollHandle(page);
        var wash = FindFill(s, s.Root, StickyClipProbe.WashColor);
        // offset 280: the band line (viewport top + 56) sits at content 336 — 16 DIP into the magazine (top 320) and
        // 336 DIP into the wash: both clips engaged; the magazine stays inside the viewport.
        handle?.ScrollTo(280.0, ScrollMove.Immediate);
        for (int i = 0; i < 30; i++) host.RunFrame();
        long bytes = 0;
        int ticks = 0, compositeOnly = 0, rasters = 0, groupHits = 0, groupPreps = 0;
        float washClipTop = float.NaN;
        for (int i = 1; i <= 3 && handle is not null; i++)
        {
            handle.ScrollTo(280.0 + 7.0 * i, ScrollMove.Immediate);
            host.RunFrame();
            ticks++;
            bytes += host.LastStats.Slices.BytesRecorded;
            if (host.LastStats.CompositeOnlyTurn) compositeOnly++;
            foreach (var r in device.LastCompositeRecords)
            {
                if (r.Kind == CompositeRecordKind.RasterTile) rasters++;
                if (r.Kind == CompositeRecordKind.PrepareGroup) { groupPreps++; if (r.Hit) groupHits++; }
            }
            if (!wash.IsNull && TryItemOfNode(host, wash, out var wi)) washClipTop = wi.Clip.Y;
        }
        // the wash composites as its own item, cut at the band line (window px, scale 1)
        bool washCut = MathF.Abs(washClipTop - StickyClipProbe.Inset) < 0.5f;
        Check("gate.slices.stickyclip-composite a page scroll under an engaged .StickyClip records 0 bytes and rasters 0 tiles (a composite-only turn): the clipped wash is its own item cut at the band line, and the clipped column's group is re-drawn from its retained surface",
            ticks == 3 && bytes == 0 && compositeOnly == ticks && rasters == 0 && groupPreps == ticks && groupHits == ticks && washCut,
            $"ticks={ticks} bytes={bytes} compositeOnly={compositeOnly} rasters={rasters} groupPrep={groupPreps} groupHits={groupHits} washClipTop={washClipTop:0.##} groups={CountKind(host, CompositeKind.Group)} items={host.UiSlices.LastItems.Length} slices={host.UiSlices.LastStats}");

        // stickyclip-cuts-at-line: in the COMPOSED frame (the headless model of what the backend draws) the wash and the
        // magazine's heading are both cut at the band line — through the composite route, and through the paint route the
        // StickyClipInPaint knockout forces (the clip baked into the stream, re-recorded by the watched pose). The paint
        // route must stay FRESH across page scrolls: a stale baked clip would ride the content up by the scroll.
        float compositeWashTop = EffectiveClipTopOf(device, W, StickyClipProbe.HeroH + 200f);
        float compositeHeadTop = EffectiveClipTopOf(device, 200f, 30f);
        host.GpuKnockouts = GpuKnockouts.StickyClipInPaint;
        for (int i = 0; i < 6; i++) host.RunFrame();
        long paintBytes = 0;
        int paintContent = 0, paintTicks = 0;
        float worstWash = 0f, worstHead = 0f;
        for (int i = 1; i <= 3 && handle is not null; i++)
        {
            handle.ScrollTo(276.0 + 5.0 * i, ScrollMove.Immediate);   // the heading straddles the line at every tick
            host.RunFrame();
            paintTicks++;
            paintBytes += host.LastStats.Slices.BytesRecorded;
            paintContent += host.UiSliceTable.InvalidationCount(InvalidationReason.Content);
            worstWash = MathF.Max(worstWash, MathF.Abs(EffectiveClipTopOf(device, W, StickyClipProbe.HeroH + 200f) - StickyClipProbe.Inset));
            worstHead = MathF.Max(worstHead, MathF.Abs(EffectiveClipTopOf(device, 200f, 30f) - StickyClipProbe.Inset));
        }
        host.GpuKnockouts = GpuKnockouts.None;
        bool compositeCut = MathF.Abs(compositeWashTop - StickyClipProbe.Inset) < 0.51f && MathF.Abs(compositeHeadTop - StickyClipProbe.Inset) < 0.51f;
        Check("gate.slices.stickyclip-cuts-at-line the composed frame cuts the sticky-clipped wash and the clipped column's heading at the band line through the composite route AND through the paint route (StickyClipInPaint), which re-records the watched clip on every page scroll and never leaves it stale",
            compositeCut && paintTicks == 3 && paintBytes > 0 && paintContent > 0 && worstWash < 0.51f && worstHead < 0.51f,
            $"composite wash={compositeWashTop:0.##} heading={compositeHeadTop:0.##}; paint route bytes={paintBytes} contentInvalidations={paintContent} worst |top−line| wash={worstWash:0.##} heading={worstHead:0.##}");
    }

    /// <summary>A default-EdgeCues scroller inside a TRANSLUCENT card on an opaque page: the painted cue used to fill its
    /// edge bands with the page colour (the first opaque ancestor), a slab over the card.</summary>
    sealed class EdgeCueProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = W, Height = H, Fill = ColorF.FromRgba(26, 28, 34),
            Children =
            [
                new BoxEl
                {
                    Width = W, Height = H, Fill = ColorF.FromRgba(0x24, 0x2C, 0x3C, 0xA0),
                    Children =
                    [
                        new ScrollEl
                        {
                            Width = W, Height = 400f, SuppressScrollBar = true,
                            Content = new BoxEl { Direction = 1, Children = EdgeCueRows() },
                        },
                    ],
                },
            ],
        };

        static Element[] EdgeCueRows()
        {
            var rows = new Element[60];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl { Height = 40f, Fill = ColorF.FromRgba((byte)(0x30 + i % 0x60), 0x40, 0x68, 0xB8) };
            return rows;
        }
    }

    // ── gate.slices.edge-cue-is-feather ───────────────────────────────────────────────────────────────────────────
    // The default scroll-edge cue (ScrollEdgeCues.Auto → Fade) is the viewport's ANALYTIC feather: the content item
    // carries it, and nothing is painted over the content (no GradientRect in the viewport's stream after its marker).
    static void EdgeCueIsFeatherChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new EdgeCueProbe(), out _, out var device, out var app);
        using var _app = app;
        for (int i = 0; i < 4; i++) host.RunFrame();
        var s = host.Scene;
        var scrollers = new List<NodeHandle>();
        Collect(s, s.Root, scrollers);
        var vp = scrollers.Count > 0 ? scrollers[0] : NodeHandle.Null;
        host.TryGetScrollHandle(vp)?.ScrollTo(300.0, ScrollMove.Immediate);   // past the 24-DIP runway at both edges
        for (int i = 0; i < 20; i++) host.RunFrame();
        CompositeItem content = default;
        bool found = !vp.IsNull && TryItemOfNode(host, s.ScrollRef(vp).ContentNode, out content);
        float bandTop = found ? content.Feather.BandTop : 0f, bandBottom = found ? content.Feather.BandBottom : 0f;
        int gradients = 0;
        foreach (var r in device.LastGradients) if (r.Rect.W >= W - 1f) gradients++;   // a full-width band over the content
        Check("gate.slices.edge-cue-is-feather a default-EdgeCues scroller in a translucent card composites its edge cue as the analytic feather on its content item (both scrolled edges, the standard band) and paints no gradient band over the content",
            found && bandTop > 0.5f && bandBottom > 0.5f && gradients == 0,
            $"found={found} feather top={bandTop:0.##} bottom={bandBottom:0.##} fullWidthGradients={gradients}");
    }

    /// <summary>A default-EdgeCues scroller whose overlay scrollbar is pinned visible, mid-list: the thumb lies over the
    /// content inside the feather band.</summary>
    sealed class ChromeFeatherProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = W, Height = H, Fill = ColorF.FromRgba(26, 28, 34),
            Children =
            [
                new ScrollEl
                {
                    Width = W, Height = 400f, AlwaysShowScrollbar = true,
                    Content = new BoxEl { Direction = 1, Children = ChromeRows() },
                },
            ],
        };

        static Element[] ChromeRows()
        {
            var rows = new Element[60];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl { Height = 40f, Fill = ColorF.FromRgba((byte)(0x30 + i % 0x60), 0x40, 0x68) };
            return rows;
        }
    }

    // ── gate.slices.chrome-over-feather ───────────────────────────────────────────────────────────────────────────
    // A scroller's chrome (scrollbar thumb, rail, edge-cue chevrons) is drawn OVER its edge feather, never under it. Under
    // it, the visible thumb overlapped the content inside the band, the fade could not be distributed, and every scroll
    // frame re-rendered the whole viewport as a group surface. Over it: the fade distributes (no group), the content item
    // carries the feather, the thumb item none, and a scroll tick is composite-only.
    static void ChromeOverFeatherChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new ChromeFeatherProbe(), out _, out _, out var app);
        using var _app = app;
        for (int i = 0; i < 4; i++) host.RunFrame();
        var s = host.Scene;
        var scrollers = new List<NodeHandle>();
        Collect(s, s.Root, scrollers);
        var vp = scrollers.Count > 0 ? scrollers[0] : NodeHandle.Null;
        host.TryGetScrollHandle(vp)?.ScrollTo(30.0, ScrollMove.Immediate);   // past the runway: full band, the thumb at the top of its track, inside it
        if (!vp.IsNull) host.ScrollChrome.NotifyMoved((int)vp.Raw.Index);   // the user's scroll: a programmatic move alone no longer shows the bar (F238)
        for (int i = 0; i < 20; i++) host.RunFrame();
        int groups = CountKind(host, CompositeKind.Group);
        CompositeItem content = default;
        bool found = !vp.IsNull && TryItemOfNode(host, s.ScrollRef(vp).ContentNode, out content);
        float band = found ? content.Feather.BandTop : 0f;
        int thumbId = -1, thumbInherited = -1;
        var rows = host.UiSlices.LastRows;
        for (int i = 0; i < rows.Length; i++)
            if (!vp.IsNull && rows[i].NodeIndex == (int)vp.Raw.Index && (rows[i].Sub & 7) == (int)SliceRole.Thumb) { thumbId = rows[i].Id; break; }
        foreach (var it in host.UiSlices.LastItems)
            if (thumbId >= 0 && it.SliceId == thumbId) { thumbInherited = it.Inherited; break; }
        int composite = 0;
        long bytes = 0;
        for (int i = 1; i <= 3; i++)
        {
            host.TryGetScrollHandle(vp)?.ScrollTo(30.0 + 3.0 * i, ScrollMove.Immediate);
            if (!vp.IsNull) host.ScrollChrome.NotifyMoved((int)vp.Raw.Index);
            host.RunFrame();
            bytes += host.LastStats.Slices.BytesRecorded;
            if (host.LastStats.CompositeOnlyTurn) composite++;
            groups += CountKind(host, CompositeKind.Group);
        }
        Check("gate.slices.chrome-over-feather a fading scroller with its scrollbar showing and its thumb inside the top feather band distributes its fade (no group surface on any frame): the content item carries the feather, the thumb item none, and a scroll tick records 0 bytes",
            groups == 0 && found && band > 0.5f && thumbId >= 0 && thumbInherited == 0 && bytes == 0 && composite == 3,
            $"groups={groups} contentFeather={band:0.##} thumbItem={(thumbId >= 0)} thumbInherited={thumbInherited} bytes={bytes} compositeOnly={composite}/3");
    }

    /// <summary>The scene ROOT is a scroller with the default edge cue: its fade is a layer on a node that is itself a
    /// slice root.</summary>
    sealed class RootScrollerProbe : Component
    {
        public override Element Render()
        {
            var rows = new Element[80];
            for (int i = 0; i < rows.Length; i++) rows[i] = new BoxEl { Height = 40f, Fill = ColorF.FromRgba((byte)(40 + i % 80), 60, 90) };
            return new ScrollEl { Width = W, Height = H, SuppressScrollBar = true, Content = new BoxEl { Direction = 1, Children = rows } };
        }
    }

    // ── gate.slices.slice-root-layer ──────────────────────────────────────────────────────────────────────────────
    // A group layer on a node that is itself a slice root (here the scene root, a fading scroller) used to record INLINE —
    // and nothing is cut inside an inline layer, so its whole content folded into the root slice and every scroll
    // re-recorded it. It is cut as a nested Layer-role slice of the same node: the content stays a Scroll slice, nothing
    // folds, and a scroll tick records nothing.
    static void SliceRootLayerChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var host = Host(strings, fonts, new RootScrollerProbe(), out _, out var device, out var app);
        using var _app = app;
        for (int i = 0; i < 4; i++) host.RunFrame();
        var root = host.Scene.Root;
        host.TryGetScrollHandle(root)?.ScrollTo(300.0, ScrollMove.Immediate);
        for (int i = 0; i < 10; i++) host.RunFrame();
        Span<(int NodeIndex, uint Gen, SliceRole Role, SliceKind Kind)> order = stackalloc (int, uint, SliceRole, SliceKind)[16];
        int n = host.UiSlices.CopySliceOrder(order);
        bool layer = false, scroll = false;
        for (int i = 0; i < n && i < order.Length; i++)
        {
            if (order[i].NodeIndex == (int)root.Raw.Index && order[i].Role == SliceRole.Layer) layer = true;
            if (order[i].Kind == SliceKind.Scroll) scroll = true;
        }
        int folded = host.UiSlices.LastStats.Folded;
        long bytes = 0;
        int composite = 0;
        for (int i = 1; i <= 3; i++)
        {
            host.TryGetScrollHandle(root)?.ScrollTo(300.0 + 5.0 * i, ScrollMove.Immediate);
            host.RunFrame();
            bytes += host.LastStats.Slices.BytesRecorded;
            if (host.LastStats.CompositeOnlyTurn) composite++;
        }
        Check("gate.slices.slice-root-layer a fading scroller that is the scene ROOT cuts its fade as a nested Layer slice: its content stays a Scroll slice, nothing folds inline, and a scroll tick records 0 bytes (composite-only)",
            layer && scroll && folded == 0 && bytes == 0 && composite == 3,
            $"layerSlice={layer} scrollSlice={scroll} folded={folded} bytes={bytes} compositeOnly={composite}/3");
    }

    /// <summary>The top edge (window DIP) of the clip in effect at the first composed FillRoundRect of size
    /// <paramref name="w"/>×<paramref name="h"/> — the intersection of the open clip scopes; NaN when absent; −∞ when unclipped.</summary>
    static float EffectiveClipTopOf(HeadlessGpuDevice device, float w, float h)
    {
        ReadOnlySpan<byte> bytes = device.LastComposedStream.Bytes;
        var stack = new List<float>();
        int pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)System.Runtime.InteropServices.MemoryMarshal.Read<int>(bytes[pos..]);
            pos += sizeof(int);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + body > bytes.Length) break;
            switch (op)
            {
                case DrawOp.PushClip:
                    stack.Add(System.Runtime.InteropServices.MemoryMarshal.Read<ClipCmd>(bytes[pos..]).DeviceRect.Y);
                    break;
                case DrawOp.PushStencilClip:
                    stack.Add(System.Runtime.InteropServices.MemoryMarshal.Read<PushStencilClipCmd>(bytes[pos..]).DeviceRect.Y);
                    break;
                case DrawOp.PopClip:
                case DrawOp.PopStencilClip:
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    break;
                case DrawOp.FillRoundRect:
                {
                    var r = System.Runtime.InteropServices.MemoryMarshal.Read<FillRoundRectCmd>(bytes[pos..]);
                    if (MathF.Abs(r.Rect.W - w) < 0.5f && MathF.Abs(r.Rect.H - h) < 0.5f)
                    {
                        float top = float.NegativeInfinity;
                        foreach (float t in stack) top = MathF.Max(top, t);
                        return top;
                    }
                    break;
                }
            }
            pos += body;
        }
        return float.NaN;
    }
}
