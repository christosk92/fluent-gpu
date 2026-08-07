using System;
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
        PublishGapChecks();
        HeadlessPayloadChecks(strings);
        PolicyChecks();
        StreamSafetyChecks();
        CullHaloChecks();
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
