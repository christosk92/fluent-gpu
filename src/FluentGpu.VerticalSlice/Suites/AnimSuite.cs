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
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;




static class AnimSuite
{
    public static void Run(StringTable strings)
    {
        AnimChecks();
        ExpressiveMotionChecks(strings);
        BlurPinKeyChecks(strings);
        SkeletonChecks(strings);
        ProjectionChecks(strings);
        EnterExitChecks(strings);
        SizeModeChecks(strings);
        ReflowChecks(strings);
        AnimRegressionChecks(strings);
        StyleChecks();
        ButtonAxesChecks();
        AnimValueChecks();
        CompositorChecks(strings);
        CleanSpanReuseChecks();
        SpanReuseScopingChecks();
        AnimEngineChecks(strings);
        AnimHookChecks(strings);
        MarqueeChecks(strings);
        CrossfadeChecks(strings);
        WaveeSkeletonChecks(strings);
        BrushTransitionChecks(strings);
        AnimRestChecks(strings);
    }

    static void AnimChecks()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        var engine = new AnimEngine(scene);

        scene.Paint(node).Opacity = 0f;
        scene.Flags(node) &= ~(NodeFlags.PaintDirty | NodeFlags.LayoutDirty | NodeFlags.TransformDirty);
        engine.Animate(node, AnimChannel.Opacity, 0f, 1f, 100f, Easing.Linear);
        engine.Tick(0f);
        bool startOk = MathF.Abs(scene.Paint(node).Opacity) < 0.001f;
        engine.Tick(50f);
        float op = scene.Paint(node).Opacity;
        var fl = scene.Flags(node);
        bool midOk = MathF.Abs(op - 0.5f) < 0.02f && (fl & NodeFlags.PaintDirty) != 0 && (fl & NodeFlags.LayoutDirty) == 0;
        engine.Tick(60f);   // 110ms > 100ms → complete
        bool doneOk = MathF.Abs(scene.Paint(node).Opacity - 1f) < 0.001f && !engine.HasActive;
        Check("22. opacity timeline samples t0, eases & completes (no relayout)", startOk && midOk && doneOk, $"@50ms={op:0.00}");

        scene.Flags(node) &= ~(NodeFlags.TransformDirty | NodeFlags.LayoutDirty);
        engine.Animate(node, AnimChannel.TranslateX, 0f, 100f, 100f, Easing.Linear);
        engine.Tick(0f);
        engine.Tick(25f);
        float dx = scene.Paint(node).LocalTransform.Dx;
        var fl2 = scene.Flags(node);
        bool transOk = MathF.Abs(dx - 25f) < 0.5f && (fl2 & NodeFlags.TransformDirty) != 0 && (fl2 & NodeFlags.LayoutDirty) == 0;
        Check("23. translate timeline marks TransformDirty only", transOk, $"@25ms dx={dx:0.0}");

