using System;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;

/// <summary>
/// Repaint damage (gpu-renderer.md §13): the TRUTHFUL repaint set that crosses the render seam inside
/// <see cref="FrameInfo.RepaintDamage"/> — the pure region algebra, the recorder-emitted bands, the publish-gap carry, the
/// end-to-end payload the headless device receives, and the pure geometry layer the tile rasterizer culls through
/// (<see cref="RepaintPolicy.ToPixel"/>, <see cref="RepaintStreamSafety.TryBodySize"/>, <see cref="RepaintCull"/>).
/// </summary>
static class DamageSuite
{
    public static void Run(StringTable strings)
    {
        RegionMathChecks();
        RecordDamageChecks();
        RecordDirtyLifetimeChecks.Run();
        CompositorDamageChecks();
        PublishGapChecks();
        HeadlessPayloadChecks(strings);
        ToPixelChecks();
        BodySizeChecks();
        CullHaloChecks();
        SpanReuseEquivalenceChecks(strings);
        SceneSnapshotChecks.Run();
        PublicationGapChecks.Run();
        CompositorAnimationChecks.Run();
        ImageRepaintChecks(strings);   // E1 (design-engine-images.md): image landing/crossfade damage their nodes, not the window
        VramShedPolicyChecks();        // E4: VramShedPolicy hysteresis/cooldown/grace, tested as a pure struct
    }

    // ── the pure geometry layer (RepaintPolicy.ToPixel / RepaintStreamSafety.TryBodySize / RepaintCull) ─────────────
    const float W = 1000f, H = 1000f;   // a 1e6 DIP² target — a rect's area in "% of target" reads directly

    // The ONE DIP → device-pixel conversion (scissor, tile cull box, Present dirty-rect census): round OUT on every side
    // (a partially-covered device pixel must be repainted whole, or the AA edge inside it keeps last frame's value) and
    // clamp to the target.
    static void ToPixelChecks()
    {
        PixelRect one = RepaintPolicy.ToPixel(new RectF(10.2f, 20.9f, 5.5f, 3.2f), 1f, (int)W, (int)H);
        bool roundsOut = one.Left == 10 && one.Top == 20 && one.Right == 16 && one.Bottom == 25;
        PixelRect off = RepaintPolicy.ToPixel(new RectF(-50f, -50f, 20f, 20f), 1f, (int)W, (int)H);
        bool clampsAway = off.IsEmpty;
        PixelRect edge = RepaintPolicy.ToPixel(new RectF(W - 5f, H - 5f, 500f, 500f), 1f, (int)W, (int)H);
        bool clampsToTarget = edge.Right == (int)W && edge.Bottom == (int)H;
        // A non-unit scale is the case that ships (150 % DPI): the rounding is applied to the SCALED coordinates.
        PixelRect scaled = RepaintPolicy.ToPixel(new RectF(50f, 10f, 50.2f, 40f), 1.5f, (int)(W * 1.5f), (int)(H * 1.5f));
        bool scaledOut = scaled.Left == 75 && scaled.Top == 15 && scaled.Right == 151 && scaled.Bottom == 75;
        Check("gate.repaint.to-pixel-rounds-out the DIP → device-pixel conversion rounds OUT on every side, applies the scale before rounding, and clamps to the target (an off-target rect is empty)",
            roundsOut && clampsAway && clampsToTarget && scaledOut,
            $"roundsOut={roundsOut}({one}) clampAway={clampsAway} clampTarget={clampsToTarget} scaled={scaled}");
    }

    // The ONE opcode → payload-size table every stream walker frames through: an unknown opcode is refused (never guessed
    // past), every known op sizes to its command struct.
    static void BodySizeChecks()
    {
        bool unknownRefused = !RepaintStreamSafety.TryBodySize((DrawOp)9999, out int unknownBody) && unknownBody == 0;
        bool fillSized = RepaintStreamSafety.TryBodySize(DrawOp.FillRoundRect, out int fillBody)
                         && fillBody == System.Runtime.CompilerServices.Unsafe.SizeOf<FillRoundRectCmd>();
        bool popClipEmpty = RepaintStreamSafety.TryBodySize(DrawOp.PopClip, out int popBody) && popBody == 0;
        bool sliceSized = RepaintStreamSafety.TryBodySize(DrawOp.CompositeSlice, out int sliceBody)
                          && sliceBody == System.Runtime.CompilerServices.Unsafe.SizeOf<CompositeSliceCmd>();
        Check("gate.repaint.body-size-table the one opcode → payload-size table refuses an unknown opcode and sizes known ones (payload-free PopClip, the slice marker) to their command structs",
            unknownRefused && fillSized && popClipEmpty && sliceSized,
            $"unknown={unknownRefused} fill={fillSized} popClip={popClipEmpty} slice={sliceSized}");
    }

    static void CullHaloChecks()
    {
        var rect = new RectF(100f, 100f, 100f, 100f);

        // Edge-exact primitives are KEPT (the tests are inclusive on every side) — the single most dangerous rounding
        // direction in the whole cull, because a wrongly-dropped boundary straddler is a visible seam.
        {
            RepaintCull.Aabb(0f, 100f, 100f, 100f, 1f, 0f, 0f, 1f, 0f, 0f, out float l, out float t, out float r, out float b);
            bool touching = RepaintCull.Keep(l, t, r, b, 0f, in rect);        // right edge == rect.X exactly
            bool clearOf = !RepaintCull.Keep(l - 50f, t, r - 50f, b, 0f, in rect);
            Check("gate.repaint.cull-edge-inclusive a primitive whose device AABB touches the replay rect EXACTLY on an edge is KEPT (inclusive on all four sides), while one clear of it by real space is culled — a wrongly-dropped boundary straddler is a visible seam",
                touching && clearOf, $"touching={touching} clearOf={clearOf}");
        }

        // Per-kind halos pull an outside primitive back in by exactly the footprint its vertex shader rasterizes.
        {
            // A stroke 20 wide whose box ends 11 units left of the rect: half the band (10) + the 2-unit AA feather reaches in.
            float strokeHalo = RepaintCull.StrokeHalo(20f);
            bool strokeReaches = RepaintCull.Keep(80f, 100f, 89f, 200f, strokeHalo, in rect);
            bool strokeIsHalf = MathF.Abs(strokeHalo - 12f) < 1e-4f;

            // A shadow's tail: spread 4 + 3σ. The halo must be at least the shader's own 3·max(blur/2, 0.5).
            float shadowHalo = RepaintCull.ShadowHalo(4f, 10f);
            bool shadowCoversShader = shadowHalo >= 4f + 3f * MathF.Max(10f * 0.5f, 0.5f);
            bool shadowReaches = RepaintCull.Keep(60f, 100f, 70f, 200f, shadowHalo, in rect);

            // Glyph: max(GlyphHaloMinDip, em × GlyphHaloEmScale), floored for tiny text and scaling with big text; the
            // wipe LIFT adds on top. (Raised from the old max(4, em/2) — see gate.repaint.glyph-halo-bound.)
            bool glyphFloor = MathF.Abs(RepaintCull.GlyphHalo(4f) - RepaintCull.GlyphHaloMinDip) < 1e-4f;
            bool glyphScales = MathF.Abs(RepaintCull.GlyphHalo(40f) - 40f * RepaintCull.GlyphHaloEmScale) < 1e-4f;
            bool glyphLift = MathF.Abs(RepaintCull.GlyphHalo(40f, 6f) - (40f * RepaintCull.GlyphHaloEmScale + 6f)) < 1e-4f;

            // A plain fill gets only the SDF pipelines' 2-unit AA margin — and 5 units away it is genuinely gone.
            bool aaKeeps = RepaintCull.Keep(98f, 100f, 99f, 200f, RepaintCull.AaHaloDip, in rect);
            bool aaDrops = !RepaintCull.Keep(90f, 100f, 95f, 200f, RepaintCull.AaHaloDip, in rect);

            Check("gate.repaint.cull-halos the per-kind halos match the footprint each vertex shader actually rasterizes — stroke w/2+2, shadow spread+3sigma (>= the shader's own 3*max(blur/2,0.5)), glyph max(GlyphHaloMinDip, em*GlyphHaloEmScale) plus any wipe lift, plain fill the 2-unit AA margin — so an off-rect primitive whose PIXELS reach in is kept and one whose pixels do not is dropped",
                strokeReaches && strokeIsHalf && shadowCoversShader && shadowReaches
                && glyphFloor && glyphScales && glyphLift && aaKeeps && aaDrops,
                $"stroke={strokeHalo} shadow={shadowHalo} glyph={RepaintCull.GlyphHalo(40f, 6f)} aaKeeps={aaKeeps} aaDrops={aaDrops}");
        }

        // I4 — the glyph halo is the ONE cull halo with no vertex shader to derive it from: GlyphRenderer places quads
        // from shaped advances + atlas bearings against a declared Bounds that is the NODE BOX, not an ink box. So it is
        // pinned here as a BOUND with margin over the classes that provably exceed the old em/2 heuristic. Under-covering
        // does not merely over-draw — it DROPS a run straddling a replay-rect edge and freezes a half-letter into the
        // retained canvas until something else repaints that region.
        {
            // A COLR/CBDT colour-emoji fallback rasterizes past the em box (the fallback face is picked for coverage,
            // not for metric compatibility with the run's declared size).
            const float EmojiOverhangEm = 1.35f;
            // A LineStacking/LineBounds line box tighter than the font's ascent+descent (~1.2 em) pushes ink outside the
            // declared Bounds on both sides; a 0.4 em line box leaves ~0.8 em of overhang.
            const float TightLineOverhangEm = 0.80f;

            bool coversEmoji = true, coversTightLine = true, beatsOldHeuristic = true, floored = true;
            foreach (float em in new[] { 4f, 9f, 12f, 14f, 28f, 64f, 180f })
            {
                float halo = RepaintCull.GlyphHalo(em);
                coversEmoji &= halo >= em * EmojiOverhangEm - 1e-3f;
                coversTightLine &= halo >= em * TightLineOverhangEm - 1e-3f;
                beatsOldHeuristic &= halo > em * 0.5f;                    // strictly wider than the heuristic it replaces
                floored &= halo >= RepaintCull.GlyphHaloMinDip - 1e-3f;   // tiny text still gets a real band
            }
            // The lift is a per-glyph VERTICAL displacement (karaoke wipe), so it adds to the halo rather than scaling it.
            bool liftAdds = MathF.Abs(RepaintCull.GlyphHalo(14f, 11f) - (RepaintCull.GlyphHalo(14f) + 11f)) < 1e-4f
                            && MathF.Abs(RepaintCull.GlyphHalo(14f, -11f) - (RepaintCull.GlyphHalo(14f) + 11f)) < 1e-4f;
            // A run whose ink hangs one full em below its declared Bounds is KEPT when the replay rect is that far away —
            // the concrete "chopped descender at a rect edge" case, at the engine's own body size.
            var edge = new RectF(100f, 100f, 100f, 100f);
            bool keepsOverhangingRun = RepaintCull.Keep(120f, 86f, 180f, 99f, RepaintCull.GlyphHalo(14f), in edge);

            Check("gate.repaint.glyph-halo-bound the glyph cull halo is a BOUND, not a heuristic: it is the only one with no vertex shader to derive it from (quads come from shaped advances + atlas bearings against a NODE-BOX Bounds), so it must cover an oversized COLR/CBDT colour-emoji fallback (1.35 em past the box), a LineStacking/LineBounds line box tighter than the font's ascent+descent (0.8 em), and a hard floor for tiny text — strictly wider than the old max(4, em/2) at every size, with the karaoke wipe lift ADDING to it in both directions",
                coversEmoji && coversTightLine && beatsOldHeuristic && floored && liftAdds && keepsOverhangingRun,
                $"emoji={coversEmoji} tightLine={coversTightLine} beatsOld={beatsOldHeuristic} floored={floored} lift={liftAdds} keepsOverhang={keepsOverhangingRun} halo14={RepaintCull.GlyphHalo(14f)}");
        }

        // Rotation: the AABB must come from all FOUR transformed corners (canon §13.1), not from transforming the
        // top-left/bottom-right pair — a 45° square's AABB is sqrt(2)x wider than its axis-aligned box.
        {
            const float c = 0.70710678f;
            RepaintCull.Aabb(0f, 0f, 100f, 100f, c, c, -c, c, 150f, 20f, out float l, out float t, out float r, out float b);
            bool widened = MathF.Abs((r - l) - 141.42f) < 0.1f && MathF.Abs((b - t) - 141.42f) < 0.1f;
            bool reachesIn = RepaintCull.Keep(l, t, r, b, 0f, in rect);
            Check("gate.repaint.cull-rotated-aabb the cull AABB folds all FOUR transformed corners, so a rotated primitive's true footprint (a 45-degree square spans sqrt(2)x its side) is tested — transforming only two corners would under-cover and drop a straddler",
                widened && reachesIn, $"w={(r - l):0.00} h={(b - t):0.00} reachesIn={reachesIn}");
        }
    }

