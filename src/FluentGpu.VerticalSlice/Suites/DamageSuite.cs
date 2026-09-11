using System;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;

/// <summary>
/// Repaint damage (gpu-renderer.md §13.1 / architecture-spec "Partial present"), Phase A: the TRUTHFUL repaint set that
/// crosses the render seam inside <see cref="FrameInfo.RepaintDamage"/>. No backend consumes it yet, so these gates are
/// the only thing pinning it — both the pure region algebra and the end-to-end payload the headless device receives.
///
/// The load-bearing invariant throughout: <b>two accumulators, never substituted.</b> <c>FrameInfo.Damage</c> is the
/// acrylic blur-cache union (transform-moved nodes only; scroll content and paint-only writes excluded BY DESIGN);
/// <c>FrameInfo.RepaintDamage</c> is "what pixels must be redrawn". Several gates below assert BOTH sides so a future
/// change cannot quietly collapse them into one.
/// </summary>
static class DamageSuite
{
    public static void Run(StringTable strings)
    {
        RegionMathChecks();
        RecordDamageChecks();
        CompositorDamageChecks();
        PublishGapChecks();
        HeadlessPayloadChecks(strings);
        PolicyChecks();
        StreamSafetyChecks();
        CullHaloChecks();
        VideoRepaintChecks();
        LayeredVideoRepaintChecks();
        TwoHoleRepaintChecks();
        SpanReuseEquivalenceChecks(strings);
        SceneSnapshotChecks.Run();
        PublicationGapChecks.Run();
        CompositorAnimationChecks.Run();
    }

    // ── §5.1-B: the pure decision layer (RepaintPolicy / RepaintStreamSafety / RepaintCull) ──────────────────────────
    // The D3D12 partial-repaint path cannot run headlessly, so everything about it that CAN be a pure function is one,
    // and these gates are the whole safety net for that half. The binding property throughout: every uncertain input
    // resolves to a FULL redraw.
    const float W = 1000f, H = 1000f;   // a 1e6 DIP² target — a rect's area in "% of target" reads directly

    static RepaintRoute Decide(in RepaintDamageRegion r, int layerKind, bool streamSafe, bool canvasValid, bool sizeMatches,
        out ReplayRects rects)
        => RepaintPolicy.Decide(in r, W, H, layerKind, streamSafe, canvasValid, sizeMatches, out rects);

    static RepaintDamageRegion Small()
    {
        var r = default(RepaintDamageRegion);
        r.Add(new RectF(10f, 10f, 50f, 50f));   // 0.25 % coverage
        return r;
    }

    static void PolicyChecks()
    {
        // Every disqualifier forces a FULL redraw, one at a time off an otherwise partial-eligible frame.
        {
            var ok = Decide(Small(), RepaintPolicy.LayerKindNone, true, true, true, out var okRects);
            bool baseline = ok == RepaintRoute.Partial && okRects.Count == 1;

            bool unsafeStream = Decide(Small(), RepaintPolicy.LayerKindNone, false, true, true, out _) == RepaintRoute.FullDirect;
            bool sizeMismatch = Decide(Small(), RepaintPolicy.LayerKindNone, true, true, false, out _) == RepaintRoute.FullDirect;
            bool acrylic = Decide(Small(), RepaintPolicy.LayerKindAcrylic, true, true, true, out _) == RepaintRoute.FullDirect;
            bool unknownKind = Decide(Small(), 7, true, true, true, out _) == RepaintRoute.FullDirect;
            var smallRegion = Small();
            bool degenerate = RepaintPolicy.Decide(in smallRegion, 0f, 0f, RepaintPolicy.LayerKindNone, true, true, true, out _) == RepaintRoute.FullDirect;

            var forced = default(RepaintDamageRegion);
            forced.Add(new RectF(10f, 10f, 50f, 50f));
            forced.ForceFull(RepaintFullReason.ImageContent);
            bool forcedFull = Decide(forced, RepaintPolicy.LayerKindNone, true, true, true, out _) == RepaintRoute.FullDirect;

            Check("gate.repaint.policy-fallbacks EVERY uncertain input forces a FULL redraw off an otherwise partial-eligible frame — unsafe stream, target-size disagreement, an acrylic stream (its backdrop snapshot writes INTO the canvas), an UNKNOWN layer kind, a degenerate target, and a forced-full region",
                baseline && unsafeStream && sizeMismatch && acrylic && unknownKind && degenerate && forcedFull,
                $"baseline={ok}/{okRects.Count} unsafe={unsafeStream} size={sizeMismatch} acrylic={acrylic} unknown={unknownKind} degenerate={degenerate} forced={forcedFull}");
        }

        // Acrylic (kind 2) is full EVEN with a live canvas and empty damage: SnapshotTargetRegion clobbers the canvas.
        {
            var empty = default(RepaintDamageRegion);
            bool emptyToo = Decide(empty, RepaintPolicy.LayerKindAcrylic, true, true, true, out _) == RepaintRoute.FullDirect;
            var half = default(RepaintDamageRegion);
            half.Add(new RectF(0f, 0f, W, H * 0.4f));
            bool bigToo = Decide(half, RepaintPolicy.LayerKindAcrylic, true, true, true, out _) == RepaintRoute.FullDirect;
            Check("gate.repaint.policy-acrylic-always-full an acrylic stream is FullDirect on every input — even empty damage over a live canvas — because the backdrop snapshot physically copies target regions INTO the canvas",
                emptyToo && bigToo, $"empty={emptyToo} small={bigToo}");
        }

        // The coverage cutoff, checked on BOTH sides of the line and BOTH sides of the merge.
        {
            var under = default(RepaintDamageRegion);
            under.Add(new RectF(0f, 0f, W, H * 0.55f));                       // 55 % — under
            bool underPartial = Decide(under, RepaintPolicy.LayerKindNone, true, true, true, out var uRects) == RepaintRoute.Partial
                                && uRects.Count == 1;

            var over = default(RepaintDamageRegion);
            over.Add(new RectF(0f, 0f, W, H * 0.65f));                        // 65 % — over
            bool overFull = Decide(over, RepaintPolicy.LayerKindNone, true, true, true, out _) == RepaintRoute.FullDirect;

            // POST-MERGE: five separated full-height columns total 50 % RAW — under the cutoff — but coalescing five
            // rects down to four must swallow the gap between two of them, and the merged set reaches 62.5 %. A policy
            // that only tested the accumulated rects would run a partial that costs more than a full frame.
            var cols = default(RepaintDamageRegion);
            for (int i = 0; i < 5; i++) cols.Add(new RectF(i * 225f, 0f, 100f, H));   // 5 × 10 % = 50 % raw, gaps of 125
            float rawCoverage = cols.Coverage(W, H);
            bool postMerge = Decide(cols, RepaintPolicy.LayerKindNone, true, true, true, out var tRects) == RepaintRoute.FullDirect
                             && tRects.Count == 0;

            Check("gate.repaint.policy-coverage-cutoff the 60 % cutoff is checked BOTH pre- and post-merge: 55 % stays partial, 65 % goes full, and five separated columns totalling 50 % RAW go FULL because coalescing them to 4 replay rects swallows a gap and reaches 62.5 % (the merge adds dead area — checking only the accumulated rects would authorize a partial that costs more than a full frame)",
                underPartial && overFull && postMerge && rawCoverage < RepaintPolicy.CoverageCutoff,
                $"under={underPartial} over={overFull} raw={rawCoverage:0.000} postMerge={postMerge} tRects={tRects.Count}");
        }

        // Coalescing: ≤ MaxReplayRects, still pairwise disjoint, and the union of the inputs is fully covered.
        {
            var many = default(RepaintDamageRegion);
            for (int i = 0; i < 8; i++) many.Add(new RectF(i * 100f, i * 100f, 20f, 20f));   // 8 separated dots
            var route = Decide(many, RepaintPolicy.LayerKindNone, true, true, true, out var rects);
            bool capped = rects.Count > 0 && rects.Count <= RepaintPolicy.MaxReplayRects;
            bool disjoint = ReplayDisjoint(in rects);
            bool covers = true;
            for (int i = 0; i < many.Count; i++) covers &= CoveredBy(many[i], in rects);
            // Least-waste: merging near neighbours must beat merging far ones, so the merged set's total area stays far
            // below the bounding box of everything.
            bool notOneBigBox = rects.SummedArea() < 700f * 700f;
            Check("gate.repaint.policy-coalesce 8 disjoint damage dots coalesce to <= 4 replay rects that are still PAIRWISE DISJOINT (so no pixel is cleared+replayed twice and SummedArea stays exact) and that COVER every input rect, via least-waste pair merging rather than one bounding box",
                route == RepaintRoute.Partial && capped && disjoint && covers && notOneBigBox,
                $"route={route} count={rects.Count} disjoint={disjoint} covers={covers} area={rects.SummedArea():0}");
        }

        // The layered route collapses to ONE union rect (a group RT is pool-leased ⇒ the stream cannot replay twice).
        {
            var many = default(RepaintDamageRegion);
            many.Add(new RectF(10f, 10f, 20f, 20f));
            many.Add(new RectF(300f, 300f, 20f, 20f));
            many.Add(new RectF(600f, 100f, 20f, 20f));
            var route = Decide(many, RepaintPolicy.LayerKindGroups, true, true, true, out var rects);
            bool one = rects.Count == 1;
            bool spans = one && rects[0].X <= 10f && rects[0].Y <= 10f && rects[0].Right >= 620f && rects[0].Bottom >= 320f;
            // …and the same damage on the STREAMING route keeps its three rects.
            Decide(many, RepaintPolicy.LayerKindNone, true, true, true, out var streamRects);
            Check("gate.repaint.policy-layered-single-rect the LAYERED route (opacity groups) collapses to ONE union rect — a group RT is pool-leased (acquire -> composite -> release) so the stream cannot be replayed twice — while the same damage on the STREAMING route keeps its separate rects",
                route == RepaintRoute.Partial && one && spans && streamRects.Count == 3,
                $"route={route} layered={rects.Count} spans={spans} streaming={streamRects.Count}");
        }

        // Empty damage: blit-only over a live canvas, full redraw without one (FLIP_DISCARD leaves it undefined).
        {
            var empty = default(RepaintDamageRegion);
            var blitOnly = Decide(empty, RepaintPolicy.LayerKindNone, true, canvasValid: true, sizeMatches: true, out var noRects);
            var mustDraw = Decide(empty, RepaintPolicy.LayerKindNone, true, canvasValid: false, sizeMatches: true, out _);
            Check("gate.repaint.policy-empty-damage an empty region blits the RETAINED canvas (Partial with zero replay rects — the upload-forced frame) when the canvas is live, and redraws in full when it is not: a FLIP_DISCARD back buffer is undefined after present, so SOMETHING must be painted",
                blitOnly == RepaintRoute.Partial && noRects.Count == 0 && mustDraw == RepaintRoute.FullDirect,
                $"blitOnly={blitOnly}/{noRects.Count} mustDraw={mustDraw}");
        }

        // An invalid canvas + small damage rebuilds INTO the canvas (so the NEXT frame can go partial); an invalid
        // canvas + big damage stays on the cheapest full frame there is.
        {
            var rebuild = Decide(Small(), RepaintPolicy.LayerKindNone, true, canvasValid: false, sizeMatches: true, out var rRects);
            var big = default(RepaintDamageRegion);
            big.Add(new RectF(0f, 0f, W, H * 0.9f));
            var stayDirect = Decide(big, RepaintPolicy.LayerKindNone, true, canvasValid: false, sizeMatches: true, out _);
            Check("gate.repaint.policy-canvas-rebuild an INVALID canvas plus SMALL damage takes FullIntoCanvas — one full replay whose only purpose is to make the next small-damage frame partial-eligible — while an invalid canvas plus BIG damage stays FullDirect (no blit tax on a frame that could never have gone partial)",
                rebuild == RepaintRoute.FullIntoCanvas && rRects.Count == 0 && stayDirect == RepaintRoute.FullDirect,
                $"rebuild={rebuild}/{rRects.Count} big={stayDirect}");
        }

        // Rects are clamped to the target before anything else: an 8-DIP AA pad hanging off the edge must not inflate
        // coverage, and a rect wholly outside must not become a replay rect.
        {
            var edge = default(RepaintDamageRegion);
            edge.Add(new RectF(-40f, -40f, 80f, 80f));         // three quarters outside the top-left corner
            Decide(edge, RepaintPolicy.LayerKindNone, true, true, true, out var rects);
            bool clamped = rects.Count == 1 && rects[0].X >= 0f && rects[0].Y >= 0f
                           && rects[0].Right <= W && rects[0].Bottom <= H
                           && MathF.Abs(rects[0].W - 40f) < 1e-3f;
            var outside = default(RepaintDamageRegion);
            outside.Add(new RectF(W + 10f, H + 10f, 30f, 30f));
            var outRoute = Decide(outside, RepaintPolicy.LayerKindNone, true, true, true, out var outRects);
            Check("gate.repaint.policy-clamp-to-target replay rects are clamped to the target first, so the AA/effect pad hanging off a screen edge cannot inflate coverage, and damage entirely off-target yields NO replay rect (it degenerates to the retained-canvas blit)",
                clamped && outRoute == RepaintRoute.Partial && outRects.Count == 0,
                $"clamped={clamped} out={outRoute}/{outRects.Count}");
        }

        PixelGridChecks();
        BlitOnlyGuardChecks();
    }

