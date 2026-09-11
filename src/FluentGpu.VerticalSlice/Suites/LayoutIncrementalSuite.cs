using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Pal;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// Operation ultra-fast GPU engine, P4 ("Incremental layout by default") — registered as the <c>layout-inc</c> suite
/// (also reachable via the <c>layout</c> tag alongside <see cref="LayoutShellSuite"/>). Exercises the subtree-dirty
/// propagation (<c>SceneStore.AuxFlags.SubtreeLayoutDirty</c>/<c>IsLayoutClean</c>), the arranged-rect validity column
/// (<c>FlexLayout._arranged</c>/<c>AuxFlags.ArrangedValid</c>), the Measure cross-pass ring, and the Arrange early-out
/// itself (<c>layout.md</c> §4.2/§4.6). See <c>docs/plans/operation-ultra-fast-progress.md</c> for the narrative.
/// </summary>
static class LayoutIncrementalSuite
{
    public static void Run(StringTable strings)
    {
        ArrangeEarlyOutChecks(strings);
        EarlyOutRestoresMeasuredDescendantsChecks(strings);
        SubtreeBitsClearChecks(strings);
        ResizeRelayoutsAllChangedChecks(strings);
        VirtualCleanRowsSkippedChecks(strings);
        ParityFromScratchChecks(strings);
        TextCacheRingChecks(strings);
        ParityOracleChecks(strings);
        DirtyMarkTripwireChecks(strings);
        VirtualRecycleRowsSkippedChecks(strings);
    }

