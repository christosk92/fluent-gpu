using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scroll.Motion;
using FluentGpu.Text;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Layout;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// P0 of the retained tiled content layer (docs/plans/scroll-gpu-retained-tiles-implementation.md §A, §E): the PURE
/// decisions every later phase builds on, driven headlessly with no GPU — the needed-set order and its coverage clamp
/// (<see cref="TileGrid.Needed(in RectF, double, in MotionFeel, double, double, bool, Span{TileKey}, Span{byte}, out int, int)"/>),
/// the <see cref="SliceTable"/>'s exposed-only raster scheduling, one-tile invalidation, the budget's never-drop-visible
/// eviction/degrade, the analytic <see cref="EdgeFeatherMask"/>, the headless <see cref="IGpuDevice.SubmitComposite"/>
/// record, and zero allocation across Needed/invalidate/evict/submit.
/// </summary>
static class TileSuite
{
    const long MiB = 1024 * 1024;
    const long BigBudget = 96 * MiB;   // 48 tiles
    static readonly SliceFrame Unit = new(0, 0, 0f, 0f, 1f);

    public static void Run(StringTable strings)
    {
        NeededOrderChecks();
        CoverageClampChecks();
        ExposedOnlyChecks();
        InvalidationOneChecks();
        BudgetChecks();
        FeatherChecks();
        CompositeRecordChecks();
        AllocZeroChecks();
        var fonts = new HeadlessFontSystem(strings);
        ScrollNoCopyChecks(strings, fonts);
        InvalidationOneEndToEndChecks(strings, fonts);
        NoBlankChecks(strings, fonts);
        SegmentExtentChecks(strings, fonts);
        RenderAllocZeroChecks(strings, fonts);
        RepaintBoundaryChecks(strings, fonts);
        CompositePoseChecks(strings, fonts);
        InvisibleBoundsChecks(strings, fonts);
        OpaqueCoverChecks(strings, fonts);
        TilePaintChecks(strings, fonts);
    }