    // ── C1: disjointness has to hold in the space the GPU actually uses ────────────────────────────────────────────
    // Coalesce guarantees pairwise disjointness on CLOSED FLOAT intervals; ToPixel then rounds each rect OUT
    // independently. Any gap in (0, 1) device pixels therefore COLLAPSES — the two rects claim the same device column,
    // which the single ClearRenderTargetView covers ONCE and the two scissored replays composite over TWICE. In a stack
    // with no opaque coats (Wavee's, measured) that is a permanent 1-px double-blend hairline in the retained canvas,
    // re-created every frame the same damage geometry recurs and self-healing never.
    static void PixelGridChecks()
    {
        Span<PixelRect> pix = stackalloc PixelRect[RepaintPolicy.MaxReplayRects];

        // The exact case from the review, on X: A ends at 100.3 (ceil ⇒ 101), B starts at 100.7 (floor ⇒ 100).
        var h = default(RepaintDamageRegion);
        h.Add(new RectF(50f, 10f, 50.3f, 40f));      // A: [50, 100.3)
        h.Add(new RectF(100.7f, 10f, 60f, 40f));     // B: [100.7, 160.7)
        Decide(h, RepaintPolicy.LayerKindNone, true, true, true, out var hRects);
        bool hSeparateInDip = hRects.Count == 2;                          // float-space adjacency keeps them apart…
        int hn = RepaintPolicy.ToPixelRects(hRects.AsSpan(), 1f, (int)W, (int)H, pix);
        bool hFolded = hn == 1 && PixelDisjoint(pix, hn);                 // …pixel space folds the shared column away
        bool hCovers = PixelCovers(hRects.AsSpan(), pix, hn, 1f);

        // The vertical twin — the same arithmetic on the other axis, which is where a stacked-row layout lands.
        var v = default(RepaintDamageRegion);
        v.Add(new RectF(10f, 50f, 40f, 50.3f));      // A: [50, 100.3) in Y
        v.Add(new RectF(10f, 100.7f, 40f, 60f));     // B: [100.7, 160.7) in Y
        Decide(v, RepaintPolicy.LayerKindNone, true, true, true, out var vRects);
        bool vSeparateInDip = vRects.Count == 2;
        int vn = RepaintPolicy.ToPixelRects(vRects.AsSpan(), 1f, (int)W, (int)H, pix);
        bool vFolded = vn == 1 && PixelDisjoint(pix, vn);
        bool vCovers = PixelCovers(vRects.AsSpan(), pix, vn, 1f);

        // …and the fold must not be a sledgehammer: a gap of a WHOLE device pixel leaves two disjoint pixel sets, and
        // keeping them apart is both correct and cheaper than their union, so the merge must NOT fire.
        var wide = default(RepaintDamageRegion);
        wide.Add(new RectF(50f, 10f, 50f, 40f));     // A: [50, 100)
        wide.Add(new RectF(101f, 10f, 60f, 40f));    // B: [101, 161) — one clear device column between them
        Decide(wide, RepaintPolicy.LayerKindNone, true, true, true, out var wRects);
        int wn = RepaintPolicy.ToPixelRects(wRects.AsSpan(), 1f, (int)W, (int)H, pix);
        bool keptApart = wRects.Count == 2 && wn == 2 && PixelDisjoint(pix, wn);

        // A non-unit scale is the case that actually ships (150 % DPI): the same sub-pixel collapse happens at a
        // different DIP gap, so the fold has to be driven by the SCALED arithmetic, not by a DIP-space threshold.
        var scaled = default(RepaintDamageRegion);
        scaled.Add(new RectF(50f, 10f, 50.2f, 40f));   // A: right 100.2 → ×1.5 = 150.3 → ceil 151
        scaled.Add(new RectF(100.6f, 10f, 60f, 40f));  // B: left  100.6 → ×1.5 = 150.9 → floor 150
        Decide(scaled, RepaintPolicy.LayerKindNone, true, true, true, out var sRects);
        int sn = RepaintPolicy.ToPixelRects(sRects.AsSpan(), 1.5f, (int)(W * 1.5f), (int)(H * 1.5f), pix);
        bool scaledFolded = sRects.Count == 2 && sn == 1 && PixelDisjoint(pix, sn);

        // The conversion itself: round OUT on every side (a partially-covered device pixel must be repainted whole,
        // or the AA edge inside it keeps last frame's value) and clamp to the target.
        PixelRect one = RepaintPolicy.ToPixel(new RectF(10.2f, 20.9f, 5.5f, 3.2f), 1f, (int)W, (int)H);
        bool roundsOut = one.Left == 10 && one.Top == 20 && one.Right == 16 && one.Bottom == 25;
        PixelRect off = RepaintPolicy.ToPixel(new RectF(-50f, -50f, 20f, 20f), 1f, (int)W, (int)H);
        bool clampsAway = off.IsEmpty;
        PixelRect edge = RepaintPolicy.ToPixel(new RectF(W - 5f, H - 5f, 500f, 500f), 1f, (int)W, (int)H);
        bool clampsToTarget = edge.Right == (int)W && edge.Bottom == (int)H;

        Check("gate.repaint.pixel-grid-disjoint replay rects are re-disjointed in DEVICE-PIXEL space, not just in float DIPs: two bands 0.4 DIP apart survive Coalesce as SEPARATE rects (adjacency is tested on closed float intervals) and would then round OUT into a shared device column — cleared once, replayed twice, a permanent double-blend hairline — so ToPixelRects folds them into one on BOTH axes and at a non-unit scale, while a gap of a whole device pixel is deliberately kept apart; the conversion rounds OUT on every side and clamps to the target",
            hSeparateInDip && hFolded && hCovers && vSeparateInDip && vFolded && vCovers
            && keptApart && scaledFolded && roundsOut && clampsAway && clampsToTarget,
            $"h={hRects.Count}->{hn}(fold={hFolded},covers={hCovers}) v={vRects.Count}->{vn}(fold={vFolded},covers={vCovers}) " +
            $"keptApart={keptApart} scaled={sRects.Count}->{sn} roundsOut={roundsOut}({one}) clampAway={clampsAway} clampTarget={clampsToTarget}");
    }

