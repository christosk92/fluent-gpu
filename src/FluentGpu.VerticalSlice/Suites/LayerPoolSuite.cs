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
using static FluentGpu.VerticalSlice.Harness.Gate;

// The compositor LAYER POOL's portable contract (gpu-renderer.md §7.1). The D3D12 leaves that own the actual render
// targets are unreachable headlessly (TerraFX/COM, render-thread-confined) and there is no headless framebuffer, so
// what is pinned here is every DECISION the leaves now compute through: the size bucket, the idle-trim/warm-reserve/
// weak-cap verdict, the fence rule, the bounded-target coordinate mapping, the bounded-group extent, the subtree
// admission probe, and the pooled-vs-in-use byte census. The GPU pixels those decisions produce stay a --screenshot
// check, exactly as AnimSuite says of the pin lease.
//
// GpuProfile.IsWeak is ALWAYS false headlessly (only D3D12Device's UMA probe ever sets Tier), which is precisely why
// LayerTargetTrim.Classify takes `weak` as an argument instead of reading the global: both tiers are driven here.

static class LayerPoolSuite
{
    public static void Run(StringTable strings)
    {
        BucketChecks();
        TrimVerdictChecks();
        FenceRuleChecks();
        ReuseWithoutGrowthChecks();
        BoundedTargetMappingChecks();
        BoundedGroupExtentChecks();
        SubtreeProbeChecks(strings);
        CensusChecks();
        AllocChecks(strings);
    }

    // ── the size ladder ─────────────────────────────────────────────────────────────────────────────────────────────

    static void BucketChecks()
    {
        bool monotone = true, covers = true, floor64 = true, linear = true, po2 = true;
        int prev = 0;
        for (int px = 1; px <= 9000; px++)
        {
            int d = LayerTargetBucket.Dim(px);
            if (d < prev) monotone = false;
            if (d < px) covers = false;
            if (d < LayerTargetBucket.MinDim) floor64 = false;
            if (px <= LayerTargetBucket.LinearCeiling && d % LayerTargetBucket.LinearStep != 0) linear = false;
            if (px > LayerTargetBucket.LinearCeiling && (d & (d - 1)) != 0) po2 = false;
            prev = d;
        }
        Check("gate.layerpool.bucket-ladder is monotone, always covers the request, floors at 64, steps by 64 up to the linear ceiling and is power-of-two above it",
            monotone && covers && floor64 && linear && po2,
            $"monotone={monotone} covers={covers} floor64={floor64} linear64={linear} po2={po2} ceiling={LayerTargetBucket.LinearCeiling}");

        // The reason this ladder exists: a guarded lyric strip (a ~340x120 RegionBox + a sigma-4 tap guard) wastes
        // far less area than the acrylic power-of-two ladder would, WITHOUT ever under-covering.
        int gw = 340 + 13 * 2, gh = 120 + 13 * 2;
        long tight = (long)LayerTargetBucket.Dim(gw) * LayerTargetBucket.Dim(gh);
        long po2Area = (long)AcrylicBackdropMath.BucketDim(gw) * AcrylicBackdropMath.BucketDim(gh);
        long need = (long)gw * gh;
        Check("gate.layerpool.bucket-tighter-than-po2 the layer ladder covers a guarded blur strip with materially less waste than the acrylic power-of-two ladder",
            tight >= need && tight < po2Area,
            $"need={need}px tight={tight}px po2={po2Area}px waste={(double)tight / need:0.00}x vs {(double)po2Area / need:0.00}x");

        // The step that mattered most: a full-window WIDTH. The po2 ladder jumps 1195 -> 2048 (1.7x waste on that axis
        // alone), which was enough to cancel the saving for a full-width bounded band and push it back to the canvas
        // lease; the linear ceiling is set past a typical window dimension precisely to stop that.
        Check("gate.layerpool.bucket-window-width the ladder does not round a typical window width up to the next power of two (the step that used to cancel a full-width band's saving)",
            LayerTargetBucket.Dim(1195) == 1216 && AcrylicBackdropMath.BucketDim(1195) == 2048
            && LayerTargetBucket.LinearCeiling >= 2048,
            $"layer(1195)={LayerTargetBucket.Dim(1195)} acrylic(1195)={AcrylicBackdropMath.BucketDim(1195)} ceiling={LayerTargetBucket.LinearCeiling}");

        // Bytes: the census/budget unit, and the number the whole change is measured in.
        Check("gate.layerpool.bucket-bytes B8G8R8A8 byte math matches w*h*4 and clamps negatives",
            LayerTargetBucket.Bytes(1195, 767) == 1195L * 767L * 4L && LayerTargetBucket.Bytes(-4, 10) == 0,
            $"canvas1195x767={LayerTargetBucket.Bytes(1195, 767)}B");
    }

    // ── the trim / warm-reserve / weak-cap verdicts ─────────────────────────────────────────────────────────────────