    // ── gate.layout.virtual-scroll-is-layout-free ─────────────────────────────────────────────────────────────────
    // The LITERAL virtualization path the deterministic sibling gate above stands in for: a real ItemsView over a real
    // ScrollKernel, scrolled through its controller. Two legs, because either one alone would be misleading:
    //   (A) a scroll that stays inside the realized band costs ZERO Measure/Arrange and moves no row's Bounds at all -
    //       scroll is a transform, and the P4 early-out is what keeps the whole realized subtree from being re-solved
    //       every frame the offset changes;
    //   (B) a scroll far enough to move the realize window DOES run layout and DOES recycle rows - which is what makes
    //       (A)'s zero a real property of the engine rather than "the harness never actually scrolled".
    static void VirtualRecycleRowsSkippedChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("layout-virt-recycle", new Size2(320, 240), 1f));
        window.Show();
        var itemsController = new ItemsViewController();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
            new RecycleProbe { Controller = itemsController });
        for (int i = 0; i < 4; i++) host.RunFrame();
        var vp = itemsController.Viewport;

        var before = new List<(NodeHandle Node, RectF Rect)>();
        CollectRealized(host.Scene, vp, before);

        // (A) inside the realized band.
        itemsController.ScrollBy(400f);
        var a1 = host.RunFrame();
        var a2 = host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sA);
        var afterA = new List<(NodeHandle Node, RectF Rect)>();
        CollectRealized(host.Scene, vp, afterA);
        int movedA = 0;
        foreach (var (node, rect) in before)
            foreach (var (n2, r2) in afterA)
                if (node == n2)
                {
                    if (r2.X != rect.X || r2.Y != rect.Y || r2.W != rect.W || r2.H != rect.H) movedA++;
                    break;
                }
        int measureA = a1.MeasureCount + a2.MeasureCount, arrangeA = a1.ArrangeCount + a2.ArrangeCount;

        // (B) past the realized band.
        itemsController.ScrollBy(2000f);
        var b1 = host.RunFrame();
        var b2 = host.RunFrame();
        host.Scene.TryGetScroll(vp, out var sB);
        var afterB = new List<(NodeHandle Node, RectF Rect)>();
        CollectRealized(host.Scene, vp, afterB);
        int reboundB = 0;
        foreach (var (node, rect) in afterA)
            foreach (var (n2, r2) in afterB)
                if (node == n2)
                {
                    if (r2.Y != rect.Y) reboundB++;
                    break;
                }
        int measureB = b1.MeasureCount + b2.MeasureCount;

        Check("gate.layout.virtual-scroll-is-layout-free a REAL ItemsView/ScrollKernel scroll inside the realized band moves the offset for ZERO Measure/Arrange and leaves every realized row's Bounds byte-identical (scroll is a transform); a scroll PAST the band does re-realize and re-lay rows, so that zero is a property, not a stalled harness",
            System.Math.Abs(sA.OffsetY - 400f) < 0.5f && before.Count > 6 && movedA == 0 && measureA == 0 && arrangeA == 0
            && System.Math.Abs(sB.OffsetY - 2400f) < 0.5f && measureB > 0 && reboundB > 0,
            $"(A) offset->{sA.OffsetY} rows={before.Count}->{afterA.Count} movedRows={movedA} measure={measureA} arrange={arrangeA} | " +
            $"(B) offset->{sB.OffsetY} rows->{afterB.Count} reboundRows={reboundB} measure={measureB}");
    }

    sealed class RecycleProbe : Component
    {
        public ItemsViewController? Controller;
        public override Element Render() => ItemsView.Create(400,
            i => new BoxEl
            {
                Height = 40f, Direction = 0, Gap = 4f, Children =
                [
                    new BoxEl { Width = 24f, Height = 24f, Children = [new TextEl(i.ToString()) { Size = 11f }] },
                    new BoxEl { Direction = 1, Grow = 1f, Children =
                    [
                        new TextEl("row " + i) { Size = 12f },
                        new BoxEl { Direction = 0, Height = 12f, Children =
                        [
                            new BoxEl { Width = 8f, Height = 8f },
                            new BoxEl { Width = 8f, Height = 8f },
                            new BoxEl { Width = 8f, Height = 8f },
                        ] },
                    ] },
                    new BoxEl { Width = 20f, Height = 20f },
                ],
            },
            RepeatLayout.Stack(40f),
            new ListOptions { Controller = Controller, Selector = SelectorVisual.None });
    }

    static void CollectRealized(SceneStore s, NodeHandle viewport, List<(NodeHandle, RectF)> into)
    {
        if (viewport.IsNull || !s.TryGetScroll(viewport, out var sc) || sc.ContentNode.IsNull) return;
        for (var row = s.FirstChild(sc.ContentNode); !row.IsNull; row = s.NextSibling(row))
            into.Add((row, s.Bounds(row)));
    }

    // ── gate.layout.parity-oracle ─────────────────────────────────────────────────────────────────────────────────
    // The FG_LAYOUT_VERIFY oracle itself (FlexLayout.Verify.cs), exercised through its forced-run test hook: after a
    // pile of scoped incremental edits, a from-scratch re-solve of the SAME root must land on the SAME rects, and the
    // oracle must leave the scene byte-identical afterwards (it observes, it never decides).
    static void ParityOracleChecks(StringTable strings)
    {
        var widths = new float[10];
        for (int i = 0; i < widths.Length; i++) widths[i] = 25f + i * 7f;
        var scene = BuildFlatLeafTree(strings, widths, out var leaves);
        var layout = new FlexLayout(scene, new HeadlessFontSystem(strings));
        var rng = new Random(20260909);
        for (int edit = 0; edit < 12; edit++)
        {
            int i = rng.Next(widths.Length);
            float w = 15f + rng.Next(140);
            ref var li = ref scene.Layout(leaves[i]);
            li.Width = w;
            scene.Mark(leaves[i], NodeFlags.LayoutDirty);
            layout.Run(scene.Root);
        }

        var before = new RectF[leaves.Length];
        for (int i = 0; i < leaves.Length; i++) before[i] = scene.Bounds(leaves[i]);

        int mismatches = layout.VerifyLayoutParityNow(scene.Root, new Size2(3000f, 60f));

        bool restored = true;
        for (int i = 0; i < leaves.Length; i++)
        {
            var a = before[i]; var b = scene.Bounds(leaves[i]);
            if (a.X != b.X || a.Y != b.Y || a.W != b.W || a.H != b.H) { restored = false; break; }
        }

        // Release compiles the oracle out entirely and reports -1 — an honest "not available", never a silent pass.
        bool ok = FlexLayout.VerifyCompiledIn ? mismatches == 0 && restored : mismatches == -1;
        Check("gate.layout.parity-oracle the FG_LAYOUT_VERIFY oracle re-solves the incrementally-edited tree from scratch, finds ZERO diverging rects, and restores the scene byte-identically (DEBUG only; Release reports -1 = compiled out)",
            ok, $"compiledIn={FlexLayout.VerifyCompiledIn} mismatches={mismatches} boundsRestored={restored}");
    }

    // ── gate.layout.dirty-mark-tripwire ───────────────────────────────────────────────────────────────────────────
    // The DEBUG tripwire that catches a LayoutInput writer skipping Mark(LayoutDirty): the Arrange early-out claims a
    // whole subtree is unchanged, so the tripwire re-hashes that subtree against the signature its last real arrange
    // recorded. Positive case (an unmarked write is caught) AND control case (a properly marked write is silent —
    // the tripwire must never cry wolf on the normal path, or it is worthless).
    static void DirtyMarkTripwireChecks(StringTable strings)
    {
        if (!FlexLayout.VerifyCompiledIn)
        {
            Check("gate.layout.dirty-mark-tripwire (Release: the DEBUG unmarked-LayoutInput tripwire is compiled out — nothing to assert)",
                true, "compiledIn=False");
            return;
        }

        var widths = new float[6];
        for (int i = 0; i < widths.Length; i++) widths[i] = 30f;
        var scene = BuildFlatLeafTree(strings, widths, out var leaves);
        var layout = new FlexLayout(scene, new HeadlessFontSystem(strings));
        layout.Run(scene.Root);
        layout.Run(scene.Root);   // settle: everything clean + ArrangedValid, so the next Arrange takes the early-out

        // (a) an UNMARKED LayoutInput write deep in the tree — exactly the bug class the tripwire exists for.
        layout.ResetFrameDiagCounters();
        scene.Layout(leaves[3]).Width = 77f;   // deliberately NO scene.Mark(..., NodeFlags.LayoutDirty)
        layout.Run(scene.Root);
        int caught = layout.DiagUnmarkedLayoutWrites;

        // (b) control: the SAME write, done correctly. The early-out never engages on a dirty chain, so the tripwire
        // has nothing to check and must report nothing.
        layout.Run(scene.Root);   // let the (a) mismatch settle into the recorded signature
        layout.ResetFrameDiagCounters();
        ref var li = ref scene.Layout(leaves[4]);
        li.Width = 91f;
        scene.Mark(leaves[4], NodeFlags.LayoutDirty);
        layout.Run(scene.Root);
        int falsePositives = layout.DiagUnmarkedLayoutWrites;

        Check("gate.layout.dirty-mark-tripwire an UNMARKED LayoutInput write under a clean Arrange early-out is caught (DiagUnmarkedLayoutWrites >= 1), while the same write done correctly (Mark(LayoutDirty)) reports nothing",
            caught >= 1 && falsePositives == 0, $"caughtUnmarked={caught} falsePositives={falsePositives}");
    }

    // ── gate.layout.text-cache-ring ───────────────────────────────────────────────────────────────────────────────
    // A stretched text leaf is measured TWICE per layout pass at two DIFFERENT widths: once during Measure (against
    // the row's grow estimate, before free space is distributed) and once during Arrange (the column stretch
    // re-measure, at the final width). A single-slot TextMeasureCache thrashed between them, so every single pass
    // re-shaped the run. With the 2-entry ring (Scene/Columns.cs) both widths survive, so a SECOND pass over the same
    // content shapes nothing at all.
    static void TextCacheRingChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Layout(root) = LayoutInput.Default with { Direction = 0, Width = 300f, Height = 100f };

        // A grow column holding the text: measured at the WHOLE remaining width (the row's pre-pass estimate), then
        // arranged at its share of the distributed free space — two distinct measure widths for the same run.
        var col = scene.CreateNode(1);
        scene.Layout(col) = LayoutInput.Default with { Direction = 1, FlexGrow = 1f, AlignItems = FlexAlign.Stretch };
        scene.AppendChild(root, col);
        var text = scene.CreateNode(1);
        scene.Paint(text).VisualKind = VisualKind.Text;
        scene.Paint(text).Text = strings.Intern("a text run long enough that its wrap width genuinely matters here");
        scene.Layout(text) = LayoutInput.Default with
        {
            TextStyle = new FluentGpu.Text.TextStyle(default, 12f, 400, FluentGpu.Foundation.TextWrap.Wrap),
        };
        scene.AppendChild(col, text);
        var sibling = scene.CreateNode(1);
        scene.Layout(sibling) = LayoutInput.Default with { FlexGrow = 1f, Height = 10f };
        scene.AppendChild(root, sibling);

        var layout = new FlexLayout(scene, fonts);
        layout.ResetFrameDiagCounters();
        layout.Run(root);
        int firstMiss = layout.DiagTextMiss;
        float measuredW = scene.Bounds(text).W;

        // Second pass over the SAME content, with the text leaf explicitly re-dirtied so P4's Measure ring cannot
        // short-circuit it: the run is re-measured at both widths again and BOTH must come out of the text cache.
        long shapesBefore = fonts.ShapeCount;
        layout.ResetFrameDiagCounters();
        scene.Mark(text, NodeFlags.LayoutDirty);
        layout.Run(root);
        int secondMiss = layout.DiagTextMiss;
        long shapes = fonts.ShapeCount - shapesBefore;

        Check("gate.layout.text-cache-ring text in a stretched column, re-laid twice ⇒ the second pass shapes NOTHING (TextShapes == 0, TextShapeMisses == 0) — the 2-entry ring holds BOTH the measure-pass and the arrange-pass widths where a single slot thrashed",
            firstMiss >= 2 && secondMiss == 0 && shapes == 0 && measuredW > 0f,
            $"firstPassMisses={firstMiss} secondPassMisses={secondMiss} secondPassShapes={shapes} textW={measuredW}");
    }

    // ── a fixed-size ClipToBounds boundary, 6 levels of single-child BoxEl wrappers, ending in a bound-width leaf ──
    sealed class DeepBoundaryProbe : Component
    {
        public readonly Signal<float> LeafWidth = new(40f);
        public override Element Render()
        {
            Element Wrap(int depth, Element inner) => depth == 0 ? inner : new BoxEl
            {
                Direction = 1, Grow = 1f, Children = [Wrap(depth - 1, inner)],
            };
            var leaf = new BoxEl { Width = LeafWidth, Height = 20f };
            return new BoxEl
            {
                Direction = 1, Width = 300f, Height = 300f, ClipToBounds = true,
                Children = [Wrap(6, leaf)],
            };
        }
    }

    // ── gate.layout.arrange-early-out ─────────────────────────────────────────────────────────────────────────────
    static void ArrangeEarlyOutChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("layout-early-out", new Size2(400, 400), 1f));
        window.Show();
        var probe = new DeepBoundaryProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame();
        for (int i = 0; i < 2; i++) host.RunFrame();   // settle any realize-after-layout catch-up passes

        probe.LeafWidth.Value = 55f;   // dirty ONLY the leaf, 6 levels inside the boundary
        var f = host.RunFrame();

        Check("gate.layout.arrange-early-out a bound-width leaf 6 levels inside a fixed-size boundary ⇒ only the leaf's ancestor chain re-arranges (ArrangeCount<=10, MeasureCount<=10), not the whole tree",
            f.ArrangeCount <= 10 && f.MeasureCount <= 10,
            $"arrangeCount={f.ArrangeCount} measureCount={f.MeasureCount}");
    }

    // ── gate.layout.early-out-restores-measured-descendants ────────────────────────────────────────────────────────
    // The SeekBar / MediaSeekBar failure: a dirty ancestor Measure-descends a clean ZStack (ring-excluded), scribbles
    // Grow=1 Width=NaN rail leaves to W=0, then Arrange early-outs the ZStack (same rect) and used to leave those
    // descendants stranded. Host is FIRST + explicit Width so a sibling-width edit dirties the row without moving the
    // host's arranged rect — the skip actually fires.
    sealed class EarlyOutRestoreProbe : Component
    {
        public readonly Signal<float> SiblingWidth = new(80f);
        public NodeHandle Host, Rail, Fill, Thumb;
        public override Element Render() => new BoxEl
        {
            Direction = 0, Width = 400f, Height = 22f,
            Children =
            [
                new BoxEl
                {
                    ZStack = true, Width = 200f, Height = 22f, OnRealized = h => Host = h,
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, Height = 4f, AlignSelf = FlexAlign.Center,
                            Fill = ColorF.FromRgba(0x40, 0x40, 0x40),
                            ClipToBounds = true, ZStack = true, OnRealized = h => Rail = h,
                            Children =
                            [
                                new BoxEl { Grow = 1f, Height = 4f, Fill = ColorF.FromRgba(0x2E, 0x6C, 0xE0), OnRealized = h => Fill = h },
                            ],
                        },
                        new BoxEl { Width = 22f, Height = 22f, OnRealized = h => Thumb = h },
                    ],
                },
                new BoxEl { Width = SiblingWidth, Height = 22f },
            ],
        };
    }

    static void EarlyOutRestoresMeasuredDescendantsChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("layout-early-out-restore", new Size2(400, 60), 1f));
        window.Show();
        var probe = new EarlyOutRestoreProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame();
        for (int i = 0; i < 2; i++) host.RunFrame();

        var host0 = host.Scene.Bounds(probe.Host);
        var rail0 = host.Scene.Bounds(probe.Rail);
        var fill0 = host.Scene.Bounds(probe.Fill);
        var thumb0 = host.Scene.Bounds(probe.Thumb);
        bool settled = !probe.Host.IsNull && !probe.Rail.IsNull && !probe.Fill.IsNull && !probe.Thumb.IsNull
            && host0.W > 0f && Math.Abs(rail0.W - host0.W) < 0.01f && Math.Abs(fill0.W - rail0.W) < 0.01f;
        Check("gate.layout.early-out-restores-measured-descendants: precondition — rail W equals host W (>0) after settle",
            settled, $"host={host0} rail={rail0} fill={fill0} thumb={thumb0}");

        probe.SiblingWidth.Value = 40f;   // dirties the row; host subtree stays clean and its rect is byte-identical
        host.RunFrame();
        for (int i = 0; i < 2; i++) host.RunFrame();

        var host1 = host.Scene.Bounds(probe.Host);
        var rail1 = host.Scene.Bounds(probe.Rail);
        var fill1 = host.Scene.Bounds(probe.Fill);
        var thumb1 = host.Scene.Bounds(probe.Thumb);
        bool hostUnmoved = host1.X == host0.X && host1.Y == host0.Y && host1.W == host0.W && host1.H == host0.H;
        bool railOk = rail1.W > 0f && Math.Abs(rail1.W - host1.W) < 0.01f;
        bool fillOk = Math.Abs(fill1.W - rail1.W) < 0.01f;
        bool thumbOk = thumb1.X == thumb0.X && thumb1.Y == thumb0.Y && thumb1.W == thumb0.W && thumb1.H == thumb0.H;

        int mismatches = new FlexLayout(host.Scene, new HeadlessFontSystem(strings))
            .VerifyLayoutParityNow(host.Scene.Root, new Size2(400f, 60f));
        bool parityOk = FlexLayout.VerifyCompiledIn ? mismatches == 0 : mismatches == -1;

        Check("gate.layout.early-out-restores-measured-descendants a sibling-width edit on a row whose clean ZStack host keeps a byte-identical rect ⇒ rail W == host W (not 0), inner fill matches the rail, thumb unchanged; from-scratch parity reports 0 diverging rects (Release: -1 = compiled out)",
            settled && hostUnmoved && railOk && fillOk && thumbOk && parityOk,
            $"host {host0}->{host1} rail {rail0}->{rail1} fill {fill0}->{fill1} thumb {thumb0}->{thumb1} compiledIn={FlexLayout.VerifyCompiledIn} mismatches={mismatches}");
    }

    // ── gate.layout.subtree-bits-clear ────────────────────────────────────────────────────────────────────────────
    static bool AnySubtreeBitSet(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return false;
        if (s.IsSubtreeLayoutDirty(n)) return true;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            if (AnySubtreeBitSet(s, c)) return true;
        return false;
    }

    static void SubtreeBitsClearChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("layout-bits-clear", new Size2(400, 400), 1f));
        window.Show();
        var probe = new DeepBoundaryProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame();
        for (int i = 0; i < 2; i++) host.RunFrame();

        probe.LeafWidth.Value = 90f;
        host.RunFrame();
        for (int i = 0; i < 2; i++) host.RunFrame();   // fully settle — nothing should be dirty by now

        bool anySet = AnySubtreeBitSet(host.Scene, host.Scene.Root);
        bool rootClean = host.Scene.IsLayoutClean(host.Scene.Root);
        Check("gate.layout.subtree-bits-clear after a dirty leaf settles, AuxFlags.SubtreeLayoutDirty is clear EVERYWHERE in the tree (ClearLayoutDirty's ancestor-chain walk leaves no stray bit) and the root reads layout-clean",
            !anySet && rootClean, $"anySubtreeBitSet={anySet} rootClean={rootClean}");
    }

    // ── gate.layout.resize-relayouts-all-changed ──────────────────────────────────────────────────────────────────
    sealed class StretchRowsProbe : Component
    {
        public override Element Render()
        {
            var rows = new Element[6];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl { Height = 20f };
            return new BoxEl { Direction = 1, Grow = 1f, Children = rows };
        }
    }

    static void ResizeRelayoutsAllChangedChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("layout-resize", new Size2(300, 200), 1f));
        window.Show();
        var probe = new StretchRowsProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame();
        for (int i = 0; i < 2; i++) host.RunFrame();

        var rootBefore = host.Scene.Bounds(host.Scene.Root);
        window.ClientSizePx = new Size2(500, 200);
        var f = host.RunFrame();
        var rootAfter = host.Scene.Bounds(host.Scene.Root);

        // Find every stretched row and confirm its width actually followed the new window width — the early-out
        // must NOT have (wrongly) treated a truly-resized subtree as "unchanged" just because it settled once before.
        bool allWidened = true;
        int rowCount = 0;
        for (var c = host.Scene.FirstChild(host.Scene.Root); !c.IsNull; c = host.Scene.NextSibling(c))
            FindRows(host.Scene, c, ref allWidened, ref rowCount);

        Check("gate.layout.resize-relayouts-all-changed a window resize actually widens every stretched row (the early-out never mistakes a resized subtree for an unchanged one)",
            rootBefore.W != rootAfter.W && rowCount == 6 && allWidened && f.ArrangeCount > 0,
            $"rootW {rootBefore.W}->{rootAfter.W} rowCount={rowCount} allWidened={allWidened} arrangeCount={f.ArrangeCount}");
    }

    static void FindRows(SceneStore s, NodeHandle n, ref bool allWidened, ref int rowCount)
    {
        if (Math.Abs(s.Bounds(n).H - 20f) < 0.5f && s.FirstChild(n).IsNull)
        {
            rowCount++;
            if (s.Bounds(n).W < 490f) allWidened = false;
        }
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) FindRows(s, c, ref allWidened, ref rowCount);
    }

    static NodeHandle FindNodeWithChildCount(SceneStore s, NodeHandle n, int count)
    {
        if (n.IsNull) return NodeHandle.Null;
        int cc = 0;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) cc++;
        if (cc == count) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindNodeWithChildCount(s, c, count);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    // ── gate.layout.virtual-clean-rows-skipped ────────────────────────────────────────────────────────────────────
    // A deterministic stand-in for "48 realized bound rows, one row's content changes": 48 SIBLING rows (not routed
    // through ItemsView/ScrollKernel — that recycler's realize-then-catch-up timing is its own subsystem and not
    // what P4 owns) each ~12 nodes deep, mounted once and settled, then ONE row's title changes (the plan's "a
    // one-row scroll" stand-in — the same shape of update a recycled slot's rebind produces: one row's subtree
    // content changes, the other 47 must stay untouched). Exercises the exact same Measure/Arrange fan-out the
    // virtualization recycler would hit per row; what's NOT covered by this specific gate (documented in the
    // progress file) is the ItemsView/ScrollKernel realize-timing path itself.
    sealed class ManyRowsProbe : Component
    {
        public const int RowCount = 48;
        public readonly Signal<string> Title0 = new("Row 0");

        static Element Row(Signal<string>? boundTitle, int i) => new BoxEl
        {
            Width = 320f, Height = 36f, Direction = 0, Gap = 4f,
            Children =
            [
                new BoxEl { Width = 24f, Height = 24f, Children = [ new TextEl(i.ToString()) { Size = 11f } ] },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Children =
                    [
                        boundTitle is null ? new TextEl("Row " + i) { Size = 12f } : new TextEl(boundTitle) { Size = 12f },
                        new BoxEl
                        {
                            Direction = 0, Height = 12f, Children =
                            [
                                new BoxEl { Width = 8f, Height = 8f },
                                new BoxEl { Width = 8f, Height = 8f },
                                new BoxEl { Width = 8f, Height = 8f },
                            ],
                        },
                    ],
                },
                new BoxEl { Width = 20f, Height = 20f },
                new BoxEl { Width = 20f, Height = 20f },
            ],
        };

        public override Element Render()
        {
            var rows = new Element[RowCount];
            for (int i = 0; i < RowCount; i++) rows[i] = Row(i == 0 ? Title0 : null, i) with { Key = "row" + i };
            return new BoxEl { Direction = 1, Width = 340f, Height = 2000f, Gap = 0f, Children = rows };
        }
    }

    static void VirtualCleanRowsSkippedChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("layout-virt-clean", new Size2(340, 2000), 1f));
        window.Show();
        var probe = new ManyRowsProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame();
        for (int i = 0; i < 2; i++) host.RunFrame();   // fully settle

        // Snapshot every row's Bounds before the one-row content change.
        var listRoot = FindNodeWithChildCount(host.Scene, host.Scene.Root, ManyRowsProbe.RowCount);
        var beforeRects = new List<RectF>();
        for (var row = host.Scene.FirstChild(listRoot); !row.IsNull; row = host.Scene.NextSibling(row))
            beforeRects.Add(host.Scene.Bounds(row));
        Check("gate.layout.virtual-clean-rows-skipped: precondition — all 48 rows mounted", beforeRects.Count == ManyRowsProbe.RowCount, $"rows={beforeRects.Count}");

        // ONE row's title changes (a bound TextEl.Text write — the same shape as a recycled slot's rebind touching
        // one row's content) — the other 47 rows must stay entirely untouched (clean subtree, unchanged rect).
        probe.Title0.Value = "Row 0 CHANGED";
        var f = host.RunFrame();

        var afterRects = new List<RectF>();
        for (var row = host.Scene.FirstChild(listRoot); !row.IsNull; row = host.Scene.NextSibling(row))
            afterRects.Add(host.Scene.Bounds(row));

        bool othersUnchanged = true;
        int compareCount = Math.Min(beforeRects.Count, afterRects.Count);
        for (int i = 1; i < compareCount; i++)
        {
            var b = beforeRects[i]; var a = afterRects[i];
            if (b.X != a.X || b.Y != a.Y || b.W != a.W || b.H != a.H) { othersUnchanged = false; break; }
        }

        // ~12 nodes in the changed row's subtree (+ its ancestor chain to the list root, a handful more) — nowhere
        // near "all 48 rows x 12 nodes" (~576), which is what a non-incremental pass would cost.
        int budget = 12 + 8;
        bool measureOk = f.MeasureCount <= budget;
        bool arrangeOk = f.ArrangeCount <= budget;
        bool textOk = f.TextShapes <= 2;

        Check("gate.layout.virtual-clean-rows-skipped 48 sibling rows (~12 nodes/row); ONE row's bound title changes ⇒ MeasureCount<=20, ArrangeCount<=20, TextShapes<=2 (not O(48 rows)), and every OTHER row's Bounds is byte-identical",
            measureOk && arrangeOk && textOk && othersUnchanged,
            $"measure={f.MeasureCount} arrange={f.ArrangeCount} textShapes={f.TextShapes} rows={compareCount} othersUnchanged={othersUnchanged}");
    }

    // ── gate.layout.parity-from-scratch ───────────────────────────────────────────────────────────────────────────
    static SceneStore BuildFlatLeafTree(StringTable strings, float[] widths, out NodeHandle[] leaves)
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;   // SceneStore.Root is a plain settable property — CreateNode does NOT assign it
        scene.Layout(root) = LayoutInput.Default with { Direction = 0, Width = 3000f, Height = 60f };
        leaves = new NodeHandle[widths.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            var leaf = scene.CreateNode(1);
            scene.Layout(leaf) = LayoutInput.Default with { Width = widths[i], Height = 20f, Margin = new Edges4() };
            scene.AppendChild(root, leaf);
            leaves[i] = leaf;
        }
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(root);
        return scene;
    }

    static void ParityFromScratchChecks(StringTable strings)
    {
        const int n = 12;
        var rng = new Random(20260908);
        var widths = new float[n];
        for (int i = 0; i < n; i++) widths[i] = 30f + i * 4f;

        var scene = BuildFlatLeafTree(strings, widths, out var leaves);
        var layout = new FlexLayout(scene, new HeadlessFontSystem(strings));
        var root = scene.Root;

        // 20 random scoped edits: mutate one leaf's width, mark it LayoutDirty, re-solve — exercising the subtree-
        // dirty propagation + Arrange early-out + Measure ring on every intervening pass, exactly like a live app's
        // scattered `setState`s would.
        for (int edit = 0; edit < 20; edit++)
        {
            int i = rng.Next(n);
            float w = 15f + rng.Next(120);
            widths[i] = w;
            ref var li = ref scene.Layout(leaves[i]);
            li.Width = w;
            scene.Mark(leaves[i], NodeFlags.LayoutDirty);
            layout.Run(root);
        }

        // A from-scratch build with the SAME final widths — zero incremental history, so every node's Bounds here is
        // the ground truth a full solve would produce for this exact final state.
        var freshScene = BuildFlatLeafTree(strings, widths, out var freshLeaves);

        bool allMatch = true;
        string mismatch = "";
        for (int i = 0; i < n; i++)
        {
            var a = scene.Bounds(leaves[i]);
            var b = freshScene.Bounds(freshLeaves[i]);
            if (Math.Abs(a.X - b.X) > 0.01f || Math.Abs(a.Y - b.Y) > 0.01f || Math.Abs(a.W - b.W) > 0.01f || Math.Abs(a.H - b.H) > 0.01f)
            {
                allMatch = false;
                mismatch = $"leaf#{i} incremental={a} freshSolve={b}";
                break;
            }
        }
        var rootA = scene.Bounds(root); var rootB = freshScene.Bounds(freshScene.Root);
        bool rootMatches = Math.Abs(rootA.W - rootB.W) < 0.01f && Math.Abs(rootA.H - rootB.H) < 0.01f;

        Check("gate.layout.parity-from-scratch 20 random scoped width edits, each re-solved through the incremental (early-out+ring) path, land on the EXACT same Bounds a from-scratch full solve of the final state produces",
            allMatch && rootMatches, allMatch ? $"rootMatches={rootMatches}" : mismatch);
    }
}