    // ── I1: the 0-rect blit-only route is the one place the engine TRUSTS an unenforced invariant ──────────────────
    static void BlitOnlyGuardChecks()
    {
        bool sameStream = RepaintPolicy.BlitOnlyStreamMatches(0xA5A5_1234UL, 0xA5A5_1234UL);
        bool driftCaught = !RepaintPolicy.BlitOnlyStreamMatches(0xA5A5_1234UL, 0xA5A5_1235UL);
        bool frameUnknown = RepaintPolicy.BlitOnlyStreamMatches(0UL, 0xA5A5_1234UL);
        bool canvasUnknown = RepaintPolicy.BlitOnlyStreamMatches(0xA5A5_1234UL, 0UL);
        bool bothUnknown = RepaintPolicy.BlitOnlyStreamMatches(0UL, 0UL);
        // The reason has to be its OWN token: "the region said nothing changed and the bytes disagree" points at a
        // missing damage source, which is a different bug from every other full-repaint cause.
        bool named = RepaintFullReason.EmptyDamageStreamMismatch != RepaintFullReason.None
                     && RepaintFullReason.EmptyDamageStreamMismatch != RepaintFullReason.TargetInvalidated
                     && RepaintFullReason.EmptyDamageStreamMismatch != RepaintFullReason.BackendUnsupported;

        Check("gate.repaint.blit-only-hash-guard the zero-rect route paints NOTHING and blits the retained canvas, which is correct only while \"bytes differ ⇒ the region is non-empty\" — an invariant nothing enforces — so the frame's content fingerprint is CHECKED against the one the canvas was painted from: equal passes, different surrenders one NAMED full frame (EmptyDamageStreamMismatch) instead of freezing a permanent ghost the host's skip-submit hash would then elide forever, and an unstamped hash on either side keeps today's behaviour rather than surrendering every blit-only frame",
            sameStream && driftCaught && frameUnknown && canvasUnknown && bothUnknown && named,
            $"same={sameStream} drift={driftCaught} frameUnknown={frameUnknown} canvasUnknown={canvasUnknown} bothUnknown={bothUnknown} named={named}");
    }

    static bool PixelDisjoint(ReadOnlySpan<PixelRect> p, int n)
    {
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (p[i].Overlaps(in p[j])) return false;
        return true;
    }

    // Every input rect's own rounded-OUT pixel box must sit inside one merged rect: the fold may only ever GROW the
    // covered set (a merge that dropped pixels would leave the damage unrepainted, which is the opposite failure).
    static bool PixelCovers(ReadOnlySpan<RectF> dip, ReadOnlySpan<PixelRect> p, int n, float scale)
    {
        for (int i = 0; i < dip.Length; i++)
        {
            PixelRect want = RepaintPolicy.ToPixel(in dip[i], scale, (int)(W * scale), (int)(H * scale));
            bool inside = false;
            for (int j = 0; j < n && !inside; j++)
                inside = want.Left >= p[j].Left && want.Top >= p[j].Top && want.Right <= p[j].Right && want.Bottom <= p[j].Bottom;
            if (!inside) return false;
        }
        return true;
    }

    static void StreamSafetyChecks()
    {
        var white = new ColorF(1f, 1f, 1f, 1f);
        var id = Affine2D.Identity;

        // Plain content is safe.
        {
            var dl = new DrawList();
            dl.FillRoundRect(new RectF(0f, 0f, 10f, 10f), default, white, id, 1f);
            dl.DrawImage(new RectF(0f, 0f, 10f, 10f), default, 1, true, white, id, 1f, new RectF(0f, 0f, 1f, 1f));
            dl.PushClip(new RectF(0f, 0f, 10f, 10f));
            dl.Shadow(new RectF(0f, 0f, 10f, 10f), default, white, 0f, 2f, 8f, 1f, id, 1f);
            dl.PopClip();
            bool plainSafe = RepaintStreamSafety.Scan(dl.Bytes);
            bool emptySafe = RepaintStreamSafety.Scan(ReadOnlySpan<byte>.Empty);

            // A plain OPACITY group is safe: its pooled RT is cleared this frame and composited back under the clamped
            // scissor, so every texel it reads was written inside the clamp.
            var og = new DrawList();
            og.PushOpacityLayer(new RectF(0f, 0f, 10f, 10f), default, 0.5f);
            og.FillRoundRect(new RectF(0f, 0f, 10f, 10f), default, white, id, 1f);
            og.PopLayer(new RectF(0f, 0f, 10f, 10f));
            bool opacitySafe = RepaintStreamSafety.Scan(og.Bytes);

            Check("gate.repaint.stream-safe plain fills/images/shadows/clips (and an empty stream) survive a damage-clamped replay, and so does a flat OPACITY group — its pooled RT is cleared this frame and composited back under the clamped scissor",
                plainSafe && emptySafe && opacitySafe,
                $"plain={plainSafe} empty={emptySafe} opacity={opacitySafe}");
        }

        // Acrylic / blur / edge-fade (BOTH classes) are unsafe.
        {
            var acr = new DrawList();
            acr.PushLayer(new RectF(0f, 0f, 10f, 10f), default, white, white, 1f, 8f, 0f, 1f);
            acr.PopLayer(new RectF(0f, 0f, 10f, 10f));
            bool acrylicUnsafe = !RepaintStreamSafety.Scan(acr.Bytes);

            var blur = new DrawList();
            blur.PushBlurLayer(new RectF(0f, 0f, 10f, 10f), default, 6f, 1f);
            blur.PopLayer(new RectF(0f, 0f, 10f, 10f));
            bool blurUnsafe = !RepaintStreamSafety.Scan(blur.Bytes);

            // sigma == 0 ⇒ the PLAIN strip-fade class specifically (R12), not the blurred one already covered above.
            var fade = new DrawList();
            fade.PushEdgeFadeLayer(new RectF(0f, 0f, 10f, 10f), new RectF(0f, 0f, 10f, 10f), default, 1f,
                edges: 1, bandL: 4f, bandT: 0f, bandR: 0f, bandB: 0f, falloff: 0, intensity: 1f, blurSigma: 0f);
            fade.PopLayer(new RectF(0f, 0f, 10f, 10f));
            bool fadeUnsafe = !RepaintStreamSafety.Scan(fade.Bytes);

            Check("gate.repaint.stream-unsafe-layers acrylic (snapshot writes INTO the canvas), self-blur (gaussian taps read OUTSIDE the clamp) and edge fade — INCLUDING the plain sigma=0 strip-fade class, unverified under a clamped replay in v1 (R12) — all mark the stream unsafe",
                acrylicUnsafe && blurUnsafe && fadeUnsafe,
                $"acrylic={acrylicUnsafe} blur={blurUnsafe} edgeFade={fadeUnsafe}");
        }

        // An unknown opcode and a truncated payload are unsafe — never guessed past.
        {
            Span<byte> unknown = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(unknown, 9999);
            bool unknownUnsafe = !RepaintStreamSafety.Scan(unknown);

            var dl = new DrawList();
            dl.FillRoundRect(new RectF(0f, 0f, 10f, 10f), default, white, id, 1f);
            byte[] truncated = dl.Bytes.Slice(0, dl.Bytes.Length - 8).ToArray();
            bool truncatedUnsafe = !RepaintStreamSafety.Scan(truncated);

            Check("gate.repaint.stream-unknown-op an unrecognized opcode and a TRUNCATED payload both mark the stream unsafe — a walk that cannot account for every byte must never be allowed to authorize a clamped replay",
                unknownUnsafe && truncatedUnsafe, $"unknown={unknownUnsafe} truncated={truncatedUnsafe}");
        }
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

    static bool ReplayDisjoint(in ReplayRects r)
    {
        for (int i = 0; i < r.Count; i++)
            for (int j = i + 1; j < r.Count; j++)
                if (r[i].Overlaps(r[j])) return false;
        return true;
    }

    static bool CoveredBy(in RectF probe, in ReplayRects rects)
    {
        for (int i = 0; i < rects.Count; i++)
        {
            RectF c = rects[i];
            if (probe.X >= c.X && probe.Y >= c.Y && probe.Right <= c.Right && probe.Bottom <= c.Bottom) return true;
        }
        return false;
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
            var fa = new FrameInfo(new Size2(100, 100), 1f, default, default, 0f, 0, false, a);
            var fb = new FrameInfo(new Size2(100, 100), 1f, default, default, 0f, 0, false, a);
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
            bool acrylicEmpty = painted.Damage.IsEmpty;
            Check("gate.damage.record-paint-only a fill-only write (hover fade / text / recolor) damages the node's band in the REPAINT set — the class the acrylic union deliberately drops — while FrameInfo.Damage stays empty; the two accumulators must not be collapsed",
                repainted && acrylicEmpty && !painted.RepaintDamage.IsFull,
                $"repainted={repainted} acrylicEmpty={acrylicEmpty} full={painted.RepaintDamage.FullReason}");
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
            bool acrylicStillExcluded = scrolled.Damage.IsEmpty;
            Check("gate.damage.record-scroll-viewport a scrolled viewport's content node damages the VIEWPORT rect in the repaint set (not its 4000px content box), while FrameInfo.Damage stays EMPTY — the acrylic union's scroll-content exclusion survives untouched",
                viewportDamaged && acrylicStillExcluded,
                $"viewport={viewportDamaged} acrylicEmpty={acrylicStillExcluded} count={scrolled.RepaintDamage.Count} full={scrolled.RepaintDamage.FullReason}");
        }

        // 6. §13.1 I3 — a node whose ancestor TRANSLATED-span-copied it, and which then moves ON ITS OWN.
        //    CopySpanFromPriorTranslated shifts the ancestor's whole subtree WITHOUT walking one descendant, so every
        //    descendant's stored extent keeps pre-translation coordinates and its stored frame stops advancing. The old
        //    rule padded such a "stale" extent by RepaintUnknownHaloDip — a 32-DIP EFFECT-halo constant — while the
        //    quantity it has to cover is the ancestor's accumulated TRANSLATION, which nothing bounds (a scroll is
        //    hundreds of DIP). The band the node actually vacated (its post-scroll, pre-move position) was then in
        //    NEITHER emitted rect, so the canvas kept its old pixels there — for minutes, in the playing-idle state this
        //    campaign targets. The fallback must place that band from the nearest FRESH ancestor (a proven superset,
        //    since the very translated copy that moved the descendant refreshed the ancestor's own SubtreeBounds), or
        //    surrender ONE named full frame.
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

            // Scroll: the CONTENT node moves, and the whole subtree (card included) rebases through the translated copy.
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
            => new FrameInfo(new Size2(400, 300), 1f, default, default, 0f, 0, false, region);

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

        for (int i = 0; i < 4; i++) fx.Host.RunFrame();   // settle (these elide the submit — nothing changed)
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
        // I1/I2: the two scalars the backend's canvas ledger reads. The content fingerprint must actually be STAMPED
        // (an unstamped hash silently disables the blit-only self-check), it must MOVE when the stream does, and with
        // nothing dropped the carry stamp must equal the frame's own seq.
        bool hashStamped = first.DrawListHash != 0UL && later.DrawListHash != 0UL;
        bool hashTracksStream = later.DrawListHash != first.DrawListHash;
        bool carrySelf = later.CarriedFromSeq == later.PublishSequence;

        Check("gate.damage.headless-payload the repaint region + publish sequence cross the render seam into the device's FrameInfo: the first frame is a NAMED full repaint (nothing was ever presented into the target), the stamp is monotonic, a later paint-only write submits a PARTIAL region (the invalidation does not latch), and the two ledger scalars ride along — a NON-ZERO DrawListHash that moves with the stream (an unstamped one silently disables the blit-only self-check) and a CarriedFromSeq equal to the frame's own seq when nothing was dropped",
            firstFull && firstStamped && submitted && advanced && partial && bounded
            && hashStamped && hashTracksStream && carrySelf,
            $"firstFull={first.RepaintDamage.FullReason} seq0={first.PublishSequence} submitted={submitted} advanced={advanced} " +
            $"later={later.RepaintDamage.FullReason}/{later.RepaintDamage.Count} cov={later.RepaintDamage.Coverage(480f, 320f):0.000} seq={later.PublishSequence} " +
            $"hash={hashStamped}/{hashTracksStream} carry={later.CarriedFromSeq}");
    }

