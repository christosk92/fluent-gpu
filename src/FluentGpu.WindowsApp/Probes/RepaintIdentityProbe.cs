using System;
using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu;

/// <summary>
/// <c>--repaint-identity</c> — the PIXEL gates of the retained tiled content layer (docs/plans/scroll-gpu-retained-tiles-
/// implementation.md §E). Headless gates prove the tile bookkeeping; only real pixels prove that compositing retained
/// tiles is INDISTINGUISHABLE from rastering the frame directly. Three scenario families, every one a GPU-side A/B of the
/// same scene state on the real D3D12 backend:
/// <list type="bullet">
/// <item><b>tile-static-identity</b> — every sub-scene of <see cref="RepaintIdentityScene"/> (translucent coat stacks,
/// text over fractional edges, opacity groups, a video hole, edge fades, self-blur, stencil path clips, off-screen
/// content) at device scales 1.0 / 1.25 / 1.5 / 1.75 / 2.0: the composited retained tiles (A) vs the same frame with every
/// segment DEGRADED to direct raster (the <see cref="GpuKnockouts.ForceFullDirect"/> knockout — the direct route a slice
/// takes when its tiles do not fit the budget) (B). Must be 0 px: the canonical replay viewport makes a tile's device
/// positions identical to a direct raster's, so a seam, a sub-pixel shift or a lost primitive shows as a difference.</item>
/// <item><b>tile-scroll-identity</b> — a list scrolled through the retained tiles (kept tiles re-placed at the new offset,
/// newly exposed ones rastered) (A) vs a forced full re-raster at the same offset (B): 0 px at every scale.</item>
/// <item><b>tile-feather-identity</b> — an opaque panel with an analytic top/bottom edge feather: every captured pixel vs
/// the prediction of <see cref="EdgeFeatherMask.Evaluate(in EdgeFeather, float, float)"/> (the C# port of the composite
/// shader's feather): ≤ 1/255 per channel.</item>
/// </list>
/// A mismatch writes A/B/diff PNGs and the differing region's bounding box. It is a command-line arm, not a behaviour
/// switch: the knockout and the forced full repaint are the explicit forms of paths the engine already has.
/// </summary>
internal static class RepaintIdentityProbe
{
    private const int Width = 900, Height = 640;
    private static readonly float[] Scales = [1.0f, 1.25f, 1.5f, 1.75f, 2.0f];
    // The GROUP variant runs both routes under the GroupFades control: where a fade distributes is decided per placement
    // from what paints inside its band, and the paint route's scan sees own paint pre-clipped at the band line, so the two
    // routes may legitimately settle a fade differently (≤ 1/255 per group surface — fade-distribute-identity's bound).
    // With every fade a group the routes differ ONLY by where the sticky clip is applied: 0 px.
    private static readonly (int Scenario, string Variant, GpuKnockouts RouteControl)[] StickyVariants =
        [(17, "group", GpuKnockouts.GroupFades), (18, "distributed", GpuKnockouts.None), (21, "whilestuck", GpuKnockouts.None)];
    private static readonly string[] StaticNames =
    [
        "twin-animators", "glyph-straddle", "stale-prior-extent", "opacity-group", "video-hole", "three-animators",
        "edge-fade", "blur-group", "stencil-sibling-blur", "nested-stencil", "offscreen-tail",
    ];

    /// <summary><c>--only name</c>: run only the scenario families whose name starts with it (iterating on one fixture).</summary>
    private static string? s_only;
    private static bool Want(string family) => s_only is null || family.StartsWith(s_only, StringComparison.Ordinal);

    public static int Run(string? outDir, string? only = null)
    {
        s_only = only;
        outDir ??= ".tmp/repaint-identity";
        int exit = 1;
        FluentApp.DiagnosticRun = (host, window, device) =>
        {
            exit = Drive(host, window, device, outDir);
            return true;   // we own the run; skip the interactive loop
        };
        FluentAppHarness.Run(() => new RepaintIdentityScene(),
            new AppOptions
            {
                Title = "FluentGpu — tile identity",
                Width = Width, Height = Height,
                // Mica OFF: the comparison is of the engine's own pixels over an opaque ground.
                Mica = false,
                WarmCadenceMs = 0f,
            });
        return exit;
    }

    private static int Drive(AppHost host, IPlatformWindow window, IGpuDevice device, string outDir)
    {
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Console.Error.WriteLine("repaint-identity: needs the Win32 + D3D12 backend (GPU required).");
            return 2;
        }
        float rawScale = w.Scale / MathF.Max(0.01f, w.Zoom);
        int passed = 0, failed = 0, total = 0;
        Console.Error.WriteLine($"[repaint-identity] {Width}x{Height} monitorScale={rawScale:0.###} scales=[{string.Join(", ", Scales)}]");

