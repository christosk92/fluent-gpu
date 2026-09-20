using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// P4 fix (2026-09-19, "the drawer-under-context-flyout overflow"): <see cref="FlexLayout"/>'s cross-pass measure
/// ring (<c>MeasureMemo.Ring0/Ring1</c>) used to validate a hit with <c>SceneStore.IsLayoutClean(node)</c> alone —
/// a FRAME-scoped signal ("nothing under this node was marked layout-dirty THIS frame"). That is not the same claim
/// as "this ring slot is still correct": a ring slot is written once and then re-read on any LATER frame that asks
/// the same (node, availWidth) again, and <c>IsLayoutClean</c> says nothing about frames between the write and that
/// later read. A subtree that grows (an expanded row's drawer) and then settles (its LayoutDirty flag is cleared once
/// that frame's worklist is processed) leaves the OTHER ring slot — the one the reflow tick didn't happen to revisit
/// — holding the PRE-growth size, primed to be served as a false hit the next time an unrelated full-root layout
/// (a "…" context flyout opening) asks for that exact width again.
/// <para>The fix: <see cref="SceneStore.SubtreeVersion"/>, a per-node version that increments on every LayoutDirty
/// mark (edge or not) for the node itself AND every ancestor (<c>SceneStore.Aux.cs</c>'s <c>MarkSubtreeLayoutDirtyChain</c>) and
/// never resets. <c>StoreRing</c> stamps it; <c>TryRingHit</c> requires it unchanged AND keeps the <c>IsLayoutClean</c>
/// gate — the two are complementary (same-frame dirtiness vs cross-frame staleness), see the parity tests below.</para>
/// <para>Driven headlessly against the real <see cref="FlexLayout"/> + <see cref="SceneStore"/> (no source-text
/// reads/greps — the regression is reproduced through the shipping Measure/Arrange path, not re-implemented).</para>
/// </summary>
public sealed class MeasureRingTests
{
    private const float RowH = 64f;
    private const float ExpandedH = 264f;

    private sealed class Harness
    {
        public SceneStore Scene = null!;
        public FlexLayout Layout = null!;
        public NodeHandle P, Spacer, N, C;
        private int _frame;

        /// <summary>Run a full pass treating <see cref="P"/> as the root, at a window width UNIQUE to this call
        /// (defeats P's OWN measure ring so it always re-descends — the width argument P receives is otherwise inert
        /// since P's Width/Height are both explicit) — isolates the ring behaviour under test to <see cref="N"/>.</summary>
        public void Run() => Layout.Run(P, new Size2(1000f + (++_frame), 1000f));
    }

    private static Harness Build()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var layout = new FlexLayout(scene, new HeadlessFontSystem(new StringTable()));

        // P: the flex row. Explicit, constant Width/Height so its OWN measured output never depends on the (always-
        // unique-per-call) window width fed to Run — only on Spacer's width, which the test controls directly.
        // AlignItems=Start (not the LayoutInput.Default Stretch) so N's CROSS size (height, in this row) comes from
        // N's own measured content, not a forced stretch to P's box — the thing this test needs to observe.
        var p = scene.CreateNode(1);
        scene.Layout(p).Direction = 0;   // row
        scene.Layout(p).Width = 1000f;
        scene.Layout(p).Height = 1000f;
        scene.Layout(p).AlignItems = FlexAlign.Start;

        // Spacer: a fixed-width sibling. Its width sets N's leftover (grow) share, i.e. the AVAILW argument N's
        // Measure receives — the ring key under test — entirely independent of P's own (defeated) ring.
        var spacer = scene.CreateNode(2);
        scene.AppendChild(p, spacer);
        scene.Layout(spacer).Width = 300f;
        scene.Layout(spacer).Height = 0f;

        // N: the "expanded row" stand-in. Auto Width/Height (content-driven) + FlexGrow=1 so it always takes exactly
        // P's leftover row-width (Spacer's width controls that leftover). Its own LayoutInput never changes across
        // this test — only its CHILD C's does — mirroring the real bug: LayoutSig(N) hashes N's own participation
        // fields, never C's, so it cannot by itself catch a content-only growth.
        var n = scene.CreateNode(3);
        scene.AppendChild(p, n);
        scene.Layout(n).Direction = 1;   // column
        scene.Layout(n).FlexGrow = 1f;