    // ── §5.1-A/§13.1, the STREAMING partial route around a video hole punch ──────────────────────────────────────────
    static void VideoRepaintChecks()
    {
        // A 440x340 DIP target at scale 1 — one raster cell per DIP, so "pixel-exact" is literal, and big enough that
        // a damage band can fit WHOLLY INSIDE the hole (the repaint-pad is 40 DIP on a plain fill, so a smaller hole
        // would make every case a straddler and the interesting one would never be exercised).
        const int TW = 440, TH = 340;

        // ── the reference rasterizer ────────────────────────────────────────────────────────────────────────────────
        // Premultiplied float RGBA, matching the engine's colour contract (premultiplied, linear blend). Two ops
        // matter: a plain fill (src-over) and DrawVideo (DestOut: dst *= 1 - strength).
        static void Blend(float[] c, int x, int y, float r, float g, float b, float a)
        {
            int i = (y * TW + x) * 4;
            c[i] = r + c[i] * (1f - a); c[i + 1] = g + c[i + 1] * (1f - a);
            c[i + 2] = b + c[i + 2] * (1f - a); c[i + 3] = a + c[i + 3] * (1f - a);
        }
        static void Erase(float[] c, int x, int y, float strength)
        {
            int i = (y * TW + x) * 4;
            float k = 1f - strength;
            c[i] *= k; c[i + 1] *= k; c[i + 2] *= k; c[i + 3] *= k;
        }
        static void ClearRect(float[] c, in PixelRect p, in ColorF col)
        {
            for (int y = Math.Max(0, p.Top); y < Math.Min(TH, p.Bottom); y++)
                for (int x = Math.Max(0, p.Left); x < Math.Min(TW, p.Right); x++)
                {
                    int i = (y * TW + x) * 4;
                    c[i] = col.R * col.A; c[i + 1] = col.G * col.A; c[i + 2] = col.B * col.A; c[i + 3] = col.A;
                }
        }

        // Replay the stream into `canvas`, clamped to `scissor` and culled exactly as the backend culls. The byte
        // framing goes through RepaintStreamSafety.TryBodySize — the ONE opcode→size table — so this walker can never
        // disagree with the stream-safety scan about where an op ends.
        static void Replay(float[] canvas, ReadOnlySpan<byte> cmds, PixelRect scissor, bool cullActive, RectF cullRect)
        {
            Span<RectF> clipStack = stackalloc RectF[32];
            int depth = 0;
            RectF clip = RectF.Infinite;
            int pos = 0;
            while (pos + sizeof(int) <= cmds.Length)
            {
                DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
                pos += sizeof(int);
                if (!RepaintStreamSafety.TryBodySize(op, out int body)) return;
                switch (op)
                {
                    case DrawOp.PushClip:
                    {
                        var cc = MemoryMarshal.Read<ClipCmd>(cmds.Slice(pos));
                        clipStack[depth++] = clip;
                        clip = clip.Intersect(cc.DeviceRect);
                        break;
                    }
                    case DrawOp.PopClip:
                        clip = clipStack[--depth];
                        break;
                    case DrawOp.FillRoundRect:
                    {
                        var c = MemoryMarshal.Read<FillRoundRectCmd>(cmds.Slice(pos));
                        if (Cull(c.Rect, c.Transform)) break;
                        Paint(c.Rect, c.Transform, c.Fill, c.Opacity, strength: -1f);
                        break;
                    }
                    case DrawOp.DrawVideo:
                    {
                        var c = MemoryMarshal.Read<DrawVideoCmd>(cmds.Slice(pos));
                        if (c.VideoReady <= 0f || c.Opacity <= 0f) break;
                        if (Cull(c.Dst, c.Transform)) break;
                        Paint(c.Dst, c.Transform, default, c.Opacity, strength: c.VideoReady * c.Opacity);
                        break;
                    }
                }
                pos += body;

                bool Cull(in RectF r, in Affine2D xf)
                {
                    if (!cullActive) return false;
                    RepaintCull.Aabb(r.X, r.Y, r.W, r.H, xf.M11, xf.M12, xf.M21, xf.M22, xf.Dx, xf.Dy,
                        out float l, out float t, out float rr, out float bb);
                    return !RepaintCull.Keep(l, t, rr, bb, RepaintCull.AaHaloDip, in cullRect);
                }

                void Paint(in RectF r, in Affine2D xf, in ColorF fill, float opacity, float strength)
                {
                    RepaintCull.Aabb(r.X, r.Y, r.W, r.H, xf.M11, xf.M12, xf.M21, xf.M22, xf.Dx, xf.Dy,
                        out float l, out float t, out float rr, out float bb);
                    var box = new RectF(l, t, rr - l, bb - t).Intersect(in clip);
                    int x0 = Math.Max(Math.Max(0, scissor.Left), (int)MathF.Round(box.X));
                    int y0 = Math.Max(Math.Max(0, scissor.Top), (int)MathF.Round(box.Y));
                    int x1 = Math.Min(Math.Min(TW, scissor.Right), (int)MathF.Round(box.Right));
                    int y1 = Math.Min(Math.Min(TH, scissor.Bottom), (int)MathF.Round(box.Bottom));
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            if (strength >= 0f) Erase(canvas, x, y, strength);
                            else
                            {
                                float a = fill.A * opacity;
                                Blend(canvas, x, y, fill.R * a, fill.G * a, fill.B * a, a);
                            }
                        }
                }
            }
        }

        // ── the scene: a page of full-width rows + two left-hand cards, then a PiP video hole over the right half,
        // then the PiP's own transport chrome INSIDE the hole (the damage source that only stays a small partial
        // repaint if §5.1-A's inflation is narrowed). Painter order is the app's: page → hole → chrome. ─────────────
        var scene = new SceneStore();
        var root = scene.CreateNode(1); scene.Root = root;
        scene.Bounds(root) = new RectF(0f, 0f, TW, TH);
        ref NodePaint rootPaint = ref scene.Paint(root);
        rootPaint.VisualKind = VisualKind.Box; rootPaint.Fill = new ColorF(0.05f, 0.05f, 0.06f, 1f);

        var rows = new NodeHandle[14];
        for (int i = 0; i < rows.Length; i++)
        {
            var n = scene.CreateNode(1); scene.AppendChild(root, n);
            scene.Bounds(n) = new RectF(0f, 16f + i * 54f, TW, 36f);
            ref NodePaint np = ref scene.Paint(n);
            np.VisualKind = VisualKind.Box; np.Fill = new ColorF(0.30f, 0.32f, 0.36f, 1f);
            rows[i] = n;
        }
        // Two cards at the FAR LEFT, inside the vertical band the hole occupies — the "album header text at the far
        // left" of the reported defect. Nothing about the hole may touch them.
        var leftCard = scene.CreateNode(1); scene.AppendChild(root, leftCard);
        scene.Bounds(leftCard) = new RectF(12f, 140f, 84f, 26f);
        ref NodePaint lcp = ref scene.Paint(leftCard);
        lcp.VisualKind = VisualKind.Box; lcp.Fill = new ColorF(0.92f, 0.90f, 0.88f, 1f);
        var midCard = scene.CreateNode(1); scene.AppendChild(root, midCard);
        scene.Bounds(midCard) = new RectF(120f, 140f, 84f, 26f);
        ref NodePaint mcp = ref scene.Paint(midCard);
        mcp.VisualKind = VisualKind.Box; mcp.Fill = new ColorF(0.92f, 0.90f, 0.88f, 1f);

