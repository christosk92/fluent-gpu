using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Extent;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Scroll rework Wave 1 step 1: the runtime pieces (PlanSlots seqlock, ScrollPoser, WheelClassifier,
/// ScrollRouter, ScrollHandle, Virtualizer) as pure, engine-free behaviour tests.</summary>
public sealed class ScrollRuntimeTests
{
    private static readonly ScrollViewportId VpA = new(7, 1);
    private static readonly ScrollViewportId VpB = new(9, 3);

    private static ScrollPlan Cubic(int node, double t0, double dur, double p0, double p1, double max = 1e9)
    {
        var seg = new MotionSeg(SegKind.Cubic, t0, t0 + dur, p0, p1);
        return new ScrollPlan(seg, default, default, default, 1, node, 0, 1, 0.0, max, 0.0, 0.0, 0.0, OverpanPolicy.None, MotionKind.Wheel, default);
    }

    // ── PlanSlots ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanSlots_AllocateWriteReadRelease_RoundTrips()
    {
        var slots = new PlanSlots();
        int wakes = 0;
        slots.OnWritten = () => wakes++;
        Assert.True(slots.Allocate(VpA, ScrollPlan.Idle(VpA.Node, 10.0, 0.0, 100.0)));
        ulong e0 = slots.Epoch;
        Assert.True(slots.Write(VpA, Cubic(VpA.Node, 0.0, 0.257, 10.0, 42.0)));
        Assert.True(slots.Epoch > e0);
        Assert.True(slots.TryRead(VpA, out ScrollPlan read));
        Assert.Equal(42.0, read.Dest);
        Assert.False(slots.TryRead(VpB, out _));
        slots.Release(VpA);
        Assert.False(slots.TryRead(VpA, out _));
        Assert.False(slots.Write(VpA, read));
        Assert.Equal(3, wakes);   // allocate, write, release
    }

    /// <summary>Every render tick scans the slot table up to its high-water mark. A page that once bound 40 scrollers
    /// (a shelf-heavy home page) and then unmounted them must not leave every later tick scanning 40 dead slots: the
    /// scan bound follows the highest LIVE slot, and freed slots are reused lowest-first.</summary>
    [Fact]
    public void PlanSlots_ReleaseShrinksTheScanBoundToTheHighestLiveSlot()
    {
        var slots = new PlanSlots();
        for (int i = 0; i < 40; i++) Assert.True(slots.Allocate(new ScrollViewportId(100 + i, 1), ScrollPlan.Idle(100 + i, 0.0, 0.0, 10.0)));
        Assert.Equal(40, slots.ScanBound);
        for (int i = 39; i >= 1; i--) slots.Release(new ScrollViewportId(100 + i, 1));
        Assert.Equal(1, slots.ScanBound);
        Assert.True(slots.TryRead(new ScrollViewportId(100, 1), out _));
        // Released in the middle, not at the top: the bound stays at the top live slot until that one goes too.
        Assert.True(slots.Allocate(new ScrollViewportId(200, 1), ScrollPlan.Idle(200, 0.0, 0.0, 10.0)));
        Assert.True(slots.Allocate(new ScrollViewportId(201, 1), ScrollPlan.Idle(201, 0.0, 0.0, 10.0)));
        Assert.Equal(3, slots.ScanBound);
        slots.Release(new ScrollViewportId(200, 1));
        Assert.Equal(3, slots.ScanBound);
        slots.Release(new ScrollViewportId(201, 1));
        Assert.Equal(1, slots.ScanBound);
    }

    [Fact]
    public void PlanSlots_StaleGeneration_DoesNotReadNewViewportsPlan()
    {
        var slots = new PlanSlots();
        var old = new ScrollViewportId(5, 1);
        var reused = new ScrollViewportId(5, 2);
        slots.Allocate(old, ScrollPlan.Idle(5, 1.0, 0.0, 10.0));
        slots.Release(old);
        slots.Allocate(reused, ScrollPlan.Idle(5, 2.0, 0.0, 10.0));
        Assert.False(slots.TryRead(old, out _));
        Assert.True(slots.TryRead(reused, out ScrollPlan p));
        Assert.Equal(2.0, p.Dest);
    }

    [Fact]
    public void PlanSlots_Shift_PreservesEvalMinusDelta()
    {
        var slots = new PlanSlots();
        slots.Allocate(VpA, Cubic(VpA.Node, 1.0, 0.257, 100.0, 132.0));
        slots.TryRead(VpA, out ScrollPlan before);
        slots.Shift(VpA, 17.5);
        slots.TryRead(VpA, out ScrollPlan after);
        for (double t = 0.9; t < 1.5; t += 0.01)
            Assert.Equal(before.Eval(t, out _, out _) + 17.5, after.Eval(t, out _, out _), 9);
        Assert.Equal(before.Max + 17.5, after.Max);
    }

    [Fact]
    public void PlanSlots_Seqlock_NeverTearsUnderConcurrentWriter()
    {
        var slots = new PlanSlots();
        slots.Allocate(VpA, Cubic(VpA.Node, 0.0, 1.0, 1.0, 2.0, 3.0));
        using var stop = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            double k = 1.0;
            while (!stop.IsSet)
            {
                // A self-consistent plan: P0 = k, P1 = 2k, Max = 3k — a torn read breaks one of the relations.
                slots.Write(VpA, Cubic(VpA.Node, 0.0, 1.0, k, 2.0 * k, 3.0 * k));
                k += 1.0;
            }
        });
        writer.Start();
        int reads = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < deadline)
        {
            Assert.True(slots.TryRead(VpA, out ScrollPlan p));
            double p0 = p.S0.P0;
            Assert.Equal(2.0 * p0, p.S0.P1);
            Assert.Equal(3.0 * p0, p.Max);
            reads++;
        }
        stop.Set();
        writer.Join();
        Assert.True(reads > 100);
    }

    [Fact]
    public void PlanSlots_Capacity64_ThenRefuses()
    {
        var slots = new PlanSlots();
        for (int i = 0; i < PlanSlots.Capacity; i++)
            Assert.True(slots.Allocate(new ScrollViewportId(i, 1), ScrollPlan.Idle(i, 0, 0, 0)));
        Assert.False(slots.Allocate(new ScrollViewportId(999, 1), ScrollPlan.Idle(999, 0, 0, 0)));
        Assert.Equal(PlanSlots.Capacity, slots.LiveCount);
    }

    // ── ScrollPoser ───────────────────────────────────────────────────────────────────────────────────────────

    private sealed class RecordingSink : IScrollPoseSink
    {
        public readonly Dictionary<int, float> Content = new();
        public readonly Dictionary<(int, EffectChannel), float> Effects = new();
        public int Calls;
        public void PoseViewport(int vpNode, double shown) { }
        public void PoseContent(int node, bool horizontal, float trans, bool changed) { Content[node] = trans; Calls++; }
        public void PoseEffect(int node, EffectChannel channel, float value, bool changed) { Effects[(node, channel)] = value; Calls++; }
        public void PoseTransform(int node, in EffectTransform t, bool changed)
        {
            Transforms[node] = t;
            Effects[(node, EffectChannel.TransX)] = t.Tx;
            Effects[(node, EffectChannel.TransY)] = t.Ty;
            Effects[(node, EffectChannel.ScaleXY)] = t.Scale * t.Stretch;
            Calls++;
        }
        public readonly Dictionary<int, EffectTransform> Transforms = new();
    }

    private static ScrollCoverageTable Coverage(double origin, double start, double end, double viewport, double total, ReadOnlySpan<ScrollEffectRow> effects)
    {
        var cov = new ScrollCoverageTable();
        cov.AddRow(new ScrollCoverageRow(VpA.Node, VpA.Gen, ContentNodeIndex: 100, origin, start, end, viewport, total, false, 0, 0, 0.0), effects);
        return cov;
    }

    [Fact]
    public void Poser_CoverageClamp_NeverShowsOutsideWindow_AndRecordsClamp()
    {
        var slots = new PlanSlots();
        // Plan runs 1000 → 3000 but coverage only realizes [1500, 2500) with a 400 viewport: shown ∈ [1500, 2100].
        slots.Allocate(VpA, Cubic(VpA.Node, 0.0, 1.0, 1000.0, 3000.0));
        var poser = new ScrollPoser();
        poser.Adopt(Coverage(origin: 1500.0, start: 1500.0, end: 2500.0, viewport: 400.0, total: 100_000.0, ReadOnlySpan<ScrollEffectRow>.Empty));
        var sink = new RecordingSink();

        poser.Tick(slots, 0.0, 1f, sink);                       // p = 1000 → clamped up to 1500
        Assert.True(poser.TryGetFeedback(VpA, out var f0));
        Assert.Equal(1500.0, f0.Shown);
        Assert.True(f0.Clamped);
        Assert.Equal(0f, sink.Content[100]);                    // origin − shown = 0

        poser.Tick(slots, 0.5, 1f, sink);                       // p = 1000 + 2000·(1.5·0.5 − 0.5·0.125) = 2375 → clamped to 2100
        Assert.True(poser.TryGetFeedback(VpA, out var f1));
        Assert.Equal(2100.0, f1.Shown);
        Assert.True(f1.Clamped);
        Assert.Equal(-600f, sink.Content[100]);

        poser.Tick(slots, 0.2, 1f, sink);                       // p = 1000 + 2000·(0.3 − 0.004) = 1592 → inside, no clamp
        Assert.True(poser.TryGetFeedback(VpA, out var f2));
        Assert.Equal(1592.0, f2.Shown, 9);
        Assert.False(f2.Clamped);
    }

    /// <summary>(Shift, coverage) are atomic from the poser's view. A measured correction above the anchor shifts the plan
    /// frame the moment layout finds it (Virtualizer.ApplyMeasured → PlanSlots.Shift), while the render thread keeps
    /// posing the PREVIOUS publication's content (arranged relative to the old WindowOrigin) until the next one is
    /// adopted. Posing the shifted plan against that old coverage moved the content by the correction for those ticks
    /// and back on adopt — a transient jump the user saw as a hitch mid-drag.</summary>
    [Fact]
    public void Poser_PlanShiftNewerThanItsCoverage_PosesInTheCoveragesFrame_UntilTheNextAdopt()
    {
        var slots = new PlanSlots();
        slots.Allocate(VpA, ScrollPlan.Idle(VpA.Node, 1_000.0, 0.0, 100_000.0));
        var poser = new ScrollPoser();
        poser.Adopt(Coverage(origin: 900.0, start: 900.0, end: 2_000.0, viewport: 400.0, total: 100_000.0, ReadOnlySpan<ScrollEffectRow>.Empty));
        var sink = new RecordingSink();
        poser.Tick(slots, 1.0, 1f, sink);
        Assert.Equal(-100f, sink.Content[100]);

        Assert.True(slots.Shift(VpA, 24.0));                 // a row above the anchor measured 24 DIP taller
        Assert.Equal(24.0, slots.FrameShiftOf(VpA));
        poser.Tick(slots, 1.0 + 1 / 120.0, 1f, sink);        // a render tick before the next publication is adopted
        Assert.Equal(-100f, sink.Content[100]);              // the old content does not move
        Assert.True(poser.TryGetFeedback(VpA, out var fb));
        Assert.Equal(1_024.0, fb.Shown, 9);                  // feedback speaks the plan's (shifted) frame

        var fresh = new ScrollCoverageTable();               // the next publication: laid out after the shift
        fresh.AddRow(new ScrollCoverageRow(VpA.Node, VpA.Gen, 100, 924.0, 924.0, 2_024.0, 400.0, 100_024.0, false, 0, 0,
            slots.FrameShiftOf(VpA)), ReadOnlySpan<ScrollEffectRow>.Empty);
        poser.Adopt(fresh);
        poser.Tick(slots, 1.0 + 2 / 120.0, 1f, sink);
        Assert.Equal(-100f, sink.Content[100]);              // and the adopt does not move it either
    }

    [Fact]
    public void Poser_OverpanAtContentStart_IsNotAClamp()
    {
        var slots = new PlanSlots();
        slots.Allocate(VpA, ScrollPlan.Idle(VpA.Node, -30.0, 0.0, 1000.0));   // a rubber-banded position above row 0
        var poser = new ScrollPoser();
        poser.Adopt(Coverage(0.0, 0.0, 800.0, 400.0, 5000.0, ReadOnlySpan<ScrollEffectRow>.Empty));
        var sink = new RecordingSink();
        poser.Tick(slots, 0.0, 1f, sink);
        Assert.True(poser.TryGetFeedback(VpA, out var f));
        Assert.Equal(-30.0, f.Shown);
        Assert.False(f.Clamped);
        Assert.Equal(30f, sink.Content[100]);
    }

    [Fact]
    public void Poser_StickyEffect_SitsOnContentPixelGrid_BitExact()
    {
        var slots = new PlanSlots();
        var poser = new ScrollPoser();
        var sink = new RecordingSink();
        const float scale = 1.5f;
        const double origin = 1234.0, nodeY = 1300.0, nodeH = 40.0, scopeEnd = 2000.0;
        var effects = new[]
        {
            new ScrollEffectRow(NodeIndex: 200, ScrollEffect.Sticky(inset: 0f), new EffectGeometry(nodeY, nodeH, scopeEnd, 100_000.0, 400.0, 0f, 0f)),
        };
        poser.Adopt(Coverage(origin, origin - 100.0, origin + 3000.0, 400.0, 100_000.0, effects));

        // Sweep a smooth motion through positions that are NOT on the 1.5× device grid.
        slots.Allocate(VpA, Cubic(VpA.Node, 0.0, 1.0, 1300.0, 1700.0));
        for (double t = 0.0; t <= 1.0; t += 0.0137)
        {
            poser.Tick(slots, t, scale, sink);
            float contentTrans = sink.Content[100];
            float stickyTrans = sink.Effects[(200, EffectChannel.TransY)];
            poser.TryGetFeedback(VpA, out var f);

            // The content translate is on the device grid.
            Assert.Equal((double)MathF.Round(contentTrans * scale), (double)(contentTrans * scale), 4);

            // Header screen position = (nodeY − origin) + contentTrans + stickyTrans must equal exactly what the
            // content's snapped position implies: pSnapped = origin − contentTrans; sticky = clamp(pSnapped − nodeY, 0, limit).
            double pSnapped = origin - (double)contentTrans;
            float expected = ScrollEffectEval.Evaluate(effects[0].Effect, pSnapped, effects[0].Geometry);
            Assert.Equal(expected, stickyTrans);   // bit-exact
            // While pinned, the header's screen top is exactly the viewport top (0) in double arithmetic — no half pixel.
            if (pSnapped > nodeY && pSnapped < scopeEnd - nodeH)
                Assert.Equal(0.0, (nodeY - origin) + contentTrans + stickyTrans, 9);
            Assert.True(Math.Abs(f.Shown - (origin - contentTrans)) <= 0.5 / scale + 1e-9);
        }
    }

    [Fact]
    public void Poser_HasActive_And_Changed_Semantics()
    {
        var slots = new PlanSlots();
        slots.Allocate(VpA, Cubic(VpA.Node, 0.0, 1.0, 0.0, 400.0));
        var poser = new ScrollPoser();
        poser.Adopt(Coverage(0.0, 0.0, 5000.0, 400.0, 5000.0, ReadOnlySpan<ScrollEffectRow>.Empty));
        var sink = new RecordingSink();

        Assert.True(poser.Tick(slots, 0.1, 1f, sink));      // fresh adopt → changed
        Assert.True(poser.HasActive);
        Assert.True(poser.Tick(slots, 0.2, 1f, sink));      // moved → changed
        Assert.False(poser.Tick(slots, 0.2, 1f, sink));     // same time → nothing moved
        poser.Tick(slots, 2.0, 1f, sink);                   // past T1 → settled
        Assert.False(poser.HasActive);
        Assert.False(poser.Tick(slots, 3.0, 1f, sink));     // settled and unchanged
        poser.Adopt(Coverage(0.0, 0.0, 5000.0, 400.0, 5000.0, ReadOnlySpan<ScrollEffectRow>.Empty));
        Assert.True(poser.Tick(slots, 3.0, 1f, sink));      // re-adopt forces a change
        slots.Release(VpA);
        Assert.False(poser.Tick(slots, 4.0, 1f, sink));     // no plan → not posed
        Assert.False(poser.HasActive);
    }

    // ── WheelClassifier ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(120, WheelDeviceEvidence.Unknown, ScrollSource.MouseWheel)]
    [InlineData(-240, WheelDeviceEvidence.Mouse, ScrollSource.MouseWheel)]
    [InlineData(37, WheelDeviceEvidence.Unknown, ScrollSource.MouseWheelHiRes)]
    [InlineData(-13, WheelDeviceEvidence.Mouse, ScrollSource.MouseWheelHiRes)]
    [InlineData(120, WheelDeviceEvidence.PrecisionTouchpad, ScrollSource.Touchpad)]
    [InlineData(9, WheelDeviceEvidence.PrecisionTouchpad, ScrollSource.Touchpad)]
    public void WheelClassifier_FirstPacketTable(int raw, WheelDeviceEvidence evidence, ScrollSource expected)
    {
        var c = new WheelClassifier();
        Assert.Equal(expected, c.Classify(raw, evidence, double.PositiveInfinity));
    }

    [Fact]
    public void WheelClassifier_LatchesHiResForTheGesture_AndReleasesAfterGap()
    {
        var c = new WheelClassifier();
        Assert.Equal(ScrollSource.MouseWheel, c.Classify(120, WheelDeviceEvidence.Unknown, double.PositiveInfinity));
        Assert.Equal(ScrollSource.MouseWheelHiRes, c.Classify(30, WheelDeviceEvidence.Unknown, 8));
        Assert.Equal(ScrollSource.MouseWheelHiRes, c.Classify(120, WheelDeviceEvidence.Unknown, 8));   // exact packet mid-gesture stays hi-res
        Assert.Equal(ScrollSource.MouseWheelHiRes, c.Classify(120, WheelDeviceEvidence.Unknown, WheelClassifier.GestureGapMs));
        Assert.Equal(ScrollSource.MouseWheel, c.Classify(120, WheelDeviceEvidence.Unknown, WheelClassifier.GestureGapMs + 1));
    }

    /// <summary>The Ctrl+wheel zoom sign contract (<c>InputHooks.ZoomWheel</c>: &gt;0 = the wheel rotated AWAY from the
    /// user = zoom in). A detented wheel rotated away is +120 raw → one whole notch → a notch event toward the content
    /// START (Dy &lt; 0, the scroll convention) → +1 zoom notch. The dispatcher used to hand the hook the raw Dy, so a
    /// wheel rotated away zoomed OUT.</summary>
    [Fact]
    public void ZoomNotches_WheelRotatedAway_ZoomsIn_TowardTheUser_ZoomsOut_AndATiltNeverZooms()
    {
        int accum = 0;
        int away = WheelClassifier.Carryover(ref accum, +120);
        var awayEvent = WheelClassifier.DetentNotch(away, 0, 1f, 0, default, 0, KeyModifiers.Ctrl);
        Assert.True(awayEvent.Dy < 0f, "a notch rotated away scrolls toward the content start");
        Assert.Equal(1f, WheelClassifier.ZoomNotches(in awayEvent));

        int toward = WheelClassifier.Carryover(ref accum, -120);
        var towardEvent = WheelClassifier.DetentNotch(toward, 0, 1f, 0, default, 0, KeyModifiers.Ctrl);
        Assert.Equal(-1f, WheelClassifier.ZoomNotches(in towardEvent));

        var tilt = WheelClassifier.DetentNotch(0, +1, 1f, 0, default, 0, KeyModifiers.Ctrl);
        Assert.Equal(1f, tilt.Dx);
        Assert.Equal(0f, WheelClassifier.ZoomNotches(in tilt));
    }

    [Fact]
    public void WheelClassifier_TouchpadEvidenceWins_UnlessAMouseWasPositivelySeen()
    {
        var c = new WheelClassifier();
        Assert.Equal(ScrollSource.MouseWheelHiRes, c.Classify(30, WheelDeviceEvidence.Unknown, double.PositiveInfinity));
        Assert.Equal(ScrollSource.Touchpad, c.Classify(30, WheelDeviceEvidence.PrecisionTouchpad, 8));
        Assert.Equal(ScrollSource.Touchpad, c.Classify(120, WheelDeviceEvidence.Unknown, 8));

        var m = new WheelClassifier();
        Assert.Equal(ScrollSource.MouseWheel, m.Classify(120, WheelDeviceEvidence.Mouse, double.PositiveInfinity));
        Assert.Equal(ScrollSource.Touchpad, m.Classify(30, WheelDeviceEvidence.PrecisionTouchpad, 8));   // authoritative for this packet
        Assert.Equal(ScrollSource.MouseWheelHiRes, m.Classify(30, WheelDeviceEvidence.Unknown, 8));      // but a seen mouse pulls the latch back
    }

    [Fact]
    public void WheelClassifier_Carryover_EmitsWholeNotchesAndKeepsRemainder()
    {
        int accum = 0;
        Assert.Equal(1, WheelClassifier.Carryover(ref accum, 120));
        Assert.Equal(0, WheelClassifier.Carryover(ref accum, 60));
        Assert.Equal(1, WheelClassifier.Carryover(ref accum, 60));
        Assert.Equal(-1, WheelClassifier.Carryover(ref accum, -150));
        Assert.Equal(-30, accum);
        Assert.True(WheelClassifier.IsPinchSynthesis(ScrollSource.Touchpad, KeyModifiers.Ctrl));
        Assert.False(WheelClassifier.IsPinchSynthesis(ScrollSource.MouseWheel, KeyModifiers.Ctrl));
    }

    // ── ScrollRouter ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Page(1) ⊃ Shelf(2). Movability is a mutable table so a test can drive "the shelf hit its edge".</summary>
    private sealed class Tree : IScrollerQuery
    {
        public const int Page = 1, Shelf = 2;
        public readonly Dictionary<(int, bool, int), bool> Movable = new();
        public int ParentScroller(int vp) => vp == Shelf ? Page : -1;
        public bool CanMove(int vp, bool horizontal, int sign, double tNow) => Movable.TryGetValue((vp, horizontal, sign), out bool m) && m;
        public void Set(int vp, bool horizontal, int sign, bool can) => Movable[(vp, horizontal, sign)] = can;
        public readonly Dictionary<int, uint> Ids = new();
        public uint Identity(int vp) => Ids.TryGetValue(vp, out uint id) ? id : 1u;
    }

    private static Tree PageWithShelf(bool shelfCanMoveDown)
    {
        var t = new Tree();
        t.Set(Tree.Page, false, +1, true);
        t.Set(Tree.Page, false, -1, true);
        t.Set(Tree.Shelf, false, +1, shelfCanMoveDown);
        t.Set(Tree.Shelf, false, -1, true);
        return t;
    }

    [Fact]
    public void Router_ShelfReachesEdgeMidSpin_DoesNotHandOff()
    {
        var tree = PageWithShelf(shelfCanMoveDown: true);
        var r = new ScrollRouter(tree, wheelLatchSilenceS: 0.3);
        Assert.Equal(Tree.Shelf, r.Route(Tree.Shelf, false, +1, 0.00, ScrollSource.MouseWheel));
        Assert.Equal(Tree.Shelf, r.Route(Tree.Shelf, false, +1, 0.05, ScrollSource.MouseWheel));
        tree.Set(Tree.Shelf, false, +1, false);   // the shelf hit its end mid-spin
        Assert.Equal(Tree.Shelf, r.Route(Tree.Shelf, false, +1, 0.10, ScrollSource.MouseWheel));   // absorbed, no hand-off
        Assert.Equal(Tree.Shelf, r.Route(Tree.Shelf, false, +1, 0.30, ScrollSource.MouseWheel));
        // After latch silence the gesture restarts AT the edge → now the page takes it.
        Assert.Equal(Tree.Page, r.Route(Tree.Shelf, false, +1, 0.70, ScrollSource.MouseWheel));
    }

    [Fact]
    public void Router_AWheelLatchWhoseScrollerIsGone_LatchesOnTheHitInstead()
    {
        // A spin on the shelf, then a navigation parks (or frees) it mid-spin and the next notch lands on another
        // scroller under the pointer: the latch must not keep feeding a scroller that can never move again.
        var tree = PageWithShelf(shelfCanMoveDown: true);
        var r = new ScrollRouter(tree, wheelLatchSilenceS: 0.3);
        Assert.Equal(Tree.Shelf, r.Route(Tree.Shelf, false, +1, 0.00, ScrollSource.MouseWheel));
        tree.Ids[Tree.Shelf] = 0;   // gone
        Assert.Equal(Tree.Page, r.Route(Tree.Page, false, +1, 0.05, ScrollSource.MouseWheel));
        Assert.Equal(Tree.Page, r.LatchedScroller);
        // The same index reused by a NEW scroller is not the latched one either.
        tree.Ids[Tree.Shelf] = 7;
        Assert.Equal(Tree.Shelf, r.Route(Tree.Shelf, false, +1, 0.50, ScrollSource.MouseWheel));
        tree.Ids[Tree.Shelf] = 8;
        Assert.Equal(Tree.Page, r.Route(Tree.Page, false, +1, 0.55, ScrollSource.MouseWheel));
    }

    [Fact]
    public void Router_AContactWhoseScrollerIsGone_IsDroppedUntilTheNextBegin()
    {
        var tree = PageWithShelf(shelfCanMoveDown: true);
        var r = new ScrollRouter(tree, wheelLatchSilenceS: 0.3);
        Assert.Equal(Tree.Shelf, r.Decide(Tree.Shelf, false, +1, 0.00, ScrollSource.Touchpad, ScrollGesture.Begin, KeyModifiers.None).Vp);
        tree.Ids[Tree.Shelf] = 0;
        Assert.True(r.Decide(Tree.Page, false, +1, 0.02, ScrollSource.Touchpad, ScrollGesture.Sample, KeyModifiers.None).IsNone);
        Assert.Equal(Tree.Page, r.Decide(Tree.Page, false, +1, 0.10, ScrollSource.Touchpad, ScrollGesture.Begin, KeyModifiers.None).Vp);
    }

    [Fact]
    public void Router_GestureStartedAtEdge_HandsOffToParent_ButReverseGoesBackToShelf()
    {
        var tree = PageWithShelf(shelfCanMoveDown: false);
        var r = new ScrollRouter(tree, wheelLatchSilenceS: 0.3);
        Assert.Equal(Tree.Page, r.Route(Tree.Shelf, false, +1, 0.0, ScrollSource.MouseWheel));
        Assert.Equal(Tree.Page, r.Route(Tree.Shelf, false, +1, 0.1, ScrollSource.MouseWheel));
        // Reversing direction within the latch: the shelf can move up, so the latched child takes it again.
        Assert.Equal(Tree.Shelf, r.Route(Tree.Shelf, false, -1, 0.2, ScrollSource.MouseWheel));
    }

    [Fact]
    public void Router_ShiftWheel_RoutesHorizontal()
    {
        var tree = new Tree();
        tree.Set(Tree.Shelf, true, +1, true);
        tree.Set(Tree.Shelf, false, +1, false);
        tree.Set(Tree.Page, false, +1, true);
        var r = new ScrollRouter(tree, 0.3);
        RouteDecision d = r.Decide(Tree.Shelf, horizontal: false, +1, 0.0, ScrollSource.MouseWheel, ScrollGesture.Notch, KeyModifiers.Shift);
        Assert.True(d.Horizontal);
        Assert.Equal(Tree.Shelf, d.Vp);
        // Without Shift the vertical notch over the pinned shelf starts at the edge → the page.
        var r2 = new ScrollRouter(tree, 0.3);
        RouteDecision d2 = r2.Decide(Tree.Shelf, horizontal: false, +1, 0.0, ScrollSource.MouseWheel, ScrollGesture.Notch, KeyModifiers.None);
        Assert.False(d2.Horizontal);
        Assert.Equal(Tree.Page, d2.Vp);
    }

    [Fact]
    public void Router_ContactLatch_HoldsBeginToEnd_ThenReleases()
    {
        var tree = PageWithShelf(shelfCanMoveDown: true);
        var r = new ScrollRouter(tree, 0.3);
        Assert.Equal(Tree.Shelf, r.Decide(Tree.Shelf, false, +1, 0.0, ScrollSource.Touchpad, ScrollGesture.Begin, KeyModifiers.None).Vp);
        tree.Set(Tree.Shelf, false, +1, false);
        // Long after any wheel silence window, the contact latch still holds and still does not hand off.
        Assert.Equal(Tree.Shelf, r.Decide(Tree.Shelf, false, +1, 5.0, ScrollSource.Touchpad, ScrollGesture.Sample, KeyModifiers.None).Vp);
        Assert.Equal(Tree.Shelf, r.Decide(Tree.Shelf, false, +1, 5.1, ScrollSource.Touchpad, ScrollGesture.End, KeyModifiers.None).Vp);
        Assert.Equal(-1, r.LatchedScroller);
        // A new gesture at the edge hands off.
        Assert.Equal(Tree.Page, r.Decide(Tree.Shelf, false, +1, 5.2, ScrollSource.Touchpad, ScrollGesture.Begin, KeyModifiers.None).Vp);
    }

    [Fact]
    public void Router_Keyboard_NotLatched_WalksUpOnlyWhenPinned()
    {
        var tree = PageWithShelf(shelfCanMoveDown: false);
        var r = new ScrollRouter(tree, 0.3);
        Assert.Equal(Tree.Page, r.Route(Tree.Shelf, false, +1, 0.0, ScrollSource.Keyboard));
        Assert.Equal(Tree.Shelf, r.Route(Tree.Shelf, false, -1, 0.0, ScrollSource.Keyboard));
        Assert.Equal(-1, r.LatchedScroller);
        Assert.True(ScrollRouter.TryMapKey(Keys.Down, false, out KeyMove m) && m == KeyMove.LineDown);
        Assert.False(ScrollRouter.TryMapKey(Keys.Down, true, out _));
        Assert.True(ScrollRouter.TryMapKey(Keys.Right, true, out m) && m == KeyMove.LineDown);
        Assert.True(ScrollRouter.TryMapKey(Keys.PageUp, false, out m) && m == KeyMove.PageUp);
        Assert.True(ScrollRouter.TryMapKey(Keys.End, false, out m) && m == KeyMove.End);
        Assert.Equal(-1, ScrollRouter.SignOf(KeyMove.Home));
    }

    // ── ScrollHandle ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_ScrollTo_AuthorsAGlide_FromDisplayedPosition_ClampedToExtent()
    {
        var slots = new PlanSlots();
        double now = 10.0;
        var h = new ScrollHandle(slots, VpA, () => now);
        h.SetExtent(extent: 10_000.0, viewport: 600.0);
        h.ScrollTo(300.0, ScrollMove.Immediate);
        Assert.Equal(300.0, h.OffsetNow);

        now = 11.0;
        h.ScrollTo(2_000.0);
        ScrollPlan p = h.Plan;
        Assert.Equal(MotionKind.Programmatic, p.Kind);
        Assert.Equal(SegKind.Glide, p.S0.Kind);
        Assert.Equal(300.0, p.S0.P0);
        Assert.Equal(2_000.0, p.Dest);
        Assert.Equal(300.0, p.Eval(11.0, out _, out _), 9);
        Assert.True(p.Eval(11.1, out double v, out _) > 300.0 && v > 0.0);
        Assert.Equal(2_000.0, p.Eval(30.0, out _, out _), 3);

        h.ScrollTo(50_000.0);
        Assert.Equal(10_000.0 - 600.0, h.Plan.Dest);   // clamped to Max
        h.ScrollBy(-100.0);
        Assert.Equal(9_300.0, h.Plan.Dest);
    }

    [Fact]
    public void Handle_Feedback_DrivesOffsetAndMotionSignals()
    {
        var slots = new PlanSlots();
        var h = new ScrollHandle(slots, VpA, () => 0.0);
        h.ApplyFeedback(new ScrollPoseFeedback(VpA.Node, VpA.Gen, 123.5, -800.0, false, false, MotionKind.Fling, 0.0));
        Assert.Equal(123.5, h.Offset.Peek());
        ScrollMotionState m = h.Motion.Peek();
        Assert.Equal(MotionKind.Fling, m.Kind);
        Assert.Equal(800f, m.SpeedDipPerS);
        Assert.True(m.UserDriven);

        h.ApplyFeedback(new ScrollPoseFeedback(VpA.Node, VpA.Gen, 200.0, 0.0, true, false, MotionKind.Programmatic, 1.0));
        Assert.Equal(ScrollMotionState.Idle, h.Motion.Peek());

        h.ApplyFeedback(new ScrollPoseFeedback(VpA.Node, VpA.Gen, 210.0, 40.0, false, false, MotionKind.Programmatic, 2.0));
        Assert.False(h.Motion.Peek().UserDriven);
    }

    [Fact]
    public void Handle_Follow_YieldsToALiveUserPlan()
    {
        var slots = new PlanSlots();
        double now = 0.0;
        var h = new ScrollHandle(slots, VpA, () => now);
        h.SetExtent(5_000.0, 400.0);
        h.Wheel(0.0, 1.0);
        Assert.Equal(MotionKind.Wheel, h.Plan.Kind);
        now = 0.1;
        h.ScrollTo(1_000.0, ScrollMove.Follow);          // wheel cubic still live → ignored
        Assert.Equal(MotionKind.Wheel, h.Plan.Kind);
        now = 1.0;                                        // cubic finished (settled)
        h.ScrollTo(1_000.0, ScrollMove.Follow);
        Assert.Equal(MotionKind.Programmatic, h.Plan.Kind);
        Assert.Equal(1_000.0, h.Plan.Dest);
    }

    [Fact]
    public void Handle_BringIntoView_MinimalMove()
    {
        var slots = new PlanSlots();
        var h = new ScrollHandle(slots, VpA, () => 0.0);
        h.SetExtent(10_000.0, 500.0);
        h.ScrollTo(1_000.0, ScrollMove.Immediate);
        h.BringIntoView(1_100.0, 56.0);                   // already visible → no-op
        Assert.Equal(1_000.0, h.Plan.Dest);
        h.BringIntoView(1_600.0, 56.0);                   // below → bottom-align
        Assert.Equal(1_156.0, h.Plan.Dest);
        h.BringIntoView(200.0, 56.0, move: ScrollMove.Immediate);   // above → top-align
        Assert.Equal(200.0, h.Plan.Dest);
        h.BringIntoView(3_000.0, 100.0, align: 0.5f);     // centered
        Assert.Equal(3_000.0 - 0.5 * (500.0 - 100.0), h.Plan.Dest);
    }

    [Fact]
    public void Handle_Stop_HoldsWhereTheContentWasShown_NeverStepsBack()
    {
        // A fling is live; the last pose (at a present time AHEAD of the input's own time) showed 600. A finger landing
        // now must freeze the content at 600 — the plan evaluated at the earlier input time would step it back.
        double now = 1.0;
        var slots = new PlanSlots();
        var h = new ScrollHandle(slots, VpA, () => now);
        h.SetExtent(10_000.0, 500.0);
        var decay = new MotionSeg(SegKind.Decay, 0.0, double.PositiveInfinity, 0.0, 0.0, 1_000.0, 2.0);
        slots.Write(VpA, new ScrollPlan(decay, default, default, default, 1, VpA.Node, VpA.Gen, 2, 0.0, 9_500.0, 500.0, 0.55, 0.0,
            OverpanPolicy.None, MotionKind.Fling, default));
        double shownAhead = h.EvalAt(now + 0.033, out double v, out _);
        h.ApplyShown(shownAhead, v, MotionKind.Fling, settled: false);

        h.Stop(now);
        Assert.Equal(MotionKind.Programmatic, h.Plan.Kind);
        Assert.Equal(shownAhead, h.EvalAt(now, out _, out _), 9);
        Assert.Equal(shownAhead, h.EvalAt(now + 1.0, out _, out _), 9);
    }

    [Fact]
    public void Handle_ContactBeginHere_GrabsTheShownPosition()
    {
        double now = 2.0;
        var slots = new PlanSlots();
        var h = new ScrollHandle(slots, VpA, () => now);
        h.SetExtent(10_000.0, 500.0);
        h.ScrollTo(3_000.0);                                   // a live glide
        double shownAhead = h.EvalAt(now + 0.05, out double v, out _);
        h.ApplyShown(shownAhead, v, MotionKind.Programmatic, settled: false);

        h.ContactBeginHere(now, ContactClock.Device);
        Assert.Equal(MotionKind.Drag, h.Plan.Kind);
        Assert.Equal(shownAhead, h.EvalAt(now + 0.1, out _, out _), 9);   // one sample: holds exactly there
        h.ContactDelta(now + 0.016, 10.0);
        Assert.Equal(shownAhead + 10.0, h.Plan.Ring.LastPos, 9);
    }

    /// <summary>A composition-timed (DirectManipulation) contact: its stamps are the present times DM composed each sample
    /// for — ahead of the plan clock by design — so they are taken as they are (never resynced back to now), the plan shows
    /// the newest sample without predicting past it, and a lift stamped behind the newest sample (a status edge the
    /// producer could only stamp "now") never rewinds what was shown.</summary>
    [Fact]
    public void Handle_PresentClockContact_KeepsItsPresentStamps_AndTheLiftNeverRewinds()
    {
        double now = 2.0;
        var slots = new PlanSlots();
        var h = new ScrollHandle(slots, VpA, () => now);
        h.SetExtent(100_000.0, 500.0);
        h.ScrollTo(1_000.0, ScrollMove.Immediate);
        h.ApplyShown(1_000.0, 0.0, MotionKind.Programmatic, settled: true);

        h.ContactBeginHere(now + 0.030, ContactClock.Present);
        for (int k = 1; k <= 6; k++) h.ContactDelta(now + 0.030 + k / 60.0, 25.0);   // 1500 DIP/s, one sample per frame
        var ring = h.Plan.Ring;
        Assert.Equal(ContactClock.Present, h.Plan.Clock);
        Assert.Equal(now + 0.030 + 6 / 60.0, ring.LastT, 12);                          // not resynced to now
        Assert.Equal(1_150.0, h.EvalAt(now + 1.0, out _, out _), 9);                   // held: no look-ahead

        double shownNewest = h.EvalAt(ring.LastT, out _, out _);
        h.ContactEnd(now);                                                              // a stale "now" stamp
        Assert.Equal(MotionKind.Fling, h.Plan.Kind);
        Assert.Equal(shownNewest, h.EvalAt(ring.LastT, out _, out _), 9);               // authored from the newest sample
        Assert.True(h.EvalAt(ring.LastT + 0.1, out _, out _) > shownNewest);
    }

    /// <summary>The touchpad wheel fallback detects the lift only after 50-120 ms of packet silence, but stamps the End at
    /// the LAST packet (the true lift, so the release velocity is the finger's own). The fling is authored from the
    /// contact ring at that time — and the position shown at the moment the lift is DETECTED must be the plan's position
    /// at that instant: the defect evaluated a coast that started at the last packet, so the content appeared already
    /// 50-120 ms further along the coast — a forward jump at lift.</summary>
    [Fact]
    public void Handle_LiftDetectedAfterSilence_FlingsWithoutAJump()
    {
        double now = 5.0;
        var slots = new PlanSlots();
        var h = new ScrollHandle(slots, VpA, () => now);
        h.SetExtent(100_000.0, 500.0);
        h.ContactBegin(now, 1_000.0);
        double pos = 1_000.0;
        for (int i = 0; i < 12; i++)                        // 1500 DIP/s at a 125 Hz packet rate
        {
            now += 0.008;
            pos += 12.0;
            h.ContactSample(now, pos);
        }
        double tLast = now;
        now += 0.100;                                      // 100 ms of silence: the lift is detected now
        double shownAtDetection = h.EvalAt(now, out _, out _);
        h.ContactEnd(tLast, detectedAt: now);              // stamped at the last packet, detected after the silence

        Assert.Equal(MotionKind.Fling, h.Plan.Kind);
        Assert.Equal(shownAtDetection, h.EvalAt(now, out double v, out _), 6);   // no jump at the lift
        Assert.True(v > 0.0, "the lift still flings (the release velocity is the finger's, not a stopped finger's)");
        Assert.True(h.EvalAt(now + 0.1, out _, out _) > shownAtDetection);      // and coasts on from the shown position
    }

    [Fact]
    public void Handle_ContactSamplesAheadOfThePlanClock_ResyncKeepsTheDeviceSpacing()
    {
        // A coalesced burst: the device stamped the newest sample 0.5 s after the down, but it is dispatched only one
        // frame after it. The ring must end at NOW (no interpolating behind the finger) with the device's 0.5 s spacing
        // (so the release velocity is the finger's own 60 DIP/s, not 30 DIP over one frame).
        double now = 10.0;
        var slots = new PlanSlots();
        var h = new ScrollHandle(slots, VpA, () => now);
        h.SetExtent(10_000.0, 500.0);
        h.ContactBegin(now, 100.0);
        now += 1.0 / 60.0;
        h.ContactSample(10.5, 130.0);

        var ring = h.Plan.Ring;
        Assert.Equal(2, ring.Count);
        Assert.Equal(now, ring.SampleAt(1).T, 12);
        Assert.Equal(0.5, ring.SampleAt(1).T - ring.SampleAt(0).T, 12);
        Assert.Equal(130.0, h.EvalAt(now, out _, out _), 9);
        Assert.Equal(60.0, ring.Velocity(1.0, FeelProfiles.Standard.VelocityMinSpanS), 6);
    }

    [Fact]
    public void RestoreLatch_HoldsUntilExtentCanHoldTheTarget()
    {
        var l = new RestoreLatch();
        l.Arm(4_000.0);
        Assert.False(l.TryResolve(extent: 1_000.0, viewport: 500.0, out _));
        Assert.True(l.Pending);
        Assert.False(l.TryResolve(4_400.0, 500.0, out _));    // max = 3900 < 4000
        Assert.True(l.TryResolve(4_500.0, 500.0, out double off));
        Assert.Equal(4_000.0, off);
        Assert.False(l.Pending);
        Assert.False(l.TryResolve(9_000.0, 500.0, out _));    // consumed

        // The handle latches the same way, and while the content is still too short it CHASES the end (best effort, the
        // way a list that is still filling reveals the rows nearest the saved position) until the target fits exactly.
        var slots = new PlanSlots();
        var h = new ScrollHandle(slots, VpA, () => 0.0);
        h.Restore(4_000.0);
        h.SetExtent(1_000.0, 500.0);
        Assert.True(h.RestorePending);
        Assert.Equal(500.0, h.OffsetNow);                        // held at today's max (1000 − 500)
        h.SetExtent(3_000.0, 500.0);
        Assert.True(h.RestorePending);
        Assert.Equal(2_500.0, h.OffsetNow);                      // the grown max, still short of the target
        h.SetExtent(4_500.0, 500.0);
        Assert.False(h.RestorePending);
        Assert.Equal(4_000.0, h.OffsetNow);

        // Any user input drops the latch: a later growth never yanks the list back to a stale target.
        var h2 = new ScrollHandle(new PlanSlots(), VpB, () => 0.0);
        h2.Restore(4_000.0);
        h2.SetExtent(1_000.0, 500.0);
        h2.Wheel(0.0, -1.0);
        Assert.False(h2.RestorePending);
    }

    // ── Virtualizer ───────────────────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> VirtualizerCases()
    {
        const double total = 100_000 * 56.0;
        foreach (double p in new[] { 0.0, total / 2.0, total - 900.0 })
            foreach (double v in new[] { 0.0, 8000.0, -8000.0 })
                yield return new object[] { p, v };
    }

    [Theory]
    [MemberData(nameof(VirtualizerCases))]
    public void Virtualizer_Window_CoversViewportPlusVelocityOverscan(double p, double v)
    {
        var ext = new FixedExtent(100_000, 56.0);
        const double viewport = 900.0;
        MotionFeel feel = FeelProfiles.Standard;
        RealizeWindow w = Virtualizer.Plan(ext, p, v, viewport, in feel, 0);

        Assert.False(w.IsEmpty);
        Assert.Equal(ext.OffsetOf(w.First), w.CoverStart);
        Assert.Equal(ext.OffsetOf(w.Last + 1), w.CoverEnd);

        // Coverage ⊇ the visible viewport (clamped to the content).
        double visStart = Math.Max(0.0, p), visEnd = Math.Min(ext.Total, p + viewport);
        Assert.True(w.CoverStart <= visStart);
        Assert.True(w.CoverEnd >= visEnd);

        // Leading-side overscan is velocity sized: clamp(|v|·LookaheadS, min, max); trailing side is the floor.
        double lead = Math.Clamp(Math.Abs(v) * feel.LookaheadS, feel.OverscanMinPx, feel.OverscanMaxPx);
        double ahead = v < 0 ? feel.OverscanMinPx : lead, behind = v < 0 ? lead : feel.OverscanMinPx;
        Assert.True(w.CoverStart <= Math.Max(0.0, p - behind));
        Assert.True(w.CoverEnd >= Math.Min(ext.Total, p + viewport + ahead));
        if (v > 0 && p + viewport + lead < ext.Total) Assert.True(w.CoverEnd - (p + viewport) >= lead);

        // Anchor = first fully visible row.
        Assert.True(ext.OffsetOf(w.AnchorIndex) >= p || w.AnchorIndex == ext.Count - 1);
        Assert.True(w.AnchorIndex == 0 || ext.OffsetOf(w.AnchorIndex - 1) < p);
        // No row cap: the window is sized in pixels, never truncated to a fixed count.
        double wantStart = Math.Max(0.0, p - behind), wantEnd = Math.Min(ext.Total, p + viewport + ahead);
        Assert.True(w.CoverEnd - w.CoverStart >= wantEnd - wantStart);
    }

    [Fact]
    public void Virtualizer_Anchor_IsTheFirstFullyVisibleRow_VisibleBandIncludesThePartialOne()
    {
        var ext = new FixedExtent(1_000, 50.0);
        var feel = FeelProfiles.Standard;
        RealizeWindow partial = Virtualizer.Plan(ext, 1_010.0, 0.0, 400.0, in feel, 0);   // row 20 is 10 DIP scrolled off
        Assert.Equal(20, partial.VisibleFirst);
        Assert.Equal(21, partial.AnchorIndex);
        RealizeWindow aligned = Virtualizer.Plan(ext, 1_000.0, 0.0, 400.0, in feel, 0);   // row 20 exactly at the top
        Assert.Equal(20, aligned.VisibleFirst);
        Assert.Equal(20, aligned.AnchorIndex);
    }

    [Fact]
    public void Virtualizer_ArrangeOrigin_KeptAcrossWindowShifts_RecentresBeforePrecisionIsAtRisk()
    {
        var ext = new FixedExtent(100_000, 56.0);
        // A window near the top re-centres on its middle row from "no origin yet".
        int o = Virtualizer.ArrangeOriginIndex(ext, -1, 0, 20);
        Assert.Equal(ext.IndexAt(0.5 * ext.OffsetOf(20)), o);

        // Scrolling forward row by row keeps the origin (retained rows keep their boxes) while the window stays within
        // MaxLocalExtent of it ...
        int kept = 0, first = 0;
        for (; first < 100_000 - 40; first++)
        {
            int next = Virtualizer.ArrangeOriginIndex(ext, o, first, first + 30);
            if (next != o) break;
            kept++;
        }
        Assert.True(kept > (int)(Virtualizer.MaxLocalExtent / 56.0) - 40);
        // ... and re-centres as soon as the window's far edge would pass it, so no realized row is ever farther than
        // MaxLocalExtent from the origin (float positions stay exact to 1/512 DIP).
        int re = Virtualizer.ArrangeOriginIndex(ext, o, first, first + 30);
        Assert.True(ext.OffsetOf(first + 30) - ext.OffsetOf(o) > Virtualizer.MaxLocalExtent);
        Assert.True(ext.OffsetOf(first) >= ext.OffsetOf(re) - Virtualizer.MaxLocalExtent);
        Assert.True(ext.OffsetOf(first + 30) <= ext.OffsetOf(re) + Virtualizer.MaxLocalExtent);

        // Deep in a 100k list: same rule, same bound.
        int deep = Virtualizer.ArrangeOriginIndex(ext, re, 99_900, 99_940);
        Assert.True(Math.Abs(ext.OffsetOf(99_940) - ext.OffsetOf(deep)) <= Virtualizer.MaxLocalExtent);
        Assert.True(Math.Abs(ext.OffsetOf(99_900) - ext.OffsetOf(deep)) <= Virtualizer.MaxLocalExtent);
    }

    [Fact]
    public void Virtualizer_ApplyMeasured_AboveAnchor_KeepsAnchorRowScreenPositionUnchanged()
    {
        var ext = new MeasuredExtent(1_000, 56.0);
        var slots = new PlanSlots();
        const double t = 5.0;
        // A live glide mid-list.
        var feel = FeelProfiles.Standard;
        ScrollPlan plan = PlanAuthor.Glide(ScrollPlan.Idle(VpA.Node, 5_000.0, 0.0, ext.Total), t, 6_000.0, in feel);
        slots.Allocate(VpA, plan);
        double p = plan.Eval(t + 0.05, out _, out _);
        RealizeWindow w = Virtualizer.Plan(ext, p, 300.0, 700.0, in feel, 0);
        int anchor = w.AnchorIndex;
        double anchorScreenBefore = ext.OffsetOf(anchor) - p;

        // Row 3 (far above the anchor) measures 40 px taller than its estimate.
        double delta = Virtualizer.ApplyMeasured(ext, 3, 96.0, anchor, slots, VpA);
        Assert.Equal(40.0, delta);
        slots.TryRead(VpA, out ScrollPlan shifted);
        double pAfter = shifted.Eval(t + 0.05, out _, out _);
        Assert.Equal(anchorScreenBefore, ext.OffsetOf(anchor) - pAfter, 9);
        Assert.Equal(p + 40.0, pAfter, 9);

        // At or below the anchor: no delta, no shift.
        Assert.Equal(0.0, Virtualizer.ApplyMeasured(ext, anchor + 2, 120.0, anchor, slots, VpA));
        slots.TryRead(VpA, out ScrollPlan same);
        Assert.Equal(pAfter, same.Eval(t + 0.05, out _, out _));
    }

    [Fact]
    public void CoverageTable_CopyFrom_IsAValueCopy()
    {
        var a = new ScrollCoverageTable();
        var effects = new[] { new ScrollEffectRow(5, ScrollEffect.Sticky(10f), new EffectGeometry(1, 2, 3, 4, 5, 6f, 7f)) };
        a.AddRow(new ScrollCoverageRow(1, 1, 2, 0, 0, 100, 50, 100, false, 0, 0, 0.0), effects);
        var b = new ScrollCoverageTable();
        b.CopyFrom(a);
        a.Clear();
        Assert.Equal(1, b.RowCount);
        Assert.Equal(1, b.EffectCount);
        Assert.Equal(0, b.RowAt(0).EffectStart);
        Assert.Equal(1, b.RowAt(0).EffectCount);
        Assert.Equal(5, b.EffectAt(0).NodeIndex);
        Assert.Equal(0, b.IndexOf(new ScrollViewportId(1, 1)));
        Assert.Equal(-1, b.IndexOf(new ScrollViewportId(1, 2)));
    }
    [Fact]
    public void Coverage_APersistentPrefixContiguousWithTheWindow_CoversFromTheContentStart()
    {
        // hero 360 + chrome 48 (the prefix), then 40-DIP rows; the recyclable window starts right after the prefix.
        var ext = new MeasuredExtent(100, 40.0);
        ext.SetMeasured(0, 360.0, 0);
        ext.SetMeasured(1, 48.0, 0);
        ScrollContentPose.CoverageOf(ext, prefix: 2, firstRealized: 2, lastRealized: 20, out double start, out double end);
        Assert.Equal(0.0, start);                       // the retained prefix IS realized — offset 0 is covered
        Assert.Equal(ext.OffsetOf(20), end);

        // Scrolled past the prefix: the band the viewport can sit in is the window itself.
        ScrollContentPose.CoverageOf(ext, prefix: 2, firstRealized: 30, lastRealized: 50, out start, out end);
        Assert.Equal(ext.OffsetOf(30), start);
        Assert.Equal(ext.OffsetOf(50), end);

        // No prefix: a window from item 0 covers from the content start; one reaching the last item covers to Total.
        ScrollContentPose.CoverageOf(ext, prefix: 0, firstRealized: 0, lastRealized: 100, out start, out end);
        Assert.Equal(0.0, start);
        Assert.Equal(ext.Total, end);
    }

    [Fact]
    public void Poser_APrefixedListAtRest_IsNotClampedByItsOwnPrefix()
    {
        var ext = new MeasuredExtent(100, 40.0);
        ext.SetMeasured(0, 360.0, 0);
        ext.SetMeasured(1, 48.0, 0);
        ScrollContentPose.CoverageOf(ext, prefix: 2, firstRealized: 2, lastRealized: 20, out double start, out double end);
        var slots = new PlanSlots();
        slots.Allocate(VpA, ScrollPlan.Idle(VpA.Node, 0.0, 0.0, ext.Total - 400.0, 400.0));
        var poser = new ScrollPoser();
        poser.Adopt(Coverage(origin: ext.OffsetOf(2), start, end, viewport: 400.0, total: ext.Total, ReadOnlySpan<ScrollEffectRow>.Empty));
        poser.Tick(slots, 1.0, 1f, new RecordingSink());
        Assert.True(poser.TryGetFeedback(VpA, out var fb));
        Assert.Equal(0.0, fb.Shown);
        Assert.False(fb.Clamped);
    }
}