    // ── pure region algebra ─────────────────────────────────────────────────────────────────────────────────────────
    static void RegionMathChecks()
    {
        // Disjointness + restart-on-fold. The chain is built so a SINGLE fold pass would be wrong: A and C are apart,
        // B touches neither, and the newcomer D bridges A→B; folding A into D grows it enough to also reach C, which a
        // non-restarting scan would leave overlapping.
        {
            var r = default(RepaintDamageRegion);
            r.Add(new RectF(0f, 0f, 10f, 10f));       // A
            r.Add(new RectF(40f, 0f, 10f, 10f));      // B (clear of A)
            r.Add(new RectF(80f, 0f, 10f, 10f));      // C (clear of both)
            bool three = r.Count == 3;
            r.Add(new RectF(5f, 0f, 80f, 10f));       // D bridges A..C in one go
            bool collapsed = r.Count == 1 && r[0].X == 0f && MathF.Abs(r[0].Right - 90f) < 1e-3f;
            bool disjoint = PairwiseDisjoint(r);
            Check("gate.damage.region-disjoint Add keeps members PAIRWISE disjoint and RESTARTS the fold scan — a bridging rect that grows past two more members collapses them all into one (a single pass would leave an overlap, which silently double-counts SummedArea)",
                three && collapsed && disjoint, $"three={three} collapsed={collapsed} disjoint={disjoint} count={r.Count}");
        }

        // Abutting rects fold too (closed-interval adjacency), which is what makes SummedArea an exact area.
        {
            var r = default(RepaintDamageRegion);
            r.Add(new RectF(0f, 0f, 10f, 10f));
            r.Add(new RectF(10f, 0f, 10f, 10f));      // shares the edge exactly
            bool folded = r.Count == 1 && MathF.Abs(r[0].W - 20f) < 1e-3f;
            var s = default(RepaintDamageRegion);
            s.Add(new RectF(0f, 0f, 10f, 10f));
            s.Add(new RectF(10.5f, 0f, 10f, 10f));    // a real gap ⇒ stays separate
            bool kept = s.Count == 2;
            bool emptyIgnored;
            {
                var t = default(RepaintDamageRegion);
                t.Add(new RectF(5f, 5f, 0f, 40f));
                t.Add(new RectF(5f, 5f, 40f, -1f));
                emptyIgnored = t.Count == 0 && t.IsEmpty;
            }
            Check("gate.damage.region-abut abutting rects fold into one (so \"disjoint\" means separated by real space and SummedArea stays exact); a genuine gap keeps them apart; zero/negative-extent rects are ignored",
                folded && kept && emptyIgnored, $"folded={folded} kept={kept} emptyIgnored={emptyIgnored}");
        }

        // Capacity: the 17th disjoint rect must LAND, by merging the pair whose union adds the least dead area. The
        // layout puts 16 far-apart rects on a diagonal plus TWO neighbours 1 unit apart — those two are the cheapest
        // merge by a wide margin, so the result must contain their tight union and still hold 16 members.
        {
            var r = default(RepaintDamageRegion);
            for (int i = 0; i < 15; i++) r.Add(new RectF(i * 1000f, i * 1000f, 10f, 10f));
            r.Add(new RectF(50f, 20000f, 10f, 10f));         // the cheap pair, part 1  → 16 members
            bool atCap = r.Count == RepaintDamageRegion.MaxRects;
            r.Add(new RectF(61f, 20000f, 10f, 10f));         // the 17th: 1 unit of gap from its neighbour
            bool stillCap = r.Count == RepaintDamageRegion.MaxRects;
            bool cheapPairMerged = false;
            for (int i = 0; i < r.Count; i++)
                if (MathF.Abs(r[i].X - 50f) < 1e-3f && MathF.Abs(r[i].Right - 71f) < 1e-3f && MathF.Abs(r[i].Y - 20000f) < 1e-3f)
                    cheapPairMerged = true;
            bool disjoint = PairwiseDisjoint(r);
            Check("gate.damage.region-cap-least-waste the 17th rect still lands: the pair whose union adds the LEAST dead area is merged first (two neighbours 1 unit apart, not two diagonal rects 1000 apart), the count stays at MaxRects, and the members stay disjoint",
                atCap && stillCap && cheapPairMerged && disjoint,
                $"atCap={atCap} stillCap={stillCap} cheapPairMerged={cheapPairMerged} disjoint={disjoint} count={r.Count}");
        }

        // ForceFull: first cause wins, rects are dropped, and the region is sealed against further Adds.
        {
            var r = default(RepaintDamageRegion);
            r.Add(new RectF(0f, 0f, 10f, 10f));
            r.ForceFull(RepaintFullReason.MissingPriorExtent);
            bool cleared = r.IsFull && r.Count == 0 && !r.IsEmpty;
            r.ForceFull(RepaintFullReason.ImageContent);                 // a later, less specific cause must NOT win
            bool firstCauseWins = r.FullReason == RepaintFullReason.MissingPriorExtent;
            r.Add(new RectF(100f, 100f, 10f, 10f));
            bool sealedAfter = r.Count == 0 && r.Coverage(1000f, 1000f) == 1f;
            var noop = default(RepaintDamageRegion);
            noop.ForceFull(RepaintFullReason.None);
            bool noneIsNoop = !noop.IsFull && noop.IsEmpty;
            Check("gate.damage.region-force-full-first-cause ForceFull drops the rects, seals the region against further Adds, reads Coverage 1, and keeps the FIRST reason (the one that actually surrendered — otherwise the diagnostic names the wrong source); ForceFull(None) is a no-op",
                cleared && firstCauseWins && sealedAfter && noneIsNoop,
                $"cleared={cleared} firstCause={r.FullReason} sealed={sealedAfter} noneIsNoop={noneIsNoop}");
        }

        // SummedArea/Coverage against an analytic answer — the whole point of the disjointness invariant.
        {
            var r = default(RepaintDamageRegion);
            r.Add(new RectF(0f, 0f, 10f, 20f));       // 200
            r.Add(new RectF(100f, 0f, 30f, 10f));     // 300
            bool exact = MathF.Abs(r.SummedArea() - 500f) < 1e-3f;
            // Overlapping adds must NOT double-count: two 10×10 rects overlapping by 5×10 union to 15×10 = 150.
            var o = default(RepaintDamageRegion);
            o.Add(new RectF(0f, 0f, 10f, 10f));
            o.Add(new RectF(5f, 0f, 10f, 10f));
            bool noDoubleCount = o.Count == 1 && MathF.Abs(o.SummedArea() - 150f) < 1e-3f;
            bool coverage = MathF.Abs(r.Coverage(100f, 10f) - 0.5f) < 1e-3f;   // 500 of 1000
            bool degenerate = r.Coverage(0f, 0f) == 0f;
            Check("gate.damage.region-summed-area SummedArea is the EXACT damaged area (disjoint members ⇒ no double count, even after an overlapping Add folds) and Coverage is that over the target, clamped, with a degenerate target reading 0",
                exact && noDoubleCount && coverage && degenerate,
                $"exact={exact} noDoubleCount={noDoubleCount} coverage={r.Coverage(100f, 10f):0.000} degenerate={degenerate}");
        }

        // Union — the publisher's publish-gap carry.
        {
            var a = default(RepaintDamageRegion);
            a.Add(new RectF(0f, 0f, 10f, 10f));
            var b = default(RepaintDamageRegion);
            b.Add(new RectF(500f, 500f, 10f, 10f));
            a.Union(in b);
            bool both = a.Count == 2 && MathF.Abs(a.SummedArea() - 200f) < 1e-3f;

            var c = default(RepaintDamageRegion);
            c.Add(new RectF(0f, 0f, 10f, 10f));
            var full = default(RepaintDamageRegion);
            full.ForceFull(RepaintFullReason.PublishGap);
            c.Union(in full);
            bool inherits = c.IsFull && c.FullReason == RepaintFullReason.PublishGap && c.Count == 0;

            // A full region absorbing a rect-carrying one stays full with ITS OWN reason (first cause).
            var keep = default(RepaintDamageRegion);
            keep.ForceFull(RepaintFullReason.TargetInvalidated);
            keep.Union(in b);
            bool keepsOwn = keep.IsFull && keep.FullReason == RepaintFullReason.TargetInvalidated;

            Check("gate.damage.region-union Union folds another region's rects in (over-inclusion is the safe direction for a publish gap) and a forced-full other forces this one full with that reason; an already-full region keeps its own first cause",
                both && inherits && keepsOwn, $"both={both} inherits={inherits} keepsOwn={keepsOwn}");
        }

        // Equality must go through the hand-written IEquatable, never ValueType.Equals over the [InlineArray] (which
        // boxes). Asserting behaviour here also pins that FrameInfo's synthesized comparison stays alloc-free.
        {
            var a = default(RepaintDamageRegion);
            var b = default(RepaintDamageRegion);
            a.Add(new RectF(1f, 2f, 3f, 4f));
            b.Add(new RectF(1f, 2f, 3f, 4f));
            bool eq = a.Equals(b) && a.GetHashCode() == b.GetHashCode();
            b.Add(new RectF(500f, 500f, 3f, 4f));
            bool neq = !a.Equals(b);
            var fa = new FrameInfo(new Size2(100, 100), 1f, default, RepaintDamage: a);
            var fb = new FrameInfo(new Size2(100, 100), 1f, default, RepaintDamage: a);
            bool frameEq = true;
            for (int i = 0; i < 8; i++) frameEq &= fa.Equals(fb);   // warm: the one-time EqualityComparer<T>.Default construction is not the measurement
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++) frameEq &= fa.Equals(fb);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("gate.damage.region-equality the hand-written IEquatable drives equality/hashing, so comparing two FrameInfos (which carry the region by value) allocates 0 bytes — the synthesized record path would box the [InlineArray] through ValueType.Equals",
                eq && neq && frameEq && delta == 0, $"eq={eq} neq={neq} frameEq={frameEq} delta={delta}B");
        }
    }

    static bool PairwiseDisjoint(RepaintDamageRegion r)
    {
        for (int i = 0; i < r.Count; i++)
            for (int j = i + 1; j < r.Count; j++)
                if (r[i].Overlaps(r[j])) return false;
        return true;
    }

    static bool CoveredBy(RepaintDamageRegion r, in RectF probe)
    {
        for (int i = 0; i < r.Count; i++)
        {
            RectF m = r[i];
            if (probe.X >= m.X && probe.Y >= m.Y && probe.Right <= m.Right && probe.Bottom <= m.Bottom) return true;
        }
        return false;
    }

    // ── gate.damage.compositor-* — a render-thread POSE damages the posed node, not the window ──────────────────────
    // §13.1 promises "animated transforms dirty only old∪new bounds → a spinner repaints a tiny region". It did not:
    // a pose marked the ancestor TRAIL, Flags() reported TransformDirty from that trail, and the recorder damages a
    // TransformDirty node's SubtreeBounds — which at the ROOT is the whole window. One looping marquee therefore
    // repainted every pixel, at panel rate, forever. These five gates pin the fix from both ends: the posed node is
    // damaged and its ancestors are not; a row that re-poses the same value damages nothing at all; and a row that
    // disappears or parks damages exactly once, because the node falls back to its authored pose and no surviving row
    // would otherwise report that.
    static void CompositorDamageChecks()
    {
        const float W = 800f, H = 600f;

        // A small leaf three container levels deep, so "the leaf's band" and "an ancestor's SubtreeBounds" are
        // unmistakably different rectangles — the whole point of the split.
        static (SceneStore Scene, NodeHandle Root, NodeHandle Mid, NodeHandle Leaf, AnimEngine Anim) Build()
        {
            var scene = new SceneStore();
            var root = scene.CreateNode(1); scene.Root = root;
            scene.Bounds(root) = new RectF(0f, 0f, W, H);
            scene.Paint(root).VisualKind = VisualKind.Box;

            var outer = scene.CreateNode(1); scene.AppendChild(root, outer);
            scene.Bounds(outer) = new RectF(0f, 0f, W, H);
            scene.Paint(outer).VisualKind = VisualKind.Box;

            var mid = scene.CreateNode(1); scene.AppendChild(outer, mid);
            scene.Bounds(mid) = new RectF(40f, 40f, 600f, 400f);
            scene.Paint(mid).VisualKind = VisualKind.Box;

            var leaf = scene.CreateNode(1); scene.AppendChild(mid, leaf);
            scene.Bounds(leaf) = new RectF(100f, 100f, 40f, 20f);
            ref NodePaint lp = ref scene.Paint(leaf);
            lp.VisualKind = VisualKind.Box; lp.Fill = new ColorF(0.9f, 0.3f, 0.2f, 1f);
            return (scene, root, mid, leaf, new AnimEngine(scene) { RenderOwnsCompositor = true });
        }

        // Capture a snapshot of a SETTLED store. A snapshot copies the store's authored dirty bits at capture and keeps
        // them for the slot's life, so a snapshot taken straight off a freshly built scene re-damages every authored
        // node on every record — swamping the compositor band these gates exist to measure. The host never sees it
        // because it clears after each record; here the clear has to happen BEFORE the capture.
        static SceneRecordingSnapshot Settled(SceneStore scene, DrawList dl, SpanTable spans)
        {
            SceneRecorder.Record(scene, dl, spans: spans);
            scene.ClearRecordDirty();
            scene.ClearTransformDirty();
            var snap = new SceneRecordingSnapshot();
            snap.Capture(scene);
            dl.Reset();
            return snap;
        }

        // One posed render turn: tick the compositor onto the snapshot, then record THAT snapshot (not a fresh capture
        // — the overlay lives on the instance the renderer just wrote).
        static SceneRecordStats PosedFrame(SceneRecordingSnapshot snap, RenderCompositorAnimations renderer,
                                           DrawList dl, SpanTable spans, double nowMs)
        {
            renderer.Tick(snap, nowMs);
            dl.Reset();
            return snap.Recording.Record(snap, dl, spans: spans);
        }

        // ── 1. the posed leaf's band is scoped, and contains the leaf ───────────────────────────────────────────────
        {
            var (scene, root, mid, leaf, anim) = Build();
            var dl = new DrawList(); var spans = new SpanTable();
            var snap = Settled(scene, dl, spans);
            var renderer = new RenderCompositorAnimations();

            anim.Keyframes(leaf, AnimChannel.TranslateX,
                [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 120f, Easing.Linear)], 1000f, loop: true);
            var desired = new CompositorAnimationSnapshot();
            anim.CaptureCompositorAnimations(desired, 0);
            renderer.Adopt(desired, snap, 0);

            var st = PosedFrame(snap, renderer, dl, spans, 120);
            float coverage = st.RepaintDamage.Coverage(W, H);
            // The leaf is 40x20 in an 800x600 window = 0.17 %. Its old∪new band plus AA padding is still tiny; a
            // generous 5 % ceiling fails hard on the old behaviour (which was exactly 1.0) without pinning the
            // recorder's padding constants.
            Check("gate.damage.compositor-pose-scoped a looping TranslateX on a small leaf three containers deep damages a BAND, not the window — the posed node's own SubtreeBounds, never the root's",
                !st.RepaintDamage.IsFull && coverage > 0f && coverage < 0.05f,
                $"full={st.RepaintDamage.IsFull} coverage={coverage:0.0000} rects={st.RepaintDamage.Count}");

            // ── 2. ancestors are on the walk trail but do not claim to have moved ───────────────────────────────────
            bool leafMoved = (snap.Flags(leaf) & NodeFlags.TransformDirty) != 0;
            bool midMoved = (snap.Flags(mid) & NodeFlags.TransformDirty) != 0;
            bool rootMoved = (snap.Flags(root) & NodeFlags.TransformDirty) != 0;
            bool trailKept = (snap.RecordDirtyDescendantBits(root) & SceneStore.RecordDirtyContent) != 0
                          && (snap.RecordDirtyDescendantBits(mid) & SceneStore.RecordDirtyContent) != 0;
            Check("gate.damage.compositor-ancestors-clean a posed leaf leaves its PARENT and the ROOT reporting no TransformDirty, while the re-walk trail still reaches both — the two jobs one epoch array used to do, now separated",
                leafMoved && !midMoved && !rootMoved && trailKept,
                $"leaf={leafMoved} mid={midMoved} root={rootMoved} trail={trailKept}");
        }

        // ── 3. a cadence-held row damages nothing between its steps ─────────────────────────────────────────────────
        {
            var (scene, _, _, leaf, anim) = Build();
            var dl = new DrawList(); var spans = new SpanTable();
            var snap = Settled(scene, dl, spans);
            var renderer = new RenderCompositorAnimations();

            // 30 Hz cadence ticked at 120 Hz: three of every four ticks re-pose the identical value.
            anim.Keyframes(leaf, AnimChannel.TranslateX,
                [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 120f, Easing.Linear)], 1000f, loop: true,
                cadence: Cadence.At(30f));
            var desired = new CompositorAnimationSnapshot();
            anim.CaptureCompositorAnimations(desired, 0);
            renderer.Adopt(desired, snap, 0);

            PosedFrame(snap, renderer, dl, spans, 100);              // a step lands here
            var held = PosedFrame(snap, renderer, dl, spans, 100 + 1000.0 / 120.0);   // 8.3 ms later: still held
            Check("gate.damage.compositor-held-empty a Cadence.At(30) row ticked at 120 Hz damages NOTHING between its steps — a held row re-poses an identical float, and posing that as a change is what made a 60 Hz marquee repaint at panel rate",
                held.RepaintDamage.IsEmpty && !held.RepaintDamage.IsFull && !renderer.ChangedThisTick,
                $"empty={held.RepaintDamage.IsEmpty} full={held.RepaintDamage.IsFull} changed={renderer.ChangedThisTick}");
        }

        // ── 4. a finished row goes quiet, and stays quiet across re-adoption ────────────────────────────────────────
        {
            var (scene, _, _, leaf, anim) = Build();
            var dl = new DrawList(); var spans = new SpanTable();
            var snap = Settled(scene, dl, spans);
            var renderer = new RenderCompositorAnimations();

            anim.Animate(leaf, AnimChannel.Opacity, 0f, 1f, 100f, Easing.Linear);
            var desired = new CompositorAnimationSnapshot();
            anim.CaptureCompositorAnimations(desired, 0);
            renderer.Adopt(desired, snap, 0);
            PosedFrame(snap, renderer, dl, spans, 200);              // well past the 100 ms duration: Done

            var again = new CompositorAnimationSnapshot();
            anim.CaptureCompositorAnimations(again, 200);
            renderer.Adopt(again, snap, 260);                        // Adopt ends in Tick
            dl.Reset();
            var quiet = snap.Recording.Record(snap, dl, spans: spans);
            Check("gate.damage.compositor-done-quiet a finished row left in the desired set damages nothing on re-adoption — it re-poses its landed value every tick forever, and that is not a change",
                quiet.RepaintDamage.IsEmpty && !quiet.RepaintDamage.IsFull,
                $"empty={quiet.RepaintDamage.IsEmpty} full={quiet.RepaintDamage.IsFull} rects={quiet.RepaintDamage.Count}");
        }

        // ── 5. a dropped row reverts the node, damaging it exactly once ─────────────────────────────────────────────
        {
            var (scene, _, _, leaf, anim) = Build();
            var dl = new DrawList(); var spans = new SpanTable();
            var snap = Settled(scene, dl, spans);
            var renderer = new RenderCompositorAnimations();

            anim.Keyframes(leaf, AnimChannel.TranslateX,
                [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 120f, Easing.Linear)], 1000f, loop: true);
            var desired = new CompositorAnimationSnapshot();
            anim.CaptureCompositorAnimations(desired, 0);
            renderer.Adopt(desired, snap, 0);
            PosedFrame(snap, renderer, dl, spans, 120);

            // Drop the instance: the node snaps back to its AUTHORED pose, which no row reports.
            var empty = new CompositorAnimationSnapshot();
            renderer.Adopt(empty, snap, 140);
            dl.Reset();
            var reverted = snap.Recording.Record(snap, dl, spans: spans);
            // The NEXT turn ticks first, exactly as the host does — that is what advances the overlay epoch and retires
            // the revert stamp. Recording twice with no tick between re-reads the same epoch and is not a state the
            // host can reach.
            var after = PosedFrame(snap, renderer, dl, spans, 160);
            Check("gate.damage.compositor-revert dropping a posed instance damages that node ONCE — it presents its authored pose again, a real pixel change no surviving row would report — and the frame after it is quiet",
                !reverted.RepaintDamage.IsEmpty && !reverted.RepaintDamage.IsFull && after.RepaintDamage.IsEmpty,
                $"reverted={!reverted.RepaintDamage.IsEmpty} full={reverted.RepaintDamage.IsFull} thenQuiet={after.RepaintDamage.IsEmpty}");
        }
    }

    // ── recorder-emitted damage (headless, straight through SceneRecorder.Record) ───────────────────────────────────
    static void RecordDamageChecks()
    {
        // One frame of the recorder, then the two clears the host does right after record. Without them every node
        // stays dirty forever and no gate below could tell a settled frame from a changed one.
        static SceneRecordStats Frame(SceneStore scene, DrawList dl, SpanTable spans)
        {
            var st = SceneRecorder.Record(scene, dl, spans: spans);
            scene.ClearRecordDirty();
            scene.ClearTransformDirty();
            return st;
        }

        static (SceneStore Scene, NodeHandle Root, NodeHandle Child) Build()
        {
            var scene = new SceneStore();
            var root = scene.CreateNode(1); scene.Root = root;
            scene.Bounds(root) = new RectF(0f, 0f, 400f, 300f);
            ref NodePaint rp = ref scene.Paint(root);
            rp.VisualKind = VisualKind.Box; rp.Fill = new ColorF(0.1f, 0.1f, 0.1f, 1f);

            var child = scene.CreateNode(1); scene.AppendChild(root, child);
            scene.Bounds(child) = new RectF(10f, 10f, 50f, 40f);
            ref NodePaint cp = ref scene.Paint(child);
            cp.VisualKind = VisualKind.Box; cp.Fill = new ColorF(0.8f, 0.2f, 0.2f, 1f);
            return (scene, root, child);
        }

        // 1. A settled frame damages NOTHING. This is the baseline every other gate is read against — a region that is
        //    always non-empty would make "a few % coverage while playing" unmeasurable.
        // 2. A MOVED node damages old ∪ new. Two arms: transform-only (which takes the translated-span-copy reuse path)
        //    and transform+paint (which falls through to a real re-record) — the emission must survive BOTH.
        foreach (bool alsoPaint in new[] { false, true })
        {
            var (scene, _, child) = Build();
            var dl = new DrawList();
            var spans = new SpanTable();
            NodeFlags marks = alsoPaint ? NodeFlags.TransformDirty | NodeFlags.PaintDirty : NodeFlags.TransformDirty;

            Frame(scene, dl, spans);                       // first record: everything is dirty
            var settled = Frame(scene, dl, spans);         // nothing changed — the ROOT exact-copies its whole span, so
                                                           // the child is never walked and its stored extent is CARRIED
                                                           // OVER rather than refreshed
            bool settledClean = settled.RepaintDamage.IsEmpty;

            // Move #1 reads a carried-over prior extent. It must still produce old∪new — a recency-gated lookup would
            // report the extent as lost and force a full repaint here, i.e. on the first change after ANY idle frame.
            scene.Paint(child).LocalTransform = Affine2D.Translation(200f, 0f);
            scene.Mark(child, marks);
            var first = Frame(scene, dl, spans);
            bool firstOld = CoveredBy(first.RepaintDamage, new RectF(10f, 10f, 50f, 40f));
            bool firstNew = CoveredBy(first.RepaintDamage, new RectF(210f, 10f, 50f, 40f));
            bool survivedReuse = !first.RepaintDamage.IsFull;

            // Move #2 reads a FRESH prior extent (move #1 re-recorded the child), and for the transform-only arm goes
            // through the translated-span-copy path instead of a re-record — the emission must survive both routes.
            scene.Paint(child).LocalTransform = Affine2D.Translation(320f, 0f);
            scene.Mark(child, marks);
            var second = Frame(scene, dl, spans);
            bool secondOld = CoveredBy(second.RepaintDamage, new RectF(210f, 10f, 50f, 40f));
            bool secondNew = CoveredBy(second.RepaintDamage, new RectF(330f, 10f, 50f, 40f));
            bool disjoint = PairwiseDisjoint(second.RepaintDamage);

            Check($"gate.damage.record-moved-node-old-union-new (paintToo={alsoPaint}) a settled frame damages NOTHING; a node that moves damages BOTH the band it vacated and the band it lands on, over disjoint rects — including the first move after an idle frame, where an ancestor's span reuse left the prior extent carried over rather than refreshed",
                settledClean && firstOld && firstNew && survivedReuse && secondOld && secondNew && disjoint && !second.RepaintDamage.IsFull,
                $"settledClean={settledClean} first=({firstOld},{firstNew}) survivedReuse={survivedReuse} second=({secondOld},{secondNew}) disjoint={disjoint} count={second.RepaintDamage.Count} full={second.RepaintDamage.FullReason}");
        }

        // 3. Paint-only damage EXISTS now. This is the exact class the acrylic union silently drops (its own comment
        //    says so), and the reason a second accumulator had to be built rather than reusing Damage. Assert BOTH: the
        //    repaint set gains the node's band, and the acrylic union stays empty (that exclusion must survive).
        {
            var (scene, _, child) = Build();
            var dl = new DrawList();
            var spans = new SpanTable();
            Frame(scene, dl, spans);
            Frame(scene, dl, spans);

            scene.Paint(child).Fill = new ColorF(0.2f, 0.8f, 0.3f, 1f);
            scene.Mark(child, NodeFlags.PaintDirty);
            var painted = Frame(scene, dl, spans);

            bool repainted = CoveredBy(painted.RepaintDamage, new RectF(10f, 10f, 50f, 40f));
            Check("gate.damage.record-paint-only a fill-only write (hover fade / text / recolor) damages the node's band in the REPAINT set without forcing a full repaint",
                repainted && !painted.RepaintDamage.IsFull,
                $"repainted={repainted} full={painted.RepaintDamage.FullReason}");
        }

        // 4. An UNMOUNTED node damages the extent it last presented at. Nothing re-touches that band, so without this
        //    a region-aware repaint freezes last frame's pixels there (the "ghost" class).
        {
            var (scene, _, child) = Build();
            var dl = new DrawList();
            var spans = new SpanTable();
            Frame(scene, dl, spans);
            Frame(scene, dl, spans);

            scene.FreeSubtree(child);
            var removed = Frame(scene, dl, spans);

            bool vacated = CoveredBy(removed.RepaintDamage, new RectF(10f, 10f, 50f, 40f));
            bool ledgerDrained = scene.PendingRemovalExtents.Length == 0 && !scene.PendingRemovalOverflow;
            Check("gate.damage.record-removal an unmounted node damages the extent it LAST PRESENTED at (recovered from the span table under its pre-free generation), and the scene's removal ledger is drained by the record that consumed it",
                vacated && ledgerDrained && !removed.RepaintDamage.IsFull,
                $"vacated={vacated} drained={ledgerDrained} count={removed.RepaintDamage.Count} full={removed.RepaintDamage.FullReason}");
        }

        // 5. A scrolled viewport's CONTENT node damages the VIEWPORT (not the content's far taller box) in the repaint
        //    set, while the acrylic union keeps excluding it entirely. Both halves are asserted: the exclusion at
        //    SceneRecorder's damage arm is deliberate (in-popup scrolling must not re-blur the popup's own backdrop)
        //    and must survive, but a repaint set that inherited it would leave the whole viewport stale.
        {
            var scene = new SceneStore();
            var root = scene.CreateNode(1); scene.Root = root;
            scene.Bounds(root) = new RectF(0f, 0f, 400f, 300f);
            ref NodePaint rp = ref scene.Paint(root);
            rp.VisualKind = VisualKind.Box; rp.Fill = new ColorF(0.1f, 0.1f, 0.1f, 1f);

            var viewport = scene.CreateNode(1); scene.AppendChild(root, viewport);
            scene.Bounds(viewport) = new RectF(20f, 30f, 200f, 100f);
            ref NodePaint vp = ref scene.Paint(viewport);
            vp.VisualKind = VisualKind.Box; vp.Fill = new ColorF(0.15f, 0.15f, 0.18f, 1f);
            scene.Mark(viewport, NodeFlags.ClipsToBounds);
            scene.ScrollRef(viewport);   // get-or-create ⇒ marks NodeFlags.Scrollable

            var content = scene.CreateNode(1); scene.AppendChild(viewport, content);
            scene.Bounds(content) = new RectF(0f, 0f, 200f, 4000f);   // far taller than the viewport
            ref NodePaint cp = ref scene.Paint(content);
            cp.VisualKind = VisualKind.Box; cp.Fill = new ColorF(0.3f, 0.3f, 0.35f, 1f);

            var dl = new DrawList();
            var spans = new SpanTable();
            Frame(scene, dl, spans);
            Frame(scene, dl, spans);

            scene.Paint(content).LocalTransform = Affine2D.Translation(0f, -120f);
            scene.Mark(content, NodeFlags.TransformDirty);
            var scrolled = Frame(scene, dl, spans);

            bool viewportDamaged = CoveredBy(scrolled.RepaintDamage, new RectF(20f, 30f, 200f, 100f));
            Check("gate.damage.record-scroll-viewport a scrolled viewport's content node damages the VIEWPORT rect in the repaint set (not its 4000px content box)",
                viewportDamaged,
                $"viewport={viewportDamaged} count={scrolled.RepaintDamage.Count} full={scrolled.RepaintDamage.FullReason}");
        }

        // 6. §13.1 I3 — a row inside SCROLLED content, which then moves ON ITS OWN. The scroll never re-records the row
        //    (it is a composite parameter of the content's slice), so the row's stored extent is in the slice's
        //    POSE-FREE space, 200 DIP away from where its pixels are in the window. The band it vacated must be mapped
        //    by where its slice was last PRESENTED (the slice's composite offset) — a constant halo pad cannot reach it —
        //    or the frame surrenders ONE named full.
        {
            var scene = new SceneStore();
            var root = scene.CreateNode(1); scene.Root = root;
            scene.Bounds(root) = new RectF(0f, 0f, 400f, 300f);
            ref NodePaint rp = ref scene.Paint(root);
            rp.VisualKind = VisualKind.Box; rp.Fill = new ColorF(0.1f, 0.1f, 0.1f, 1f);

            var viewport = scene.CreateNode(1); scene.AppendChild(root, viewport);
            scene.Bounds(viewport) = new RectF(0f, 0f, 400f, 300f);
            ref NodePaint vp = ref scene.Paint(viewport);
            vp.VisualKind = VisualKind.Box; vp.Fill = new ColorF(0.15f, 0.15f, 0.18f, 1f);
            scene.ScrollRef(viewport);

            var content = scene.CreateNode(1); scene.AppendChild(viewport, content);
            scene.Bounds(content) = new RectF(0f, 0f, 400f, 4000f);
            ref NodePaint cp = ref scene.Paint(content);
            cp.VisualKind = VisualKind.Box; cp.Fill = new ColorF(0.3f, 0.3f, 0.35f, 1f);

            // One row, visible BOTH before the scroll (y 250) and after it (y 50) — so the recorder really stores an
            // extent for it, and the extent it stores is 200 DIP away from where the pixels end up. 200 ≫ 32.
            var card = scene.CreateNode(1); scene.AppendChild(content, card);
            scene.Bounds(card) = new RectF(20f, 250f, 100f, 40f);
            ref NodePaint kp = ref scene.Paint(card);
            kp.VisualKind = VisualKind.Box; kp.Fill = new ColorF(0.8f, 0.4f, 0.2f, 1f);

            var dl = new DrawList();
            var spans = new SpanTable();
            Frame(scene, dl, spans);
            Frame(scene, dl, spans);

            // Scroll: the CONTENT node moves — a composite parameter; the card records nothing.
            scene.Paint(content).LocalTransform = Affine2D.Translation(0f, -200f);
            scene.Mark(content, NodeFlags.TransformDirty);
            Frame(scene, dl, spans);

            // Now the row makes a move of its own. Its stored extent still says y = 250; its pixels are at y = 50.
            scene.Paint(card).LocalTransform = Affine2D.Translation(150f, 0f);
            scene.Mark(card, NodeFlags.TransformDirty);
            var moved = Frame(scene, dl, spans);

            bool full = moved.RepaintDamage.IsFull;
            // The band the card TRULY vacated: post-scroll (y 50), pre-move (x 20).
            bool vacatedCovered = full ? moved.RepaintDamage.FullReason == RepaintFullReason.MissingPriorExtent
                                       : CoveredBy(moved.RepaintDamage, new RectF(20f, 50f, 100f, 40f));
            // …and the band it landed on.
            bool landedCovered = full || CoveredBy(moved.RepaintDamage, new RectF(170f, 50f, 100f, 40f));
            // The fresh-ancestor fallback is the intended outcome; a named full frame is the sound fallback of last
            // resort. What is NOT acceptable is a bounded region that covers neither.
            bool named = !full || moved.RepaintDamage.FullReason == RepaintFullReason.MissingPriorExtent;

            Check("gate.damage.record-stale-prior-after-ancestor-translate a row whose ancestor translated-span-copied it (a scroll) and which THEN moves on its own repaints the band it truly vacated — recovered from the nearest FRESH ancestor's extent, since its own stored extent describes a pre-scroll position 200 DIP away that a 32-DIP effect halo cannot reach — or, if no ancestor on the chain can place it, surrenders ONE named MissingPriorExtent full frame; the old constant-pad rule covered neither and left a ghost for as long as the app stayed on the partial path",
                vacatedCovered && landedCovered && named,
                $"full={full}/{moved.RepaintDamage.FullReason} vacated={vacatedCovered} landed={landedCovered} count={moved.RepaintDamage.Count}");
        }
    }

    // ── publisher: seq stamping + publish-gap accumulation ─────────────────────────────────────────────────────────
    static void PublishGapChecks()
    {
        FluentGpu.Hosting.Threading.ThreadGuard.BindCurrent(FluentGpu.Hosting.Threading.ThreadGuard.ThreadRole.Ui);
        var seam = new FluentGpu.Hosting.Threading.SceneFramePublisher();
        ReadOnlySpan<ulong> noKeys = default;

        static FrameInfo Info(in RepaintDamageRegion region)
            => new FrameInfo(new Size2(400, 300), 1f, default, RepaintDamage: region);

        var first = default(RepaintDamageRegion);
        first.Add(new RectF(0f, 0f, 10f, 10f));
        var second = default(RepaintDamageRegion);
        second.Add(new RectF(300f, 200f, 10f, 10f));

        // TWO publishes with no consume in between: DropOldest throws the first frame away, so its damage has to be
        // carried forward or those pixels are never repainted.
        seam.Publish(stackalloc byte[] { 1 }, noKeys, Info(in first));
        seam.Publish(stackalloc byte[] { 2 }, noKeys, Info(in second));
        bool acquired = seam.TryAcquire(out var rf);
        bool carriesBoth = acquired
            && CoveredBy(rf.Submit.RepaintDamage, new RectF(0f, 0f, 10f, 10f))
            && CoveredBy(rf.Submit.RepaintDamage, new RectF(300f, 200f, 10f, 10f));
        bool stamped = acquired && rf.Submit.PublishSequence == rf.PublishSeq && rf.Submit.PublishSequence == 2;
        // I2: the seq the carried region SPEAKS FOR. A consumer that sees seq 2 after consuming nothing must be able to
        // tell "the damage of frame 1 rode forward" from "frame 1 vanished" — otherwise a DropOldest gap (normal under
        // exactly the load partial repaint exists for) reads as a correctness event and every dropped frame becomes a
        // full replay PLUS a full-surface blit, strictly worse than the path it replaced.
        bool carriedFromOldest = acquired && rf.Submit.CarriedFromSeq == 1;

        // Once the consumer has caught up, the carry is DISCHARGED — a later publish must not keep re-damaging bands
        // that were already presented (that would ratchet every frame toward full).
        var third = default(RepaintDamageRegion);
        third.Add(new RectF(100f, 100f, 10f, 10f));
        seam.Publish(stackalloc byte[] { 3 }, noKeys, Info(in third));
        bool discharged = seam.TryAcquire(out var rf3)
            && rf3.Submit.RepaintDamage.Count == 1
            && CoveredBy(rf3.Submit.RepaintDamage, new RectF(100f, 100f, 10f, 10f))
            && rf3.Submit.PublishSequence == 3
            // …and with the carry discharged, the region speaks only for ITSELF again (a stamp that stayed pinned to an
            // old seq would make the device's carry check fire forever after one drop).
            && rf3.Submit.CarriedFromSeq == 3;

        // A forced-full frame that is dropped propagates its REASON forward, not just its (empty) rect list.
        var forced = default(RepaintDamageRegion);
        forced.ForceFull(RepaintFullReason.ImageContent);
        seam.Publish(stackalloc byte[] { 4 }, noKeys, Info(in forced));
        seam.Publish(stackalloc byte[] { 5 }, noKeys, Info(in third));
        bool fullCarried = seam.TryAcquire(out var rf5)
            && rf5.Submit.RepaintDamage.IsFull && rf5.Submit.RepaintDamage.FullReason == RepaintFullReason.ImageContent;

        Check("gate.damage.publish-gap-union Publish stamps the monotonic PublishSequence into FrameInfo and UNIONS the damage of every frame the consumer never acquired into the next one (DropOldest drops frames, not their damage); CarriedFromSeq names the OLDEST seq the carried region speaks for — so a consumer can ask \"was the gap's damage carried?\" instead of the useless \"was there a gap?\" — the carry is discharged once the consumer catches up (and the stamp returns to the frame's own seq), and a dropped forced-full frame propagates its reason",
            carriesBoth && stamped && carriedFromOldest && discharged && fullCarried,
            $"carriesBoth={carriesBoth} stamped={stamped} carriedFrom={carriedFromOldest} discharged={discharged} fullCarried={fullCarried}");
    }

    // ── end-to-end: the payload the device actually receives ───────────────────────────────────────────────────────
    static void HeadlessPayloadChecks(StringTable strings)
    {
        using var fx = new HeadlessFixture(strings, new DamageProbe(), "damage-payload");
        fx.Host.RunFrame();
        var first = fx.Device.LastFrameInfo;
        // The very first frame has an untrustworthy target (nothing was ever presented into it) ⇒ full, named.
        bool firstFull = first.RepaintDamage.IsFull && first.RepaintDamage.FullReason == RepaintFullReason.TargetInvalidated;
        bool firstStamped = first.PublishSequence == 1;

        // E5 (design-engine-images.md): an elided frame is fence-only maintenance, not nothing — each settle turn
        // above took the UI-side skipSubmit branch (headless is always the async gate OFF, never a real render
        // thread), which must reclaim retired GPU resources instead of leaving them stuck behind a fence that will
        // never see another submit while the loop stays idle. One ReclaimCompletedUploads call per elided turn.
        int reclaimBeforeSettle = fx.Device.ReclaimCalls;
        for (int i = 0; i < 4; i++) fx.Host.RunFrame();   // settle (these elide the submit — nothing changed)
        bool reclaimedEverySettleFrame = fx.Device.ReclaimCalls == reclaimBeforeSettle + 4;
        Check("gate.repaint.elided-frame-reclaims every elided settle turn (skip-submit, nothing changed) calls IGpuDevice.ReclaimCompletedUploads exactly once — fence-only maintenance for retired GPU resources, never a reason to force a submit",
            reclaimedEverySettleFrame, $"reclaimCalls={fx.Device.ReclaimCalls} before={reclaimBeforeSettle}");
        int framesBefore = fx.Device.FrameCount;
        ulong seqBefore = fx.Device.LastFrameInfo.PublishSequence;

        // A bound-Fill write: paint-only, no relayout, no image traffic — exactly the "small animator" class §5.1 exists
        // for. The frame MUST submit (the stream changed) and MUST NOT be full: the invalidation cannot latch, and a
        // recolor of one box is a rect, not a window.
        DamageProbe.Tint.Value = 1;
        fx.Host.RunFrame();
        var later = fx.Device.LastFrameInfo;
        bool submitted = fx.Device.FrameCount == framesBefore + 1;
        bool advanced = later.PublishSequence == seqBefore + 1;
        bool partial = !later.RepaintDamage.IsFull && later.RepaintDamage.Count > 0;
        bool bounded = later.RepaintDamage.Coverage(480f, 320f) <= 1f;
        // With nothing dropped the carry stamp must equal the frame's own seq.
        bool carrySelf = later.CarriedFromSeq == later.PublishSequence;

        Check("gate.damage.headless-payload the repaint region + publish sequence cross the render seam into the device's FrameInfo: the first frame is a NAMED full repaint (nothing was ever presented into the target), the stamp is monotonic, a later paint-only write submits a PARTIAL region (the invalidation does not latch), and CarriedFromSeq equals the frame's own seq when nothing was dropped",
            firstFull && firstStamped && submitted && advanced && partial && bounded && carrySelf,
            $"firstFull={first.RepaintDamage.FullReason} seq0={first.PublishSequence} submitted={submitted} advanced={advanced} " +
            $"later={later.RepaintDamage.FullReason}/{later.RepaintDamage.Count} cov={later.RepaintDamage.Coverage(480f, 320f):0.000} seq={later.PublishSequence} " +
            $"carry={later.CarriedFromSeq}");
    }

    // ── span reuse vs a forced FULL re-record ────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// The clean-span reuse path (<c>DrawList.CopySpanFromPrior</c>, and a slice KEPT whole) reuses a
    /// PRIOR frame's recorded bytes for a subtree the recorder believes is unchanged. Every OTHER repaint gate in this
    /// file compares a partial paint against a full replay of the SAME stream, so a stream that is itself missing (or
    /// carrying stale) commands — a span reused for a subtree whose content actually changed, or sliced at a byte range
    /// the stream layout has moved out from under — is invisible to them: both sides of the comparison omit the same
    /// bytes. This gate closes that hole by comparing the reused stream against a FORCED FULL RE-RECORD of the same
    /// scene state (<c>spans: null</c> takes the reuse-free path), byte for byte, every frame.
    /// <para>The fixture is the frame shape the in-window PiP defect lives in, and is deliberately NOT fills-only at
    /// scale 1 (which is all the two sibling video gates model): shadows, gradient fills, images, glyph runs, borders,
    /// rounded corners and a non-unit scale all ride through the same spans, and the sequence interleaves the three
    /// reuse paths — exact copy, TRANSLATED copy (a scrolling viewport) and re-record.</para>
    /// </summary>
    static void SpanReuseEquivalenceChecks(StringTable strings)
    {
        int mismatchFrames = 0;
        string firstMismatch = "";
        long reusedTotal = 0, keptTotal = 0;

        RunAt(1f);
        RunAt(1.5f);

        Check("gate.repaint.span-reuse-equals-full-record the SPAN-REUSED DrawList is byte-identical to a FORCED FULL RE-RECORD of the same scene state, every frame, over the shape the in-window PiP defect lives in: a DrawVideo hole inside a floating surface (rounded, bordered, SHADOWED), transport chrome that stays MOUNTED and mutates EVERY frame INSIDE the hole (seek geometry + elapsed glyph run + an Opacity-channel fade), and unchanged page content OUTSIDE it — a far-left album header and full-width rows carrying glyphs, images, gradients and shadows, all inside the hole's vertical band — while the page SCROLLS (translated-copy reuse), the PiP MOVES, and rows repaint on their own. Every other repaint gate here compares a partial paint against a full replay of the SAME stream, so a span reused for a subtree that actually changed is invisible to them; this one compares the stream itself against the reuse-free path, at scale 1 AND at a non-unit scale, and drives the exact-copy, TRANSLATED-copy and off-screen-subtree-cull paths in turn",
            mismatchFrames == 0, mismatchFrames == 0 ? "" : $"{mismatchFrames} frame(s) diverged; first {firstMismatch}");

        Check("gate.repaint.span-reuse-actually-engaged the equivalence fixture above is only meaningful while span reuse is doing work — a change that silently disabled reuse, or that stopped the content slice from being KEPT whole on frames only its pose moved, would make it pass vacuously",
            reusedTotal > 0 && keptTotal > 0, $"spansReused={reusedTotal} slicesKept={keptTotal}");

        void RunAt(float scale)
        {
            const float TW = 520f, TH = 380f;

            var scene = new SceneStore();
            scene.DeviceScale = scale;
            var root = scene.CreateNode(1); scene.Root = root;
            scene.Bounds(root) = new RectF(0f, 0f, TW, TH);
            { ref NodePaint p = ref scene.Paint(root); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = new ColorF(0.05f, 0.05f, 0.06f, 1f); p.LocalTransform = Affine2D.Scale(scale, scale); }

            // The page: a scrolling viewport (ClipsToBounds + a ScrollState row) over a tall content node. Scrolling it
            // is a composite parameter of the content slice: the rows are kept, not re-recorded.
            var viewport = scene.CreateNode(1); scene.AppendChild(root, viewport);
            scene.Bounds(viewport) = new RectF(0f, 0f, TW, TH);
            { ref NodePaint p = ref scene.Paint(viewport); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = new ColorF(0.07f, 0.07f, 0.09f, 1f); }
            scene.ScrollRef(viewport);   // get-or-create ⇒ marks NodeFlags.Scrollable

            var content = scene.CreateNode(1); scene.AppendChild(viewport, content);
            scene.Bounds(content) = new RectF(0f, 0f, TW, 1400f);
            { ref NodePaint p = ref scene.Paint(content); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = new ColorF(0.08f, 0.08f, 0.10f, 1f); }
            scene.ScrollRef(viewport).ContentNode = content;

            // The album header at the FAR LEFT, inside the vertical band the PiP hole occupies, and full-width rows
            // through the middle of it: the content the reported defect loses.
            AddText(scene, strings, content, new RectF(14f, 150f, 150f, 22f), "Album Header");
            var rows = new NodeHandle[6];
            for (int i = 0; i < rows.Length; i++)
            {
                var n = scene.CreateNode(1); scene.AppendChild(content, n);
                scene.Bounds(n) = new RectF(0f, 24f + i * 46f, TW, 34f);
                ref NodePaint np = ref scene.Paint(n); np = NodePaint.Default;
                np.VisualKind = VisualKind.Box; np.Fill = new ColorF(0.30f, 0.32f, 0.36f, 1f);
                np.BorderWidth = 1f; np.BorderColor = new ColorF(0.5f, 0.5f, 0.55f, 1f);
                np.Corners = new CornerRadius4(4f, 4f, 4f, 4f);
                rows[i] = n;
                if ((i & 1) == 0) scene.SetShadow(n, new ShadowSpec(8f, 2f, 0f, new ColorF(0f, 0f, 0f, 0.4f)));
                if (i == 3) scene.SetGradient(n, GradientSpec.Vertical(new ColorF(0.2f, 0.3f, 0.5f, 1f), new ColorF(0.4f, 0.2f, 0.3f, 1f)));

                // an artwork thumbnail + a track title: image and glyph ops inside the same spans
                var art = scene.CreateNode(1); scene.AppendChild(n, art);
                scene.Bounds(art) = new RectF(6f, 4f, 26f, 26f);
                ref NodePaint ap = ref scene.Paint(art); ap = NodePaint.Default;
                ap.VisualKind = VisualKind.Image; ap.ImageId = 100 + i; ap.Fill = new ColorF(0.2f, 0.2f, 0.24f, 1f);
                AddText(scene, strings, n, new RectF(40f, 6f, 220f, 20f), "track row " + i);
            }

            // Inset cards: unlike the full-width rows (whose shadow halo hangs off both edges of the viewport clip and
            // therefore can never be clip-complete), these sit wholly inside it, so a scroll rebases them through
            // the flatten's per-primitive placement — instead of re-recording them.
            for (int i = 0; i < 4; i++)
            {
                var card = scene.CreateNode(1); scene.AppendChild(content, card);
                scene.Bounds(card) = new RectF(60f, 40f + i * 60f, 160f, 30f);
                ref NodePaint cp2 = ref scene.Paint(card); cp2 = NodePaint.Default;
                cp2.VisualKind = VisualKind.Box; cp2.Fill = new ColorF(0.22f, 0.24f, 0.30f, 1f);
                cp2.Corners = new CornerRadius4(6f, 6f, 6f, 6f);
                scene.SetShadow(card, new ShadowSpec(6f, 2f, 0f, new ColorF(0f, 0f, 0f, 0.35f)));
                AddText(scene, strings, card, new RectF(8f, 6f, 120f, 18f), "card " + i);
            }

            // The in-window floating PiP. The full-bleed pass-through layer is the app's: it paints nothing but its box
            // is the WHOLE window, so it is the node whose reused span reports the video hole to §5.1-A.
            var layer = scene.CreateNode(1); scene.AppendChild(root, layer);
            scene.Bounds(layer) = new RectF(0f, 0f, TW, TH);
            { ref NodePaint p = ref scene.Paint(layer); p = NodePaint.Default; p.VisualKind = VisualKind.Box; }

            var surface = scene.CreateNode(1); scene.AppendChild(layer, surface);
            scene.Bounds(surface) = new RectF(250f, 130f, 240f, 170f);
            { ref NodePaint p = ref scene.Paint(surface); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = ColorF.Transparent; p.Corners = new CornerRadius4(8f, 8f, 8f, 8f); p.BorderWidth = 1f; p.BorderColor = new ColorF(1f, 1f, 1f, 0.2f); }
            scene.Mark(surface, NodeFlags.ClipsToBounds);
            scene.SetShadow(surface, new ShadowSpec(16f, 8f, 0f, new ColorF(0f, 0f, 0f, 0.5f)));

            var video = scene.CreateNode(1); scene.AppendChild(surface, video);
            scene.Bounds(video) = new RectF(0f, 0f, 240f, 170f);
            { ref NodePaint p = ref scene.Paint(video); p = NodePaint.Default; p.VisualKind = VisualKind.Video; p.ImageId = 9; p.Corners = new CornerRadius4(8f, 8f, 8f, 8f); }

            var chrome = scene.CreateNode(1); scene.AppendChild(surface, chrome);
            scene.Bounds(chrome) = new RectF(0f, 130f, 240f, 40f);
            { ref NodePaint p = ref scene.Paint(chrome); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = new ColorF(0f, 0f, 0f, 0.55f); }

            var track = scene.CreateNode(1); scene.AppendChild(chrome, track);
            scene.Bounds(track) = new RectF(10f, 18f, 220f, 4f);
            { ref NodePaint p = ref scene.Paint(track); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = new ColorF(1f, 1f, 1f, 0.3f); }

            var progress = scene.CreateNode(1); scene.AppendChild(chrome, progress);
            scene.Bounds(progress) = new RectF(10f, 18f, 20f, 4f);
            { ref NodePaint p = ref scene.Paint(progress); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = new ColorF(0.1f, 0.85f, 0.45f, 1f); }

            var thumb = scene.CreateNode(1); scene.AppendChild(chrome, thumb);
            scene.Bounds(thumb) = new RectF(28f, 14f, 12f, 12f);
            { ref NodePaint p = ref scene.Paint(thumb); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = new ColorF(1f, 1f, 1f, 1f); p.Corners = new CornerRadius4(6f, 6f, 6f, 6f); }

            var elapsed = AddText(scene, strings, chrome, new RectF(10f, 24f, 60f, 14f), "0:00");

            var dlReuse = new DrawList();
            var dlFull = new DrawList();
            var spans = new SpanTable();
            // Both sides record the way the host does (slice arenas) and are composited by the headless model; the
            // reuse-free ground truth records into its OWN slice arenas (a slice recorder pairs with one span table).
            var reuseRec = new SlicedRecording();
            var fullRec = new SlicedRecording();
            float scrollY = 0f;

            void Frame(string label, Action mutate)
            {
                mutate();
                var st = reuseRec.Record(scene, dlReuse, spans, TW * scale, TH * scale);
                reusedTotal += st.SpansReused;
                keptTotal += st.Slices.Kept;
                // The same scene state, the same dirty bits, but the reuse-free path (spans: null ⇒ no SpanTable ⇒
                // every node is walked and re-emitted). Ground truth for what this frame's stream should contain.
                fullRec.Record(scene, dlFull, null, TW * scale, TH * scale);
                scene.ClearRecordDirty();
                scene.ClearTransformDirty();

                ReadOnlySpan<byte> a = dlReuse.Bytes, b = dlFull.Bytes;
                if (a.Length == b.Length && a.SequenceEqual(b)) return;
                mismatchFrames++;
                if (firstMismatch.Length != 0) return;
                int at = -1, lim = Math.Min(a.Length, b.Length);
                for (int i = 0; i < lim; i++) if (a[i] != b[i]) { at = i; break; }
                firstMismatch = $"scale={scale} {label}: reuse={a.Length}B full={b.Length}B firstDiff@{at} reused={st.SpansReused} kept={st.Slices.Kept}";
            }

            Frame("f1 first paint", () => { });
            Frame("f2 settled", () => { });
            // The steady state of a playing PiP with MOUNTED chrome: the seek bar ticks EVERY frame inside the hole
            // while the page outside it is unchanged and therefore span-reused. The page scrolls in CONSECUTIVE bursts
            // on the SAME frames — a translated copy needs the node's span to have been stored on the IMMEDIATELY
            // preceding frame (SpanTable's `_frame[i] != frameId - 1` recency test), and a settled frame reuses the
            // whole viewport as ONE span without walking its descendants, so a scroll frame with a settled frame in
            // front of it always finds a stale descendant span and re-records instead of rebasing.
            for (int k = 0; k < 60; k++)
            {
                int i = k;
                bool scrolling = i % 12 >= 4 && i % 12 <= 9;
                bool pipMoves = i % 7 == 6;
                bool rowRepaints = i % 9 == 8;
                Frame($"f3.{i} tick{(scrolling ? "+scroll" : "")}{(pipMoves ? "+pip" : "")}{(rowRepaints ? "+row" : "")}", () =>
                {
                    float t = 20f + (i % 20) * 10f;
                    scene.Bounds(progress) = new RectF(10f, 18f, t, 4f);
                    scene.Bounds(thumb) = new RectF(4f + t, 14f, 12f, 12f);
                    scene.Mark(progress, NodeFlags.PaintDirty);
                    scene.Mark(thumb, NodeFlags.PaintDirty);
                    scene.Paint(elapsed).Text = strings.Intern($"0:{(i % 60):00}");
                    scene.Mark(elapsed, NodeFlags.PaintDirty);
                    // the auto-hide fade on the Opacity channel — the chrome stays MOUNTED
                    scene.Paint(chrome).Opacity = 0.55f + 0.4f * MathF.Abs(MathF.Sin(i * 0.21f));
                    scene.Mark(chrome, NodeFlags.PaintDirty);

                    if (scrolling)
                    {
                        // A SMALL offset: every card stays wholly inside the viewport clip, which is what lets the
                        // stationary neighbourhood take the TRANSLATED-copy path instead of re-recording.
                        scrollY -= 3f;
                        if (scrollY < -24f) scrollY = 0f;
                        scene.Paint(content).LocalTransform = Affine2D.Translation(0f, scrollY);
                        scene.Mark(content, NodeFlags.TransformDirty);
                    }
                    if (pipMoves)
                    {
                        scene.Paint(surface).LocalTransform = Affine2D.Translation(-5f * (i / 7 + 1), 3f * (i / 7 + 1));
                        scene.Mark(surface, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                    }
                    if (rowRepaints)
                    {
                        int r = (i / 9) % rows.Length;
                        scene.Paint(rows[r]).Fill = new ColorF(0.30f + 0.02f * r, 0.32f, 0.36f, 1f);
                        scene.Mark(rows[r], NodeFlags.PaintDirty);
                    }
                });
                if (i % 13 == 12) Frame($"f4.{i} settled", () => { });
            }

            // A LONG scroll, on consecutive frames, far enough that rows leave the viewport entirely and come back:
            // the off-screen subtree cull (SpanTable.StoreCulled / TryGetSubtree) and the clip-completeness
            // transitions at the viewport edge, neither of which the small oscillation above reaches.
            for (int step = 0; step < 40; step++)
            {
                int i = step;
                Frame($"f5.{i} long scroll", () =>
                {
                    scrollY = -40f * (i < 20 ? i : 40 - i);
                    scene.Paint(content).LocalTransform = Affine2D.Translation(0f, scrollY);
                    scene.Mark(content, NodeFlags.TransformDirty);
                    scene.Paint(elapsed).Text = strings.Intern($"1:{(i % 60):00}");
                    scene.Mark(elapsed, NodeFlags.PaintDirty);
                });
            }
        }
    }

    static NodeHandle AddText(SceneStore s, StringTable strings, NodeHandle parent, in RectF bounds, string text)
    {
        var n = s.CreateNode(1);
        s.AppendChild(parent, n);
        s.Bounds(n) = bounds;
        ref NodePaint p = ref s.Paint(n);
        p = NodePaint.Default;
        p.VisualKind = VisualKind.Text;
        p.Text = strings.Intern(text);
        return n;
    }

    // ── E1 (design-engine-images.md, "Step 3 — image landings and crossfades damage their nodes") ─────────────────────
    // A1/A3 killed the ancestor-trail and re-pose-latching full-window forces; A2/E1 kill the last two: an image
    // LANDING (ContentEpoch advanced under byte-identical commands) and a live REVEAL/crossfade (pixels advance with
    // ImageClockMs, no dirty bit anywhere) no longer force RepaintFullReason.ImageContent/DetachedContent outright —
    // MarkImageDirty + AddCrossfadeRepaint describe them as the owning node's own band instead. The two named fulls
    // survive only as an EXPLICIT surrender: more than 64 ids landing in one pump (ContentChangedOverflow), or a node
    // this per-node path cannot describe at all (a scaled/rotated ancestor, or the detached-fly slab).
    static void ImageRepaintChecks(StringTable strings)
    {
        const float W = 800f, H = 600f;

        // gate.damage.image-landing-partial / gate.damage.image-crossfade-partial: a real decode landing through
        // AppHost, end to end — GatedDecoder holds the decode Pending until Arm()+Pump() land it on a chosen turn (the
        // 46k fixture, ImageSuite.cs), and ManualFrameTimeSource drives the reveal clock deterministically.
        {
            var decoder = new GatedDecoder();
            var cache = new ImageCache(decoder);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("img-landing", new Size2(W, H), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, new ImageProbe(), cache, frameTime: clock);

            host.RunFrame();                 // frame 1: first frame ever — untrustworthy target, forced full; image Pending
            decoder.Arm();
            host.RunFrame();                 // frame 2 (the Arm frame): the decode LANDS in this pump
            var landedInfo = device.LastFrameInfo;
            bool oneImage = device.LastImages.Count == 1;
            // DrawImageCmd.Rect is LOCAL geometry, not window-space — the actual device/absolute rect (what
            // AddImageNodeRepaint's TryAbsoluteRectTranslationOnly and the recorder's own SubtreeBounds band describe)
            // is Transform.TransformBounds(Rect), exactly like the recorder's own Walk resolves it at record time.
            var imageRect = oneImage ? device.LastImages[0].Transform.TransformBounds(device.LastImages[0].Rect) : default;
            bool landingPartial = oneImage && !landedInfo.RepaintDamage.IsFull
                && CoveredBy(landedInfo.RepaintDamage, imageRect)
                && landedInfo.RepaintDamage.Coverage(W, H) < 0.1f;
            Check("gate.damage.image-landing-partial an image decode landing (Pending→Ready) damages ONLY its own node's band — MarkImageDirty's PaintDirty feeds the recorder's §13.1 block instead of the host forcing RepaintFullReason.ImageContent — so the region is partial, covers the drawn rect, and its coverage of an 800x600 window is a small fraction",
                landingPartial,
                $"oneImage={oneImage} full={landedInfo.RepaintDamage.IsFull}/{landedInfo.RepaintDamage.FullReason} covers={(oneImage && CoveredBy(landedInfo.RepaintDamage, imageRect))} coverage={landedInfo.RepaintDamage.Coverage(W, H):0.0000}");

            // The reveal this landing started is now live (ImageTransition.Default, 220ms). Every tick while it runs
            // must stay partial AND keep covering the image's rect (AddCrossfadeRepaint's per-node band, not a
            // DetachedContent surrender); once it settles, an otherwise-idle frame must elide outright.
            bool everFull = false, everMissesImage = false;
            int ticks = 0;
            while (cache.HasActiveCrossfades && ticks < 60)
            {
                clock.Advance(16f);
                host.RunFrame();
                var r = device.LastFrameInfo.RepaintDamage;
                if (r.IsFull) everFull = true;
                else if (!CoveredBy(r, imageRect)) everMissesImage = true;
                ticks++;
            }
            bool settledWithinBudget = ticks > 0 && ticks < 60;
            int framesBeforeElide = device.FrameCount;
            clock.Advance(300f);   // well past ImageTransition.Default's 220ms — nothing left to describe
            host.RunFrame();
            bool elidesAfterSettle = device.FrameCount == framesBeforeElide;

            Check("gate.damage.image-crossfade-partial every tick while the reveal is live stays PARTIAL and keeps covering the image's rect (AddCrossfadeRepaint's per-node band over RevealingIds, never a DetachedContent surrender) and once HasActiveCrossfades settles, an otherwise-unchanged frame elides the submit outright (FrameCount unchanged)",
                !everFull && !everMissesImage && settledWithinBudget && elidesAfterSettle,
                $"ticks={ticks} everFull={everFull} everMissesImage={everMissesImage} elides={elidesAfterSettle} framesBefore={framesBeforeElide} framesAfter={device.FrameCount}");
        }

        // gate.damage.image-offscreen-landing-elides: a Prefetch handle nothing on screen owns. ImageTransition.None
        // isolates the case from the reveal machinery above — MarkImageDirty finds ZERO owning nodes for this id
        // (Reconciler._imageNodes has no entry), so the landing cannot describe a band, cannot force ImageContent
        // (that id never touched a live draw op), and cannot keep the frame awake either.
        {
            var decoder = new GatedDecoder();
            var cache = new ImageCache(decoder);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("img-offscreen", new Size2(W, H), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new BlankBoxProbe(), cache);

            host.RunFrame();                                   // frame 1: first frame ever — forced full
            for (int i = 0; i < 4; i++) host.RunFrame();        // settle (mirrors gate.damage.headless-payload)

            decoder.Arm();
            cache.Request("offscreen/prefetch.jpg", 8, 8, ImagePriority.Prefetch, transition: ImageTransition.None);
            int framesBefore = device.FrameCount;
            var landed = host.RunFrame();                      // begins AND lands the prefetch decode in this one pump
            bool elided = device.FrameCount == framesBefore;
            bool emptyRegion = landed.RepaintRectCount == 0 && landed.RepaintFullReason == RepaintFullReason.None;

            Check("gate.damage.image-offscreen-landing-elides a Prefetch-only handle nothing on screen owns describes an EMPTY repaint region on landing (never RepaintFullReason.ImageContent/DetachedContent) and the otherwise-idle frame still elides the submit",
                elided && emptyRegion,
                $"elided={elided} rects={landed.RepaintRectCount} reason={landed.RepaintFullReason} frames={device.FrameCount}/{framesBefore}");
        }

        // gate.damage.image-content-overflow-named-full: 70 distinct decodes complete in ONE Pump — past the fixed
        // 64-slot ContentChangedIds capacity, ImageCache sets ContentChangedOverflow instead of growing, and the host's
        // ONLY response to that flag is the named surrender (a per-id describe over 70 nodes is exactly the "over
        // budget" case the itemized path exists to bound).
        {
            var decoder = new GatedDecoder();
            var cache = new ImageCache(decoder);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("img-overflow", new Size2(W, H), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ManyImagesProbe(), cache);

            host.RunFrame();      // frame 1: ManyImagesProbe.Count decodes begin, all Pending
            decoder.Arm();
            host.RunFrame();      // frame 2: all of them land in this ONE Pump ⇒ ContentChangedOverflow
            var region = device.LastFrameInfo.RepaintDamage;

            Check("gate.damage.image-content-overflow-named-full more ids land in one Pump than ContentChangedIds' fixed capacity (64) — ContentChangedOverflow forces the named RepaintFullReason.ImageContent surrender instead of an itemized per-node describe",
                region.IsFull && region.FullReason == RepaintFullReason.ImageContent,
                $"full={region.IsFull} reason={region.FullReason} draws={device.LastImages.Count} count={ManyImagesProbe.Count}");
        }

        // gate.damage.image-crossfade-scaled-ancestor-full: the image's node sits under a ScaleX/ScaleY≠1 ancestor, so
        // SceneStore.TryAbsoluteRectTranslationOnly refuses the whole chain — AddImageNodeRepaint returns -1,
        // AddCrossfadeRepaint returns false, and the caller keeps its named DetachedContent surrender for the frame
        // (over-inclusion, never an under-covering rect for a box this path cannot describe).
        {
            var decoder = new GatedDecoder();
            var cache = new ImageCache(decoder);
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("img-scaled", new Size2(W, H), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ScaledImageProbe(), cache);

            host.RunFrame();      // frame 1: first frame ever — forced full; image Pending
            decoder.Arm();
            host.RunFrame();      // frame 2: the decode lands AND starts a reveal, both under the scaled ancestor
            var region = device.LastFrameInfo.RepaintDamage;

            Check("gate.damage.image-crossfade-scaled-ancestor-full an image node under a ScaleX/ScaleY≠1 ancestor cannot be described as a plain translated rect (TryAbsoluteRectTranslationOnly refuses) — AddCrossfadeRepaint reports failure and the host keeps its named DetachedContent surrender rather than emit an under-covering rect",
                region.IsFull && region.FullReason == RepaintFullReason.DetachedContent,
                $"full={region.IsFull} reason={region.FullReason} draws={device.LastImages.Count}");
        }
    }

    // ── E4 (design-engine-images.md) — VramShedPolicy: hysteresis / cooldown / same-sample suppression, as a pure
    // struct with no ImageCache/AppHost dependency (it decides WHEN to shed; ImageCache.EvictToVramPressure does the
    // shedding). Directly mirrors the M5 defect: a 128 MB LOCAL part reads used > 0.90·budget on essentially every
    // frame, and the device only refreshes its VRAM sample every ~10 presents, so reacting on every frame re-sheds the
    // identical stale overage for up to 10 frames straight before the next real sample arrives.
    static void VramShedPolicyChecks()
    {
        {
            var p = default(VramShedPolicy);
            bool notArmedYet = !p.Armed;
            bool fired = p.Decide(91, 100);   // 91% crosses ArmRatio(90%) on a brand-new sample ⇒ arms AND fires
            Check("gate.img.vram-shed-policy arms and fires the moment usage crosses 90% of budget on a brand-new sample",
                notArmedYet && fired && p.Armed, $"fired={fired} armed={p.Armed}");
        }
        {
            var p = default(VramShedPolicy);
            p.Decide(91, 100);
            bool refired = p.Decide(91, 100);   // the IDENTICAL (used, budget) pair — the exact stale-sample defect
            Check("gate.img.vram-shed-policy re-acting on an unchanged (used, budget) sample never re-fires",
                !refired, $"refired={refired}");
        }
        {
            var p = default(VramShedPolicy);
            p.Decide(91, 100);
            p.NoteShed(1024);   // this frame's eviction actually freed something ⇒ arms the cooldown
            int fireCount = 0;
            bool firedBeforeCooldownDrained = false;
            for (int i = 0; i < VramShedPolicy.CooldownFrames; i++)
            {
                bool fired = p.Decide(92 + i, 100);   // a genuinely NEW, still-armed sample every call
                if (fired)
                {
                    fireCount++;
                    if (i < VramShedPolicy.CooldownFrames - 1) firedBeforeCooldownDrained = true;
                }
            }
            Check("gate.img.vram-shed-policy a shed arms a cooldown (longer than the device's ~10-present sample cadence): no fire on a new sample until it drains, then fires on the very next one",
                fireCount == 1 && !firedBeforeCooldownDrained, $"fireCount={fireCount} early={firedBeforeCooldownDrained}");
        }
        {
            var p = default(VramShedPolicy);
            p.Decide(91, 100);          // arm
            p.Decide(85, 100);          // 85% sits BETWEEN DisarmRatio(80%) and ArmRatio(90%) — hysteresis band
            bool staysArmedAt85 = p.Armed;
            p.Decide(79, 100);          // < 80% ⇒ disarms
            bool disarmsBelow80 = !p.Armed;
            Check("gate.img.vram-shed-policy hysteresis: stays armed at 85% (between the two ratios, so it does not chatter at every sample dithering around 90%) and disarms only once usage falls below 80%",
                staysArmedAt85 && disarmsBelow80, $"staysArmed85={staysArmedAt85} disarmsBelow80={disarmsBelow80}");
        }
    }
}