        var video = scene.CreateNode(1); scene.AppendChild(root, video);
        scene.Bounds(video) = new RectF(220f, 120f, 200f, 160f);
        ref NodePaint vpaint = ref scene.Paint(video);
        vpaint.VisualKind = VisualKind.Video; vpaint.ImageId = 9;

        var chrome = scene.CreateNode(1); scene.AppendChild(root, chrome);      // playhead, INSIDE the hole
        scene.Bounds(chrome) = new RectF(275f, 190f, 90f, 20f);
        ref NodePaint chp = ref scene.Paint(chrome);
        chp.VisualKind = VisualKind.Box; chp.Fill = new ColorF(0.10f, 0.85f, 0.45f, 1f);

        var dl = new DrawList();
        var spans = new SpanTable();
        var clear = new ColorF(0f, 0f, 0f, 0f);        // the app's transparent clear: an unpainted band reads as a hole
        var canvas = new float[TW * TH * 4];            // the RETAINED canvas the partial route paints into
        var reference = new float[TW * TH * 4];         // what a full redraw of the same stream would produce
        bool canvasValid = false;
        var whole = new PixelRect(0, 0, TW, TH);
        string worst = "";
        long worstCells = 0;
        bool everyTouchInflates = true;                 // §5.1-A, asserted on every frame that damages the hole at all
        string inflateDetail = "";

        // One frame of the real loop: record, decide, paint the partial result into `canvas`, paint the full result
        // into `reference`, compare. `mutate` runs before the record, exactly where the app's writes land.
        void Frame(string label, Action mutate)
        {
            mutate();
            var st = SceneRecorder.Record(scene, dl, spans: spans);
            scene.ClearRecordDirty();
            scene.ClearTransformDirty();
            ReadOnlySpan<byte> cmds = dl.Bytes;

            // Reference: clear the whole target, replay the whole stream, cull nothing.
            ClearRect(reference, in whole, in clear);
            Replay(reference, cmds, whole, cullActive: false, default);

            var dmg = st.RepaintDamage;

            // §5.1-A: ANY repaint rect that overlaps a hole Dst must have inflated the region to cover the WHOLE Dst.
            // The backend's DrawOp.DrawVideo decode declines to re-punch a hole outside the replay rect and names this
            // rule as the reason it is allowed to. Read the hole's world rect back out of the stream so the assertion
            // is against the geometry that was actually recorded, not a copy of the scene's.
            if (!dmg.IsFull && TryReadHole(cmds, out RectF holeRect))
            {
                bool touches = false, covered = false;
                for (int i = 0; i < dmg.Count; i++)
                {
                    if (dmg[i].Overlaps(holeRect)) touches = true;
                    if (dmg[i].X <= holeRect.X && dmg[i].Y <= holeRect.Y
                        && dmg[i].Right >= holeRect.Right && dmg[i].Bottom >= holeRect.Bottom) covered = true;
                }
                if (touches && !covered)
                {
                    everyTouchInflates = false;
                    if (inflateDetail.Length == 0) inflateDetail = $"{label}: hole={holeRect} not covered by any of {dmg.Count} repaint rects";
                }
            }

            var route = RepaintPolicy.Decide(in dmg, TW, TH, RepaintPolicy.LayerKindNone,
                streamSafe: true, canvasValid: canvasValid, sizeMatches: true, out var replay);

            if (route != RepaintRoute.Partial)
            {
                ClearRect(canvas, in whole, in clear);
                Replay(canvas, cmds, whole, cullActive: false, default);
                canvasValid = route == RepaintRoute.FullIntoCanvas;
            }
            else
            {
                Span<PixelRect> pix = stackalloc PixelRect[RepaintPolicy.MaxReplayRects];
                int n = RepaintPolicy.ToPixelRects(replay.AsSpan(), 1f, TW, TH, pix);
                for (int i = 0; i < n; i++) ClearRect(canvas, in pix[i], in clear);
                for (int i = 0; i < n; i++)
                {
                    // BeginReplayRect derives the cull box back from the PHYSICAL rect, padded by CullSafetyDip.
                    var cull = new RectF(pix[i].Left - 1f, pix[i].Top - 1f,
                                         pix[i].Right - pix[i].Left + 2f, pix[i].Bottom - pix[i].Top + 2f);
                    Replay(canvas, cmds, pix[i], cullActive: true, cull);
                }
                canvasValid = true;
            }

            long bad = 0;
            for (int i = 0; i < canvas.Length; i++)
                if (MathF.Abs(canvas[i] - reference[i]) > 0.002f) bad++;
            if (bad > worstCells)
            {
                worstCells = bad;
                worst = $"{label}: route={route} rects={replay.Count} badChannels={bad}";
            }
        }