    static void TrimVerdictChecks()
    {
        // (a) an IN-USE slot is never retired, at any age, on any tier, in any class.
        bool inUseSafe = true;
        foreach (bool weak in new[] { false, true })
            foreach (bool pin in new[] { false, true })
                foreach (bool canvas in new[] { false, true })
                    foreach (int age in new[] { 0, 121, 601, 100000 })
                        foreach (int rank in new[] { 0, 3, 9 })
                            if (LayerTargetTrim.Classify(inUse: true, pin, canvas, age, weak, rank) != LayerTrimVerdict.Keep)
                                inUseSafe = false;
        Check("gate.layerpool.trim-never-touches-in-use a leased slot is Keep for every tier/class/age/rank combination",
            inUseSafe, "in-use x {tier} x {pin} x {canvas} x {age} x {rank}");

        // (b) trim AFTER idle, not before — bucketed scratch, both tiers, at the exact window boundary.
        int wWeak = LayerTargetTrim.IdleFramesWeak, wStrong = LayerTargetTrim.IdleFramesStrong;
        bool weakAt = LayerTargetTrim.Classify(false, false, false, wWeak, true, 0) == LayerTrimVerdict.Keep;
        bool weakPast = LayerTargetTrim.Classify(false, false, false, wWeak + 1, true, 0) == LayerTrimVerdict.Retire;
        bool strongAt = LayerTargetTrim.Classify(false, false, false, wStrong, false, 0) == LayerTrimVerdict.Keep;
        bool strongPast = LayerTargetTrim.Classify(false, false, false, wStrong + 1, false, 0) == LayerTrimVerdict.Retire;
        bool strongKeepsWeakWindow = LayerTargetTrim.Classify(false, false, false, wWeak + 1, false, 0) == LayerTrimVerdict.Keep;
        Check("gate.layerpool.trim-after-idle a free bucketed scratch slot survives exactly its tier's window and is retired one frame past it (and a discrete GPU does NOT inherit the weak window)",
            weakAt && weakPast && strongAt && strongPast && strongKeepsWeakWindow,
            $"weak={wWeak} strong={wStrong} weakAt={weakAt} weakPast={weakPast} strongAt={strongAt} strongPast={strongPast} strongAtWeakWindow=Keep:{strongKeepsWeakWindow}");

        // (c) the WARM RESERVE: past the ordinary window but inside the cold one, exactly WarmCanvasReserve canvas
        // slots survive — ranked by recency — on BOTH tiers. (This is also why ColdIdleFrames must be strictly greater
        // than IdleFramesStrong: at equality the cold clause would fire on the same frame and there would be no
        // reserve stage at all on a discrete adapter.)
        int reserve = LayerTargetTrim.WarmCanvasReserve;
        int survivedStrong = 0, survivedWeak = 0;
        for (int rank = 0; rank < 8; rank++)
        {
            if (LayerTargetTrim.Classify(false, false, true, wStrong + 1, false, rank) == LayerTrimVerdict.Keep) survivedStrong++;
            // On weak, stay under the M5 hard cap so this measures the RESERVE and not the cap.
            if (rank < LayerTargetTrim.WeakCanvasHardCap
                && LayerTargetTrim.Classify(false, false, true, wWeak + 1, true, rank) == LayerTrimVerdict.Keep) survivedWeak++;
        }
        Check("gate.layerpool.warm-reserve past the ordinary window (and inside the cold one) exactly WarmCanvasReserve canvas-sized slots survive on both tiers, ranked by recency",
            survivedStrong == reserve && survivedWeak == reserve && LayerTargetTrim.ColdIdleFrames > wStrong,
            $"reserve={reserve} strongSurvived={survivedStrong}/8 weakSurvived={survivedWeak}/{LayerTargetTrim.WeakCanvasHardCap} cold={LayerTargetTrim.ColdIdleFrames}>strong={wStrong}");

        // (d) the COLD window releases the reserve too — a closed surface must not pin canvas targets forever.
        bool coldClears = true;
        for (int rank = 0; rank < 8; rank++)
            if (LayerTargetTrim.Classify(false, false, true, LayerTargetTrim.ColdIdleFrames + 1, false, rank) != LayerTrimVerdict.Retire)
                coldClears = false;
        Check("gate.layerpool.cold-window-releases-the-reserve past ColdIdleFrames every idle canvas slot goes, warm reserve included",
            coldClears, $"cold={LayerTargetTrim.ColdIdleFrames} ranks=0..7 all Retire={coldClears}");

        // (e) the weak-tier HARD CAP is immediate (age 0) and only beyond the cap.
        int cap = LayerTargetTrim.WeakCanvasHardCap;
        bool underCapKept = LayerTargetTrim.Classify(false, false, true, 0, true, cap - 1) == LayerTrimVerdict.Keep;
        bool atCapRetired = LayerTargetTrim.Classify(false, false, true, 0, true, cap) == LayerTrimVerdict.Retire;
        bool strongUncapped = LayerTargetTrim.Classify(false, false, true, 0, false, cap + 4) == LayerTrimVerdict.Keep;
        Check("gate.layerpool.weak-canvas-hard-cap on a weak adapter idle canvas slots beyond WeakCanvasHardCap are retired immediately (age 0) and a discrete adapter is uncapped",
            underCapKept && atCapRetired && strongUncapped,
            $"cap={cap} underCap=Keep:{underCapKept} atCap=Retire:{atCapRetired} strongBeyondCap=Keep:{strongUncapped}");

        // (f) PINS use the short window on BOTH tiers (a live pin is hit every submit, so only orphans age at all).
        int pw = LayerTargetTrim.PinIdleFrames;
        bool pinsShort = LayerTargetTrim.Classify(false, true, false, pw, false, 0) == LayerTrimVerdict.Keep
                      && LayerTargetTrim.Classify(false, true, false, pw + 1, false, 0) == LayerTrimVerdict.Retire
                      && LayerTargetTrim.Classify(false, true, false, pw + 1, true, 0) == LayerTrimVerdict.Retire;
        Check("gate.layerpool.pin-window retained region pins reclaim on the short window on every tier",
            pinsShort, $"pinWindow={pw} strongPast=Retire strong@window=Keep weakPast=Retire");
    }