        foreach (float scale in Scales)
        {
            if (w.IsClosed) break;
            w.SetZoom(scale / rawScale);
            Settle(host, w, 8);

            // ── tile-static-identity ──
            for (int id = 0; id < StaticNames.Length && Want("tile-static-identity"); id++)
            {
                total++;
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = id;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] tiles = Capture(host, gpu, out int aw, out int ah);
                host.GpuKnockouts = GpuKnockouts.ForceFullDirect;
                host.RequestFullRepaintOnce();
                Settle(host, w, 6);
                byte[] direct = Capture(host, gpu, out int bw, out int bh);
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 4);
                string name = $"tile-static-identity/{StaticNames[id]}@{scale:0.00}";
                if (Judge(name, tiles, direct, aw, ah, bw, bh, 0, outDir)) passed++; else failed++;
            }

            // ── tile-scroll-identity ──
            if (Want("tile-scroll-identity"))
            {
                total++;
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 11;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                var handle = FindScroller(host);
                string name = $"tile-scroll-identity@{scale:0.00}";
                if (handle is null) { Console.Error.WriteLine($"[repaint-identity] {name}: INCONCLUSIVE — no scroller"); failed++; }
                else
                {
                    // walk down in uneven steps: every step re-places the kept tiles and rasters the entering ones
                    double at = 0.0;
                    for (int step = 0; step < 9; step++)
                    {
                        at += 97.0 + 13.0 * step;
                        handle.ScrollTo(at, ScrollMove.Immediate);
                        Settle(host, w, 3);
                    }
                    byte[] retained = Capture(host, gpu, out int aw, out int ah);
                    host.RequestFullRepaintOnce();   // every tile invalidated → re-rastered at this very offset
                    Settle(host, w, 4);
                    byte[] fresh = Capture(host, gpu, out int bw, out int bh);
                    if (Judge(name, retained, fresh, aw, ah, bw, bh, 0, outDir)) passed++; else failed++;
                    handle.ScrollTo(0.0, ScrollMove.Immediate);
                }
            }

            // ── partial-present-identity: frames composited through the PRESERVE route (repaint rects into a back buffer
            //    that keeps its pixels, FLIP_SEQUENTIAL) vs the same state composited and presented WHOLE ──
            for (int id = 0; id < StaticNames.Length && Want("partial-present-identity"); id++)
            {
                total++;
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = id;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                long partial0 = 0, partial1 = 0;
                host.RunWithRenderThreadParked(() => partial0 = gpu.PartialFrameCount);
                for (int step = 1; step <= 6; step++)
                {
                    RepaintIdentityScene.Tick.Value++;
                    if (step % 2 == 0) RepaintIdentityScene.TickB.Value++;
                    if (step % 3 == 0) RepaintIdentityScene.TickC.Value++;
                    RepaintIdentityScene.ScrollY.Value += 7f;
                    RepaintIdentityScene.RowX.Value += 5f;
                    Settle(host, w, 3);
                }
                byte[] partial = Capture(host, gpu, out int aw, out int ah, () => partial1 = gpu.PartialFrameCount);
                host.GpuKnockouts = GpuKnockouts.FullPresent;
                host.RequestFullRepaintOnce();
                Settle(host, w, 6);
                byte[] whole = Capture(host, gpu, out int bw, out int bh);
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 4);
                string name = $"partial-present-identity/{StaticNames[id]}@{scale:0.00}";
                Console.Error.WriteLine($"[repaint-identity] {name}: partial frames={partial1 - partial0}");
                if (Judge(name, partial, whole, aw, ah, bw, bh, 0, outDir)) passed++; else failed++;
            }
            if (Want("partial-present-identity"))
            {
                total++;
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 11;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                var handle = FindScroller(host);
                string name = $"partial-present-identity/scroll@{scale:0.00}";
                if (handle is null) { Console.Error.WriteLine($"[repaint-identity] {name}: INCONCLUSIVE — no scroller"); failed++; }
                else
                {
                    long p0 = 0, p1 = 0;
                    host.RunWithRenderThreadParked(() => p0 = gpu.PartialFrameCount);
                    double at = 0.0;
                    for (int step = 0; step < 9; step++)
                    {
                        at += 23.0 + 5.0 * step;   // small steps: most of the window keeps its pixels
                        handle.ScrollTo(at, ScrollMove.Immediate);
                        Settle(host, w, 3);
                    }
                    byte[] partial = Capture(host, gpu, out int aw, out int ah, () => p1 = gpu.PartialFrameCount);
                    host.GpuKnockouts = GpuKnockouts.FullPresent;
                    host.RequestFullRepaintOnce();
                    Settle(host, w, 6);
                    byte[] whole = Capture(host, gpu, out int bw, out int bh);
                    host.GpuKnockouts = GpuKnockouts.None;
                    Console.Error.WriteLine($"[repaint-identity] {name}: partial frames={p1 - p0}");
                    if (Judge(name, partial, whole, aw, ah, bw, bh, 0, outDir)) passed++; else failed++;
                    handle.ScrollTo(0.0, ScrollMove.Immediate);
                }
            }

            // ── occlusion-identity: an opaque overlay hides the page under it — the composite leaves the hidden items out
            //    (CompositeItem.Opaque) vs the NoOcclusion control that composites every item: 0 px, and the route engaged ──
            (int Scenario, string Name)[] occlusion = [(24, "tiles-inset"), (25, "lowres-window"), (26, "lowres-inset")];
            for (int v = 0; v < occlusion.Length && Want("occlusion-identity"); v++)
            {
                total++;
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = occlusion[v].Scenario;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                for (int step = 1; step <= 4; step++) { RepaintIdentityScene.Tick.Value++; Settle(host, w, 3); }
                // both routes composite from a FRESH scratch pool: a sampled route's pixels depend on the leased texture's
                // size (SurfacePool.DropScratch), and the two routes lease differently (their backdrop keys differ)
                host.RunWithRenderThreadParked(gpu.DropScratchSurfaces);
                host.RequestFullRepaintOnce();
                Settle(host, w, 4);
                int hidden = 0;
                byte[] occluded = Capture(host, gpu, out int aw, out int ah, () => hidden = gpu.LastOccludedItems);
                host.GpuKnockouts = GpuKnockouts.NoOcclusion;
                host.RunWithRenderThreadParked(gpu.DropScratchSurfaces);
                host.RequestFullRepaintOnce();
                Settle(host, w, 6);
                byte[] every = Capture(host, gpu, out int bw, out int bh);
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 4);
                string name = $"occlusion-identity/{occlusion[v].Name}@{scale:0.00}";
                Console.Error.WriteLine($"[repaint-identity] {name}: hidden items={hidden}");
                if (hidden <= 0) { Console.Error.WriteLine($"[repaint-identity] {name}: FAIL — the occlusion route never engaged"); failed++; }
                else if (Judge(name, occluded, every, aw, ah, bw, bh, 0, outDir)) passed++; else failed++;
            }

            // ── tile-feather-identity ──
            if (Want("tile-feather-identity"))
            {
                total++;
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 12;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] px = Capture(host, gpu, out int cw, out int ch);
                string name = $"tile-feather-identity@{scale:0.00}";
                if (JudgeFeather(name, px, cw, ch, w.Scale, outDir)) passed++; else failed++;
            }

            // ── tile-feather-identity, the PRODUCT of two nested feathers on one item ──
            if (Want("tile-feather-identity"))
            {
                total++;
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 16;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] px = Capture(host, gpu, out int cw, out int ch);
                string name = $"tile-feather-identity/product@{scale:0.00}";
                if (JudgeFeatherProduct(name, px, cw, ch, w.Scale, outDir)) passed++; else failed++;
            }

            // ── tile-acrylic-budget-identity: a frost exists only as a composite Backdrop item, never folded ──
            if (Want("tile-acrylic-budget-identity"))
            {
                total++;
                string name = $"tile-acrylic-budget-identity@{scale:0.00}";
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 13;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] none = Capture(host, gpu, out int aw, out int ah);
                RepaintIdentityScene.AcrylicEffects.Value = RepaintIdentityScene.AcrylicBudgetEffects;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] budget = Capture(host, gpu, out int bw, out int bh);
                RepaintIdentityScene.AcrylicPlate.Value = false;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] crisp = Capture(host, gpu, out int cw, out int ch);
                float s = w.Scale;
                var plate = new PixelRect((int)MathF.Ceiling(RepaintIdentityScene.AcrylicPlateX * s) + 3, (int)MathF.Ceiling(RepaintIdentityScene.AcrylicPlateY * s) + 3,
                    (int)MathF.Floor((RepaintIdentityScene.AcrylicPlateX + RepaintIdentityScene.AcrylicPlateW) * s) - 3,
                    (int)MathF.Floor((RepaintIdentityScene.AcrylicPlateY + RepaintIdentityScene.AcrylicPlateH) * s) - 3);
                bool same = JudgeRegion(name, none, budget, aw, ah, bw, bh, plate, 0, outDir);
                bool frosted = DiffersInRegion(name, none, crisp, aw, ah, cw, ch, plate, minMeanDelta: 12.0);
                if (same && frosted) passed++; else failed++;
                RepaintIdentityScene.ResetAll();
            }

            // ── fade-distribute-identity: the distributed analytic feathers vs every fade as a group surface ──
            if (Want("fade-distribute-identity"))
            {
                total++;
                string name = $"fade-distribute-identity@{scale:0.00}";
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 14;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                ScrollAll(host, vertical: 260.0, horizontal: 300.0);
                Settle(host, w, 8);
                OffscreenSplit dSplit = default, gSplit = default;
                byte[] distributed = Capture(host, gpu, out int aw, out int ah, () => dSplit = gpu.LastOffscreenSplit);
                host.GpuKnockouts = GpuKnockouts.GroupFades;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] grouped = Capture(host, gpu, out int bw, out int bh, () => gSplit = gpu.LastOffscreenSplit);
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 4);
                // both routes must really have run: no group surface when distributed, the fades' groups under the knockout
                bool routes = dSplit.GroupSurfaces + dSplit.GroupCacheHits == 0 && gSplit.GroupSurfaces + gSplit.GroupCacheHits >= 2;
                Console.Error.WriteLine($"[repaint-identity] {name}: distributed groups={dSplit.GroupSurfaces}+{dSplit.GroupCacheHits}hit; knockout groups={gSplit.GroupSurfaces}+{gSplit.GroupCacheHits}hit{(routes ? "" : "  FAIL — a route did not run")}");
                if (Judge(name, distributed, grouped, aw, ah, bw, bh, 1, outDir) && routes) passed++; else failed++;
                ScrollAll(host, vertical: 0.0, horizontal: 0.0);
            }

            // ── scroll-edge-identity: the default scroll-edge cue is the viewport's analytic feather over what lies behind ──
            if (Want("scroll-edge-identity"))
            {
                total++;
                string name = $"scroll-edge-identity@{scale:0.00}";
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 19;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                ScrollAll(host, vertical: 300.0, horizontal: double.NaN);   // past the runway at both edges
                Settle(host, w, 8);
                byte[] cued = Capture(host, gpu, out int aw, out int ah);
                RepaintIdentityScene.EdgeCueOff.Value = true;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] plain = Capture(host, gpu, out int bw, out int bh);
                RepaintIdentityScene.ContentHidden.Value = true;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] ground = Capture(host, gpu, out int gw, out int gh);
                bool sizes = aw == bw && ah == bh && aw == gw && ah == gh && cued.Length > 0;
                if (sizes && JudgeEdgeFeather(name, cued, plain, ground, aw, ah, w.Scale, outDir)) passed++;
                else { if (!sizes) Console.Error.WriteLine($"[repaint-identity] {name}: INCONCLUSIVE — capture sizes differ"); failed++; }
                RepaintIdentityScene.ResetAll();
                ScrollAll(host, vertical: 0.0, horizontal: 0.0);
            }

            // ── scroll-chrome-identity: the scrollbar thumb over the feather — distributed vs grouped vs the paint route ──
            if (Want("scroll-chrome-identity"))
            {
                total++;
                string name = $"scroll-chrome-identity@{scale:0.00}";
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 20;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                ScrollAll(host, vertical: 30.0, horizontal: double.NaN);   // past the runway; the thumb inside the top band
                Settle(host, w, 8);
                OffscreenSplit dSplit = default, gSplit = default;
                byte[] distributed = Capture(host, gpu, out int aw, out int ah, () => dSplit = gpu.LastOffscreenSplit);
                host.GpuKnockouts = GpuKnockouts.GroupFades;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] grouped = Capture(host, gpu, out int bw, out int bh, () => gSplit = gpu.LastOffscreenSplit);
                host.GpuKnockouts = GpuKnockouts.ForceFullDirect;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] direct = Capture(host, gpu, out int cw, out int ch);
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 4);
                bool routes = dSplit.GroupSurfaces + dSplit.GroupCacheHits == 0 && gSplit.GroupSurfaces + gSplit.GroupCacheHits >= 1;
                Console.Error.WriteLine($"[repaint-identity] {name}: distributed groups={dSplit.GroupSurfaces}+{dSplit.GroupCacheHits}hit; knockout groups={gSplit.GroupSurfaces}+{gSplit.GroupCacheHits}hit{(routes ? "" : "  FAIL — a route did not run")}");
                bool dg = Judge(name + "/distributed-vs-grouped", distributed, grouped, aw, ah, bw, bh, 1, outDir);
                bool dd = Judge(name + "/distributed-vs-paint", distributed, direct, aw, ah, cw, ch, 1, outDir);
                if (dg && dd && routes) passed++; else failed++;
                RepaintIdentityScene.ResetAll();
                ScrollAll(host, vertical: 0.0, horizontal: 0.0);
            }

            // ── stickyclip-identity: the composite-time sticky clip vs the paint route (StickyClipInPaint) ──
            foreach ((int scenario, string variant, GpuKnockouts routeControl) in StickyVariants)
            {
                if (!Want("stickyclip-identity")) continue;
                total++;
                string name = $"stickyclip-identity/{variant}@{scale:0.00}";
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = scenario;
                host.GpuKnockouts = routeControl;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                ScrollAll(host, vertical: 240.0, horizontal: 130.0);   // the band line crosses the magazine's heading / first shelf
                Settle(host, w, 8);
                ScrollAll(host, vertical: 251.0, horizontal: double.NaN);   // a page scroll: composite-only on this route
                Settle(host, w, 8);
                int compositeSlices = host.LastTileCensus.EffectSlices;
                OffscreenSplit cSplit = default, pSplit = default;
                byte[] composite = Capture(host, gpu, out int aw, out int ah, () => cSplit = gpu.LastOffscreenSplit);
                host.GpuKnockouts = routeControl | GpuKnockouts.StickyClipInPaint;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                int paintSlices = host.LastTileCensus.EffectSlices;
                byte[] paint = Capture(host, gpu, out int bw, out int bh, () => pSplit = gpu.LastOffscreenSplit);
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 4);
                // both routes must really have run: the wash is cut as its own (effect) slice only on the composite route
                bool routes = compositeSlices > paintSlices;
                Console.Error.WriteLine($"[repaint-identity] {name}: effect slices composite={compositeSlices} paint={paintSlices} groups composite={cSplit.GroupSurfaces}+{cSplit.GroupCacheHits}hit paint={pSplit.GroupSurfaces}+{pSplit.GroupCacheHits}hit{(routes ? "" : "  FAIL — a route did not run")}");
                if (Judge(name, composite, paint, aw, ah, bw, bh, 0, outDir) && routes) passed++; else failed++;
                ScrollAll(host, vertical: 0.0, horizontal: 0.0);
            }

            // ── item-band-identity: a prefixed virtual list under one viewport-fixed item band, jumped deep ──
            if (Want("item-band-identity"))
            {
                total++;
                string name = $"item-band-identity@{scale:0.00}";
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 22;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                // Far past the local-extent window (the thumb drag's jump): the arrange origin re-centres and the rows above
                // it sit at negative local offsets — the shape whose rows the record-time band clip used to cull.
                ScrollAll(host, vertical: 30000.0, horizontal: double.NaN);
                Settle(host, w, 12);
                byte[] composite = Capture(host, gpu, out int aw, out int ah);
                host.GpuKnockouts = GpuKnockouts.ForceFullDirect;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                byte[] direct = Capture(host, gpu, out int bw, out int bh);
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 4);
                // Identity alone cannot see rows that neither route recorded: the band below the feather must be ROWS,
                // top to bottom, down a column inside the rows' own padding (no glyphs there).
                int holes = ItemBandHoles(composite, aw, ah, w.Scale, out int firstHole, out int lastHole);
                Console.Error.WriteLine($"[repaint-identity] {name}: band column non-row px={holes}{(holes == 0 ? "" : $"  FAIL — the band is not covered by rows (device y {firstHole}..{lastHole})")}");
                if (holes != 0) WriteEvidence(outDir, name + "-band", composite, direct, aw, ah);
                if (Judge(name, composite, direct, aw, ah, bw, bh, 1, outDir) && holes == 0) passed++; else failed++;
                RepaintIdentityScene.ResetAll();
                ScrollAll(host, vertical: 0.0, horizontal: 0.0);
            }

            // ── group-cache-identity: a group re-drawn from its retained surface (hit) vs re-rendered (miss) ──
            if (Want("group-cache-identity"))
            {
                total++;
                string name = $"group-cache-identity@{scale:0.00}";
                RepaintIdentityScene.ResetAll();
                RepaintIdentityScene.Scenario.Value = 15;
                host.GpuKnockouts = GpuKnockouts.None;
                host.RequestFullRepaintOnce();
                Settle(host, w, 8);
                ScrollAll(host, vertical: 100.0, horizontal: 220.0);
                Settle(host, w, 8);
                ScrollAll(host, vertical: 163.0, horizontal: double.NaN);   // a page scroll: the group moves rigidly
                Settle(host, w, 8);
                CompositeCacheStats hitCache = default;
                byte[] hit = Capture(host, gpu, out int aw, out int ah, () => hitCache = gpu.LastCompositeCache);
                host.RequestFullRepaintOnce();   // every tile re-rastered ⇒ the key misses ⇒ the group re-renders
                Settle(host, w, 8);
                CompositeCacheStats missCache = default;
                byte[] miss = Capture(host, gpu, out int bw, out int bh, () => missCache = gpu.LastCompositeCache);
                Console.Error.WriteLine($"[repaint-identity] {name}: hit frame rendered={hitCache.GroupSurfaces} hits={hitCache.GroupCacheHits}; miss frame rendered={missCache.GroupSurfaces} hits={missCache.GroupCacheHits}");
                bool pixels = Judge(name, hit, miss, aw, ah, bw, bh, 0, outDir);
                bool exercised = hitCache.GroupCacheHits >= 1 && missCache.GroupSurfaces >= 1;
                if (!exercised) Console.Error.WriteLine($"[repaint-identity] {name}: FAIL  the group cache was not exercised (needs a hit frame and a miss frame)");
                if (pixels && exercised) passed++; else failed++;
                ScrollAll(host, vertical: 0.0, horizontal: 0.0);
            }
        }
        w.SetZoom(1f);
        Console.Error.WriteLine($"[repaint-identity] {passed}/{total} identical, {failed} mismatched");
        return failed == 0 && passed == total ? 0 : 1;
    }

    private static ScrollHandle? FindScroller(AppHost host)
    {
        var scene = host.Scene;
        for (int i = 0; i < scene.Capacity; i++)
        {
            var h = scene.HandleAt(i);
            if (h.IsNull || !scene.IsLive(h) || !scene.TryGetScroll(h, out var sc)) continue;
            if (sc.ContentH <= sc.ViewportH + 1f) continue;
            return host.TryGetScrollHandle(h);
        }
        return null;
    }

    /// <summary>Scroll every live scroller of the scene: vertical ones to <paramref name="vertical"/>, horizontal ones to
    /// <paramref name="horizontal"/> (NaN = leave that axis alone), each clamped to its own range.</summary>
    private static void ScrollAll(AppHost host, double vertical, double horizontal)
    {
        var scene = host.Scene;
        for (int i = 0; i < scene.Capacity; i++)
        {
            var h = scene.HandleAt(i);
            if (h.IsNull || !scene.IsLive(h) || !scene.TryGetScroll(h, out var sc)) continue;
            bool horizontalAxis = sc.Orientation == 1;
            double to = horizontalAxis ? horizontal : vertical;
            if (double.IsNaN(to)) continue;
            host.TryGetScrollHandle(h)?.ScrollTo(to, ScrollMove.Immediate);
        }
    }

    /// <summary>Two captures compared inside <paramref name="region"/> only (device px).</summary>
    private static bool JudgeRegion(string name, byte[] a, byte[] b, int aw, int ah, int bw, int bh, PixelRect region, int tolerance, string outDir)
    {
        if (aw != bw || ah != bh || a.Length != b.Length || a.Length == 0)
        {
            Console.Error.WriteLine($"[repaint-identity] {name}: INCONCLUSIVE — capture size {aw}x{ah} vs {bw}x{bh}");
            return false;
        }
        int diff = 0, maxDelta = 0;
        for (int y = Math.Max(0, region.Top); y < Math.Min(ah, region.Bottom); y++)
            for (int x = Math.Max(0, region.Left); x < Math.Min(aw, region.Right); x++)
            {
                int i = (y * aw + x) * 4;
                int d = Math.Max(Math.Max(Math.Abs(a[i] - b[i]), Math.Abs(a[i + 1] - b[i + 1])),
                                 Math.Max(Math.Abs(a[i + 2] - b[i + 2]), Math.Abs(a[i + 3] - b[i + 3])));
                if (d > maxDelta) maxDelta = d;
                if (d > tolerance) diff++;
            }
        if (diff == 0)
        {
            Console.Error.WriteLine($"[repaint-identity] {name}: PASS  (0 px in {region})");
            return true;
        }
        Console.Error.WriteLine($"[repaint-identity] {name}: FAIL  {diff} px differ in {region} (max {maxDelta}/255)");
        WriteEvidence(outDir, name, a, b, aw, ah);
        return false;
    }

    /// <summary>The frosted plate must NOT be the crisp page: the mean per-channel difference inside
    /// <paramref name="region"/> must exceed <paramref name="minMeanDelta"/> (of 255). A folded acrylic was a transparent
    /// hole — the page showing straight through — which is exactly a near-zero difference here.</summary>
    private static bool DiffersInRegion(string name, byte[] plate, byte[] crisp, int aw, int ah, int bw, int bh, PixelRect region, double minMeanDelta)
    {
        if (aw != bw || ah != bh || plate.Length != crisp.Length || plate.Length == 0) return false;
        double sum = 0; long n = 0;
        for (int y = Math.Max(0, region.Top); y < Math.Min(ah, region.Bottom); y++)
            for (int x = Math.Max(0, region.Left); x < Math.Min(aw, region.Right); x++)
            {
                int i = (y * aw + x) * 4;
                sum += (Math.Abs(plate[i] - crisp[i]) + Math.Abs(plate[i + 1] - crisp[i + 1]) + Math.Abs(plate[i + 2] - crisp[i + 2])) / 3.0;
                n++;
            }
        double mean = n == 0 ? 0 : sum / n;
        bool ok = mean > minMeanDelta;
        Console.Error.WriteLine($"[repaint-identity] {name}: plate vs crisp page mean Δ={mean:0.0}/255 ({(ok ? "frosted" : "FAIL — the plate shows the crisp page: a hole")})");
        return ok;
    }

    /// <summary>Scenario 19: every pixel of the scroll viewport vs G + (B − G)·f — the uncued capture composited over the
    /// ground by the viewport's analytic feather (both scrolled edges live, the standard 40-DIP band, smoothstep), as
    /// <see cref="EdgeFeatherMask.Evaluate(in EdgeFeather, float, float)"/> predicts it: ≤ 1/255 per channel.</summary>
    private static bool JudgeEdgeFeather(string name, byte[] a, byte[] b, byte[] g, int w, int h, float scale, string outDir)
    {
        var rect = new RectF(RepaintIdentityScene.ScrollEdgeX * scale, RepaintIdentityScene.ScrollEdgeY * scale,
            RepaintIdentityScene.ScrollEdgeW * scale, RepaintIdentityScene.ScrollEdgeH * scale);
        float band = 40f * scale;   // ScrollEdgeCueResolver.DefaultBandDip: the standard AutoEdgeFade band
        var feather = new EdgeFeather(rect, 0f, band, 0f, band, default, FadeFalloff.Smoothstep, 1f);
        int x0 = (int)MathF.Ceiling(rect.X) + 1, y0 = (int)MathF.Ceiling(rect.Y) + 1;
        int x1 = (int)MathF.Floor(rect.Right) - 1, y1 = (int)MathF.Floor(rect.Bottom) - 1;
        int worst = 0, bad = 0, worstX = -1, worstY = -1;
        var expected = new byte[a.Length];
        Array.Copy(a, expected, a.Length);
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
            {
                float f = EdgeFeatherMask.Evaluate(in feather, x + 0.5f, y + 0.5f);
                int i = (y * w + x) * 4;
                int d = 0;
                for (int c = 0; c < 3; c++)
                {
                    int e = (int)MathF.Round(g[i + c] + (b[i + c] - g[i + c]) * f);
                    expected[i + c] = (byte)Math.Clamp(e, 0, 255);
                    d = Math.Max(d, Math.Abs(a[i + c] - e));
                }
                if (d > worst) { worst = d; worstX = x; worstY = y; }
                if (d > 1) bad++;
            }
        if (bad == 0)
        {
            Console.Error.WriteLine($"[repaint-identity] {name}: PASS  (max {worst}/255 vs G + (B − G)·EdgeFeatherMask)");
            return true;
        }
        Console.Error.WriteLine($"[repaint-identity] {name}: FAIL  {bad} px over 1/255 (max {worst}/255 at {worstX},{worstY} vs G + (B − G)·EdgeFeatherMask)");
        WriteEvidence(outDir, name, a, expected, w, h);
        return false;
    }

    /// <summary>The nested feathers of scenario 16: every pixel of the panel vs ground + (colour − ground)·f_outer·f_inner.</summary>
    private static bool JudgeFeatherProduct(string name, byte[] px, int w, int h, float scale, string outDir)
    {
        if (px.Length == 0) { Console.Error.WriteLine($"[repaint-identity] {name}: INCONCLUSIVE — no capture"); return false; }
        var rect = new RectF(RepaintIdentityScene.FeatherX * scale, RepaintIdentityScene.FeatherY * scale,
            RepaintIdentityScene.FeatherW * scale, RepaintIdentityScene.FeatherH * scale);
        float outer = RepaintIdentityScene.ProductBandOuter * scale, inner = RepaintIdentityScene.ProductBandInner * scale;
        var fOuter = new EdgeFeather(rect, 0f, outer, 0f, outer, default, FadeFalloff.Smoothstep, 1f);
        var fInner = new EdgeFeather(rect, inner, 0f, inner, 0f, default, FadeFalloff.Smoothstep, 1f);
        ColorF c = RepaintIdentityScene.FeatherColor, g = RepaintIdentityScene.PageGround;
        int x0 = (int)MathF.Ceiling(rect.X) + 1, y0 = (int)MathF.Ceiling(rect.Y) + 1;
        int x1 = (int)MathF.Floor(rect.Right) - 1, y1 = (int)MathF.Floor(rect.Bottom) - 1;
        int worst = 0, bad = 0;
        var expected = new byte[px.Length];
        Array.Copy(px, expected, px.Length);
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
            {
                float f = EdgeFeatherMask.Evaluate(in fOuter, x + 0.5f, y + 0.5f) * EdgeFeatherMask.Evaluate(in fInner, x + 0.5f, y + 0.5f);
                int i = (y * w + x) * 4;
                int eb = Q(c.B * f + g.B * (1f - f)), eg = Q(c.G * f + g.G * (1f - f)), er = Q(c.R * f + g.R * (1f - f));
                expected[i] = (byte)eb; expected[i + 1] = (byte)eg; expected[i + 2] = (byte)er;
                int d = Math.Max(Math.Abs(px[i] - eb), Math.Max(Math.Abs(px[i + 1] - eg), Math.Abs(px[i + 2] - er)));
                if (d > worst) worst = d;
                if (d > 1) bad++;
            }
        if (bad == 0)
        {
            Console.Error.WriteLine($"[repaint-identity] {name}: PASS  (max {worst}/255 vs EdgeFeatherMask product)");
            return true;
        }
        Console.Error.WriteLine($"[repaint-identity] {name}: FAIL  {bad} px over 1/255 (max {worst}/255 vs EdgeFeatherMask product)");
        WriteEvidence(outDir, name, px, expected, w, h);
        return false;

        static int Q(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
    }

    private static bool Judge(string name, byte[] a, byte[] b, int aw, int ah, int bw, int bh, int tolerance, string outDir)
    {
        if (aw != bw || ah != bh || a.Length != b.Length || a.Length == 0)
        {
            Console.Error.WriteLine($"[repaint-identity] {name}: INCONCLUSIVE — capture size {aw}x{ah} vs {bw}x{bh}");
            return false;
        }
        Compare(a, b, aw, ah, tolerance, out int diff, out int l, out int t, out int r, out int bt, out int maxDelta);
        if (diff == 0)
        {
            Console.Error.WriteLine($"[repaint-identity] {name}: PASS  (0 px)");
            return true;
        }
        Console.Error.WriteLine($"[repaint-identity] {name}: FAIL  {diff} px differ (max {maxDelta}/255), bbox [{l},{t} → {r},{bt}]");
        WriteEvidence(outDir, name, a, b, aw, ah);
        return false;
    }

    /// <summary>Every pixel of the feathered panel vs <c>lerp(ground, colour, feather)</c> from the C# evaluator.</summary>
    private static bool JudgeFeather(string name, byte[] px, int w, int h, float scale, string outDir)
    {
        if (px.Length == 0) { Console.Error.WriteLine($"[repaint-identity] {name}: INCONCLUSIVE — no capture"); return false; }
        var rect = new RectF(RepaintIdentityScene.FeatherX * scale, RepaintIdentityScene.FeatherY * scale,
            RepaintIdentityScene.FeatherW * scale, RepaintIdentityScene.FeatherH * scale);
        float band = RepaintIdentityScene.FeatherBand * scale;
        var feather = new EdgeFeather(rect, 0f, band, 0f, band, default, FadeFalloff.Smoothstep, 1f);
        ColorF c = RepaintIdentityScene.FeatherColor, g = RepaintIdentityScene.PageGround;
        int x0 = (int)MathF.Ceiling(rect.X) + 1, y0 = (int)MathF.Ceiling(rect.Y) + 1;
        int x1 = (int)MathF.Floor(rect.Right) - 1, y1 = (int)MathF.Floor(rect.Bottom) - 1;
        int worst = 0, bad = 0;
        var expected = new byte[px.Length];
        Array.Copy(px, expected, px.Length);
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
            {
                float f = EdgeFeatherMask.Evaluate(in feather, x + 0.5f, y + 0.5f);
                int i = (y * w + x) * 4;
                // BGRA8, premultiplied over an opaque ground: out = c·f + g·(1 − f)
                int eb = Q(c.B * f + g.B * (1f - f)), eg = Q(c.G * f + g.G * (1f - f)), er = Q(c.R * f + g.R * (1f - f));
                expected[i] = (byte)eb; expected[i + 1] = (byte)eg; expected[i + 2] = (byte)er;
                int d = Math.Max(Math.Abs(px[i] - eb), Math.Max(Math.Abs(px[i + 1] - eg), Math.Abs(px[i + 2] - er)));
                if (d > worst) worst = d;
                if (d > 1) bad++;
            }
        if (bad == 0)
        {
            Console.Error.WriteLine($"[repaint-identity] {name}: PASS  (max {worst}/255 vs EdgeFeatherMask)");
            return true;
        }
        Console.Error.WriteLine($"[repaint-identity] {name}: FAIL  {bad} px over 1/255 (max {worst}/255 vs EdgeFeatherMask)");
        WriteEvidence(outDir, name, px, expected, w, h);
        return false;

        static int Q(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
    }

    // ── frame driving ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Run until the scene stops producing: <paramref name="quietFrames"/> consecutive attempts that neither
    /// published nor elided anything; then wait for everything published to be PRESENTED before a capture reads it.</summary>
    private static void Settle(AppHost host, Win32Window w, int quietFrames)
    {
        var sw = Stopwatch.StartNew();
        int quiet = 0;
        while (quiet < quietFrames && sw.ElapsedMilliseconds < 4000 && !w.IsClosed)
        {
            ulong before = host.PublishSequence;
            long skippedBefore = host.FramesSkippedSubmit;
            host.RunFrame();
            if (host.PublishSequence != before || host.FramesSkippedSubmit != skippedBefore) quiet = 0;
            else quiet++;
            w.WaitForWork(Math.Clamp(host.RecommendedWaitMs(), 1, 16));
        }
        ulong target = host.PublishSequence;
        while (host.RenderPresentSeq < target && sw.ElapsedMilliseconds < 6000 && !w.IsClosed) w.WaitForWork(1);
    }

    /// <summary>Read the presented back buffer with the render thread PARKED (CaptureBgra resets the command allocator +
    /// fence the render thread otherwise owns).</summary>
    /// <summary>Pixels down one device column of the item-band list, from just below the band's feather to the list's
    /// bottom, 4 DIP inside the rows' left padding, that are NOT the suffix rows' fill (BGRA, ±3).</summary>
    private static int ItemBandHoles(byte[] px, int w, int h, float scale, out int firstHole, out int lastHole)
    {
        firstHole = lastHole = -1;
        if (px.Length == 0) return int.MaxValue;
        const float left = 40.5f, top = 40.25f, height = 540f;
        int x = (int)MathF.Round((left + 4f) * scale);
        int y0 = (int)MathF.Ceiling((top + RepaintIdentityScene.ItemBandInset + RepaintIdentityScene.ItemBandFade) * scale) + 1;
        int y1 = (int)MathF.Floor((top + height) * scale) - 1;
        int holes = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
        {
            int i = (y * w + x) * 4;
            if (Math.Abs(px[i] - 0x58) > 3 || Math.Abs(px[i + 1] - 0x44) > 3 || Math.Abs(px[i + 2] - 0x3A) > 3)
            {
                holes++;
                if (firstHole < 0) firstHole = y;
                lastHole = y;
            }
        }
        return holes;
    }

    private static byte[] Capture(AppHost host, D3D12Device gpu, out int width, out int height, Action? whileParked = null)
    {
        byte[]? px = null;
        int cw = 0, ch = 0;
        if (whileParked is not null) host.RunWithRenderThreadParked(whileParked);   // read render-thread census first
        host.RunWithRenderThreadParked(() => { px = gpu.CaptureBgra(out cw, out ch); });
        width = cw; height = ch;
        return px ?? [];
    }

    private static void Compare(byte[] a, byte[] b, int w, int h, int tolerance,
        out int diffPixels, out int left, out int top, out int right, out int bottom, out int maxDelta)
    {
        diffPixels = 0; maxDelta = 0; left = int.MaxValue; top = int.MaxValue; right = int.MinValue; bottom = int.MinValue;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                int d = Math.Max(Math.Max(Math.Abs(a[i] - b[i]), Math.Abs(a[i + 1] - b[i + 1])),
                                 Math.Max(Math.Abs(a[i + 2] - b[i + 2]), Math.Abs(a[i + 3] - b[i + 3])));
                if (d > maxDelta) maxDelta = d;
                if (d <= tolerance) continue;
                diffPixels++;
                if (x < left) left = x;
                if (x + 1 > right) right = x + 1;
                if (y < top) top = y;
                if (y + 1 > bottom) bottom = y + 1;
            }
        }
    }

    private static void WriteEvidence(string outDir, string name, byte[] a, byte[] b, int w, int h)
    {
        try
        {
            System.IO.Directory.CreateDirectory(outDir);
            string stem = System.IO.Path.Combine(outDir, name.Replace('/', '_').Replace('@', '_'));
            PngWriter.WriteBgra($"{stem}-a.png", a, w, h);
            PngWriter.WriteBgra($"{stem}-b.png", b, w, h);
            byte[] diff = new byte[a.Length];
            for (int i = 0; i < a.Length; i += 4)
            {
                bool differs = a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3];
                diff[i] = differs ? (byte)0xFF : (byte)0x10;
                diff[i + 1] = differs ? (byte)0x00 : (byte)0x10;
                diff[i + 2] = differs ? (byte)0xFF : (byte)0x10;
                diff[i + 3] = 0xFF;
            }
            PngWriter.WriteBgra($"{stem}-diff.png", diff, w, h);
            Console.Error.WriteLine($"[repaint-identity]   evidence: {stem}-{{a,b,diff}}.png");
        }
        catch (Exception e) { Console.Error.WriteLine($"[repaint-identity]   (evidence write failed: {e.Message})"); }
    }
}