sealed class DamageProbe : FluentGpu.Hooks.Component
{
    /// <summary>Drives a BOUND Fill (compositor-only, no relayout) so the payload gate can produce a paint-only frame.</summary>
    public static readonly FluentGpu.Signals.Signal<int> Tint = new(0);

    public override FluentGpu.Dsl.Element Render()
        => new FluentGpu.Dsl.BoxEl
        {
            Grow = 1f,
            Fill = FluentGpu.Signals.Prop.Of(() => ColorF.FromRgba((byte)(24 + Tint.Value * 90), 24, 28)),
        };
}

// A plain box with no image at all — the "nothing on screen owns this id" half of
// gate.damage.image-offscreen-landing-elides (a raw ImageCache.Request never reaches this tree).
sealed class BlankBoxProbe : FluentGpu.Hooks.Component
{
    public override FluentGpu.Dsl.Element Render() => new FluentGpu.Dsl.BoxEl { Grow = 1f };
}

// gate.damage.image-content-overflow-named-full: enough distinct decodes to overrun ContentChangedIds' fixed 64-slot
// capacity when they all land in the SAME GatedDecoder Pump.
sealed class ManyImagesProbe : FluentGpu.Hooks.Component
{
    public const int Count = 70;

    public override FluentGpu.Dsl.Element Render()
    {
        var children = new FluentGpu.Dsl.Element[Count];
        for (int i = 0; i < Count; i++) children[i] = FluentGpu.Dsl.Ui.Image($"many/{i}.jpg", 8, 8);
        return new FluentGpu.Dsl.BoxEl { Children = children };
    }
}

// gate.damage.image-crossfade-scaled-ancestor-full: the image sits under a ScaleX/ScaleY≠1 ancestor, so its node's
// absolute rect cannot be described as a plain translated box (SceneStore.TryAbsoluteRectTranslationOnly refuses).
sealed class ScaledImageProbe : FluentGpu.Hooks.Component
{
    public override FluentGpu.Dsl.Element Render()
        => new FluentGpu.Dsl.BoxEl
        {
            Width = 200, Height = 200, ScaleX = 2f, ScaleY = 2f,
            Children = [FluentGpu.Dsl.Ui.Image("scaled/1.jpg", 80, 80, 6f)],
        };
}
