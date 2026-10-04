using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Diag.Analysis;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// The scroll rework's motion gates (scroll-rework-design.md, Wave 1): closed-form plans evaluated at present time, a
/// velocity-sized realize window that covers the shown viewport on EVERY frame, same-frame extent corrections that hold
/// the anchor, double-precision offsets deep into 100k-row lists, zero managed allocation on a flat fling, a wheel notch
/// that authors its plan synchronously and shows on the very next present, sticky headers on the device-pixel grid, and
/// the nested-scroller contact latch. Every gate drives the real headless host (AppHost + HeadlessWindow) — nothing
/// here re-implements the engine's math.
/// </summary>
static class ScrollMotionSuite
{
    const float RowH = 40f;

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        NoBlankChecks(strings, fonts);
        ExtentStableChecks(strings, fonts);
        AnchorHoldsChecks(strings, fonts);
        PrecisionDeepChecks(strings, fonts);
        FlatZeroAllocChecks(strings, fonts);
        NotchToPresentChecks(strings, fonts);
        HiResBurstChecks(strings, fonts);
        StickyGridChecks(strings, fonts);
        NestedLatchChecks(strings, fonts);
        TouchpadDmStreamChecks(strings, fonts);
        TouchpadStampRaceChecks(strings, fonts);
        ShiftCoverageAtomicChecks(strings, fonts);
        CtrlWheelZoomSignChecks(strings, fonts);
        PrefixCoverageChecks(strings, fonts);
        ProbeExportChainChecks(strings, fonts);
        ReseedAnchoredChecks(strings, fonts);
    }

    // ── probes ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A flat fixed-extent bound list: <paramref name="n"/> rows × 40 DIP in a 300×400 viewport (the viewport is
    /// the scene root). Fill-only rows: recycling a slot re-evaluates one Prop, never a template.</summary>
    sealed class FlatListProbe(int n) : Component
    {
        public int TemplateCalls;
        public override Element Render()
            => Virtual.ListBound(n, RowH, idx =>
               {
                   TemplateCalls++;
                   return new BoxEl
                   {
                       Height = RowH,
                       Fill = Prop.Of(() => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 30 : 50))),
                   };
               })
               with { Width = 300, Height = 400 };
    }

    /// <summary>A MEASURED list whose every row measures larger than its estimate: rows entering the realize window from
    /// above correct the extent table (+24 each) while the viewport moves — the anchor / extent gates' scenario.</summary>
    sealed class MeasuredProbe : Component
    {
        public const int N = 800;
        public const float Estimate = 40f;
        public const float Real = 64f;
        public MeasuredStackVirtualLayout? Layout;
        public override Element Render()
        {
            var layout = UseMemo(static () => new MeasuredStackVirtualLayout(Estimate), DepKey.Empty);
            Layout = layout;
            return Virtual.Measured(N, layout,
                       renderItem: _ => new BoxEl { Height = Real, Fill = ColorF.FromRgba(30, 30, 30) },
                       keyOf: i => "sm" + i)
                   with { Width = 300, Height = 300 };
        }
    }

    /// <summary>A sticky header (48 DIP, pinned at the viewport top) over a tall body whose clip line rides the header.</summary>
    sealed class StickyProbe : Component
    {
        public const float HeaderH = 48f;
        public static readonly ColorF HeaderFill = ColorF.FromRgba(30, 34, 40);
        public override Element Render() => new ScrollEl
        {
            Width = 400f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Height = HeaderH, Fill = HeaderFill }.Sticky(0f),
                    new BoxEl { Height = 4000f, Fill = ColorF.FromRgba(20, 22, 26) }.StickyClip(HeaderH),
                ],
            },
        };
    }

    /// <summary>An inner 200-DIP vertical scroller (content 400 ⇒ max 200) inside an outer 300-DIP page (content 1300
    /// ⇒ max 1000). The inner sits at y 100..300 of the outer's content.</summary>
    sealed class NestedProbe : Component
    {
        public const float InnerMax = 200f;
        public override Element Render() => new ScrollEl
        {
            Width = 400f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Height = 100f, Fill = ColorF.FromRgba(40, 40, 40) },
                    new ScrollEl
                    {
                        Width = 400f, Height = 200f,
                        Content = new BoxEl
                        {
                            Direction = 1, MinWidth = 0f,
                            Children = Rows(10),
                        },
                    },
                    new BoxEl { Height = 1000f, Fill = ColorF.FromRgba(24, 24, 24) },
                ],
            },
        };

        static Element[] Rows(int n)
        {
            var rows = new Element[n];
            for (int i = 0; i < n; i++) rows[i] = new BoxEl { Height = RowH, Fill = ColorF.FromRgba(60, 60, (byte)(60 + i * 10)) };
            return rows;
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

    static void FindScrollables(SceneStore s, NodeHandle n, List<NodeHandle> into)
    {
        if (n.IsNull) return;
        if ((s.Flags(n) & NodeFlags.Scrollable) != 0 && s.HasScroll(n)) into.Add(n);
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) FindScrollables(s, c, into);
    }

    /// <summary>The offset the recorder DREW this frame: the content child's applied translation, read back through the
    /// same window-origin convention the layout arranges rows in.</summary>
    static double ShownOffset(SceneStore s, in ScrollState sc)
        => sc.WindowOrigin - s.Paint(sc.ContentNode).LocalTransform.Dy;

    static void Settle(AppHost host, NodeHandle vp, int maxFrames = 400)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc);
            if (!sc.Motion.IsMoving && !host.HasActiveWork) return;
        }
    }

    // ── gate.scroll.no-blank ──────────────────────────────────────────────────────────────────────────────────────

    static void NoBlankChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-no-blank", new Size2(640, 480), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new FlatListProbe(100_000));
        host.RunFrame();
        var vp = host.Scene.Root;
        Settle(host, vp);

        int uncovered = 0, frames = 0; double worstGap = 0, maxStep = 0, prevShown = 0; bool havePrev = false;
        void Observe()
        {
            host.Scene.TryGetScroll(vp, out var sc);
            double shown = ShownOffset(host.Scene, in sc);
            // A rubber-band overpan past either edge shows the background beyond the content, never a blank ROW: the
            // covered band is measured against the content-clamped window.
            double visTop = Math.Clamp(shown, 0.0, Math.Max(0.0, sc.ContentH - sc.ViewportH));
            double visBot = Math.Min(visTop + sc.ViewportH, sc.ContentH);
            double realizedTop = sc.FirstRealized * RowH, realizedBot = sc.LastRealized * RowH;
            bool covered = realizedTop <= visTop + 0.01 && realizedBot >= visBot - 0.01;
            if (!covered) { uncovered++; worstGap = Math.Max(worstGap, Math.Max(visBot - realizedBot, realizedTop - visTop)); }
            if (havePrev) maxStep = Math.Max(maxStep, Math.Abs(shown - prevShown));
            prevShown = shown; havePrev = true; frames++;
        }

        // A fast touchpad fling: 10 frames of 120 DIP contact, then the lift's fling coasts for as long as it needs.
        var prod = new HeadlessScrollProducer(window, host, new Point2(150, 200));
        prod.ContactBegin(0f); prod.Step(16); Observe();
        for (int i = 0; i < 10; i++) { prod.ContactUpdate(120f); prod.Step(16); Observe(); }
        prod.ContactEnd();
        for (int f = 0; f < 600; f++)
        {
            prod.Step(16); Observe();
            host.Scene.TryGetScroll(vp, out var s);
            if (!s.Motion.IsMoving) break;
        }
        host.Scene.TryGetScroll(vp, out var afterFling);
        double flingDistance = afterFling.Offset;

        // A wheel storm: 40 notches in one burst, then 20 more one per frame, followed by the glide.
        window.SendWheelNotch(new Point2(150, 200), 40f);
        for (int i = 0; i < 20; i++) { window.SendWheelNotch(new Point2(150, 200), 1f); host.RunFrame(); Observe(); }
        for (int f = 0; f < 200; f++)
        {
            host.RunFrame(); Observe();
            host.Scene.TryGetScroll(vp, out var s);
            if (!s.Motion.IsMoving) break;
        }
        // And back up at speed: the receding side must stay covered too.
        prod.ContactBegin(0f); prod.Step(16); Observe();
        for (int i = 0; i < 10; i++) { prod.ContactUpdate(-150f); prod.Step(16); Observe(); }
        prod.ContactEnd();
        for (int f = 0; f < 600; f++)
        {
            prod.Step(16); Observe();
            host.Scene.TryGetScroll(vp, out var s);
            if (!s.Motion.IsMoving) break;
        }

        Check("gate.scroll.no-blank the drawn viewport of a 100k-row flat list is fully covered by realized rows on EVERY frame of a fast touchpad fling, a 60-notch wheel storm and a reverse fling — the velocity-sized realize window never trails the shown offset",
            uncovered == 0 && frames > 40 && flingDistance > 2000,
            $"frames={frames} uncovered={uncovered} worstGap={worstGap:0.#} flingDistance={flingDistance:0} maxFrameStep={maxStep:0.#}");
    }

    // ── gate.scroll.extent-stable ─────────────────────────────────────────────────────────────────────────────────

    static void ExtentStableChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-extent", new Size2(360, 460), 1f)); window.Show();
        var probe = new MeasuredProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        var vp = host.Scene.Root;
        var handle = host.TryGetScrollHandle(vp)!;
        handle.ScrollTo(16_000f, ScrollMove.Immediate);
        for (int i = 0; i < 12; i++) host.RunFrame();

        var layout = probe.Layout!;
        bool extentAgrees = true, boxesAgree = true, extentMonotone = true;
        double prevExtent = 0; int corrections = 0; float worstBox = 0f;
        for (int step = 0; step < 60; step++)
        {
            handle.ScrollBy(-37f, ScrollMove.Immediate);   // upward, into estimate-priced rows that realize at 64
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc);
            float cross = sc.ViewportW;
            double layoutTotal = layout.OffsetOf(MeasuredProbe.N - 1, cross) + layout.ItemRect(MeasuredProbe.N - 1, cross).H;
            // (1) the extent the handle publishes IS the layout's total, on the frame the correction landed.
            if (Math.Abs(handle.Extent - layoutTotal) > 0.01 || Math.Abs(sc.ContentH - layoutTotal) > 0.01) extentAgrees = false;
            if (step > 0 && layoutTotal < prevExtent - 0.01) extentMonotone = false;
            if (step > 0 && layoutTotal > prevExtent + 0.01) corrections++;
            prevExtent = layoutTotal;
            // (2) every REALIZED row's arranged box agrees with the table: box.Y + WindowOrigin == OffsetOf(index).
            for (var c = host.Scene.FirstChild(sc.ContentNode); !c.IsNull; c = host.Scene.NextSibling(c))
            {
                var b = host.Scene.Bounds(c);
                int index = layout.IndexAt((float)(b.Y + sc.WindowOrigin) + 0.5f, cross);
                float expectedY = (float)(layout.OffsetOf(index, cross) - sc.WindowOrigin);
                float err = MathF.Abs(b.Y - expectedY);
                if (err > worstBox) worstBox = err;
                if (err > 0.01f) boxesAgree = false;
            }
        }
        Check("gate.scroll.extent-stable a measured list corrected mid-motion publishes the layout's total as the handle's extent on the SAME frame, the extent only grows as estimate-priced rows realize, and every realized row's box agrees with the extent table",
            extentAgrees && boxesAgree && extentMonotone && corrections >= 5,
            $"extentAgrees={extentAgrees} boxesAgree={boxesAgree} (worst {worstBox:0.###}) monotone={extentMonotone} corrections={corrections} extent={handle.Extent:0}");
    }

    // ── gate.scroll.anchor-holds ──────────────────────────────────────────────────────────────────────────────────

    static void AnchorHoldsChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-anchor", new Size2(360, 460), 1f)); window.Show();
        var probe = new MeasuredProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        var vp = host.Scene.Root;
        host.TryGetScrollHandle(vp)!.ScrollTo(16_000f, ScrollMove.Immediate);
        host.ScrollChrome.NotifyMoved((int)vp.Raw.Index);   // the user's scroll: a programmatic move alone no longer arms the bar, and the live chrome is what keeps these frames running
        for (int i = 0; i < 12; i++) host.RunFrame();

        // Track one visible row by INDEX (rows recycle, so node identity is not stable): its on-screen Y must move
        // exactly with the finger frame to frame while rows ABOVE it realize at +24 each (a dropped or fought re-pin
        // shows up as a 24-DIP step in one frame).
        var layout = probe.Layout!;
        host.Scene.TryGetScroll(vp, out var sc0);
        float contentDy0 = host.Scene.Paint(sc0.ContentNode).LocalTransform.Dy;
        int trackedIndex = -1; float prevScreenY = 0f;
        for (var c = host.Scene.FirstChild(sc0.ContentNode); !c.IsNull; c = host.Scene.NextSibling(c))
        {
            float screenY = host.Scene.Bounds(c).Y + contentDy0;
            if (screenY >= 0f && screenY + MeasuredProbe.Real <= 120f)   // the first FULLY visible row near the top
            {
                trackedIndex = layout.IndexAt((float)(host.Scene.Bounds(c).Y + sc0.WindowOrigin) + 0.5f, sc0.ViewportW);
                prevScreenY = screenY;
                break;
            }
        }
        float ScreenYOf(int index, in ScrollState sc)
        {
            float dy = host.Scene.Paint(sc.ContentNode).LocalTransform.Dy;
            for (var c = host.Scene.FirstChild(sc.ContentNode); !c.IsNull; c = host.Scene.NextSibling(c))
            {
                int i = layout.IndexAt((float)(host.Scene.Bounds(c).Y + sc.WindowOrigin) + 0.5f, sc.ViewportW);
                if (i == index) return host.Scene.Bounds(c).Y + dy;
            }
            return float.NaN;
        }

        var prod = new HeadlessScrollProducer(window, host, new Point2(150, 150));
        prod.ContactBegin(0f); prod.Step(16);
        host.Scene.TryGetScroll(vp, out var scB);
        prevScreenY = ScreenYOf(trackedIndex, in scB);
        const float perFrame = 14.4f;   // 0.9 DIP/ms upward
        float worstDrift = 0f, leadScreenY = 0f; int frames = 0; bool leadEstablished = false; bool trackedAlive = trackedIndex >= 0 && !float.IsNaN(prevScreenY);
        double extentBefore = host.TryGetScrollHandle(vp)!.Extent;
        for (int kf = 0; kf < 24 && trackedAlive; kf++)
        {
            prod.ContactUpdate(-perFrame);
            prod.Step(16);
            host.Scene.TryGetScroll(vp, out var sc);
            float screenY = ScreenYOf(trackedIndex, in sc);
            if (float.IsNaN(screenY)) { trackedAlive = false; break; }
            if (sc.Motion.Kind != MotionKind.Drag) { prevScreenY = screenY; continue; }
            // The contact's first sample establishes the Follow ring's present-time prediction (one refresh of lead, a
            // constant from then on) — that frame's step is the lead, not tracking; measure from the next one.
            if (!leadEstablished) { leadEstablished = true; leadScreenY = screenY; prevScreenY = screenY; continue; }
            // The finger moved the content DOWN by perFrame every frame; a correction above must not add to that. Measured
            // CUMULATIVELY against the finger (a slow creep is caught as surely as a one-frame step) and to one device
            // pixel: the content translate is snapped to the device grid, so each posed Y carries ≤ half a pixel of
            // rounding against the unsnapped finger line (a dropped or fought re-pin is a whole 24-DIP row step).
            frames++;
            float drift = MathF.Abs((screenY - leadScreenY) - frames * perFrame);
            worstDrift = MathF.Max(worstDrift, drift);
            prevScreenY = screenY;
        }
        prod.ContactEnd();
        double extentAfter = host.TryGetScrollHandle(vp)!.Extent;
        bool correctionsFired = extentAfter - extentBefore >= (MeasuredProbe.Real - MeasuredProbe.Estimate) * 3 - 1;
        Check("gate.scroll.anchor-holds during an upward touchpad drag that realizes estimate-priced rows ABOVE the viewport, a visible row's screen position tracks the finger 1:1 (cumulative, ≤ one device pixel) — every extent correction shifts the plan in the same frame instead of jumping the content",
            frames >= 16 && trackedAlive && correctionsFired && worstDrift <= 1.0f,
            $"frames={frames} tracked={trackedIndex} alive={trackedAlive} worstDrift={worstDrift:0.###} extent {extentBefore:0}→{extentAfter:0}");
    }

    // ── gate.scroll.precision-deep ────────────────────────────────────────────────────────────────────────────────

    static void PrecisionDeepChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-deep", new Size2(640, 480), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new FlatListProbe(100_000));
        host.RunFrame();
        var vp = host.Scene.Root;
        var handle = host.TryGetScrollHandle(vp)!;
        const double Deep = 3_999_000.0;   // 100k × 40 = 4,000,000 DIP of content; a float has ~0.25 DIP of resolution here
        handle.ScrollTo(Deep, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) host.RunFrame();

        bool offsetsExact = true, transformSnapped = true, rowsExact = true; double worstOffsetErr = 0; float worstRowErr = 0f;
        double expectedOffset = Deep;
        for (int step = 0; step < 50; step++)
        {
            handle.ScrollBy(0.37, ScrollMove.Immediate);
            expectedOffset += 0.37;
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc);
            double err = Math.Abs(sc.Offset - expectedOffset);
            worstOffsetErr = Math.Max(worstOffsetErr, err);
            if (err > 1e-6) offsetsExact = false;
            float expectedDy = ScrollContentPose.Translate(sc.WindowOrigin, sc.Offset, 1f);
            if (host.Scene.Paint(sc.ContentNode).LocalTransform.Dy != expectedDy) transformSnapped = false;
            for (var c = host.Scene.FirstChild(sc.ContentNode); !c.IsNull; c = host.Scene.NextSibling(c))
            {
                var b = host.Scene.Bounds(c);
                int index = (int)Math.Round((b.Y + sc.WindowOrigin) / RowH);
                float rowErr = MathF.Abs(b.Y - (float)(index * (double)RowH - sc.WindowOrigin));
                if (rowErr > worstRowErr) worstRowErr = rowErr;
                if (rowErr > 0.01f) rowsExact = false;
            }
        }
        Check("gate.scroll.precision-deep 4,000,000 DIP into a 100k-row list, 0.37-DIP immediate steps land exactly (double offsets), the content transform is the one device-pixel-snapped translation, and rows arranged relative to the window origin carry no float error",
            offsetsExact && transformSnapped && rowsExact,
            $"offsetsExact={offsetsExact} (worst {worstOffsetErr:E2}) transformSnapped={transformSnapped} rowsExact={rowsExact} (worst {worstRowErr:0.####})");

        // gate.scroll.bring-node-deep: the node-level BringIntoView resolves a realized row of a virtualized list to its
        // CONTENT offset (layout position + the window's arrange origin) — 4M DIP deep, aligning a row near the bottom of
        // the viewport to the top lands the offset exactly on that row's start.
        host.Scene.TryGetScroll(vp, out var scB);
        NodeHandle target = NodeHandle.Null; int targetIndex = -1;
        for (var c = host.Scene.FirstChild(scB.ContentNode); !c.IsNull; c = host.Scene.NextSibling(c))
        {
            var b = host.Scene.Bounds(c);
            double top = b.Y + scB.WindowOrigin;
            if (top >= scB.Offset + 200.0 && top < scB.Offset + 300.0) { target = c; targetIndex = (int)Math.Round(top / RowH); break; }
        }
        bool resolved = !target.IsNull && SceneScrollExtensions.BringIntoView(host.Scene, target, 0f, ScrollMove.Immediate);
        host.RunFrame();
        host.Scene.TryGetScroll(vp, out var scA);
        double want = targetIndex * (double)RowH;
        Check("gate.scroll.bring-node-deep the node-level BringIntoView resolves a realized row 4M DIP deep in a virtualized list through the window's arrange origin and lands the offset exactly on it",
            resolved && Math.Abs(scA.Offset - want) < 1e-6,
            $"resolved={resolved} index={targetIndex} offset={scA.Offset:0.###} want={want:0.###}");
    }

    // ── gate.scroll.100k-flat-zero-alloc ──────────────────────────────────────────────────────────────────────────

    static void FlatZeroAllocChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-zero-alloc", new Size2(640, 480), 1f)); window.Show();
        var probe = new FlatListProbe(100_000);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        var vp = host.Scene.Root;
        host.TryGetScrollHandle(vp)!.ScrollTo(2_000_000f, ScrollMove.Immediate);
        for (int i = 0; i < 40; i++) host.RunFrame();
        var prod = new HeadlessScrollProducer(window, host, new Point2(150, 200));

        (long WorstHot, long WorstFrame, int Templates, int Frames) Fling(float dy, bool measure)
        {
            long worstHot = 0, worstFrame = 0; int t0 = probe.TemplateCalls, frames = 0;
            void Acc(FrameStats f, long frameBytes)
            {
                frames++;
                if (!measure) return;
                if (f.HotPhaseAllocBytes > worstHot) worstHot = f.HotPhaseAllocBytes;
                if (frameBytes > worstFrame) worstFrame = frameBytes;
            }
            prod.ContactBegin(0f); Acc(prod.Step(16), 0);
            for (int i = 0; i < 10; i++) { prod.ContactUpdate(dy); long b = GC.GetAllocatedBytesForCurrentThread(); var f = prod.Step(16); Acc(f, GC.GetAllocatedBytesForCurrentThread() - b); }
            prod.ContactEnd();
            for (int f = 0; f < 400; f++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                var fs = prod.Step(16);
                Acc(fs, GC.GetAllocatedBytesForCurrentThread() - b);
                host.Scene.TryGetScroll(vp, out var s);
                if (!s.Motion.IsMoving) break;
            }
            return (worstHot, worstFrame, probe.TemplateCalls - t0, frames);
        }
        Fling(90f, measure: false);    // warm: the slot pool reaches its high-water mark, every path JITs
        Fling(-90f, measure: false);
        var fwd = Fling(90f, measure: true);
        var rev = Fling(-90f, measure: true);
        Check("gate.scroll.100k-flat-zero-alloc a warm forward+reverse touchpad fling over a 100k-row flat list allocates 0 managed bytes in the frame's hot phases on every frame and runs zero row templates (slots recycle)",
            fwd.WorstHot == 0 && rev.WorstHot == 0 && fwd.Templates == 0 && rev.Templates == 0 && fwd.Frames > 12,
            $"hotWorst fwd={fwd.WorstHot}B rev={rev.WorstHot}B; frameWorst fwd={fwd.WorstFrame}B rev={rev.WorstFrame}B; templates fwd={fwd.Templates} rev={rev.Templates}; frames fwd={fwd.Frames} rev={rev.Frames}");
    }

    // ── gate.scroll.notch-to-present ──────────────────────────────────────────────────────────────────────────────

    static void NotchToPresentChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-notch", new Size2(640, 480), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new FlatListProbe(100_000));
        host.RunFrame();
        var vp = host.Scene.Root;
        var handle = host.TryGetScrollHandle(vp)!;
        Settle(host, vp, 8);
        host.Scene.TryGetScroll(vp, out var rest);
        bool atRest = rest.Offset == 0.0 && !rest.Motion.IsMoving;

        // The notch is delivered through the window's URGENT sink: its plan exists before any frame runs.
        window.SendWheelNotch(new Point2(150, 200), 1f);
        bool authoredSynchronously = handle.Plan.Kind == MotionKind.Wheel;
        double notchDip = ScrollTunables.Current.WheelNotchDip;
        bool destIsOneNotch = Math.Abs(handle.Plan.Dest - notchDip) < 0.01;

        host.RunFrame();
        host.Scene.TryGetScroll(vp, out var first);
        bool movedOnFirstPresent = first.Offset > 0.0 && first.Motion.Kind == MotionKind.Wheel;
        float drawnDy = host.Scene.Paint(first.ContentNode).LocalTransform.Dy;
        bool drawnMoved = drawnDy < 0f;

        int frames = 1; bool monotone = true; double prev = first.Offset;
        for (; frames < 60; frames++)
        {
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var s);
            if (s.Offset < prev - 1e-6) monotone = false;
            prev = s.Offset;
            if (!s.Motion.IsMoving) break;
        }
        host.Scene.TryGetScroll(vp, out var fin);
        bool landedExactly = Math.Abs(fin.Offset - notchDip) < 0.01 && !fin.Motion.IsMoving;
        double glideS = ScrollTunables.Current.WheelDurationS;
        int expectedFrames = (int)Math.Ceiling(glideS / 0.016) + 2;
        Check("gate.scroll.notch-to-present a wheel notch authors its plan synchronously in the message (urgent sink), the offset and the drawn content have moved on the very NEXT present, the glide is monotone and lands exactly one WheelNotchDip within the feel's wheel duration",
            atRest && authoredSynchronously && destIsOneNotch && movedOnFirstPresent && drawnMoved && monotone && landedExactly && frames <= expectedFrames,
            $"atRest={atRest} authored={authoredSynchronously} dest={handle.Plan.Dest:0.##}/{notchDip:0.##} firstOff={first.Offset:0.##} drawnDy={drawnDy:0.##} monotone={monotone} final={fin.Offset:0.##} frames={frames}/{expectedFrames}");
    }

    // ── gate.scroll.hires-burst ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A hi-res (free-spin) wheel roll through the real host: 12 packets of −40 raw (a third of a notch, toward
    /// the content end) every 24 ms (72 ms a whole notch — the feel's full-step band, above <c>AccelUnityGapS</c>, so
    /// the spin curve leaves each notch at 1×) — one of them a COINCIDENT pair delivered in the same instant — built by the PAL's
    /// own conversion (<see cref="WheelClassifier.HiResNotch"/>) and delivered through the window's urgent sink. The
    /// packets are fractional notches on the accumulating wheel cubic: the content travels exactly the 480 raw units
    /// turned (4 notches × WheelNotchDip), every posed frame-to-frame step stays within 2× the burst's steady step (no
    /// jump at the coincident pair, no catch-up), the motion never reverses, and it stops where the wheel stopped (no
    /// silence-detected lift, no coast).</summary>
    static void HiResBurstChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-hires", new Size2(640, 480), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new FlatListProbe(100_000),
                                     frameTime: new FixedFrameTimeSource(8f));
        host.RunFrame();
        var vp = host.Scene.Root;
        Settle(host, vp, 8);
        host.Scene.TryGetScroll(vp, out var rest);
        double start = rest.Offset;
        var ptr = new Point2(150, 200);

        const int Raw = -40;          // a third of a notch, wheel toward the user = toward the content end
        var offsets = new List<double> { start };
        int sent = 0;
        bool anyAuthoredAsContact = false;
        for (int frame = 0; frame < 120; frame++)
        {
            int packets = sent >= 12 ? 0 : frame == 15 ? 2 : frame % 3 == 0 ? 1 : 0;   // one packet per 24 ms; frame 15 carries the coincident pair
            for (int k = 0; k < packets; k++, sent++)
            {
                window.SendScroll(WheelClassifier.HiResNotch(Raw, horizontal: false, qpc: 0, ptr, pointerId: 0, KeyModifiers.None));
                if (host.TryGetScrollHandle(vp)!.Plan.Kind == MotionKind.Drag) anyAuthoredAsContact = true;
            }
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc);
            offsets.Add(sc.Offset);
            if (sent >= 12 && !sc.Motion.IsMoving && frame > 20) break;
        }

        var moving = new List<double>();
        double maxStep = 0.0, minStep = double.PositiveInfinity;
        for (int i = 1; i < offsets.Count; i++)
        {
            double d = offsets[i] - offsets[i - 1];
            minStep = Math.Min(minStep, d);
            if (Math.Abs(d) <= ScrollMetricsEpsilon) continue;
            moving.Add(Math.Abs(d));
            maxStep = Math.Max(maxStep, Math.Abs(d));
        }
        moving.Sort();
        double steady = moving.Count > 0 ? moving[moving.Count / 2] : 0.0;
        double expected = start + 4.0 * ScrollTunables.Current.WheelNotchDip;
        double final = offsets[^1];
        host.Scene.TryGetScroll(vp, out var fin);
        bool noJump = moving.Count >= 10 && maxStep <= 2.0 * steady;
        bool neverBack = minStep >= -1e-9;
        // The PAL carries a packet as float notch units (−40/120 is not exact in binary): 1e-3 DIP over the burst.
        bool exact = Math.Abs(final - expected) < 1e-3 && !fin.Motion.IsMoving;
        Check("gate.scroll.hires-burst a hi-res wheel roll (12 × −40 raw at 24 ms, one coincident pair) rides the wheel cubic as fractional notches: it travels exactly 4 notches and stops there (no lift, no coast), every posed step stays within 2× the steady step and the motion never reverses",
            sent == 12 && !anyAuthoredAsContact && noJump && neverBack && exact,
            $"sent={sent} contact={anyAuthoredAsContact} final={final:0.######}/{expected:0.##} moving={fin.Motion.IsMoving} maxStep={maxStep:0.###} steady={steady:0.###} minStep={minStep:0.###} frames={offsets.Count - 1}");
    }

    const double ScrollMetricsEpsilon = 0.05;   // a posed frame "moved" above this (DIP) — the scroll lab's noise floor

    // ── gate.scroll.sticky-grid ───────────────────────────────────────────────────────────────────────────────────

    static void StickyGridChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        const int Steps = 200;
        const float StepDip = 0.37f;   // never a whole device pixel at any tested scale
        var detail = new System.Text.StringBuilder();
        bool allOk = true;
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f })
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scroll-motion-sticky", new Size2(400, 300), scale)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new StickyProbe());
            host.RunFrame(); host.RunFrame();
            var scrollers = new List<NodeHandle>();
            FindScrollables(host.Scene, host.Scene.Root, scrollers);
            var vp = scrollers[0];
            var handle = host.TryGetScrollHandle(vp)!;
            host.Scene.TryGetScroll(vp, out var sc0);
            var header = host.Scene.FirstChild(sc0.ContentNode);

            bool everPinned = false; float baseline = 0f, worstDrift = 0f; int pinnedFrames = 0; bool onGrid = true;
            for (int i = 0; i < Steps; i++)
            {
                handle.ScrollBy(StepDip, ScrollMove.Immediate);
                host.RunFrame(); host.RunFrame();
                host.Scene.TryGetScroll(vp, out var sc);
                if ((host.Scene.Flags(header) & NodeFlags.StickyPinned) == 0) continue;
                float contentDy = host.Scene.Paint(sc.ContentNode).LocalTransform.Dy;
                float screenY = contentDy + host.Scene.Paint(header).LocalTransform.Dy + host.Scene.Bounds(header).Y;
                if (!everPinned) { everPinned = true; baseline = screenY; }
                worstDrift = MathF.Max(worstDrift, MathF.Abs(screenY - baseline));
                // The pinned edge sits on the device grid: screenY × scale is a whole device pixel.
                float px = screenY * scale;
                if (MathF.Abs(px - MathF.Round(px)) > 1e-3f) onGrid = false;
                pinnedFrames++;
            }
            bool ok = everPinned && pinnedFrames >= Steps / 2 && worstDrift <= 1e-3f && onGrid && MathF.Abs(baseline) <= 0.5f / scale + 1e-3f;
            allOk &= ok;
            detail.Append($"[{scale:0.##}x pinned={pinnedFrames} drift={worstDrift:0.####} base={baseline:0.###} grid={onGrid}] ");
        }
        Check("gate.scroll.sticky-grid at 100/125/150/175% a sticky header pinned during 200 fractional (0.37 DIP) immediate steps never drifts against the content (one snapped translation for content + pin) and its pinned edge sits on the device-pixel grid",
            allOk, detail.ToString().TrimEnd());
    }

    // ── gate.scroll.nested-latch ──────────────────────────────────────────────────────────────────────────────────

    static void NestedLatchChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-nested", new Size2(400, 300), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new NestedProbe());
        host.RunFrame(); host.RunFrame();
        var scrollers = new List<NodeHandle>();
        FindScrollables(host.Scene, host.Scene.Root, scrollers);
        bool twoScrollers = scrollers.Count == 2;
        var outer = scrollers[0];
        var inner = scrollers.Count > 1 ? scrollers[1] : outer;
        var innerRect = host.Scene.AbsoluteRect(inner);
        var overInner = new Point2(innerRect.X + innerRect.W * 0.5f, innerRect.Y + innerRect.H * 0.5f);

        // (1) A contact gesture that starts over the inner scroller (which can move) latches it for the WHOLE gesture:
        //     600 DIP of finger travel takes the inner to its end and stops there — the outer never moves mid-gesture.
        var prod = new HeadlessScrollProducer(window, host, overInner);
        prod.ContactBegin(0f); prod.Step(16);
        for (int i = 0; i < 30; i++) { prod.ContactUpdate(20f); prod.Step(16); }
        host.Scene.TryGetScroll(inner, out var innerMid);
        host.Scene.TryGetScroll(outer, out var outerMid);
        bool innerAtEnd = Math.Abs(innerMid.Offset - NestedProbe.InnerMax) <= 0.5 || innerMid.Offset > NestedProbe.InnerMax;   // held (a rubber-band overpan reads past max)
        bool outerHeldDuringLatch = outerMid.Offset == 0.0;
        prod.ContactEnd();
        for (int i = 0; i < 90; i++) { prod.Step(16); host.Scene.TryGetScroll(inner, out var s); if (!s.Motion.IsMoving) break; }
        host.Scene.TryGetScroll(inner, out var innerSettled);
        host.Scene.TryGetScroll(outer, out var outerSettled);
        bool innerSettledAtEnd = Math.Abs(innerSettled.Offset - NestedProbe.InnerMax) <= 0.5 && outerSettled.Offset == 0.0;

        // (2) A NEW contact over the inner, which is now pinned at its end in the finger's direction, chains to the outer
        //     from its first sample.
        prod.ContactBegin(0f); prod.Step(16);
        for (int i = 0; i < 10; i++) { prod.ContactUpdate(20f); prod.Step(16); }
        host.Scene.TryGetScroll(outer, out var outerChained);
        host.Scene.TryGetScroll(inner, out var innerChained);
        bool chainedToOuter = outerChained.Offset > 100.0 && Math.Abs(innerChained.Offset - NestedProbe.InnerMax) <= 0.5;
        prod.ContactEnd();
        for (int i = 0; i < 120; i++) { prod.Step(16); host.Scene.TryGetScroll(outer, out var s); if (!s.Motion.IsMoving) break; }

        // (3) The wheel obeys the same rule at notch time: over the pinned inner a notch chains to the outer at once.
        //     Bring the outer back to the top first so the inner (still pinned at its end) sits under the pointer.
        host.TryGetScrollHandle(outer)!.ScrollTo(0.0, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) host.RunFrame();
        host.Scene.TryGetScroll(outer, out var outerBeforeWheel);
        var innerRectNow = host.Scene.AbsoluteRect(inner);
        window.SendWheelNotch(new Point2(innerRectNow.X + 100f, innerRectNow.Y + innerRectNow.H * 0.5f), 1f);
        for (int i = 0; i < 40; i++) { host.RunFrame(); host.Scene.TryGetScroll(outer, out var s); if (!s.Motion.IsMoving && i > 2) break; }
        host.Scene.TryGetScroll(outer, out var outerAfterWheel);
        host.Scene.TryGetScroll(inner, out var innerAfterWheel);
        bool wheelChained = Math.Abs(outerAfterWheel.Offset - outerBeforeWheel.Offset - ScrollTunables.Current.WheelNotchDip) < 0.5
            && Math.Abs(innerAfterWheel.Offset - NestedProbe.InnerMax) <= 0.5;

        Check("gate.scroll.nested-latch a contact gesture latches the inner scroller for its whole life (the outer never moves mid-gesture even past the inner's end); a new gesture that begins on the pinned inner chains to the outer from its first sample; a wheel notch over the pinned inner chains at notch time",
            twoScrollers && innerAtEnd && outerHeldDuringLatch && innerSettledAtEnd && chainedToOuter && wheelChained,
            $"scrollers={scrollers.Count} innerMid={innerMid.Offset:0.##} outerMid={outerMid.Offset:0.##} innerSettled={innerSettled.Offset:0.##} outerChained={outerChained.Offset:0.##} wheel {outerBeforeWheel.Offset:0.##}→{outerAfterWheel.Offset:0.##} inner={innerAfterWheel.Offset:0.##}");
    }

    // ── gate.touchpad.dm-stream-monotone ─────────────────────────────────────────────────────────────────────────

    /// <summary>Records the content translate the poser writes, per content node (the render-thread model's sink).</summary>
    sealed class ContentPoseSink : IScrollPoseSink
    {
        public readonly Dictionary<int, float> Trans = new();
        public void PoseViewport(int vpNode, double shown) { }
        public void PoseContent(int node, bool horizontal, float trans, bool changed) => Trans[node] = trans;
        public void PoseEffect(int node, EffectChannel channel, float value, bool changed) { }
        public void PoseTransform(int node, in EffectTransform transform, bool changed) { }
    }

    /// <summary>A DirectManipulation-shaped touchpad stream through the REAL host: the headless frame-aligned producer
    /// (the DM stand-in) stamps each frame's delta by the production rule (<see cref="ContactStamp.ForFrame"/> — the next
    /// tick's present) and flags it composition-timed; the
    /// finger runs at 1500 DIP/s, then decelerates to rest along a quarter sine over 100 ms, rests 70 ms and lifts. Every
    /// 5th frame the producer raises NO content update although the finger moved (DM had no fresh input for that Update —
    /// the motion arrives with the next frame's delta). Poses are read where the user sees them: the UI frame step at each
    /// present AND a render-thread poser model half a frame later (a compositor tick before the next frame's sample).
    /// While the finger advances the shown position never steps back (the defect: a zero-delta sample / a velocity drop
    /// moved the pose by ΔV·lead against the finger — back-steps of 2–8 DIP, then overshoot), and the lift after the
    /// rest does not fling (the finger had stopped: a time rule, no zero-delta sample).</summary>
    static void TouchpadDmStreamChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        const float FrameMs = 1000f / 60f;
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-dm-stream", new Size2(360, 460), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new FlatListProbe(4000),
                                     frameTime: new FixedFrameTimeSource(FrameMs));
        host.RunFrame();
        var vp = host.Scene.Root;
        Settle(host, vp, 8);
        var id = host.TryGetScrollHandle(vp)!.Vp;
        var at = new Point2(150, 200);
        var render = new ScrollPoser();
        var sink = new ContentPoseSink();
        var cov = new ScrollCoverageTable();

        const double V0 = 1500.0, TMove = 0.30, TDecel = 0.10, TRest = 0.07;
        static double Finger(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t < TMove) return V0 * t;
            double u = Math.Min((t - TMove) / TDecel, 1.0);
            return V0 * TMove + V0 * (2.0 * TDecel / Math.PI) * Math.Sin(Math.PI * u / 2.0);
        }

        double dt = FrameMs * 1e-3, pending = 0.0, prevPose = double.NaN, worst = 0.0;
        int packetless = 0, samples = 0, frames = 0;
        // Travel is measured from the rest offset BEFORE the gesture, against the finger's whole travel: a frame shows
        // the previous tick's sample (ContactStamp.ForFrame), so the first frame of the drag still shows the rest offset.
        host.Scene.TryGetScroll(vp, out var restBefore);
        double startOffset = restBefore.Offset;
        int moveFrames = (int)Math.Ceiling((TMove + TDecel + TRest) / dt);
        for (int k = 1; k <= moveFrames; k++, frames++)
        {
            double fPrev = Finger((k - 1) * dt), fNow = Finger(k * dt);
            pending += fNow - fPrev;
            bool skip = k % 5 == 3 && fNow > fPrev;
            if (skip) packetless++;
            else if (pending != 0.0) { window.QueueScrollDelta(0f, (float)pending, PointerKind.Touchpad, at); pending = 0.0; samples++; }
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc);
            double presentSec = FluentGpu.Hooks.FrameClock.PresentQpc / (double)System.Diagnostics.Stopwatch.Frequency;
            host.Scene.CaptureScrollCoverage!(cov);
            render.Adopt(cov);
            render.Tick(host.Plans, presentSec + dt * 0.5, 1f, sink);
            render.TryGetFeedback(id, out var fb);
            bool advancing = fNow > fPrev;
            foreach (double pose in new[] { sc.Offset, fb.Shown })
            {
                if (!double.IsNaN(prevPose) && advancing) worst = Math.Max(worst, prevPose - pose);
                prevPose = pose;
            }
        }
        host.Scene.TryGetScroll(vp, out var beforeLift);
        double travelled = beforeLift.Offset - startOffset;
        window.QueueScrollLift();
        host.RunFrame();
        host.Scene.TryGetScroll(vp, out var atLift);
        for (int i = 0; i < 30; i++) host.RunFrame();
        host.Scene.TryGetScroll(vp, out var after);
        bool monotone = worst <= 0.5;
        bool followed = Math.Abs(travelled - (Finger(TMove + TDecel) - Finger(0.0))) < 1.0;   // 1:1 with the finger (DM deltas are content deltas)
        bool noFling = Math.Abs(after.Offset - beforeLift.Offset) < 0.5 && after.Motion.Kind != MotionKind.Fling;
        Check("gate.touchpad.dm-stream-monotone a DirectManipulation-shaped touchpad stream (present-stamped, composition-timed, packetless frames, a decelerating finger) through the real host never steps the shown position back while the finger advances — neither at the UI frame's present nor at a render tick between frames — tracks the finger 1:1, and a lift after the fingers rested does not fling",
            monotone && followed && noFling && packetless >= 5 && samples >= 15,
            $"worstBackStep={worst:0.###} travelled={travelled:0.##}/{Finger(TMove + TDecel) - Finger(0.0):0.##} packetless={packetless} samples={samples} frames={frames} lift {beforeLift.Offset:0.##}->{atLift.Offset:0.##}->{after.Offset:0.##} kind={after.Motion.Kind}");
    }

    // ── gate.touchpad.stamp-race ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>One drag of <see cref="TouchpadStampRaceChecks"/>: the render model's pose at every tick, the offset the
    /// drag started from, the UI's offset once it rested, how many ticks read the plan BEFORE their frame's write, and how
    /// many poses the coverage clamp moved.</summary>
    sealed record StampRaceRun(double[] Poses, double RestOffset, double FinalUiOffset, int Befores, int Clamps);

    /// <summary>The touchpad sample race (scroll-jitter plan A.4; the 2026-09-29 RCA) through the REAL host. On a real
    /// window the UI thread writes frame k's DirectManipulation sample into the plan table within a fraction of a
    /// millisecond of the render thread reading it for the SAME compositor tick, so either can land first. A
    /// constant-speed drag (1500 DIP/s at 60 Hz — the RCA's fast drag) runs through the headless frame-aligned producer
    /// (the DM stand-in, stamping by the production rule <see cref="ContactStamp.ForFrame"/>), and a render-poser model
    /// ticks each frame at that tick's present either BEFORE or AFTER <c>host.RunFrame()</c> writes the frame's sample:
    /// once with every tick after its write, once with a seeded mix. The pose sequences must be identical (the shown
    /// position is a function of time, never of thread order), advance exactly one sample per tick (displacement
    /// irregularity Green), never step back, and travel 1:1 with the finger. The defect (a sample stamped for the tick that
    /// reads it) showed sample k on a tick whose read lost the race and sample k−1 on one that won — +2/0 sample steps.</summary>
    static void TouchpadStampRaceChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        const int MoveFrames = 30, RestFrames = 6;
        const float StepDip = 25f;
        var allAfter = StampRaceDrag(strings, fonts, MoveFrames, RestFrames, StepDip, seed: -1);
        var mixed = StampRaceDrag(strings, fonts, MoveFrames, RestFrames, StepDip, seed: 20260929);

        double maxDiff = allAfter.Poses.Length == mixed.Poses.Length ? 0.0 : double.PositiveInfinity;
        for (int i = 0; i < Math.Min(allAfter.Poses.Length, mixed.Poses.Length); i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(allAfter.Poses[i] - mixed.Poses[i]));
        double refreshS = 1.0 / 60.0;
        double irrAfter = StampRaceIrregularity(allAfter.Poses, refreshS), irrMixed = StampRaceIrregularity(mixed.Poses, refreshS);
        double backAfter = StampRaceWorstBackStep(allAfter.Poses), backMixed = StampRaceWorstBackStep(mixed.Poses);
        double travel = MoveFrames * (double)StepDip;
        double travelAfter = allAfter.Poses[^1] - allAfter.RestOffset, travelMixed = mixed.Poses[^1] - mixed.RestOffset;
        double travelUi = allAfter.FinalUiOffset - allAfter.RestOffset;

        bool identical = maxDiff <= 1e-6;
        bool reallyMixed = mixed.Befores > 0 && mixed.Befores < mixed.Poses.Length;
        bool regular = irrAfter < 0.15 && irrMixed < 0.15;
        bool monotone = backAfter <= 0.5 && backMixed <= 0.5;
        bool followed = Math.Abs(travelAfter - travel) <= 1.0 && Math.Abs(travelMixed - travel) <= 1.0 && Math.Abs(travelUi - travel) <= 1.0;
        Check("gate.touchpad.stamp-race a constant-speed DirectManipulation-shaped touchpad drag through the real host poses the SAME position at every render tick whether that tick read the plan before or after the UI wrote the tick's sample (each sample is stamped with the NEXT tick's present — ContactStamp.ForFrame): the all-after and a seeded mixed ordering give identical pose sequences that advance exactly one sample per tick (displacement irregularity Green), never step back and travel 1:1 with the finger",
            identical && reallyMixed && regular && monotone && followed,
            $"maxDiff={maxDiff:0.######} befores={mixed.Befores}/{mixed.Poses.Length} irregularity after={irrAfter:0.###} mixed={irrMixed:0.###} backStep after={backAfter:0.###} mixed={backMixed:0.###} travel after={travelAfter:0.##} mixed={travelMixed:0.##} ui={travelUi:0.##}/{travel:0.##} clamps={allAfter.Clamps}/{mixed.Clamps}");
    }

    /// <summary>One drag for <see cref="TouchpadStampRaceChecks"/>: <paramref name="moveFrames"/> frames of
    /// <paramref name="stepDip"/> each, then <paramref name="restFrames"/> still frames (the gesture stays open). A negative
    /// <paramref name="seed"/> reads every tick AFTER its frame; otherwise a seeded coin decides per tick.</summary>
    static StampRaceRun StampRaceDrag(StringTable strings, HeadlessFontSystem fonts, int moveFrames, int restFrames, float stepDip, int seed)
    {
        const float FrameMs = 1000f / 60f;
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-stamp-race", new Size2(360, 460), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new FlatListProbe(4000),
                                     frameTime: new FixedFrameTimeSource(FrameMs));
        host.RunFrame();
        var vp = host.Scene.Root;
        Settle(host, vp, 8);
        var id = host.TryGetScrollHandle(vp)!.Vp;
        var at = new Point2(150, 200);
        var render = new ScrollPoser();
        var sink = new ContentPoseSink();
        var cov = new ScrollCoverageTable();
        // DirectManipulation raises its Begin on RUNNING, before the first content update: a pre-roll frame delivers the
        // Begin with no motion (the dispatcher keeps the contact unrouted until the first movement). It also guarantees
        // the frame before the first sample was PRODUCED (an idle settle frame does not advance the headless clock), so
        // tick 0's present sits exactly on the Begin's stamp and every tick k on the stamp of sample k−1.
        window.QueueScrollDelta(0f, 0f, PointerKind.Touchpad, at);
        host.RunFrame();
        host.Scene.CaptureScrollCoverage!(cov);
        render.Adopt(cov);
        host.Scene.TryGetScroll(vp, out var rest);
        // Headless PresentQpc = frame + one refresh: the difference IS the refresh the producer's stamp adds.
        long refreshQpc = FluentGpu.Hooks.FrameClock.PresentQpc - FluentGpu.Hooks.FrameClock.FrameQpc;
        long lastPresentQpc = FluentGpu.Hooks.FrameClock.PresentQpc;
        var coin = seed >= 0 ? new Random(seed) : null;
        int ticks = moveFrames + restFrames, befores = 0, clamps = 0;
        var poses = new double[ticks];

        double RenderPose(double presentSec)
        {
            render.Tick(host.Plans, presentSec, 1f, sink);
            if (!render.TryGetFeedback(id, out var fb)) return double.NaN;
            if (fb.Clamped) clamps++;
            return fb.Shown;
        }

        for (int k = 0; k < ticks; k++)
        {
            if (k < moveFrames) window.QueueScrollDelta(0f, stepDip, PointerKind.Touchpad, at);
            // This tick's present on the render lattice — the previous frame's present + one refresh, which is exactly the
            // stamp the producer gave the previous frame's sample. Computed before the frame runs, so both orderings pose
            // the identical instant.
            double presentSec = (lastPresentQpc + refreshQpc) / (double)System.Diagnostics.Stopwatch.Frequency;
            bool before = coin is not null && coin.Next(2) == 0;
            if (before) { poses[k] = RenderPose(presentSec); befores++; }
            host.RunFrame();                                  // PumpScroll writes this frame's sample into the plan table
            if (!before) poses[k] = RenderPose(presentSec);
            // The next tick poses over this frame's publication, adopted at the same point in both orderings — so the ONLY
            // difference between them is whether the UI's plan write for the tick landed before the render read.
            host.Scene.CaptureScrollCoverage!(cov);
            render.Adopt(cov);
            lastPresentQpc = FluentGpu.Hooks.FrameClock.PresentQpc;
        }
        host.Scene.TryGetScroll(vp, out var end);
        return new StampRaceRun(poses, rest.Offset, end.Offset, befores, clamps);
    }

    /// <summary><see cref="ScrollMetrics.DisplacementIrregularity"/> (σ(Δ²p) / mean|Δp| over the moving ticks) of one
    /// tick-spaced pose sequence; +∞ when it has nothing to measure.</summary>
    static double StampRaceIrregularity(double[] poses, double refreshS)
    {
        var samples = new PoseSample[poses.Length];
        for (int i = 0; i < poses.Length; i++) samples[i] = new PoseSample(i * refreshS, poses[i]);
        var r = ScrollMetrics.DisplacementIrregularity(new SessionSeries(Array.Empty<InputSample>(), samples, refreshPeriodS: refreshS));
        return double.IsNaN(r.Value) ? double.PositiveInfinity : r.Value;
    }

    /// <summary>The largest step against the motion (DIP) in a forward pose sequence.</summary>
    static double StampRaceWorstBackStep(double[] poses)
    {
        double worst = 0.0;
        for (int i = 1; i < poses.Length; i++) worst = Math.Max(worst, poses[i - 1] - poses[i]);
        return double.IsNaN(worst) ? double.PositiveInfinity : worst;
    }

    // ── gate.scroll.shift-coverage-atomic ────────────────────────────────────────────────────────────────────────

    /// <summary>A measured list jumped deep (rows above keep their 40-DIP estimate) and dragged UPWARD by a
    /// DirectManipulation-shaped touchpad stream: rows entering from above realize at 64 DIP, and every one of them is a
    /// mid-layout correction above the anchor (Virtualizer.ApplyMeasured → PlanSlots.Shift) that rebases the live contact
    /// plan in the same call. The render thread holds the PREVIOUS publication — content arranged against the old
    /// WindowOrigin — until it adopts the next one. A render-thread poser model adopts each frame's coverage BEFORE the
    /// frame runs and poses it at the present it was already showing; after the frame (new sample + shifts) it poses the
    /// SAME present again with the SAME coverage — the render tick that lands between the shift and the adopt. The plan's
    /// value at an already-shown present differs only by the frame shift, so the content must not move (the defect: a
    /// transient jump by every correction — 24 DIP for those ticks, then back on adopt).</summary>
    static void ShiftCoverageAtomicChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        const float FrameMs = 1000f / 60f;
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-shift-atomic", new Size2(360, 460), 1f)); window.Show();
        var probe = new MeasuredProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe,
                                     frameTime: new FixedFrameTimeSource(FrameMs));
        host.RunFrame();
        var vp = host.Scene.Root;
        var handle = host.TryGetScrollHandle(vp)!;
        handle.ScrollTo(16_000f, ScrollMove.Immediate);
        for (int i = 0; i < 12; i++) host.RunFrame();   // the realize window + its local corrections settle at the seed
        var at = new Point2(150, 150);
        var render = new ScrollPoser();
        var sink = new ContentPoseSink();
        var cov = new ScrollCoverageTable();
        double shiftFired = 0.0, worstJump = 0.0;
        int shiftFrames = 0, frames = 0;
        for (int f = 0; f < 40; f++, frames++)
        {
            double shownPresent = FluentGpu.Hooks.FrameClock.PresentQpc / (double)System.Diagnostics.Stopwatch.Frequency;
            host.Scene.CaptureScrollCoverage!(cov);          // what the render thread holds going into this frame
            render.Adopt(cov);
            host.Scene.TryGetScroll(vp, out var sc);
            int content = (int)sc.ContentNode.Raw.Index;
            render.Tick(host.Plans, shownPresent, 1f, sink);
            float before = sink.Trans[content];
            double shiftBefore = host.Plans.FrameShiftOf(handle.Vp);
            window.QueueScrollDelta(0f, -14.4f, PointerKind.Touchpad, at);   // 0.9 DIP/ms upward
            host.RunFrame();                                  // the sample + layout realizing rows above the anchor
            double shift = host.Plans.FrameShiftOf(handle.Vp) - shiftBefore;
            render.Tick(host.Plans, shownPresent, 1f, sink);  // a render tick before the next publication is adopted
            float after = sink.Trans[content];
            if (shift != 0.0) { shiftFired += shift; shiftFrames++; }
            worstJump = Math.Max(worstJump, Math.Abs(after - before));
        }
        host.Scene.TryGetScroll(vp, out var end);
        Check("gate.scroll.shift-coverage-atomic measured corrections above the anchor shift the live contact plan mid-layout while the render thread still holds the previous publication; a render tick between the shift and the adopt poses the old content in ITS frame (the coverage carries the shift epoch it was built with) — the content never jumps by the correction",
            worstJump <= 0.5 && shiftFired >= MeasuredProbe.Real - MeasuredProbe.Estimate && shiftFrames >= 1 && end.Motion.Kind == MotionKind.Drag,
            $"worstJump={worstJump:0.###} shiftFired={shiftFired:0.##} shiftFrames={shiftFrames}/{frames} kind={end.Motion.Kind}");
    }

    // ── gate.input.ctrl-wheel-zoom-sign ──────────────────────────────────────────────────────────────────────────

    /// <summary>An app-zoom hook (<c>InputHooks.ZoomWheel</c>, browser Ctrl+wheel) over a scroller, recording each call.</summary>
    sealed class ZoomHookProbe : Component
    {
        public readonly List<float> Calls = new();
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            UseEffect(() =>
            {
                Func<float, bool> onWheel = n => { Calls.Add(n); return true; };
                hooks.ZoomWheel = onWheel;
                return () => { if (ReferenceEquals(hooks.ZoomWheel, onWheel)) hooks.ZoomWheel = null; };
            });
            return new BoxEl
            {
                Direction = 1, Width = 300, Height = 300,
                Children = [Ui.ScrollView(new BoxEl { Width = 300, Height = 3000, Fill = ColorF.FromRgba(30, 30, 30) }) with { Width = 300, Height = 300 }],
            };
        }
    }

    /// <summary>The Ctrl+wheel sign contract through the real host: the hook's argument is "&gt;0 = the wheel rotated AWAY
    /// from the user = zoom in" (InputHooks.ZoomWheel; Wavee's shell zooms in on &gt;0). A detented notch rotated away is
    /// built by the PAL's own conversion (<see cref="WheelClassifier.DetentNotch"/>: +1 whole notch) and delivered through
    /// the urgent sink with Ctrl held — the defect handed the hook the scroll-convention Dy (away = toward the content
    /// start = negative), so a wheel rotated away zoomed OUT. A Ctrl tilt never zooms; a plain notch never reaches the hook.</summary>
    static void CtrlWheelZoomSignChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-zoom-sign", new Size2(360, 360), 1f)); window.Show();
        var probe = new ZoomHookProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame(); host.RunFrame();
        var at = new Point2(150, 150);
        window.SendScroll(WheelClassifier.DetentNotch(+1, 0, 1f, 0, at, 0, KeyModifiers.Ctrl));    // rotated away
        host.RunFrame();
        window.SendScroll(WheelClassifier.DetentNotch(-1, 0, 1f, 0, at, 0, KeyModifiers.Ctrl));    // rotated toward the user
        host.RunFrame();
        int beforeTilt = probe.Calls.Count;
        window.SendScroll(WheelClassifier.DetentNotch(0, +1, 1f, 0, at, 0, KeyModifiers.Ctrl));    // a Ctrl tilt
        window.SendScroll(WheelClassifier.DetentNotch(+1, 0, 1f, 0, at, 0, KeyModifiers.None));    // a plain notch
        host.RunFrame();
        bool awayZoomsIn = probe.Calls.Count >= 1 && probe.Calls[0] > 0f;
        bool towardZoomsOut = probe.Calls.Count >= 2 && probe.Calls[1] < 0f;
        bool othersIgnored = probe.Calls.Count == beforeTilt && beforeTilt == 2;
        Check("gate.input.ctrl-wheel-zoom-sign Ctrl + a wheel notch rotated AWAY from the user hands InputHooks.ZoomWheel a POSITIVE count (zoom in) and one rotated toward the user a negative count; a Ctrl tilt and a plain notch never reach the hook",
            awayZoomsIn && towardZoomsOut && othersIgnored,
            $"calls=[{string.Join(",", probe.Calls)}]");
    }
    // ── gate.scroll.prefix-coverage ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A bound list with a two-item PERSISTENT PREFIX (a tall hero + a chrome band — Wavee's Track.Table shape:
    /// <c>PersistentPrefixCount = 2</c>) over measured 40-DIP rows, in a 300×400 viewport. The prefix items are retained
    /// children arranged at their real slots; the recyclable window starts after them.</summary>
    sealed class PrefixListProbe(int prefix, float heroH, float chromeH) : Component
    {
        public const int N = 400;
        public const float RowH = 40f, W = 300f, H = 400f;
        public float PrefixH => prefix == 2 ? heroH + chromeH : heroH;
        public override Element Render()
        {
            var layout = UseMemo(static () => new MeasuredStackVirtualLayout(RowH), DepKey.Empty);
            return new VirtualListEl
            {
                ItemCount = N,
                ItemLayout = layout,
                PersistentPrefixCount = prefix,
                RowBind = idx => (idx.Peek() < prefix ? idx.Peek() : 2) switch
                {
                    0 => new BoxEl { Height = heroH, Fill = ColorF.FromRgba(80, 30, 30) },
                    1 => new BoxEl { Height = chromeH, Fill = ColorF.FromRgba(30, 80, 30) },
                    _ => new BoxEl
                    {
                        Height = RowH,
                        Fill = Prop.Of(() => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 40 : 60))),
                    },
                },
                Width = W, Height = H, Grow = 1f,
            };
        }
    }

    /// <summary>What the UI poser (the render poser's exact arithmetic) showed for <paramref name="vp"/> on this frame,
    /// the drawn offset (window origin − the content's applied translate) and whether the pose was clamped.</summary>
    static (double Shown, bool Clamped, double Drawn, double Plan, double CoverStart, double CoverEnd) PoseOf(AppHost host, NodeHandle vp)
    {
        var handle = host.TryGetScrollHandle(vp)!;
        host.Scene.TryGetScroll(vp, out var sc);
        bool have = host.UiScrollPoser.TryGetFeedback(handle.Vp, out var fb);
        return (have ? fb.Shown : double.NaN, have && fb.Clamped, ShownOffset(host.Scene, in sc), sc.Offset, sc.CoverStart, sc.CoverEnd);
    }

    /// <summary>A persistent prefix is realized and covered, so a prefixed list at offset 0 SHOWS offset 0: the coverage
    /// the poser clamps against must describe what is actually built (the prefix band plus the recyclable window, contiguous
    /// when the window starts right after the prefix) — not the recyclable window alone. The defect published
    /// <c>CoverStart = OffsetOf(FirstRealized)</c> = hero + chrome, so both posers clamped every prefixed list to that
    /// offset at rest: the page opened looking collapsed, its top was unreachable, the realized window (planned for the
    /// plan's 0) ended below the shown viewport, and the first notches moved the plan but no pixels. Checked at rest after
    /// mount, on the first down notch (pixels move on the very next present), and again after a deep scroll past the prefix
    /// and back to 0.</summary>
    static void PrefixCoverageChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        // Track.Table's shape (hero + chrome, prefix 2) and the sidebar rail's (one head item, prefix 1).
        var detail = new System.Text.StringBuilder();
        bool all = true;
        foreach (var probe in new[] { new PrefixListProbe(2, 360f, 48f), new PrefixListProbe(1, 86.4f, 0f) })
        {
            all &= PrefixCoverageCase(strings, fonts, probe, out string d);
            detail.Append(d).Append(' ');
        }
        Check("gate.scroll.prefix-coverage a bound list with a persistent prefix (Track.Table: tall hero + chrome, PersistentPrefixCount = 2; the sidebar rail: one head item, prefix 1) shows offset 0 at rest — unclamped, drawn at 0, realized through the viewport + overscan; the first down notch moves the drawn content on the very next present; the same holds after scrolling deep past the prefix and back to 0",
            all, detail.ToString().TrimEnd());
    }

    static bool PrefixCoverageCase(StringTable strings, HeadlessFontSystem fonts, PrefixListProbe probe, out string detail)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-prefix", new Size2(360, 460), 1f)); window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        var vp = host.Scene.Root;
        var handle = host.TryGetScrollHandle(vp)!;
        Settle(host, vp, 16);
        double overscan = ScrollTunables.Current.OverscanMinPx;
        const double Viewport = PrefixListProbe.H;

        bool RestOk((double Shown, bool Clamped, double Drawn, double Plan, double CoverStart, double CoverEnd) p)
            => p.Shown == 0.0 && !p.Clamped && Math.Abs(p.Drawn) < 1e-3 && p.Plan == 0.0
               && p.CoverEnd >= Viewport + overscan;

        var mount = PoseOf(host, vp);
        bool mountOk = RestOk(mount);

        // The first down notch: the plan moves AND the drawn content moves on the very next present, unclamped.
        window.SendWheelNotch(new Point2(150, 200), 1f);
        host.RunFrame();
        var notch = PoseOf(host, vp);
        bool notchMoves = notch.Shown > 0.0 && !notch.Clamped && notch.Drawn > 0.0 && Math.Abs(notch.Drawn - notch.Shown) < 1.0;
        Settle(host, vp, 120);

        // Deep past the prefix (the prefix stays retained, the window recycles far below it) and back to the top.
        handle.ScrollTo(6000.0, ScrollMove.Immediate);
        Settle(host, vp, 16);
        var deep = PoseOf(host, vp);
        bool deepOk = Math.Abs(deep.Shown - 6000.0) < 1e-6 && !deep.Clamped && Math.Abs(deep.Drawn - 6000.0) < 0.5;
        handle.ScrollTo(0.0, ScrollMove.Immediate);
        Settle(host, vp, 16);
        var back = PoseOf(host, vp);
        bool backOk = RestOk(back);

        detail = $"[prefixH={probe.PrefixH:0.#}: mount(shown={mount.Shown:0.##} clamped={mount.Clamped} drawn={mount.Drawn:0.##} plan={mount.Plan:0.##} cover=[{mount.CoverStart:0.##},{mount.CoverEnd:0.##})) "
            + $"notch(shown={notch.Shown:0.##} clamped={notch.Clamped} drawn={notch.Drawn:0.##}) "
            + $"deep(shown={deep.Shown:0.##} clamped={deep.Clamped} drawn={deep.Drawn:0.##}) "
            + $"back(shown={back.Shown:0.##} clamped={back.Clamped} drawn={back.Drawn:0.##} plan={back.Plan:0.##} cover=[{back.CoverStart:0.##},{back.CoverEnd:0.##}))]";
        return mountOk && notchMoves && deepOk && backOk;
    }
    // ── gate.scroll.reseed-anchored ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The Library V3 sidebar rail's shape (Wavee <c>sidebar.v3.rail</c>, the owner's 2026-09-25 evidence): ONE
    /// bound list — a persistent head item (prefix 1) over 77 tile rows — on a <see cref="MeasuredStackVirtualLayout"/>
    /// whose analytic seed puts the head at 101 DIP (two tiles and a rule) while it really measures 287 (six 40-DIP tiles,
    /// 6-DIP gaps, the 8-DIP top pad and a 9-DIP rule); every row is a component that re-renders on the selection (the
    /// rail's epoch bump), and a selection republish RESEEDS the layout (the app's wholesale-publish path).</summary>
    sealed class RailReseedProbe : Component
    {
        public const int N = 200;
        public const float HeadH = 287f, HeadSeed = 101f, RowH = 46f, W = 56f, H = 571f;
        public readonly MeasuredStackVirtualLayout RailLayout = new(RowH, extentOf: static i => i == 0 ? HeadSeed : RowH);
        public readonly Signal<int> Selection = new(-1);

        sealed class RailRow(IReadSignal<int> idx, Signal<int> selected) : Component
        {
            public override Element Render()
            {
                int i = idx.Value;
                bool on = selected.Value == i;
                if (i == 0) return new BoxEl { Height = HeadH, Width = W, Fill = ColorF.FromRgba(60, 30, 30) };
                // The selected tile gains a (size-neutral) pill child: the re-render is a real reconcile, as in the app.
                return new BoxEl
                {
                    Height = RowH, Width = W, Fill = ColorF.FromRgba(30, 30, (byte)(i % 2 == 0 ? 40 : 60)),
                    Children = on ? new Element[] { new BoxEl { Width = 4f, Height = 4f, Fill = ColorF.FromRgba(200, 200, 200) } } : System.Array.Empty<Element>(),
                };
            }
        }

        public override Element Render()
            => new VirtualListEl
            {
                ItemCount = N,
                ItemLayout = RailLayout,
                PersistentPrefixCount = 1,
                ContentType = static i => i == 0 ? 1 : 0,
                RowBind = idx => Embed.Comp(() => new RailRow(idx, Selection)),
                Width = W, Height = H, Grow = 1f,
            };
    }

    /// <summary>A reseed of a measured layout never moves the rows on screen. The owner's rail "jumped" by exactly the
    /// head's seed error (287 − 101 = 186 DIP) on selecting an item: the selection republish reseeded every extent (the
    /// head back to 101 — 186 DIP of content above the viewport vanished with NO plan shift), and the next layout pass
    /// re-measured the head and shifted the plan +186 through the ordinary anchored correction — so the offset moved
    /// 3107 → 3293 while the content did not. The reseed must be anchored like a measured correction
    /// (<c>IAnchoredReseedLayout</c>, taken in the same layout call), and the always-on detector must count a jump that
    /// does get through (a bare <c>ShiftFrame</c> at rest, which moves the rows).</summary>
    static void ReseedAnchoredChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-motion-reseed", new Size2(120, 640), 1f)); window.Show();
        var probe = new RailReseedProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        var vp = host.Scene.Root;
        var handle = host.TryGetScrollHandle(vp)!;
        Settle(host, vp, 16);
        handle.ScrollTo(3107.0, ScrollMove.Immediate);
        host.ScrollChrome.NotifyMoved((int)vp.Raw.Index);          // the user's scroll: a programmatic move alone no longer arms the bar, and the live chrome is what keeps these frames running
        for (int i = 0; i < 30; i++) host.RunFrame();              // at rest (well past the detector's at-rest window)
        var before = PoseOf(host, vp);
        host.Scene.TryGetScroll(vp, out var sc0);
        long jumps0 = FluentGpu.Scroll.Diag.ScrollProbe.Jumps;

        // Select an item: the rows re-render and the layout is reseeded (the app's wholesale republish), then frames run.
        probe.RailLayout.Reseed(RailReseedProbe.N);
        probe.Selection.Value = 70;
        var worst = 0.0;
        for (int i = 0; i < 6; i++)
        {
            host.RunFrame();
            var p = PoseOf(host, vp);
            worst = Math.Max(worst, Math.Max(Math.Abs(p.Shown - before.Shown), Math.Abs(p.Drawn - before.Drawn)));
        }
        var after = PoseOf(host, vp);
        host.Scene.TryGetScroll(vp, out var sc1);
        long reseedJumps = FluentGpu.Scroll.Diag.ScrollProbe.Jumps - jumps0;
        bool anchored = worst < 0.5 && Math.Abs(sc1.ContentMain - sc0.ContentMain) < 0.5;

        // The detector itself: a bare frame shift at rest (no content change) moves the rows — one jump, cause shiftframe.
        long jumps1 = FluentGpu.Scroll.Diag.ScrollProbe.Jumps;
        handle.ShiftFrame(186.0);
        host.RunFrame();
        host.RunFrame();
        long shiftJumps = FluentGpu.Scroll.Diag.ScrollProbe.Jumps - jumps1;
        var last = FluentGpu.Scroll.Diag.ScrollProbe.LastJump;
        bool detected = shiftJumps == 1 && last.Cause == ScrollJumpCause.ShiftFrame && last.Vp == (int)vp.Raw.Index;

        Check("gate.scroll.reseed-anchored a wholesale RESEED of a measured layout (the Library V3 rail: its head seeded at 101 DIP but measuring 287, reseeded on every selection republish) never moves the rows on screen — the reseed is anchored in the same layout call as the head's re-measure, so the shown offset and the drawn content hold (the defect moved the offset +186 with the content standing still); and the always-on detector counts an at-rest jump that does get through (a bare ShiftFrame) with cause shiftframe",
            anchored && reseedJumps == 0 && detected,
            $"shown {before.Shown:0.##}->{after.Shown:0.##} drawn {before.Drawn:0.##}->{after.Drawn:0.##} worst={worst:0.##} extent {sc0.ContentMain:0.#}->{sc1.ContentMain:0.#} reseedJumps={reseedJumps} shiftJumps={shiftJumps} lastCause={last.Cause}");
    }

    // ── gate.scroll.probe-export-chain ───────────────────────────────────────────────────────────────────────────

    /// <summary>The probe CSV (schema 4) from a real host, read the way an investigation reads it: a Trace capture over the
    /// owner's list shape (a prefixed list) of a FAST spin (7 notches, 8 ms apart — a free-spinning wheel), 700 ms of
    /// silence and a second notch. Every plan and coverage row names its viewport (the vp column); the coverage published
    /// at rest starts at 0; and the plan chain is continuous — every plan starts where the previous one stood at its
    /// anchor, and the second burst starts EXACTLY at the first burst's destination. The spin's travel is the accelerated
    /// accumulation of its own notches (<c>PlanAuthor.AccelFor</c>, up to <c>AccelMax</c>), so a start that jumps by
    /// hundreds of DIP between bursts is the previous burst's destination, not something else moving the plan.</summary>
    static void ProbeExportChainChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var level = FluentGpu.Scroll.Diag.ScrollProbe.Level;
        FluentGpu.Scroll.Diag.ScrollProbe.Level = ProbeLevel.Trace;
        try
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scroll-motion-export", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new PrefixListProbe(2, 360f, 48f),
                                         frameTime: new FixedFrameTimeSource(8f));
            long ui0 = FluentGpu.Scroll.Diag.ScrollProbe.UiWriteCount;
            host.RunFrame();
            var vp = host.Scene.Root;
            Settle(host, vp, 16);
            var at = new Point2(150, 200);
            for (int i = 0; i < 7; i++) { window.SendWheelNotch(at, 1f); host.RunFrame(); }
            for (int i = 0; i < 88; i++) host.RunFrame();          // ~700 ms: the burst settles
            host.Scene.TryGetScroll(vp, out var rest1);
            window.SendWheelNotch(at, 1f);
            Settle(host, vp, 120);

            var ui = new ProbeRow[FluentGpu.Scroll.Diag.ScrollProbe.Capacity];
            int n = FluentGpu.Scroll.Diag.ScrollProbe.ReadUi(ui0, ui, out _);
            var sw = new System.IO.StringWriter();
            FluentGpu.Scroll.Diag.ScrollProbe.ExportCsv(sw, ui.AsSpan(0, n), ReadOnlySpan<ProbeRow>.Empty, System.Diagnostics.Stopwatch.Frequency, 125.0, 1.0,
                                  scenario: null);
            bool schema4 = sw.ToString().Contains("# scroll_probe_schema=4", StringComparison.Ordinal);
            var rows = new List<string[]>();
            foreach (var line in sw.ToString().Split('\n'))
            {
                string l = line.TrimEnd('\r');
                if (l.Length == 0 || l[0] == '#' || l.StartsWith("qpc_ms,", StringComparison.Ordinal)) continue;
                rows.Add(l.Split(','));
            }
            string vpText = ((int)vp.Raw.Index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            static double Note(string[] c, string key)
            {
                foreach (var kv in c[7].Split(';'))
                    if (kv.StartsWith(key + "=", StringComparison.Ordinal))
                        return double.Parse(kv.AsSpan(key.Length + 1), System.Globalization.CultureInfo.InvariantCulture);
                return double.NaN;
            }
            var plans = rows.FindAll(c => c[2] == "state_changed" && c[7].StartsWith("plan_kind=", StringComparison.Ordinal));
            var coverage = rows.FindAll(c => c[2] == "coverage");
            bool allNamed = rows.TrueForAll(c => c.Length == 9)
                            && plans.TrueForAll(c => c[8] == vpText) && coverage.TrueForAll(c => c[8] == vpText);
            var wheel = plans.FindAll(c => c[7].Contains("kind=Wheel", StringComparison.Ordinal));
            bool coverageAtRestFromZero = coverage.Count > 0 && Note(coverage[0], "start") == 0.0;
            // Burst 1 = the first 7 wheel plans; burst 2 = the 8th. The second burst starts at burst 1's destination.
            bool chained = wheel.Count == 8
                           && Math.Abs(Note(wheel[7], "start") - Note(wheel[6], "dest")) < 1e-3
                           && Math.Abs(rest1.Offset - Note(wheel[6], "dest")) < 1e-3;
            double burstTravel = wheel.Count >= 7 ? Note(wheel[6], "dest") - Note(wheel[0], "start") : 0.0;
            // 8 ms apart is the fast-spin branch (AccelRefGapS / ema > 1): the burst travels more than 7 plain notches.
            bool accelerated = burstTravel > 7 * ScrollTunables.Current.WheelNotchDip;
            Check("gate.scroll.probe-export-chain the schema-4 probe CSV of a real host names the viewport on every plan and coverage row, the coverage of a prefixed list at rest starts at 0, and a fast spin's plan chain is continuous: the next burst starts EXACTLY at the previous burst's (accelerated) destination — nothing but the wheel moves the plan",
                schema4 && allNamed && coverageAtRestFromZero && chained && accelerated,
                $"schema4={schema4} rows={rows.Count} plans={plans.Count} wheel={wheel.Count} coverage={coverage.Count} named={allNamed} cover0={(coverage.Count > 0 ? Note(coverage[0], "start") : double.NaN):0.##} "
                + $"burst1 dest={(wheel.Count >= 7 ? Note(wheel[6], "dest") : double.NaN):0.###} travel={burstTravel:0.#} rest={rest1.Offset:0.###} burst2 start={(wheel.Count >= 8 ? Note(wheel[7], "start") : double.NaN):0.###}");
        }
        finally { FluentGpu.Scroll.Diag.ScrollProbe.Level = level; }
    }
}