    // ── the fence rule ──────────────────────────────────────────────────────────────────────────────────────────────

    static void FenceRuleChecks()
    {
        Check("gate.layerpool.no-release-before-fence CanRelease is false until the frame fence has passed the entry's last recorded use, and true at equality",
            !LayerTargetTrim.CanRelease(lastUseFence: 10, completedFence: 9)
            && LayerTargetTrim.CanRelease(10, 10)
            && LayerTargetTrim.CanRelease(10, 11)
            && !LayerTargetTrim.CanRelease(ulong.MaxValue, 0),
            "10>9:false 10>=10:true 10<=11:true max>0:false");

        // A trim on a frame whose fence has NOT completed must retire (stop leasing) and yet release NOTHING: that is
        // the whole "no trim while fenced" invariant, and it is the composition of the two rules above. Modelled here
        // over the same two-stage shape the leaves implement (Classify → retire queue → CanRelease → Release).
        var pool = new PoolModel(canvasW: 1195, canvasH: 767, slots: 8);
        int slot = pool.LeaseCanvas(frameFence: 100);
        pool.Release(slot);
        // Fence 100 is still in flight (completed 99) for the whole trim window and beyond.
        for (int f = 0; f <= LayerTargetTrim.ColdIdleFrames + 2; f++) pool.Tick(weak: true, completedFence: 99);
        bool retiredNotReleased = pool.LiveSlots == 0 && pool.RetiredCount == 1 && pool.ReleasedCount == 0;
        pool.Tick(weak: true, completedFence: 100);
        bool releasedOnceFenced = pool.RetiredCount == 0 && pool.ReleasedCount == 1;
        Check("gate.layerpool.no-trim-while-fenced an idle slot whose last-use fence is still in flight is retired but NEVER released, and is released on the first tick after the fence passes",
            retiredNotReleased && releasedOnceFenced,
            $"whileInFlight(live={pool.LiveSlots} retired=1 released=0)={retiredNotReleased} afterFence={releasedOnceFenced}");
    }

    // ── bucketed reuse without growth ───────────────────────────────────────────────────────────────────────────────

    static void ReuseWithoutGrowthChecks()
    {
        // A scrolling blurred row: the guarded region box jitters by a pixel or two every frame (RegionBox depends on
        // the sub-pixel floor/ceil), which is exactly the pattern an EXACT-size pool would churn on. Bucketing plus a
        // best-fit lease must stop creating surfaces after warmup and must not grow the pool's bytes.
        var pool = new PoolModel(canvasW: 1195, canvasH: 767, slots: 16);
        int createsAfterWarmup = 0;
        long bytesAfterWarmup = 0;
        for (int f = 0; f < 400; f++)
        {
            int w = 340 + f % 7, h = 120 + (f * 3) % 5;      // the per-frame jitter
            int a = pool.LeaseBucketed(w, h, frameFence: (ulong)f);
            int b = pool.LeaseBucketed(w, h, frameFence: (ulong)f);   // the blur's ping-pong pair
            pool.Release(a); pool.Release(b);
            pool.Tick(weak: true, completedFence: (ulong)f);
            if (f == 60) { pool.ResetCreateCount(); bytesAfterWarmup = pool.LiveBytes; }
            if (f > 60) createsAfterWarmup = pool.CreateCount;
        }
        Check("gate.layerpool.bucketed-reuse-without-growth 340 frames of a per-frame-jittering guarded region box create ZERO new surfaces after warmup and the pool's live bytes never grow",
            createsAfterWarmup == 0 && pool.LiveBytes <= bytesAfterWarmup,
            $"createsAfterWarmup={createsAfterWarmup} bytes={pool.LiveBytes}B (warm={bytesAfterWarmup}B) liveSlots={pool.LiveSlots}");

        // …and the bounded lease is a fraction of the canvas lease it replaces. This is the memory claim, stated in
        // the units the census reports.
        long canvasBytes = LayerTargetBucket.Bytes(1195, 767);
        long boundedBytes = LayerTargetBucket.Bytes(LayerTargetBucket.Dim(340 + 26), LayerTargetBucket.Dim(120 + 26));
        Check("gate.layerpool.bounded-lease-is-a-fraction a guarded blur strip's bucketed surface is far smaller than the canvas surface the exact arm used to lease",
            boundedBytes * 4 < canvasBytes,
            $"bounded={boundedBytes / 1024}KiB canvas={canvasBytes / 1024}KiB ratio={(double)canvasBytes / boundedBytes:0.0}x");
    }

    // ── bounded-target coordinate correctness ───────────────────────────────────────────────────────────────────────