        Frame("f1 first paint", () => { });
        Frame("f2 settled", () => { });
        // The frame a narrowed §5.1-A would turn into a rect wholly INSIDE the hole: a playhead tick.
        Frame("f3 chrome inside hole", () =>
        {
            scene.Paint(chrome).Fill = new ColorF(0.10f, 0.85f, 0.55f, 1f);
            scene.Mark(chrome, NodeFlags.PaintDirty);
        });
        Frame("f4 chrome tick again", () =>
        {
            scene.Paint(chrome).Fill = new ColorF(0.12f, 0.80f, 0.50f, 1f);
            scene.Mark(chrome, NodeFlags.PaintDirty);
        });
        // A full-width row that STRADDLES the hole edge. Its band unions with RepaintBand(hole) into ONE replay rect
        // spanning the whole target at the hole's vertical extent — the exact geometry of the reported defect.
        Frame("f5 straddling row", () =>
        {
            scene.Paint(rows[2]).Fill = new ColorF(0.36f, 0.30f, 0.34f, 1f);
            scene.Mark(rows[2], NodeFlags.PaintDirty);
        });
        Frame("f6 row far below", () =>
        {
            scene.Paint(rows[5]).Fill = new ColorF(0.26f, 0.30f, 0.38f, 1f);
            scene.Mark(rows[5], NodeFlags.PaintDirty);
        });
        Frame("f7 left card only", () =>
        {
            scene.Paint(leftCard).Fill = new ColorF(0.80f, 0.86f, 0.94f, 1f);
            scene.Mark(leftCard, NodeFlags.PaintDirty);
        });
        // The PiP MOVES, then keeps ticking its playhead from the new position: the hole the retained canvas already
        // holds is no longer where the stream says it is, which is the state every "the canvas still holds last
        // frame's premultiplied-zero hole there" argument has to survive.
        for (int step = 1; step <= 4; step++)
        {
            int k = step;
            Frame($"f8.{k} video moves", () =>
            {
                scene.Paint(video).LocalTransform = Affine2D.Translation(-14f * k, 6f * k);
                scene.Mark(video, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                scene.Paint(chrome).LocalTransform = Affine2D.Translation(-14f * k, 6f * k);
                scene.Mark(chrome, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            });
            Frame($"f9.{k} playhead tick", () =>
            {
                scene.Paint(chrome).Fill = new ColorF(0.10f, 0.80f + 0.02f * k, 0.45f, 1f);
                scene.Mark(chrome, NodeFlags.PaintDirty);
            });
            Frame($"f10.{k} settled", () => { });
        }

        Check("gate.repaint.video-hole-partial-equals-full a PARTIAL repaint around a video hole punch paints EXACTLY what a full redraw of the same stream would have. Over a sequence of frames — a playhead tick wholly INSIDE the hole, a full-width row STRADDLING its edge (whose damage unions with RepaintBand(Dst) into one full-width band at the hole's vertical extent), a repaint far from the hole, and the hole MOVING — the retained canvas is compared cell-for-cell against a full replay of the same stream. This is the gate for the class where a damage rect is CLEARED and then something declines to repaint it: the reported defect was exactly that band, left empty, with page content at the far left (nowhere near the hole) missing",
            worstCells == 0, worstCells == 0 ? "" : $"worst {worst}");

        Check("gate.repaint.video-hole-touch-inflates-whole-dst ANY repaint rect that overlaps a DrawVideo Dst inflates the repaint region to cover the WHOLE Dst (SceneRecorder §5.1-A). This is not an optimization detail: the backend's DrawOp.DrawVideo decode declines to re-punch a hole that falls outside the replay rect — the canvas is supposed to still hold last frame's premultiplied-zero hole there — and names this rule as its licence to do so. Narrowing it to \"only a rect that STRADDLES the edge inflates\" removes that licence, so the rule is pinned here rather than left as a comment",
            everyTouchInflates, inflateDetail);
    }

    // ── §5.1-A/§13.1, the LAYERED partial route ─────────────────────────────────────────────────────────────────────
    // The sibling of VideoRepaintChecks for the route the app actually takes while a video plays with auto-hiding
    // transport chrome: a plain (non-acrylic) opacity GROUP in the stream makes RepaintPolicy report LayerKindGroups,
    // which caps the replay at ONE rect and sends the frame through SubmitWithLayers instead of SubmitStreaming.
    static void LayeredVideoRepaintChecks()
    {
        const int TW = 440, TH = 340;
        const int MaxGroups = 4;

        var canvas = new float[TW * TH * 4];
        var reference = new float[TW * TH * 4];
        // The pooled group RTs. Each lease is CANVAS-SIZED and pre-filled with garbage before its Acquire clear, so a
        // composite that reads a pixel the clear never reached shows up as pixel error rather than as luck.
        var groupBufs = new float[MaxGroups][];
        for (int i = 0; i < MaxGroups; i++) groupBufs[i] = new float[TW * TH * 4];
        bool uncleared = false;
        string unclearedDetail = "";

        // ── the scene: the same page + PiP hole as VideoRepaintChecks, plus the auto-hiding transport chrome as a
        // fractional-opacity GROUP (NodePaint.OpacityGroup) over the hole. ────────────────────────────────────────────
        var scene = new SceneStore();
        var root = scene.CreateNode(1); scene.Root = root;
        scene.Bounds(root) = new RectF(0f, 0f, TW, TH);
        ref NodePaint rootPaint = ref scene.Paint(root);
        rootPaint.VisualKind = VisualKind.Box; rootPaint.Fill = new ColorF(0.05f, 0.05f, 0.06f, 1f);

        // A clipping page container — the app's scroll host. Its ClipRect is what makes the group's CompositeClip
        // narrower than the canvas, which is the whole point of the "composite inside the clear" assertion below.
        var page = scene.CreateNode(1); scene.AppendChild(root, page);
        scene.Bounds(page) = new RectF(0f, 8f, TW, 324f);
        ref NodePaint pagePaint = ref scene.Paint(page);
        pagePaint.VisualKind = VisualKind.Box; pagePaint.Fill = new ColorF(0.08f, 0.08f, 0.10f, 1f);
        pagePaint.ClipRect = new RectF(0f, 0f, TW, 324f);

        var rows = new NodeHandle[6];
        for (int i = 0; i < rows.Length; i++)
        {
            var n = scene.CreateNode(1); scene.AppendChild(page, n);
            scene.Bounds(n) = new RectF(0f, 16f + i * 54f, TW, 36f);
            ref NodePaint np = ref scene.Paint(n);
            np.VisualKind = VisualKind.Box; np.Fill = new ColorF(0.30f, 0.32f, 0.36f, 1f);
            rows[i] = n;
        }
        var leftCard = scene.CreateNode(1); scene.AppendChild(page, leftCard);
        scene.Bounds(leftCard) = new RectF(12f, 140f, 84f, 26f);
        ref NodePaint lcp = ref scene.Paint(leftCard);
        lcp.VisualKind = VisualKind.Box; lcp.Fill = new ColorF(0.92f, 0.90f, 0.88f, 1f);

        var video = scene.CreateNode(1); scene.AppendChild(root, video);
        scene.Bounds(video) = new RectF(220f, 120f, 200f, 160f);
        ref NodePaint vpaint = ref scene.Paint(video);
        vpaint.VisualKind = VisualKind.Video; vpaint.ImageId = 9;

        // The transport chrome: MOUNTED and fading on the Opacity channel, as a flat group.
        var chrome = scene.CreateNode(2); scene.AppendChild(root, chrome);
        scene.Bounds(chrome) = new RectF(232f, 236f, 176f, 34f);
        ref NodePaint chp = ref scene.Paint(chrome);
        chp.VisualKind = VisualKind.Box; chp.Fill = new ColorF(0.06f, 0.06f, 0.08f, 0.85f);
        chp.OpacityGroup = true; chp.Opacity = 0.5f;

        var bar = scene.CreateNode(1); scene.AppendChild(chrome, bar);
        scene.Bounds(bar) = new RectF(240f, 248f, 120f, 6f);
        ref NodePaint barPaint = ref scene.Paint(bar);
        barPaint.VisualKind = VisualKind.Box; barPaint.Fill = new ColorF(0.10f, 0.85f, 0.45f, 1f);

        var knob = scene.CreateNode(1); scene.AppendChild(chrome, knob);
        scene.Bounds(knob) = new RectF(240f, 258f, 22f, 22f);
        ref NodePaint knobPaint = ref scene.Paint(knob);
        knobPaint.VisualKind = VisualKind.Box; knobPaint.Fill = new ColorF(0.95f, 0.95f, 0.95f, 1f);

        var dl = new DrawList();
        var spans = new SpanTable();
        var clear = new ColorF(0f, 0f, 0f, 0f);
        bool canvasValid = false;
        var whole = new PixelRect(0, 0, TW, TH);
        string worst = "";
        long worstCells = 0;
        int layeredFrames = 0, partialFrames = 0;
        bool sawGroup = false;

        static void Blend(float[] c, int i, float r, float g, float b, float a)
        {
            c[i] = r + c[i] * (1f - a); c[i + 1] = g + c[i + 1] * (1f - a);
            c[i + 2] = b + c[i + 2] * (1f - a); c[i + 3] = a + c[i + 3] * (1f - a);
        }
        static void ClearBox(float[] c, int l, int t, int r, int b, float cr, float cg, float cb, float ca)
        {
            for (int y = Math.Max(0, t); y < Math.Min(TH, b); y++)
                for (int x = Math.Max(0, l); x < Math.Min(TW, r); x++)
                {
                    int i = (y * TW + x) * 4;
                    c[i] = cr * ca; c[i + 1] = cg * ca; c[i + 2] = cb * ca; c[i + 3] = ca;
                }
        }
        static void FillAll(float[] c, float r, float g, float b, float a)
        {
            for (int i = 0; i < c.Length; i += 4) { c[i] = r; c[i + 1] = g; c[i + 2] = b; c[i + 3] = a; }
        }

        // The layered replay: PushLayer leases a canvas-sized group buffer (garbage-filled, then cleared over exactly
        // the box EdgeFadeLayerClear.Compute names), drawing redirects into it, and PopLayer composites it back over
        // the parent target at GroupAlpha under (clip ∩ scissor ∩ CompositeClip).
        void ReplayLayered(float[] target, ReadOnlySpan<byte> cmds, PixelRect scissor, bool cullActive, RectF cullRect)
        {
            Span<RectF> clipStack = stackalloc RectF[32];
            int depth = 0;
            RectF clip = RectF.Infinite;
            Span<int> clearedL = stackalloc int[MaxGroups];
            Span<int> clearedT = stackalloc int[MaxGroups];
            Span<int> clearedR = stackalloc int[MaxGroups];
            Span<int> clearedB = stackalloc int[MaxGroups];
            Span<PushLayerCmd> groupCmds = stackalloc PushLayerCmd[MaxGroups];
            int groups = 0;
            int pos = 0;
            while (pos + sizeof(int) <= cmds.Length)
            {
                DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
                pos += sizeof(int);
                if (!RepaintStreamSafety.TryBodySize(op, out int body)) break;
                float[] dst = groups > 0 ? groupBufs[groups - 1] : target;
                switch (op)
                {
                    case DrawOp.PushClip:
                    {
                        var cc = MemoryMarshal.Read<ClipCmd>(cmds.Slice(pos));
                        clipStack[depth++] = clip;
                        clip = clip.Intersect(cc.DeviceRect);
                        break;
                    }
                    case DrawOp.PopClip:
                        clip = clipStack[--depth];
                        break;
                    case DrawOp.PushLayer:
                    {
                        var layer = MemoryMarshal.Read<PushLayerCmd>(cmds.Slice(pos));
                        if (groups < MaxGroups)
                        {
                            FillAll(groupBufs[groups], 1f, 0f, 1f, 1f);   // GARBAGE: any read past the clear is visible
                            EdgeFadeLayerClear.Compute(in layer, 1f, TW, TH, out int cl, out int ct, out int cr, out int cb, out bool fullCanvas);
                            if (layer.CompositeClip.W <= 0f || layer.CompositeClip.H <= 0f || fullCanvas || cr <= cl || cb <= ct)
                            { cl = 0; ct = 0; cr = TW; cb = TH; }
                            ClearBox(groupBufs[groups], cl, ct, cr, cb, 0f, 0f, 0f, 0f);
                            clearedL[groups] = cl; clearedT[groups] = ct; clearedR[groups] = cr; clearedB[groups] = cb;
                            groupCmds[groups] = layer;
                            groups++;
                        }
                        break;
                    }
                    case DrawOp.PopLayer:
                    {
                        if (groups == 0) break;
                        groups--;
                        var layer = groupCmds[groups];
                        float[] src = groupBufs[groups];
                        float[] under = groups > 0 ? groupBufs[groups - 1] : target;
                        PixelRect cp = RepaintPolicy.ToPixel(in clip, 1f, TW, TH);
                        int l = Math.Max(cp.Left, scissor.Left), t = Math.Max(cp.Top, scissor.Top);
                        int r = Math.Min(cp.Right, scissor.Right), b = Math.Min(cp.Bottom, scissor.Bottom);
                        if (layer.CompositeClip.W > 0f && layer.CompositeClip.H > 0f)
                        {
                            PixelRect cc = RepaintPolicy.ToPixel(layer.CompositeClip, 1f, TW, TH);
                            l = Math.Max(l, cc.Left); t = Math.Max(t, cc.Top);
                            r = Math.Min(r, cc.Right); b = Math.Min(b, cc.Bottom);
                        }
                        if (r <= l || b <= t) break;
                        if (l < clearedL[groups] || t < clearedT[groups] || r > clearedR[groups] || b > clearedB[groups])
                        {
                            if (!uncleared)
                                unclearedDetail = $"composite ({l},{t},{r},{b}) outside cleared ({clearedL[groups]},{clearedT[groups]},{clearedR[groups]},{clearedB[groups]})";
                            uncleared = true;
                        }
                        float ga = Math.Clamp(layer.GroupAlpha, 0f, 1f);
                        for (int y = t; y < b; y++)
                            for (int x = l; x < r; x++)
                            {
                                int i = (y * TW + x) * 4;
                                Blend(under, i, src[i] * ga, src[i + 1] * ga, src[i + 2] * ga, src[i + 3] * ga);
                            }
                        break;
                    }
                    case DrawOp.FillRoundRect:
                    {
                        var c = MemoryMarshal.Read<FillRoundRectCmd>(cmds.Slice(pos));
                        if (!Cull(c.Rect, c.Transform)) Paint(dst, c.Rect, c.Transform, c.Fill, c.Opacity, strength: -1f);
                        break;
                    }
                    case DrawOp.DrawVideo:
                    {
                        var c = MemoryMarshal.Read<DrawVideoCmd>(cmds.Slice(pos));
                        if (c.VideoReady > 0f && c.Opacity > 0f && !Cull(c.Dst, c.Transform))
                            Paint(dst, c.Dst, c.Transform, default, c.Opacity, strength: c.VideoReady * c.Opacity);
                        break;
                    }
                }
                pos += body;
            }

            bool Cull(in RectF r, in Affine2D xf)
            {
                if (!cullActive) return false;
                RepaintCull.Aabb(r.X, r.Y, r.W, r.H, xf.M11, xf.M12, xf.M21, xf.M22, xf.Dx, xf.Dy,
                    out float l, out float t, out float rr, out float bb);
                return !RepaintCull.Keep(l, t, rr, bb, RepaintCull.AaHaloDip, in cullRect);
            }

            void Paint(float[] c, in RectF r, in Affine2D xf, in ColorF fill, float opacity, float strength)
            {
                RepaintCull.Aabb(r.X, r.Y, r.W, r.H, xf.M11, xf.M12, xf.M21, xf.M22, xf.Dx, xf.Dy,
                    out float l, out float t, out float rr, out float bb);
                var box = new RectF(l, t, rr - l, bb - t).Intersect(in clip);
                int x0 = Math.Max(Math.Max(0, scissor.Left), (int)MathF.Round(box.X));
                int y0 = Math.Max(Math.Max(0, scissor.Top), (int)MathF.Round(box.Y));
                int x1 = Math.Min(Math.Min(TW, scissor.Right), (int)MathF.Round(box.Right));
                int y1 = Math.Min(Math.Min(TH, scissor.Bottom), (int)MathF.Round(box.Bottom));
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = (y * TW + x) * 4;
                        if (strength >= 0f)
                        {
                            float k = 1f - strength;
                            c[i] *= k; c[i + 1] *= k; c[i + 2] *= k; c[i + 3] *= k;
                        }
                        else
                        {
                            float a = fill.A * opacity;
                            Blend(c, i, fill.R * a, fill.G * a, fill.B * a, a);
                        }
                    }
            }
        }

        void Frame(string label, Action mutate)
        {
            mutate();
            var st = SceneRecorder.Record(scene, dl, spans: spans);
            scene.ClearRecordDirty();
            scene.ClearTransformDirty();
            ReadOnlySpan<byte> cmds = dl.Bytes;

            if (dl.OpcodeStats.PushLayer > 0) sawGroup = true;
            int layerKind = dl.OpcodeStats.PushLayer > 0 ? RepaintPolicy.LayerKindGroups : RepaintPolicy.LayerKindNone;
            if (layerKind == RepaintPolicy.LayerKindGroups) layeredFrames++;
            bool streamSafe = RepaintStreamSafety.Scan(cmds);

            ClearBox(reference, 0, 0, TW, TH, clear.R, clear.G, clear.B, clear.A);
            ReplayLayered(reference, cmds, whole, cullActive: false, default);

            var dmg = st.RepaintDamage;
            var route = RepaintPolicy.Decide(in dmg, TW, TH, layerKind,
                streamSafe: streamSafe, canvasValid: canvasValid, sizeMatches: true, out var replay);

            if (route != RepaintRoute.Partial)
            {
                ClearBox(canvas, 0, 0, TW, TH, clear.R, clear.G, clear.B, clear.A);
                ReplayLayered(canvas, cmds, whole, cullActive: false, default);
                canvasValid = route == RepaintRoute.FullIntoCanvas;
            }
            else
            {
                partialFrames++;
                Span<PixelRect> pix = stackalloc PixelRect[RepaintPolicy.MaxReplayRects];
                int n = RepaintPolicy.ToPixelRects(replay.AsSpan(), 1f, TW, TH, pix);
                for (int i = 0; i < n; i++) ClearBox(canvas, pix[i].Left, pix[i].Top, pix[i].Right, pix[i].Bottom, clear.R, clear.G, clear.B, clear.A);
                for (int i = 0; i < n; i++)
                {
                    var cull = new RectF(pix[i].Left - 1f, pix[i].Top - 1f,
                                         pix[i].Right - pix[i].Left + 2f, pix[i].Bottom - pix[i].Top + 2f);
                    ReplayLayered(canvas, cmds, pix[i], cullActive: true, cull);
                }
                canvasValid = true;
            }

            long bad = 0;
            for (int i = 0; i < canvas.Length; i++)
                if (MathF.Abs(canvas[i] - reference[i]) > 0.002f) bad++;
            if (bad > worstCells)
            {
                worstCells = bad;
                worst = $"{label}: route={route} layerKind={layerKind} rects={replay.Count} badChannels={bad}";
            }
        }

        Frame("L1 first paint", () => { });
        Frame("L2 settled", () => { });
        for (int step = 1; step <= 5; step++)
        {
            float a = 0.5f - 0.08f * step;
            Frame($"L3.{step} chrome fade a={a:0.00}", () =>
            {
                scene.Paint(chrome).Opacity = a;
                scene.Mark(chrome, NodeFlags.PaintDirty);
            });
        }
        Frame("L4 chrome back up", () =>
        {
            scene.Paint(chrome).Opacity = 0.5f;
            scene.Mark(chrome, NodeFlags.PaintDirty);
        });
        Frame("L5 straddling row", () =>
        {
            scene.Paint(rows[2]).Fill = new ColorF(0.36f, 0.30f, 0.34f, 1f);
            scene.Mark(rows[2], NodeFlags.PaintDirty);
        });
        Frame("L6 row + chrome fade", () =>
        {
            scene.Paint(rows[2]).Fill = new ColorF(0.34f, 0.31f, 0.33f, 1f);
            scene.Mark(rows[2], NodeFlags.PaintDirty);
            scene.Paint(chrome).Opacity = 0.34f;
            scene.Mark(chrome, NodeFlags.PaintDirty);
        });
        Frame("L7 left card only", () =>
        {
            scene.Paint(leftCard).Fill = new ColorF(0.80f, 0.86f, 0.94f, 1f);
            scene.Mark(leftCard, NodeFlags.PaintDirty);
        });
        Frame("L8 row far below", () =>
        {
            scene.Paint(rows[5]).Fill = new ColorF(0.26f, 0.30f, 0.38f, 1f);
            scene.Mark(rows[5], NodeFlags.PaintDirty);
        });
        for (int step = 1; step <= 4; step++)
        {
            int k = step;
            Frame($"L9.{k} video moves", () =>
            {
                var xf = Affine2D.Translation(-14f * k, 6f * k);
                scene.Paint(video).LocalTransform = xf;
                scene.Mark(video, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                scene.Paint(chrome).LocalTransform = xf;
                scene.Mark(chrome, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            });
            Frame($"L10.{k} chrome fade", () =>
            {
                scene.Paint(chrome).Opacity = 0.5f - 0.07f * k;
                scene.Mark(chrome, NodeFlags.PaintDirty);
            });
            Frame($"L11.{k} settled", () => { });
        }

        Check("gate.repaint.video-hole-layered-partial-equals-full a PARTIAL repaint on the LAYERED route (a plain PushLayer Opacity group in the stream => RepaintPolicy.LayerKindGroups => ONE replay rect through SubmitWithLayers) paints EXACTLY what a full layered redraw of the same stream would have. The fixture is the shape the app took when MediaPlayerElement stopped UNMOUNTING its auto-hiding transport chrome and started fading it on the Opacity channel instead: a video hole punch, a fractional-opacity chrome group over it, a clipping page container, and full-width rows whose damage unions with RepaintBand(Dst) into one full-width band at the hole's vertical extent. Each group lease is pre-filled with GARBAGE so any composite that reads past its Acquire clear shows up as pixel error, and the retained canvas is compared cell-for-cell against the full replay every frame",
            worstCells == 0 && sawGroup && layeredFrames > 0 && partialFrames > 0,
            worstCells != 0 ? $"worst {worst}"
                            : (sawGroup && layeredFrames > 0 && partialFrames > 0 ? ""
                               : $"fixture never exercised the route: group={sawGroup} layered={layeredFrames} partial={partialFrames}"));

        Check("gate.repaint.opacity-group-composite-inside-clear a plain opacity group's COMPOSITE box (CompositeClip intersect CurrentScissorRect) is always a subset of the box its Acquire cleared. The pooled lease is canvas-sized and holds a previous group's pixels everywhere the clear did not reach, so a composite that reaches past the clear samples another layer's content — the property every \"scissor is a subset of cleared\" claim in OpacityLayerCompositor rests on, asserted on the partial route as well as the full one",
            !uncleared, unclearedDetail);
    }

    // ── §5.1-A with MORE THAN ONE hole: the inflation must reach a FIXPOINT ──────────────────────────────────────────
    static void TwoHoleRepaintChecks()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1); scene.Root = root;
        scene.Bounds(root) = new RectF(0f, 0f, 440f, 340f);
        ref NodePaint rootPaint = ref scene.Paint(root);
        rootPaint.VisualKind = VisualKind.Box; rootPaint.Fill = new ColorF(0.05f, 0.05f, 0.06f, 1f);

        // Two holes close enough that inflating the SECOND grows a rect back over the FIRST — the state a single
        // walk of the hole set leaves OVERLAPPED but not COVERED.
        var holeA = scene.CreateNode(1); scene.AppendChild(root, holeA);
        scene.Bounds(holeA) = new RectF(120f, 60f, 80f, 60f);
        ref NodePaint ap = ref scene.Paint(holeA);
        ap.VisualKind = VisualKind.Video; ap.ImageId = 7;

        var holeB = scene.CreateNode(1); scene.AppendChild(root, holeB);
        scene.Bounds(holeB) = new RectF(200f, 40f, 120f, 100f);
        ref NodePaint bp = ref scene.Paint(holeB);
        bp.VisualKind = VisualKind.Video; bp.ImageId = 8;

        // A tiny playhead beside hole B: the ONE damage source, small enough that only the inflation can reach A.
        var tick = scene.CreateNode(1); scene.AppendChild(root, tick);
        scene.Bounds(tick) = new RectF(300f, 60f, 20f, 20f);
        ref NodePaint tp = ref scene.Paint(tick);
        tp.VisualKind = VisualKind.Box; tp.Fill = new ColorF(0.10f, 0.80f, 0.40f, 1f);

        var dl = new DrawList();
        var spans = new SpanTable();
        SceneRecorder.Record(scene, dl, spans: spans);
        scene.ClearRecordDirty(); scene.ClearTransformDirty();
        SceneRecorder.Record(scene, dl, spans: spans);
        scene.ClearRecordDirty(); scene.ClearTransformDirty();

        scene.Paint(tick).Fill = new ColorF(0.10f, 0.85f, 0.45f, 1f);
        scene.Mark(tick, NodeFlags.PaintDirty);
        var st = SceneRecorder.Record(scene, dl, spans: spans);
        scene.ClearRecordDirty(); scene.ClearTransformDirty();

        var dmg = st.RepaintDamage;
        Span<RectF> holes = stackalloc RectF[RepaintPolicy.MaxReplayRects];
        int holeCount = ReadHoles(dl.Bytes, holes);
        bool ok = !dmg.IsFull;
        string detail = holeCount == 2 ? "" : $"expected 2 holes in the stream, got {holeCount}";
        if (holeCount != 2) ok = false;
        for (int i = 0; i < holeCount && ok; i++)
        {
            bool touches = false, covered = false;
            for (int j = 0; j < dmg.Count; j++)
            {
                if (dmg[j].Overlaps(in holes[i])) touches = true;
                if (dmg[j].X <= holes[i].X && dmg[j].Y <= holes[i].Y
                    && dmg[j].Right >= holes[i].Right && dmg[j].Bottom >= holes[i].Bottom) covered = true;
            }
            if (touches && !covered)
            {
                ok = false;
                detail = $"hole[{i}]={holes[i]} is OVERLAPPED but not COVERED by any of {dmg.Count} repaint rects";
            }
        }

        Check("gate.repaint.video-hole-multi-inflation-is-a-fixpoint with MORE THAN ONE DrawVideo hole in the frame, §5.1-A holds for EVERY hole: inflating the repaint region for one hole UNIONS its band in, which grows a rect until it can reach a hole that was already tested and passed. A single walk of the hole set is therefore not a fixpoint, and the second hole ends up OVERLAPPED but not COVERED — precisely the state D3D12Device's DrawOp.DrawVideo decode is allowed to assume never happens (it skips a hole outside the replay rect because \"a hole that is repainted at all is repainted completely\"), so it re-erases only the sliver inside the rect and leaves a torn seam along the DComp visual's edge. Two holes in one frame is a real frame: a windowed popup's RecordSubtree pass APPENDS into the same hole set as the main pass",
            ok, detail);
    }