        // C: the drawer stand-in — a leaf whose explicit Height is what "opens"/"closes".
        var c = scene.CreateNode(4);
        scene.AppendChild(n, c);
        scene.Layout(c).Height = RowH;

        return new Harness { Scene = scene, Layout = layout, P = p, Spacer = spacer, N = n, C = c };
    }

    // THE REGRESSION. N is measured at avail=700 (Spacer=300), then at avail=800 (Spacer=200) — two distinct ring
    // slots. C then grows while Spacer stays at 200 (re-storing the avail=800 slot with the TALL size and leaving the
    // avail=700 slot holding the OLD short size). SceneStore.ClearLayoutDirty then simulates "this frame's dirty
    // worklist already got processed" — exactly what has already happened by the time an unrelated full-root layout
    // (this test's stand-in: Spacer flips back to 300, revisiting the stale avail=700 slot) runs. N's own LayoutInput
    // never changed and nothing is dirty this frame, so the OLD (IsLayoutClean-only) ring validation would hit the
    // stale slot and report N still 64 tall — silently re-collapsing the still-open drawer.
    [Fact]
    public void ARingSlotThatPredatesAGrowthIsNotServedOnceTheSubtreeHasChangedSinceItWasWritten()
    {
        var h = Build();

        h.Scene.Layout(h.Spacer).Width = 300f;   // N's avail = 700
        h.Scene.Mark(h.Spacer, NodeFlags.LayoutDirty);
        h.Run();
        Assert.Equal(RowH, h.Scene.Bounds(h.N).H, 3);

        h.Scene.Layout(h.Spacer).Width = 200f;   // N's avail = 800 — a SECOND ring slot
        h.Scene.Mark(h.Spacer, NodeFlags.LayoutDirty);
        h.Run();
        Assert.Equal(RowH, h.Scene.Bounds(h.N).H, 3);

        // The drawer opens. Re-measure happens at the CURRENT avail (800) only — the avail=700 slot is not touched,
        // exactly as "the reflow ticks refresh only the width(s) they visit".
        h.Scene.Layout(h.C).Height = ExpandedH;
        h.Scene.Mark(h.C, NodeFlags.LayoutDirty);
        h.Run();
        Assert.Equal(ExpandedH, h.Scene.Bounds(h.N).H, 3);   // sanity: the live path always gets this one right

        // "Later" (a different, unrelated part of the tree gets marked dirty and forces a full root layout — a "…"
        // context flyout opening): simulate the frame boundary that already cleared N/C's LayoutDirty…
        h.Scene.ClearLayoutDirty();
        // …then revisit the STALE avail=700 slot. Nothing about N itself is dirty; N's own LayoutSig is unchanged
        // (only C's Height changed, and LayoutSig never reads a child's fields) — the ring's ONLY remaining line of
        // defense is SubtreeVersion. (Spacer IS re-marked and re-measures via its own unrelated LayoutSig change —
        // that's orthogonal to what's under test here, which is purely N's ring.)
        h.Scene.Layout(h.Spacer).Width = 300f;
        h.Scene.Mark(h.Spacer, NodeFlags.LayoutDirty);
        h.Run();

        Assert.Equal(ExpandedH, h.Scene.Bounds(h.N).H, 3);   // MUST still read the open drawer's real height
        Assert.True(h.Scene.Bounds(h.N).H > RowH, "a stale ring slot must not silently re-collapse a still-open subtree");
    }

    // THE PARITY REGRESSION (gate.layout.parity-from-scratch, 2026-09-19). The first version of this fix REPLACED the
    // IsLayoutClean gate with the version check and bumped the version only on a LayoutDirty 0→1 EDGE. A second edit
    // to a descendant that is STILL dirty (no ClearLayoutDirty between two edits — the parity gate's shape, and the
    // host's D1 realize loop: RunDirty → realize/rebind marks → RunDirty → ClearLayoutDirty) is not an edge, so
    // nothing bumped: N's ring slot written by the pass in between (holding the FIRST edit's height) hit on the next
    // pass, Measure(N) never descended, and Arrange read C's stale measured Bounds. Two things must each catch it:
    // IsLayoutClean(N) is false here (C is still dirty), AND the version moved (every mark bumps, not just the edge).
    [Fact]
    public void ASecondEditToAStillDirtyDescendantIsNotServedFromTheSlotStoredBetweenTheTwoEdits()
    {
        var h = Build();
        h.Scene.Layout(h.Spacer).Width = 300f;   // N's avail = 700, constant throughout — N's ring key never changes
        h.Run();
        Assert.Equal(RowH, h.Scene.Bounds(h.N).H, 3);

        h.Scene.Layout(h.C).Height = 100f;
        h.Scene.Mark(h.C, NodeFlags.LayoutDirty);   // the 0→1 edge
        h.Run();                                     // stores N's avail=700 slot with H=100
        Assert.Equal(100f, h.Scene.Bounds(h.N).H, 3);

        // No ClearLayoutDirty: C is still LayoutDirty, so this second mark is NOT an edge.
        h.Scene.Layout(h.C).Height = ExpandedH;
        h.Scene.Mark(h.C, NodeFlags.LayoutDirty);
        h.Run();
        Assert.Equal(ExpandedH, h.Scene.Bounds(h.N).H, 3);   // must NOT be the 100 the in-between slot holds
    }

    // The same non-edge mark, cross-FRAME — the host's D1 shape (RunDirty → realize/rebind marks a still-dirty node →
    // RunDirty → ClearLayoutDirty). The slot for avail=700 is stored by the pass BETWEEN the two marks (H=100, C still
    // dirty); the second mark is not an edge; the pass that follows it runs at a DIFFERENT width (800) so the 700 slot
    // is never re-stored; the frame boundary then clears C. The later revisit at 700 finds IsLayoutClean(N) TRUE
    // (only Spacer, a sibling, is dirty), so the version bump on the NON-EDGE mark is the only thing that stops the
    // ring from handing back the 100 that slot still holds.
    [Fact]
    public void ANonEdgeMarkStillInvalidatesASlotStoredBetweenTheTwoMarksAcrossAFrameBoundary()
    {
        var h = Build();
        h.Scene.Layout(h.Spacer).Width = 300f;   // N's avail = 700
        h.Run();
        Assert.Equal(RowH, h.Scene.Bounds(h.N).H, 3);

        h.Scene.Layout(h.C).Height = 100f;
        h.Scene.Mark(h.C, NodeFlags.LayoutDirty);   // the 0→1 edge
        h.Run();                                     // stores N's avail=700 slot with H=100 (C still dirty)
        Assert.Equal(100f, h.Scene.Bounds(h.N).H, 3);

        h.Scene.Layout(h.C).Height = ExpandedH;
        h.Scene.Mark(h.C, NodeFlags.LayoutDirty);   // non-edge: C is still dirty from the previous mark
        h.Scene.Layout(h.Spacer).Width = 200f;       // N's avail = 800 — this pass never touches the 700 slot
        h.Scene.Mark(h.Spacer, NodeFlags.LayoutDirty);
        h.Run();
        Assert.Equal(ExpandedH, h.Scene.Bounds(h.N).H, 3);

        h.Scene.ClearLayoutDirty();                  // the frame boundary — N is clean again
        h.Scene.Layout(h.Spacer).Width = 300f;       // back to avail = 700: the stale H=100 slot's key
        h.Scene.Mark(h.Spacer, NodeFlags.LayoutDirty);
        h.Run();
        Assert.Equal(ExpandedH, h.Scene.Bounds(h.N).H, 3);
    }

    // THE CACHE MUST STILL WORK. Re-asking for the SAME (node, availWidth) with NOTHING changed anywhere in the
    // subtree must still ride the ring (no regression of the P4 perf win this fix touches) — SubtreeVersion is
    // monotonic-and-otherwise-inert, so two reads with no intervening Mark(..., LayoutDirty) must see the same value
    // and hit.
    [Fact]
    public void ARingSlotIsStillServedWhenNothingInTheSubtreeChanged()
    {
        var h = Build();
        h.Scene.Layout(h.Spacer).Width = 300f;   // N's avail = 700, constant across both calls below

        h.Run();
        Assert.Equal(RowH, h.Scene.Bounds(h.N).H, 3);

        h.Layout.ResetFrameDiagCounters();
        h.Run();   // same Spacer width ⇒ same avail ⇒ nothing dirtied in between ⇒ must hit the ring

        Assert.Equal(RowH, h.Scene.Bounds(h.N).H, 3);
        Assert.True(h.Layout.DiagMeasureMemoHits > 0,
            "an unchanged (node, availWidth) pair must still be served by the memo/ring, not recomputed from scratch");
    }
}