    static void BoundedTargetMappingChecks()
    {
        // (a) a CANVAS-sized bounded target degenerates to the legacy full-target mapping — the bounded composite is a
        // strict generalization of the 1:1 one, not a second code path with its own conventions.
        var full = LayerTargetMap.For(0, 0, 1195, 767, new SelfBlurPixelBox(0, 0, 1195, 767));
        Check("gate.layerpool.uv-identity a canvas-origin, canvas-sized bounded target maps to the identity uv rect (0,0,1,1) the legacy full-target composite uses",
            full == new LayerTargetUv(0f, 0f, 1f, 1f), $"uv=({full.U0},{full.V0},{full.DU},{full.DV})");

        // (b) a real bounded target: origin (200,300), best-fit surface LARGER than the used extent (the whole point —
        // pool leases are best-fit, so the mapping must be against the SURFACE, never the used extent).
        int originX = 200, originY = 300, usedW = 366, usedH = 146;
        int texW = LayerTargetBucket.Dim(usedW), texH = LayerTargetBucket.Dim(usedH);
        var output = new SelfBlurPixelBox(originX, originY, originX + usedW, originY + usedH);
        var uv = LayerTargetMap.For(originX, originY, texW, texH, in output);
        bool startsAtTexelZero = Math.Abs(uv.U0) < 1e-6f && Math.Abs(uv.V0) < 1e-6f;
        bool scaledBySurface = Math.Abs(uv.DU - usedW / (float)texW) < 1e-6f && Math.Abs(uv.DV - usedH / (float)texH) < 1e-6f;
        bool notScaledByUsed = texW != usedW && Math.Abs(uv.DU - 1f) > 1e-6f;   // proves it is not the used-extent mapping
        Check("gate.layerpool.uv-bounded-composite a bounded target's uv rect starts at texel 0 and is scaled by the SURFACE dims, not the used extent (best-fit leases differ)",
            startsAtTexelZero && scaledBySurface && notScaledByUsed,
            $"used={usedW}x{usedH} tex={texW}x{texH} uv=({uv.U0:0.####},{uv.V0:0.####},{uv.DU:0.####},{uv.DV:0.####})");

        // (c) a CLIPPED bounded target: the visible output is a sub-box of the surface's region, so the uv rect must
        // offset into the surface — the case that draws the right pixels in the wrong place when it is wrong.
        var clipped = new SelfBlurPixelBox(originX + 40, originY + 20, originX + 140, originY + 60);
        var cuv = LayerTargetMap.For(originX, originY, texW, texH, in clipped);
        bool offsetIn = Math.Abs(cuv.U0 - 40f / texW) < 1e-6f && Math.Abs(cuv.V0 - 20f / texH) < 1e-6f
                     && Math.Abs(cuv.DU - 100f / texW) < 1e-6f && Math.Abs(cuv.DV - 40f / texH) < 1e-6f;
        Check("gate.layerpool.uv-clipped-subbox a partially clipped bounded output maps to the matching OFFSET sub-rect of the surface (never a stretch of the whole surface)",
            offsetIn, $"uv=({cuv.U0:0.#####},{cuv.V0:0.#####},{cuv.DU:0.#####},{cuv.DV:0.#####})");

        Check("gate.layerpool.uv-degenerate a zero-area surface or an empty output yields the identity rect instead of a divide-by-zero",
            LayerTargetMap.For(0, 0, 0, 0, new SelfBlurPixelBox(0, 0, 4, 4)) == new LayerTargetUv(0f, 0f, 1f, 1f)
            && LayerTargetMap.For(0, 0, 64, 64, default) == new LayerTargetUv(0f, 0f, 1f, 1f),
            "tex 0x0 and empty output both identity");

        // (d) the NESTED-SPACE ROUND TRIP — the invariant that makes a non-canvas-sized target legal at all. The blur's
        // H pass writes a canvas-sized group RT into a small guarded scratch; the V pass reads it back while targeting
        // the canvas again. Composing the two sweeps must land on the SAME canvas pixel centre it started from, for
        // every pixel of the region, or the blur is displaced by a sub-pixel (a soft double-image) or a whole pixel.
        int canvasW = 1195, canvasH = 767;
        int minX = 400, minY = 250, regionW = 340, regionH = 120;
        int guard = SelfBlurRegion.TapRadius(4f) + 1;
        int gx = minX - guard, gy = minY - guard, gw = regionW + guard * 2, gh = regionH + guard * 2;
        int sTexW = LayerTargetBucket.Dim(gw), sTexH = LayerTargetBucket.Dim(gh);
        var hSweep = LayerTargetMap.Sweep(gx, gy, canvasW, canvasH, gw, gh);              // scratch viewport → canvas uv
        var vSweep = LayerTargetMap.Sweep(-gx, -gy, sTexW, sTexH, canvasW, canvasH);      // canvas viewport → scratch uv
        float worst = 0f;
        for (int py = minY; py < minY + regionH; py++)
        {
            for (int px = minX; px < minX + regionW; px++)
            {
                // V pass, at canvas pixel (px,py): its viewport uv, mapped into the scratch, is a texel index.
                float vU = (px + 0.5f) / canvasW, vV = (py + 0.5f) / canvasH;
                float sTexel = (vSweep.U0 + vU * vSweep.DU) * sTexW;
                float sTexelY = (vSweep.V0 + vV * vSweep.DV) * sTexH;
                // H pass, writing that same scratch texel: its viewport uv, mapped into the canvas, is a canvas pixel.
                float hU = sTexel / gw, hV = sTexelY / gh;
                float backX = (hSweep.U0 + hU * hSweep.DU) * canvasW;
                float backY = (hSweep.V0 + hV * hSweep.DV) * canvasH;
                worst = MathF.Max(worst, MathF.Max(MathF.Abs(backX - (px + 0.5f)), MathF.Abs(backY - (py + 0.5f))));
            }
        }
        Check("gate.layerpool.sweep-roundtrip-nested composing the H-pass (canvas group RT -> bounded scratch) and V-pass (bounded scratch -> canvas) sweeps returns every region pixel to its own centre, so a non-canvas-sized target is not displaced",
            worst < 1e-3f, $"region={regionW}x{regionH} guard={guard} scratch={gw}x{gh} in {sTexW}x{sTexH} worstErr={worst:0.######}px");

        // (e) the guard band must cover the V pass's full tap reach: that is what makes every texel it can read either
        // an H-written one or a CLEARED transparent one, which is what makes the bounded blur pixel-identical.
        bool guardCovers = true;
        Span<float> kOff = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        Span<float> kWgt = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        for (float sigma = 0.25f; sigma <= 4f; sigma += 0.25f)
        {
            int n = AcrylicBackdropMath.BuildKernel(sigma, kOff, kWgt);
            float maxTap = 0f;
            for (int k = 0; k < n; k++) maxTap = MathF.Max(maxTap, kOff[k]);
            if (maxTap > SelfBlurRegion.TapRadius(sigma) + 1) guardCovers = false;
        }
        Check("gate.layerpool.guard-covers-taps the bounded blur's guard band (TapRadius + 1) is at least the largest bilinear tap offset the kernel emits for every down==1 sigma",
            guardCovers, "sigma 0.25..4 step 0.25, maxTapOffset <= TapRadius+1");
    }

