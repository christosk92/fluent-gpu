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
using FluentGpu.Reconciler;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// The scroll-linked APIs of the scroll-GPU plan §F over the current posing path (the UI poser + the render poser's
/// compositor overlay): the sticky/sticky-clip ENGAGED edge as a signal, hero collapse (presented height, hit-testing
/// below the compact band), the top overscroll stretch composed with a parallax, <c>UseScroll</c>/<c>UseScrollProgress</c>
/// resolving the nearest scroller, and <c>MeasureAll</c> (every row measured, so offsets computed from the layout — a
/// follow target — are real). Every gate drives the real headless host.
/// </summary>
static class ScrollEffectsSuite
{
    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        EngagedEdgeChecks(strings, fonts);
        StickyClipPaintOrderChecks(strings, fonts);
        CollapseChecks(strings, fonts);
        StretchChecks(strings, fonts);
        StretchUnderCollapseChecks(strings, fonts);
        UseScrollChecks(strings, fonts);
        MeasureAllChecks(strings, fonts);
    }

    static NodeHandle FirstScroller(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.HasScroll(n)) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FirstScroller(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    static bool IsWithin(SceneStore s, NodeHandle n, NodeHandle ancestor)
    {
        for (var a = n; !a.IsNull; a = s.Parent(a)) if (a == ancestor) return true;
        return false;
    }

    // ── 1. the engaged edge ───────────────────────────────────────────────────────────────────────────────────────

    sealed class EngagedProbe : Component
    {
        public const float Top = 100f, HeaderH = 48f;
        public required Signal<bool> Pinned;
        public required Signal<bool> Clipped;
        public override Element Render() => new ScrollEl
        {
            Width = 400f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Height = Top },
                    new BoxEl { Height = HeaderH, Fill = ColorF.FromRgba(30, 34, 40) }.Sticky(0f, engaged: Pinned),
                    // The clip line sits at the header's bottom: it cuts into this node from offset Top on (Top + 48 − 148).
                    new BoxEl { Height = 4000f, Fill = ColorF.FromRgba(20, 22, 26) }.StickyClip(HeaderH, engaged: Clipped),
                ],
            },
        };
    }

    static void EngagedEdgeChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var detail = new System.Text.StringBuilder();
        bool allOk = true;
        foreach (float scale in new[] { 1f, 1.5f })
        {
            var pinned = new Signal<bool>(false);
            var clipped = new Signal<bool>(false);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scroll-engaged", new Size2(400, 300), scale)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
                new EngagedProbe { Pinned = pinned, Clipped = clipped });
            host.RunFrame(); host.RunFrame();
            var vp = FirstScroller(host.Scene, host.Scene.Root);
            var handle = host.TryGetScrollHandle(vp)!;
            // Warm: the first jump off the top realizes/arms the chrome once; the sweep below measures steady frames.
            handle.ScrollTo(90.0, ScrollMove.Immediate);
            for (int i = 0; i < 4; i++) host.RunFrame();

            // Sweep THROUGH the pin offset (100) in half-DIP steps, down and back up. At every frame the edge must equal
            // the predicate (offset > pin line) — flips exactly there, both ways — and flip exactly once each way.
            var path = new List<double>();
            for (double o = 90.0; o <= 110.0; o += 0.5) path.Add(o);
            for (double o = 110.0; o >= 90.0; o -= 0.5) path.Add(o);
            int pinRises = 0, pinFalls = 0, clipRises = 0, clipFalls = 0, mismatches = 0;
            bool lastPin = pinned.Peek(), lastClip = clipped.Peek();
            bool atLineFalse = true, pastLineTrue = true;
            long worstHot = 0;
            double worstAt = double.NaN;
            foreach (double o in path)
            {
                handle.ScrollTo(o, ScrollMove.Immediate);
                var f = host.RunFrame();
                host.Scene.TryGetScroll(vp, out var sc);
                bool expect = sc.Offset > EngagedProbe.Top;
                bool p = pinned.Peek(), c = clipped.Peek();
                if (p != expect || c != expect) mismatches++;
                if (o == 100.0 && (p || c)) atLineFalse = false;
                if (o == 100.5 && (!p || !c)) pastLineTrue = false;
                if (p && !lastPin) pinRises++; if (!p && lastPin) pinFalls++;
                if (c && !lastClip) clipRises++; if (!c && lastClip) clipFalls++;
                lastPin = p; lastClip = c;
                // A steady frame (no flip) writes nothing and allocates nothing in the hot phases.
                if (Math.Abs(o - 100.0) > 1.0 && f.HotPhaseAllocBytes > worstHot) { worstHot = f.HotPhaseAllocBytes; worstAt = o; }
            }
            bool ok = mismatches == 0 && pinRises == 1 && pinFalls == 1 && clipRises == 1 && clipFalls == 1
                      && atLineFalse && pastLineTrue && worstHot == 0;
            allOk &= ok;
            detail.Append($"@{scale:0.##}: mismatch={mismatches} pin↑{pinRises}↓{pinFalls} clip↑{clipRises}↓{clipFalls} at100={atLineFalse} at100.5={pastLineTrue} hotAlloc={worstHot}@{worstAt}; ");
        }
        Check("gate.scroll-effects.engaged-edge Sticky(engaged:) and StickyClip(engaged:) write their signal exactly at the pin offset in both directions (false at the line, true half a DIP past it), once per crossing (no flicker), and a steady frame allocates nothing",
            allOk, detail.ToString());
    }

    // ── 1b. paint order: a Sticky pin lifts its node above its siblings, a StickyClip never does ────────────────────

    /// <summary>Two ZStacks in one scroller. The first is Wavee's artist hero shape (Artist.Page's wash): a backdrop wash
    /// with <c>.StickyClip(56)</c> as child 0 BEHIND the hero (child 1). The second is a sticky header (child 0,
    /// <c>.Sticky(0)</c>) over a tall body (child 1). The wash's clip is engaged from offset 0 (inset 56 past its top), the
    /// header's pin from offset 300 (its top).</summary>
    sealed class PaintOrderProbe : Component
    {
        public static readonly ColorF Wash = ColorF.FromRgba(200, 40, 40), Hero = ColorF.FromRgba(40, 200, 40),
                                      Header = ColorF.FromRgba(40, 40, 200), Body = ColorF.FromRgba(200, 200, 40);
        public override Element Render() => new ScrollEl
        {
            Width = 400f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, Height = 300f,
                        Children =
                        [
                            new BoxEl { Height = 300f, Fill = Wash }.StickyClip(56f),
                            new BoxEl { Height = 240f, Fill = Hero },
                        ],
                    },
                    new BoxEl
                    {
                        ZStack = true, Height = 2000f,
                        Children =
                        [
                            new BoxEl { Height = 48f, Fill = Header }.Sticky(0f),
                            new BoxEl { Height = 2000f, Fill = Body },
                        ],
                    },
                ],
            },
        };
    }

    /// <summary>The paint-order contract of the two sticky kinds, read from the recorded draw stream. A pinned
    /// <c>Sticky</c> header paints AFTER the sibling it pins over (and in document order while released). A
    /// <c>StickyClip</c> never changes paint order: the defect marked every engaged sticky-kind node
    /// <c>StickyPinned</c>, so a wash clipped at 56 DIP — engaged from offset 0 — painted OVER the hero it backs, cut at
    /// its clip line (the artist page's horizontal ghost band).</summary>
    static void StickyClipPaintOrderChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-paint-order", new Size2(400, 300), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new PaintOrderProbe());
        host.RunFrame(); host.RunFrame();
        var vp = FirstScroller(host.Scene, host.Scene.Root);
        var handle = host.TryGetScrollHandle(vp)!;

        (int Wash, int Hero, int Header, int Body) Orders()
        {
            var dl = new DrawList();
            SceneRecorder.Record(host.Scene, dl);
            return (FindFillCommand(dl, PaintOrderProbe.Wash).Order, FindFillCommand(dl, PaintOrderProbe.Hero).Order,
                    FindFillCommand(dl, PaintOrderProbe.Header).Order, FindFillCommand(dl, PaintOrderProbe.Body).Order);
        }

        var top = Orders();   // offset 0: the wash's clip is engaged
        handle.ScrollTo(150.0, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) host.RunFrame();
        var released = Orders();   // offset 150: the header (top 300) is on screen and not pinned yet
        handle.ScrollTo(420.0, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) host.RunFrame();
        var pinned = Orders();   // offset 420: the header is pinned (the hero ZStack has scrolled off)
        handle.ScrollTo(20.0, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) host.RunFrame();
        var clipDeep = Orders();   // offset 20: the wash clip cuts deeper, still behind the hero

        bool washBehind = top.Wash >= 0 && top.Hero >= 0 && top.Wash < top.Hero
                          && clipDeep.Wash >= 0 && clipDeep.Hero >= 0 && clipDeep.Wash < clipDeep.Hero;
        bool headerReleasedInOrder = released.Header >= 0 && released.Body >= 0 && released.Header < released.Body;
        bool headerPinnedAbove = pinned.Header >= 0 && pinned.Body >= 0 && pinned.Header > pinned.Body;
        Check("gate.scroll-effects.stickyclip-paint-order an engaged StickyClip never changes sibling paint order (a clipped wash stays BEHIND the hero it backs at offsets 0 and 20), while a pinned Sticky header still paints after the sibling it pins over (and in document order while released)",
            washBehind && headerReleasedInOrder && headerPinnedAbove,
            $"@0 wash={top.Wash} hero={top.Hero}; @150 header={released.Header} body={released.Body}; @20 wash={clipDeep.Wash} hero={clipDeep.Hero}; @420 header={pinned.Header} body={pinned.Body}");
    }

    // ── 2. hero collapse ───────────────────────────────────────────────────────────────────────────────────────────

    sealed class CollapseProbe : Component
    {
        public const float HeroH = 300f, MinH = 56f, Over = 244f, RowH = 40f;
        public const int Rows = 60;
        public required bool Collapse;
        public required CollapseAnchor Anchor;
        public override Element Render()
        {
            var rows = new Element[Rows];
            for (int i = 0; i < Rows; i++) rows[i] = new BoxEl { Height = RowH, Fill = ColorF.FromRgba(40, 40, (byte)(i % 2 == 0 ? 40 : 60)) };
            // The hero is the LATER ZStack child, so without a presented height it sits above the rows in hit order.
            var hero = new BoxEl
            {
                // ClipToBounds: the clip (paint AND input) follows the presented height, as on the app's heroes.
                Height = HeroH, AlignSelf = FlexAlign.Start, Direction = 1, Fill = ColorF.FromRgba(90, 20, 20), ClipToBounds = true,
                Children =
                [
                    new BoxEl { Height = HeroH - 50f },
                    new BoxEl { Height = 50f, Fill = ColorF.FromRgba(200, 200, 200) },   // the hero's bottom strip
                ],
            }.Sticky(0f);
            if (Collapse) hero = hero.Collapse(Over, MinH, Anchor);
            return new ScrollEl
            {
                Width = 400f, Height = 500f,
                Content = new BoxEl
                {
                    ZStack = true, MinWidth = 0f,
                    Children = [new BoxEl { Direction = 1, Padding = new Edges4(0f, HeroH, 0f, 0f), Children = rows }, hero],
                },
            };
        }
    }

    static void CollapseChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        (AppHost Host, HeadlessWindow Window, HeadlessPlatformApp App, NodeHandle Vp, NodeHandle Rows, NodeHandle Hero) Mount(bool collapse, CollapseAnchor anchor)
        {
            var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scroll-collapse", new Size2(400, 500), 1f)); window.Show();
            var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new CollapseProbe { Collapse = collapse, Anchor = anchor });
            host.RunFrame(); host.RunFrame();
            var vp = FirstScroller(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc);
            var rowsCol = host.Scene.FirstChild(sc.ContentNode);
            return (host, window, app, vp, rowsCol, host.Scene.NextSibling(rowsCol));
        }

        // Presented height: monotone full → min across a downward sweep, exactly the full height at rest.
        {
            var (host, _, app, vp, _, hero) = Mount(collapse: true, CollapseAnchor.Leading);
            using var _h = host; using var _a = app;
            var handle = host.TryGetScrollHandle(vp)!;
            float atRest = host.Scene.Paint(hero).PresentedH;
            float prev = float.PositiveInfinity, last = 0f;
            bool monotone = true;
            for (double o = 0.0; o <= 320.0; o += 3.1)
            {
                handle.ScrollTo(o, ScrollMove.Immediate);
                host.RunFrame();
                float h = host.Scene.Paint(hero).PresentedH;
                if (!(h <= prev + 1e-4f) || h < CollapseProbe.MinH - 1e-3f || h > CollapseProbe.HeroH + 1e-3f) monotone = false;
                prev = h; last = h;
            }
            Check("gate.scroll-effects.collapse-monotone .Collapse(over, minH) writes the hero's presented height: the full laid-out height at rest, non-increasing across a downward sweep, the compact height once the collapse distance is scrolled",
                Near(atRest, CollapseProbe.HeroH, 0.01f) && monotone && Near(last, CollapseProbe.MinH, 0.01f),
                $"atRest={atRest:0.##} monotone={monotone} last={last:0.##}");
        }

        // Hit-testing: once collapsed, a point below the compact band reaches the rows; the band itself still hits the
        // hero. The same probe WITHOUT the collapse row keeps the pinned hero on top there (the gate discriminates).
        {
            var (host, window, app, vp, rows, hero) = Mount(collapse: true, CollapseAnchor.Leading);
            using var _h = host; using var _a = app;
            host.TryGetScrollHandle(vp)!.ScrollTo(CollapseProbe.Over, ScrollMove.Immediate);
            for (int i = 0; i < 3; i++) host.RunFrame();
            var inBand = host.Input.DiagHitTest(new Point2(200f, 30f));
            var below = host.Input.DiagHitTest(new Point2(200f, 120f));
            bool bandHitsHero = IsWithin(host.Scene, inBand, hero);
            bool belowHitsRows = IsWithin(host.Scene, below, rows);

            var (host2, _, app2, vp2, _, hero2) = Mount(collapse: false, CollapseAnchor.Leading);
            using var _h2 = host2; using var _a2 = app2;
            host2.TryGetScrollHandle(vp2)!.ScrollTo(CollapseProbe.Over, ScrollMove.Immediate);
            for (int i = 0; i < 3; i++) host2.RunFrame();
            bool controlHitsHero = IsWithin(host2.Scene, host2.Input.DiagHitTest(new Point2(200f, 120f)), hero2);

            Check("gate.scroll-effects.collapse-hit a collapsed sticky hero takes input only inside its presented (compact) band: a point below the band reaches the rows beneath (without the collapse row the pinned hero would have eaten it)",
                bandHitsHero && belowHitsRows && controlHitsHero,
                $"band→hero={bandHitsHero} below→rows={belowHitsRows} control(no collapse)→hero={controlHitsHero}");
        }

        // Trailing anchor: the children ride the presented bottom edge — the hero's bottom strip is what the compact band
        // shows (and hits) once collapsed.
        {
            var (host, _, app, vp, _, hero) = Mount(collapse: true, CollapseAnchor.Trailing);
            using var _h = host; using var _a = app;
            host.TryGetScrollHandle(vp)!.ScrollTo(CollapseProbe.Over, ScrollMove.Immediate);
            for (int i = 0; i < 3; i++) host.RunFrame();
            float shift = host.Scene.Paint(hero).ChildShiftY;
            var strip = host.Scene.NextSibling(host.Scene.FirstChild(hero));
            bool stripHit = IsWithin(host.Scene, host.Input.DiagHitTest(new Point2(200f, 30f)), strip);
            Check("gate.scroll-effects.collapse-trailing CollapseAnchor.Trailing shifts the children by presentedH − fullH, so the hero's bottom strip occupies (and takes the input of) the compact band",
                Near(shift, CollapseProbe.MinH - CollapseProbe.HeroH, 0.01f) && stripHit,
                $"childShiftY={shift:0.##} stripHit={stripHit}");
        }
    }

    // ── 3. overscroll stretch ─────────────────────────────────────────────────────────────────────────────────────

    sealed class StretchProbe : Component
    {
        public const float HeroH = 200f;
        public override Element Render() => new ScrollEl
        {
            Width = 400f, Height = 460f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    // Default (centre) transform origin on purpose: the stretch must pivot at the top-centre regardless.
                    new BoxEl { Height = HeroH, Fill = ColorF.FromRgba(80, 60, 40) }.StretchFromTop().ParallaxY(0.5f, HeroH),
                    new BoxEl { Height = 3000f, Fill = ColorF.FromRgba(20, 20, 20) },
                ],
            },
        };
    }

    static void StretchChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-stretch", new Size2(400, 460), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new StretchProbe());
        host.RunFrame(); host.RunFrame();
        var vp = FirstScroller(host.Scene, host.Scene.Root);
        host.Scene.TryGetScroll(vp, out var sc0);
        var content = sc0.ContentNode;
        var hero = host.Scene.FirstChild(content);
        var next = host.Scene.NextSibling(hero);

        float Drawn(NodeHandle n, float localY)
        {
            ref readonly RectF b = ref host.Scene.Bounds(n);
            ref readonly NodePaint p = ref host.Scene.Paint(n);
            float oy = b.H * p.OriginY;
            var m = p.LocalTransform;
            return host.Scene.Paint(content).LocalTransform.Dy + b.Y + (m.M22 * (localY - oy) + oy + m.Dy);
        }

        // Pull DOWN past the top with a held touch pan: the shown offset rubber-bands negative.
        uint t = 90_000;
        var ev = new InputEvent[1];
        ev[0] = Touch(InputKind.PointerDown, new Point2(150, 120), t, 71); host.Input.Dispatch(ev); host.RunFrame();
        for (int i = 1; i <= 12; i++)
        {
            t += 16;
            ev[0] = Touch(InputKind.PointerMove, new Point2(150, 120 + i * 15), t, 71); host.Input.Dispatch(ev); host.RunFrame();
        }
        host.Scene.TryGetScroll(vp, out var pulled);
        float scale = host.Scene.Paint(hero).LocalTransform.M22;
        float top = Drawn(hero, 0f);
        float bottom = Drawn(hero, StretchProbe.HeroH);
        float nextTop = host.Scene.Paint(content).LocalTransform.Dy + host.Scene.Bounds(next).Y;
        bool overpanned = pulled.Offset < -1.0;
        bool stretched = scale > 1.005f;
        bool topPinned = MathF.Abs(top) <= 0.01f;
        bool fillsGap = MathF.Abs(bottom - nextTop) <= 0.01f;
        ev[0] = Touch(InputKind.PointerUp, new Point2(150, 300), t + 16, 71); host.Input.Dispatch(ev);
        for (int i = 0; i < 200; i++) host.RunFrame();

        // Past the start: identity stretch, and the parallax on the SAME node still lands (the two compose).
        host.TryGetScrollHandle(vp)!.ScrollTo(100.0, ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) host.RunFrame();
        var rest = host.Scene.Paint(hero).LocalTransform;
        bool parallaxLands = Near(rest.M22, 1f, 1e-4f) && Near(rest.Dy, 50f, 0.01f);

        Check("gate.scroll-effects.stretch-from-top a rubber-banded pull past the start scales the hero about its top-centre (whatever its authored origin): its top stays on the viewport top and its bottom meets the next row; past the start the stretch is identity and the node's parallax still lands (the rows compose, not overwrite)",
            overpanned && stretched && topPinned && fillsGap && parallaxLands,
            $"offset={pulled.Offset:0.##} scale={scale:0.####} top={top:0.###} bottom={bottom:0.###} nextTop={nextTop:0.###} rest=(s{rest.M22:0.###}, dy{rest.Dy:0.##})");
    }

    // ── 3b. overscroll stretch under a leading collapse (the app's artist hero) ────────────────────────────────────

    /// <summary>Wavee's artist hero structure, exactly: a root <c>{ Direction 1, Height H, ZStack }.Sticky(0)
    /// .Collapse(over, minH, Leading)</c> with NO <c>ClipToBounds</c>, wrapping a <c>ClipToBounds</c> photo (origin at
    /// its top) with <c>.StretchFromTop()</c> whose art is a solid fill. The hero is the LATER ZStack child over a padded
    /// row column (as in <see cref="CollapseProbe"/>), so without the collapse cut it would sit above the rows in hit
    /// order too.</summary>
    sealed class StretchUnderCollapseProbe : Component
    {
        public const float HeroH = 300f, MinH = 56f, Over = 244f, RowH = 40f, W = 400f, ViewH = 600f;
        public const int Rows = 60;
        public static readonly ColorF Art = ColorF.FromRgba(170, 90, 30);
        public override Element Render()
        {
            var rows = new Element[Rows];
            for (int i = 0; i < Rows; i++) rows[i] = new BoxEl { Height = RowH, Fill = ColorF.FromRgba(40, 40, (byte)(i % 2 == 0 ? 40 : 60)) };
            var photo = new BoxEl
            {
                Width = W, Height = HeroH, ClipToBounds = true, TransformOriginX = 0.5f, TransformOriginY = 0f,
                Children = [new BoxEl { Width = W, Height = HeroH, Fill = Art }],
            }.StretchFromTop();
            var hero = new BoxEl
            {
                Direction = 1, Height = HeroH, ZStack = true, AlignSelf = FlexAlign.Start,
                Children = [photo],
            }.Sticky(0f).Collapse(Over, MinH, CollapseAnchor.Leading);
            return new ScrollEl
            {
                Width = W, Height = ViewH,
                Content = new BoxEl
                {
                    ZStack = true, MinWidth = 0f,
                    Children = [new BoxEl { Direction = 1, Padding = new Edges4(0f, HeroH, 0f, 0f), Children = rows }, hero],
                },
            };
        }
    }

    /// <summary>The first <see cref="DrawOp.FillRoundRect"/> of exactly <paramref name="fill"/> in <paramref name="dl"/>:
    /// its device rect and the scissor in effect at it (the innermost pushed clip — every push is pre-intersected with
    /// the enclosing one by the recorder; <see cref="RectF.Infinite"/> when none is active).</summary>
    static bool FindFillAndScissor(DrawList dl, ColorF fill, out RectF device, out RectF scissor)
    {
        ReadOnlySpan<byte> bytes = dl.Bytes;
        var stack = new List<RectF>();
        int pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)System.Runtime.InteropServices.MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            switch (op)
            {
                case DrawOp.FillRoundRect:
                {
                    var cmd = System.Runtime.InteropServices.MemoryMarshal.Read<FillRoundRectCmd>(
                        bytes.Slice(pos, System.Runtime.CompilerServices.Unsafe.SizeOf<FillRoundRectCmd>()));
                    if (cmd.Fill.Equals(fill))
                    {
                        device = cmd.Transform.TransformBounds(cmd.Rect);
                        scissor = stack.Count > 0 ? stack[^1] : RectF.Infinite;
                        return true;
                    }
                    break;
                }
                case DrawOp.PushClip:
                    stack.Add(System.Runtime.InteropServices.MemoryMarshal.Read<ClipCmd>(
                        bytes.Slice(pos, System.Runtime.CompilerServices.Unsafe.SizeOf<ClipCmd>())).DeviceRect);
                    break;
                case DrawOp.PushStencilClip:
                    stack.Add(System.Runtime.InteropServices.MemoryMarshal.Read<PushStencilClipCmd>(
                        bytes.Slice(pos, System.Runtime.CompilerServices.Unsafe.SizeOf<PushStencilClipCmd>())).DeviceRect);
                    break;
                case DrawOp.PopClip:
                case DrawOp.PopStencilClip:
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    break;
            }
            pos += DrawPayloadSize(op);
        }
        device = default;
        scissor = default;
        return false;
    }

    /// <summary>The two recipes compose: a leading <c>.Collapse</c> cuts its children at the presented edge by
    /// itself, so the hero root carries no <c>ClipToBounds</c> — and its <c>.StretchFromTop()</c> photo fills a top
    /// overscroll instead of a dark band opening above it (the root's box clip used to cut the pull away). Read from the
    /// recorded draw stream: at a −40 DIP pull the art's device rect AND the scissor it draws under reach 40 DIP above
    /// the root's top; mid-collapse the art is still cut — scissor bottom on the presented edge — and a point below that
    /// edge reaches the rows beneath (input follows the cut) while the band itself still hits the hero.</summary>
    static void StretchUnderCollapseChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        const float Pull = 40f, Eps = 0.5f;
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-stretch-collapse",
            new Size2(StretchUnderCollapseProbe.W, StretchUnderCollapseProbe.ViewH), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new StretchUnderCollapseProbe());
        host.RunFrame(); host.RunFrame();
        var vp = FirstScroller(host.Scene, host.Scene.Root);
        host.Scene.TryGetScroll(vp, out var sc0);
        var content = sc0.ContentNode;
        var rowsCol = host.Scene.FirstChild(content);
        var hero = host.Scene.NextSibling(rowsCol);
        bool rootUnclipped = !hero.IsNull && (host.Scene.Flags(hero) & NodeFlags.ClipsToBounds) == 0;

        float RootTop() => host.Scene.Paint(content).LocalTransform.Dy + host.Scene.Bounds(hero).Y
                           + host.Scene.Paint(hero).LocalTransform.Dy;
        (bool Found, RectF Device, RectF Scissor) ArtNow()
        {
            var dl = new DrawList();
            SceneRecorder.Record(host.Scene, dl);
            bool found = FindFillAndScissor(dl, StretchUnderCollapseProbe.Art, out var device, out var scissor);
            return (found, device, scissor);
        }

        // Pull DOWN past the top with a held touch pan until the rubber-banded offset reaches −40.
        uint t = 120_000;
        var ev = new InputEvent[1];
        float y = 30f;
        ev[0] = Touch(InputKind.PointerDown, new Point2(200, y), t, 72); host.Input.Dispatch(ev); host.RunFrame();
        host.Scene.TryGetScroll(vp, out var pulled);
        for (int i = 0; i < 45 && pulled.Offset > -Pull; i++)
        {
            t += 16; y += 12f;
            ev[0] = Touch(InputKind.PointerMove, new Point2(200, y), t, 72); host.Input.Dispatch(ev); host.RunFrame();
            host.Scene.TryGetScroll(vp, out pulled);
        }
        bool reached = pulled.Offset <= -Pull + 0.01;
        float rootTopPulled = RootTop();
        var over = ArtNow();
        bool artCovers = over.Found && over.Device.Y <= rootTopPulled - Pull + Eps;
        bool scissorOpen = over.Found && over.Scissor.Y <= rootTopPulled - Pull + Eps;
        ev[0] = Touch(InputKind.PointerUp, new Point2(200, y), t + 16, 72); host.Input.Dispatch(ev);
        for (int i = 0; i < 200; i++) host.RunFrame();

        // Mid-collapse: the art is cut at the presented edge (paint), and input below it reaches the rows.
        host.TryGetScrollHandle(vp)!.ScrollTo(StretchUnderCollapseProbe.Over * 0.5, ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) host.RunFrame();
        float presented = host.Scene.Paint(hero).PresentedH;
        float rootTopMid = RootTop();
        float edge = rootTopMid + presented;
        bool midCollapse = presented > StretchUnderCollapseProbe.MinH + 1f && presented < StretchUnderCollapseProbe.HeroH - 1f;
        var mid = ArtNow();
        bool cutAtEdge = mid.Found && MathF.Abs(mid.Scissor.Bottom - edge) <= Eps && mid.Device.Bottom > edge + 1f;
        bool belowHitsRows = IsWithin(host.Scene, host.Input.DiagHitTest(new Point2(200f, edge + 30f)), rowsCol);
        bool bandHitsHero = IsWithin(host.Scene, host.Input.DiagHitTest(new Point2(200f, edge - 20f)), hero);

        Check("gate.scroll.stretch-under-collapse the app's hero (root { Height, ZStack }.Sticky(0).Collapse(Leading), NO ClipToBounds, around a ClipToBounds .StretchFromTop() photo): at a −40 DIP top overpan the photo's art is drawn — device rect AND its scissor — up to 40 DIP above the root's top (no dark band); mid-collapse the art is still cut at the presented bottom edge (scissor bottom there) and a point below that edge hits the rows beneath while the band hits the hero",
            rootUnclipped && reached && artCovers && scissorOpen && midCollapse && cutAtEdge && belowHitsRows && bandHitsHero,
            $"rootUnclipped={rootUnclipped} offset={pulled.Offset:0.##} rootTop={rootTopPulled:0.##} art=({over.Found} y{over.Device.Y:0.##}) scissorY={over.Scissor.Y:0.##}; " +
            $"mid presented={presented:0.##} edge={edge:0.##} scissorBottom={mid.Scissor.Bottom:0.##} artBottom={mid.Device.Bottom:0.##} below→rows={belowHitsRows} band→hero={bandHitsHero}");
    }

    // ── 4. UseScroll / UseScrollProgress ───────────────────────────────────────────────────────────────────────────

    sealed class ObservationHolder
    {
        public ScrollObservation Inner, Outside, InList;
        public IReadSignal<float>? Progress;
    }

    sealed class InnerObserver(ObservationHolder holder) : Component
    {
        public override Element Render()
        {
            holder.Inner = UseScroll();
            holder.Progress = UseScrollProgress(0.0, 200.0);
            return new BoxEl { Height = 10f };
        }
    }

    sealed class OutsideObserver(ObservationHolder holder) : Component
    {
        public override Element Render()
        {
            holder.Outside = UseScroll();
            return new BoxEl { Height = 10f };
        }
    }

    sealed class ListItemObserver(ObservationHolder holder) : Component
    {
        public override Element Render()
        {
            holder.InList = UseScroll();
            return new BoxEl { Height = 40f };
        }
    }

    sealed class UseScrollProbe : Component
    {
        public required ObservationHolder Holder;
        public required ScrollHandle PageHandle;
        public required ScrollHandle ListHandle;
        public override Element Render()
        {
            var holder = Holder;
            return new BoxEl
            {
                Direction = 0,
                Children =
                [
                    Embed.Comp(() => new OutsideObserver(holder)),
                    new ScrollEl
                    {
                        Width = 200f, Height = 300f, Handle = PageHandle,
                        Content = new BoxEl { Direction = 1, Children = [Embed.Comp(() => new InnerObserver(holder)), new BoxEl { Height = 3000f }] },
                    },
                    Virtual.List(50, 40f, i => i == 0 ? Embed.Comp(() => new ListItemObserver(holder)) : new BoxEl { Height = 40f })
                        with { Width = 200f, Height = 300f, Grow = 0f, Handle = ListHandle },
                ],
            };
        }
    }

    static void UseScrollChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var holder = new ObservationHolder();
        var page = new ScrollHandle();
        var list = new ScrollHandle();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-usescroll", new Size2(600, 300), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
            new UseScrollProbe { Holder = holder, PageHandle = page, ListHandle = list });
        host.RunFrame(); host.RunFrame();

        bool innerIsPage = ReferenceEquals(holder.Inner.Offset, page.Offset) && ReferenceEquals(holder.Inner.Motion, page.Motion)
                           && ReferenceEquals(holder.Inner.AtEnd, page.AtEnd) && ReferenceEquals(holder.Inner.Viewport, page.ViewportSignal);
        bool listIsList = ReferenceEquals(holder.InList.Offset, list.Offset);
        bool outsideIsNone = ReferenceEquals(holder.Outside.Offset, ScrollObservation.None.Offset);
        float progress0 = holder.Progress?.Value ?? -1f;

        page.ScrollTo(100.0, ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) host.RunFrame();
        double offset = holder.Inner.Offset.Value;
        bool atStart = holder.Inner.AtStart.Value;
        float progress1 = holder.Progress?.Value ?? -1f;

        Check("gate.scroll-effects.use-scroll UseScroll() resolves the NEAREST scroller (a ScrollEl's content, a virtual list's item) through ScrollCtx and returns that viewport's own handle signals; outside every scroller it is the constant None; UseScrollProgress(0, 200) tracks the offset (0 at rest, 0.5 at 100)",
            innerIsPage && listIsList && outsideIsNone && Near(progress0, 0f, 1e-4f) && Math.Abs(offset - 100.0) < 1e-6 && !atStart && Near(progress1, 0.5f, 1e-4f),
            $"inner=page:{innerIsPage} inList=list:{listIsList} outside=None:{outsideIsNone} p0={progress0:0.###} offset={offset:0.##} atStart={atStart} p1={progress1:0.###}");
    }

    // ── 5. MeasureAll ────────────────────────────────────────────────────────────────────────────────────────────

    sealed class MeasureAllProbe : Component
    {
        public const int N = 80;
        public const float Estimate = 48f;
        public static float RowH(int i) => 20f + (i % 3) * 25f;
        public required bool All;
        public MeasuredStackVirtualLayout? Layout;
        public override Element Render()
        {
            var layout = UseMemo(static () => new MeasuredStackVirtualLayout(Estimate), DepKey.Empty);
            Layout = layout;
            return Virtual.Measured(N, layout, renderItem: i => new BoxEl { Height = RowH(i) }, keyOf: i => "ma" + i)
                with { Width = 300f, Height = 200f, Grow = 0f, MeasureAll = All };
        }
    }

    static void MeasureAllChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        const int Target = 60;
        float trueOffset = 0f;
        for (int i = 0; i < Target; i++) trueOffset += MeasureAllProbe.RowH(i);

        (float OffsetOfTarget, int Realized, float TargetRowTop, long SteadyAlloc) Run(bool all)
        {
            var probe = new MeasureAllProbe { All = all };
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scroll-measure-all", new Size2(300, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            for (int i = 0; i < 4; i++) host.RunFrame();
            var vp = FirstScroller(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc);
            // The FOLLOW TARGET, computed from the layout before the row was ever on screen (the lyrics follow's shape).
            float target = probe.Layout!.OffsetOf(Target, 300f);
            host.TryGetScrollHandle(vp)!.ScrollTo(target, ScrollMove.Immediate);
            for (int i = 0; i < 3; i++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var after);
            // Where row `Target` actually landed in the viewport (its drawn top relative to the viewport top).
            float rowTop = float.NaN;
            int ord = 0;
            for (var c = host.Scene.FirstChild(after.ContentNode); !c.IsNull; c = host.Scene.NextSibling(c), ord++)
                if (after.FirstRealized + ord == Target)
                    rowTop = host.Scene.Bounds(c).Y + host.Scene.Paint(after.ContentNode).LocalTransform.Dy;
            long worst = 0;
            for (int i = 0; i < 4; i++)
            {
                host.TryGetScrollHandle(vp)!.ScrollBy(3.0, ScrollMove.Immediate);
                var f = host.RunFrame();
                if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes;
            }
            return (target, sc.LastRealized - sc.FirstRealized, rowTop, worst);
        }

        var measured = Run(all: true);
        var windowed = Run(all: false);
        Check("gate.scroll-effects.measure-all MeasureAll realizes and measures every row, so a follow target computed from the layout for a row never on screen is its REAL offset and the row lands exactly on it (the windowed list's target is still an estimate); scrolling it stays allocation-free",
            measured.Realized == MeasureAllProbe.N && Near(measured.OffsetOfTarget, trueOffset, 0.01f) && Near(measured.TargetRowTop, 0f, 0.01f)
            && windowed.Realized < MeasureAllProbe.N && !Near(windowed.OffsetOfTarget, trueOffset, 1f) && measured.SteadyAlloc == 0,
            $"all: realized={measured.Realized} target={measured.OffsetOfTarget:0.##} true={trueOffset:0.##} rowTop={measured.TargetRowTop:0.###} alloc={measured.SteadyAlloc}; windowed: realized={windowed.Realized} target={windowed.OffsetOfTarget:0.##}");
    }
}