    // ── gate.tiles.paint ─────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A small box in a segment whose bounds a second box far away stretches over the whole window: every tile
    /// placement carries the part of its tile its ops paint, so the composite draws two small quads, not the cells.</summary>
    sealed class SparseProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Grow = 1f, ZStack = true,
            Children =
            [
                new BoxEl { AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(40f, 40f, 0f, 0f), Width = 160f, Height = 40f, Fill = ColorF.FromRgba(200, 90, 60) },
                new BoxEl { AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(1000f, 900f, 0f, 0f), Width = 120f, Height = 60f, Fill = ColorF.FromRgba(60, 90, 200) },
            ],
        };
    }

    static void TilePaintChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, window, dev, host) = Host("tiles-paint", strings, fonts, new SparseProbe());
        using var _a = app; using var _h = host;
        Frames(host, 20);
        long cells = 0, painted = 0;
        bool inside = true;
        string seen = "";
        foreach (var p in dev.LastCompositePlacements)
        {
            cells += (long)p.W * p.H;
            painted += (long)Math.Max(0, p.Px1 - p.Px0) * Math.Max(0, p.Py1 - p.Py0);
            inside &= p.Px0 >= 0 && p.Py0 >= 0 && p.Px1 <= p.W && p.Py1 <= p.H;
            seen += $" [{p.Key.Tx},{p.Key.Ty} {p.W}x{p.H} paint {p.Px0},{p.Py0}-{p.Px1},{p.Py1}]";
        }
        Check("gate.tiles.paint a sparse segment's placements composite only what their tiles paint (two small boxes, not the cells)",
            cells > 0 && painted > 0 && painted * 20 < cells && inside, $"cells={cells} painted={painted}{seen}");
    }

    // ── gate.tiles.opaque-cover ──────────────────────────────────────────────────────────────────────────────────
    /// <summary>A page under an overlay in its own repaint boundary (the Wavee stage over the app shell). The overlay's
    /// composite item carries <see cref="CompositeItem.Opaque"/> — the window-px rect it paints fully opaque, which lets the
    /// backend leave out what it hides — ONLY when that is true: an opaque square fill at alpha 1, opacity 1.</summary>
    sealed class CoverProbe(int mode) : Component
    {
        // 0 opaque full-window (tiles) · 1 the same at RasterScale 1/4 · 2 a translucent fill · 3 rounded corners ·
        // 4 opacity 0.5 · 5 opaque at fractional insets
        public override Element Render()
        {
            var page = new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(40f, 40f, 0f, 0f),
                Width = 300f, Height = 200f, Fill = ColorF.FromRgba(200, 90, 60),
            };
            var overlay = new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, RepaintBoundary = true,
                RasterScale = mode == 1 ? 0.25f : 1f,
                Margin = mode == 5 ? new Edges4(40.5f, 30.25f, 60f, 50.75f) : default,
                Fill = mode == 2 ? ColorF.FromRgba(20, 22, 28, 0xF0) : ColorF.FromRgba(20, 22, 28),
                Corners = mode == 3 ? CornerRadius4.All(12f) : default,
                Opacity = mode == 4 ? 0.5f : 1f,
                Children = [new BoxEl { AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Width = 100f, Height = 100f, Fill = ColorF.FromRgba(90, 160, 220) }],
            };
            return new BoxEl { Grow = 1f, ZStack = true, Children = [page, overlay] };
        }
    }

    static RectF CoverOf(StringTable strings, HeadlessFontSystem fonts, int mode, out bool found)
    {
        var (app, window, dev, host) = Host("tiles-cover-" + mode, strings, fonts, new CoverProbe(mode));
        using var _a = app; using var _h = host;
        Frames(host, 20);
        int effect = -1;
        foreach (var row in dev.LastCompositeSlices) if (row.Kind == SliceKind.Effect) effect = row.Id;
        RectF opaque = default;
        found = false;
        foreach (var op in dev.LastCompositeRecords)
            if (op.Kind == CompositeRecordKind.DrawItem && op.Item.SliceId == effect && effect >= 0) { opaque = op.Item.Opaque; found = true; }
        return opaque;
    }

    static void OpaqueCoverChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var tiles = CoverOf(strings, fonts, 0, out bool f0);
        Check("gate.tiles.opaque-cover an opaque full-window overlay boundary covers the window",
            f0 && tiles.X <= 0f && tiles.Y <= 0f && tiles.Right >= 1200f && tiles.Bottom >= 1000f, $"found={f0} opaque={tiles}");
        var low = CoverOf(strings, fonts, 1, out bool f1);
        Check("gate.tiles.opaque-cover the same overlay at RasterScale 1/4 carries the same cover (the backend maps it through the upsample)",
            f1 && low.X <= 0f && low.Y <= 0f && low.Right >= 1200f && low.Bottom >= 1000f, $"found={f1} opaque={low}");
        var inset = CoverOf(strings, fonts, 5, out bool f5);
        Check("gate.tiles.opaque-cover fractional insets keep the fill's exact edges (the backend rounds them in)",
            f5 && inset.X == 40.5f && inset.Y == 30.25f && inset.Right == 1140f && inset.Bottom == 949.25f, $"found={f5} opaque={inset}");
        // the overlay's own fill claims nothing when it is translucent or rounded: the cover left is the opaque square
        // centred inside it (550,450 100x100); at opacity 0.5 nothing in the overlay is opaque
        string seen = "";
        bool exact = true;
        foreach (int m in (int[])[2, 3, 4])
        {
            var r = CoverOf(strings, fonts, m, out bool f);
            bool want = m == 4 ? r.W <= 0f && r.H <= 0f : r.X == 550f && r.Y == 450f && r.W == 100f && r.H == 100f;
            if (!f || !want) exact = false;
            seen += $" mode{m}: found={f} opaque={r.X},{r.Y} {r.W}x{r.H}";
        }
        Check("gate.tiles.opaque-cover a translucent fill, rounded corners or opacity 0.5 claim no cover of their own", exact, seen);
    }

    // ── gate.tiles.invisible-bounds ──────────────────────────────────────────────────────────────────────────────
    /// <summary>A full-window plate parked at opacity 0 (Wavee's setup cover scrim, always mounted) after a small box: the
    /// plate paints nothing, so the box's segment must not hold a window of tiles for it.</summary>
    sealed class PlateProbe(int mode) : Component   // 0 = no plate, 1 = plate at opacity 0, 2 = plate at opacity 1
    {
        public override Element Render()
        {
            var box = new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(40f, 40f, 0f, 0f),
                Width = 160f, Height = 40f, Fill = ColorF.FromRgba(200, 90, 60),
            };
            Element[] kids = mode == 0 ? [box]
                : [box, new BoxEl { Grow = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                       Fill = ColorF.FromRgba(0, 0, 0, 0x4D), Opacity = mode == 1 ? 0f : 1f, HitTestVisible = false }];
            return new BoxEl { Grow = 1f, ZStack = true, Children = kids };
        }
    }

    static void InvisibleBoundsChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        long Tiles(int mode)
        {
            var (app, window, dev, host) = Host("tiles-plate-" + mode, strings, fonts, new PlateProbe(mode));
            using var _a = app; using var _h = host;
            Frames(host, 20);
            return host.LastTileCensus.VisibleNeedBytes;
        }
        long none = Tiles(0), hidden = Tiles(1), shown = Tiles(2);
        Check("gate.tiles.invisible-bounds a full-window plate at opacity 0 adds no tiles to its segment",
            hidden == none, $"visible tile bytes: no plate={none} plate@0={hidden} plate@1={shown}");
        Check("gate.tiles.invisible-bounds the same plate at opacity 1 is painted and tiled",
            shown > none, $"visible tile bytes: no plate={none} plate@1={shown}");
    }


    // ── gate.tiles.repaint-boundary ──────────────────────────────────────────────────────────────────────────────
    /// <summary>A moving shape UNDER static content (the fullscreen stage's drifting Field under its scrim, bars and
    /// panels): inline, every tile it crosses re-rasters with all the static paint in it; behind a BoxEl.RepaintBoundary
    /// it re-rasters only its own isolation slice and the static tiles stay valid.</summary>
    sealed class BoundaryProbe(bool boundary, float rasterScale = 1f) : Component
    {
        public static readonly Signal<int> Shift = new(0);
        public override Element Render()
        {
            var cells = new Element[24];
            for (int i = 0; i < cells.Length; i++)
            {
                int c = i % 6, r = i / 6;
                cells[i] = new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(50f + c * 190f, 50f + r * 230f, 0f, 0f), Width = 150f, Height = 150f,
                    Fill = ColorF.FromRgba(40, 44, (byte)(60 + i * 4)),
                };
            }
            var blob = new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = 900f, Height = 700f, Corners = CornerRadius4.All(350f),
                Fill = ColorF.FromRgba(120, 60, 160),
                Transform = Prop.Of(() => Affine2D.Translation(Shift.Value * 7f, Shift.Value * 3f)),
            };
            return new BoxEl
            {
                Grow = 1f, ZStack = true, Fill = ColorF.FromRgba(18, 18, 22),
                Children =
                [
                    new BoxEl { Grow = 1f, ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, RepaintBoundary = boundary, RasterScale = rasterScale, Children = [blob] },
                    new BoxEl { Grow = 1f, ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Children = cells },
                ],
            };
        }
    }

    static (int Rasters, int Outside, int EffectSlice) BoundaryRun(StringTable strings, HeadlessFontSystem fonts, bool boundary)
        => BoundaryRun(strings, fonts, boundary, 1f, out _);

    static (int Rasters, int Outside, int EffectSlice) BoundaryRun(StringTable strings, HeadlessFontSystem fonts, bool boundary,
        float rasterScale, out int lowResItems)
    {
        BoundaryProbe.Shift.Value = 0;
        lowResItems = 0;
        var (app, window, dev, host) = Host(boundary ? "tiles-boundary" : "tiles-boundary-inline", strings, fonts, new BoundaryProbe(boundary, rasterScale));
        using var _a = app; using var _h = host;
        Frames(host, 30);
        int effect = -1;
        foreach (var row in dev.LastCompositeSlices) if (row.Kind == SliceKind.Effect) effect = row.Id;
        int rasters = 0, outside = 0;
        int seen = dev.CompositeFrameCount;
        for (int step = 0; step < 4; step++)
        {
            BoundaryProbe.Shift.Value++;
            for (int i = 0; i < 4; i++)
            {
                host.RunFrame();
                if (dev.CompositeFrameCount == seen) continue;
                seen = dev.CompositeFrameCount;
                foreach (var op in dev.LastCompositeRecords)
                {
                    if (op.Kind == CompositeRecordKind.DrawItem && op.Item.Kind == CompositeKind.Direct && op.Item.LowResDown == 4) lowResItems++;
                    if (op.Kind != CompositeRecordKind.RasterTile) continue;
                    rasters++;
                    if (op.Tile.SliceId != effect) outside++;
                }
            }
        }
        return (rasters, outside, effect);
    }

    static void RepaintBoundaryChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var inline = BoundaryRun(strings, fonts, boundary: false);
        var cut = BoundaryRun(strings, fonts, boundary: true);
        Check("gate.tiles.repaint-boundary control: INLINE, a shape moving under static cells re-rasters the static tiles it crosses",
            inline.EffectSlice < 0 && inline.Outside > 0, $"inline rasters={inline.Rasters} outside={inline.Outside} effect={inline.EffectSlice}");
        Check("gate.tiles.repaint-boundary a RepaintBoundary cuts its own Effect slice and the move re-rasters ONLY that slice's tiles",
            cut.EffectSlice >= 0 && cut.Rasters > 0 && cut.Outside == 0,
            $"boundary rasters={cut.Rasters} outside={cut.Outside} effect={cut.EffectSlice} (inline rasters={inline.Rasters})");
        var low = BoundaryRun(strings, fonts, boundary: true, rasterScale: 0.25f, out int lowItems);
        Check("gate.tiles.repaint-boundary RasterScale 1/4: the boundary composites as ONE low-resolution Direct item (LowResDown 4) and rasters NO tiles, its own or the static ones",
            low.EffectSlice >= 0 && lowItems > 0 && low.Rasters == 0,
            $"lowres items={lowItems} rasters={low.Rasters} outside={low.Outside} effect={low.EffectSlice}");
        Check("gate.tiles.repaint-boundary RasterScale snaps to the downscale ladder (≥0.75→1, 0.5→2, 0.25→4, below→8)",
            SceneStore.RasterDown(1f) == 1 && SceneStore.RasterDown(0.8f) == 1 && SceneStore.RasterDown(0.5f) == 2
            && SceneStore.RasterDown(0.25f) == 4 && SceneStore.RasterDown(0.1f) == 8 && SceneStore.RasterDown(float.NaN) == 1,
            $"1→{SceneStore.RasterDown(1f)} 0.5→{SceneStore.RasterDown(0.5f)} 0.25→{SceneStore.RasterDown(0.25f)} 0.1→{SceneStore.RasterDown(0.1f)}");
        BoundaryProbe.Shift.Value = 0;
    }

    // ── gate.tiles.composite-pose ────────────────────────────────────────────────────────────────────────────────
    /// <summary>BoxEl.CompositePose: ONE full-bleed photo whose scale + translate step every frame (the visualizer's hero pan).
    /// Eligible, it composites as an Image item at the current pose and rasters no tile; with rounded corners the stream is
    /// ineligible and the box falls back to the ordinary tiled boundary (counted).</summary>
    sealed class PosedProbe(float corners) : Component
    {
        public static readonly Signal<int> Step = new(0);
        public override Element Render() => new BoxEl
        {
            Grow = 1f, ZStack = true, Fill = ColorF.FromRgba(18, 18, 22),
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = 800f, Height = 500f, ClipToBounds = true,
                    CompositePose = true,
                    Transform = Prop.Of(() => Affine2D.Scale(1f + Step.Value * 0.01f, 1f + Step.Value * 0.01f).Multiply(Affine2D.Translation(Step.Value * 0.5f, 0f))),
                    Children = [Ui.Image("album/posed.jpg", ImageFit.Cover, float.NaN, 512f, corners) with { Width = 800f, Height = 500f }],
                },
            ],
        };
    }

    static (int Rasters, int ImageItems, int Fallbacks, bool Moved) PosedRun(StringTable strings, HeadlessFontSystem fonts, float corners)
    {
        PosedProbe.Step.Value = 0;
        var (app, window, dev, host) = Host(corners > 0f ? "tiles-posed-rounded" : "tiles-posed", strings, fonts, new PosedProbe(corners));
        using var _a = app; using var _h = host;
        Frames(host, 30);
        int rasters = 0, imageItems = 0;
        float firstM11 = float.NaN, lastM11 = float.NaN;
        int seen = dev.CompositeFrameCount;
        for (int step = 0; step < 60; step++)
        {
            PosedProbe.Step.Value++;
            host.RunFrame();
            if (dev.CompositeFrameCount == seen) continue;
            seen = dev.CompositeFrameCount;
            foreach (var op in dev.LastCompositeRecords)
                if (op.Kind == CompositeRecordKind.RasterTile) rasters++;
            foreach (var it in host.UiSlices.LastItems)
                if (it.Kind == CompositeKind.Image) { imageItems++; if (float.IsNaN(firstM11)) firstM11 = it.Transform.M11; lastM11 = it.Transform.M11; }
        }
        return (rasters, imageItems, host.UiSlices.PoseFallbacks, !float.IsNaN(firstM11) && lastM11 > firstM11);
    }

    static void CompositePoseChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var posed = PosedRun(strings, fonts, corners: 0f);
        Check("gate.tiles.composite-pose an eligible CompositePose photo composites as Image items whose scale grows step by step and rasters NO tile",
            posed.ImageItems > 0 && posed.Moved && posed.Rasters == 0 && posed.Fallbacks == 0,
            $"imageItems={posed.ImageItems} moved={posed.Moved} rasters={posed.Rasters} fallbacks={posed.Fallbacks}");
        var rounded = PosedRun(strings, fonts, corners: 12f);
        Check("gate.tiles.composite-pose rounded corners make the stream ineligible: no Image item, the tiled boundary route takes over and the fallback is counted",
            rounded.ImageItems == 0 && rounded.Fallbacks > 0,
            $"imageItems={rounded.ImageItems} rasters={rounded.Rasters} fallbacks={rounded.Fallbacks}");
        PosedProbe.Step.Value = 0;
    }

    // ── gate.tiles.needed-order ─────────────────────────────────────────────────────────────────────────────────
    static void NeededOrderChecks()
    {
        MotionFeel feel = FeelProfiles.Standard;   // LookaheadS = 0.025 s
        Span<TileKey> keys = stackalloc TileKey[128];
        Span<byte> ord = stackalloc byte[128];
        var vp = new RectF(0, 1000, 2560, 1600);    // rows 1..5 (y 1000..2600), cols 0..2

        TileGrid.Needed(vp, 0.0, feel, 0.0, 100_000.0, false, keys, ord, out int n, 7);
        bool visibleFirst = n == 21;
        for (int i = 0; i < 15 && visibleFirst; i++)
            visibleFirst = ord[i] == TileGrid.OrderVisible && keys[i] == new TileKey(7, (short)(i % 3), (short)(1 + i / 3));
        Check("gate.tiles.needed-order at rest the 15 visible tiles (rows 1..5 × cols 0..2) come first, row-major, order 0",
            visibleFirst, $"n={n}");
        bool restBehind = n == 21;
        for (int i = 15; i < n && restBehind; i++)
            restBehind = ord[i] == TileGrid.OrderBehind && (keys[i].Ty == 0 || keys[i].Ty == 6);
        Check("gate.tiles.needed-order at rest one row is retained behind on BOTH sides (rows 0 and 6, order 2), nothing ahead",
            restBehind, $"n={n}");

        TileGrid.Needed(vp, 20_000.0, feel, 0.0, 100_000.0, false, keys, ord, out n, 7);   // lead = 500 px → y 3100 → row 6
        bool fwd = n == 21 && ord[14] == 0;
        for (int i = 15; i < 18 && fwd; i++) fwd = ord[i] == TileGrid.OrderAhead && keys[i].Ty == 6;
        for (int i = 18; i < 21 && fwd; i++) fwd = ord[i] == TileGrid.OrderBehind && keys[i].Ty == 0;
        Check("gate.tiles.needed-order moving down: visible (0), then the |v|·LookaheadS band below (row 6, order 1), then one row behind above (row 0, order 2)",
            fwd, $"n={n}");

        TileGrid.Needed(vp, -20_000.0, feel, 0.0, 100_000.0, false, keys, ord, out n, 7);  // lead = 500 px → y 500 → row 0
        bool back = n == 21;
        for (int i = 15; i < 18 && back; i++) back = ord[i] == TileGrid.OrderAhead && keys[i].Ty == 0;
        for (int i = 18; i < 21 && back; i++) back = ord[i] == TileGrid.OrderBehind && keys[i].Ty == 6;
        Check("gate.tiles.needed-order moving up mirrors it: the ahead band is row 0, the retained-behind row is row 6",
            back, $"n={n}");

        TileGrid.Needed(vp, 200_000.0, feel, 0.0, 100_000.0, false, keys, ord, out n, 7);  // lead 5000 px → rows 6..14, nearest first
        bool nearestFirst = n > 15;
        int prev = 5;
        for (int i = 15; i < n && ord[i] == TileGrid.OrderAhead && nearestFirst; i += 3) { nearestFirst = keys[i].Ty == prev + 1; prev = keys[i].Ty; }
        Check("gate.tiles.needed-order a fast fling's ahead band is emitted nearest row first (budget pressure drops the far rows)",
            nearestFirst && prev == 14, $"lastAheadRow={prev}");

        var hvp = new RectF(3000, 0, 1600, 900);    // horizontal: cols 2..4 (x 3000..4600), rows 0..1
        TileGrid.Needed(hvp, 0.0, feel, 0.0, 1e6, true, keys, ord, out n, 2);
        bool horiz = n >= 6;
        for (int i = 0; i < 6 && horiz; i++) horiz = ord[i] == 0 && keys[i].Tx >= 2 && keys[i].Tx <= 4 && keys[i].Ty <= 1;
        Check("gate.tiles.needed-order a horizontal scroller runs the main axis along X (visible cols 2..4 × rows 0..1)",
            horiz && n == 10, $"n={n}");

        TileGrid.Needed(vp, 0.0, feel, 0.0, 100_000.0, false, keys[..4], ord, out n, 7);
        Check("gate.tiles.needed-order a too-small destination truncates in priority order (visible first)",
            n == 4 && ord[3] == 0 && keys[3] == new TileKey(7, 0, 2), $"n={n}");
    }

    // ── gate.tiles.coverage-clamp ───────────────────────────────────────────────────────────────────────────────
    static void CoverageClampChecks()
    {
        MotionFeel feel = FeelProfiles.Standard;
        Span<TileKey> keys = stackalloc TileKey[128];
        Span<byte> ord = stackalloc byte[128];
        var vp = new RectF(0, 1000, 2560, 1600);

        TileGrid.Needed(vp, 20_000.0, feel, 1200.0, 2000.0, false, keys, ord, out int n, 1);   // coverage rows 2..3 only
        bool inside = n == 6;
        for (int i = 0; i < n && inside; i++) inside = keys[i].Ty >= 2 && keys[i].Ty <= 3 && ord[i] == 0;
        Check("gate.tiles.coverage-clamp only tile rows overlapping the realized coverage [1200,2000) are requested — no ahead/behind row past it",
            inside, $"n={n}");

        TileGrid.Needed(vp, 0.0, feel, 0.0, 1024.0, false, keys, ord, out n, 1);   // coverage ends exactly on row 2's top edge
        bool edge = n > 0;
        for (int i = 0; i < n && edge; i++) edge = keys[i].Ty <= 1;
        Check("gate.tiles.coverage-clamp coverage ending ON a tile boundary does not request the next tile (half-open)",
            edge, $"n={n}");

        TileGrid.Needed(vp, 0.0, feel, 5000.0, 5000.0, false, keys, ord, out n, 1);
        Check("gate.tiles.coverage-clamp an empty coverage (no realized rows) requests nothing", n == 0, $"n={n}");

        TileGrid.Needed(vp, 0.0, feel, 50_000.0, 60_000.0, false, keys, ord, out n, 1);
        Check("gate.tiles.coverage-clamp a viewport outside coverage requests nothing (a tile with no realized rows is never requested)",
            n == 0, $"n={n}");
    }

    // One single-slice render turn: open, request the needed set, resolve, optionally mark every raster done.
    static int Turn(SliceTable table, int frame, in RectF vp, double v, double c0, double c1, long budget,
        Span<TileKey> keys, Span<byte> ord, Span<TileRaster> raster, bool markRastered, int node = 1)
    {
        table.BeginFrame(frame);
        int s = table.OpenSlice(node, 1, SliceKind.Scroll, Unit, vp);
        TileGrid.Needed(vp, v, FeelProfiles.Standard, c0, c1, false, keys, ord, out int n, s);
        table.Request(s, vp, c0, c1, false, keys[..n], ord[..n]);
        table.Resolve(budget, raster, out int rc);
        if (markRastered) for (int i = 0; i < rc; i++) table.MarkRastered(raster[i].Key);
        table.EndFrame();
        return rc;
    }

    // ── gate.tiles.exposed-only ─────────────────────────────────────────────────────────────────────────────────
    static void ExposedOnlyChecks()
    {
        var table = new SliceTable();
        Span<TileKey> keys = stackalloc TileKey[128];
        Span<byte> ord = stackalloc byte[128];
        Span<TileRaster> raster = stackalloc TileRaster[128];
        Span<TileKey> before = stackalloc TileKey[128];
        const double cover = 100_000.0;

        var vp0 = new RectF(0, 0, 2560, 1600);   // rows 0..3 + behind row 4 (row −1 is off coverage)
        int first = Turn(table, 1, vp0, 0.0, 0.0, cover, BigBudget, keys, ord, raster, true);
        bool firstAll = first == 15;
        for (int i = 0; i < first && firstAll; i++) firstAll = raster[i].Reason == InvalidationReason.NoTexture;
        Check("gate.tiles.exposed-only the first turn rasters every needed tile once (12 visible + 3 retained-behind), reason NoTexture",
            firstAll, $"rastered={first}");
        TileGrid.Needed(vp0, 0.0, FeelProfiles.Standard, 0.0, cover, false, before, Span<byte>.Empty, out int nBefore, 0);

        int still = Turn(table, 2, vp0, 0.0, 0.0, cover, BigBudget, keys, ord, raster, true);
        Check("gate.tiles.exposed-only a tick with no scroll and no damage rasters 0 tiles", still == 0, $"rastered={still}");

        var vp1 = new RectF(0, 600, 2560, 1600);   // scroll 600 px: rows 1..4 visible, behind rows 0 and 5
        int moved = Turn(table, 3, vp1, 0.0, 0.0, cover, BigBudget, keys, ord, raster, false);
        TileGrid.Needed(vp1, 0.0, FeelProfiles.Standard, 0.0, cover, false, keys, ord, out int nAfter, 0);
        int entering = 0;
        for (int i = 0; i < nAfter; i++) if (!Contains(before[..nBefore], keys[i])) entering++;
        bool onlyNew = moved == entering && entering == 3;
        for (int i = 0; i < moved && onlyNew; i++)
            onlyNew = !Contains(before[..nBefore], new TileKey(0, raster[i].Key.Tx, raster[i].Key.Ty)) && raster[i].Key.Ty == 5;
        Check("gate.tiles.exposed-only a pure scroll of 600 px rasters ONLY the newly entering tiles (row 5 × 3 cols) — retained tiles are reused",
            onlyNew, $"rastered={moved} entering={entering}");
    }

    static bool Contains(ReadOnlySpan<TileKey> set, TileKey k)
    {
        for (int i = 0; i < set.Length; i++) if (set[i].Tx == k.Tx && set[i].Ty == k.Ty) return true;
        return false;
    }

    // ── gate.tiles.invalidation-one ─────────────────────────────────────────────────────────────────────────────
    static void InvalidationOneChecks()
    {
        var table = new SliceTable();
        Span<TileKey> keys = stackalloc TileKey[128];
        Span<byte> ord = stackalloc byte[128];
        Span<TileRaster> raster = stackalloc TileRaster[128];
        var vp = new RectF(0, 0, 2560, 1600);
        Turn(table, 1, vp, 0.0, 0.0, 100_000.0, BigBudget, keys, ord, raster, true);

        table.BeginFrame(2);
        int s = table.OpenSlice(1, 1, SliceKind.Scroll, Unit, vp);
        int hit = table.InvalidateRect(s, new RectF(1100, 1100, 100, 40), InvalidationReason.Content);   // inside tile (1, 2)
        table.TryGetTile(new TileKey(s, 1, 2), out TileState t12);
        int othersInvalid = 0;
        for (short ty = 0; ty <= 4; ty++)
            for (short tx = 0; tx <= 2; tx++)
                if ((tx != 1 || ty != 2) && table.TryGetTile(new TileKey(s, tx, ty), out TileState o) && o.Invalid != InvalidationReason.None) othersInvalid++;
        Check("gate.tiles.invalidation-one a damage rect inside one tile invalidates exactly that tile, reason Content",
            hit == 1 && t12.Invalid == InvalidationReason.Content && othersInvalid == 0
            && table.InvalidationCount(InvalidationReason.Content) == 1,
            $"hit={hit} reason={t12.Invalid} others={othersInvalid}");

        TileGrid.Needed(vp, 0.0, FeelProfiles.Standard, 0.0, 100_000.0, false, keys, ord, out int n, s);
        table.Request(s, vp, 0.0, 100_000.0, false, keys[..n], ord[..n]);
        table.Resolve(BigBudget, raster, out int rc);
        Check("gate.tiles.invalidation-one the next turn re-rasters that one tile (same surface, reason Content) and nothing else",
            rc == 1 && raster[0].Key == new TileKey(s, 1, 2) && raster[0].Reason == InvalidationReason.Content && raster[0].Surface == t12.Surface,
            $"rastered={rc}");
        table.MarkRastered(raster[0].Key);
        table.EndFrame();

        table.BeginFrame(3);
        s = table.OpenSlice(1, 1, SliceKind.Scroll, Unit, vp);
        int straddle = table.InvalidateRect(s, new RectF(1000, 1000, 48, 48), InvalidationReason.Content);   // crosses x 1024 and y 1024
        int edgeOnly = table.InvalidateRect(s, new RectF(0, 512, 100, 0.0001f), InvalidationReason.Content);
        Check("gate.tiles.invalidation-one a rect crossing a tile corner hits the 4 tiles it overlaps",
            straddle == 4, $"hit={straddle} (a sliver on the row-1 top edge hit {edgeOnly})");

        table.OpenSlice(1, 1, SliceKind.Scroll, new SliceFrame(0, 0, 0f, 0f, 1.5f), vp);
        Check("gate.tiles.invalidation-one a raster-scale change invalidates the whole slice, reason ScaleChanged",
            table.InvalidationCount(InvalidationReason.ScaleChanged) == 15 - straddle - edgeOnly,
            $"scaleChanged={table.InvalidationCount(InvalidationReason.ScaleChanged)} (15 tiles − {straddle + edgeOnly} already invalid)");
        table.EndFrame();
    }

    // ── gate.tiles.budget-never-drops-visible ───────────────────────────────────────────────────────────────────
    static void BudgetChecks()
    {
        Span<TileKey> keys = stackalloc TileKey[128];
        Span<byte> ord = stackalloc byte[128];
        Span<TileRaster> raster = stackalloc TileRaster[128];
        MotionFeel feel = FeelProfiles.Standard;
        const double cover = 100_000.0;

        // Distance-weighted LRU: a jump leaves rows 0..2 unused (9 tiles, budget 12). The 6 new visible tiles take the 3
        // free surfaces + evict row 0 (farthest); the retained-behind row 3 then evicts row 1 — row 2, the nearest, survives.
        {
            var table = new SliceTable();
            long budget = 12 * TileBudget.TileBytes;
            var vpTop = new RectF(0, 0, 2560, 1024);              // rows 0..1 (+ behind row 2)
            Turn(table, 1, vpTop, 0.0, 0.0, cover, budget, keys, ord, raster, true);
            var vpFar = new RectF(0, 4 * 512, 2560, 1024);        // rows 4..5 (+ behind row 3; coverage ends at row 5)
            table.BeginFrame(2);
            int s = table.OpenSlice(1, 1, SliceKind.Scroll, Unit, vpFar);
            TileGrid.Needed(vpFar, 1.0, feel, 0.0, 3072.0, false, keys, ord, out int n, s);
            table.Request(s, vpFar, 0.0, 3072.0, false, keys[..n], ord[..n]);
            table.Resolve(budget, raster, out int rc);
            table.TryGetTile(new TileKey(s, 0, 0), out TileState row0);
            table.TryGetTile(new TileKey(s, 0, 2), out TileState row2);
            int visibleResident = 0;
            for (short ty = 4; ty <= 5; ty++) for (short tx = 0; tx <= 2; tx++)
                if (table.TryGetTile(new TileKey(s, tx, ty), out TileState v) && v.Surface >= 0) visibleResident++;
            Check("gate.tiles.budget-never-drops-visible eviction is LRU weighted by distance: the tiles farthest from the new viewport go first",
                visibleResident == 6 && row0.Surface < 0 && row0.Invalid == InvalidationReason.Evicted && row2.Surface >= 0
                && row2.Invalid == InvalidationReason.None && rc == 9,
                $"visibleResident={visibleResident} row0={row0.Invalid} row2Surface={row2.Surface} rastered={rc}");
            table.EndFrame();
        }

        // Two slices, 8-tile budget: A is visible on 6 tiles (+2 ahead); B needs 4 visible → B degrades, A keeps every
        // visible tile.
        {
            var table = new SliceTable();
            long budget = 8 * TileBudget.TileBytes;
            var vpA = new RectF(0, 0, 2560, 1024);
            var contentA = new RectF(0, 0, 3072, 1024);           // three whole tile columns: every tile a full surface
            table.BeginFrame(1);
            int a = table.OpenSlice(1, 1, SliceKind.Scroll, Unit, contentA);
            TileGrid.Needed(vpA, 40_000.0, feel, 0.0, cover, false, keys, ord, out int n, a);    // lead 1000 px → rows 2..3 ahead
            table.Request(a, vpA, 0.0, cover, false, keys[..n], ord[..n]);
            table.Resolve(budget, raster, out int rc1);
            for (int i = 0; i < rc1; i++) table.MarkRastered(raster[i].Key);
            table.EndFrame();
            Check("gate.tiles.budget-never-drops-visible within budget the visible tiles come first and the ahead band takes only what is left",
                rc1 == 8 && raster[5].Order == TileGrid.OrderVisible && raster[6].Order == TileGrid.OrderAhead && table.ResidentTiles == 8,
                $"rastered={rc1} resident={table.ResidentTiles}");

            table.BeginFrame(2);
            a = table.OpenSlice(1, 1, SliceKind.Scroll, Unit, contentA);
            var vpB = new RectF(0, 0, 2048, 1024);                 // 2 cols × 2 rows
            int b = table.OpenSlice(2, 1, SliceKind.Scroll, Unit, vpB);
            TileGrid.Needed(vpA, 0.0, feel, 0.0, 1024.0, false, keys, ord, out n, a);
            table.Request(a, vpA, 0.0, 1024.0, false, keys[..n], ord[..n]);
            TileGrid.Needed(vpB, 0.0, feel, 0.0, 1024.0, false, keys, ord, out n, b);
            table.Request(b, vpB, 0.0, 1024.0, false, keys[..n], ord[..n]);
            table.Resolve(budget, raster, out int rc2);
            int aVisible = 0;
            for (short ty = 0; ty <= 1; ty++) for (short tx = 0; tx <= 2; tx++)
                if (table.TryGetTile(new TileKey(a, tx, ty), out TileState v) && v.Surface >= 0 && v.Invalid == InvalidationReason.None) aVisible++;
            table.TryGetTile(new TileKey(b, 0, 0), out TileState b00);
            Check("gate.tiles.budget-never-drops-visible over budget the second slice DEGRADES (direct raster this frame) and no visible tile of the first is dropped",
                table.IsDegraded(b) && !table.IsDegraded(a) && aVisible == 6 && b00.Invalid == InvalidationReason.Degraded
                && table.DegradedSlicesThisFrame == 1 && rc2 == 0,
                $"aVisible={aVisible} degradedB={table.IsDegraded(b)} b00={b00.Invalid} rastered={rc2}");
            table.EndFrame();

            // A retires (not opened): its surfaces return and B fits.
            table.BeginFrame(3);
            b = table.OpenSlice(2, 1, SliceKind.Scroll, Unit, vpB);
            TileGrid.Needed(vpB, 0.0, feel, 0.0, 1024.0, false, keys, ord, out n, b);
            table.Request(b, vpB, 0.0, 1024.0, false, keys[..n], ord[..n]);
            table.Resolve(budget, raster, out int rc3);
            table.EndFrame();
            Check("gate.tiles.budget-never-drops-visible once the budget frees up the degraded slice rasters its visible tiles (never blank twice)",
                rc3 == 4 && !table.IsDegraded(b) && table.LiveSlices == 1, $"rastered={rc3} live={table.LiveSlices} resident={table.ResidentTiles}");
        }

        // Tile surfaces cover the painted bounds, not the grid: a static pane 1380×1158 px costs 1024+384 × 512+512+192
        // (the far column / row cut at the content and bucketed), not 2×3 whole tiles; a VIRTUAL list's vertical scroll
        // slice's tiles are cut on the cross axis only (its main-axis extent grows as rows realize).
        {
            var table = new SliceTable();
            var pane = new RectF(0, 0, 1380, 1158);
            table.BeginFrame(1);
            int st = table.OpenSlice(1, 1, SliceKind.Static, Unit, pane);
            TileGrid.Needed(pane, 0.0, feel, 0.0, 1158.0, false, keys, ord, out int n, st);
            table.Request(st, pane, 0.0, 1158.0, false, keys[..n], ord[..n]);
            table.Resolve(64 * TileBudget.TileBytes, raster, out int rcs);
            long staticBytes = table.ResidentBytes;
            table.EndFrame();
            long expectStatic = LayerTargetBucket.Bytes(1024 + 384, 512 + 512 + 192);

            var list = new SliceTable();
            var listContent = new RectF(0, 0, 1320, 100_000);
            var listVp = new RectF(0, 0, 1320, 1024);
            list.BeginFrame(1);
            int sc = list.OpenSlice(1, 1, SliceKind.Scroll, Unit, listContent);
            TileGrid.Needed(listVp, 0.0, feel, 0.0, 1024.0, false, keys, ord, out n, sc);
            list.Request(sc, listVp, 0.0, 1024.0, false, keys[..n], ord[..n]);
            list.Resolve(64 * TileBudget.TileBytes, raster, out int rcl);
            long listBytes = list.ResidentBytes;
            list.EndFrame();
            long expectList = LayerTargetBucket.Bytes(1024 + 320, 2 * 512);
            Check("gate.tiles.memory-ceiling tile surfaces cover the painted bounds: a static pane's far column/row and a scroll slice's cross axis are cut at the content",
                rcs == 6 && staticBytes == expectStatic && rcl == 4 && listBytes == expectList,
                $"static rastered={rcs} bytes={staticBytes} (expect {expectStatic}); list rastered={rcl} bytes={listBytes} (expect {expectList})");
        }

        long at2560 = TileBudget.Compute(2560, 1600, TileBudget.DefaultWindowMultiplier, TileBudget.DefaultFloorBytes, TileBudget.DefaultCeilingBytes);
        long atSmall = TileBudget.Compute(800, 600, TileBudget.DefaultWindowMultiplier, TileBudget.DefaultFloorBytes, TileBudget.DefaultCeilingBytes);
        long at4k = TileBudget.Compute(3840, 2160, TileBudget.DefaultWindowMultiplier, TileBudget.DefaultFloorBytes, TileBudget.DefaultCeilingBytes);
        Check("gate.tiles.budget-never-drops-visible TileBudgetBytes = clamp(5.0 × windowBytes, 48 MiB, 128 MiB) (~78 MiB at 2560×1600; raised 2026-09-24 from 3.5×/32/96 by the measured visible need)",
            at2560 == (long)(5.0 * 2560 * 1600 * 4) && atSmall == 48 * MiB && at4k == 128 * MiB,
            $"2560x1600={at2560 / (double)MiB:0.0}MiB 800x600={atSmall / MiB}MiB 4k={at4k / MiB}MiB");

    }

    // ── gate.tiles.feather ──────────────────────────────────────────────────────────────────────────────────────
    static void FeatherChecks()
    {
        Check("gate.tiles.feather the default (no) feather is exactly 1 everywhere",
            EdgeFeatherMask.Evaluate(default(EdgeFeather), 0.5f, 0.5f) == 1f && EdgeFeatherMask.Evaluate(default(EdgeFeather), -50f, 9999f) == 1f);

        var top = new EdgeFeather(new RectF(0, 0, 200, 400), 0f, 40f, 0f, 0f, default, FadeFalloff.Linear, 1f);
        float atEdge = EdgeFeatherMask.Evaluate(top, 100f, 0f);
        float mid = EdgeFeatherMask.Evaluate(top, 100f, 20f);
        float inside = EdgeFeatherMask.Evaluate(top, 100f, 40.5f);
        float bottom = EdgeFeatherMask.Evaluate(top, 100f, 399.5f);
        Check("gate.tiles.feather a top band fades 0 at the edge → 1 at band depth (linear: 0.5 halfway); the unfeathered edges stay 1",
            atEdge == 0f && MathF.Abs(mid - 0.5f) < 1e-6f && inside == 1f && bottom == 1f,
            $"edge={atEdge} mid={mid} inside={inside} bottom={bottom}");

        var smooth = top with { Falloff = FadeFalloff.Smoothstep, Intensity = 0.5f };
        float q = EdgeFeatherMask.Evaluate(smooth, 100f, 10f);   // t = 0.25 → smoothstep 0.15625 → lerp(1, ., 0.5)
        Check("gate.tiles.feather the smoothstep curve and the intensity lerp match the shader formula",
            MathF.Abs(q - (1f + (0.15625f - 1f) * 0.5f)) < 1e-6f, $"q={q}");

        var corner = new EdgeFeather(new RectF(0, 0, 200, 200), 20f, 20f, 0f, 0f, CornerRadius4.All(40f), FadeFalloff.Linear, 1f);
        float diag = EdgeFeatherMask.Evaluate(corner, 40f - 40f * 0.7071068f + 5f * 0.7071068f, 40f - 40f * 0.7071068f + 5f * 0.7071068f);
        Check("gate.tiles.feather where both adjacent edges fade, the feather follows the rounded-corner ARC (5 px in along the diagonal = 5/20)",
            MathF.Abs(diag - 0.25f) < 1e-4f, $"diag={diag}");

        Span<float> packed = stackalloc float[EdgeFeatherMask.PackedFloats];
        EdgeFeatherMask.Pack(smooth, packed);
        Check("gate.tiles.feather Pack writes the shader's constant layout and the packed evaluator agrees with the struct one",
            packed[3] == 400f && packed[5] == 40f && packed[12] == (float)FadeFalloff.Smoothstep && packed[13] == 0.5f
            && EdgeFeatherMask.Evaluate(packed, 37.5f, 13.5f) == EdgeFeatherMask.Evaluate(smooth, 37.5f, 13.5f));

        var spec = EdgeFeather.FromSpec(new EdgeFadeSpec(EdgeMask.Top, 24f), new RectF(10, 20, 100, 50), CornerRadius4.All(4f), 2f);
        Check("gate.tiles.feather FromSpec scales DIP bands/rect/radii to device px and keeps only the enabled edges",
            spec.Rect == new RectF(20, 40, 200, 100) && spec.BandTop == 48f && spec.BandLeft == 0f && spec.BandBottom == 0f && spec.Radii.TopLeft == 8f);
    }

    // ── gate.tiles.composite-record ─────────────────────────────────────────────────────────────────────────────
    static void CompositeRecordChecks()
    {
        var dev = new HeadlessGpuDevice();
        IGpuDevice seam = dev;
        var target = dev.CreateSwapchain(new SwapchainDesc(default, new Size2(2560, 1600)));   // the composite route's PRIMARY target
        TileRaster[] rasters =
        [
            new(new TileKey(0, 0, 3), 5, InvalidationReason.NoTexture, 0),
            new(new TileKey(0, 1, 3), 6, InvalidationReason.Content, 0),
        ];
        TilePlacement[] placements = [new(new TileKey(0, 0, 3), 5), new(new TileKey(0, 1, 3), 6), new(new TileKey(1, 0, 0), 9)];
        CompositeItem[] items =
        [
            new(1, CompositeKind.Tiles, Affine2D.Identity, 1f, RectF.Infinite, default, default, 0f, default, 1),
            new(0, CompositeKind.Tiles, Affine2D.Translation(0, -1536), 1f, new RectF(0, 48, 2560, 1552), default,
                new EdgeFeather(new RectF(0, 48, 2560, 1552), 0f, 40f, 0f, 0f, default), 0f, default, 0),
            new(2, CompositeKind.EraseVideoHole, Affine2D.Identity, 1f, new RectF(100, 100, 640, 360), CornerRadius4.All(8f), default, 0f, default, 0),
        ];
        PixelRect[] dirty = [new(0, 48, 2560, 128)];
        var frame = new CompositeFrame(new FrameInfo(new Size2(2560, 1600), 2f, default), ReadOnlySpan<SliceRow>.Empty, ReadOnlySpan<byte>.Empty,
            rasters, placements, items, new PresentParams(dirty, new PixelRect(0, 48, 2560, 1600), 0, -120));
        Check("gate.tiles.composite-record the headless device supports the composite seam; PlacementsOf returns a slice's contiguous run",
            seam.SupportsComposite && frame.PlacementsOf(0).Length == 2 && frame.PlacementsOf(1).Length == 1 && frame.PlacementsOf(7).IsEmpty);

        seam.SubmitComposite(frame, target);
        var ops = dev.LastCompositeRecords;
        bool tilePasses = ops.Count == 3 * 2 + 1 + 3 + 1 + 1;
        for (int i = 0; i < 2 && tilePasses; i++)
            tilePasses = ops[3 * i].Kind == CompositeRecordKind.BeginPass && ops[3 * i].Target == CompositePassTarget.TileSurface
                && ops[3 * i].Load == CompositePassLoad.Clear && ops[3 * i + 1].Kind == CompositeRecordKind.RasterTile
                && ops[3 * i + 1].Surface == rasters[i].Surface && ops[3 * i + 2].Kind == CompositeRecordKind.EndPass;
        Check("gate.tiles.composite-record each rastered tile is one CLEAR→STORE pass on its own surface, in raster-list order",
            tilePasses, $"records={ops.Count}");

        int bb = 6;
        bool backBuffer = ops.Count > bb + 5 && ops[bb].Kind == CompositeRecordKind.BeginPass && ops[bb].Target == CompositePassTarget.BackBuffer
            && ops[bb].Load == CompositePassLoad.Clear && ops[bb + 4].Kind == CompositeRecordKind.EndPass;
        for (int i = 0; i < 3 && backBuffer; i++) backBuffer = ops[bb + 1 + i].Kind == CompositeRecordKind.DrawItem && ops[bb + 1 + i].ItemIndex == i
            && ops[bb + 1 + i].Item.SliceId == items[i].SliceId;
        int backBufferPasses = 0;
        foreach (var op in ops) if (op.Target == CompositePassTarget.BackBuffer && op.Kind == CompositeRecordKind.BeginPass) backBufferPasses++;
        Check("gate.tiles.composite-record the back buffer is ONE CLEAR→STORE pass (the clear colour is its load op, the previous frame is never read) drawing the items in painter order",
            backBuffer && backBufferPasses == 1);

        var present = ops[ops.Count - 1];
        Check("gate.tiles.composite-record the Present1 parameters (dirty rects, scroll rect, texel offset) are staged last",
            present.Kind == CompositeRecordKind.StagePresent && present.DirtyRectCount == 1 && present.HasScroll && present.ScrollDy == -120
            && present.ScrollRect.Top == 48 && dev.CompositeFrameCount == 1,
            $"dirty={present.DirtyRectCount} scroll={present.HasScroll} dy={present.ScrollDy}");
    }

    // ── gate.tiles.alloc-zero ───────────────────────────────────────────────────────────────────────────────────
    static void AllocZeroChecks()
    {
        var table = new SliceTable();
        var dev = new HeadlessGpuDevice();
        var target = dev.CreateSwapchain(new SwapchainDesc(default, new Size2(2560, 1600)));   // the composite route's PRIMARY target
        var keys = new TileKey[128];
        var ord = new byte[128];
        var raster = new TileRaster[128];
        var placements = new TilePlacement[128];
        var items = new CompositeItem[2];
        var dirty = new PixelRect[1];
        MotionFeel feel = FeelProfiles.Standard;
        long tight = 32 * TileBudget.TileBytes;   // forces evictions as the viewport walks

        void Tick(int frame)
        {
            var vp = new RectF(0, frame * 97 % 40_000, 2560, 1600);
            double v = (frame & 1) == 0 ? 30_000.0 : -30_000.0;
            table.BeginFrame(frame);
            int s = table.OpenSlice(1, 1, SliceKind.Scroll, Unit, vp);
            int s2 = table.OpenSlice(2, 1, SliceKind.Static, Unit, new RectF(0, 0, 2560, 1600));
            table.InvalidateRect(s, new RectF(10, vp.Y + 30, 200, 40), InvalidationReason.Content);
            table.InvalidateSlice(s2, InvalidationReason.BackgroundOrTheme);
            TileGrid.Needed(vp, v, feel, 0.0, 60_000.0, false, keys, ord, out int n, s);
            table.Request(s, vp, 0.0, 60_000.0, false, keys.AsSpan(0, n), ord.AsSpan(0, n));
            var svp = new RectF(0, 0, 2560, 1600);
            TileGrid.Needed(svp, 0.0, feel, 0.0, 1600.0, false, keys, ord, out n, s2);
            table.Request(s2, svp, 0.0, 1600.0, false, keys.AsSpan(0, n), ord.AsSpan(0, n));
            table.Resolve(tight, raster, out int rc);
            for (int i = 0; i < rc; i++) table.MarkRastered(raster[i].Key);
            int pc = table.CollectPlacements(s2, placements);
            pc += table.CollectPlacements(s, placements.AsSpan(pc));
            items[0] = new CompositeItem(s2, CompositeKind.Tiles, Affine2D.Identity, 1f, RectF.Infinite, default, default, 0f, default, 1);
            items[1] = new CompositeItem(s, CompositeKind.Tiles, Affine2D.Translation(0, -vp.Y), 1f, svp, default, default, 0f, default, 0);
            dirty[0] = new PixelRect(0, 0, 2560, 1600);
            dev.SubmitComposite(new CompositeFrame(default, ReadOnlySpan<SliceRow>.Empty, ReadOnlySpan<byte>.Empty,
                raster.AsSpan(0, rc), placements.AsSpan(0, pc), items, new PresentParams(dirty)), target);
            table.EndFrame();
        }

        for (int f = 1; f <= 200; f++) Tick(f);   // warm: JIT, list capacities
        int evictedWarm = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 201; f <= 1200; f++) { Tick(f); evictedWarm += table.EvictedThisFrame; }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.tiles.alloc-zero 1000 turns of Needed + invalidate + Request + Resolve (with evictions) + MarkRastered + headless SubmitComposite allocate 0 bytes",
            delta == 0 && evictedWarm > 0, $"bytes={delta} evictions={evictedWarm}");
    }
    // ── end-to-end gates: the real headless host (AppHost + HeadlessWindow + the headless composite model) ──────────

    /// <summary>A plain (non-virtualized) scroller: 60 rows × 100 DIP of fills with a label, all realized at once, so its
    /// coverage is the whole content and a scroll is a PURE pose change.</summary>
    sealed class PlainScrollProbe : Component
    {
        public override Element Render()
        {
            var rows = new Element[60];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl
                {
                    Height = 100f, Fill = ColorF.FromRgba(30, (byte)(30 + (i % 4) * 12), 40),
                    Children = [new TextEl("row " + i) { Size = 14f, Color = ColorF.FromRgba(0xE0, 0xE0, 0xE8) }],
                };
            return new ScrollEl { Width = 1100f, Height = 900f, Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = rows } };
        }
    }

    /// <summary>A 6 × 4 grid of 150-DIP boxes over two tile columns and two tile rows; box (0, 0) takes its fill from
    /// <see cref="Hot"/>.</summary>
    sealed class GridProbe : Component
    {
        public static readonly Signal<int> Hot = new(0);
        public override Element Render()
        {
            var cells = new Element[24];
            for (int i = 0; i < cells.Length; i++)
            {
                int c = i % 6, r = i / 6;
                cells[i] = new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(50f + c * 190f, 50f + r * 230f, 0f, 0f), Width = 150f, Height = 150f,
                    Fill = i == 0 ? Prop.Of(() => Hot.Value % 2 == 0 ? ColorF.FromRgba(200, 90, 60) : ColorF.FromRgba(60, 90, 200))
                                  : (Prop<ColorF>)ColorF.FromRgba(40, 44, (byte)(60 + i * 4)),
                };
            }
            return new BoxEl { Grow = 1f, ZStack = true, Fill = ColorF.FromRgba(18, 18, 22), Children = cells };
        }
    }

    /// <summary>A bound 100k-row list (36 DIP rows, a label each) in a 1100 × 900 viewport.</summary>
    sealed class BigListProbe(int n, bool text) : Component
    {
        public const float RowH = 36f;
        public override Element Render()
            => Virtual.ListBound(n, RowH, idx => new BoxEl
               {
                   Height = RowH,
                   Fill = Prop.Of(() => ColorF.FromRgba(30, 30, (byte)(idx.Value % 2 == 0 ? 30 : 50))),
                   Children = text ? [new TextEl("") { Size = 13f, Text = Prop.Of(() => "row " + idx.Value) }] : [],
               })
               with { Width = 1100, Height = 900 };
    }

    static (HeadlessPlatformApp App, HeadlessWindow Window, HeadlessGpuDevice Device, AppHost Host) Host(
        string name, StringTable strings, HeadlessFontSystem fonts, Component root)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(1200, 1000), 1f));
        window.Show();
        var dev = new HeadlessGpuDevice();
        var host = new AppHost(app, window, dev, fonts, strings, root);
        return (app, window, dev, host);
    }

    static void Frames(AppHost host, int n) { for (int i = 0; i < n; i++) host.RunFrame(); }

    /// <summary>The id of the composite's scroll-content slice (−1 when none).</summary>
    static int ScrollSliceId(HeadlessGpuDevice dev)
    {
        foreach (var row in dev.LastCompositeSlices) if (row.Kind == SliceKind.Scroll) return row.Id;
        return -1;
    }

    // ── gate.tiles.scroll-no-copy / gate.tiles.exposed-only (end to end) ─────────────────────────────────────────
    static void ScrollNoCopyChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, window, dev, host) = Host("tiles-scroll-no-copy", strings, fonts, new PlainScrollProbe());
        using var _a = app; using var _h = host;
        Frames(host, 30);
        var vp = host.Scene.Root;
        var handle = host.TryGetScrollHandle(vp)!;
        int content = ScrollSliceId(dev);

        // Collect this turn's content-slice rasters and every op kind the composite performed.
        var rastered = new List<TileKey>();
        int foreignOps = 0, backBufferPasses = 0, contentRastersAll = 0;
        int seen = dev.CompositeFrameCount;
        void Turn()
        {
            host.RunFrame();
            if (dev.CompositeFrameCount == seen) return;   // an elided turn composited nothing new
            seen = dev.CompositeFrameCount;
            foreach (var op in dev.LastCompositeRecords)
            {
                switch (op.Kind)
                {
                    case CompositeRecordKind.RasterTile:
                        if (op.Tile.SliceId == content) { rastered.Add(op.Tile); contentRastersAll++; }
                        break;
                    case CompositeRecordKind.BeginPass:
                        if (op.Target == CompositePassTarget.BackBuffer) backBufferPasses++;
                        break;
                    case CompositeRecordKind.EndPass:
                    case CompositeRecordKind.DrawItem:
                    case CompositeRecordKind.StagePresent:
                    case CompositeRecordKind.PrepareGroup:   // offscreen work, never a read or copy of a target
                        break;
                    default: foreignOps++; break;
                }
            }
        }

        // Inside the rows the first turn already holds (visible 0..1 + the row retained behind): nothing rasters. A
        // non-virtual scroll segment's tile origin sits on the 64-px grid AT ITS CONTENT (gate.tiles.segment-extent) —
        // here one grid step above it (the rows' AA halo) — so "still inside tile row 1" is measured from that origin:
        // the viewport's bottom (900 DIP at scale 1) must stay below tile-space 1024.
        int originY = 0;
        foreach (var row in dev.LastCompositeSlices) if (row.Id == content) { originY = row.Frame.OriginY; break; }
        double within = 2 * TileGrid.H + originY - 900 - 10;
        rastered.Clear(); backBufferPasses = 0;
        handle.ScrollTo(within, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) Turn();
        bool noneWithin = rastered.Count == 0 && backBufferPasses >= 1;
        Check("gate.tiles.scroll-no-copy a pure scroll inside the retained rows rasters NOTHING — the kept tiles are re-placed at the new offset, the back buffer is recomposed from them in one pass per frame, and no render target is ever read or copied (the composite has no such op)",
            content >= 0 && noneWithin && foreignOps == 0, $"contentSlice={content} originY={originY} within={within} rastered={rastered.Count} backBufferPasses={backBufferPasses} foreignOps={foreignOps}");

        // To 700: rows 1..3 visible, 0 and 4 retained around them — exactly tile rows 3 and 4 enter (2 columns each).
        rastered.Clear();
        handle.ScrollTo(700.0, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) Turn();
        bool entering = rastered.Count == 4;
        foreach (var k in rastered) entering &= k.Ty is 3 or 4 && k.Tx is 0 or 1;
        var distinct = new HashSet<TileKey>(rastered);
        Check("gate.tiles.exposed-only a scroll to a new offset rasters ONLY the tiles entering the needed set (tile rows 3 and 4 × 2 columns), each once; the retained ones are reused",
            entering && distinct.Count == rastered.Count, $"rastered=[{string.Join(" ", rastered.ConvertAll(k => $"{k.Tx},{k.Ty}"))}]");

        // And back inside rows 0..1: rows 0..2 are still resident (LRU keeps them within budget) — nothing rasters.
        rastered.Clear();
        handle.ScrollTo(within, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) Turn();
        Check("gate.tiles.exposed-only scrolling back over rows still resident rasters nothing (the tiles were kept, not re-drawn)",
            rastered.Count == 0 && foreignOps == 0, $"rastered={rastered.Count}");
    }

    // ── gate.tiles.invalidation-one (end to end) ─────────────────────────────────────────────────────────────────
    static void InvalidationOneEndToEndChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, window, dev, host) = Host("tiles-invalidation-one", strings, fonts, new GridProbe());
        using var _a = app; using var _h = host;
        Frames(host, 30);
        int rasters = -1;
        var keys = new List<(TileKey Key, InvalidationReason Reason)>();
        GridProbe.Hot.Value++;
        int seen = dev.CompositeFrameCount;
        for (int i = 0; i < 6; i++)
        {
            host.RunFrame();
            if (dev.CompositeFrameCount == seen) continue;   // an elided turn composited nothing new
            seen = dev.CompositeFrameCount;
            foreach (var op in dev.LastCompositeRecords)
                if (op.Kind == CompositeRecordKind.RasterTile) keys.Add((op.Tile, op.Reason));
        }
        rasters = keys.Count;
        Check("gate.tiles.invalidation-one a one-node paint change re-rasters exactly the ONE tile under it (reason Content) and nothing else",
            rasters == 1 && keys[0].Key.Tx == 0 && keys[0].Key.Ty == 0 && keys[0].Reason == InvalidationReason.Content,
            $"rasters=[{string.Join(" ", keys.ConvertAll(k => $"{k.Key.SliceId}:{k.Key.Tx},{k.Key.Ty}:{k.Reason}"))}]");
    }

    // ── gate.tiles.no-blank-8000 / gate.tiles.memory-ceiling ─────────────────────────────────────────────────────
    static void NoBlankChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, window, dev, host) = Host("tiles-no-blank", strings, fonts, new BigListProbe(100_000, text: true));
        using var _a = app; using var _h = host;
        Frames(host, 30);
        var vp = host.Scene.Root;
        var handle = host.TryGetScrollHandle(vp)!;
        handle.AutoScroll(8000.0);
        int frames = 0, exposedMissing = 0, clamps = 0, degraded = 0, overBudget = 0, overCeiling = 0, rasteredTotal = 0, maxPerTurn = 0;
        long residentMax = 0, budget = 0, retainedMax = 0;
        for (int f = 0; f < 360; f++)
        {
            host.RunFrame();
            var c = host.LastTileCensus;
            frames++;
            exposedMissing += c.ExposedMissing;
            clamps += c.CoverageClamps;
            degraded += c.DegradedSlices;
            if (c.ResidentBytes > c.BudgetBytes) overBudget++;
            // retained derived surfaces (group / blur / backdrop caches) ride on top, bounded by RetainedShare
            if (c.ResidentBytes + c.RetainedBytes > c.BudgetBytes + TileBudget.RetainedBytesCap(c.BudgetBytes)) overCeiling++;
            residentMax = Math.Max(residentMax, c.ResidentBytes);
            retainedMax = Math.Max(retainedMax, c.RetainedBytes);
            budget = c.BudgetBytes;
            rasteredTotal += c.Rastered;
            maxPerTurn = Math.Max(maxPerTurn, c.Rastered);
        }
        host.Scene.TryGetScroll(vp, out var sc);
        Check("gate.tiles.no-blank-8000 an 8000 DIP/s scroll over a 100k-row list composites a tile under every visible pixel on every frame (0 exposed-missing tiles) and its realized rows always cover the viewport (0 coverage clamps), never degrading",
            exposedMissing == 0 && clamps == 0 && degraded == 0 && sc.Offset > 20_000 && rasteredTotal > 0,
            $"frames={frames} offset={sc.Offset:0} exposedMissing={exposedMissing} clamps={clamps} degraded={degraded} rastered={rasteredTotal} maxPerTurn={maxPerTurn}");
        Check("gate.tiles.memory-ceiling resident tile bytes never exceed the tile budget through the whole 8000 DIP/s scroll, and resident + retained derived surfaces never exceed budget × (1 + RetainedShare)",
            overBudget == 0 && overCeiling == 0 && residentMax > 0,
            $"residentMax={residentMax / 1048576.0:0.0}MiB retainedMax={retainedMax / 1048576.0:0.0}MiB budget={budget / 1048576.0:0.0}MiB share={TileBudget.RetainedShare:0.00} overBudgetTurns={overBudget} overCeilingTurns={overCeiling}");
    }

    /// <summary>A plain (non-virtual) scroller whose content alternates 40-DIP headings with opacity-group boxes (each an
    /// effect slice — a marker that splits the content slice), so the content slice's segments between them are thin
    /// slivers — the artist page's shelf headings.</summary>
    sealed class SegmentsProbe : Component
    {
        public const float HeadingH = 40f;
        public override Element Render()
        {
            var kids = new List<Element>();
            for (int i = 0; i < 5; i++)
            {
                kids.Add(new BoxEl { Height = HeadingH, Fill = ColorF.FromRgba(50, 50, (byte)(60 + i * 20)),
                    Children = [new TextEl("heading " + i) { Size = 14f, Color = ColorF.FromRgba(0xE0, 0xE0, 0xE8) }] });
                kids.Add(new BoxEl { Height = 120f, Fill = ColorF.FromRgba(200, 120, 40), Opacity = 0.6f, OpacityGroup = true });
            }
            kids.Add(new BoxEl { Height = 1200f });
            return new ScrollEl { Width = 1100f, Height = 900f, Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = kids.ToArray() } };
        }
    }

    // ── gate.tiles.segment-extent ──────────────────────────────────────────────────────────────────────────────────
    // A non-virtual scroll segment used to be cut on its CROSS axis only (SliceTable.SurfaceExtent), so a 40-DIP heading
    // between two effect slices charged a whole 512-row cell per tile column — the artist page's shelf headings saturated
    // the budget that way and degraded slices. Only a VIRTUAL list's segments grow along the main axis; every other scroll
    // segment is cut at its painted bounds on both axes (its origin on the 64-px grid at its content).
    static void SegmentExtentChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, window, dev, host) = Host("tiles-segment-extent", strings, fonts, new SegmentsProbe());
        using var _a = app; using var _h = host;
        Frames(host, 30);
        var table = host.UiSliceTable;
        int thin = 0, checkedTiles = 0, over = 0;
        string worst = "";
        foreach (var row in dev.LastCompositeSlices)
        {
            if (row.Kind != SliceKind.Scroll || row.ContentBounds.IsEmpty || row.ContentBounds.H > 100f) continue;
            thin++;
            int limit = LayerTargetBucket.Dim((int)MathF.Ceiling(row.ContentBounds.H) + TileGrid.OriginGrid);
            for (short ty = 0; ty < 8; ty++)
                for (short tx = 0; tx < 4; tx++)
                {
                    if (!table.TryGetTile(new TileKey(row.Id, tx, ty), out TileState ts) || ts.Surface < 0) continue;
                    checkedTiles++;
                    if (ts.SurfH > limit) { over++; worst = $"slice={row.Id} contentH={row.ContentBounds.H:0.#} tile=({tx},{ty}) surfH={ts.SurfH} limit={limit}"; }
                }
        }
        Check("gate.tiles.segment-extent a thin (40-DIP) non-virtual scroll segment charges surfaces cut to its painted bounds on the main axis too (≤ Dim(height + 64) rows), not a 512-row cell per column",
            thin > 0 && checkedTiles > 0 && over == 0, $"thinSegments={thin} residentTiles={checkedTiles} oversized={over} {worst}");
    }

    // ── gate.tiles.render-alloc-zero ─────────────────────────────────────────────────────────────────────────────
    static void RenderAllocZeroChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var (app, window, dev, host) = Host("tiles-render-alloc", strings, fonts, new BigListProbe(100_000, text: false));
        using var _a = app; using var _h = host;
        Frames(host, 30);
        var handle = host.TryGetScrollHandle(host.Scene.Root)!;
        handle.AutoScroll(3000.0);
        for (int f = 0; f < 200; f++) host.RunFrame();   // warm: slot pool, slice/tile tables, instance lists, JIT
        long worst = 0; int rastered = 0;
        for (int f = 0; f < 300; f++)
        {
            var s = host.RunFrame();
            worst = Math.Max(worst, s.HotPhaseAllocBytes);
            rastered += host.LastTileCensus.Rastered;
        }
        Check("gate.tiles.render-alloc-zero 300 warm frames of a 3000 DIP/s scroll — record, slice partition, tile schedule, composite build, the backend submit and the tile bookkeeping — allocate 0 managed bytes per frame",
            worst == 0 && rastered > 0, $"worstFrameBytes={worst} rastered={rastered}");
    }
}