    // ── the bounded plain-opacity group's extent ────────────────────────────────────────────────────────────────────

    static void BoundedGroupExtentChecks()
    {
        int canvasW = 1195, canvasH = 767;
        var extent = new RectF(100f, 120f, 300f, 180f);
        var patched = OpacityLayer(extent);

        // The lease, the CLEAR and the bounded COMPOSITE must be ONE box: EdgeFadeLayerClear.Compute is what the
        // full-canvas path clears, OpacityLayerCompositor.CompositeOpacity is what it composites, and this is what the
        // bounded path leases. If they ever diverge, a composited texel can be one the lease never cleared.
        EdgeFadeLayerClear.Compute(in patched, 1f, canvasW, canvasH, out int cl, out int ct, out int cr, out int cb, out bool fullCanvas);
        var box = BoundedGroupRegion.Compute(patched.CompositeClip, 1f, canvasW, canvasH);
        Check("gate.layerpool.bounded-group-extent-equals-clear the bounded plain-opacity lease box is byte-identical to the box the full-canvas path clears and composites",
            !fullCanvas && !box.IsEmpty && box.MinX == cl && box.MinY == ct && box.MaxX == cr && box.MaxY == cb,
            $"clear=({cl},{ct},{cr},{cb}) lease=({box.MinX},{box.MinY},{box.MaxX},{box.MaxY})");

        // Fractional DPI: floor/ceil OUTWARD on both, so the lease can never be a pixel narrower than the composite.
        EdgeFadeLayerClear.Compute(in patched, 1.5f, canvasW, canvasH, out cl, out ct, out cr, out cb, out _);
        var dpiBox = BoundedGroupRegion.Compute(patched.CompositeClip, 1.5f, canvasW, canvasH);
        Check("gate.layerpool.bounded-group-extent-dpi at fractional scale the lease box still matches the clear box exactly (same floor/ceil clamp)",
            !dpiBox.IsEmpty && dpiBox.MinX == cl && dpiBox.MinY == ct && dpiBox.MaxX == cr && dpiBox.MaxY == cb,
            $"scale=1.5 clear=({cl},{ct},{cr},{cb}) lease=({dpiBox.MinX},{dpiBox.MinY},{dpiBox.MaxX},{dpiBox.MaxY})");

        Check("gate.layerpool.bounded-group-declines-unpatched an UNPATCHED extent means 'unknown', never 'empty' — it must stay on the full-canvas lease",
            BoundedGroupRegion.Compute(default, 1f, canvasW, canvasH).IsEmpty,
            "CompositeClip default -> empty -> canvas lease");

        // A near-full-canvas group has nothing to gain (its bucket rounds back up to roughly the canvas) and the
        // bounded path only adds restrictions, so it must decline rather than take a lossy trade for no saving.
        var nearFull = OpacityLayer(new RectF(0f, 0f, canvasW - 4, canvasH - 4));
        Check("gate.layerpool.bounded-group-declines-no-saving a near-canvas extent declines the bounded lease (its bucket rounds back to the canvas, so the shifted-viewport restrictions would buy nothing)",
            BoundedGroupRegion.Compute(nearFull.CompositeClip, 1f, canvasW, canvasH).IsEmpty,
            $"extent={canvasW - 4}x{canvasH - 4} minRatio={BoundedGroupRegion.MinAreaSavingRatio}");

        Check("gate.layerpool.bounded-group-declines-degenerate a zero-area or fully off-canvas extent declines instead of leasing a 0x0 target",
            BoundedGroupRegion.Compute(new RectF(10f, 10f, 0f, 40f), 1f, canvasW, canvasH).IsEmpty
            && BoundedGroupRegion.Compute(new RectF(-500f, -500f, 100f, 100f), 1f, canvasW, canvasH).IsEmpty,
            "zero width and off-canvas both empty");
    }