    /// <summary>Every fully-erasing <c>DrawVideo</c> world rect in a stream, through the shared opcode→size table.</summary>
    static int ReadHoles(ReadOnlySpan<byte> cmds, Span<RectF> dst)
    {
        int n = 0, pos = 0;
        while (pos + sizeof(int) <= cmds.Length && n < dst.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
            pos += sizeof(int);
            if (!RepaintStreamSafety.TryBodySize(op, out int body)) return n;
            if (pos + body > cmds.Length) return n;
            if (op == DrawOp.DrawVideo)
            {
                var v = MemoryMarshal.Read<DrawVideoCmd>(cmds.Slice(pos));
                if (v.VideoReady > 0f && v.Opacity > 0f)
                {
                    RepaintCull.Aabb(v.Dst.X, v.Dst.Y, v.Dst.W, v.Dst.H,
                        v.Transform.M11, v.Transform.M12, v.Transform.M21, v.Transform.M22,
                        v.Transform.Dx, v.Transform.Dy, out float l, out float t, out float r, out float b);
                    dst[n++] = new RectF(l, t, r - l, b - t);
                }
            }
            pos += body;
        }
        return n;
    }

    /// <summary>The world rect of the first fully-erasing <c>DrawVideo</c> in a stream, read back through the shared
    /// opcode→size table so the assertion is made against the geometry that was actually RECORDED.</summary>
    static bool TryReadHole(ReadOnlySpan<byte> cmds, out RectF world)
    {
        world = default;
        int pos = 0;
        while (pos + sizeof(int) <= cmds.Length)
        {
            DrawOp op = (DrawOp)MemoryMarshal.Read<int>(cmds.Slice(pos));
            pos += sizeof(int);
            if (!RepaintStreamSafety.TryBodySize(op, out int body)) return false;
            if (pos + body > cmds.Length) return false;
            if (op == DrawOp.DrawVideo)
            {
                var v = MemoryMarshal.Read<DrawVideoCmd>(cmds.Slice(pos));
                if (v.VideoReady > 0f && v.Opacity > 0f)
                {
                    RepaintCull.Aabb(v.Dst.X, v.Dst.Y, v.Dst.W, v.Dst.H,
                        v.Transform.M11, v.Transform.M12, v.Transform.M21, v.Transform.M22,
                        v.Transform.Dx, v.Transform.Dy, out float l, out float t, out float r, out float b);
                    world = new RectF(l, t, r - l, b - t);
                    return true;
                }
            }
            pos += body;
        }
        return false;
    }