        var modal = scene.CreateNode(1);
        ref NodePaint mp = ref scene.Paint(modal);
        mp.Opacity = 1f;
        mp.LocalTransform = Affine2D.Identity;
        engine.Animate(modal, AnimChannel.ScaleX, 1f, 1.05f, 167f, Easing.FluentPopOpen);
        engine.Animate(modal, AnimChannel.ScaleY, 1f, 1.05f, 167f, Easing.FluentPopOpen);
        engine.Animate(modal, AnimChannel.Opacity, 1f, 0f, 83f, Easing.Linear);
        engine.Tick(0f);
        for (int i = 0; i < 6; i++) engine.Tick(16f);  // 96ms: opacity settled/removed, scale still active
        float faded = scene.Paint(modal).Opacity;
        bool scaleStillActive = engine.HasTracks(modal);
        engine.Tick(16f);                              // previous bug: remaining scale tracks reset Opacity to 1 here
        float held = scene.Paint(modal).Opacity;
        Check("23z. multi-channel animation preserves completed channels while longer tracks continue",
            faded < 0.01f && held < 0.01f && scaleStillActive,
            $"opacity {faded:0.00}->{held:0.00}, active={scaleStillActive}");
    }

    static (ulong key, bool ok) BlurStripKey(StringTable strings, float ox, float oy, float sigma, float scaleX, float w, float h,
        StringId text, float childDx, float childDy, float split, StringId text2 = default, float child2Dx = 0f, float child2Dy = 0f)
    {
        var dl = new DrawList();
        var fam = strings.Intern("Segoe UI");
        var layerRect = new RectF(ox, oy, w * scaleX, h);
        dl.PushBlurLayer(layerRect, default, sigma, 1f);
        var local = new RectF(0f, 0f, w, h);
        var world = new Affine2D(scaleX, 0f, 0f, 1f, ox + childDx, oy + childDy);   // world = translate(layer origin) ∘ child offset
        if (split >= 0f)
            dl.DrawGlyphRunGradient(local, text, fam, 20f, 400, 0, 0, 1, 0f, 24f, 0, 0, world, 1f,
                new ColorF(1f, 1f, 1f, 1f), new ColorF(1f, 1f, 1f, 0.4f), split, 0.05f, 0f);
        else
            dl.DrawGlyphRun(local, new ColorF(1f, 1f, 1f, 1f), text, fam, 20f, 400, 0, 0, 1, 0f, 24f, 0, 0, world, 1f);
        if (!text2.IsEmpty)
        {
            var world2 = new Affine2D(scaleX, 0f, 0f, 1f, ox + child2Dx, oy + child2Dy);
            dl.DrawGlyphRun(new RectF(0f, 0f, w, h), new ColorF(1f, 1f, 1f, 1f), text2, fam, 20f, 400, 0, 0, 1, 0f, 24f, 0, 0, world2, 1f);
        }
        dl.PopLayer(layerRect);
        var bytes = dl.Bytes;
        var L = MemoryMarshal.Read<PushLayerCmd>(bytes.Slice(sizeof(int)));
        int start = sizeof(int) + Unsafe.SizeOf<PushLayerCmd>();
        bool ok = BlurPinKey.TryCompute(bytes, start, in L, out ulong key, out _);
        return (key, ok);
    }

    static (ulong key, PushLayerCmd L) BlurStripKeyL(StringTable strings, float ox, float oy, float sigma, float w, float h, StringId text)
    {
        var dl = new DrawList();
        var fam = strings.Intern("Segoe UI");
        var layerRect = new RectF(ox, oy, w, h);
        dl.PushBlurLayer(layerRect, default, sigma, 1f);
        var world = new Affine2D(1f, 0f, 0f, 1f, ox + 8f, oy + 6f);
        dl.DrawGlyphRun(new RectF(0f, 0f, w, h), new ColorF(1f, 1f, 1f, 1f), text, fam, 20f, 400, 0, 0, 1, 0f, 24f, 0, 0, world, 1f);
        dl.PopLayer(layerRect);
        var bytes = dl.Bytes;
        var L = MemoryMarshal.Read<PushLayerCmd>(bytes.Slice(sizeof(int)));
        int start = sizeof(int) + Unsafe.SizeOf<PushLayerCmd>();
        BlurPinKey.TryCompute(bytes, start, in L, out ulong key, out _);
        return (key, L);
    }

    static void BlurPinKeyChecks(StringTable strings)
    {
        // BP.1 — the core G3 invariant: a pure translation (scroll) at two DIFFERENT (and fractional) origins ⇒ SAME key.
        var t1 = strings.Intern("lyric line");
        var (k1, o1) = BlurStripKey(strings, 100f, 200f, 4f, 1f, 300f, 40f, t1, 8f, 6f, -1f);
        var (k2, o2) = BlurStripKey(strings, 100f - 13.7f, 200f - 37.4f, 4f, 1f, 300f, 40f, t1, 8f, 6f, -1f);
        Check("BP.1 translation-invariant key (scroll → HIT, not a re-blur)", o1 && o2 && k1 == k2 && k1 != 0, $"k1={k1:X16} k2={k2:X16}");

        // BP.2 — determinism: sweep the origin across 200 sub-pixel AND integer-crossing offsets; the key must not wobble
        // (a raw fl(P+X)-X rebase would leak a ≤1-ULP jitter — rounding to the integer grid kills it). Non-integer child
        // offsets (12.3, 5.7) make the P+X and P subtractions genuinely inexact, so this exercises the wobble path.
        ulong refk = 0; bool allSame = true, allOk = true;
        var t2 = strings.Intern("determinism");
        for (int i = 0; i < 200; i++)
        {
            float frac = i / 50f;   // 0.00 .. 3.98, crossing 0/1/2/3
            var (k, ok) = BlurStripKey(strings, 100f + frac, 200f + frac, 4f, 1f, 300f, 40f, t2, 12.3f, 5.7f, -1f);
            if (i == 0) refk = k;
            if (k != refk) allSame = false;
            if (!ok) allOk = false;
        }
        Check("BP.2 key constant across 200 sub-pixel + integer-crossing offsets (no float wobble)", allOk && allSame && refk != 0, $"ref={refk:X16}");

        // BP.3 — content sensitivity: a glyph text change and a wipe-Split change (DrawGlyphRunGradient) each flip the key.
        var ta = strings.Intern("verse one");
        var tb = strings.Intern("verse two");
        var (ka, _) = BlurStripKey(strings, 100f, 200f, 4f, 1f, 300f, 40f, ta, 8f, 6f, -1f);
        var (kb, _) = BlurStripKey(strings, 100f, 200f, 4f, 1f, 300f, 40f, tb, 8f, 6f, -1f);
        var (ks1, _) = BlurStripKey(strings, 100f, 200f, 4f, 1f, 300f, 40f, ta, 8f, 6f, 0.3f);
        var (ks2, _) = BlurStripKey(strings, 100f, 200f, 4f, 1f, 300f, 40f, ta, 8f, 6f, 0.7f);
        Check("BP.3 content-sensitive: text change and wipe-split change each flip the key", ka != kb && ks1 != ks2, $"text={ka != kb} split={ks1 != ks2}");

        // BP.4 — size/sigma sensitivity: a σ step and a ScaleX step (⇒ device W/H + world M11) each flip the key. This
        // proves SIZE stays a CONTENT miss (scale reuse is a non-goal; the app steps scale so the ease is position-only).
        var tz = strings.Intern("emphasis");
        var (kg1, _) = BlurStripKey(strings, 100f, 200f, 4f, 1f, 300f, 40f, tz, 8f, 6f, -1f);
        var (kg2, _) = BlurStripKey(strings, 100f, 200f, 5f, 1f, 300f, 40f, tz, 8f, 6f, -1f);
        var (kc1, _) = BlurStripKey(strings, 100f, 200f, 4f, 1.00f, 300f, 40f, tz, 8f, 6f, -1f);
        var (kc2, _) = BlurStripKey(strings, 100f, 200f, 4f, 1.10f, 300f, 40f, tz, 8f, 6f, -1f);
        Check("BP.4 size-sensitive: σ change and ScaleX change each flip the key (size stays a content miss)", kg1 != kg2 && kc1 != kc2, $"sigma={kg1 != kg2} scale={kc1 != kc2}");

        // BP.5 — relative-layout sensitivity: shift ONE child's local offset by ≥1 px (same size/content) ⇒ the key flips.
        // Proves the rebase catches a genuine relative move — it is NOT a blanket "ignore all Dx/Dy" (no false-share).
        var tr = strings.Intern("main");
        var tr2 = strings.Intern("child2");
        var (kr1, or1) = BlurStripKey(strings, 100f, 200f, 4f, 1f, 300f, 40f, tr, 8f, 6f, -1f, tr2, 40f, 6f);
        var (kr2, or2) = BlurStripKey(strings, 100f, 200f, 4f, 1f, 300f, 40f, tr, 8f, 6f, -1f, tr2, 44f, 6f);
        Check("BP.5 relative-layout-sensitive: a ≥1px child-local shift flips the key (no false-share)", or1 && or2 && kr1 != kr2 && kr1 != 0, $"k1={kr1:X16} k2={kr2:X16}");

        // BP.6 — alloc: 10000 TryCompute calls on a fixed byte buffer under the GC tripwire ⇒ delta == 0 (stackalloc only,
        // safe for the phase 6–13 record hot path).
        {
            var dl = new DrawList();
            var fam = strings.Intern("Segoe UI");
            var layerRect = new RectF(100f, 200f, 300f, 40f);
            dl.PushBlurLayer(layerRect, default, 4f, 1f);
            dl.DrawGlyphRun(new RectF(0f, 0f, 300f, 40f), new ColorF(1f, 1f, 1f, 1f), strings.Intern("alloc"), fam, 20f, 400, 0, 0, 1, 0f, 24f, 0, 0, new Affine2D(1f, 0f, 0f, 1f, 108f, 206f), 1f);
            dl.PopLayer(layerRect);
            var pl = MemoryMarshal.Read<PushLayerCmd>(dl.Bytes.Slice(sizeof(int)));
            int st = sizeof(int) + Unsafe.SizeOf<PushLayerCmd>();
            BlurPinKey.TryCompute(dl.Bytes, st, in pl, out _, out _);   // warm
            long before = GC.GetAllocatedBytesForCurrentThread();
            ulong last = 0;
            for (int i = 0; i < 10000; i++) { BlurPinKey.TryCompute(dl.Bytes, st, in pl, out ulong k, out _); last = k; }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("BP.6 BlurPinKey.TryCompute is zero-alloc (10000 calls)", delta == 0 && last != 0, $"delta={delta}B/10000 last={last:X16}");
        }

        // gate.blur.edgeClampedPinCaches (FA-2a edge-clamp fix). A self-blur whose halo-inflated region is clamped by a
        // canvas edge is STILL cacheable: SelfBlurRegion clamps the region and the compositor's FindPin matches SIZE-
        // exactly, so a STATIONARY clamped row produces a byte-identical key AND a byte-identical clamped region box two
        // frames running ⇒ a pin HIT (no re-blur — the fix for an edge-clamped blur re-running its Gaussian every submit).
        // A ≥1-device-px move that shifts the clamp changes the region-box size ⇒ a size MISS (re-render the exact strip),
        // never a full pin squished into a shorter viewport. The size-exact match is why removing the old clamped-refusal
        // is sound. (The D3D12 pool/pin lease is a --screenshot golden; the portable predicate is gated here.)
        {
            int cw = 1920, ch = 1080;
            var te = strings.Intern("edge line");
            // Near the TOP edge: σ=8 ⇒ tap halo min(ceil(24),32)=24 px; DeviceRect.Y=10 ⇒ 10−24 = −14 < 0 ⇒ clamped.
            var (ke1, Le1) = BlurStripKeyL(strings, 100f, 10f, 8f, 300f, 40f, te);
            var (ke2, Le2) = BlurStripKeyL(strings, 100f, 10f, 8f, 300f, 40f, te);   // stationary: byte-identical next frame
            bool clamped = SelfBlurRegion.IsClamped(in Le1, 1f, cw, ch);
            SelfBlurRegion.RegionBox(in Le1, 1f, cw, ch, out int aX, out int aY, out int aXe, out int aYe);
            SelfBlurRegion.RegionBox(in Le2, 1f, cw, ch, out int bX, out int bY, out int bXe, out int bYe);
            bool stationaryHit = ke1 == ke2 && ke1 != 0 && aX == bX && aY == bY && aXe == bXe && aYe == bYe && aY == 0; // clamped at top ⇒ minY 0
            // Fully on-canvas ⇒ not clamped (mints/hits a full pin as before).
            var (_, Lon) = BlurStripKeyL(strings, 100f, 400f, 8f, 300f, 40f, te);
            bool onCanvasUnclamped = !SelfBlurRegion.IsClamped(in Lon, 1f, cw, ch);
            // A 1px move at the clamped edge changes the region SIZE ⇒ size-exact FindPin misses (re-blur, no squish).
            var (_, Lmv) = BlurStripKeyL(strings, 100f, 11f, 8f, 300f, 40f, te);
            SelfBlurRegion.RegionBox(in Lmv, 1f, cw, ch, out _, out int mY, out _, out int mYe);
            bool moveChangesSize = (mYe - mY) != (aYe - aY);
            Check("gate.blur.edgeClampedPinCaches: a stationary canvas-clamped self-blur keys + region-boxes identically (pin HIT); a 1px edge move changes the region size (size MISS, no squish)",
                clamped && stationaryHit && onCanvasUnclamped && moveChangesSize,
                $"clamped={clamped} hit={stationaryHit} onCanvas={onCanvasUnclamped} moveMiss={moveChangesSize}");
        }

        // gate.blur.selfBlurHaloCoversKernel. The self-blur RegionBox halo (SelfBlurRegion.TapRadius) MUST equal the
        // ACTUAL support of the downsample-then-separable-Gaussian schedule OpacityLayerCompositor.BlurInPlace runs —
        // KernelRadiusTexels(σ/down)·down phys px — so the region's scissor + the cross-frame pin capture keep every
        // non-zero tap (a HIT stays pixel-identical to a MISS). At σ ≤ 4 (down 1, the exact full-res path) it is the
        // un-capped ceil(3σ); at large σ it is the true ≈3σ reach, NOT the old 32 px cap (which truncated the Gaussian
        // to ~1.2σ at σ26). This ties the portable halo to AcrylicBackdropMath, the schedule owner.
        {
            bool haloOk = true;
            string detail = "";
            // Each σ: halo == kernel support, and (crucially) halo >= the down==1 full-res shader's discrete radius
            // min(ceil(3σ),32) at σ≤4 so the exact path's scissor never clips a tap.
            foreach (float s in new[] { 1f, 2f, 4f, 5f, 8f, 16f, 26f, 32f })
            {
                int down = AcrylicBackdropMath.DownsampleFactor(s, 1f);
                float texelSigma = AcrylicBackdropMath.EffectiveTexelSigma(s, 1f, down);
                int expected = AcrylicBackdropMath.KernelRadiusTexels(texelSigma) * down;
                int actual = SelfBlurRegion.TapRadius(s);
                bool covers = actual == expected && texelSigma <= AcrylicBackdropMath.MaxEffectiveTexelSigma + 1e-4f;
                if (s <= 4f) covers &= actual >= (int)MathF.Ceiling(s * 3f);   // exact path: halo covers the shader's radius
                if (!covers) { haloOk = false; detail += $" σ{s}:halo={actual}!=support{expected}(down{down},texelσ{texelSigma:0.00})"; }
            }
            // σ26 (the editorial-card case): down 8, texelσ 3.25, radius 10 texels ⇒ 80 px support — well past the old 32 cap.
            int r26 = SelfBlurRegion.TapRadius(26f);
            bool liftsCap = r26 == 80 && r26 > 32;
            Check("gate.blur.selfBlurHaloCoversKernel: the self-blur halo == the downsample schedule's KernelRadiusTexels(σ/down)·down support (σ26 ⇒ 80 px, lifting the old 32 px truncation)",
                haloOk && liftsCap, $"haloOk={haloOk} r26={r26}{detail}");
        }

        // gate.blur.tightWorkGeometry. A self blur has three distinct regions: the clipped output pixels, the subset of
        // crisp source that can contribute to those pixels, and their union (the local RT work box). All conversions are
        // conservative floor/ceil in physical pixels; blur sigma is already physical, so DPI scales geometry/clip but
        // not the kernel halo. These are the portable invariants used by the D3D12 local-surface implementation.
        {
            static PushLayerCmd Layer(RectF rect, float sigma, RectF clip) => new(
                rect, default, default, default, 0f, sigma, 0f, 0f,
                Kind: (int)LayerKind.Blur, CompositeClip: clip);
            static bool Is(SelfBlurPixelBox b, int x0, int y0, int x1, int y1)
                => b.MinX == x0 && b.MinY == y0 && b.MaxX == x1 && b.MaxY == y1;

            // DPI: floor left/top, ceil right/bottom, then add the physical (not DIP-scaled) sigma-3 halo of 9 px.
            var dpiLayer = Layer(new RectF(10.25f, 20.25f, 100.5f, 40.5f), 3f, new RectF(0f, 0f, 200f, 100f));
            var dpi = SelfBlurRegion.ComputeWork(in dpiLayer, 1.5f, 300, 150);
            bool dpiOk = Is(dpi.VisibleOutput, 6, 21, 176, 101)
                && Is(dpi.RequiredSource, 15, 30, 167, 92)
                && Is(dpi.Work, 6, 21, 176, 101);
            Check("gate.blur.tightWorkGeometry.dpi: DIP layer/clip scale conservatively while the physical sigma halo stays unscaled",
                dpiOk, $"out={dpi.VisibleOutput} src={dpi.RequiredSource} work={dpi.Work}");

            // Partial clip: only source within one halo of the 50x30 visible output is required. The far-left 128 px of
            // the layer never contributes, and the work area is much smaller than its full 224x84 halo box.
            var partialLayer = Layer(new RectF(100f, 100f, 200f, 60f), 4f, new RectF(240f, 110f, 50f, 30f));
            var partial = SelfBlurRegion.ComputeWork(in partialLayer, 1f, 500, 300);
            bool partialOk = Is(partial.VisibleOutput, 240, 110, 290, 140)
                && Is(partial.RequiredSource, 228, 100, 300, 152)
                && Is(partial.Work, 228, 100, 300, 152)
                && partial.Work.AreaPx == 3744 && partial.Work.AreaPx < 224L * 84L;
            Check("gate.blur.tightWorkGeometry.partialClip: clipped output pulls only its contributing crisp-source neighborhood",
                partialOk, $"out={partial.VisibleOutput} src={partial.RequiredSource} work={partial.Work} area={partial.Work.AreaPx}");

            // Canvas edge: off-canvas layer/halo pixels do not enter either source or output. A clip that touches only
            // the blur halo still retains the narrow strip of layer source that contributes to it.
            var edgeLayer = Layer(new RectF(-5f, 5f, 40f, 20f), 3f, RectF.Infinite);
            var edge = SelfBlurRegion.ComputeWork(in edgeLayer, 1f, 100, 50);
            var haloOnlyLayer = Layer(new RectF(100f, 100f, 200f, 60f), 4f, new RectF(305f, 110f, 5f, 30f));
            var haloOnly = SelfBlurRegion.ComputeWork(in haloOnlyLayer, 1f, 500, 300);
            bool edgesOk = Is(edge.VisibleOutput, 0, 0, 44, 34)
                && Is(edge.RequiredSource, 0, 5, 35, 25)
                && Is(edge.Work, 0, 0, 44, 34)
                && Is(haloOnly.VisibleOutput, 305, 110, 310, 140)
                && Is(haloOnly.RequiredSource, 293, 100, 300, 152)
                && Is(haloOnly.Work, 293, 100, 310, 152);
            Check("gate.blur.tightWorkGeometry.edges: canvas clamping is exact and a halo-only clip keeps its contributing source strip",
                edgesOk, $"edge(out={edge.VisibleOutput} src={edge.RequiredSource}) halo(out={haloOnly.VisibleOutput} src={haloOnly.RequiredSource})");

            var outsideLayer = Layer(new RectF(100f, 100f, 80f, 40f), 3f, new RectF(300f, 200f, 20f, 20f));
            var outside = SelfBlurRegion.ComputeWork(in outsideLayer, 1f, 500, 300);
            Check("gate.blur.tightWorkGeometry.outsideClip: a clip outside the halo schedules zero blur work",
                outside.VisibleOutput.IsEmpty && outside.RequiredSource.IsEmpty && outside.Work.IsEmpty,
                $"out={outside.VisibleOutput} src={outside.RequiredSource} work={outside.Work}");
        }
    }

    static void ExpressiveMotionChecks(StringTable strings)
    {
        // EM.a — the four expressive curves: endpoints 0→1; SmoothOut decelerates (past halfway by t=0.5); Overshoot &
        // Pop exceed 1.0 mid-flight (the spring-past-target look); OvershootStrong peaks highest of all.
        bool ends =
            Near(Easings.Ease(Easing.SmoothOut, 0f), 0f, 1e-3f) && Near(Easings.Ease(Easing.SmoothOut, 1f), 1f, 1e-3f) &&
            Near(Easings.Ease(Easing.Overshoot, 0f), 0f, 1e-3f) && Near(Easings.Ease(Easing.Overshoot, 1f), 1f, 1e-3f) &&
            Near(Easings.Ease(Easing.OvershootStrong, 0f), 0f, 1e-3f) && Near(Easings.Ease(Easing.OvershootStrong, 1f), 1f, 1e-3f) &&
            Near(Easings.Ease(Easing.Pop, 0f), 0f, 1e-3f) && Near(Easings.Ease(Easing.Pop, 1f), 1f, 1e-3f);
        bool smoothDecel = Easings.Ease(Easing.SmoothOut, 0.5f) > 0.6f;
        float ovPeak = 0f, ovStrongPeak = 0f, popPeak = 0f;
        for (int i = 1; i < 100; i++)
        {
            float t = i / 100f;
            ovPeak = MathF.Max(ovPeak, Easings.Ease(Easing.Overshoot, t));
            ovStrongPeak = MathF.Max(ovStrongPeak, Easings.Ease(Easing.OvershootStrong, t));
            popPeak = MathF.Max(popPeak, Easings.Ease(Easing.Pop, t));
        }
        bool overshoots = ovPeak > 1.0f && popPeak > 1.0f && ovStrongPeak > ovPeak;
        Check("EM.a expressive curves: 0→1 endpoints, SmoothOut decelerates, Overshoot/Pop exceed 1, OvershootStrong peaks highest",
            ends && smoothDecel && overshoots,
            $"smooth@.5={Easings.Ease(Easing.SmoothOut, 0.5f):0.00} ovPeak={ovPeak:0.00} popPeak={popPeak:0.00} strongPeak={ovStrongPeak:0.00}");

        // EM.b — AnimChannel.BlurSigma eases NodePaint.BlurSigma (8→0), marks PaintDirty (never LayoutDirty), settles at 0.
        {
            var scene = new SceneStore();
            var node = scene.CreateNode(1);
            scene.Root = node;
            var engine = new AnimEngine(scene);
            scene.Paint(node).BlurSigma = 8f;
            scene.Flags(node) &= ~(NodeFlags.PaintDirty | NodeFlags.LayoutDirty | NodeFlags.TransformDirty);
            engine.Animate(node, AnimChannel.BlurSigma, 8f, 0f, 100f, Easing.Linear);
            bool intentSeeded = scene.Paint(node).BlurAnimationActive != 0;
            engine.Tick(0f);
            float b0 = scene.Paint(node).BlurSigma;
            engine.Tick(50f);
            float bMid = scene.Paint(node).BlurSigma;
            var fl = scene.Flags(node);
            bool midOk = Near(bMid, 4f, 0.2f) && scene.Paint(node).BlurAnimationActive != 0
                && (fl & NodeFlags.PaintDirty) != 0 && (fl & NodeFlags.LayoutDirty) == 0;
            engine.Tick(60f);   // > 100ms → complete
            float bEnd = scene.Paint(node).BlurSigma;
            bool doneOk = Near(bEnd, 0f, 1e-3f) && !engine.HasActive && scene.Paint(node).BlurAnimationActive == 0;

            // The bit follows slab lifecycle rather than sigma: KeepAlive parking turns it off without destroying
            // the row, resume restores it, and cancellation clears it even if the last blur value remains.
            engine.Animate(node, AnimChannel.BlurSigma, 0f, 8f, 100f, Easing.Linear);
            bool reseeded = scene.Paint(node).BlurAnimationActive != 0;
            engine.SetNodeParked(node, true);
            bool parked = scene.Paint(node).BlurAnimationActive == 0;
            engine.SetNodeParked(node, false);
            bool resumed = scene.Paint(node).BlurAnimationActive != 0;
            engine.Cancel(node, AnimChannel.BlurSigma);
            bool cancelled = scene.Paint(node).BlurAnimationActive == 0;
            Check("EM.b BlurSigma eases 8→0 and transient intent follows live/non-parked track lifecycle",
                intentSeeded && Near(b0, 8f, 0.1f) && midOk && doneOk && reseeded && parked && resumed && cancelled,
                $"intentSeed={intentSeeded} t0={b0:0.0} mid={bMid:0.0} end={bEnd:0.00} done={doneOk} park={parked}/{resumed} cancel={cancelled}");
        }

        // EM.c — the recorder wraps a node with Blur>0 in a balanced PushLayer{Blur} carrying its σ; a 0-blur node does not.
        {
            var s = new SceneStore();
            var recon = new TreeReconciler(s, strings);
            recon.ReconcileRoot(new BoxEl
            {
                Width = 80, Height = 60, ClipToBounds = true, Fill = ColorF.FromRgba(0x20, 0x20, 0x20),
                Children = [new BoxEl { Width = 30, Height = 30, Blur = 6f, Fill = ColorF.FromRgba(0x60, 0xCD, 0xFF) }],
            }, null);
            new FlexLayout(s, new HeadlessFontSystem(strings)).Run(s.Root);
            var dl = new DrawList();
            SceneRecorder.Record(s, dl);
            var dev = new HeadlessGpuDevice();
            dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(120, 80), 1f, ColorF.Transparent));
            bool blurLayer = false; float sigma = 0f; RectF blurClip = default; bool staticIntent = true;
            foreach (var l in dev.LastLayers) if (l.Kind == (int)LayerKind.Blur)
            {
                blurLayer = true; sigma = l.BlurSigma; blurClip = l.CompositeClip;
                staticIntent &= l.BlurIsTransient == 0;
            }
            bool balanced = dev.LayerBalance == 0;

            // A real BlurSigma row propagates the engine-owned transient intent into the POD layer command.
            var animatedNode = s.FirstChild(s.Root);
            var anim = new AnimEngine(s);
            anim.Animate(animatedNode, AnimChannel.BlurSigma, 6f, 5f, 100f, Easing.Linear);
            anim.Tick(0f);
            var dlAnimated = new DrawList();
            SceneRecorder.Record(s, dlAnimated);
            var devAnimated = new HeadlessGpuDevice();
            devAnimated.SubmitDrawList(dlAnimated.Bytes, dlAnimated.SortKeys, new FrameInfo(new Size2(120, 80), 1f, ColorF.Transparent));
            bool transientIntent = false;
            foreach (var l in devAnimated.LastLayers)
                if (l.Kind == (int)LayerKind.Blur) transientIntent |= l.BlurIsTransient == 1;

            var s0 = new SceneStore();
            var recon0 = new TreeReconciler(s0, strings);
            recon0.ReconcileRoot(new BoxEl
            {
                Width = 80, Height = 60, Fill = ColorF.FromRgba(0x20, 0x20, 0x20),
                Children = [new BoxEl { Width = 30, Height = 30, Fill = ColorF.FromRgba(0x60, 0xCD, 0xFF) }],
            }, null);
            new FlexLayout(s0, new HeadlessFontSystem(strings)).Run(s0.Root);
            var dl0 = new DrawList();
            SceneRecorder.Record(s0, dl0);
            var dev0 = new HeadlessGpuDevice();
            dev0.SubmitDrawList(dl0.Bytes, dl0.SortKeys, new FrameInfo(new Size2(120, 80), 1f, ColorF.Transparent));
            bool noBlurWhenZero = true;
            foreach (var l in dev0.LastLayers) if (l.Kind == (int)LayerKind.Blur) noBlurWhenZero = false;

            // A delayed stagger row starts at alpha zero while retaining a non-zero blur channel. It is still walked,
            // but its blur is exact dead work and must not produce an offscreen layer until it becomes visible.
            var si = new SceneStore();
            var reconi = new TreeReconciler(si, strings);
            reconi.ReconcileRoot(new BoxEl
            {
                Width = 80, Height = 60,
                Children = [new BoxEl { Width = 30, Height = 30, Opacity = 0f, Blur = 6f, Fill = ColorF.FromRgba(0x60, 0xCD, 0xFF) }],
            }, null);
            new FlexLayout(si, new HeadlessFontSystem(strings)).Run(si.Root);
            var dli = new DrawList();
            SceneRecordStats invisibleStats = SceneRecorder.Record(si, dli);
            var devi = new HeadlessGpuDevice();
            devi.SubmitDrawList(dli.Bytes, dli.SortKeys, new FrameInfo(new Size2(120, 80), 1f, ColorF.Transparent));
            bool noBlurWhenInvisible = invisibleStats.BlurCandidateCount == 0;
            foreach (var l in devi.LastLayers) if (l.Kind == (int)LayerKind.Blur) noBlurWhenInvisible = false;
            bool activeClipCarried = Near(blurClip.X, 0f) && Near(blurClip.Y, 0f) && Near(blurClip.W, 80f) && Near(blurClip.H, 60f);

            Check("EM.c recorder emits balanced PushLayer{Blur} carrying clip + static/transient intent; none for invisible/zero blur",
                blurLayer && Near(sigma, 6f, 0.01f) && activeClipCarried && balanced && staticIntent && transientIntent
                    && noBlurWhenZero && noBlurWhenInvisible,
                $"blurLayer={blurLayer} sigma={sigma:0.0} clip={blurClip} balanced={balanced} static={staticIntent} transient={transientIntent} noneZero={noBlurWhenZero} noneInvisible={noBlurWhenInvisible}");
        }

        // EM.c2 — a self-blur is visible by its Gaussian support, not only by the sharp layout rect. The recorder must
        // both emit the off-viewport source glyphs and push a widened INNER clip; CompositeClip stays the viewport.
        // Exercise the DIP↔physical rounding at 100% and 150%, then prove the non-blur and edge-fade cases stay culled.
        {
            bool dpiCases = true;
            string dpiDetail = "";
            foreach (var (scale, rowY) in new[] { (1f, 64f), (1.5f, 63f) })
            {
                string label = $"halo-{scale:0.0}";
                var hs = new SceneStore { DeviceScale = scale };
                new TreeReconciler(hs, strings).ReconcileRoot(new BoxEl
                {
                    Direction = 1, Width = 80f, Height = 60f, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl { Height = rowY, Shrink = 0f },
                        new BoxEl
                        {
                            Height = 12f, Shrink = 0f, Blur = 2f,
                            Children = [new TextEl(label) { Size = 10f, Color = ColorF.FromRgba(255, 255, 255) }],
                        },
                    ],
                }, null);
                new FlexLayout(hs, new HeadlessFontSystem(strings)).Run(hs.Root);
                var hdl = new DrawList();
                SceneRecorder.Record(hs, hdl);
                var hdev = new HeadlessGpuDevice();
                hdev.SubmitDrawList(hdl.Bytes, hdl.SortKeys, new FrameInfo(new Size2(120, 80), scale, ColorF.Transparent));

                bool layer = false, compositeClip = false, sourceClip = false;
                foreach (var l in hdev.LastLayers)
                    if (l.Kind == (int)LayerKind.Blur)
                    {
                        layer = true;
                        compositeClip |= Near(l.CompositeClip.Y, 0f) && Near(l.CompositeClip.H, 60f);
                    }
                foreach (var c in hdev.LastClips)
                    sourceClip |= c.DeviceRect.Y > 60f && c.DeviceRect.Bottom > rowY;
                bool ok = layer && compositeClip && sourceClip && HasGlyph(hdev, strings, label)
                    && hdev.ClipBalance == 0 && hdev.LayerBalance == 0;
                dpiCases &= ok;
                if (!ok) dpiDetail += $" @{scale:0.0}(layer={layer} comp={compositeClip} src={sourceClip} glyph={HasGlyph(hdev, strings, label)})";

                // Removing the blur leaves the same sharp glyph wholly outside the viewport; no layer or glyph remains.
                var spacer = hs.FirstChild(hs.Root);
                var blurNode = hs.NextSibling(spacer);
                hs.Paint(blurNode).BlurSigma = 0f;
                SceneRecorder.Record(hs, hdl);
                hdev.SubmitDrawList(hdl.Bytes, hdl.SortKeys, new FrameInfo(new Size2(120, 80), scale, ColorF.Transparent));
                bool zeroCulled = !HasGlyph(hdev, strings, label);
                foreach (var l in hdev.LastLayers) zeroCulled &= l.Kind != (int)LayerKind.Blur;
                dpiCases &= zeroCulled;
                if (!zeroCulled) dpiDetail += $" @{scale:0.0}(zero-not-culled)";
            }

            Check("EM.c2 self-blur halo overlap records its off-viewport source through an inner clip at 100%/150%; zero blur stays culled",
                dpiCases, dpiDetail);

            // Span/subtree cull: the wrapper's own sharp box remains below the viewport, but its translated descendant
            // halo enters. The prior subtree bound must carry that halo so the clean wrapper is walked, not StoreCulled.
            var ss = new SceneStore();
            new TreeReconciler(ss, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1, Width = 80f, Height = 60f, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Height = 80f, Shrink = 0f },
                    new BoxEl
                    {
                        Height = 12f, Shrink = 0f,
                        Children =
                        [
                            new BoxEl
                            {
                                Height = 12f, Blur = 2f,
                                Children = [new TextEl("span-halo") { Size = 10f, Color = ColorF.FromRgba(255, 255, 255) }],
                            },
                        ],
                    },
                ],
            }, null);
            new FlexLayout(ss, new HeadlessFontSystem(strings)).Run(ss.Root);
            var spans = new SpanTable();
            var sdl = new DrawList();
            SceneRecorder.Record(ss, sdl, spans: spans);
            ss.ClearRecordDirty();
            var wrapper = ss.NextSibling(ss.FirstChild(ss.Root));
            ref RectF wrapperBounds = ref ss.Bounds(wrapper);
            wrapperBounds = new RectF(wrapperBounds.X, 64f, wrapperBounds.W, wrapperBounds.H);
            SceneRecorder.Record(ss, sdl, spans: spans, spanReuseDisabled: SpanReuseDisabledReason.Layout);
            var sdev = new HeadlessGpuDevice();
            sdev.SubmitDrawList(sdl.Bytes, sdl.SortKeys, new FrameInfo(new Size2(120, 80), 1f, ColorF.Transparent));
            bool spanEntry = HasGlyph(sdev, strings, "span-halo");
            bool hasSpanBlur = false;
            foreach (var l in sdev.LastLayers) hasSpanBlur |= l.Kind == (int)LayerKind.Blur;
            Check("EM.c3 halo-bearing span bounds prevent an off-screen clean ancestor from culling an entering blurred descendant",
                spanEntry && hasSpanBlur && sdev.ClipBalance == 0 && sdev.LayerBalance == 0,
                $"glyph={spanEntry} blur={hasSpanBlur} clips={sdev.ClipBalance} layers={sdev.LayerBalance}");

            // EdgeFade owns group semantics even when only a Blur property halo would overlap the clip.
            var es = new SceneStore();
            new TreeReconciler(es, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1, Width = 80f, Height = 60f, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Height = 64f, Shrink = 0f },
                    new BoxEl
                    {
                        Height = 12f, Shrink = 0f, Blur = 2f,
                        EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 6f),
                        Children = [new TextEl("edge-precedence") { Size = 10f, Color = ColorF.FromRgba(255, 255, 255) }],
                    },
                ],
            }, null);
            new FlexLayout(es, new HeadlessFontSystem(strings)).Run(es.Root);
            var edl = new DrawList();
            SceneRecorder.Record(es, edl);
            var edev = new HeadlessGpuDevice();
            edev.SubmitDrawList(edl.Bytes, edl.SortKeys, new FrameInfo(new Size2(120, 80), 1f, ColorF.Transparent));
            bool noWrongBlur = true;
            foreach (var l in edev.LastLayers) noWrongBlur &= l.Kind != (int)LayerKind.Blur;
            Check("EM.c4 edge-fade presence prevents a halo-only self-blur precedence flip",
                noWrongBlur && !HasGlyph(edev, strings, "edge-precedence"),
                $"layers={edev.LastLayers.Count} glyph={HasGlyph(edev, strings, "edge-precedence")}");
        }

        // EM.d — the PopIn recipe (number pop-in) seeds Opacity 0→1 + TranslateY dist→0 + Blur small→0, and settles to
        // rest (the recipe library composes the new curves + blur channel, not just one track).
        {
            var scene = new SceneStore();
            var node = scene.CreateNode(1);
            scene.Root = node;
            var engine = new AnimEngine(scene);
            engine.PopIn(node, dirY: 1f, distance: 8f, blur: 2f, durationMs: 100f);
            engine.Tick(0f);
            ref NodePaint pp = ref scene.Paint(node);
            bool t0 = Near(pp.Opacity, 0f, 0.05f) && Near(pp.LocalTransform.Dy, 8f, 0.5f) && Near(pp.BlurSigma, 2f, 0.1f);
            engine.Tick(120f);   // > 100ms → all tracks complete
            bool settled = Near(pp.Opacity, 1f, 0.01f) && Near(pp.LocalTransform.Dy, 0f, 0.2f) && Near(pp.BlurSigma, 0f, 0.01f) && !engine.HasActive;
            Check("EM.d PopIn recipe seeds Opacity+TranslateY+Blur and settles to rest", t0 && settled,
                $"t0(op={pp.Opacity:0.00} dy=8 blur=2)={t0} settled={settled}");
        }

        // EM.e — the Shake recipe (error shake) is a single multi-segment TranslateX path that swings to +distance and
        // settles back to 0 (one Replace track, completes).
        {
            var scene = new SceneStore();
            var node = scene.CreateNode(1);
            scene.Root = node;
            var engine = new AnimEngine(scene);
            engine.Shake(node, distance: 6f, overshoot: 4f, durationMs: 280f);
            engine.Tick(0f);
            engine.Tick(80f);   // 80/280 = 28.57% → the +distance peak keyframe
            float dxPeak = scene.Paint(node).LocalTransform.Dx;
            for (int i = 0; i < 16 && engine.HasActive; i++) engine.Tick(16f);   // run to settle (≈256ms more)
            float dxEnd = scene.Paint(node).LocalTransform.Dx;
            Check("EM.e Shake recipe swings to +distance then settles to 0", dxPeak > 3f && Near(dxEnd, 0f, 0.2f) && !engine.HasActive,
                $"peak={dxPeak:0.0} end={dxEnd:0.00} active={engine.HasActive}");
        }

        // EM.f — transitions.dev state/page/refit terminals and the independent interactive-resize policy.
        {
            var text = MotionRecipes.TextSwap;
            var forward = MotionRecipes.PageSlideForward;
            var back = MotionRecipes.PageSlideBack;
            var height = MotionRecipes.CardResizeHeight;
            // Page slides are deliberately BLUR-FREE: a page-root BlurSigma makes the whole page a blur group
            // (canvas-sized offscreen RT + 2-pass Gaussian per transition frame — the measured ~13ms-vs-7ms GPU
            // regression). TextSwap keeps its blur (a tiny element, not a page).
            bool recipes = text.Enter.Dy == 4f && text.Exit.Dy == -4f && text.Enter.Blur == 2f
                && forward.Enter.Dx == 8f && forward.Exit.Dx == -8f && forward.Enter.Blur == 0f && forward.Exit.Blur == 0f
                && back.Enter.Dx == -8f && back.Exit.Dx == 8f && back.Enter.Blur == 0f && back.Exit.Blur == 0f
                && height.Axes == SizeAxes.Height && height.Size == SizeMode.Reflow;

            Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.WindowResize, true);
            Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, true);
            Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.WindowResize, false);
            bool independentlyOwned = Motion.LayoutTransitionsSuppressed;
            Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, false);
            bool cleared = !Motion.LayoutTransitionsSuppressed;
            Check("EM.f text/page/refit recipes carry the authored terminals; resize suppression is independently owned",
                recipes && independentlyOwned && cleared,
                $"recipes={recipes} independentlyOwned={independentlyOwned} cleared={cleared}");
        }

        // EM.g — suppression gates STARTS: SnapStructuralToLayout (the branch ApplyProjections takes while
        // Motion.LayoutTransitionsSuppressed) cancels an in-flight FLIP track and lands the node at its laid-out
        // geometry immediately — no projection keeps running, no residual transform offset.
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 200, 50, 20);
            var engine = new AnimEngine(scene);
            var spring = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(1.0f, 1.0f));
            engine.AnimateBounds(n, new RectF(0, 100, 50, 20), new RectF(0, 200, 50, 20), spring);
            for (int i = 0; i < 4; i++) engine.Tick(16f);
            bool wasFlying = MathF.Abs(scene.Paint(n).LocalTransform.Dy) > 5f && engine.HasActive;
            engine.SnapStructuralToLayout(n);                        // the suppressed-branch action
            bool snapped = scene.Paint(n).LocalTransform.IsIdentity; // lands at final geometry (no residual offset)
            bool noTrack = !engine.HasActive;
            engine.Tick(16f);                                        // and none re-seeds next tick
            bool stays = scene.Paint(n).LocalTransform.IsIdentity && !engine.HasActive;
            Check("EM.g suppression snap cancels the in-flight projection and lands at final geometry (no residual offset)",
                wasFlying && snapped && noTrack && stays,
                $"wasFlying={wasFlying} snapped={snapped} noTrack={noTrack} stays={stays}");
        }

        // EM.h — a resize frame (CancelStructuralAll over the FLIP set) cancels an in-flight structural transition:
        // SizeMode.Relayout li.Width/Height restore to the DECLARED value (NaN = auto here), PresentedW resets, the
        // Relayouting flag clears, and the FLIP position offset is gone — bounds land clean, no poisoned layout input.
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref LayoutInput li = ref scene.Layout(n); li.Width = float.NaN; li.Height = float.NaN;  // declared = auto
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 0, 100, 200);
            var engine = new AnimEngine(scene);
            var refit = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Size,
                TransitionDynamics.Tween(300f, Easing.SmoothOut), Size: SizeMode.Relayout);
            engine.AnimateBounds(n, new RectF(0, 100, 200, 200), new RectF(0, 0, 100, 200), refit);  // moved (Y 100→0) + width 200→100
            for (int i = 0; i < 3; i++) engine.Tick(16f);
            bool inFlight = engine.HasActive && !float.IsNaN(scene.Paint(n).PresentedW)
                && (scene.Flags(n) & NodeFlags.Relayouting) != 0 && MathF.Abs(scene.Paint(n).LocalTransform.Dy) > 1f;
            engine.CancelStructuralAll(new List<NodeHandle> { n });   // the resize-frame action
            bool liRestored = float.IsNaN(scene.Layout(n).Width) && float.IsNaN(scene.Layout(n).Height);  // declared, not stale interp
            bool presentedReset = float.IsNaN(scene.Paint(n).PresentedW);
            bool relayoutCleared = (scene.Flags(n) & NodeFlags.Relayouting) == 0;
            bool noOffset = scene.Paint(n).LocalTransform.IsIdentity;
            bool noTrack = !engine.HasActive;
            Check("EM.h resize-frame cancel restores declared LayoutInput (NaN), resets presented size + transform, clears Relayouting",
                inFlight && liRestored && presentedReset && relayoutCleared && noOffset && noTrack,
                $"inFlight={inFlight} li={liRestored} pres={presentedReset} relayout={relayoutCleared} offset={noOffset} track={noTrack}");
        }
    }

    private sealed record SkTrack(int Number, string Title, string Dur);

    // Host-level shape of Wavee Home: a grow-to-viewport Skel.Region whose one authored content tree is a measured
    // virtual list. The controllable loadable lets SK.k exercise the real Post → Flush → scoped-layout path without
    // manufacturing a focus/resize event.
    private sealed class SkeletonVirtualHostProbe : Component
    {
        public readonly Loadable<int> Count = Loadable<int>.Pending(6);
        private readonly MeasuredStackVirtualLayout _layout = new(72f);

        public override Element Render() => new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            Children =
            [
                Skel.Region(Count, n => Virtual.Measured(n, _layout,
                    i => new BoxEl
                    {
                        Direction = 1, Height = 72f,
                        Children = [SkRow(new SkTrack(i + 1, "Host " + i, "0:00"))],
                    }, keyOf: i => i.ToString()) with
                    { Grow = 1f, Shrink = 1f, MinHeight = 0f },
                    reveal: SkelReveal.StaggerRows),
            ],
        };
    }

    static Element SkRow(SkTrack? t) => new BoxEl
    {
        Direction = 0, Gap = 12f,
        Children =
        [
            new TextEl(t is null ? "" : t.Number.ToString()) { Size = 14f, Width = 24f },
            new TextEl(t?.Title ?? "") { Size = 14f, Grow = 1f },
            new TextEl(t?.Dur ?? "") { Size = 13f, Width = 48f },
        ],
    };

    static void SkeletonChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // SK.a derivation fidelity + SK.b swap + SK.c wake-loop.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var tracks = Loadable<SkTrack[]>.Pending(Array.Empty<SkTrack>());
            recon.ReconcileRoot(
                Skel.Region(tracks, SkRow, count: 5,
                    content: ts => Flow.For<SkTrack>(() => ts, t => t.Number.ToString(), (t, i) => SkRow(t)),
                    reveal: SkelReveal.StaggerRows),
                null);
            new FlexLayout(scene, fonts).Run(scene.Root);

            var region = scene.Root;
            var shimmer = Child(scene, region, 0);
            int shimmerRows = 0; for (var c = scene.FirstChild(shimmer); !c.IsNull; c = scene.NextSibling(c)) shimmerRows++;
            var row0 = Child(scene, shimmer, 0);
            bool widths = Near(scene.Bounds(Child(scene, row0, 0)).W, 24f, 0.5f) && Near(scene.Bounds(Child(scene, row0, 2)).W, 48f, 0.5f);
            bool noTextPending = CountText(scene, region) == 0;
            bool pulsing = engine.LoopTrackCount >= 1;
            Check("SK.a skeleton derives N shimmer rows from the ONE row template (declared bar widths 24/48; no real text; pulsing)",
                shimmerRows == 5 && widths && noTextPending && pulsing,
                $"rows={shimmerRows} bar0={scene.Bounds(Child(scene, row0, 0)).W:0} bar2={scene.Bounds(Child(scene, row0, 2)).W:0} text={CountText(scene, region)} loops={engine.LoopTrackCount}");

            tracks.SetReady(new[] { new SkTrack(1, "One", "1:01"), new SkTrack(2, "Two", "2:02"), new SkTrack(3, "Three", "3:03") });
            recon.Runtime.Flush();
            new FlexLayout(scene, fonts).Run(scene.Root);
            bool realText = CountText(scene, region) > 0;
            engine.Tick(0f);
            var realRow0 = Child(scene, Child(scene, region, 0), 0);
            bool revealSeeded = engine.TryGetTrackValue(realRow0, AnimChannel.Opacity, out var op0) && op0 < 0.2f;
            Check("SK.b Pending→Ready swaps shimmer→real (text appears) and blur-reveals the rows",
                realText && revealSeeded, $"realText={realText} revealOp0={op0:0.00}");

            for (int i = 0; i < 80 && engine.HasActive; i++) engine.Tick(16f);
            Check("SK.c the looping skeleton pulse is cancelled on swap (no loop pins the orphan — wake-loop fix)",
                engine.LoopTrackCount == 0, $"loops={engine.LoopTrackCount} active={engine.HasActive}");
        }

        // SK.d partial-known: a pre-Ready region renders real immediately (no shimmer).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var ready = Loadable<SkTrack[]>.Ready(new[] { new SkTrack(1, "Known", "0:30") });
            recon.ReconcileRoot(
                Skel.Region(ready, SkRow, count: 3, content: ts => Flow.For<SkTrack>(() => ts, t => t.Number.ToString(), (t, i) => SkRow(t))),
                null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            Check("SK.d a pre-Ready region (partial-known data) renders REAL immediately — no shimmer",
                CountText(scene, scene.Root) > 0, $"text={CountText(scene, scene.Root)}");
        }

        // SK.e incremental per-field: .Pending(field) shimmers ONE leaf in place and reveals on the field flip.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var dur = Loadable<string>.Pending("");
            Element leaf = new TextEl("") { Text = dur.Bind(), Size = 13f, Width = 48f }.Pending(dur);
            recon.ReconcileRoot(new BoxEl { Direction = 0, Children = [new TextEl("Title") { Size = 14f }, leaf] }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var leafRegion = Child(scene, scene.Root, 1);
            bool pendingBar = CountText(scene, leafRegion) == 0;
            dur.SetReady("3:14");
            recon.Runtime.Flush();
            new FlexLayout(scene, fonts).Run(scene.Root);
            bool nowReal = CountText(scene, leafRegion) > 0;
            Check("SK.e incremental field (.Pending) shimmers ONE leaf in place, reveals on the field flip (row identity kept)",
                pendingBar && nowReal, $"pendingBar={pendingBar} nowReal={nowReal}");
        }

        // SK.f failed: SetFailed mounts the onFailed branch.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var ld = Loadable<SkTrack[]>.Pending(Array.Empty<SkTrack>());
            recon.ReconcileRoot(
                Skel.Region(ld, SkRow, count: 3, content: ts => Flow.For<SkTrack>(() => ts, t => t.Number.ToString(), (t, i) => SkRow(t)),
                    onFailed: () => new TextEl("FAILED") { Size = 14f }),
                null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            ld.SetFailed(new Exception("nope"));
            recon.Runtime.Flush();
            new FlexLayout(scene, fonts).Run(scene.Root);
            Check("SK.f a failed load mounts the onFailed branch", CountText(scene, scene.Root) == 1, $"text={CountText(scene, scene.Root)}");
        }

        // SK.g reduced motion: structural swap occurs, but no pulse/reveal tracks are seeded (snap).
        {
            bool prev = Motion.ReducedMotion;
            Motion.ReducedMotion = true;
            try
            {
                var scene = new SceneStore();
                var engine = new AnimEngine(scene);
                var recon = new TreeReconciler(scene, strings) { Anim = engine };
                var tracks = Loadable<SkTrack[]>.Pending(Array.Empty<SkTrack>());
                recon.ReconcileRoot(
                    Skel.Region(tracks, SkRow, count: 3, content: ts => Flow.For<SkTrack>(() => ts, t => t.Number.ToString(), (t, i) => SkRow(t))),
                    null);
                new FlexLayout(scene, fonts).Run(scene.Root);
                bool noPulse = engine.LoopTrackCount == 0;
                tracks.SetReady(new[] { new SkTrack(1, "R", "0:01") });
                recon.Runtime.Flush();
                new FlexLayout(scene, fonts).Run(scene.Root);
                bool real = CountText(scene, scene.Root) > 0;
                bool noReveal = !engine.HasActive;
                Check("SK.g reduced motion: structural swap occurs but no pulse/reveal tracks are seeded (snap)",
                    noPulse && real && noReveal, $"noPulse={noPulse} real={real} noReveal={noReveal}");
            }
            finally { Motion.ReducedMotion = prev; }
        }

        // RM.a reduced-motion-as-value (engine seed, the rework): under reduced motion a SnapEnd token snaps EVERY channel
        // (incl Opacity) to its end-state immediately — no glide, settles in one frame; a KeepFade token still cross-fades
        // Opacity (a fade aids orientation, it is not "motion"). Proves the engine reads reduced-motion as DATA at the seed
        // (AnimScheduler.Structural.ReducedSnap), never an early-return — the [[motion-hooks-reducedmotion-conditional]] fix.
        {
            bool prev = Motion.ReducedMotion;
            Motion.ReducedMotion = true;
            try
            {
                var sceneA = new SceneStore();
                var engineA = new AnimEngine(sceneA);
                new TreeReconciler(sceneA, strings).ReconcileRoot(new BoxEl { Width = 40, Height = 40, Fill = ColorF.FromRgba(0, 0, 0) }, null);
                engineA.SeedEnter(sceneA.Root, new EnterExit(Opacity: 0f, Active: true), MotionTokenDef.Eased(300f, Easing.FluentDecelerate));   // bare Eased ⇒ SnapEnd
                engineA.Tick(16f); engineA.Tick(16f);
                bool snapEnd = sceneA.Paint(sceneA.Root).Opacity > 0.99f && !engineA.HasActive;

                var sceneB = new SceneStore();
                var engineB = new AnimEngine(sceneB);
                new TreeReconciler(sceneB, strings).ReconcileRoot(new BoxEl { Width = 40, Height = 40, Fill = ColorF.FromRgba(0, 0, 0) }, null);
                engineB.SeedEnter(sceneB.Root, new EnterExit(Opacity: 0f, Active: true), MotionTok.StandardEnter);   // KeepFade
                engineB.Tick(16f); engineB.Tick(16f);   // 2 ticks: the seed frame holds the initial value, the advance begins next frame
                float opB = sceneB.Paint(sceneB.Root).Opacity;
                bool keepFade = opB > 0.001f && opB < 0.99f && engineB.HasActive;

                Check("RM.a reduced-motion-as-value: SnapEnd token snaps Opacity to end (no glide); KeepFade still cross-fades",
                    snapEnd && keepFade, $"snapEnd={snapEnd} keepFadeOp={opB:0.00} keepFadeActive={engineB.HasActive}");
            }
            finally { Motion.ReducedMotion = prev; }
        }

        // ST.a Stagger (declarative): a parent's Stagger delays each child's Enter by (sibling index × stagger ms) — the
        // staggered list/shelf reveal. After a sub-stagger tick, child 0 is fading in but child 2 is still delayed (0 opacity).
        {
            var sceneS = new SceneStore();
            var engineS = new AnimEngine(sceneS);
            var reconS = new TreeReconciler(sceneS, strings) { Anim = engineS };
            EnterExit fadeS = new(Opacity: 0f, Active: true);
            reconS.ReconcileRoot(new BoxEl
            {
                Direction = 1, Stagger = 100f,
                Children =
                [
                    new BoxEl { Width = 20, Height = 20, Fill = ColorF.FromRgba(0, 0, 0), Enter = fadeS, Transition = MotionTok.ControlNormal },
                    new BoxEl { Width = 20, Height = 20, Fill = ColorF.FromRgba(0, 0, 0), Enter = fadeS, Transition = MotionTok.ControlNormal },
                    new BoxEl { Width = 20, Height = 20, Fill = ColorF.FromRgba(0, 0, 0), Enter = fadeS, Transition = MotionTok.ControlNormal },
                ],
            }, null);
            new FlexLayout(sceneS, fonts).Run(sceneS.Root);
            var cs0 = Child(sceneS, sceneS.Root, 0);
            var cs2 = Child(sceneS, sceneS.Root, 2);
            engineS.Tick(16f); engineS.Tick(16f); engineS.Tick(16f);   // ~48ms < the 100ms stagger to child 1 (200ms to child 2)
            float ops0 = sceneS.Paint(cs0).Opacity, ops2 = sceneS.Paint(cs2).Opacity;
            Check("ST.a Stagger: a parent staggers child Enters (child 0 revealing, child 2 still delayed)",
                ops0 > 0.01f && ops2 < 0.01f, $"op0={ops0:0.00} op2={ops2:0.00}");
        }

        // RT.a FLIP relativeTarget: a follower's RelativeTo resolves to the live node carrying that MorphId (the
        // shared-layout anchor the host's projection capture FLIPs against, so the follower rides the anchor coherently
        // instead of double-counting its motion). A plain node resolves to none (the default parent-relative FLIP).
        {
            var sceneR = new SceneStore();
            var reconR = new TreeReconciler(sceneR, strings);
            reconR.ReconcileRoot(new BoxEl
            {
                Children =
                [
                    new BoxEl { Width = 20, Height = 20, MorphId = "grp" },     // the anchor
                    new BoxEl { Width = 20, Height = 20, RelativeTo = "grp" },   // the follower
                    new BoxEl { Width = 20, Height = 20 },                       // a plain node (no relativeTarget)
                ],
            }, null);
            var anchorR = Child(sceneR, sceneR.Root, 0);
            var followerR = Child(sceneR, sceneR.Root, 1);
            var plainR = Child(sceneR, sceneR.Root, 2);
            bool resolves = reconR.ResolveRelativeTarget(followerR) == anchorR;
            bool plainNull = reconR.ResolveRelativeTarget(plainR).IsNull;
            Check("RT.a FLIP relativeTarget: a follower resolves to its keyed shared-layout anchor (plain node → none)",
                resolves && plainNull, $"resolves={resolves} plainNull={plainNull}");
        }

        // CF.a connected-fly rebuild (FG_DETACHED_FLY): SceneRecorder.RecordDetached draws a DetachedAnimSlab snapshot as
        // an image at its baked WORLD transform + opacity — the render path that replaces the live overlay node. Device-
        // verified directly (the per-frame ConnectedAnimation.SyncDetached mirror, which feeds these fields, uses the exact
        // recorder world formula, so a fly drawn this way is pixel-identical to the live-overlay path).
        {
            var slab = new DetachedAnimSlab();
            int g = slab.OpenGroup(default, PresenceMode.Sync);
            int s = slab.Detach(g);
            ref DetachedNode d = ref slab.At(s);
            d.Kind = (byte)VisualKind.Image;
            d.ImageId = 1;                                   // images=null ⇒ placeholder-fill path; we verify the rect/world/opacity emit
            d.Bounds = new RectF(0f, 0f, 40f, 40f);
            d.WorldTransform = Affine2D.Translation(100f, 50f);
            d.Opacity = 0.5f;
            d.Fill = ColorF.FromRgba(255, 255, 255);
            var dl = new DrawList();
            SceneRecorder.RecordDetached(new SceneStore(), dl, null, slab, RectF.Infinite);
            var dev = new HeadlessGpuDevice();
            dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200, 200), 1f, ColorF.Transparent));
            bool drew = false;
            foreach (var im in dev.LastImages)
                if (im.ImageId == 1 && Near(im.Rect.W, 40f, 0.5f) && Near(im.Opacity, 0.5f, 0.02f) && Near(im.Transform.Dx, 100f, 0.5f)) drew = true;
            bool retired = slab.Retire(s) == g && slab.Count == 0;   // completion-gate retirement frees the row + the group
            Check("CF.a connected-fly rebuild: RecordDetached draws a detached snapshot at its world transform + opacity; Retire frees the slot",
                drew && retired, $"images={dev.LastImages.Count} drew={drew} retired={retired}");
        }

        // SK.h smooth-resize: the region is BoundsAnimated + carries a SizeMode.Reflow transition, so a height-changing
        // swap eases the region's layout size (the host re-solves the parent each tick → surrounding content reflows,
        // not snaps). The reflow RUNTIME is the host-driven FLIP path (proven by ReflowChecks); here we prove the region
        // is enrolled AND that its spec produces a reflow track when the host applies the bounds diff.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var ld = Loadable<SkTrack[]>.Pending(Array.Empty<SkTrack>());
            recon.ReconcileRoot(
                Skel.Region(ld, SkRow, count: 4, content: ts => Flow.For<SkTrack>(() => ts, t => t.Number.ToString(), (t, i) => SkRow(t))),
                null);
            var region = scene.Root;
            bool boundsAnim = (scene.Flags(region) & NodeFlags.BoundsAnimated) != 0;
            bool reflowSpec = engine.TryGetTransition(region, out var spec) && (spec.Channels & TransitionChannels.Size) != 0 && spec.Size == SizeMode.Reflow;
            // Mimic the host FLIP "apply" for a shrinking swap (4 shimmer rows → a short branch): a Reflow size track runs.
            new FlexLayout(scene, fonts).Run(scene.Root);
            engine.AnimateBounds(region, new RectF(0, 0, 320, 200), new RectF(0, 0, 320, 60), spec);
            bool reflowRuns = engine.HasActive;
            Check("SK.h smooth-resize: region is BoundsAnimated + SizeMode.Reflow → a height-changing swap reflows surrounding content (not snap)",
                boundsAnim && reflowSpec && reflowRuns, $"boundsAnim={boundsAnim} reflowSpec={reflowSpec} reflowRuns={reflowRuns}");
        }

        // SK.i composes with VIRTUALIZATION: a region whose content is a 10k-row Virtual.List swaps to a WINDOWED list
        // (only a viewport-worth of rows realized, not 10k materialized) — so skeleton-loading scales to huge lists (the
        // Wavee track list). Shimmer = a viewport-fill of placeholder rows (NOT 10k); real = the virtualized list.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var count = Loadable<int>.Pending(0);
            var shimmerRows = new Element[8];
            for (int i = 0; i < shimmerRows.Length; i++) shimmerRows[i] = SkRow(null);
            recon.ReconcileRoot(new BoxEl
            {
                Width = 360f, Height = 400f, ClipToBounds = true,
                Children =
                [
                    Skel.Region(count,
                        shimmerSource: () => new BoxEl { Direction = 1, Gap = 8f, Children = shimmerRows },
                        content: n => Virtual.List(n, 44f, i => SkRow(new SkTrack(i + 1, "Track " + (i + 1), "0:00")), keyOf: i => i.ToString())),
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            int pendingNodes = CountNodes(scene, scene.Root);
            count.SetReady(10_000);
            recon.Runtime.Flush();
            new FlexLayout(scene, fonts).Run(scene.Root);
            int realNodes = CountNodes(scene, scene.Root);
            Check("SK.i composes with a 10k-row Virtual.List — swaps to a WINDOWED list (≪10k nodes realized, not 10k)",
                realNodes < 2000, $"pendingNodes={pendingNodes} realNodes={realNodes} (10k items)");
        }

        // SK.j content(seed) over a virtual viewport still derives a representative pending window. The deriver invokes
        // the real RenderItem source for at most eight rows; it neither collapses to one opaque bar nor materializes the
        // complete collection. This is the Home pending-state shape (heterogeneous measured virtual rows).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var count = Loadable<int>.Pending(6);
            recon.ReconcileRoot(
                Skel.Region(count, n => Virtual.List(n, 44f,
                    i => SkRow(new SkTrack(i + 1, "Seed " + i, "0:00")), keyOf: i => i.ToString()) with
                    { Width = 360f, Height = 300f }),
                null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            int pendingNodes = CountNodes(scene, scene.Root);
            bool representative = pendingNodes >= 20 && CountText(scene, scene.Root) == 0
                && Near(scene.Bounds(scene.Root).H, 300f, 0.5f);
            Check("SK.j content(seed) derives a bounded virtual-list pending window (not one blank opaque leaf)",
                representative, $"nodes={pendingNodes} text={CountText(scene, scene.Root)} h={scene.Bounds(scene.Root).H:0}");
        }

        // SK.k is the end-to-end regression for Wavee Home being blank until Alt+Tab. Pending must occupy the viewport
        // immediately, and a worker-style HostDispatch.Post of Ready must mount, realize, lay out and record real virtual
        // rows in that SAME next frame. No focus, resize, extra signal write or second frame is allowed to unstick it.
        {
            var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("skel-virtual-host", new Size2(420, 300), 1f));
            var probe = new SkeletonVirtualHostProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);

            host.RunFrame();
            bool pendingVisible = CountNodes(host.Scene, host.Scene.Root) >= 20
                && CountText(host.Scene, host.Scene.Root) == 0
                && host.Scene.Bounds(host.Scene.Root).H >= 299f;

            host.Post(() => probe.Count.SetReady(6));
            var readyFrame = host.RunFrame();
            bool readyVisible = CountText(host.Scene, host.Scene.Root) > 0
                && host.Scene.Bounds(host.Scene.Root).H >= 299f;
            var viewport = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(viewport, out var scroll);
            var row0 = host.Scene.FirstChild(scroll.ContentNode);
            var row1 = row0.IsNull ? NodeHandle.Null : host.Scene.NextSibling(row0);
            bool rowsOwnStagger = !row0.IsNull && !row1.IsNull
                && host.Animation.HasTracks(row0) && host.Animation.HasTracks(row1)
                && !host.Animation.HasTracks(scroll.ContentNode);

            Check("SK.k host Post Pending→Ready: virtual skeleton is visible immediately and real rows appear next frame (no focus/resize)",
                pendingVisible && readyVisible && readyFrame.Rendered && rowsOwnStagger,
                $"pending={pendingVisible} ready={readyVisible} text={CountText(host.Scene, host.Scene.Root)} rendered={readyFrame.Rendered} rowTracks={rowsOwnStagger}");
        }
    }

    static void ProjectionChecks(StringTable strings)
    {
        // 23a — BoxEl.Animate wires the BoundsAnimated flag + the per-node transition side-table (Phase 0 plumbing).
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            recon.ReconcileRoot(new BoxEl { Animate = LayoutTransition.Slide, Width = 50, Height = 20 }, null);
            var root = scene.Root;
            bool flagSet = (scene.Flags(root) & NodeFlags.BoundsAnimated) != 0;
            bool roundTrip = engine.TryGetTransition(root, out var spec) && spec.Channels == TransitionChannels.Position;
            // dropping Animate clears both
            recon.ReconcileRoot(new BoxEl { Width = 50, Height = 20 }, new BoxEl { Animate = LayoutTransition.Slide, Width = 50, Height = 20 });
            bool cleared = (scene.Flags(root) & NodeFlags.BoundsAnimated) == 0 && !engine.TryGetTransition(root, out _);
            Check("23a. BoxEl.Animate ↔ BoundsAnimated + transition side-table (set/clear)", flagSet && roundTrip && cleared);
        }

        // 23b — a moved node FLIPs: the presented offset springs old→new monotonically and settles, never relaying out.
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 200, 50, 20);   // final laid-out position
            var engine = new AnimEngine(scene);
            scene.Flags(n) &= ~(NodeFlags.TransformDirty | NodeFlags.LayoutDirty);
            var crit = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(0.18f, 1.0f));  // critically damped → no overshoot
            engine.AnimateBounds(n, new RectF(0, 100, 50, 20), new RectF(0, 200, 50, 20), crit);  // was at y=100, now laid out at y=200

            bool monotonic = true; float prev = -1e9f; int settledAt = -1;
            for (int i = 0; i < 80 && settledAt < 0; i++)
            {
                engine.Tick(16f);
                float dy = scene.Paint(n).LocalTransform.Dy;
                if (i > 0 && dy < prev - 0.6f) monotonic = false;   // offset climbs -100 → 0
                prev = dy;
                if (!engine.HasActive) settledAt = i;
            }
            var f = scene.Flags(n);
            bool noRelayout = (f & NodeFlags.LayoutDirty) == 0 && (f & NodeFlags.TransformDirty) != 0;
            bool settledZero = settledAt >= 0 && MathF.Abs(scene.Paint(n).LocalTransform.Dy) < 0.5f;
            Check("23b. projection FLIPs a moved node (offset springs → 0, settles, no relayout)",
                monotonic && settledZero && noRelayout, $"settled@{settledAt}");
        }

        // 23c — interruption is velocity-continuous: re-projecting mid-flight must NOT snap the presented position
        // (the old code overwrote the transform, losing the in-flight offset → the visible jump).
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 200, 50, 20);
            var engine = new AnimEngine(scene);
            // a slow spring keeps per-tick motion tiny (~3px), so the continuity test isolates the reframe (≈3px) from
            // the old overwrite bug (which loses the in-flight offset → a ~90px snap).
            var spring = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(1.0f, 1.0f));
            engine.AnimateBounds(n, new RectF(0, 100, 50, 20), new RectF(0, 200, 50, 20), spring);
            for (int i = 0; i < 5; i++) engine.Tick(16f);
            float d = scene.Paint(n).LocalTransform.Dy;     // in-flight offset (large for a slow spring)
            float presentedBefore = nb.Y + d;               // its on-screen Y this instant
            // it moves again to y=300; layout snaps Bounds, the transform is unchanged → toAbs = 300 + d
            nb = new RectF(0, 300, 50, 20);
            engine.AnimateBounds(n, new RectF(0, presentedBefore, 50, 20), new RectF(0, 300f + d, 50, 20), spring);
            engine.Tick(16f);
            float presentedAfter = 300f + scene.Paint(n).LocalTransform.Dy;
            bool continuous = MathF.Abs(presentedAfter - presentedBefore) < 15f;
            Check("23c. projection reframes on interruption (velocity-continuous, no jump)",
                continuous, $"presented {presentedBefore:0.0}→{presentedAfter:0.0}");
        }

        // 23d — Reveal (size): the presented extent springs old→new with NO relayout (model Bounds stay final), and
        // resets to NaN on settle so the recorder falls back to the layout size. This replaces the deleted Width channel.
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 0, 48, 600);   // final (collapsed) model width
            scene.Flags(n) &= ~NodeFlags.LayoutDirty;
            var engine = new AnimEngine(scene);
            var reveal = LayoutTransition.BoundsT(SizeMode.Reveal) with { Dynamics = TransitionDynamics.Spring(0.18f, 1.0f) };
            engine.AnimateBounds(n, new RectF(0, 0, 320, 600), new RectF(0, 0, 48, 600), reveal);  // collapsing 320 → 48
            engine.Tick(16f);
            float firstW = scene.Paint(n).PresentedW;                    // presented starts near 320 (not snapped to 48)
            bool startedWide = firstW > 200f;
            bool noRelayout = (scene.Flags(n) & NodeFlags.LayoutDirty) == 0;
            bool modelFinal = Near(scene.Bounds(n).W, 48f);              // only the presented extent animates
            int settledAt = -1;
            for (int i = 0; i < 90 && settledAt < 0; i++) { engine.Tick(16f); if (!engine.HasActive) settledAt = i; }
            bool resetNaN = float.IsNaN(scene.Paint(n).PresentedW);      // on settle, falls back to the (final) layout size
            Check("23d. Reveal springs presented size (no relayout, model final, resets on settle)",
                startedWide && noRelayout && modelFinal && resetNaN && settledAt >= 0, $"firstW={firstW:0} settled@{settledAt}");
        }
    }

    static void EnterExitChecks(StringTable strings)
    {
        // 23e — exit orphan: removing the child keeps it live + drawing until its fade settles, then reclaims (gen bump).
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var exit = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Spring(0.12f, 1f),
                Exit: new EnterExit(Opacity: 0f, Active: true));
            Element Tree(bool present) => new BoxEl
            {
                Width = 100, Height = 100,
                Children = present ? [new BoxEl { Key = "x", Width = 50, Height = 20, Animate = exit }] : [],
            };
            var old = Tree(true);
            recon.ReconcileRoot(old, null);
            new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
            var child = Child(scene, scene.Root, 0);
            bool mountedLive = scene.IsLive(child);

            recon.ReconcileRoot(Tree(false), old);                       // remove → orphan + seed exit
            bool orphaned = scene.IsOrphan(child) && scene.IsLive(child) && scene.OrphanCount == 1;

            int settledAt = -1;
            for (int i = 0; i < 90 && settledAt < 0; i++)
            {
                engine.Tick(16f);
                for (int k = scene.OrphanCount - 1; k >= 0; k--)        // host's ReclaimSettledOrphans
                { var o = scene.OrphanAt(k, out _, out _); if (!engine.HasTracks(o)) scene.ReclaimOrphan(o); }
                if (scene.OrphanCount == 0) settledAt = i;
            }
            bool reclaimed = settledAt >= 0 && !scene.IsLive(child);     // deferred free → handle dead
            Check("23e. exit orphan stays live while fading, then reclaims (deferred free)",
                mountedLive && orphaned && reclaimed, $"settled@{settledAt}");
        }

        // 23e2: an exit inside a rounded flyout + rectangular scroller must replay at its former parent, not in the
        // global un-clipped band. Outgoing paints first (behind the incoming row), under both active ancestor clips.
        // Hard-removing the containing surface then cascade-reclaims the still-running exit.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var fonts = new HeadlessFontSystem(strings);
            var exit = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(300f, Easing.Linear),
                Exit: new EnterExit(Dy: -4f, Opacity: 0f, Active: true));
            ColorF outgoingColor = ColorF.FromRgba(211, 47, 47);
            ColorF incomingColor = ColorF.FromRgba(33, 150, 243);

            Element Tree(bool incoming, bool surface = true) => new BoxEl
            {
                Width = 180, Height = 140,
                Children = surface
                    ?
                    [
                        new BoxEl
                        {
                            Width = 120, Height = 80, ClipToBounds = true, Corners = CornerRadius4.All(8f),
                            Children =
                            [
                                new BoxEl
                                {
                                    Width = 100, Height = 40, Margin = Edges4.All(10f), ClipToBounds = true,
                                    Children =
                                    [
                                        new BoxEl
                                        {
                                            Key = incoming ? "incoming" : "outgoing",
                                            Width = 100, Height = 80,
                                            Fill = incoming ? incomingColor : outgoingColor,
                                            Animate = exit,
                                        },
                                    ],
                                },
                            ],
                        },
                    ]
                    : [],
            };

            var old = Tree(incoming: false);
            recon.ReconcileRoot(old, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var outgoing = Child(scene, Child(scene, Child(scene, scene.Root, 0), 0), 0);

            var next = Tree(incoming: true);
            recon.ReconcileRoot(next, old);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var dl = new DrawList();
            SceneRecorder.Record(scene, dl);
            var outCmd = FindFillCommand(dl, outgoingColor);
            var inCmd = FindFillCommand(dl, incomingColor);
            bool contained = scene.IsOrphan(outgoing) && outCmd.Order >= 0 && inCmd.Order >= 0
                             && outCmd.Order < inCmd.Order && outCmd.ClipDepth == 2 && inCmd.ClipDepth == 2;

            var empty = Tree(incoming: true, surface: false);
            recon.ReconcileRoot(empty, next);
            engine.Tick(0f);   // freed-handle rows self-prune on the animation slab's next gen-check
            bool cascaded = scene.OrphanCount == 0 && !scene.IsLive(outgoing) && !engine.HasTracks(outgoing);
            Check("23e2. exits replay inside former parent clip/order and cascade-reclaim when that parent is removed",
                contained && cascaded,
                $"out={outCmd.Order}@clip{outCmd.ClipDepth} in={inCmd.Order}@clip{inCmd.ClipDepth} cascaded={cascaded}");
        }

        // 23f — enter: a mounted node with Enter.Active starts at the enter terminal (opacity 0) and springs to 1.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var enter = new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Spring(0.15f, 1f),
                Enter: new EnterExit(Opacity: 0f, Active: true));
            recon.ReconcileRoot(new BoxEl { Width = 100, Height = 100, Children = [new BoxEl { Width = 50, Height = 20, Animate = enter }] }, null);
            new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
            var child = Child(scene, scene.Root, 0);
            engine.Tick(16f);
            float a1 = scene.Paint(child).Opacity;                      // entering: near 0
            for (int i = 0; i < 90; i++) engine.Tick(16f);
            float a2 = scene.Paint(child).Opacity;                      // settled to 1
            Check("23f. enter animates a mounted node from the enter terminal (opacity 0 → 1)",
                a1 < 0.5f && Near(a2, 1f), $"opacity {a1:0.00}→{a2:0.00}");
        }

        // (The CheckBox checkmark draw-on is a component reveal hook → it needs the host's layout-effect drain, so it is
        //  exercised through the real AppHost in check 66b, not the bare reconciler here.)

        // 23h — WinUI RadioButton motion: CheckGlyph is 12px at rest, 14px on PointerOver, 10px on Pressed; unchecked
        // Pressed uses a separate PressedCheckGlyph that appears while held and grows from 4px toward 10px.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            recon.ReconcileRoot(RadioButton.Create("x", true), null);   // root = row; ring = child0; dot = ring.child0
            new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
            var ring = Child(scene, scene.Root, 0);
            var dot = Child(scene, ring, 0);
            bool sized = Near(scene.Bounds(dot).W, 12f, 0.01f) && Near(scene.Bounds(dot).H, 12f, 0.01f);
            bool interactive = scene.TryGetInteract(dot, out var ia)
                && Near(ia.HoverScale, 14f / 12f, 0.001f)
                && Near(ia.PressScale, 10f / 12f, 0.001f)
                && Near(ia.HoverDurationMs, 250f, 0.01f)
                && Near(ia.PressDurationMs, 250f, 0.01f);
            bool instantChecked = scene.Paint(dot).LocalTransform.IsIdentity;
            Check("23h. RadioButton wires WinUI CheckGlyph size states (12 rest, 14 hover, 10 pressed)",
                sized && interactive && instantChecked,
                $"size={scene.Bounds(dot).W:0.#} hoverScale={ia.HoverScale:0.###} pressScale={ia.PressScale:0.###}");

            var unselected = new SceneStore();
            var unselectedRecon = new TreeReconciler(unselected, strings);
            unselectedRecon.ReconcileRoot(RadioButton.Create("x", false), null);
            new FlexLayout(unselected, new HeadlessFontSystem(strings)).Run(unselected.Root);
            var unselectedRing = Child(unselected, unselected.Root, 0);
            var pressedGlyph = Child(unselected, unselectedRing, 0);
            bool hiddenAtRest = Near(unselected.Bounds(pressedGlyph).W, 4f, 0.01f)
                && Near(unselected.Paint(pressedGlyph).Opacity, 0f, 0.001f)
                && Near(unselected.Paint(pressedGlyph).PressedOpacity, 1f, 0.001f);
            bool growsToPressedSize = unselected.TryGetInteract(pressedGlyph, out var pia)
                && Near(pia.PressScale, 10f / 4f, 0.001f)
                && Near(pia.PressDurationMs, 167f, 0.01f);

            var iax = new AnimEngine(unselected);   // hover/press now engine-driven (InteractionAnimator subsumed)
            iax.SetPress(unselected.Root, true);
            iax.Tick(16f);
            var dl = new DrawList();
            SceneRecorder.Record(unselected, dl);
            var dev = new HeadlessGpuDevice();
            dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(120, 80), 1f, ColorF.Transparent));
            bool drewPressedGlyph = false;
            foreach (var rect in dev.LastRects)
                if (Near(rect.Rect.W, 4f, 0.01f) && rect.Opacity > 0.08f && rect.Transform.M11 > 1.08f)
                    drewPressedGlyph = true;
            Check("23h2. RadioButton unchecked press draws PressedCheckGlyph (4px hidden → visible/growing toward 10px)",
                hiddenAtRest && growsToPressedSize && drewPressedGlyph,
                $"rest={hiddenAtRest} scale={pia.PressScale:0.###} drew={drewPressedGlyph}");
        }

        // 23i — visual-state RAMP wiring (the StateBrush model, not a 12-state matrix): an unchecked CheckBox wires the
        // full interaction ladder into the box's scene columns. Crucially the PRESSED stroke DIMS to
        // ControlStrongStrokeColorDisabled (the exact WinUI press feedback) — provable here without pixels, the empirical
        // counterpart to a screenshot. The recorder eases BorderColor→PressedBorderColor on PressT (covered by check 58).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            recon.ReconcileRoot(CheckBox.Create("x", new Signal<CheckState>(CheckState.Unchecked)), null);
            var cbRow = FindRole(scene, scene.Root, AutomationRole.CheckBox);   // CheckBox is a component now → find its row
            ref var p = ref scene.Paint(Child(scene, cbRow, 0));   // the 20px box (child 0 of the CheckBox row)
            bool restRing = MathF.Abs(p.BorderColor.A - Tok.StrokeControlStrongDefault.A) < 0.02f;
            bool pressDims = MathF.Abs(p.PressedBorderColor.A - Tok.StrokeControlStrongDisabled.A) < 0.02f && p.PressedBorderColor.A < p.BorderColor.A;
            bool hoverFill = MathF.Abs(p.HoverFill.A - Tok.FillControlAltTertiary.A) < 0.02f;
            bool pressFill = MathF.Abs(p.PressedFill.A - Tok.FillControlAltQuaternary.A) < 0.02f;
            Check("23i. CheckBox wires the interaction ramp (pressed stroke dims to StrongDisabled, no 12-state matrix)",
                restRing && pressDims && hoverFill && pressFill,
                $"ring.A={p.BorderColor.A:0.00}→press {p.PressedBorderColor.A:0.00}; fill hover.A={p.HoverFill.A:0.00} press.A={p.PressedFill.A:0.00}");
        }

    }

    static float RevealingW(SceneStore s, NodeHandle n)
    {
        float best = float.NaN;
        float w = s.Paint(n).PresentedW;
        if (!float.IsNaN(w)) best = w;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            float cw = RevealingW(s, c);
            if (!float.IsNaN(cw) && (float.IsNaN(best) || cw < best)) best = cw;
        }
        return best;
    }


    static void SizeModeChecks(StringTable strings)
    {
        // 23g — ScaleCorrect: a grown node starts scaled-down and springs its scale to 1, never relaying out.
        {
            var scene = new SceneStore();
            var n = scene.CreateNode(1); scene.Root = n;
            ref RectF nb = ref scene.Bounds(n); nb = new RectF(0, 0, 200, 100);
            scene.Flags(n) &= ~NodeFlags.LayoutDirty;
            var engine = new AnimEngine(scene);
            var sc = LayoutTransition.BoundsT(SizeMode.ScaleCorrect) with { Dynamics = TransitionDynamics.Spring(0.2f, 1f) };
            engine.AnimateBounds(n, new RectF(0, 0, 100, 100), new RectF(0, 0, 200, 100), sc);  // width 100→200 ⇒ scaleX 0.5→1
            engine.Tick(16f);
            float m11a = scene.Paint(n).LocalTransform.M11;
            bool noRelayout = (scene.Flags(n) & NodeFlags.LayoutDirty) == 0;
            int settledAt = -1;
            for (int i = 0; i < 90 && settledAt < 0; i++) { engine.Tick(16f); if (!engine.HasActive) settledAt = i; }
            float m11b = scene.Paint(n).LocalTransform.M11;
            Check("23g. ScaleCorrect springs the node scale → 1 (compositor-only, no relayout)",
                m11a > 0.3f && m11a < 0.7f && Near(m11b, 1f, 0.02f) && noRelayout && settledAt >= 0, $"M11 {m11a:0.00}→{m11b:0.00}");
        }

        // 23h — Relayout: the node's MODEL width interpolates via scoped RunSubtree (so its content re-solves live).
        {
            var scene = new SceneStore();
            var fonts = new HeadlessFontSystem(strings);
            var engine = new AnimEngine(scene);
            var layout = new FlexLayout(scene, fonts);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var rel = LayoutTransition.BoundsT(SizeMode.Relayout) with { Dynamics = TransitionDynamics.Spring(0.2f, 1f) };
            recon.ReconcileRoot(new BoxEl { Width = 100, Height = 200, Animate = rel,
                Children = [new TextEl("the quick brown fox jumps over the lazy dog") { Wrap = TextWrap.Wrap }] }, null);
            layout.Run(scene.Root, new Size2(400, 200));
            var panel = scene.Root;
            engine.AnimateBounds(panel, new RectF(0, 0, 300, 200), new RectF(0, 0, 100, 200), rel);   // 300 → 100
            bool relayouting = (scene.Flags(panel) & NodeFlags.Relayouting) != 0;
            float midW = -1f; int runs = 0;
            for (int i = 0; i < 8; i++)
            {
                engine.Tick(16f);
                runs += engine.IncrementalRoots.Count;                // exactly one root re-solves per tick (scoped, not full-tree)
                foreach (var r in engine.IncrementalRoots)
                {
                    ref LayoutInput li = ref scene.Layout(r);
                    ref NodePaint pp = ref scene.Paint(r);
                    if (!float.IsNaN(pp.PresentedW)) li.Width = pp.PresentedW;
                    layout.RunSubtree(r);
                }
                engine.IncrementalRoots.Clear();
                if (i == 1) midW = scene.Bounds(panel).W;
            }
            bool interpolated = midW > 105f && midW < 300f;          // model width genuinely moved through the range
            Check("23h. Relayout re-solves only the subtree at the interpolated size (live reflow)",
                relayouting && interpolated && runs >= 2, $"midW={midW:0} runs={runs}");
        }
    }

    static void AnimRegressionChecks(StringTable strings)
    {
        // 23s — spring retarget continuity through the live AppHost path (UseLayoutEffect → Context.Anim.Spring).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("springlab", new Size2(320, 120), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new SpringLabProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var toggle = Child(host.Scene, host.Scene.Root, 0);

            ClickNode(host, window, toggle);                       // 0 → 210
            for (int i = 0; i < 8; i++) host.RunFrame();           // mid-flight
            float before = host.Scene.Paint(root.Dot).LocalTransform.Dx;
            ClickNode(host, window, toggle);                       // retarget mid-flight → 0
            float atClick = host.Scene.Paint(root.Dot).LocalTransform.Dx;
            host.RunFrame();
            float after = host.Scene.Paint(root.Dot).LocalTransform.Dx;
            bool continuous = MathF.Abs(atClick - before) < 25f && MathF.Abs(after - atClick) < 25f;   // no snap to an endpoint
            bool carried = after > 5f;                             // velocity carry: still well away from 0 right after
            for (int i = 0; i < 90; i++) host.RunFrame();
            bool settled = MathF.Abs(host.Scene.Paint(root.Dot).LocalTransform.Dx) < 0.5f && !host.Animation.HasTracks(root.Dot);
            Check("23s. spring retarget mid-flight keeps position+velocity through the component-effect path (no snap)",
                before > 30f && continuous && carried && settled,
                $"before={before:0.0} atClick={atClick:0.0} after={after:0.0} settled={settled}");
        }

        // 23w — alpha-weighted (premultiplied) linear-light lerp: a translucent white-tinted card fill cross-fading
        // to an OPAQUE DARK solid must stay dark mid-flight. The straight per-channel lerp passed through bright
        // half-transparent grey (~0.74 sRGB) — the sticky-header "white flash". Same-alpha pairs are bit-identical
        // to the straight linear-light lerp (every pre-existing mid-color assertion stays valid).
        {
            static float S2L(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(MathF.Max(c, 0f), 1f / 2.4f) - 0.055f;
            var cardWhite5 = new ColorF(1f, 1f, 1f, 0.051f);     // CardBackgroundFillColorDefault (dark theme): white @ 5%
            var solidDark = ColorF.FromRgba(0x20, 0x20, 0x20);   // SolidBackgroundFillColorBase: opaque dark
            var mid = ColorF.LerpLinear(cardWhite5, solidDark, 0.5f);
            bool staysDark = mid.R < 0.35f && mid.G < 0.35f && mid.B < 0.35f && Near(mid.A, 0.5255f, 0.01f);
            var sa = new ColorF(0.2f, 0.4f, 0.6f, 0.8f);
            var sb = new ColorF(0.6f, 0.2f, 0.4f, 0.8f);
            var sm = ColorF.LerpLinear(sa, sb, 0.5f);
            bool sameAlphaIdentical =
                Near(sm.R, L2S((S2L(sa.R) + S2L(sb.R)) * 0.5f), 0.002f) &&
                Near(sm.G, L2S((S2L(sa.G) + S2L(sb.G)) * 0.5f), 0.002f) &&
                Near(sm.B, L2S((S2L(sa.B) + S2L(sb.B)) * 0.5f), 0.002f) && Near(sm.A, 0.8f, 0.002f);
            Check("23w. LerpLinear is alpha-weighted: translucent-white → opaque-dark stays dark mid-flight; same-alpha pairs unchanged",
                staysDark && sameAlphaIdentical,
                $"mid=({mid.R:0.00},{mid.G:0.00},{mid.B:0.00},{mid.A:0.00}) sameAlpha={sameAlphaIdentical}");
        }

        // 23u — CSS position:sticky (a ScrollBinds PinTop op): the header scrolls normally, PINS at the viewport top while
        // its parent card is in view (hit-test follows — AbsoluteRect includes the pin transform), CLAMPS at the
        // card's end (never escapes its containing block), releases on scroll-back, and fires OnFlag per transition.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sticky", new Size2(320, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            int pinEvents = 0; bool lastPin = false; NodeHandle headerN = NodeHandle.Null;
            var root = new W0fStaticProbe
            {
                Build = () => ScrollView(new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new BoxEl { Height = 100f },                              // lead-in
                        new BoxEl                                                  // the card (containing block)
                        {
                            Direction = 1,
                            Children =
                            [
                                new BoxEl { Height = 40f, ScrollBinds = [ new() { PinTop = 0f, OnFlag = p => { pinEvents++; lastPin = p; } } ], OnRealized = h => headerN = h },
                                new BoxEl { Height = 400f },                       // card content
                            ],
                        },
                        new BoxEl { Height = 600f },                               // after the card
                    ],
                }),
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var s = host.Scene;
            NodeHandle FindScrollable(NodeHandle n)
            {
                if (n.IsNull) return NodeHandle.Null;
                if (s.HasScroll(n)) return n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
                {
                    var r = FindScrollable(c);
                    if (!r.IsNull) return r;
                }
                return NodeHandle.Null;
            }
            var vp = FindScrollable(s.Root);                       // the ScrollView viewport
            var content = s.ScrollRef(vp).ContentNode;
            // headerN (the sticky node) is captured via OnRealized at mount (declared above).
            float vpTop = s.AbsoluteRect(vp).Y;
            float restY = s.AbsoluteRect(headerN).Y;

            void ScrollTo(float y)
            {
                ref ScrollState st = ref s.ScrollRef(vp);
                st.OffsetY = y; st.TargetY = y;
                s.Paint(content).LocalTransform = Affine2D.Translation(0f, -y);
                s.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                // Wake the frame loop like real input would (a wheel scroll sets frameNeeded via dispatch; a raw
                // ScrollRef write does not) — the sticky pass runs in the full frame pipeline.
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
                host.RunFrame();
            }

            ScrollTo(250f);   // header's natural Y (100) is far above the viewport top → pinned at the top
            bool pinnedNow = (s.Flags(headerN) & NodeFlags.StickyPinned) != 0;
            float pinnedY = s.AbsoluteRect(headerN).Y;
            // Card spans content 100..540; the header (40h) can pin until content-y 500 (shift limit 400). At offset
            // 520 the clamp holds it at content-y 500 → viewport −20: the card's end pushes it out, CSS-exactly.
            ScrollTo(520f);
            float clampedY = s.AbsoluteRect(headerN).Y;
            bool stillPinned = (s.Flags(headerN) & NodeFlags.StickyPinned) != 0;
            ScrollTo(0f);     // released, back at its natural slot
            bool releasedFlag = (s.Flags(headerN) & NodeFlags.StickyPinned) == 0;
            float releasedY = s.AbsoluteRect(headerN).Y;
            Check("23u. position:sticky — pins at viewport top, clamps at the card's end, releases, OnPinned fires per transition",
                pinnedNow && Near(pinnedY, vpTop, 0.5f)
                && stillPinned && Near(clampedY, vpTop - 20f, 0.5f)
                && releasedFlag && Near(releasedY, restY, 0.5f)
                && pinEvents == 2 && !lastPin,
                $"restY={restY:0} pinnedY={pinnedY:0} (vpTop={vpTop:0}) clampedY={clampedY:0} releasedY={releasedY:0} pinEvents={pinEvents} lastPin={lastPin}");
        }

        // 23u3 — sticky clip-top (ScrollBindDsl.ClipTopAtViewport, the paint dual of the 23u pin): the body's
        // ClipRect.top rides the viewport-anchored line (viewport top + inset) 1:1 with the offset while engaged,
        // releases back to the Infinite sentinel when the line sits above the body, and OnFlag fires per edge —
        // the mechanism that keeps the page backdrop (not the cards) behind a pinned section header.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sticky-clip", new Size2(320, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            int clipEvents = 0; bool lastClip = false; NodeHandle bodyN = NodeHandle.Null;
            var root = new W0fStaticProbe
            {
                Build = () => ScrollView(new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new BoxEl { Height = 100f },                              // lead-in
                        new BoxEl                                                  // the section (containing block)
                        {
                            Direction = 1,
                            Children =
                            [
                                new BoxEl { Height = 40f, ScrollBinds = [ new() { PinTop = 0f } ] },   // the pinned header
                                new BoxEl                                                              // the section body
                                {
                                    Height = 400f,
                                    ScrollBinds = [ new() { ClipTopAtViewport = 40f, OnFlag = c => { clipEvents++; lastClip = c; } } ],
                                    OnRealized = h => bodyN = h,
                                },
                            ],
                        },
                        new BoxEl { Height = 600f },                               // after the section
                    ],
                }),
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var s = host.Scene;
            NodeHandle FindScrollable(NodeHandle n)
            {
                if (n.IsNull) return NodeHandle.Null;
                if (s.HasScroll(n)) return n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
                {
                    var r = FindScrollable(c);
                    if (!r.IsNull) return r;
                }
                return NodeHandle.Null;
            }
            var vp = FindScrollable(s.Root);
            var content = s.ScrollRef(vp).ContentNode;
            void ScrollTo(float y)
            {
                ref ScrollState st = ref s.ScrollRef(vp);
                st.OffsetY = y; st.TargetY = y;
                s.Paint(content).LocalTransform = Affine2D.Translation(0f, -y);
                s.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
                host.RunFrame();
            }

            bool restReleased = s.Paint(bodyN).ClipRect.IsInfinite;   // at rest: line far above the body → no clip
            // Body spans content 140..540. At offset 250 the line (viewport top + 40) sits at content-y 290 →
            // body-local clip top = 250 + 40 − 140 = 150; one more scroll px moves it exactly one px (1:1).
            ScrollTo(250f);
            float clipA = s.Paint(bodyN).ClipRect.Y;
            ScrollTo(251f);
            float clipB = s.Paint(bodyN).ClipRect.Y;
            ScrollTo(90f);    // line at content-y 130, above the body top (140) → released, not a stale 0-clip
            bool releasedMid = s.Paint(bodyN).ClipRect.IsInfinite;
            Check("23u3. sticky clip-top — body ClipRect.top rides the viewport line 1:1, releases above the body, OnFlag per edge",
                restReleased && Near(clipA, 150f, 0.5f) && Near(clipB - clipA, 1f, 0.1f)
                && releasedMid && clipEvents == 2 && !lastClip,
                $"rest={restReleased} clipA={clipA:0.#} clipB={clipB:0.#} releasedMid={releasedMid} clipEvents={clipEvents} lastClip={lastClip}");
        }

        // 23u2 — trailing-anchored presented height: a pinned hero collapses without relayout, its bottom-authored
        // child + edge stay attached to the live reveal edge, the following content meets that edge through normal
        // scrolling, and hit-testing follows the child-group shift.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scroll-collapse-trailing", new Size2(320, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            NodeHandle heroN = NodeHandle.Null, edgeN = NodeHandle.Null, bodyN = NodeHandle.Null;
            int edgeClicks = 0, bodyClicks = 0;
            var root = new W0fStaticProbe
            {
                Build = () => ScrollView(new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = 200f, Direction = 1, Justify = FlexJustify.End, ClipToBounds = true,
                            ScrollBinds =
                            [
                                new() { PinTop = 0f },
                                new()
                                {
                                    From = ScrollChannel.Offset, To = BindSink.PresentedHTrailing,
                                    Range = ScrollRange.Px(0f, 200f), OutStart = 200f, OutEnd = 0f
                                },
                            ],
                            OnRealized = h => heroN = h,
                            Children =
                            [
                                new BoxEl { Height = 30f, OnClick = () => edgeClicks++, OnRealized = h => edgeN = h },
                            ],
                        },
                        new BoxEl { Height = 600f, OnClick = () => bodyClicks++, OnRealized = h => bodyN = h },
                    ],
                }),
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var s = host.Scene;
            NodeHandle FindScrollable(NodeHandle n)
            {
                if (n.IsNull) return NodeHandle.Null;
                if (s.HasScroll(n)) return n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
                {
                    var r = FindScrollable(c);
                    if (!r.IsNull) return r;
                }
                return NodeHandle.Null;
            }
            var vp = FindScrollable(s.Root);
            var content = s.ScrollRef(vp).ContentNode;
            ref ScrollState st = ref s.ScrollRef(vp);
            st.OffsetY = 80f; st.TargetY = 80f;
            s.Paint(content).LocalTransform = Affine2D.Translation(0f, -80f);
            s.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            ScrollBindEval.ApplyContinuous(s, vp, ref st);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
            host.RunFrame();

            float vpTop = s.AbsoluteRect(vp).Y;
            float ph = s.Paint(heroN).PresentedH;
            float shift = s.Paint(heroN).ChildShiftY;
            RectF edge = s.AbsoluteRect(edgeN);
            RectF body = s.AbsoluteRect(bodyN);
            var edgePoint = new Point2(edge.X + 5f, edge.Y + edge.H * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, edgePoint, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, edgePoint, 0, 0));
            host.RunFrame();
            var bodyPoint = new Point2(body.X + 5f, body.Y + 10f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, bodyPoint, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, bodyPoint, 0, 0));
            host.RunFrame();

            bool geometry = Near(ph, 120f, 0.5f) && Near(shift, -80f, 0.5f)
                && Near(edge.Bottom, vpTop + ph, 0.75f) && Near(body.Y, vpTop + ph, 0.75f);
            Check("23u2. trailing PresentedH collapse keeps child/content on one live edge and hit-testing follows",
                geometry && edgeClicks == 1 && bodyClicks == 1,
                $"ph={ph:0.0} shift={shift:0.0} edgeBottom={edge.Bottom:0.0} bodyY={body.Y:0.0} vpTop={vpTop:0.0} edgeClicks={edgeClicks} bodyClicks={bodyClicks}");
        }

        // 23v/23w — scroll-position restoration (ScrollKey): a revisit seeds the saved offset BEFORE the first realize
        // (no scroll-to-top flash, even cold), a never-seen key starts at the top, and a reused viewport saves/restores
        // per content identity. The "1-2 frames at the top then a jump" antipattern is structurally impossible here.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scrollrestore", new Size2(360, 260), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new ScrollRestoreProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var s = host.Scene;
            NodeHandle Find(NodeHandle n)
            {
                if (n.IsNull) return NodeHandle.Null;
                if (s.HasScroll(n)) return n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) { var r = Find(c); if (!r.IsNull) return r; }
                return NodeHandle.Null;
            }
            // Scroll "A" to row 20 (offset 400), then unmount → the offset is saved under its ScrollKey.
            { ref ScrollState st = ref s.ScrollRef(Find(s.Root)); st.OffsetY = 400f; st.TargetY = 400f; }
            host.RunFrame();
            root.Mounted.Value = false; host.RunFrame();          // unmount → SaveScroll caches "A"=400
            root.Mounted.Value = true;  host.RunFrame();          // cold remount → seed BEFORE the first realize
            ref ScrollState ra = ref s.ScrollRef(Find(s.Root));
            float restoredOffset = ra.OffsetY; int restoredFirst = ra.FirstRealized; bool noPending = !ra.RestorePending;
            Check("scroll-restore.cold-seed: a cold remount seeds the saved offset on the FIRST realized window (no scroll-to-top flash)",
                Near(restoredOffset, 400f, 1f) && restoredFirst > 0 && noPending,
                $"offset={restoredOffset:0} firstRealized={restoredFirst} pending={!noPending}");

            // Switch the ScrollKey on the reused viewport: new content starts at the top; the old content's offset is saved.
            root.Key.Value = "B"; host.RunFrame();
            float bTop = s.ScrollRef(Find(s.Root)).OffsetY;
            { ref ScrollState stb = ref s.ScrollRef(Find(s.Root)); stb.OffsetY = 600f; stb.TargetY = 600f; }
            host.RunFrame();
            root.Key.Value = "A"; host.RunFrame();                // back to A → restore 400
            float aBack = s.ScrollRef(Find(s.Root)).OffsetY;
            Check("scroll-restore.key-isolation: a different ScrollKey starts at the top; returning restores the prior content's offset",
                Near(bTop, 0f, 1f) && Near(aBack, 400f, 1f),
                $"newKeyTop={bTop:0} restoredPrev={aBack:0}");
        }

        // virtual-collection — the source-agnostic data-windowing primitive (paged remote list of known total): the total is
        // learned from page 0, pages fill on demand, repeat requests dedup, a seeded prefix refetches nothing, and the hot
        // path (indexing + an already-satisfied EnsureRange) allocates ZERO. The artist-discography virtualization rides this.
        {
            var data = new int[1000];
            for (int i = 0; i < data.Length; i++) data[i] = i * 10;
            int fetches = 0;
            var vc = new VirtualCollection<int>((off, cnt, ct) =>
            {
                fetches++;
                return new ValueTask<PageResult<int>>(new PageResult<int>(data.Length, data.AsMemory(off, Math.Min(cnt, data.Length - off))));
            }, pageSize: 50);

            bool before = vc.Count == -1 && !vc.IsLoaded(0);
            vc.EnsureRange(0, 30);                                   // page 0 → learns total + fills
            bool firstPage = vc.Count == 1000 && vc.IsLoaded(0) && vc[10] == 100 && !vc.IsLoaded(60) && fetches == 1;
            vc.EnsureRange(0, 30);                                   // same page → deduped, no new fetch
            bool deduped = fetches == 1;
            vc.EnsureRange(40, 120);                                 // pages 0(loaded)+1+2 → exactly 2 new fetches
            bool windowed = vc.IsLoaded(60) && vc[60] == 600 && vc.IsLoaded(120) && vc[120] == 1200 && fetches == 3;

            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long sum = 0; for (int i = 0; i < 150; i++) sum += vc[i];   // index across loaded pages
            vc.EnsureRange(0, 140);                                  // window already present → no fetch, no alloc
            long hot = GC.GetAllocatedBytesForCurrentThread() - a0;

            int seedFetches = 0;
            var seeded = new VirtualCollection<int>((off, cnt, ct) =>
            {
                seedFetches++;
                return new ValueTask<PageResult<int>>(new PageResult<int>(500, data.AsMemory(off, Math.Min(cnt, 500 - off))));
            }, pageSize: 50);
            seeded.Seed(500, data.AsSpan(0, 50));                    // the overview's first window — free
            seeded.EnsureRange(0, 40);                               // covered by the seed → no fetch
            bool seedFree = seeded.Count == 500 && seeded.IsLoaded(0) && seeded[7] == 70 && seedFetches == 0;

            Check("virtual-collection: paged data-windowing — total from page 0, fill, dedup, seed, 0-alloc hot path",
                before && firstPage && deduped && windowed && hot == 0 && seedFree,
                $"count={vc.Count} fetches={fetches} hotAlloc={hot} seedFetches={seedFetches} sum={sum}");
        }

        // lazy-grid windowing math — the in-page virtualized grid: a scroll band → the visible row range + spacer heights
        // that reserve the WHOLE collection's extent (so the page scrollbar/sections-below never jump), with an inline
        // drawer's height reserved whether it's in or above the window. The extent invariant (topPad+block+drawer+bottom ==
        // contentH) must hold exactly so the realized window can move without any scroll drift.
        {
            const float rowH = 200f, vh = 400f; const int total = 100, over = 2;
            float Extent(in LazyGridMath.View v, float drawer) => v.TopPad + (v.LastRow - v.FirstRow + 1) * rowH + (v.DrawerVisible ? drawer : 0f) + v.BottomPad;

            var v0 = LazyGridMath.Compute(0f, vh, rowH, total, over, -1, 0f);              // at top
            bool atTop = v0.FirstRow == 0 && v0.LastRow == 4 && Near(v0.TopPad, 0f, 0.5f) && Near(Extent(v0, 0f), 20000f, 0.5f);
            var v1 = LazyGridMath.Compute(1000f, vh, rowH, total, over, -1, 0f);           // scrolled to row 5
            bool mid = v1.FirstRow == 3 && v1.LastRow == 9 && Near(v1.TopPad, 600f, 0.5f) && Near(Extent(v1, 0f), 20000f, 0.5f);
            var vAhead = LazyGridMath.Compute(1000f, vh, rowH, total, 4, -1, 0f);           // media grids prefetch farther ahead/behind
            bool largerOverscan = vAhead.FirstRow == 1 && vAhead.LastRow == 11 && Near(Extent(vAhead, 0f), 20000f, 0.5f);
            var v2 = LazyGridMath.Compute(1000f, vh, rowH, total, over, 5, 300f);          // drawer inside the window
            bool drawerIn = v2.DrawerVisible && Near(Extent(v2, 300f), 20300f, 0.5f);
            var v3 = LazyGridMath.Compute(4000f, vh, rowH, total, over, 5, 300f);          // drawer scrolled ABOVE the window
            bool drawerAbove = !v3.DrawerVisible && Near(v3.TopPad, 3900f, 0.5f) && Near(Extent(v3, 300f), 20300f, 0.5f);
            var vEnd = LazyGridMath.Compute(1e9f, vh, rowH, total, over, -1, 0f);          // clamped at the bottom
            bool atEnd = vEnd.LastRow == total - 1 && Near(Extent(vEnd, 0f), 20000f, 0.5f);
            float shortTarget = LazyGridMath.ExpandedTarget(700f, 5000f, 1100f, 220f);
            float tallTarget = LazyGridMath.ExpandedTarget(700f, 5380f, 1100f, 600f);   // same drawer-less extent
            bool bring = Near(shortTarget, 1072f, 0.5f) && Near(tallTarget, shortTarget, 0.01f);

            Check("lazy-grid: window covers the viewport and spacers reserve the exact extent (incl. inline drawer in/above window)",
                atTop && mid && largerOverscan && drawerIn && drawerAbove && atEnd && bring,
                $"top=({v0.FirstRow},{v0.LastRow},pad{v0.TopPad:0}) mid=({v1.FirstRow},{v1.LastRow}) ahead=({vAhead.FirstRow},{vAhead.LastRow}) drawerAboveTopPad={v3.TopPad:0} endLast={vEnd.LastRow} bring={shortTarget:0}/{tallTarget:0}");
        }

        // 23t — SizeMode.Relayout restores the DECLARED LayoutInput at settle (auto height stays auto).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("relayoutfix", new Size2(360, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new RelayoutRestoreProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var toggle = Child(host.Scene, host.Scene.Root, 0);
            var card = Child(host.Scene, host.Scene.Root, 1);
            float narrowH = host.Scene.AbsoluteRect(card).H;       // tall: text wraps hard at 160

            ClickNode(host, window, toggle);                       // widen 160 → 300
            for (int i = 0; i < 90; i++) host.RunFrame();          // settle the spring fully
            bool wideAuto = float.IsNaN(host.Scene.Layout(card).Height);          // declared auto RESTORED
            bool wideDeclaredW = host.Scene.Layout(card).Width == 300f;           // declared width restored too
            float wideH = host.Scene.AbsoluteRect(card).H;                        // fewer lines → shorter

            ClickNode(host, window, toggle);                       // back to narrow
            for (int i = 0; i < 90; i++) host.RunFrame();
            bool narrowAuto = float.IsNaN(host.Scene.Layout(card).Height);
            float narrowH2 = host.Scene.AbsoluteRect(card).H;                     // re-wraps back to the tall layout
            Check("23t. Relayout settle restores declared LayoutInput (auto axis stays auto; round-trip re-wraps)",
                wideAuto && wideDeclaredW && narrowAuto && wideH < narrowH - 4f && Near(narrowH2, narrowH, 1.5f),
                $"narrowH={narrowH:0} wideH={wideH:0} narrowH2={narrowH2:0} wideAuto={wideAuto} narrowAuto={narrowAuto}");
        }
    }

    static void ReflowChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("reflow", new Size2(360, 420), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new ReflowProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        var s = host.Scene;

        host.RunFrame();   // mount collapsed (first frame never captures → no spurious enter reveal)
        var toggle = Child(s, s.Root, 0);
        var noise = Child(s, s.Root, 1);
        var shift = Child(s, s.Root, 2);
        var wrap = Child(s, s.Root, 3);
        var row = Child(s, s.Root, 4);
        var mover = Child(s, row, 1);
        float sibY0 = s.AbsoluteRect(row).Y;          // the row below IS the observable sibling
        float wrapH0 = s.AbsoluteRect(wrap).H;

        // 23r.a — expand: old size on the click frame (no jump), sibling eases MONOTONICALLY through the 333ms reveal,
        // Trailing child-shift == interp − contentExtent mid-flight, settle restores the declared NaN(auto) input.
        ClickNode(host, window, toggle);
        float wrapHClick = s.AbsoluteRect(wrap).H;
        float sibYClick = s.AbsoluteRect(row).Y;
        bool monotone = true;
        float prevY = sibYClick, midH = 0f, midShift = 0f;
        for (int i = 0; i < 30; i++)
        {
            host.RunFrame();
            float y = s.AbsoluteRect(row).Y;
            if (y < prevY - 0.25f) monotone = false;
            prevY = y;
            if (i == 2) { midH = s.AbsoluteRect(wrap).H; midShift = s.Paint(wrap).ChildShiftY; }
        }
        float wrapHOpen = s.AbsoluteRect(wrap).H;
        float sibYOpen = s.AbsoluteRect(row).Y;
        bool liRestoredOpen = float.IsNaN(s.Layout(wrap).Height);
        bool shiftRest = s.Paint(wrap).ChildShiftY == 0f;
        Check("23r.a Reflow expand: old-size click frame, sibling eases monotonically, trailing shift rides, settle restores declared input",
            wrapH0 < 0.5f && wrapHClick < 0.5f && Near(sibYClick, sibY0, 0.5f) && monotone
            && midH > 4f && midH < 56f && Near(midShift, midH - 60f, 1.5f)
            && Near(wrapHOpen, 60f, 0.5f) && Near(sibYOpen, sibY0 + 60f, 0.5f) && liRestoredOpen && shiftRest,
            $"wrapH {wrapHClick:0.0}→{midH:0.0}→{wrapHOpen:0.0} sibY {sibY0:0.0}→{sibYClick:0.0}→{sibYOpen:0.0} shift={midShift:0.0} liNaN={liRestoredOpen}");

        // 23r.b — collapse with a mid-flight UNRELATED re-commit: the commit snap-solves the wrapper at its declared
        // value inside the frame, but the target/echo guards keep the in-flight track (no restart) and phase 7
        // re-establishes the interp before record — so the collapse still settles ON SCHEDULE (167ms + pad).
        ClickNode(host, window, toggle);                 // collapse — ExitDynamics leg
        host.RunFrame(); host.RunFrame();                // ~32ms in
        float hA = s.AbsoluteRect(wrap).H;
        ClickNode(host, window, noise);                  // unrelated state commit mid-flight
        float hB = s.AbsoluteRect(wrap).H;
        bool stillFlying = host.Animation.HasTracks(wrap);
        for (int i = 0; i < 11; i++) host.RunFrame();    // total ≈ 224ms ≥ 167ms — would NOT settle if the tween restarted
        bool closedOnSchedule = Near(s.AbsoluteRect(wrap).H, 0f, 0.5f) && !host.Animation.HasTracks(wrap);
        bool liRestoredClosed = s.Layout(wrap).Height == 0f;
        bool sibHome = Near(s.AbsoluteRect(row).Y, sibY0, 0.5f);
        Check("23r.b Reflow collapse: mid-flight reconcile does not restart the track (guards), settles on schedule, declared 0 restored",
            hA < 59.5f && hA > 0.5f && hB <= hA + 0.25f && stillFlying && closedOnSchedule && liRestoredClosed && sibHome,
            $"hA={hA:0.0} hB={hB:0.0} flying={stillFlying} closed={closedOnSchedule} li0={liRestoredClosed}");

        // 23x — rigidity: a BoundsAnimated node below the reflowing wrapper rides the reveal RIGIDLY (parent-relative
        // projection skips it on commit frames — exercised by clicking noise EVERY ride frame), then a genuine LOCAL
        // move (the leading spacer) still FLIPs it.
        ClickNode(host, window, toggle);                 // expand again
        bool rigid = true;
        float prevMovY = s.AbsoluteRect(mover).Y, prevRowY = s.AbsoluteRect(row).Y;
        float rideStartY = prevMovY;
        for (int i = 0; i < 5; i++)
        {
            ClickNode(host, window, noise);              // every ride frame is a COMMIT frame (capture+apply run)
            float my = s.AbsoluteRect(mover).Y, ry = s.AbsoluteRect(row).Y;
            if (!Near(my - prevMovY, ry - prevRowY, 0.25f)) rigid = false;
            if (host.Animation.HasTracks(mover)) rigid = false;
            if (MathF.Abs(s.Paint(mover).LocalTransform.Dy) > 0.01f) rigid = false;
            prevMovY = my; prevRowY = ry;
        }
        bool rode = prevMovY > rideStartY + 4f;          // it genuinely moved with the reveal
        for (int i = 0; i < 30; i++) host.RunFrame();    // settle the reveal
        float movX0 = s.AbsoluteRect(mover).X;
        ClickNode(host, window, shift);                  // spacer 0→40: a LOCAL move within the row
        bool seeded = host.Animation.HasTracks(mover);
        float dx0 = s.Paint(mover).LocalTransform.Dx;    // JustSeeded samples u=0 → −40 on the commit frame
        bool held = Near(s.AbsoluteRect(mover).X, movX0, 1.5f);   // presented X holds (FLIP "Invert")
        for (int i = 0; i < 30; i++) host.RunFrame();
        bool landed = Near(s.AbsoluteRect(mover).X, movX0 + 40f, 0.5f) && !host.Animation.HasTracks(mover);
        Check("23x. parent-relative projection: ancestor reflow rides rigidly (no tracks, no transform); a local move still FLIPs",
            rigid && rode && seeded && dx0 < -30f && held && landed,
            $"rigid={rigid} rode={rode} seeded={seeded} dx0={dx0:0.0} held={held} landed={landed}");
    }


    static void StyleChecks()
    {
        var s = new Button.Style
        {
            Background = ColorF.FromRgba(10, 20, 30),
            Foreground = ColorF.FromRgba(40, 50, 60),
            HoverBackground = ColorF.FromRgba(70, 80, 90),
            CornerRadius = 8f,
        };
        var btn = Button.Accent("x", () => { }, s);
        bool styled = btn.Fill.Value == s.Background
            && btn.HoverFill == s.HoverBackground
            && Near(btn.Corners.Value.TopLeft, 8f)
            && btn.Children[0] is TextEl t && t.Color.Value == s.Foreground;

        var modded = Button.Accent("y", () => { }).Background(ColorF.FromRgba(1, 2, 3)).Rounded(12f);
        bool overridden = modded.Fill.Value == ColorF.FromRgba(1, 2, 3) && Near(modded.Corners.Value.TopLeft, 12f);

        // Wave-1 parity: WinUI Button storyboards swap brushes ONLY (Button_themeresources.xaml:176-229 — no scale),
        // so the default Button must have NO press scale; IconButton (engine media-transport control) keeps its
        // deliberate glyph pop, proving the scale CHANNEL still works for controls that opt in.
        var animatedButton = Button.Standard("z", () => { });
        var animatedIcon = IconButton.Create("i", () => { });
        bool animation = animatedButton.PressScale == 1f && animatedButton.HoverScale == 1f
            && animatedIcon.Children[0] is BoxEl iconGlyph
            && iconGlyph.HoverScale > 1f
            && iconGlyph.PressScale < 1f;

        Check("27. controls are user-styleable + animated (ButtonStyle, modifiers, AnimatedIcon)", styled && overridden && animation,
            "custom style + .Background().Rounded() + WinUI no-scale Button / opt-in icon scale");
    }

    static void ButtonAxesChecks()
    {
        // -- gate.ctl.button.axes -- the 4x3 matrix resolves the specified token ramps; axes are INDEPENDENT --
        var std = Button.DefaultStyle(ButtonAppearance.Standard, ControlSize.Medium);
        var accent = Button.DefaultStyle(ButtonAppearance.Accent, ControlSize.Medium);
        var subtle = Button.DefaultStyle(ButtonAppearance.Subtle, ControlSize.Medium);
        var outline = Button.DefaultStyle(ButtonAppearance.Outline, ControlSize.Medium);
        var smallStd = Button.DefaultStyle(ButtonAppearance.Standard, ControlSize.Small);
        var largeStd = Button.DefaultStyle(ButtonAppearance.Standard, ControlSize.Large);
        var subtleLarge = Button.DefaultStyle(ButtonAppearance.Subtle, ControlSize.Large);

        // Standard/Accent = EXACTLY today's WinUI-faithful tokens + Medium metrics (pixel-identical, no drift).
        bool stdIdentity = std.Background == Tok.FillControlDefault && std.HoverBackground == Tok.FillControlSecondary
            && std.PressedBackground == Tok.FillControlTertiary && std.Foreground == Tok.TextPrimary
            && std.BackgroundSizing == BackgroundSizing.InnerBorderEdge
            && std.Padding == new Edges4(11, 5, 11, 6) && std.MinHeight == 32f && std.FontSize == 14f;
        bool accentIdentity = accent.Background == Tok.AccentDefault && accent.HoverBackground == Tok.AccentSecondary
            && accent.Foreground == Tok.TextOnAccentPrimary && accent.BackgroundSizing == BackgroundSizing.OuterBorderEdge;

        // Subtle = the WinUI SubtleFillColor* ramp; the sampled assertion: hover fill == FillSubtleSecondary.
        bool subtleHover = subtle.HoverBackground == Tok.FillSubtleSecondary
            && subtle.Background == Tok.FillSubtleTransparent && subtle.Foreground == Tok.TextPrimary;
        // Outline = solid StrokeControlDefault border at REST *and* PRESSED, transparent interior.
        bool outlineBorder = outline.BorderBrush is { } obr && obr.Stops[0].Color == Tok.StrokeControlDefault
            && outline.PressedBorderBrush is { } obp && obp.Stops[0].Color == Tok.StrokeControlDefault
            && outline.Background == Tok.FillSubtleTransparent;

        // Size axis is orthogonal: Small MinHeight 24 / Large 40, appearance-independent metrics.
        bool sizes = smallStd.MinHeight == 24f && smallStd.FontSize == 12f && smallStd.Padding == new Edges4(7, 2, 7, 3)
            && largeStd.MinHeight == 40f && largeStd.Padding == new Edges4(15, 9, 15, 10);
        // Subtle+Large == Subtle palette (hover fill unchanged by size) + Large metrics (height/padding unchanged by appearance).
        bool independent = subtleLarge.HoverBackground == Tok.FillSubtleSecondary && subtleLarge.Background == subtle.Background
            && subtleLarge.MinHeight == 40f && subtleLarge.Padding == largeStd.Padding;

        Check("gate.ctl.button.axes 4x3 appearance x size matrix resolves token ramps + axes independent",
            stdIdentity && accentIdentity && subtleHover && outlineBorder && sizes && independent,
            $"stdId={stdIdentity} accId={accentIdentity} subtleHover={subtleHover} outlineBorder={outlineBorder} sizes={sizes} indep={independent}");

        // -- gate.ctl.button.stylehook -- StyleHook wins over DefaultStyle; null falls through to the composed default --
        var sentinel = new Button.Style { Background = ColorF.FromRgba(1, 2, 3), MinHeight = 99f };
        Button.StyleHook = (a, sz) => a == ButtonAppearance.Outline && sz == ControlSize.Large ? sentinel : null;
        var hooked = Button.DefaultStyle(ButtonAppearance.Outline, ControlSize.Large);
        var fell = Button.DefaultStyle(ButtonAppearance.Outline, ControlSize.Small);   // hook returns null here
        var builtHooked = Button.Create("x", () => { }, ButtonAppearance.Outline, ControlSize.Large);   // hook flows through Create
        Button.StyleHook = null;                                                        // reset the global before anything else
        bool hookWins = hooked.MinHeight == 99f && hooked.Background == ColorF.FromRgba(1, 2, 3) && builtHooked.MinHeight == 99f;
        bool nullFallsThrough = fell.MinHeight == 24f
            && fell.BorderBrush is { } fb && fb.Stops[0].Color == Tok.StrokeControlDefault;   // Outline+Small composed normally
        Check("gate.ctl.button.stylehook StyleHook wins over DefaultStyle; null falls through",
            hookWins && nullFallsThrough, $"hookWins={hookWins} nullFallsThrough={nullFallsThrough}");

        // -- gate.ctl.button.glyph-slot -- Create with glyph renders icon+label; without = label-only --
        var withGlyph = Button.Create("Save", () => { }, glyph: Icons.Play);
        var noGlyph = Button.Create("Save", () => { });
        bool glyphStructure = withGlyph.Children.Length == 2
            && withGlyph.Children[0] is TextEl g && g.FontFamily == Theme.IconFont && g.Text.Value == Icons.Play
            && withGlyph.Children[1] is TextEl gl && gl.Text.Value == "Save";
        bool labelOnly = noGlyph.Children.Length == 1
            && noGlyph.Children[0] is TextEl only && only.Text.Value == "Save" && only.FontFamily != Theme.IconFont;
        Check("gate.ctl.button.glyph-slot Create(glyph) renders icon+label; without = label-only",
            glyphStructure && labelOnly, $"withGlyph={glyphStructure} labelOnly={labelOnly}");
    }

    static void AnimValueChecks()
    {
        var p = new AnimProbe { Target = 0f };
        p.RenderWithHooks();                 // mount → value = 0
        p.Target = 1f;
        p.RenderWithHooks();                 // target changed → first eased step
        float v1 = p.Value;
        for (int i = 0; i < 20; i++) p.RenderWithHooks();   // advance past the 100ms duration
        float v2 = p.Value;
        Check("28. UseAnimatedValue eases then settles", v1 > 0f && v1 < 1f && Near(v2, 1f), $"step={v1:0.00} settled={v2:0.0}");
    }

    static void CompositorChecks(StringTable strings)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 200, Height = 100, OffsetX = 20, OffsetY = 30, Opacity = 0.5f,
            Fill = ColorF.FromRgba(255, 0, 0),
            Children = [new BoxEl { Width = 40, Height = 20, Fill = ColorF.FromRgba(0, 255, 0), Opacity = 0.5f }],
        }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);

        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));

        var parent = dev.LastRects[0];   // root box
        var child = dev.LastRects[1];    // nested box
        bool parentOk = Near(parent.Transform.Dx, 20) && Near(parent.Transform.Dy, 30) && Near(parent.Opacity, 0.5f);
        bool childOk = Near(child.Opacity, 0.25f) && child.Transform.Dx >= 20f;   // opacity composes 0.5*0.5; inherits parent offset
        Check("30. compositor: transform + cumulative opacity", parentOk && childOk, $"pOffset=({parent.Transform.Dx:0.#},{parent.Transform.Dy:0.#}) childOpacity={child.Opacity:0.00}");

        var edgeScene = new SceneStore();
        new TreeReconciler(edgeScene, strings).ReconcileRoot(new BoxEl
        {
            Width = 200, Height = 80, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Width = 200, Height = 160, Fill = ColorF.FromRgba(20, 20, 20),
                    EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 32f),
                    Children = [new BoxEl { Width = 200, Height = 160, Fill = ColorF.FromRgba(255, 255, 255) }],
                },
            ],
        }, null);
        new FlexLayout(edgeScene, new HeadlessFontSystem(strings)).Run(edgeScene.Root);
        var edgeDl = new DrawList();
        SceneRecorder.Record(edgeScene, edgeDl);
        var edgeDev = new HeadlessGpuDevice();
        edgeDev.SubmitDrawList(edgeDl.Bytes, edgeDl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
        PushLayerCmd edgeLayer = default;
        foreach (var l in edgeDev.LastLayers) if (l.Kind == (int)LayerKind.EdgeFade) { edgeLayer = l; break; }
        bool edgeClipOk = edgeLayer.Kind == (int)LayerKind.EdgeFade
            && Near(edgeLayer.DeviceRect.H, 160f)
            && Near(edgeLayer.CompositeClip.H, 80f)
            && Near(edgeLayer.CompositeClip.W, 200f)
            && edgeLayer.LayerId != 0;
        Check("30a. edge-fade layer carries effective composite clip + stable source id",
            edgeClipOk, $"deviceH={edgeLayer.DeviceRect.H:0.#} clip=({edgeLayer.CompositeClip.W:0.#}x{edgeLayer.CompositeClip.H:0.#}) id={edgeLayer.LayerId}");

        var nestedScene = new SceneStore();
        new TreeReconciler(nestedScene, strings).ReconcileRoot(new BoxEl
        {
            Width = 240, Height = 120,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Horizontal, 24f),
            Children =
            [
                new BoxEl
                {
                    Width = 180, Height = 80, Acrylic = AcrylicSpec.InAppDefault,
                    Children = [new BoxEl { Width = 180, Height = 80, Fill = ColorF.FromRgba(255, 255, 255) }],
                },
            ],
        }, null);
        new FlexLayout(nestedScene, new HeadlessFontSystem(strings)).Run(nestedScene.Root);
        var nestedDl = new DrawList();
        SceneRecorder.Record(nestedScene, nestedDl);
        var nestedDev = new HeadlessGpuDevice();
        nestedDev.SubmitDrawList(nestedDl.Bytes, nestedDl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
        bool nestedKinds = nestedDev.LastLayers.Count >= 2
            && nestedDev.LastLayers[0].Kind == (int)LayerKind.EdgeFade
            && nestedDev.LastLayers[1].Kind == (int)LayerKind.Acrylic
            && nestedDev.LastLayers[0].LayerId != 0
            && nestedDev.LastLayers[1].LayerId != 0
            && nestedDev.LayerBalance == 0;
        Check("30b. edge-fade -> acrylic records balanced nested layers with stable target identities",
            nestedKinds, $"layers={nestedDev.LastLayers.Count} balance={nestedDev.LayerBalance}");
    }

    static void AnimEngineChecks(StringTable strings)
    {
        // eased multi-keyframe tween → composed into LocalTransform
        var s1 = Single(strings);
        var a1 = new AnimEngine(s1);
        a1.Animate(s1.Root, AnimChannel.TranslateX, 0f, 100f, 100f, Easing.Linear);
        a1.Tick(0f);
        a1.Tick(50f);
        float mid = s1.Paint(s1.Root).LocalTransform.Dx;
        a1.Tick(100f);
        float end = s1.Paint(s1.Root).LocalTransform.Dx;
        Check("31. eased keyframe tween + hold", Near(mid, 50f, 1f) && Near(end, 100f, 0.5f), $"mid={mid:0.#} end={end:0.#}");

        // composite Add: two tracks on one channel combine (animation-composition: add)
        var s2 = Single(strings);
        var a2 = new AnimEngine(s2);
        a2.Animate(s2.Root, AnimChannel.TranslateX, 0f, 30f, 100f, Easing.Linear, CompositeOp.Replace);
        a2.Animate(s2.Root, AnimChannel.TranslateX, 0f, 20f, 100f, Easing.Linear, CompositeOp.Add);
        a2.Tick(0f);
        a2.Tick(100f);
        float add = s2.Paint(s2.Root).LocalTransform.Dx;
        Check("32. composite add combines tracks", Near(add, 50f, 0.5f), $"dx={add:0.#}");

        // spring settles to its target (semi-implicit ODE)
        var s3 = Single(strings);
        var a3 = new AnimEngine(s3);
        a3.Spring(s3.Root, AnimChannel.ScaleX, 1.3f, SpringParams.FromResponse(0.2f, 1f), initial: 1.0f);
        for (int i = 0; i < 150; i++) a3.Tick(16f);
        float sx = s3.Paint(s3.Root).LocalTransform.M11;
        Check("33. spring settles to target", Near(sx, 1.3f, 0.02f), $"scaleX={sx:0.###}");

        // scroll-driven timeline: a value source maps to progress (animation-timeline: scroll())
        var s4 = Single(strings);
        var a4 = new AnimEngine(s4);
        float scroll = 0f;
        int clk = a4.Clocks.Register(() => scroll);
        a4.Drive(s4.Root, AnimChannel.Opacity, [new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.Linear)], clk, 0f, 100f);
        scroll = 25f; a4.Tick(16f);
        float op25 = s4.Paint(s4.Root).Opacity;
        scroll = 100f; a4.Tick(16f);
        float op100 = s4.Paint(s4.Root).Opacity;
        Check("34. scroll-driven timeline", Near(op25, 0.25f, 0.02f) && Near(op100, 1f, 0.01f), $"op@25={op25:0.00} op@100={op100:0.00}");
    }

    static void AnimHookChecks(StringTable strings)
    {
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, strings) { Anim = anim };
        recon.ReconcileRoot(Embed.Comp(() => new SpringProbe()), null);

        foreach (var c in recon.LiveComponents)   // phase 6.5: drain layout effects → seeds the spring on the host node
        {
            foreach (var e in c.Context.PendingLayoutEffects) e();
            c.Context.PendingLayoutEffects.Clear();
        }
        for (int i = 0; i < 150; i++) anim.Tick(16f);

        var host = scene.FirstChild(scene.Root);   // the SpringProbe's box node
        float sx = scene.Paint(host).LocalTransform.M11;
        Check("35. UseSpring hook seeds + drives the node", !host.IsNull && Near(sx, 1.2f, 0.03f), $"scaleX={sx:0.###}");
    }

    static void WaveeSkeletonChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("wavee", new Size2(1100, 720), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var shell = new WaveeShell();
        using var host = new AppHost(app, window, device, fonts, strings, shell);

        host.RunFrame();
        bool home = HasGlyph(device, strings, "Album 0") && HasGlyph(device, strings, "Home");
        bool playerBar = HasGlyph(device, strings, "Now Playing") && HasGlyph(device, strings, "Shuffle");
        bool artRequested = host.Images.Count >= 12;   // 12 album cards + now-playing art all requested + pinned

        shell.Nav.Push("playlist", "p0");
        host.RunFrame();
        long liveOnPlaylist = host.Scene.LiveCount;
        bool virtualized = HasGlyph(device, strings, "Track 0")
            && !HasGlyph(device, strings, "Track 4999")     // last row never realized → virtualization holds in the shell
            && liveOnPlaylist < 600;                         // 5,000 rows × multiple nodes would be ≫ this if not virtualized

        bool back = false;
        if (shell.Nav.Pop()) { host.RunFrame(); back = HasGlyph(device, strings, "Album 0"); }   // back-stack returns Home

        Check("52. Wavee skeleton: shell composes nav + grid + images + controls + virtualized list", home && playerBar && artRequested && virtualized && back,
            $"home={home} player={playerBar} art={host.Images.Count} liveOnList={liveOnPlaylist} back={back}");
    }

    static void CrossfadeChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, new BoxEl
        {
            Width = 100, Height = 40, Fill = ColorF.FromRgba(0, 0, 0), HoverFill = ColorF.FromRgba(255, 255, 255),
        });
        var node = scene.Root;
        var ia = new AnimEngine(scene);   // hover/press now engine-driven (InteractionAnimator subsumed)
        ia.SetHover(node, true);

        var dl = new DrawList();
        var dev = new HeadlessGpuDevice();
        float Grey()
        {
            dl.Reset(); SceneRecorder.Record(scene, dl);
            dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200, 100), 1f, ColorF.Transparent));
            return dev.LastRects[0].Fill.R;
        }

        ia.Tick(4f);                                   // small step → partway, not snapped
        float mid = Grey();
        bool eased = mid > 0.02f && mid < 0.98f;
        for (int i = 0; i < 16; i++) ia.Tick(16f);     // run past 83ms → settle
        float settled = Grey();
        bool done = settled > 0.99f && !ia.HasActive;
        Check("58. hover cross-fade eases in linear light, then settles", eased && done, $"mid={mid:0.00} settled={settled:0.00}");
    }

    static void BrushTransitionChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("brush", new Size2(300, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new BrushTransitionProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();

        static FillRoundRectCmd ProbeRect(HeadlessGpuDevice dev)
        {
            foreach (var r in dev.LastRects) if (Near(r.Rect.W, 77f) && Near(r.Rect.H, 33f)) return r;
            return default;
        }

        bool restingA = ColorClose(ProbeRect(device).Fill, BrushTransitionProbe.FillA, 0.004f);

        root.On!.Value = true;   // logical flip → re-render with FillB/TextB
        host.RunFrame();         // first frame: T has advanced one fixed dt (~16.7/83) — mid-fade
        var mid = ProbeRect(device).Fill;
        var midText = GlyphColor(device, strings, "bt");
        bool fillMid = mid.B > 0.05f && mid.B < 0.95f && mid.R > 0.05f && mid.R < 0.95f;
        bool textMid = midText.G > 0.05f && midText.G < 0.95f && midText.B > 0.05f && midText.B < 0.95f;

        for (int i = 0; i < 10; i++) host.RunFrame();   // ≥83ms of fixed frames → settled
        bool fillSettled = ColorClose(ProbeRect(device).Fill, BrushTransitionProbe.FillB, 0.004f);
        bool textSettled = ColorClose(GlyphColor(device, strings, "bt"), BrushTransitionProbe.TextB, 0.004f);
        bool idle = !host.HasActiveWork;

        Check("E3.a BrushTransition cross-fades the fill on a logical flip (mid ≠ snap, settles exact)",
            restingA && fillMid && fillSettled, $"rest={restingA} mid=({mid.R:0.##},{mid.G:0.##},{mid.B:0.##}) settled={fillSettled}");
        Check("E3.b BrushTransition cross-fades the text foreground too, then the loop idles",
            textMid && textSettled && idle, $"mid=({midText.R:0.##},{midText.G:0.##},{midText.B:0.##}) settled={textSettled} idle={idle}");
    }

    static void AnimRestChecks(StringTable strings)
    {
        // B1: deactivating a ProgressRing must CancelToRest the trim channels (paint → NaN, recorder falls back to
        // the ArcSpec terminal) — a bare Cancel froze the last interpolated partial sweep in paint.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("anim-rest-ring", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var active = new Signal<bool>(true);
            NodeHandle arc = default;
            var parts = new TemplateParts();
            parts[ProgressRing.PartRing] = b => b with { OnRealized = h => arc = h };
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl { Width = 120, Height = 120, Children = [ProgressRing.Indeterminate(isActive: active.Value, parts: parts)] },
            });
            host.RunFrame(); host.RunFrame(); host.RunFrame();   // let the trim loop write real values into paint
            bool spinning = !arc.IsNull && host.Animation.HasTracks(arc);
            active.Value = false;
            host.RunFrame(); host.RunFrame();
            ref var p = ref host.Scene.Paint(arc);
            Check("anim.rest.progress.ring.cancel deactivation rests the trim channels at NaN (spec fallback), not a frozen partial arc",
                spinning && !host.Animation.HasTracks(arc) && float.IsNaN(p.StrokeTrimStart) && float.IsNaN(p.StrokeTrimEnd),
                $"spinning={spinning} tracks={host.Animation.HasTracks(arc)} trimS={p.StrokeTrimStart} trimE={p.StrokeTrimEnd}");
        }

        // B2: the NavPill shape — after a hide-fade settles and frees, an UNRELATED re-render must keep the node
        // hidden (the element declares the state-dependent static at the transition terminal).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("anim-rest-pill", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var visible = new Signal<bool>(true);
            var unrelated = new Signal<int>(0);
            using var host = new AppHost(app, window, device, fonts, strings, new PillRestProbe { Visible = visible, Unrelated = unrelated });
            host.RunFrame();
            var pill = host.Scene.FirstChild(host.Scene.Root);
            visible.Value = false;
            for (int f = 0; f < 40 && host.Animation.HasActive; f++) host.RunFrame();   // fade out + settle/free
            bool settledHidden = Near(host.Scene.Paint(pill).Opacity, 0f, 0.01f) && !host.Animation.HasTracks(pill);
            unrelated.Value = 1;                                                        // unrelated owner re-render
            host.RunFrame();
            Check("anim.rest.pill hidden-after-fade survives an unrelated re-render (state-dependent resting opacity)",
                settledHidden && Near(host.Scene.Paint(pill).Opacity, 0f, 0.01f),
                $"settledHidden={settledHidden} after={host.Scene.Paint(pill).Opacity}");
        }

        // B3: a settled transform track's terminal survives an owner re-render on an identity-declared polyline
        // (the BoxEl :1003 identity gate now applies to PolylineStrokeEl too).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("anim-rest-poly", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var rr = new Signal<int>(0);
            NodeHandle wrap = default;
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () =>
                {
                    _ = rr.Value;
                    return new BoxEl
                    {
                        Width = 200, Height = 100,
                        Children = [ new BoxEl { OnRealized = h => wrap = h, Children = [ new PolylineStrokeEl
                        {
                            P0 = new Point2(0f, 0f), P1 = new Point2(24f, 24f), PointCount = 2,
                            Color = Tok.AccentDefault, Thickness = 2f, Width = 24f, Height = 24f,
                        } ] } ],
                    };
                },
            });
            host.RunFrame();
            var poly = host.Scene.FirstChild(wrap);
            host.Animation.Animate(poly, AnimChannel.TranslateX, 0f, 14f, 40f, Easing.Linear);
            for (int f = 0; f < 40 && host.Animation.HasActive; f++) host.RunFrame();
            bool settled = !host.Animation.HasTracks(poly) && Near(host.Scene.Paint(poly).LocalTransform.Dx, 14f, 0.1f);
            rr.Value = 1;
            host.RunFrame();
            Check("anim.rest.polyline.transform settled track terminal survives an owner re-render (identity gate)",
                settled && Near(host.Scene.Paint(poly).LocalTransform.Dx, 14f, 0.1f),
                $"settled={settled} dx={host.Scene.Paint(poly).LocalTransform.Dx}");
        }
    }

    static void MarqueeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("marquee", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new MarqueeProbeRoot();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        // A long title in a 150px column overflows (~485px). Past the marquee's start delay the loop track ramps,
        // translating the content left. This also guards the engine fix it depends on: an unconstrained node's
        // OnBoundsChanged must deliver its initial size (FlexLayout one-shot initial bounds delivery) so the marquee
        // detects overflow; without it TextW stays 0 and it never scrolls.
        float maxTrack = 0f;
        for (int i = 0; i < 40; i++) { host.RunFrame(); maxTrack = MathF.Max(maxTrack, MaxAbsTrackX(host, host.Scene.Root)); }
        Check("M1. marquee scrolls overflowing text (OnBoundsChanged initial delivery + nested-component TranslateX seed)",
              maxTrack > 1f, $"maxAbsTrackX={maxTrack:0.##}");

        // M2: a REACTIVE title that grows AFTER the marquee mounted must still start scrolling — the real PlayerBar binds
        // Prop.Of(() => NowPlaying(b).Title), empty until a track loads. The marquee's text box is Shrink=0, so its
        // arranged width equals its measured width; its OnBoundsChanged must edge-trigger on the real arranged-rect change
        // (vs the LAST DELIVERED rect), NOT on a Bounds delta — Measure pre-writes Bounds to that same width each pass, so
        // a Bounds-delta check never re-fires and TextW stays 0 forever (it scrolled only after a window resize remounted
        // it). Guards the SetArrangedBounds delivered-baseline fix. M1 covers title-at-mount; this covers title-after-mount.
        var probe2 = new MarqueeAutostartProbe();
        var window2 = new HeadlessWindow(new WindowDesc("marquee-reactive", new Size2(220, 120), 1f)); window2.Show();
        using var host2 = new AppHost(app, window2, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe2);
        host2.RunFrame();                                  // mount with an EMPTY title
        for (int i = 0; i < 8; i++) host2.RunFrame();      // settle: the mount one-shot delivers TextW = 0 (no overflow yet)
        probe2.Title.Value = "This is a very long track title that should overflow and scroll";   // "a track loads"
        float maxTrack2 = 0f;
        for (int i = 0; i < 40; i++) { host2.RunFrame(); maxTrack2 = MathF.Max(maxTrack2, MaxAbsTrackX(host2, host2.Scene.Root)); }
        Check("M2. marquee scrolls when its reactive title grows AFTER mount (OnBoundsChanged edge-triggers on the real arranged-rect change, not the Measure-corrupted Bounds delta)",
              maxTrack2 > 1f, $"maxAbsTrackX={maxTrack2:0.##}");

        // M2b: the real player-bar shape sits idle before metadata arrives and uses an external PauseOnHover gate. The
        // settled idle track must not strand the later overflowing title at x=0; it must seed a fresh looping track
        // without needing a window/layout change.
        var probe2b = new PlayerBarMarqueeProbe();
        var window2b = new HeadlessWindow(new WindowDesc("marquee-playerbar", new Size2(480, 120), 1f)); window2b.Show();
        using var host2b = new AppHost(app, window2b, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe2b);
        for (int i = 0; i < 30; i++) host2b.RunFrame(); // >200ms: let the initial non-looping 0->0 track settle and retire
        probe2b.Title.Value = "EYES CLOSED (with ZAYN)";
        float maxTrack2b = 0f;
        int wakeFrames2b = 0;
        while (wakeFrames2b++ < 60 && host2b.HasActiveWork)
        {
            host2b.RunFrame();
            maxTrack2b = MathF.Max(maxTrack2b, MaxAbsTrackX(host2b, host2b.Scene.Root));
        }
        ClickNode(host2b, window2b, probe2b.TitleNode);
        host2b.RunFrame();
        Check("M2b. a production marquee moves perceptibly and its title root stays clickable under a context-menu ancestor",
              maxTrack2b > 8f && probe2b.Clicks == 1,
              $"maxAbsTrackX={maxTrack2b:0.##} wakeFrames={wakeFrames2b - 1} clicks={probe2b.Clicks}");

        // M3: scroll-aware edge fade — at translateX=0 fade right only; mid-scroll left band appears; scrollX must track motion.
        const float fadeCw = 150f, fadeTw = 485f, fadeBand = 24f;
        var fadeStyle = new Marquee.Style { FontSize = 14f, StartDelayMs = 0f, Speed = 200f, Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.Always };
        var fadeAt0 = MarqueeScroller.ResolveEdgeFade(fadeStyle, 0f, fadeCw, fadeTw, fadeBand);
        var fadeMid = MarqueeScroller.ResolveEdgeFade(fadeStyle, -120f, fadeCw, fadeTw, fadeBand);
        var fadeAtEnd = MarqueeScroller.ResolveEdgeFade(fadeStyle, -(fadeTw - fadeCw), fadeCw, fadeTw, fadeBand);
        bool startRightOnly = fadeAt0 is { } f0 && (f0.Edges & EdgeMask.Right) != 0 && f0.Band(EdgeMask.Left) <= 0.5f;
        bool midHasLeft = fadeMid is { } fm && fm.Band(EdgeMask.Left) > 0.5f;
        bool endLeftOnly = fadeAtEnd is { } fe && fe.Band(EdgeMask.Left) > 0.5f && fe.Band(EdgeMask.Right) <= 0.5f;
        Check("M3a. marquee ResolveEdgeFade is position-aware (right at start, left mid-scroll, left-only at tail)",
              startRightOnly && midHasLeft && endLeftOnly,
              $"startR={startRightOnly} midL={midHasLeft} endL={endLeftOnly}");

        var probe3 = new MarqueePingPongProbe();
        var window3 = new HeadlessWindow(new WindowDesc("marquee-edge", new Size2(220, 120), 1f)); window3.Show();
        using var host3 = new AppHost(app, window3, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe3);
        float maxTrack3 = 0f, maxLeftBand = 0f;
        for (int i = 0; i < 60; i++)
        {
            host3.RunFrame();
            maxTrack3 = MathF.Max(maxTrack3, MaxAbsTrackX(host3, host3.Scene.Root));
            maxLeftBand = MathF.Max(maxLeftBand, MaxEdgeFadeLeftBand(host3.Scene, host3.Scene.Root));
        }
        Check("M3b. marquee edge-fade left band appears after scroll (scrollX ticker wired)",
              maxTrack3 > 10f && maxLeftBand > 0.5f, $"maxAbsTrackX={maxTrack3:0.##} maxLeftBand={maxLeftBand:0.##}");
    }

    static float MaxEdgeFadeLeftBand(SceneStore s, NodeHandle n)
    {
        float best = 0f;
        if (s.TryGetEdgeFade(n, out var ef)) best = MathF.Max(best, ef.Band(EdgeMask.Left));
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            best = MathF.Max(best, MaxEdgeFadeLeftBand(s, c));
        return best;
    }

    static void CleanSpanReuseChecks()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        var a = scene.CreateNode(1);
        var b = scene.CreateNode(1);
        scene.Root = root;
        scene.AppendChild(root, a);
        scene.AppendChild(root, b);

        ref var rb = ref scene.Bounds(root);
        rb = new RectF(0, 0, 120, 40);
        ref var ab = ref scene.Bounds(a);
        ab = new RectF(0, 0, 50, 30);
        ref var bb = ref scene.Bounds(b);
        bb = new RectF(60, 0, 50, 30);

        ref var ap = ref scene.Paint(a);
        ap = NodePaint.Default;
        ap.VisualKind = VisualKind.Box;
        ap.Fill = ColorF.FromRgba(0x20, 0x80, 0xE0);
        ref var bp = ref scene.Paint(b);
        bp = NodePaint.Default;
        bp.VisualKind = VisualKind.Box;
        bp.Fill = ColorF.FromRgba(0xE0, 0x80, 0x20);

        var dl = new DrawList();
        var spans = new SpanTable();
        var first = SceneRecorder.Record(scene, dl, spans: spans);
        scene.ClearRecordDirty();

        ap.Fill = ColorF.FromRgba(0x40, 0xC0, 0x70);
        scene.Mark(a, NodeFlags.PaintDirty);
        var dirty = SceneRecorder.Record(scene, dl, spans: spans);
        bool dirtyBranch = dirty.SpanReuseDisabledReasons == SpanReuseDisabledReason.None
            && dirty.SpansReused >= 1
            && dirty.SpansReRecorded >= 2
            && dirty.SpanBytesCopied > 0;
        scene.ClearRecordDirty();

        byte[] dirtyBytes = dl.Bytes.ToArray();
        ulong[] dirtySort = dl.SortKeys.ToArray();
        int dirtyCommands = dl.CommandCount;
        var steady = SceneRecorder.Record(scene, dl, spans: spans);
        bool steadyCopy = steady.SpanReuseDisabledReasons == SpanReuseDisabledReason.None
            && steady.SpansReused == 1
            && steady.SpansReRecorded == 0
            && steady.SpanBytesCopied == dirtyBytes.Length
            && dl.CommandCount == dirtyCommands
            && dl.Bytes.SequenceEqual(dirtyBytes)
            && dl.SortKeys.SequenceEqual(dirtySort);

        float steadyFirstDx = FirstFillDx(dl.Bytes);
        ref var rp = ref scene.Paint(root);
        rp.LocalTransform = Affine2D.Translation(25f, 7f);
        scene.Mark(root, NodeFlags.TransformDirty);
        var moved = SceneRecorder.Record(scene, dl, spans: spans);
        float movedFirstDx = FirstFillDx(dl.Bytes);
        bool transformRebase = moved.SpanReuseDisabledReasons == SpanReuseDisabledReason.None
            && moved.SpansRebased == 1
            && moved.SpansReused == 1
            && moved.SpansReRecorded == 0
            && moved.SpanBytesCopied == dirtyBytes.Length
            && dl.CommandCount == dirtyCommands
            && Near(movedFirstDx, steadyFirstDx + 25f);

        Check("P6.clean-span first record populates spans under the normal recorder path",
            (first.SpanReuseDisabledReasons & SpanReuseDisabledReason.FirstRecord) != 0 && first.SpansReRecorded >= 3,
            $"firstReason={first.SpanReuseDisabledReasons} recorded={first.SpansReRecorded}");
        Check("P6.clean-span dirty child re-records ancestors while reusing a clean sibling",
            dirtyBranch, $"reused={dirty.SpansReused} recorded={dirty.SpansReRecorded} copied={dirty.SpanBytesCopied}");
        Check("P6.clean-span steady frame copies the root span byte-identically",
            steadyCopy, $"reused={steady.SpansReused} recorded={steady.SpansReRecorded} copied={steady.SpanBytesCopied}/{dirtyBytes.Length}");
        Check("P6.v2 transform-only dirty root reuses by translating the prior span, not by re-recording",
            transformRebase, $"reused={moved.SpansReused} rebased={moved.SpansRebased} recorded={moved.SpansReRecorded} copied={moved.SpanBytesCopied}/{dirtyBytes.Length} firstDx={steadyFirstDx:0.##}->{movedFirstDx:0.##}");

        var desc = new SceneStore();
        var descRoot = desc.CreateNode(1);
        var descChild = desc.CreateNode(1);
        desc.Root = descRoot;
        desc.AppendChild(descRoot, descChild);
        desc.Bounds(descRoot) = new RectF(0, 0, 80, 40);
        desc.Bounds(descChild) = new RectF(0, 0, 40, 30);
        ref var descPaint = ref desc.Paint(descChild);
        descPaint = NodePaint.Default;
        descPaint.VisualKind = VisualKind.Box;
        descPaint.Fill = ColorF.FromRgba(0x10, 0x90, 0xF0);
        var descDl = new DrawList();
        var descSpans = new SpanTable();
        _ = SceneRecorder.Record(desc, descDl, spans: descSpans);
        desc.ClearRecordDirty();
        descPaint.LocalTransform = Affine2D.Translation(12f, 0f);
        desc.Mark(descChild, NodeFlags.TransformDirty);
        var descMoved = SceneRecorder.Record(desc, descDl, spans: descSpans);
        float descMovedDx = FirstFillDx(descDl.Bytes);
        Check("P6.v2 descendant transform re-records the ancestor span while rebasing the moving child",
            descMoved.SpansReRecorded >= 1 && descMoved.SpansRebased >= 1 && Near(descMovedDx, 12f),
            $"recorded={descMoved.SpansReRecorded} rebased={descMoved.SpansRebased} firstDx={descMovedDx:0.##}");

        var scrollScene = new SceneStore();
        var viewport = scrollScene.CreateNode(1);
        var content = scrollScene.CreateNode(1);
        var rowA = scrollScene.CreateNode(1);
        var rowB = scrollScene.CreateNode(1);
        scrollScene.Root = viewport;
        scrollScene.AppendChild(viewport, content);
        scrollScene.AppendChild(content, rowA);
        scrollScene.AppendChild(content, rowB);
        scrollScene.Flags(viewport) |= NodeFlags.ClipsToBounds;
        ref var scroll = ref scrollScene.ScrollRef(viewport);
        scroll.ContentNode = content;
        scrollScene.Bounds(viewport) = new RectF(0, 0, 100, 100);
        scrollScene.Bounds(content) = new RectF(0, 0, 100, 180);
        scrollScene.Bounds(rowA) = new RectF(0, 0, 100, 30);
        scrollScene.Bounds(rowB) = new RectF(0, 130, 100, 30);
        ColorF rowAColor = ColorF.FromRgba(0x20, 0x80, 0xE0);
        ColorF rowBColor = ColorF.FromRgba(0xE0, 0x80, 0x20);
        ref var rowAPaint = ref scrollScene.Paint(rowA);
        rowAPaint = NodePaint.Default;
        rowAPaint.VisualKind = VisualKind.Box;
        rowAPaint.Fill = rowAColor;
        ref var rowBPaint = ref scrollScene.Paint(rowB);
        rowBPaint = NodePaint.Default;
        rowBPaint.VisualKind = VisualKind.Box;
        rowBPaint.Fill = rowBColor;
        var scrollDl = new DrawList();
        var scrollSpans = new SpanTable();
        _ = SceneRecorder.Record(scrollScene, scrollDl, spans: scrollSpans);
        bool initialScrollColor = SameColor(FirstFillColor(scrollDl.Bytes), rowAColor);
        scrollScene.ClearRecordDirty();

        scrollScene.Paint(content).LocalTransform = Affine2D.Translation(0f, -70f);
        scrollScene.Mark(content, NodeFlags.TransformDirty);
        var scrollMoved = SceneRecorder.Record(scrollScene, scrollDl, spans: scrollSpans);
        bool enteringRowRecorded = initialScrollColor
            && SameColor(FirstFillColor(scrollDl.Bytes), rowBColor)
            && scrollMoved.SpansReRecorded >= 1;
        Check("P6.v2 moving scroll content re-walks entering rows instead of copying a stale viewport span",
            enteringRowRecorded,
            $"recorded={scrollMoved.SpansReRecorded} rebased={scrollMoved.SpansRebased} firstFill={FirstFillColor(scrollDl.Bytes)}");

        scrollScene.ClearTransformDirty();
        scrollScene.ClearRecordDirty();
        scrollScene.Paint(content).LocalTransform = Affine2D.Translation(0f, -80f);
        scrollScene.Mark(content, NodeFlags.TransformDirty);
        var scrollMovedAgain = SceneRecorder.Record(scrollScene, scrollDl, spans: scrollSpans);
        bool interiorRowRebased = SameColor(FirstFillColor(scrollDl.Bytes), rowBColor)
            && scrollMovedAgain.SpansRebased >= 1;
        Check("P6.v2 moving scroll still rebases fully visible interior row spans",
            interiorRowRebased,
            $"recorded={scrollMovedAgain.SpansReRecorded} rebased={scrollMovedAgain.SpansRebased} firstFill={FirstFillColor(scrollDl.Bytes)}");

        static float FirstFillDx(ReadOnlySpan<byte> bytes)
        {
            int pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
                pos += sizeof(int);
                if (op == DrawOp.FillRoundRect)
                    return MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos, Unsafe.SizeOf<FillRoundRectCmd>())).Transform.Dx;
                pos += DrawPayloadSize(op);
            }
            return float.NaN;
        }

        static ColorF FirstFillColor(ReadOnlySpan<byte> bytes)
        {
            int pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
                pos += sizeof(int);
                if (op == DrawOp.FillRoundRect)
                    return MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos, Unsafe.SizeOf<FillRoundRectCmd>())).Fill;
                pos += DrawPayloadSize(op);
            }
            return default;
        }

        static bool SameColor(ColorF a, ColorF b)
            => Near(a.R, b.R) && Near(a.G, b.G) && Near(a.B, b.B) && Near(a.A, b.A);
    }

    static void SpanReuseScopingChecks()
    {
        // gate.span.popupOpenKeepsMainReuse — a popup skipRoot open ⇒ the steady main frame STILL reuses a clean sibling
        // AND culls an off-screen sibling (cull alive); only the popup's chain (popup + root) re-records.
        {
            var s = new SceneStore();
            var root = s.CreateNode(1); s.Root = root;
            s.Bounds(root) = new RectF(0, 0, 200, 200);
            s.Flags(root) |= NodeFlags.ClipsToBounds;
            _ = SB(s, root, new RectF(0, 0, 100, 40), ColorF.FromRgba(0x20, 0x80, 0xE0));   // visible sibling → reuses
            _ = SB(s, root, new RectF(0, 600, 100, 40), ColorF.FromRgba(0xE0, 0x80, 0x20)); // off-screen sibling → culls
            var popup = SB(s, root, new RectF(0, 0, 60, 60), ColorF.FromRgba(0x40, 0xC0, 0x70));

            var dl = new DrawList();
            var spans = new SpanTable();
            Span<NodeHandle> skip = stackalloc NodeHandle[1]; skip[0] = popup;
            _ = SceneRecorder.Record(s, dl, spans: spans, skipRoots: skip);
            s.ClearRecordDirty();
            var steady = SceneRecorder.Record(s, dl, spans: spans, skipRoots: skip);
            Check("gate.span.popupOpenKeepsMainReuse",
                steady.SpansReused > 0 && steady.NodesCulled > 0 && steady.ScopedBlocks > 0
                && (steady.SpanReuseDisabledReasons & SpanReuseDisabledReason.PopupWindows) != 0,
                $"reused={steady.SpansReused} culled={steady.NodesCulled} blocks={steady.ScopedBlocks} reasons={steady.SpanReuseDisabledReasons}");
        }

        // gate.span.orphanBlocksOnlyChain + gate.span.blockedNodeNeverStores — an exit orphan under branch A blocks A's
        // chain (A + root); branch B keeps reusing; the orphan's per-frame fade repaints (byte-diff across two ticks);
        // and the blocked chain STORES nothing (not-store-while-blocked).
        {
            var s = new SceneStore();
            var root = s.CreateNode(1); s.Root = root;
            s.Bounds(root) = new RectF(0, 0, 240, 120);
            var branchA = SB(s, root, new RectF(0, 0, 120, 120), ColorF.FromRgba(0x10, 0x30, 0x50));
            var branchB = SB(s, root, new RectF(120, 0, 120, 120), ColorF.FromRgba(0x50, 0x30, 0x10));
            var leafA = SB(s, branchA, new RectF(10, 10, 80, 20), ColorF.FromRgba(0x90, 0x90, 0x90));
            _ = SB(s, branchB, new RectF(10, 10, 80, 20), ColorF.FromRgba(0x30, 0x30, 0x30));

            var dl = new DrawList();
            var spans = new SpanTable();
            _ = SceneRecorder.Record(s, dl, spans: spans);
            s.ClearRecordDirty();

            // Exit branch A's leaf → an orphan whose VisualParent is branch A ⇒ block A's chain only.
            s.Orphan(leafA);
            s.Paint(leafA).Opacity = 0.5f; s.Mark(leafA, NodeFlags.PaintDirty);
            var f2 = SceneRecorder.Record(s, dl, spans: spans);
            byte[] fadeA = dl.Bytes.ToArray();
            uint frame2 = spans.CurrentFrameId;
            bool storedA = spans.StoredAtFrame((int)branchA.Raw.Index, frame2);
            bool storedRoot = spans.StoredAtFrame((int)root.Raw.Index, frame2);
            bool storedB = spans.StoredAtFrame((int)branchB.Raw.Index, frame2);

            // Advance the orphan fade a tick: branch B STILL reuses; branch A re-walks so the orphan repaints (bytes differ).
            s.ClearRecordDirty();
            s.Paint(leafA).Opacity = 0.2f; s.Mark(leafA, NodeFlags.PaintDirty);
            var f3 = SceneRecorder.Record(s, dl, spans: spans);
            byte[] fadeB = dl.Bytes.ToArray();
            bool fadeChanged = !fadeA.AsSpan().SequenceEqual(fadeB);

            Check("gate.span.orphanBlocksOnlyChain",
                f2.SpansReused > 0 && f3.SpansReused > 0 && f2.ScopedBlocks >= 2
                && (f2.SpanReuseDisabledReasons & SpanReuseDisabledReason.Orphans) != 0 && fadeChanged,
                $"f2reused={f2.SpansReused} f3reused={f3.SpansReused} blocks={f2.ScopedBlocks} reasons={f2.SpanReuseDisabledReasons} fadeChanged={fadeChanged}");
            Check("gate.span.blockedNodeNeverStores",
                !storedA && !storedRoot && storedB,
                $"storedA={storedA} storedRoot={storedRoot} storedB={storedB} frame={frame2}");
        }

        // gate.span.detachedFlyScoped — a connected-anim fly reports its anchors via CollectReuseBlockRoots → the
        // recorder's reuseBlockRoots seam; the anchor's chain blocks but an unrelated subtree keeps reusing.
        {
            var s = new SceneStore();
            var root = s.CreateNode(1); s.Root = root;
            s.Bounds(root) = new RectF(0, 0, 240, 120);
            var branchA = SB(s, root, new RectF(0, 0, 120, 120), ColorF.FromRgba(0x10, 0x30, 0x50));
            var branchB = SB(s, root, new RectF(120, 0, 120, 120), ColorF.FromRgba(0x50, 0x30, 0x10));
            var anchor = SB(s, branchA, new RectF(10, 10, 80, 80), ColorF.FromRgba(0x90, 0x90, 0x90));

            var dl = new DrawList();
            var spans = new SpanTable();
            _ = SceneRecorder.Record(s, dl, spans: spans);
            s.ClearRecordDirty();

            Span<NodeHandle> flyRoots = stackalloc NodeHandle[1]; flyRoots[0] = anchor;
            var steady = SceneRecorder.Record(s, dl, spans: spans, reuseBlockRoots: flyRoots);
            uint frame = spans.CurrentFrameId;
            Check("gate.span.detachedFlyScoped",
                steady.SpansReused > 0 && steady.ScopedBlocks >= 3
                && (steady.SpanReuseDisabledReasons & SpanReuseDisabledReason.Detached) != 0
                && !spans.StoredAtFrame((int)branchA.Raw.Index, frame)
                && spans.StoredAtFrame((int)branchB.Raw.Index, frame),
                $"reused={steady.SpansReused} blocks={steady.ScopedBlocks} reasons={steady.SpanReuseDisabledReasons}");
        }

        static NodeHandle SB(SceneStore s, NodeHandle parent, RectF r, ColorF fill)
        {
            var n = s.CreateNode(1);
            s.AppendChild(parent, n);
            s.Bounds(n) = r;
            ref var p = ref s.Paint(n); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = fill;
            return n;
        }
    }
}