    // ── the subtree admission probe ─────────────────────────────────────────────────────────────────────────────────

    static void SubtreeProbeChecks(StringTable strings)
    {
        var fam = strings.Intern("Segoe UI");
        var rect = new RectF(100f, 120f, 300f, 180f);

        // (a) a FLAT subtree (leaf draws + a plain scissor clip) is boundable, and afterPop lands past the PopLayer.
        var flat = new DrawList();
        flat.PushOpacityLayer(rect, default, 0.5f);
        int start = sizeof(int) + Unsafe.SizeOf<PushLayerCmd>();
        flat.PushClip(rect);
        flat.DrawGlyphRun(new RectF(0f, 0f, 300f, 40f), new ColorF(1f, 1f, 1f, 1f), strings.Intern("flat"), fam,
            20f, 400, 0, 0, 1, 0f, 24f, 0, 0, new Affine2D(1f, 0f, 0f, 1f, 108f, 126f), 1f);
        flat.PopClip();
        flat.PopLayer(rect);
        bool flatOk = LayerSubtreeProbe.IsBoundable(flat.Bytes, start, out int flatAfter);
        Check("gate.layerpool.probe-admits-flat a subtree of leaf draws and plain scissor clips is boundable, and afterPop is the end of the stream",
            flatOk && flatAfter == flat.Bytes.Length, $"ok={flatOk} afterPop={flatAfter} len={flat.Bytes.Length}");

        // (b) a NESTED layer is refused — it would bind its own canvas-sized RT outside the shifted space.
        var nested = new DrawList();
        nested.PushOpacityLayer(rect, default, 0.5f);
        nested.PushOpacityLayer(rect, default, 0.5f);
        nested.PopLayer(rect);
        nested.PopLayer(rect);
        Check("gate.layerpool.probe-refuses-nested a nested PushLayer inside the subtree refuses the bounded lease",
            !LayerSubtreeProbe.IsBoundable(nested.Bytes, start, out _), "nested PushLayer -> false");

        // (c) a TIER-3 STENCIL scope is refused — the one place this probe is deliberately STRICTER than
        // BlurPinKey.TryCompute, which accepts stencil ops because it only needs a content key. A shifted viewport
        // cannot address the swapchain-sized stencil DSV, and a plain opacity group is too common to accept that loss.
        var stencil = new DrawList();
        stencil.PushOpacityLayer(rect, default, 0.5f);
        stencil.PushStencilClip(rect, default, 0, new Affine2D(1f, 0f, 0f, 1f, 0f, 0f));
        stencil.PopStencilClip(rect, default, new Affine2D(1f, 0f, 0f, 1f, 0f, 0f));
        stencil.PopLayer(rect);
        var pl = MemoryMarshal.Read<PushLayerCmd>(stencil.Bytes.Slice(sizeof(int)));
        bool pinKeyAccepts = BlurPinKey.TryCompute(stencil.Bytes, start, in pl, out _, out _);
        Check("gate.layerpool.probe-refuses-stencil a tier-3 stencil scope refuses the bounded lease, and this is strictly stricter than BlurPinKey's walk (which accepts it, needing only a content key)",
            !LayerSubtreeProbe.IsBoundable(stencil.Bytes, start, out _) && pinKeyAccepts,
            $"probe=false pinKey={pinKeyAccepts}");

        // (d) a truncated stream is refused rather than walked off the end.
        var truncated = new DrawList();
        truncated.PushOpacityLayer(rect, default, 0.5f);
        truncated.PushClip(rect);
        Check("gate.layerpool.probe-refuses-unterminated a subtree with no matching PopLayer refuses instead of running off the buffer",
            !LayerSubtreeProbe.IsBoundable(truncated.Bytes, start, out _), "no PopLayer -> false");
    }

    // ── the census ──────────────────────────────────────────────────────────────────────────────────────────────────