    // ── span reuse vs a forced FULL re-record ────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// The clean-span reuse path (<c>SceneRecorder.CopySpanFromPrior</c> / <c>CopySpanFromPriorTranslated</c>) copies a
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
        long reusedTotal = 0, rebasedTotal = 0;

        RunAt(1f);
        RunAt(1.5f);

        Check("gate.repaint.span-reuse-equals-full-record the SPAN-REUSED DrawList is byte-identical to a FORCED FULL RE-RECORD of the same scene state, every frame, over the shape the in-window PiP defect lives in: a DrawVideo hole inside a floating surface (rounded, bordered, SHADOWED), transport chrome that stays MOUNTED and mutates EVERY frame INSIDE the hole (seek geometry + elapsed glyph run + an Opacity-channel fade), and unchanged page content OUTSIDE it — a far-left album header and full-width rows carrying glyphs, images, gradients and shadows, all inside the hole's vertical band — while the page SCROLLS (translated-copy reuse), the PiP MOVES, and rows repaint on their own. Every other repaint gate here compares a partial paint against a full replay of the SAME stream, so a span reused for a subtree that actually changed is invisible to them; this one compares the stream itself against the reuse-free path, at scale 1 AND at a non-unit scale, and drives the exact-copy, TRANSLATED-copy and off-screen-subtree-cull paths in turn",
            mismatchFrames == 0, mismatchFrames == 0 ? "" : $"{mismatchFrames} frame(s) diverged; first {firstMismatch}");

        Check("gate.repaint.span-reuse-actually-engaged the equivalence fixture above is only meaningful while span reuse is doing work — a change that silently disabled reuse, or that stopped the scroll from producing TRANSLATED copies, would make it pass vacuously",
            reusedTotal > 0 && rebasedTotal > 0, $"spansReused={reusedTotal} spansRebased={rebasedTotal}");

        void RunAt(float scale)
        {
            const float TW = 520f, TH = 380f;

            var scene = new SceneStore();
            scene.DeviceScale = scale;
            var root = scene.CreateNode(1); scene.Root = root;
            scene.Bounds(root) = new RectF(0f, 0f, TW, TH);
            { ref NodePaint p = ref scene.Paint(root); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = new ColorF(0.05f, 0.05f, 0.06f, 1f); p.LocalTransform = Affine2D.Scale(scale, scale); }

            // The page: a scrolling viewport (ClipsToBounds + a ScrollState row) over a tall content node. Scrolling it
            // is what drives CopySpanFromPriorTranslated for the stationary neighbourhood.
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
            // CopySpanFromPriorTranslated — the payload-patching path — instead of re-recording them.
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
            float scrollY = 0f;

            void Frame(string label, Action mutate)
            {
                mutate();
                var st = SceneRecorder.Record(scene, dlReuse, spans: spans);
                reusedTotal += st.SpansReused;
                rebasedTotal += st.SpansRebased;
                // The same scene state, the same dirty bits, but the reuse-free path (spans: null ⇒ no SpanTable ⇒
                // every node is walked and re-emitted). Ground truth for what this frame's stream should contain.
                SceneRecorder.Record(scene, dlFull, spans: null);
                scene.ClearRecordDirty();
                scene.ClearTransformDirty();

                ReadOnlySpan<byte> a = dlReuse.Bytes, b = dlFull.Bytes;
                if (a.Length == b.Length && a.SequenceEqual(b)) return;
                mismatchFrames++;
                if (firstMismatch.Length != 0) return;
                int at = -1, lim = Math.Min(a.Length, b.Length);
                for (int i = 0; i < lim; i++) if (a[i] != b[i]) { at = i; break; }
                firstMismatch = $"scale={scale} {label}: reuse={a.Length}B full={b.Length}B firstDiff@{at} reused={st.SpansReused} rebased={st.SpansRebased}";
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