    static void CensusChecks()
    {
        long canvas = LayerTargetBucket.Bytes(1195, 767);
        long small = LayerTargetBucket.Bytes(384, 192);
        LayerTargetCensus c = default;
        c = c.WithSlot(canvas, inUse: true, isPin: false);       // one group compositing
        c = c.WithSlot(canvas, inUse: false, isPin: false);      // one warm canvas slot
        c = c.WithSlot(canvas, inUse: false, isPin: false);      // …and the second
        c = c.WithSlot(small, inUse: false, isPin: true);        // a retained blur pin
        c = c.WithSlot(small, inUse: false, isPin: true);
        c = c.WithRetired(canvas);                               // one awaiting its fence

        bool split = c.InUseBytes == canvas && c.InUseCount == 1
                  && c.FreeBytes == canvas * 2 && c.FreeCount == 2
                  && c.PinBytes == small * 2 && c.PinCount == 2
                  && c.RetiredBytes == canvas && c.RetiredCount == 1;
        bool total = c.TotalBytes == canvas * 4 + small * 2 && c.LiveCount == 5;
        Check("gate.layerpool.census-bytes the pooled/in-use/pin/retired split adds up and TotalBytes accounts for every surface the pool holds (a pin is never counted as free, and a retired entry is not a live slot)",
            split && total,
            $"inuse={c.InUseBytes} free={c.FreeBytes} pin={c.PinBytes} retired={c.RetiredBytes} total={c.TotalBytes} live={c.LiveCount}");

        // The device reports three compositors as one line, so the sum must be a plain field-wise add.
        var sum = c + c;
        Check("gate.layerpool.census-sums two pools' censuses add field-wise, so the device can report opacity + acrylic + baked-blur as one census line",
            sum.TotalBytes == c.TotalBytes * 2 && sum.LiveCount == c.LiveCount * 2 && sum.PinCount == 4,
            $"sumTotal={sum.TotalBytes} sumLive={sum.LiveCount}");

        // An EMPTY census must read as all-zero, which is what a launch with no layer on screen has to report — the
        // honest answer the single `gpu bytes` total could never give.
        LayerTargetCensus empty = default;
        Check("gate.layerpool.census-empty a pool that has never leased reports zero in every field (the launch case the single gpu-bytes total could not distinguish)",
            empty.TotalBytes == 0 && empty.LiveCount == 0 && empty.ToDetail().Length > 0,
            $"detail=\"{empty.ToDetail()}\"");
    }

    // ── zero allocation ─────────────────────────────────────────────────────────────────────────────────────────────

    static void AllocChecks(StringTable strings)
    {
        // Every one of these runs on the render thread's per-frame record/submit path (TickPool once per frame,
        // Dim/For/Sweep per lease/composite), so they are inside the phases 6-13 zero-alloc contract.
        var box = new SelfBlurPixelBox(200, 300, 566, 446);
        LayerTargetTrim.Classify(false, false, true, 5, true, 1);                 // warm
        LayerTargetBucket.Dim(365);
        LayerTargetMap.For(200, 300, 512, 256, in box);
        LayerTargetMap.Sweep(-200, -300, 512, 256, 1195, 767);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int keeps = 0; long bytes = 0; float acc = 0f;
        for (int i = 0; i < 10000; i++)
        {
            if (LayerTargetTrim.Classify(i % 3 == 0, i % 5 == 0, i % 2 == 0, i % 900, i % 7 == 0, i % 6) == LayerTrimVerdict.Keep) keeps++;
            bytes += LayerTargetBucket.Bytes(LayerTargetBucket.Dim(64 + i % 1400), LayerTargetBucket.Dim(64 + i % 900));
            var uv = LayerTargetMap.For(200, 300, 512, 256, in box);
            var sw = LayerTargetMap.Sweep(-200, -300, 512, 256, 1195, 767);
            acc += uv.DU + sw.DU;
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.layerpool.alloc-zero 10000 iterations of Classify + Dim/Bytes + the two coordinate maps allocate 0 managed bytes (they run per frame / per lease on the render thread)",
            delta == 0 && keeps > 0 && bytes > 0 && acc > 0f,
            $"delta={delta}B/10000 keeps={keeps} acc={acc:0.##}");

        // The probe walks a real subtree once per bounded-group candidate per frame — same contract.
        var dl = new DrawList();
        var rect = new RectF(100f, 120f, 300f, 180f);
        var fam = strings.Intern("Segoe UI");
        dl.PushOpacityLayer(rect, default, 0.5f);
        int start = sizeof(int) + Unsafe.SizeOf<PushLayerCmd>();
        for (int i = 0; i < 40; i++)
            dl.DrawGlyphRun(new RectF(0f, 0f, 300f, 40f), new ColorF(1f, 1f, 1f, 1f), strings.Intern("row" + i), fam,
                20f, 400, 0, 0, 1, 0f, 24f, 0, 0, new Affine2D(1f, 0f, 0f, 1f, 108f, 126f + i * 24f), 1f);
        dl.PopLayer(rect);
        LayerSubtreeProbe.IsBoundable(dl.Bytes, start, out _);   // warm
        long pBefore = GC.GetAllocatedBytesForCurrentThread();
        int ok = 0;
        for (int i = 0; i < 10000; i++) if (LayerSubtreeProbe.IsBoundable(dl.Bytes, start, out _)) ok++;
        long pDelta = GC.GetAllocatedBytesForCurrentThread() - pBefore;
        Check("gate.layerpool.probe-alloc-zero 10000 LayerSubtreeProbe walks over a 40-op subtree allocate 0 managed bytes",
            pDelta == 0 && ok == 10000, $"delta={pDelta}B/10000 admitted={ok}");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static PushLayerCmd OpacityLayer(in RectF extent)
    {
        var dl = new DrawList();
        int at = dl.Bytes.Length;
        dl.PushOpacityLayer(extent, default, 0.5f);
        dl.PatchOpacityLayerExtent(at, in extent);
        return MemoryMarshal.Read<PushLayerCmd>(dl.Bytes.Slice(at + sizeof(int)));
    }

    /// <summary>
    /// A pure model of one compositor's slot pool: the SAME two-stage shape the D3D12 leaves implement — best-fit
    /// bucketed lease, LRU eviction, a per-frame <see cref="LayerTargetTrim.Classify"/> tick that RETIRES into a
    /// fence-gated queue, and a drain that releases only what <see cref="LayerTargetTrim.CanRelease"/> allows. It
    /// exists so "trim after idle", "no trim while fenced" and "reuse without growth" are assertions about a POOL and
    /// not just about a predicate; the leaves' own COM/descriptor bookkeeping is what stays --screenshot territory.
    /// </summary>
    sealed class PoolModel
    {
        struct Slot { public bool Live, InUse, Pin; public int W, H; public int Idle; public ulong Fence; }
        struct RetiredEntry { public ulong Fence; public long Bytes; }

        readonly Slot[] _slots;
        readonly System.Collections.Generic.List<RetiredEntry> _retired = new();
        readonly int _canvasW, _canvasH;
        public int CreateCount { get; private set; }
        public int ReleasedCount { get; private set; }

        public PoolModel(int canvasW, int canvasH, int slots)
        {
            _canvasW = canvasW; _canvasH = canvasH;
            _slots = new Slot[slots];
        }

        public int RetiredCount => _retired.Count;
        public void ResetCreateCount() => CreateCount = 0;

        public int LiveSlots { get { int n = 0; for (int i = 0; i < _slots.Length; i++) if (_slots[i].Live) n++; return n; } }

        public long LiveBytes
        {
            get
            {
                long b = 0;
                for (int i = 0; i < _slots.Length; i++) if (_slots[i].Live) b += LayerTargetBucket.Bytes(_slots[i].W, _slots[i].H);
                return b;
            }
        }

        public int LeaseCanvas(ulong frameFence) => Lease(_canvasW, _canvasH, frameFence, exact: true);
        public int LeaseBucketed(int w, int h, ulong frameFence)
            => Lease(LayerTargetBucket.Dim(w), LayerTargetBucket.Dim(h), frameFence, exact: false);

        int Lease(int w, int h, ulong frameFence, bool exact)
        {
            // 1) BEST-FIT among free slots that can hold the request (the leaves' reuse rule).
            int best = -1;
            for (int i = 0; i < _slots.Length; i++)
            {
                ref var s = ref _slots[i];
                if (!s.Live || s.InUse || s.Pin) continue;
                if (exact ? (s.W != w || s.H != h) : (s.W < w || s.H < h)) continue;
                if (best < 0 || (long)s.W * s.H < (long)_slots[best].W * _slots[best].H) best = i;
            }
            if (best < 0)
            {
                for (int i = 0; i < _slots.Length; i++) if (!_slots[i].Live) { best = i; break; }
                if (best < 0)                                     // 2) LRU evict a free slot
                {
                    for (int i = 0; i < _slots.Length; i++)
                    {
                        if (_slots[i].InUse) continue;
                        if (best < 0 || _slots[i].Fence < _slots[best].Fence) best = i;
                    }
                    if (best < 0) throw new InvalidOperationException("model pool exhausted");
                    RetireSlot(ref _slots[best]);
                }
                _slots[best] = new Slot { Live = true, W = w, H = h };
                CreateCount++;
            }
            ref var e = ref _slots[best];
            e.InUse = true; e.Idle = 0; e.Fence = frameFence;
            return best;
        }

        public void Release(int slot) => _slots[slot].InUse = false;

        public void Tick(bool weak, ulong completedFence)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                ref var s = ref _slots[i];
                if (!s.Live || s.InUse) continue;
                s.Idle++;
            }
            for (int i = 0; i < _slots.Length; i++)
            {
                ref var s = ref _slots[i];
                if (!s.Live) continue;
                bool canvas = !s.Pin && s.W == _canvasW && s.H == _canvasH;
                int rank = canvas && !s.InUse ? IdleCanvasRank(i) : 0;
                if (LayerTargetTrim.Classify(s.InUse, s.Pin, canvas, s.Idle, weak, rank) != LayerTrimVerdict.Retire) continue;
                RetireSlot(ref s);
            }
            Drain(completedFence);
        }

        int IdleCanvasRank(int i)
        {
            int rank = 0;
            ulong f = _slots[i].Fence;
            for (int k = 0; k < _slots.Length; k++)
            {
                if (k == i) continue;
                ref var o = ref _slots[k];
                if (!o.Live || o.InUse || o.Pin || o.W != _canvasW || o.H != _canvasH) continue;
                if (o.Fence > f || (o.Fence == f && k < i)) rank++;
            }
            return rank;
        }

        void RetireSlot(ref Slot s)
        {
            _retired.Add(new RetiredEntry { Fence = s.Fence, Bytes = LayerTargetBucket.Bytes(s.W, s.H) });
            s = default;
        }

        void Drain(ulong completedFence)
        {
            for (int i = _retired.Count - 1; i >= 0; i--)
            {
                if (!LayerTargetTrim.CanRelease(_retired[i].Fence, completedFence)) continue;
                _retired.RemoveAt(i);
                ReleasedCount++;
            }
        }
    }
}
