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
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;




static class AnimSuite
{
    /// <summary>Test-only motion write for bare-<see cref="SceneStore"/> recorder gates: sets the viewport's RESULT
    /// columns (<c>ScrollState.Offset/Velocity/Motion</c> — in the host the ONE writer is <c>AppHost.RunScrollFrame</c>
    /// evaluating the plan) and re-poses the content node's transform exactly as the UI pose sink does, so a derived
    /// column (UserScrollActive / a moving Motion) flips for the recorder without a host. <paramref name="offsetY"/>
    /// overrides the offset (default: carry the current value through unchanged); <paramref name="liveSpeedDip"/> is the
    /// motion speed — a positive speed with <see cref="FluentGpu.Scroll.Motion.MotionKind.Idle"/> reads as a fling.</summary>
    static void TestApplyScroll(SceneStore s, NodeHandle viewport,
        FluentGpu.Scroll.Motion.MotionKind kind = FluentGpu.Scroll.Motion.MotionKind.Idle,
        float? liveSpeedDip = null,
        float? offsetY = null)
    {
        ref ScrollState sc = ref s.ScrollRef(viewport);
        if (offsetY is { } o) sc.Offset = o;
        float speed = liveSpeedDip ?? 0f;
        if (kind == FluentGpu.Scroll.Motion.MotionKind.Idle && speed > 0f) kind = FluentGpu.Scroll.Motion.MotionKind.Fling;
        if (kind != FluentGpu.Scroll.Motion.MotionKind.Idle && liveSpeedDip is null) speed = 1f;   // moving, speed unmeasured
        sc.Motion = kind == FluentGpu.Scroll.Motion.MotionKind.Idle
            ? FluentGpu.Scroll.Runtime.ScrollMotionState.Idle
            : new FluentGpu.Scroll.Runtime.ScrollMotionState(kind, speed, UserDriven: true);
        sc.Velocity = speed;
        var content = sc.ContentNode;
        bool horizontal = sc.Orientation == 1;
        float trans = FluentGpu.Scroll.Runtime.ScrollContentPose.Translate(sc.WindowOrigin, sc.Offset, 1f);
        float zoom = sc.ZoomFactor;
        if (!content.IsNull && s.IsLive(content))
        {
            ref NodePaint cp = ref s.Paint(content);
            FluentGpu.Scroll.Runtime.ScrollContentPose.WriteContentTransform(ref cp, in s.Bounds(content), horizontal, trans, zoom);
            s.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }

    sealed class NeverImageDecoder : IImageDecoder
    {
        public bool Begin(int id, string source, int targetW, int targetH,
                          ImagePriority priority = ImagePriority.Visible) => false;
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) { }
    }

    public static void Run(StringTable strings)
    {
        AnimChecks();
        NavigationSelectionChecks();
        ExpressiveMotionChecks(strings);
        SkeletonChecks(strings);
        ProjectionChecks(strings);
        EnterExitChecks(strings);
        FlipCellIdleChecks(strings);
        OrphanWakeChecks.Run(strings);
        SizeModeChecks(strings);
        ReflowChecks(strings);
        SkelReflowClipChecks(strings);
        ReflowRetargetChecks(strings);
        VirtualReflowExitChecks(strings);
        AnimRegressionChecks(strings);
        StyleChecks();
        ButtonAxesChecks();
        AnimValueChecks();
        CompositorChecks(strings);
        CleanSpanReuseChecks();
        SpanReuseScopingChecks();
        SpanTranslateRebaseChecks(strings);
        RecordDepthBudgetChecks(strings);
        AnimEngineChecks(strings);
        AnimHookChecks(strings);
        MarqueeChecks(strings);
        CrossfadeChecks(strings);
        NestedHoverBoundaryChecks(strings);
        TransparentHoverScopeChecks(strings);
        HoverFillScopeChecks(strings);
        WaveeSkeletonChecks(strings);
        BrushTransitionChecks(strings);
        AnimRestChecks(strings);
        WhileRestPoseChecks(strings);
        RotationCurrentValueChecks(strings);
        ScrollSnapChecks();
    }

    // gate.scroll.snapToDevicePixel (audit 2026-09-22, cause #1) — ScrollEffectEval.SnapToDevicePixel is the ONE snap
    // function the scroll pose (ScrollContentPose.Translate → the content's LocalTransform) runs the translation through, so glyphs (which already snap to a device row, GlyphRenderer.cs:1068-
    // 1083) and rects/hairlines/images (which previously landed at the raw fractional device position) agree on the
    // SAME grid instead of shimmering a fraction of a row apart. Pure/static, so this is a plain value-in/value-out
    // gate — no scene needed.
    static void ScrollSnapChecks()
    {
        bool RoundTrips(float offsetDip, float scale, float expectedDip)
            => Near(FluentGpu.Scroll.Effects.ScrollEffectEval.SnapToDevicePixel(offsetDip, scale), expectedDip, 1e-4f);

        // Identity at scale 1: a device pixel IS a DIP, so snapping to the nearest whole device pixel is snapping to
        // the nearest whole DIP.
        bool wholeScale1 = RoundTrips(3f, 1f, 3f) && RoundTrips(0f, 1f, 0f);
        bool roundsScale1 = RoundTrips(3.2f, 1f, 3f) && RoundTrips(3.6f, 1f, 4f);

        // Half-pixel ties: MathF.Round's default MidpointRounding.ToEven — 0.5 → 0 (even), 1.5 → 2 (even), and the
        // negative mirror rounds the same way in magnitude (ties-to-even is symmetric about zero).
        bool tiesToEven = RoundTrips(0.5f, 1f, 0f) && RoundTrips(1.5f, 1f, 2f)
                        && RoundTrips(-0.5f, 1f, 0f) && RoundTrips(-1.5f, 1f, -2f);

        // Negative offsets (a rubber-band overscroll past the top/left edge, or an RTL/horizontal axis in the
        // opposite direction) snap exactly like positive ones — same grid, mirrored.
        bool negative = RoundTrips(-3.2f, 1f, -3f) && RoundTrips(-3.6f, 1f, -4f);

        // Every DPI this product ships (100%/125%/150%/165%/200%): round(offset*scale)/scale lands on a multiple of
        // 1/scale DIP, which IS a whole device pixel at that scale.
        bool everyDpi = true;
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.65f, 2f })
        {
            float snapped = FluentGpu.Scroll.Effects.ScrollEffectEval.SnapToDevicePixel(37.3f, scale);
            float devicePx = snapped * scale;
            everyDpi &= Near(devicePx, MathF.Round(devicePx), 1e-3f);
        }

        // NaN/0/negative scale ⇒ identity (nothing sane to snap to — pass the value through unrounded rather than
        // produce NaN/Infinity or silently invert sign).
        bool zeroScaleIdentity = RoundTrips(3.7f, 0f, 3.7f);
        bool nanScaleIdentity = float.IsNaN(FluentGpu.Scroll.Effects.ScrollEffectEval.SnapToDevicePixel(3.7f, float.NaN)) == false
                                && RoundTrips(3.7f, float.NaN, 3.7f);
        bool negScaleIdentity = RoundTrips(3.7f, -2f, 3.7f);
        bool infScaleIdentity = RoundTrips(3.7f, float.PositiveInfinity, 3.7f);

        // NaN/Infinity offset ⇒ passthrough unrounded (nothing sane to snap an undefined position to).
        bool nanOffsetPassthrough = float.IsNaN(FluentGpu.Scroll.Effects.ScrollEffectEval.SnapToDevicePixel(float.NaN, 2f));
        bool infOffsetPassthrough = float.IsPositiveInfinity(FluentGpu.Scroll.Effects.ScrollEffectEval.SnapToDevicePixel(float.PositiveInfinity, 2f));

        Check("gate.scroll.snapToDevicePixel",
            wholeScale1 && roundsScale1 && tiesToEven && negative && everyDpi
            && zeroScaleIdentity && nanScaleIdentity && negScaleIdentity && infScaleIdentity
            && nanOffsetPassthrough && infOffsetPassthrough,
            $"wholeScale1={wholeScale1} roundsScale1={roundsScale1} tiesToEven={tiesToEven} negative={negative} "
            + $"everyDpi={everyDpi} zeroScaleIdentity={zeroScaleIdentity} nanScaleIdentity={nanScaleIdentity} "
            + $"negScaleIdentity={negScaleIdentity} infScaleIdentity={infScaleIdentity} "
            + $"nanOffsetPassthrough={nanOffsetPassthrough} infOffsetPassthrough={infOffsetPassthrough}");
    }

    static void AnimChecks()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        var engine = new AnimEngine(scene);

        scene.Paint(node).Opacity = 0f;
        scene.ClearFlagBits(node, NodeFlags.PaintDirty | NodeFlags.LayoutDirty | NodeFlags.TransformDirty);
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

        scene.ClearFlagBits(node, NodeFlags.TransformDirty | NodeFlags.LayoutDirty);
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

    // A lyric follow translates the scroll content just like a wheel scroll, but it is programmatic rather than direct
    // manipulation. It must invalidate spans (the entering text has moved) without downgrading a newly-emphasized DoF
    // layer to HoldIfCached: that policy renders a crisp fallback on a cache miss, which made every lyric line flash
    // active at the hand-off. A genuine user scroll still gets the hold policy.
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
            scene.ClearFlagBits(node, NodeFlags.PaintDirty | NodeFlags.LayoutDirty | NodeFlags.TransformDirty);
            engine.Animate(node, AnimChannel.BlurSigma, 8f, 0f, 100f, Easing.Linear);
            engine.Tick(0f);
            float b0 = scene.Paint(node).BlurSigma;
            engine.Tick(50f);
            float bMid = scene.Paint(node).BlurSigma;
            var fl = scene.Flags(node);
            bool midOk = Near(bMid, 4f, 0.2f)
                && (fl & NodeFlags.PaintDirty) != 0 && (fl & NodeFlags.LayoutDirty) == 0;
            engine.Tick(60f);   // > 100ms → complete
            float bEnd = scene.Paint(node).BlurSigma;
            bool doneOk = Near(bEnd, 0f, 1e-3f) && !engine.HasActive;
            Check("EM.b BlurSigma eases 8→0, marks PaintDirty (never LayoutDirty), and settles at 0",
                Near(b0, 8f, 0.1f) && midOk && doneOk,
                $"t0={b0:0.0} mid={bMid:0.0} end={bEnd:0.00} done={doneOk}");
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
            bool blurLayer = false; float sigma = 0f; RectF blurClip = default;
            foreach (var l in dev.LastLayers) if (l.Kind == (int)LayerKind.Blur)
            {
                blurLayer = true; sigma = l.BlurSigma; blurClip = l.CompositeClip;
            }
            bool balanced = dev.LayerBalance == 0;

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

            Check("EM.c recorder emits balanced PushLayer{Blur} carrying its clip; none for invisible/zero blur",
                blurLayer && Near(sigma, 6f, 0.01f) && activeClipCarried && balanced
                    && noBlurWhenZero && noBlurWhenInvisible,
                $"blurLayer={blurLayer} sigma={sigma:0.0} clip={blurClip} balanced={balanced} noneZero={noBlurWhenZero} noneInvisible={noBlurWhenInvisible}");
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

    // AppHost.ReclaimSettledOrphans is private; this mirrors it exactly (settled arm + the per-orphan animation-clock
    // deadline arm) so SK.b3 can drive the wedge guard headlessly. The global WALL backstop arm is not reachable here —
    // it is the outer guard for a host that stops painting, not the per-orphan deadline under test.
    // A once-per-second digit flip must not pin the host at PANEL rate. Measured on the real app: a daylist countdown
    // (FlipCountdown — a clipped cell whose single keyed child remounts each second, the old numeral exiting upward as an
    // orphan while the new one rises) left `animTracks=4 orphans=1` on EVERY memory sample for minutes, GPU flat at 10.0%
    // with 0.4% CPU on a page where one digit changes per second. 4 = Enter(Dy,Opacity) + Exit(Dy,Opacity) for one cell.
    // A live track means HasRenderMotion, which arms the render thread's display clock, which presents every vblank
    // FOREVER — 119 of every 120 presents redundant. The flip itself is 150 ms (MotionTok.ControlFast), so the tracks and
    // the orphan must be gone ~150 ms after the remount and the loop must go quiet until the next tick.
    static void FlipCellIdleChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("flipcell", new Size2(200, 120), 1f));
        window.Show();
        var root = new FlipCellProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 4; i++) host.RunFrame();

        // Baseline: a settled cell owes nothing.
        int settle0 = 0;
        for (; settle0 < 400 && host.HasActiveWork; settle0++) host.RunFrame();
        bool quietBefore = !host.HasActiveWork && host.Animation.TrackCount == 0 && host.Scene.OrphanCount == 0;

        root.Value.Value = 1;          // the tick: key mismatch ⇒ remount ⇒ exit orphan + enter/exit tracks
        host.RunFrame();
        bool armed = host.Animation.TrackCount > 0 || host.Scene.OrphanCount > 0;

        // 150 ms at the headless refresh period, plus generous slack. If the flip retires as it should the loop is quiet
        // long before this; if it wedges, HasActiveWork is still true at the end and the real app never idles.
        int settle = 0;
        for (; settle < 400 && host.HasActiveWork; settle++) host.RunFrame();
        bool quietAfter = !host.HasActiveWork;
        int tracks = host.Animation.TrackCount, orphans = host.Scene.OrphanCount;

        Check("gate.anim.flip-cell-idles a keyed remount with Enter/Exit (the countdown digit shape) retires its tracks AND its exit orphan when the 150ms flip ends, so the host goes fully idle between ticks instead of holding the compositor clock at panel rate forever",
            quietBefore && armed && quietAfter && tracks == 0 && orphans == 0,
            $"quietBefore={quietBefore} armed={armed} quietAfter={quietAfter} tracks={tracks} orphans={orphans} settleFrames={settle} (cap 400)");
    }

    static void RunHostOrphanReclaim(SceneStore scene, AnimEngine engine)
    {
        for (int i = scene.OrphanCount - 1; i >= 0;)
        {
            if (i >= scene.OrphanCount) { i = scene.OrphanCount - 1; continue; }
            var o = scene.OrphanAt(i, out _, out _);
            float own = scene.OrphanMaxAgeMs(i);
            if (!engine.HasTracks(o) || (own > 0f && scene.OrphanAnimAgeMs(i) >= own))
            {
                scene.ReclaimOrphan(o);
                i = Math.Min(i - 1, scene.OrphanCount - 1);
                continue;
            }
            i--;
        }
    }

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
            bool pulsing = engine.LoopCount >= 1;
            Check("SK.a skeleton derives N shimmer rows from the ONE row template (declared bar widths 24/48; no real text; pulsing)",
                shimmerRows == 5 && widths && noTextPending && pulsing,
                $"rows={shimmerRows} bar0={scene.Bounds(Child(scene, row0, 0)).W:0} bar2={scene.Bounds(Child(scene, row0, 2)).W:0} text={CountText(scene, region)} loops={engine.LoopCount}");

            tracks.SetReady(new[] { new SkTrack(1, "One", "1:01"), new SkTrack(2, "Two", "2:02"), new SkTrack(3, "Three", "3:03") });
            recon.Runtime.Flush();
            new FlexLayout(scene, fonts).Run(scene.Root);
            bool realText = CountText(scene, region) > 0;
            // The shimmer is NOT gone on the swap frame: it exit-orphans (detached from topology, still LIVE and drawing
            // UNDER the live children) so it cross-dissolves with the revealing content instead of dipping to empty.
            bool shimmerOrphaned = scene.IsOrphan(shimmer) && scene.IsLive(shimmer) && scene.OrphanCount == 1;
            engine.Tick(0f);
            var realRow0 = Child(scene, Child(scene, region, 0), 0);
            bool revealSeeded = engine.TryGetTrackValue(realRow0, AnimChannel.Opacity, out var op0) && op0 < 0.2f;
            Check("SK.b Pending→Ready swaps shimmer→real (text appears) and blur-reveals the rows",
                realText && revealSeeded, $"realText={realText} revealOp0={op0:0.00}");

            // SK.b2 — the CROSS-DISSOLVE contract (the shimmer→blank→content dip this gate exists to forbid): on the swap
            // frame the shimmer root is an exit orphan carrying an Opacity track heading to 0, while the real root's
            // reveal fades UP over it. Ticking settles both; the orphan then reclaims and no loop track survives.
            bool shimmerTrack = engine.TryGetTrackValue(shimmer, AnimChannel.Opacity, out var sop0);
            engine.Tick(16f); engine.Tick(16f);
            bool shimmerFadingOut = engine.TryGetTrackValue(shimmer, AnimChannel.Opacity, out var sop1) && sop1 < sop0 - 0.001f;
            bool contentFadingUp = engine.TryGetTrackValue(realRow0, AnimChannel.Opacity, out var op1) && op1 > op0;
            Check("SK.b2 the swap frame CROSS-DISSOLVES: the shimmer root is a live exit orphan fading to 0 while the real root fades up (no empty frame)",
                shimmerOrphaned && shimmerTrack && shimmerFadingOut && contentFadingUp,
                $"orphan={shimmerOrphaned} track={shimmerTrack} shimmer {sop0:0.00}→{sop1:0.00} content {op0:0.00}→{op1:0.00}");

            int settledAt = -1;
            for (int i = 0; i < 120 && settledAt < 0; i++)
            {
                engine.Tick(16f);
                for (int k = scene.OrphanCount - 1; k >= 0; k--)        // host's ReclaimSettledOrphans (settled arm)
                { var o = scene.OrphanAt(k, out _, out _); if (!engine.HasTracks(o)) scene.ReclaimOrphan(o); }
                if (scene.OrphanCount == 0 && !engine.HasActive) settledAt = i;
            }
            Check("SK.c the looping skeleton pulse is cancelled on swap and the shimmer orphan reclaims (nothing pins the wake loop)",
                engine.LoopCount == 0 && scene.OrphanCount == 0 && !scene.IsLive(shimmer) && settledAt >= 0,
                $"loops={engine.LoopCount} active={engine.HasActive} orphans={scene.OrphanCount} shimmerLive={scene.IsLive(shimmer)} settled@{settledAt}");
        }

        // SK.b3 — per-orphan hard deadline (the wedge guard that makes the cross-dissolve safe): an exit orphan whose
        // track NEVER settles is force-reclaimed once its OWN SceneStore.OrphanMaxAgeMs passes, so a wedged page-sized
        // shimmer can't paint half-faded over live content (the historical "half resolved page"). An orphan enqueued
        // WITHOUT a deadline still waits for the host's global 2s backstop. Mirrors AppHost.ReclaimSettledOrphans (private).
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var root = scene.CreateNode(1); scene.Root = root;
            scene.Bounds(root) = new RectF(0, 0, 200, 100);
            var wedged = scene.CreateNode(1); scene.AppendChild(root, wedged); scene.Bounds(wedged) = new RectF(0, 0, 100, 40);
            var patient = scene.CreateNode(1); scene.AppendChild(root, patient); scene.Bounds(patient) = new RectF(0, 40, 100, 40);
            engine.SkeletonPulse(wedged);    // a LOOPING track = one that never settles (the wedge this gate simulates)
            engine.SkeletonPulse(patient);
            scene.AnimClockMs = 1_000d;             // the host publishes the animation timebase; the stamp is taken from it
            scene.Orphan(wedged, maxAgeMs: 350f);   // its own deadline (the reconciler passes duration + delay + slack)
            scene.Orphan(patient);                  // 0 ⇒ only the host's global wall backstop governs it
            bool setUp = scene.OrphanCount == 2 && engine.HasTracks(wedged) && engine.HasTracks(patient)
                && scene.OrphanMaxAgeMs(0) > 0f && scene.OrphanMaxAgeMs(1) == 0f
                && scene.OrphanAnimAgeMs(0) == 0d;

            // 340ms of ANIMATION time: still inside the 350ms deadline, so a mid-fade orphan is NOT dropped. (Deliberately
            // the anim clock and not the wall clock — a wall-measured deadline would fire here on any slow frame.)
            scene.AnimClockMs += 340d;
            RunHostOrphanReclaim(scene, engine);
            bool heldMidFade = scene.OrphanCount == 2;

            scene.AnimClockMs += 20d;   // 360ms > 350ms ⇒ the wedged one's own deadline has passed
            RunHostOrphanReclaim(scene, engine);

            Check("SK.b3 a wedged exit orphan is force-reclaimed once its OWN maxAge of ANIMATION time passes (not before); one without a deadline still waits for the global wall backstop",
                setUp && heldMidFade && !scene.IsLive(wedged) && scene.IsLive(patient) && scene.IsOrphan(patient) && scene.OrphanCount == 1,
                $"setUp={setUp} heldMidFade={heldMidFade} wedgedLive={scene.IsLive(wedged)} patientOrphan={scene.IsOrphan(patient)} orphans={scene.OrphanCount}");
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
                bool noPulse = engine.LoopCount == 0;
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

        // CF.a connected-fly rebuild (ConnectedAnimation.DetachedFly): SceneRecorder.RecordDetached draws a DetachedAnimSlab snapshot as
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

        // SK.j2 nested region: a whole-page content(seed) can contain a streaming list whose own row source must remain
        // representative under the outer derivation.
        {
            // A derived whole-page seed may contain its own streaming-list region. The outer derivation must walk the
            // nested region's real row source instead of replacing that entire list with one opaque fallback bar.
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var page = Loadable<int>.Pending(1);
            var rows = Loadable<int>.Pending(0);
            var rowSeed = new Element[4];
            for (int i = 0; i < rowSeed.Length; i++) rowSeed[i] = SkRow(null);
            recon.ReconcileRoot(
                Skel.Region(page, _ => new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new TextEl("Heading") { Size = 24f },
                        Skel.Region(rows,
                            shimmerSource: () => new BoxEl { Direction = 1, Children = rowSeed },
                            content: _ => new BoxEl()),
                    ],
                }),
                null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var derivedPage = Child(scene, scene.Root, 0);
            var derivedRows = Child(scene, derivedPage, 1);
            int rowCount = 0;
            for (var c = scene.FirstChild(derivedRows); !c.IsNull; c = scene.NextSibling(c)) rowCount++;
            Check("SK.j2 content(seed) derives through a nested Skel.Region using its real row source",
                rowCount == 4 && CountText(scene, scene.Root) == 0,
                $"rows={rowCount} text={CountText(scene, scene.Root)}");
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
            scene.ClearFlagBits(n, NodeFlags.TransformDirty | NodeFlags.LayoutDirty);
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
            scene.ClearFlagBits(n, NodeFlags.LayoutDirty);
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

        // 23e1 — asymmetric presence delay: a shy overlay may wait before entering, but must start dismissing
        // immediately. ExitDelayMs=0 overrides the entrance DelayMs without changing legacy specs where it is null.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            var presence = new LayoutTransition(
                TransitionChannels.Opacity,
                TransitionDynamics.Tween(160f, Easing.Linear),
                Enter: new EnterExit(Opacity: 0f, Active: true),
                Exit: new EnterExit(Opacity: 0f, Active: true),
                DelayMs: 240f,
                ExitDelayMs: 0f);
            Element Tree(bool present) => new BoxEl
            {
                Width = 100, Height = 100,
                Children = present ? [new BoxEl { Key = "shy", Width = 50, Height = 20, Animate = presence }] : [],
            };
            var old = Tree(true);
            recon.ReconcileRoot(old, null);
            new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
            var child = Child(scene, scene.Root, 0);
            recon.ReconcileRoot(Tree(false), old);
            engine.Tick(16f); engine.Tick(32f);
            float opacity = scene.Paint(child).Opacity;
            Check("23e1. ExitDelayMs overrides a delayed entrance so a shy overlay dismisses immediately",
                scene.IsOrphan(child) && opacity < 0.99f, $"orphan={scene.IsOrphan(child)} opacity={opacity:0.00}");
        }

        // 23e1b — a local shared-layout fly carries the effective ancestor crop into its overlay, interpolates the clip
        // with the same curve, and reverses from the currently presented crop. Reapplying an unchanged tag cull is clean.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var images = new ImageCache(new NeverImageDecoder());
            var connected = new ConnectedAnimation(scene, engine, images);
            var image = images.Request("clip-flight", 100, 100);

            scene.Root = scene.CreateNode(1);
            scene.Bounds(scene.Root) = new RectF(0f, 0f, 640f, 480f);
            var clip = scene.CreateNode(1);
            var source = scene.CreateNode(8);
            var dest = scene.CreateNode(8);
            scene.AppendChild(scene.Root, clip);
            scene.AppendChild(clip, source);
            scene.AppendChild(scene.Root, dest);
            scene.Bounds(clip) = new RectF(20f, 20f, 200f, 200f);
            scene.Paint(clip).PresentedH = 80f;
            scene.SetFlagBits(clip, NodeFlags.Visible | NodeFlags.ClipsToBounds);
            scene.Bounds(source) = new RectF(0f, 20f, 100f, 100f);
            scene.SetFlagBits(source, NodeFlags.Visible);
            scene.Paint(source).VisualKind = VisualKind.Image;
            scene.Paint(source).ImageId = image.Id;
            scene.Bounds(dest) = new RectF(300f, 30f, 36f, 36f);
            scene.SetFlagBits(dest, NodeFlags.Visible);
            scene.Paint(dest).VisualKind = VisualKind.Image;
            scene.Paint(dest).ImageId = image.Id;

            const string key = "local-cover";
            connected.NoteTagged(source, key);
            connected.NoteTagged(dest, key);
            var request = new ConnectedTransitionRequest(
                key,
                ConnectedMotion.Eased(EasingSpec.CubicBezier(0.8f, 0f, 0.2f, 1f), 480f),
                PreserveSourceOpacity: true,
                EnableClipAnimation: true);
            connected.Begin(request);
            connected.Tick65();

            var overlay = scene.OverlayAt(0);
            RectF startClip = scene.Paint(overlay).ClipRect;
            bool sourceCropCaptured = scene.OverlayCount == 1
                && Near(startClip.X, 0f, 0.05f)
                && Near(startClip.Y, 0f, 0.05f)
                && Near(startClip.W, 36f, 0.05f)
                && Near(startClip.H, 21.6f, 0.15f);

            engine.Tick(0f);
            engine.Tick(160f);
            RectF midClip = scene.Paint(overlay).ClipRect;
            bool clipInterpolates = midClip.H > startClip.H && midClip.H < 36f;

            static RectF PresentedClip(SceneStore s, NodeHandle node)
            {
                ref RectF b = ref s.Bounds(node);
                ref NodePaint p = ref s.Paint(node);
                float w = b.W * p.LocalTransform.M11, h = b.H * p.LocalTransform.M22;
                float x = b.X + (b.W - w) * 0.5f + p.LocalTransform.Dx;
                float y = b.Y + (b.H - h) * 0.5f + p.LocalTransform.Dy;
                RectF full = new(x, y, w, h);
                if (p.ClipRect.IsInfinite) return full;
                return new RectF(
                    x + p.ClipRect.X * p.LocalTransform.M11,
                    y + p.ClipRect.Y * p.LocalTransform.M22,
                    p.ClipRect.W * p.LocalTransform.M11,
                    p.ClipRect.H * p.LocalTransform.M22);
            }

            RectF beforeReverse = PresentedClip(scene, overlay);
            connected.Begin(request);
            connected.Tick65();
            var reverseOverlay = scene.OverlayAt(0);
            RectF afterReverse = PresentedClip(scene, reverseOverlay);
            bool reverseContinuous = Near(beforeReverse.X, afterReverse.X, 0.25f)
                && Near(beforeReverse.Y, afterReverse.Y, 0.25f)
                && Near(beforeReverse.W, afterReverse.W, 0.25f)
                && Near(beforeReverse.H, afterReverse.H, 0.25f);

            scene.ClearRecordDirty();
            connected.Tick65();
            bool stableCullIsClean = !scene.AnyRecordDirty;

            Check("23e1b. connected clip captures ancestor crop, interpolates, reverses continuously, and equality-gates tag culls",
                sourceCropCaptured && clipInterpolates && reverseContinuous && stableCullIsClean,
                $"start={startClip} mid={midClip} before={beforeReverse} after={afterReverse} clean={stableCullIsClean}");
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
            scene.ClearFlagBits(n, NodeFlags.LayoutDirty);
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

        // 23u — CSS position:sticky (an Element .Sticky() effect): the header scrolls normally, PINS at the viewport top while
        // its parent card is in view (hit-test follows — AbsoluteRect includes the pin transform), CLAMPS at the
        // card's end (never escapes its containing block), and releases on scroll-back. There is no OnFlag callback
        // any more (scroll rework) — pin state is asserted directly from `NodeFlags.StickyPinned`.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sticky", new Size2(320, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            NodeHandle headerN = NodeHandle.Null;
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
                                new BoxEl { Height = 40f, OnRealized = h => headerN = h }.Sticky(0f),
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
                // An immediate plan on the viewport's handle; the frame below evaluates it (result columns + the
                // posed content transform) before the pass reads geometry.
                host.TryGetScrollHandle(vp)?.ScrollTo(y, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
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
            // Reconcile/layout may restore the element's literal transform before the next pin pass while the retained
            // StickyPinned bit still describes the previous frame. Release must derive from current geometry, not from
            // whether ApplyPin also had to change LocalTransform.Dy.
            s.Paint(headerN).LocalTransform = Affine2D.Identity;
            s.Mark(headerN, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            ScrollTo(0f);     // released, back at its natural slot
            bool releasedFlag = (s.Flags(headerN) & NodeFlags.StickyPinned) == 0;
            float releasedY = s.AbsoluteRect(headerN).Y;
            Check("23u. position:sticky — pins at viewport top, clamps at the card's end, releases even when reconcile already restored identity",
                pinnedNow && Near(pinnedY, vpTop, 0.5f)
                && stillPinned && Near(clampedY, vpTop - 20f, 0.5f)
                && releasedFlag && Near(releasedY, restY, 0.5f),
                $"restY={restY:0} pinnedY={pinnedY:0} (vpTop={vpTop:0}) clampedY={clampedY:0} releasedY={releasedY:0}");
        }

        // 23u3 — sticky clip-top (.StickyClip(), the paint dual of the 23u pin): the body's
        // ClipRect.top rides the viewport-anchored line (viewport top + inset) 1:1 with the offset while engaged,
        // and releases back to the Infinite sentinel when the line sits above the body — the mechanism that keeps
        // the page backdrop (not the cards) behind a pinned section header. There is no OnFlag callback any more
        // (scroll rework); the engage/release edges are asserted directly from ClipRect.IsInfinite.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sticky-clip", new Size2(320, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            NodeHandle bodyN = NodeHandle.Null;
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
                                new BoxEl { Height = 40f }.Sticky(0f),   // the pinned header
                                new BoxEl                                                              // the section body
                                {
                                    Height = 400f,
                                    OnRealized = h => bodyN = h,
                                }.StickyClip(40f),
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
                // An immediate plan on the viewport's handle; the frame below evaluates it (result columns + the
                // posed content transform) before the pass reads geometry.
                host.TryGetScrollHandle(vp)?.ScrollTo(y, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
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
            Check("23u3. sticky clip-top — body ClipRect.top rides the viewport line 1:1, releases above the body",
                restReleased && Near(clipA, 150f, 0.5f) && Near(clipB - clipA, 1f, 0.1f)
                && releasedMid,
                $"rest={restReleased} clipA={clipA:0.#} clipB={clipB:0.#} releasedMid={releasedMid}");

            // Fully-hidden freeze: once the sticky line is past the body bottom, ClipRect.Y locks at Bounds.H so
            // further offset advances do not keep rewriting / dirtying the node (playlist overscan hitch).
            ScrollTo(600f);   // line at 640 → body-local top = 500 ≥ body H 400
            float frozenA = s.Paint(bodyN).ClipRect.Y;
            s.Unmark(bodyN, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            ScrollTo(620f);
            float frozenB = s.Paint(bodyN).ClipRect.Y;
            bool stayedClean = (s.Flags(bodyN) & (NodeFlags.TransformDirty | NodeFlags.PaintDirty)) == 0;
            Check("23u3b. sticky clip-top fully-hidden freezes ClipRect.Y at Bounds.H — further offset does not re-Mark",
                Near(frozenA, 400f, 0.5f) && Near(frozenB, frozenA, 0.01f) && stayedClean,
                $"frozenA={frozenA:0.#} frozenB={frozenB:0.#} stayedClean={stayedClean}");
        }

        // 23u3c — the sticky clip's INPUT dual. The clip is what lets a pinned band paint NOTHING and show the page's
        // real ground; the guillotined content is by construction a LATER sibling than the band (both hit walks keep
        // the LAST matching child), so without an input gate the invisible rows above the cut win every click aimed at
        // the chrome. A point inside the band must resolve the band's own button; a point below the cut must still
        // resolve the row it is over.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sticky-clip-input", new Size2(320, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            int bandClicks = 0, rowClicks = 0;
            NodeHandle bandBtn = NodeHandle.Null, rowN = NodeHandle.Null;
            var root = new W0fStaticProbe
            {
                Build = () => ScrollView(new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        // The pinned band — FIRST sibling, exactly like ArtistPage's hero and DetailTracks' chrome.
                        new BoxEl
                        {
                            Height = 40f,
                            Children = [ new BoxEl { Height = 40f, Width = 320f, OnClick = () => bandClicks++,
                                                     OnRealized = h => bandBtn = h } ],
                        }.Sticky(0f),
                        // The scrolled body — LAST sibling, clipped at the band's lower edge.
                        new BoxEl
                        {
                            Direction = 1,
                            Children =
                            [
                                new BoxEl { Height = 300f, Width = 320f, OnClick = () => rowClicks++,
                                            OnRealized = h => rowN = h },
                                new BoxEl { Height = 600f },
                            ],
                        }.StickyClip(40f),
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
                // An immediate plan on the viewport's handle; the frame below evaluates it (result columns + the
                // posed content transform) before the pass reads geometry.
                host.TryGetScrollHandle(vp)?.ScrollTo(y, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
                host.RunFrame();
            }
            ScrollTo(120f);                                    // body top (40) is well above the line (40) → clipped
            var disp = new InputDispatcher(s);
            var inBand = disp.HitTest(new Point2(160f, 20f));  // over the band, and over the (clipped) first row
            var belowCut = disp.HitTest(new Point2(160f, 80f)); // under the cut — the row is live there
            bool clipEngaged = !s.Paint(s.Parent(rowN)).ClipRect.IsInfinite;
            Check("23u3c. the sticky clip gates INPUT as well as paint — a pinned band's clicks are not stolen by the content guillotined above the cut",
                clipEngaged && inBand.Raw.Index == bandBtn.Raw.Index && belowCut.Raw.Index == rowN.Raw.Index,
                $"clipEngaged={clipEngaged} inBand={inBand.Raw.Index} band={bandBtn.Raw.Index} belowCut={belowCut.Raw.Index} row={rowN.Raw.Index}");
        }

        // 23u2 (DELETED, scroll rework) — "trailing-anchored presented height" drove NodePaint.PresentedH/ChildShiftY
        // straight from scroll offset via a ScrollBinds entry (`From = ScrollChannel.Offset, To =
        // BindSink.PresentedHTrailing`). ScrollChannel/BindSink/the generic ScrollBind map-to-arbitrary-paint-field
        // DSL are gone; the new `FluentGpu.Scroll.Effects.ScrollEffect` only writes TransY/Opacity/ClipTop/ScaleXY/
        // ThumbPos (Sticky/StickyClip/Parallax/Fade/Scale/Thumb) — there is no channel that reaches PresentedH. The
        // subject (a scroll-scrubbed presented-height collapse) has no replacement API, so this gate is deleted
        // rather than ported.

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
            // scroll-v3: OffsetY/TargetY are gone as pokeable columns — post an immediate ScrollTo to the handle.
            host.TryGetScrollHandle(Find(s.Root))?.ScrollTo(400f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            host.RunFrame();
            root.Mounted.Value = false; host.RunFrame();          // unmount → SaveScroll caches "A"=400
            root.Mounted.Value = true;  host.RunFrame();          // cold remount → seed BEFORE the first realize
            var restoredNode = Find(s.Root);
            ref ScrollState ra = ref s.ScrollRef(restoredNode);
            float restoredOffset = ra.OffsetY; int restoredFirst = ra.FirstRealized;
            // RestorePending is the handle's own state now (ScrollHandle.RestorePending, design §9) — not a scene column.
            bool noPending = !(host.TryGetScrollHandle(restoredNode)?.RestorePending ?? false);
            Check("scroll-restore.cold-seed: a cold remount seeds the saved offset on the FIRST realized window (no scroll-to-top flash)",
                Near(restoredOffset, 400f, 1f) && restoredFirst > 0 && noPending,
                $"offset={restoredOffset:0} firstRealized={restoredFirst} pending={!noPending}");

            // Switch the ScrollKey on the reused viewport: new content starts at the top; the old content's offset is saved.
            root.Key.Value = "B"; host.RunFrame();
            float bTop = s.ScrollRef(Find(s.Root)).OffsetY;
            host.TryGetScrollHandle(Find(s.Root))?.ScrollTo(600f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            host.RunFrame();
            root.Key.Value = "A"; host.RunFrame();                // back to A → restore 400
            float aBack = s.ScrollRef(Find(s.Root)).OffsetY;
            Check("scroll-restore.key-isolation: a different ScrollKey starts at the top; returning restores the prior content's offset",
                Near(bTop, 0f, 1f) && Near(aBack, 400f, 1f),
                $"newKeyTop={bTop:0} restoredPrev={aBack:0}");
        }

        // 23x — the OTHER swap shape: same content (same ScrollKey), but the viewport NODE is replaced because an
        // ancestor was re-keyed. ReconcileChildren mounts new keyed children before removing the old ones, and the
        // outgoing offset is only persisted on the way out (Remove → UnmountSubtree → SaveScroll) — so the incoming
        // viewport used to read ScrollMemory before that write landed and fell back to the top. Re-keying twice then
        // ping-ponged between two stale offsets. Reconciler.PreSaveScroll persists the departing subtree's offsets
        // before anything mounts, which is what makes this pass without touching node lifecycle ordering.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("scrollkeyedswap", new Size2(360, 260), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new ScrollKeyedSwapProbe();
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
            var before = Find(s.Root);
            // scroll-v3: OffsetY/TargetY are gone as pokeable columns — post an immediate ScrollTo to the kernel.
            host.TryGetScrollHandle(before)?.ScrollTo(400f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            host.RunFrame();

            root.WrapperKey.Value = 1; host.RunFrame();   // re-key the ANCESTOR → the list remounts onto a new viewport
            var after = Find(s.Root);
            ref ScrollState sa = ref s.ScrollRef(after);
            bool newNode = !after.Equals(before);
            Check("scroll-restore.keyed-swap: re-keying an ANCESTOR remounts the viewport, and the same-ScrollKey content keeps its offset on the first realized window (the outgoing offset is saved before the incoming mount reads it)",
                newNode && Near(sa.OffsetY, 400f, 1f) && sa.FirstRealized > 0,
                $"newViewport={newNode} offset={sa.OffsetY:0} firstRealized={sa.FirstRealized}");
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

        // An already-resident source still benefits from UI virtualization, but it must never enter the remote paging
        // lifecycle. Replacing the snapshot preserves the mounted collection identity and emits one surgical version edge.
        {
            int[] first = [10, 20, 30, 40];
            var resident = VirtualCollection<int>.FromSnapshot(first);
            int v0 = resident.Version.Peek();
            resident.EnsureRange(0, 1000);
            bool initial = resident.Count == first.Length && resident.PageSize == 1
                           && resident.IsLoaded(0) && resident.IsLoaded(3) && resident[2] == 30
                           && !resident.IsLoaded(4) && resident.Version.Peek() == v0;

            resident.ReplaceSnapshot(first);
            bool coalesced = resident.Version.Peek() == v0;
            int[] second = [7, 8, 9];
            resident.ReplaceSnapshot(second);
            bool replaced = resident.Count == second.Length && resident[0] == 7 && resident[2] == 9
                            && resident.Version.Peek() == v0 + 1;

            Check("virtual-collection.snapshot: resident data is fetch-free, fully loaded and replaceable in place",
                initial && coalesced && replaced,
                $"initial={initial} coalesced={coalesced} replaced={replaced} version={resident.Version.Peek()}");
        }

        // lazy-grid paging runs passively: a cache-backed page can complete synchronously and bump Version. Dispatching
        // that request from Render would write the very signal the count delegate just read (a backwards-write loop).
        {
            bool prevEnabled = BackwardsWriteGuard.Enabled, prevThrow = BackwardsWriteGuard.ThrowOnViolation;
            BackwardsWriteGuard.Enabled = BackwardsWriteGuard.CompiledIn;
            BackwardsWriteGuard.ThrowOnViolation = false;
            BackwardsWriteGuard.Reset();
            try
            {
                var data = new int[120];
                for (int i = 0; i < data.Length; i++) data[i] = i;
                int fetches = 0;
                var vc = new VirtualCollection<int>((off, cnt, ct) =>
                {
                    fetches++;
                    return new ValueTask<PageResult<int>>(
                        new PageResult<int>(data.Length, data.AsMemory(off, Math.Min(cnt, data.Length - off))));
                }, pageSize: 30);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("lazy-grid-passive-page", new Size2(640, 480), 1f));
                window.Show();
                var root = new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 620f,
                        Children =
                        [
                            Embed.Comp(() => new LazyGrid(
                                count: () => { _ = vc.Version.Value; return vc.CountOr0; },
                                cell: (i, _) => new BoxEl { Height = 40f },
                                ensureRange: (first, lastExclusive) => vc.EnsureRange(first, lastExclusive - 1),
                                minColWidth: 180f, rowExtra: 40f, overscanRows: 2)),
                        ],
                    },
                };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
                host.RunFrame();
                host.RunFrame();
                host.RunFrame();

                Check("lazy-grid.passive-page: synchronous paging runs after Render and trips no backwards-write",
                    vc.Count == data.Length && fetches >= 1 && BackwardsWriteGuard.Violations == 0,
                    $"count={vc.Count} fetches={fetches} backWrites={BackwardsWriteGuard.Violations} [{BackwardsWriteGuard.LastViolation}]");
            }
            finally
            {
                BackwardsWriteGuard.Enabled = prevEnabled;
                BackwardsWriteGuard.ThrowOnViolation = prevThrow;
            }
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

            // Card+peek reveal (NOT whole-block reveal, and never pin-to-top): keep the current offset whenever the
            // card already sits below the sticky inset AND a drawerPeek of drawer already clears the viewport bottom.
            // A full 10-row drawer (56 + 10*44 + 30 = 526) plus a 320 card is taller than the band left under a 96
            // sticky inset, so "reveal the whole block" had no solution and degenerated to pin-to-top.
            const float off = 700f, viewH = 800f, inset = 96f, peek = 144f, cardH = 320f, midTop = 900f, tallDrawer = 526f;
            float placed = LazyGridMath.MinRevealTarget(off, viewH, midTop, cardH, tallDrawer, inset, peek);
            float underSticky = LazyGridMath.MinRevealTarget(off, viewH, 740f, cardH, tallDrawer, inset, peek);
            float pastBottom = LazyGridMath.MinRevealTarget(off, viewH, 1400f, cardH, tallDrawer, inset, peek);
            float sameShort = LazyGridMath.MinRevealTarget(off, viewH, midTop, cardH, 218f, inset, peek);
            float sameTall = LazyGridMath.MinRevealTarget(off, viewH, midTop, cardH, tallDrawer, inset, peek);
            float tinyDrawer = LazyGridMath.MinRevealTarget(off, viewH, midTop, cardH, 86f, inset, peek);
            float noSlack = LazyGridMath.MinRevealTarget(off, viewH, midTop, 700f, tallDrawer, inset, peek);
            float noGeom = LazyGridMath.MinRevealTarget(off, 0f, midTop, cardH, tallDrawer, inset, peek);
            bool bring = Near(placed, off, 0.5f)                 // mid-viewport expand → do not move
                         && Near(underSticky, 644f, 0.5f)        // cardTop − inset
                         && Near(pastBottom, 1064f, 0.5f)        // cardTop + cardH + peek − viewH (NOT 1304 = whole block)
                         && Near(sameShort, off, 0.5f) && Near(sameTall, off, 0.5f)   // short→tall same row → still still
                         && Near(tinyDrawer, off, 0.5f)          // peek saturates at drawerH
                         && Near(noSlack, 804f, 0.5f)            // card taller than the band → leading wins
                         && Near(noGeom, off, 0.5f);             // viewportH unknown → never jump to 0

            var exactTop = LazyGridMath.VisibleRange(0f, vh, rowH, 20, 2, -1, 0f);
            var exactMid = LazyGridMath.VisibleRange(1000f, vh, rowH, 20, 2, -1, 0f);
            var exactDrawer = LazyGridMath.VisibleRange(650f, 300f, rowH, 20, 2, 2, 300f);
            bool exact = exactTop == new LazyGridVisibleRange(0, 4, 2)
                         && exactMid == new LazyGridVisibleRange(10, 14, 2)
                         && exactDrawer == new LazyGridVisibleRange(4, 8, 2);

            // AlignRowTarget — ExpandedReveal.AlignTop: always the SAME landing spot (cardTop - inset), unlike
            // MinRevealTarget's stay-put minimalism above; a 0/unresolved viewport never jumps; a card above the
            // inset still clamps to the 0 floor rather than going negative.
            float aligned = LazyGridMath.AlignRowTarget(off, viewH, 900f, 96f);
            float alignedNoGeom = LazyGridMath.AlignRowTarget(off, 0f, 900f, 96f);
            float alignedFloor = LazyGridMath.AlignRowTarget(off, viewH, 50f, 96f);
            bool align = Near(aligned, 804f, 0.5f)            // cardTop − inset
                         && Near(alignedNoGeom, off, 0.5f)     // viewportH unknown → never jump
                         && Near(alignedFloor, 0f, 0.5f);      // cardTop − inset < 0 → clamp to 0

            Check("lazy-grid: window covers the viewport, spacers reserve the exact extent, expand reveals card+drawer-peek (never pin-to-top), and AlignTop always lands the row at cardTop-inset",
                atTop && mid && largerOverscan && drawerIn && drawerAbove && atEnd && bring && exact && align,
                $"top=({v0.FirstRow},{v0.LastRow},pad{v0.TopPad:0}) mid=({v1.FirstRow},{v1.LastRow}) ahead=({vAhead.FirstRow},{vAhead.LastRow}) drawerAboveTopPad={v3.TopPad:0} endLast={vEnd.LastRow} bring placed={placed:0} sticky={underSticky:0} bottom={pastBottom:0} same={sameShort:0}/{sameTall:0} tiny={tinyDrawer:0} noSlack={noSlack:0} noGeom={noGeom:0} exact={exactTop}/{exactMid}/{exactDrawer} align aligned={aligned:0} noGeom={alignedNoGeom:0} floor={alignedFloor:0}");
        }

        // A flat grid uses the same exact extent at every window boundary. This is the regression for stacked artist
        // facets: moving realization from the first row to the last may replace cells, but never changes page geometry.
        {
            const float rowH = 100f, vh = 320f, drawerH = 260f;
            const int totalRows = 73, expandedRow = 14;
            float expected = totalRows * rowH + drawerH;
            bool extent = true, monotone = true, covers = true;
            int previousFirst = -1;
            for (float y = -600f; y <= expected + 600f; y += 7f)
            {
                var view = LazyGridMath.Compute(y, vh, rowH, totalRows, 2, expandedRow, drawerH);
                float block = (view.LastRow - view.FirstRow + 1) * rowH
                              + (view.DrawerVisible ? drawerH : 0f);
                extent &= Near(view.TopPad + block + view.BottomPad, expected, 0.01f);
                monotone &= view.FirstRow >= previousFirst;
                covers &= view.LastRow >= view.FirstRow;
                previousFirst = view.FirstRow;
            }

            _ = LazyGridMath.Compute(500f, vh, rowH, totalRows, 2, expandedRow, drawerH);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
                _ = LazyGridMath.Compute(i * 7f, vh, rowH, totalRows, 2, expandedRow, drawerH);
            long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;

            Check("lazy-grid.flat-sweep: every realization window preserves one monotone exact extent",
                extent && monotone && covers && alloc == 0,
                $"extent={extent} monotone={monotone} covers={covers} alloc={alloc} first={previousFirst}");
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

    // 23r.c — the REAL Skel shape (Wavee's empty-Search page), which 23r.a/b/23x structurally cannot see: ReflowProbe
    // declares ClipToBounds AND a Trailing anchor, so its wrapper never overpaints. A `Skel.Region` gets NEITHER — the
    // reconciler only marks it BoundsAnimated + SizeMode.Reflow — and its Pending branch here is a literally EMPTY box,
    // so the region reflows from ZERO while its Ready content is arranged at full natural height. Unclipped, the whole
    // region paints over the sibling below it. Wrapped in an Embed.Comp anchor, MirrorParticipation would additionally
    // snapshot the EASED height onto that anchor as a hard declared size that SettleRestore never restores — turning a
    // ~250ms flash into a permanent freeze. All four assertions below are that bug.
    static void SkelReflowClipChecks(StringTable strings)
    {
        SkelOverpaintProbe.Data = Loadable<int>.Pending(0);
        SkelOverpaintProbe.Bump.Value = 0;
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("skelreflow", new Size2(360, 520), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new SkelOverpaintProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        var s = host.Scene;

        host.RunFrame();                                     // mount PENDING: the shimmer is an empty box (height 0)
        var anchorNode = Child(s, s.Root, 0);                // the Embed.Comp component anchor (layout-transparent)
        var sibling = Child(s, s.Root, 1);
        var region = FindSkelRegion(s, anchorNode);
        bool found = !region.IsNull && !sibling.IsNull;
        bool authorClip = found && (s.Flags(region) & NodeFlags.ClipsToBounds) != 0;   // must be FALSE: nobody declared it

        SkelOverpaintProbe.Data.SetReady(1);                 // Pending → Ready: the region eases 0 → ContentH
        host.RunFrame();                                     // the swap commits; the host seeds the reflow track

        // Mid-flight. Every sample RE-RENDERS the wrapping component first (Bump), so MirrorParticipation runs while the
        // reflow is live — the exact window in which the anchor used to snapshot the EASED height as a hard declared
        // size that nothing ever restores. Each sample must: still be easing, be CLIPPED (else the full-height content
        // paints over the sibling), keep the sibling exactly at the region's animated bottom, and leave the anchor's
        // declared height NaN.
        bool everMid = false, clippedMid = true, noOverlap = true, anchorSane = true;
        int samples = 0;
        float midH = 0f, midSibY = 0f, midAnchorH = 0f, worstGap = 0f;
        for (int i = 0; i < 8 && found; i++)
        {
            SkelOverpaintProbe.Bump.Value = i + 1;           // an ordinary parent re-render mid-flight
            host.RunFrame();
            if (!host.Animation.HasTracks(region)) break;
            float h = s.AbsoluteRect(region).H;
            if (h >= SkelOverpaintProbe.ContentH - 1f) continue;
            everMid = true; samples++;
            midH = h;
            midSibY = s.AbsoluteRect(sibling).Y;
            midAnchorH = s.Layout(anchorNode).Height;
            if ((s.Flags(region) & NodeFlags.ClipsToBounds) == 0) clippedMid = false;
            float gap = MathF.Abs(midSibY - (s.AbsoluteRect(region).Y + h));
            if (gap > worstGap) worstGap = gap;
            if (gap > 1f) noOverlap = false;
            if (!float.IsNaN(midAnchorH)) anchorSane = false;   // never a mid-flight number on the anchor
        }

        for (int i = 0; i < 60; i++) host.RunFrame();        // settle
        bool settledDeclared = found && float.IsNaN(s.Layout(region).Height);   // declared (auto) restored, not the ease
        bool unclipped = found && (s.Flags(region) & NodeFlags.ClipsToBounds) == 0;   // the reflow took its clip back
        float anchorH = found ? s.Layout(anchorNode).Height : 0f;
        bool anchorFree = float.IsNaN(anchorH);
        float finalH = found ? s.AbsoluteRect(region).H : 0f;
        float finalSibY = found ? s.AbsoluteRect(sibling).Y : 0f;
        bool fullyOpen = found && Near(finalH, SkelOverpaintProbe.ContentH, 1.5f)
                         && Near(finalSibY, s.AbsoluteRect(region).Y + SkelOverpaintProbe.ContentH, 1.5f);

        Check("23r.c Skel reflow: the region CLIPS while its layout height eases (sibling never overpainted), unclips at settle, and its component anchor is never frozen at a mid-flight size",
            found && !authorClip && everMid && clippedMid && noOverlap && anchorSane
            && settledDeclared && unclipped && anchorFree && fullyOpen,
            $"found={found} authorClip={authorClip} samples={samples} mid(h={midH:0.0} sibY={midSibY:0.0} anchorH={midAnchorH:0.0} clipped={clippedMid} worstGap={worstGap:0.00} noOverlap={noOverlap} anchorSane={anchorSane}) "
            + $"settle(h={finalH:0.0} sibY={finalSibY:0.0} regionLiNaN={settledDeclared} unclipped={unclipped} anchorH={anchorH:0.0} anchorNaN={anchorFree} fullyOpen={fullyOpen})");

        static NodeHandle FindSkelRegion(SceneStore sc, NodeHandle from)
        {
            if (!sc.IsLive(from)) return NodeHandle.Null;
            if (sc.ElementTypeId(from) == 13) return from;                 // SkelRegionEl
            for (var c = sc.FirstChild(from); !c.IsNull; c = sc.NextSibling(c))
            {
                var hit = FindSkelRegion(sc, c);
                if (!hit.IsNull) return hit;
            }
            return NodeHandle.Null;
        }
    }

    // ── 23r.d–g + 23s.a — the reflow REWORK gates (they sit on top of 23r.a/b/c + 23x, which prove the base runtime) ──
    // Five behaviours a seed-once-and-forget reflow row structurally cannot have, each observable only mid-flight:
    //   d  a row seeded at "the solved auto size" RETARGETS when content lands late (the shelf that fills in), instead
    //      of easing to the empty-shell height and snapping the remainder on the settle frame;
    //   e  a mid-flight reconcile is INERT — the declared size is filed on the row (RestoreTo) instead of stomping the
    //      interp into LayoutInput, and the clip the reflow itself added survives the re-render;
    //   f  a mid-flight RE-DECLARATION is ground truth: it becomes both the row's RestoreTo and its target, so an
    //      interrupted open→close can never restore the OLD row's NaN and flash a full-height frame;
    //   g  a sibling SHOVED by an active reflow gets no position FLIP — the reflow's own per-tick re-solve IS the
    //      animation, and a FLIP translate on top of it is stale double-compensation (a genuine LOCAL move still FLIPs);
    //   23s.a a tween position reframe RETARGETS IN PLACE, keeping the original deadline (repeated deltas decay instead
    //      of restarting a full tween each time — the "knob perpetually a drawer-height behind" desync).
    // Same headless shape as ReflowChecks/SkelReflowClipChecks (no GPU, no window): HeadlessPlatformApp + HeadlessWindow
    // + HeadlessGpuDevice + HeadlessFontSystem, driven by host.RunFrame() at the deterministic 16ms headless step.
    static void ReflowRetargetChecks(StringTable strings)
    {
        const float StepMs = 16f;   // FixedFrameTimeSource default — the headless frame clock every gate below counts in

        // 23r.d + 23r.e share ONE host: two INDEPENDENT keyed entrants (A grows mid-flight, B is re-rendered
        // mid-flight), so neither gate has to reset the other's signals across a host boundary.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("reflow-grow", new Size2(360, 480), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ReflowGrowProbe());
            var s = host.Scene;

            host.RunFrame();                                   // mount WITHOUT either entrant (a first frame never captures)
            ReflowGrowProbe.MountedA.Value = true;
            host.RunFrame();                                   // A mounts → PendingEnterReflow → SeedEnterReflow(0 → BaseH)
            var grow = Child(s, s.Root, 0);
            bool seeded = host.Animation.TryGetLiveReflow(grow, AnimChannel.LayoutH, out float to0, out bool nat0, out float rest0);
            bool seedShape = seeded && nat0 && float.IsNaN(rest0) && Near(to0, ReflowGrowProbe.BaseH, 1f);

            // Per-tick ease bound: a Linear 333ms tween over the largest range this node ever flies (FullH) moves
            // FullH*(16/333) ≈ 4.8 per tick. 4x slack + 1 keeps the bound honest about the retarget re-bake, while a
            // SNAP (the whole remaining ~87 in one frame — what an un-retargeted row does at settle, and what a stomped
            // LayoutInput does on the growth frame) is an order of magnitude over it.
            float stepBound = ReflowGrowProbe.FullH * (StepMs / 333f) * 4f + 1f;
            float prevH = s.AbsoluteRect(grow).H, maxStep = 0f;
            bool monotone = true;
            void SampleGrow()
            {
                float h = s.AbsoluteRect(grow).H;
                if (h < prevH - 0.25f) monotone = false;
                float step = MathF.Abs(h - prevH);
                if (step > maxStep) maxStep = step;
                prevH = h;
            }
            for (int i = 0; i < 4; i++) { host.RunFrame(); SampleGrow(); }
            float hBeforeGrow = prevH;

            ReflowGrowProbe.Grown.Value = true;                // an extra fixed-height child mounts INSIDE the flying node
            host.RunFrame(); SampleGrow();                     // ...and RunReflowLayout retargets the live row THIS frame
            bool retargeted = host.Animation.TryGetLiveReflow(grow, AnimChannel.LayoutH, out float toGrow, out bool natGrow, out float restGrow)
                              && natGrow && float.IsNaN(restGrow) && Near(toGrow, ReflowGrowProbe.FullH, 1f);

            // Alloc tripwire on the frames that RUN the retarget machinery (RunReflowLayout's per-root TryGetLiveReflow
            // + the natural-extent walk) with no reconcile of their own. First 4 skipped for per-host list capacity /
            // JIT warm-up, exactly like gate.icon.alloc and 46n4.
            long worstTickAlloc = 0;
            for (int i = 0; i < 14; i++)
            {
                var f = host.RunFrame();
                SampleGrow();
                if (i >= 4 && f.HotPhaseAllocBytes > worstTickAlloc) worstTickAlloc = f.HotPhaseAllocBytes;
            }
            for (int i = 0; i < 40; i++) { host.RunFrame(); SampleGrow(); }   // settle
            bool growSettled = !host.Animation.TryGetLiveReflow(grow, AnimChannel.LayoutH, out _, out _, out _);
            bool growDeclared = float.IsNaN(s.Layout(grow).Height);            // declared (auto) restored, not the ease
            float growFinal = s.AbsoluteRect(grow).H;
            bool growFull = Near(growFinal, ReflowGrowProbe.FullH, 1f);        // the FINAL natural height, reached BY the ease

            Check("23r.d Reflow content growth mid-flight: the live row retargets to the new natural extent, the ease stays monotonic and jump-free, and the settle solves at the final natural height",
                seedShape && retargeted && monotone && maxStep <= stepBound && growSettled && growDeclared && growFull
                && hBeforeGrow > 0.5f && hBeforeGrow < ReflowGrowProbe.BaseH - 1f,
                $"seedTo={to0:0.0} natural={nat0} beforeGrow={hBeforeGrow:0.0} retargetTo={toGrow:0.0} (want {ReflowGrowProbe.FullH:0}) "
                + $"monotone={monotone} maxStep={maxStep:0.00} bound={stepBound:0.00} final={growFinal:0.0} liNaN={growDeclared} settled={growSettled}");

            Check("23r.d2 Reflow retarget machinery is 0-alloc: every mid-flight tick frame keeps hot-phase (6–13) alloc at 0",
                worstTickAlloc == 0, $"worstTickAlloc={worstTickAlloc}B");

            // 23r.e — the SECOND entrant: a same-props re-render on every mid-flight frame must change nothing the row owns.
            ReflowGrowProbe.MountedB.Value = true;
            host.RunFrame();                                   // B mounts → its own enter reflow 0 → BaseH
            var inert = Child(s, s.Root, 1);
            // The element declares NO ClipToBounds, so a clip on it can only be the ROW's (AnimFlags.ClipAdded) — the
            // release at settle below is what proves the ownership, and the mid-flight samples prove a re-render can no
            // longer strip it (the reconciler's `else if` now defers to HasEngineOwnedClip).
            bool rowClip = (s.Flags(inert) & NodeFlags.ClipsToBounds) != 0 && host.Animation.HasEngineOwnedClip(inert);
            bool midClipped = true, midInterp = true, midEngineOwned = true;
            int midSamples = 0;
            float midLi = 0f, midSolved = 0f;
            for (int i = 0; i < 8; i++)
            {
                ReflowGrowProbe.Bump.Value = i + 1;            // an ordinary same-props re-render, mid-flight
                host.RunFrame();
                if (!host.Animation.TryGetLiveReflow(inert, AnimChannel.LayoutH, out _, out _, out _)) break;
                midSamples++;
                midLi = s.Layout(inert).Height;
                midSolved = s.AbsoluteRect(inert).H;
                if (float.IsNaN(midLi) || !Near(midLi, midSolved, 1f)) midInterp = false;   // the INTERP, not the declared NaN
                if ((s.Flags(inert) & NodeFlags.ClipsToBounds) == 0) midClipped = false;    // the reconcile did not strip it
                if (!host.Animation.HasEngineOwnedClip(inert)) midEngineOwned = false;
            }
            for (int i = 0; i < 40; i++) host.RunFrame();      // settle
            bool inertRestored = float.IsNaN(s.Layout(inert).Height);
            bool inertUnclipped = (s.Flags(inert) & NodeFlags.ClipsToBounds) == 0 && !host.Animation.HasEngineOwnedClip(inert);
            bool inertOpen = Near(s.AbsoluteRect(inert).H, ReflowGrowProbe.BaseH, 1f);

            Check("23r.e Reflow mid-flight reconcile is inert: LayoutInput keeps the interp (the declared value is filed on the row), the engine-added clip survives the re-render, and settle restores declared + releases the clip",
                rowClip && midSamples >= 2 && midInterp && midClipped && midEngineOwned
                && inertRestored && inertUnclipped && inertOpen,
                $"rowClip={rowClip} samples={midSamples} midLi={midLi:0.0} midSolved={midSolved:0.0} "
                + $"midInterp={midInterp} midClipped={midClipped} settleLiNaN={inertRestored} unclipped={inertUnclipped} open={inertOpen}");
        }

        // 23r.f — a mid-flight RE-DECLARATION, both directions: a declared-height node re-declared LARGER (the target and
        // the settle value must both follow), and the interrupted open→close (an auto-height drawer told to close
        // mid-open) which is where the OLD row's stale RestoreTo used to flash the full natural height for one frame.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("reflow-declared", new Size2(360, 560), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ReflowDeclaredProbe());
            var s = host.Scene;

            host.RunFrame();
            ReflowDeclaredProbe.MountedDeclared.Value = true;
            host.RunFrame();                                   // mounts DECLARED at H1 → enter reflow 0 → H1
            var declared = Child(s, s.Root, 0);
            bool declSeeded = host.Animation.TryGetLiveReflow(declared, AnimChannel.LayoutH, out float dTo0, out _, out float dRest0)
                              && Near(dTo0, ReflowDeclaredProbe.DeclaredH1, 1f) && Near(dRest0, ReflowDeclaredProbe.DeclaredH1, 1f);
            for (int i = 0; i < 4; i++) host.RunFrame();
            float dMid = s.AbsoluteRect(declared).H;

            ReflowDeclaredProbe.Declared.Value = ReflowDeclaredProbe.DeclaredH2;   // re-declare mid-flight
            host.RunFrame();
            bool declRetargeted = host.Animation.TryGetLiveReflow(declared, AnimChannel.LayoutH, out float dTo1, out _, out float dRest1)
                                  && Near(dTo1, ReflowDeclaredProbe.DeclaredH2, 1f) && Near(dRest1, ReflowDeclaredProbe.DeclaredH2, 1f);
            for (int i = 0; i < 40; i++) host.RunFrame();      // settle
            float dFinal = s.AbsoluteRect(declared).H;
            float dLi = s.Layout(declared).Height;
            bool declSettled = Near(dFinal, ReflowDeclaredProbe.DeclaredH2, 1f) && Near(dLi, ReflowDeclaredProbe.DeclaredH2, 1f)
                               && !host.Animation.TryGetLiveReflow(declared, AnimChannel.LayoutH, out _, out _, out _);

            Check("23r.f Reflow declared retarget: a mid-flight re-declaration becomes the row's RestoreTo AND its target, and the settle solves at the new declared size",
                declSeeded && declRetargeted && declSettled && dMid > 0.5f && dMid < ReflowDeclaredProbe.DeclaredH1 - 1f,
                $"seed(to={dTo0:0.0} restore={dRest0:0.0}) mid={dMid:0.0} retarget(to={dTo1:0.0} restore={dRest1:0.0} want={ReflowDeclaredProbe.DeclaredH2:0}) "
                + $"final={dFinal:0.0} li={dLi:0.0}");

            // The interrupt: an AUTO-height drawer opening 0 → natural, told to close (declared 0) three ticks in. No
            // frame between the close and the settle may solve at the full natural height — that one frame IS the flash.
            ReflowDeclaredProbe.MountedDrawer.Value = true;
            host.RunFrame();                                   // mounts AUTO → enter reflow 0 → NaturalH
            var drawer = Child(s, s.Root, 1);
            bool drawerSeeded = host.Animation.TryGetLiveReflow(drawer, AnimChannel.LayoutH, out float wTo0, out bool wNat0, out float wRest0)
                                && wNat0 && float.IsNaN(wRest0) && Near(wTo0, ReflowDeclaredProbe.NaturalH, 1.5f);
            for (int i = 0; i < 3; i++) host.RunFrame();
            float wOpenPeak = s.AbsoluteRect(drawer).H;

            ReflowDeclaredProbe.DrawerH.Value = 0f;            // the CLOSE lands mid-open
            float worstFlash = 0f;
            for (int i = 0; i < 45; i++)
            {
                host.RunFrame();
                float h = s.AbsoluteRect(drawer).H;
                if (h > worstFlash) worstFlash = h;
                if (float.IsNaN(s.Layout(drawer).Height)) worstFlash = ReflowDeclaredProbe.NaturalH;   // auto restored = the bug
            }
            bool noFlash = worstFlash < ReflowDeclaredProbe.NaturalH - 5f;
            float wFinal = s.AbsoluteRect(drawer).H;
            float wLi = s.Layout(drawer).Height;
            bool closed = Near(wFinal, 0f, 0.5f) && wLi == 0f
                          && !host.Animation.TryGetLiveReflow(drawer, AnimChannel.LayoutH, out _, out _, out _);

            Check("23r.f2 interrupted open→close: the close retargets the LIVE row (no frame solves at the full natural height) and settles at the declared 0",
                drawerSeeded && noFlash && closed && wOpenPeak > 0.5f && wOpenPeak < ReflowDeclaredProbe.NaturalH - 5f,
                $"seed(to={wTo0:0.0} natural={wNat0}) openPeak={wOpenPeak:0.0} worstAfterClose={worstFlash:0.0} (natural={ReflowDeclaredProbe.NaturalH:0}) "
                + $"final={wFinal:0.0} li={wLi:0.0} closed={closed}");
        }

        // 23r.g — shove suppression. The sibling BELOW the reflowing entrant moves on every commit frame of the reveal,
        // but that move is CAUSED by the reflow: it must ride layout, not a FLIP translate. Every ride frame here is a
        // real commit frame (a trivial signal bump) so ApplyProjections runs with a live reflow every single time.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("reflow-shove", new Size2(360, 480), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ReflowShoveProbe());
            var s = host.Scene;

            host.RunFrame();
            ReflowShoveProbe.Mounted.Value = true;
            host.RunFrame();                                   // the entrant mounts; the seed runs BEFORE ApplyProjections,
                                                               // so even this frame's shove must already be suppressed
            var entrant = Child(s, s.Root, 0);
            var sib = Child(s, s.Root, 2);
            bool flying = host.Animation.TryGetLiveReflow(entrant, AnimChannel.LayoutH, out float sTo, out _, out _);
            bool noFlip = true, monotoneY = true;
            float prevY = s.AbsoluteRect(sib).Y, startY = prevY, worstDy = 0f;
            int rideFrames = 0;
            for (int i = 0; i < 14; i++)
            {
                ReflowShoveProbe.Noise.Value = i + 1;          // every ride frame is a COMMIT frame
                host.RunFrame();
                if (!host.Animation.TryGetLiveReflow(entrant, AnimChannel.LayoutH, out _, out _, out _)) break;
                rideFrames++;
                if (host.Animation.TryGetTrackValue(sib, AnimChannel.TranslateY, out float ty)) { noFlip = false; worstDy = MathF.Max(worstDy, MathF.Abs(ty)); }
                float dy = MathF.Abs(s.Paint(sib).LocalTransform.Dy);
                if (dy > 0.01f) { noFlip = false; worstDy = MathF.Max(worstDy, dy); }
                float y = s.AbsoluteRect(sib).Y;
                if (y < prevY - 0.25f) monotoneY = false;
                prevY = y;
            }
            bool rode = prevY > startY + 4f;                   // it genuinely moved WITH the reveal (layout did it)

            // The suppression bookkeeping itself is pure POD reads — a commit frame's own reconcile allocates, so the
            // tripwire goes around the API the way the virtual-collection / lazy-grid gates in this suite do, plus the
            // tick frames inside the same suppression window.
            var reflowScratch = new List<NodeHandle>(8);
            host.Animation.CollectLiveReflowNodes(reflowScratch);
            _ = host.Animation.TryGetLiveReflow(entrant, AnimChannel.LayoutH, out _, out _, out _);
            _ = host.Animation.HasEngineOwnedClip(entrant);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                host.Animation.CollectLiveReflowNodes(reflowScratch);
                _ = host.Animation.TryGetLiveReflow(entrant, AnimChannel.LayoutH, out _, out _, out _);
                _ = host.Animation.HasEngineOwnedClip(entrant);
            }
            long apiAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            long worstShoveAlloc = 0;
            for (int i = 0; i < 8; i++)
            {
                var f = host.RunFrame();                       // tick frames INSIDE the suppression window
                if (i >= 2 && f.HotPhaseAllocBytes > worstShoveAlloc) worstShoveAlloc = f.HotPhaseAllocBytes;
                float y = s.AbsoluteRect(sib).Y;
                if (y < prevY - 0.25f) monotoneY = false;
                prevY = y;
            }
            for (int i = 0; i < 45; i++) host.RunFrame();      // settle the reveal (no reflow → no suppression)
            float restY = s.AbsoluteRect(sib).Y;

            // Counter-assertion (the 23x contract): with no reflow in flight, a genuine LOCAL move still FLIPs.
            ReflowShoveProbe.Shifted.Value = true;             // the spacer above the sibling grows 0 → 40
            host.RunFrame();
            bool localSeeded = host.Animation.TryGetTrackValue(sib, AnimChannel.TranslateY, out float localDy) && localDy < -30f;
            bool localHeld = Near(s.AbsoluteRect(sib).Y, restY, 1.5f);          // presented Y holds (FLIP "Invert")
            for (int i = 0; i < 45; i++) host.RunFrame();
            bool localLanded = Near(s.AbsoluteRect(sib).Y, restY + 40f, 0.5f)
                               && !host.Animation.TryGetTrackValue(sib, AnimChannel.TranslateY, out _);

            Check("23r.g Reflow shove suppression: a sibling moved BY an active reflow gets NO position FLIP (no TranslateY row, no transform) and rides layout monotonically; a genuine local move still FLIPs",
                flying && rideFrames >= 4 && noFlip && monotoneY && rode && localSeeded && localHeld && localLanded,
                $"seedTo={sTo:0.0} rideFrames={rideFrames} noFlip={noFlip} worstDy={worstDy:0.00} monotoneY={monotoneY} rode={rode} "
                + $"sibY {startY:0.0}→{prevY:0.0}→{restY:0.0} localSeeded={localSeeded} localDy={localDy:0.0} held={localHeld} landed={localLanded}");

            Check("23r.g2 shove-suppression bookkeeping is 0-alloc (CollectLiveReflowNodes/TryGetLiveReflow/HasEngineOwnedClip + the suppression-window tick frames)",
                apiAlloc == 0 && worstShoveAlloc == 0, $"apiAlloc={apiAlloc}B worstTickAlloc={worstShoveAlloc}B");
        }

        // 23s.a — the tween position reframe. Three stacked deltas ~60ms apart: a retarget-in-place keeps the FIRST
        // delta's deadline (250ms), so the node is home by then; a full restart (the old ReframePosition tween arm) would
        // still be flying 128ms later. The peaks must also DECAY across deltas, not accumulate.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("reframe-retarget", new Size2(320, 400), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new ReframeRetargetProbe());
            var s = host.Scene;

            host.RunFrame();
            var mover = Child(s, s.Root, 1);
            float y0 = s.AbsoluteRect(mover).Y;

            ReframeRetargetProbe.Spacer.Value = 60f;           // delta 1 — t = 0 of the 250ms deadline
            host.RunFrame();
            float peak1 = MathF.Abs(s.Paint(mover).LocalTransform.Dy);
            int frames = 0;
            for (int i = 0; i < 3; i++) { host.RunFrame(); frames++; }
            ReframeRetargetProbe.Spacer.Value = 76f;           // delta 2 — t ≈ 64ms
            host.RunFrame(); frames++;
            float peak2 = MathF.Abs(s.Paint(mover).LocalTransform.Dy);
            for (int i = 0; i < 3; i++) { host.RunFrame(); frames++; }
            ReframeRetargetProbe.Spacer.Value = 84f;           // delta 3 — t ≈ 128ms
            host.RunFrame(); frames++;
            bool flyingAt3 = host.Animation.TryGetTrackValue(mover, AnimChannel.TranslateY, out float dy3) && MathF.Abs(dy3) > 1f;

            // The first delta's ORIGINAL deadline (250ms) + one tick of epsilon. A restart at delta 3 would run to
            // 128 + 250 = 378ms — six ticks past this window, with a live (not Done) row.
            int deadlineFrames = (int)MathF.Ceiling(250f / StepMs) + 1;
            while (frames < deadlineFrames) { host.RunFrame(); frames++; }
            bool settledOnFirstDeadline = !host.Animation.TryGetTrackValue(mover, AnimChannel.TranslateY, out float dyEnd)
                                          && MathF.Abs(s.Paint(mover).LocalTransform.Dy) < 0.5f;
            bool landed = Near(s.AbsoluteRect(mover).Y, y0 + 84f, 0.5f);

            Check("23s.a tween reframe retargets in place: three stacked position deltas settle by the FIRST delta's original deadline (no per-delta 250ms restart) and the residual decays instead of accumulating",
                Near(peak1, 60f, 1.5f) && peak2 < peak1 - 1f && flyingAt3 && settledOnFirstDeadline && landed,
                $"peak1={peak1:0.0} peak2={peak2:0.0} dyAtDelta3={dy3:0.0} frames={frames} deadlineFrames={deadlineFrames} "
                + $"dyEnd={dyEnd:0.00} settled={settledOnFirstDeadline} landed={landed}");
        }
    }

    // 23r.h — Wavee's playlist drawer: a keyed SizeMode.Reflow child inside a measured virtual row, wrapped in a
    // default-Direction (row) ItemContainer-shaped box. Removing the child used to (a) SetMeasured the closed height
    // on the same frame (rows below snapped up) while (b) the exit orphan kept its last full Bounds, so ClipToBounds
    // was a no-op and the facts line painted over the already-moved rows for 1–3 frames.
    static void VirtualReflowExitChecks(StringTable strings)
    {
        const float StepMs = 16f;
        VirtualDrawerExitProbe.Open.Value = false;
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("virt-drawer-exit", new Size2(280, 400), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new VirtualDrawerExitProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        var s = host.Scene;

        host.RunFrame();
        VirtualDrawerExitProbe.Open.Value = true;
        host.RunFrame();
        for (int i = 0; i < 30; i++) host.RunFrame();   // settle the enter (200ms Linear + pad)

        NodeHandle vp = FindScrollViewport(s, s.Root);
        if (vp.IsNull || !s.TryGetScroll(vp, out var sc0) || sc0.ContentNode.IsNull)
        {
            Check("23r.h Reflow exit orphan under a measured virtual row: height eases monotonically and the orphan clip stays inside the row",
                false, "no measured viewport after open");
            return;
        }
        var content = sc0.ContentNode;
        var row0 = Child(s, content, 0);
        var row1 = Child(s, content, 1);
        float openH = s.Bounds(row0).H;
        float row1OpenY = s.AbsoluteRect(row1).Y;
        bool opened = openH > VirtualDrawerExitProbe.RowH + VirtualDrawerExitProbe.DrawerH - 2f
                      && s.OrphanCount == 0;

        VirtualDrawerExitProbe.Open.Value = false;
        host.RunFrame();   // remove → orphan + SeedExit LayoutH→0; JustSeeded holds the open size this frame

        var o = s.OrphanCount > 0 ? s.OrphanAt(0, out _, out _) : default;
        float h0 = s.Bounds(row0).H;
        bool orphaned = !o.IsNull && s.IsOrphan(o) && s.IsLive(o);
        bool noSnapOnRemove = orphaned && h0 > VirtualDrawerExitProbe.RowH + 8f;
        bool clipped = orphaned && (s.Flags(o) & NodeFlags.ClipsToBounds) != 0;
        var slot = Child(s, row0, 0);
        bool insideOnRemove = orphaned && !slot.IsNull
            && s.Bounds(o).Y + s.Bounds(o).H <= s.Bounds(slot).H + 0.75f;

        bool monotone = true, clipHeld = clipped && insideOnRemove, snapped = !noSnapOnRemove;
        float prevH = h0, maxDrop = 0f;
        float stepBound = VirtualDrawerExitProbe.DrawerH * (StepMs / 200f) * 4f + 2f;
        int aliveFrames = 0;
        for (int i = 0; i < 24; i++)
        {
            host.RunFrame();
            float h = s.Bounds(row0).H;
            if (h > prevH + 0.25f) monotone = false;
            float drop = prevH - h;
            if (drop > maxDrop) maxDrop = drop;
            if (drop > stepBound) snapped = true;
            prevH = h;
            if (s.OrphanCount > 0)
            {
                aliveFrames++;
                var live = s.OrphanAt(0, out _, out _);
                var liveSlot = Child(s, row0, 0);
                if ((s.Flags(live) & NodeFlags.ClipsToBounds) == 0) clipHeld = false;
                if (!liveSlot.IsNull && s.Bounds(live).Y + s.Bounds(live).H > s.Bounds(liveSlot).H + 0.75f)
                    clipHeld = false;
                if (h <= VirtualDrawerExitProbe.RowH + 0.5f) snapped = true;   // closed while orphan still alive
            }
            if (s.OrphanCount == 0 && Near(h, VirtualDrawerExitProbe.RowH, 1f)) break;
        }

        float closedH = s.Bounds(row0).H;
        float row1ClosedY = s.AbsoluteRect(row1).Y;
        bool settled = s.OrphanCount == 0 && Near(closedH, VirtualDrawerExitProbe.RowH, 1f);
        bool siblingFollowed = row1ClosedY < row1OpenY - 8f && Near(row1ClosedY, s.AbsoluteRect(row0).Y + closedH, 1.5f);

        Check("23r.h Reflow exit orphan under a measured virtual row: height eases monotonically (never snaps while the orphan is alive) and the orphan's clip/draw bounds stay inside the row",
            opened && orphaned && noSnapOnRemove && monotone && !snapped && clipHeld && settled && siblingFollowed && aliveFrames >= 3,
            $"openH={openH:0.0} removeH={h0:0.0} closedH={closedH:0.0} alive={aliveFrames} maxDrop={maxDrop:0.0} "
            + $"monotone={monotone} snapped={snapped} clip={clipHeld} settled={settled} siblingFollowed={siblingFollowed} "
            + $"row1Y {row1OpenY:0.0}→{row1ClosedY:0.0}");
    }

    static NodeHandle FindScrollViewport(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return default;
        if (s.HasScroll(n) && s.TryGetScroll(n, out var sc) && sc.ItemCount > 0) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindScrollViewport(s, c);
            if (!r.IsNull) return r;
        }
        return default;
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
            && Near(edgeLayer.CompositeClip.W, 200f);
        Check("30a. edge-fade layer carries the effective composite clip",
            edgeClipOk, $"deviceH={edgeLayer.DeviceRect.H:0.#} clip=({edgeLayer.CompositeClip.W:0.#}x{edgeLayer.CompositeClip.H:0.#})");

        // An authored/local ClipRect is the visible boundary of the fade. Sticky ClipTopAtViewport drives this same paint
        // column while scrolling; anchoring the layer at the node's un-clipped top puts the top ramp above the scissor,
        // making the sticky edge a hard cut. Exercise the recorder seam directly so this stays independent of scroll-bind
        // evaluation and fails only if fade geometry regresses.
        var clippedEdgeScene = new SceneStore();
        new TreeReconciler(clippedEdgeScene, strings).ReconcileRoot(new BoxEl
        {
            Width = 200, Height = 160, Fill = ColorF.FromRgba(20, 20, 20),
            EdgeFade = new EdgeFadeSpec(EdgeMask.Top, 0f, 24f, 0f, 0f),
            Children = [new BoxEl { Width = 200, Height = 160, Fill = ColorF.FromRgba(255, 255, 255) }],
        }, null);
        new FlexLayout(clippedEdgeScene, new HeadlessFontSystem(strings)).Run(clippedEdgeScene.Root);
        clippedEdgeScene.Paint(clippedEdgeScene.Root).ClipRect = RectF.FromLTRB(0f, 60f, 200f, 160f);
        var clippedEdgeDl = new DrawList();
        SceneRecorder.Record(clippedEdgeScene, clippedEdgeDl);
        var clippedEdgeDev = new HeadlessGpuDevice();
        clippedEdgeDev.SubmitDrawList(clippedEdgeDl.Bytes, clippedEdgeDl.SortKeys,
            new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
        PushLayerCmd clippedEdgeLayer = default;
        foreach (var l in clippedEdgeDev.LastLayers)
            if (l.Kind == (int)LayerKind.EdgeFade) { clippedEdgeLayer = l; break; }
        bool clippedEdgeOk = clippedEdgeLayer.Kind == (int)LayerKind.EdgeFade
            && Near(clippedEdgeLayer.DeviceRect.X, 0f) && Near(clippedEdgeLayer.DeviceRect.Y, 60f)
            && Near(clippedEdgeLayer.DeviceRect.W, 200f) && Near(clippedEdgeLayer.DeviceRect.H, 100f)
            && Near(clippedEdgeLayer.CompositeClip.X, 0f) && Near(clippedEdgeLayer.CompositeClip.Y, 60f)
            && Near(clippedEdgeLayer.CompositeClip.W, 200f) && Near(clippedEdgeLayer.CompositeClip.H, 100f)
            && Near(clippedEdgeLayer.FadeBandT, 24f) && clippedEdgeLayer.FadeEdges == (int)EdgeMask.Top;
        Check("30a2. explicit EdgeFade anchors its ramp to the finite ClipRect's visible boundary",
            clippedEdgeOk,
            $"device=({clippedEdgeLayer.DeviceRect.X:0.#},{clippedEdgeLayer.DeviceRect.Y:0.#},{clippedEdgeLayer.DeviceRect.W:0.#}x{clippedEdgeLayer.DeviceRect.H:0.#}) " +
            $"clip=({clippedEdgeLayer.CompositeClip.X:0.#},{clippedEdgeLayer.CompositeClip.Y:0.#},{clippedEdgeLayer.CompositeClip.W:0.#}x{clippedEdgeLayer.CompositeClip.H:0.#}) " +
            $"bandT={clippedEdgeLayer.FadeBandT:0.#} edges={clippedEdgeLayer.FadeEdges}");

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
            && nestedDev.LayerBalance == 0;
        Check("30b. edge-fade -> acrylic records balanced nested layers",
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

    static void NavigationSelectionChecks()
    {
        var scene = new SceneStore();
        var outgoing = scene.CreateNode(1);
        var incoming = scene.CreateNode(1);
        scene.Root = outgoing;
        scene.Paint(outgoing).LocalTransform = Affine2D.Identity;
        scene.Paint(incoming).LocalTransform = Affine2D.Identity;
        var engine = new AnimEngine(scene);

        const float delta = 48f;
        NavigationSelectionMotion.StartVertical(engine, outgoing,
            from: 0f, to: delta, indicatorHeight: 16f, outgoing: true, sameDepth: true);
        NavigationSelectionMotion.StartVertical(engine, incoming,
            from: -delta, to: 0f, indicatorHeight: 16f, outgoing: false, sameDepth: true);
        var immediateOut = scene.Paint(outgoing).LocalTransform;
        var immediateIn = scene.Paint(incoming).LocalTransform;
        bool seededImmediately = MathF.Abs(immediateOut.Dy) < 0.01f
                                 && MathF.Abs(immediateOut.M22 - 1f) < 0.01f
                                 && MathF.Abs(immediateIn.Dy + delta) < 0.01f
                                 && MathF.Abs(immediateIn.M22 - 1f) < 0.01f
                                 && scene.Paint(outgoing).Opacity > 0.99f
                                 && scene.Paint(incoming).Opacity > 0.99f;
        engine.Tick(0f);
        engine.Tick(NavigationSelectionMotion.DurationMs * NavigationSelectionMotion.StretchPhase);

        var stretchedOut = scene.Paint(outgoing).LocalTransform;
        var stretchedIn = scene.Paint(incoming).LocalTransform;
        bool peak = MathF.Abs(stretchedOut.Dy) < 0.1f
                    && MathF.Abs(stretchedIn.Dy + delta) < 0.1f
                    && MathF.Abs(stretchedOut.M22 - 4f) < 0.05f
                    && MathF.Abs(stretchedIn.M22 - 4f) < 0.05f
                    && scene.Paint(outgoing).Opacity > 0.99f;

        engine.Tick(NavigationSelectionMotion.DurationMs + 1f);
        var settledOut = scene.Paint(outgoing).LocalTransform;
        var settledIn = scene.Paint(incoming).LocalTransform;
        bool end = MathF.Abs(settledOut.Dy - delta) < 0.1f
                   && MathF.Abs(settledIn.Dy) < 0.1f
                   && MathF.Abs(settledOut.M22 - 1f) < 0.01f
                   && MathF.Abs(settledIn.M22 - 1f) < 0.01f
                   && scene.Paint(outgoing).Opacity < 0.01f
                   && scene.Paint(incoming).Opacity > 0.99f;
        NavigationSelectionMotion.StartVertical(engine, outgoing,
            from: 0f, to: delta, indicatorHeight: 16f, outgoing: true, sameDepth: true);
        engine.Tick(0f);
        engine.Tick(80f);
        NavigationSelectionMotion.SnapVertical(engine, outgoing, visible: false);
        var reset = scene.Paint(outgoing).LocalTransform;
        bool resetImmediately = MathF.Abs(reset.Dy) < 0.01f
                                && MathF.Abs(reset.M22 - 1f) < 0.01f
                                && scene.Paint(outgoing).Opacity < 0.01f;
        Check("gate.anim.navigationSelectionWorm", seededImmediately && peak && end && resetImmediately,
            $"peak=({stretchedOut.Dy:0.0},{stretchedIn.Dy:0.0},{stretchedOut.M22:0.00}) " +
            $"end=({settledOut.Dy:0.0},{settledIn.Dy:0.0},{settledIn.M22:0.00}) " +
            $"seed=({immediateIn.Dy:0.0},{immediateIn.M22:0.00}) reset=({reset.Dy:0.0},{reset.M22:0.00})");
    }

    static void NestedHoverBoundaryChecks(StringTable strings)
    {
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, strings) { Anim = anim };
        static Element Row() => new BoxEl
        {
            OnClick = static () => { },
            Children =
            [
                new BoxEl { Opacity = 0f, HoverOpacity = 1f },
                new BoxEl
                {
                    Opacity = 0f, HoverOpacity = 1f,
                    OnClick = static () => { },
                },
            ],
        };
        recon.ReconcileRoot(new BoxEl
        {
            OnPointerMoveWithin = static _ => { },
            Children = [Row(), Row()],
        }, null);

        var root = scene.Root;
        var first = scene.FirstChild(root);
        var second = scene.NextSibling(first);
        var firstReveal = scene.FirstChild(first);
        var secondReveal = scene.FirstChild(second);
        var firstAction = scene.NextSibling(firstReveal);
        var secondAction = scene.NextSibling(secondReveal);

        // HoverWithin on an interactive list/pane ancestor must stop at the nested row controls. Each row receives its
        // own hover edge from input dispatch; recursively driving through the ancestor was the "all ellipses" defect.
        anim.SetHover(root, true);
        bool ancestorStopped = scene.TryGetInteract(firstReveal, out var firstAtRoot)
            && scene.TryGetInteract(secondReveal, out var secondAtRoot)
            && scene.TryGetInteract(firstAction, out var firstActionAtRoot)
            && scene.TryGetInteract(secondAction, out var secondActionAtRoot)
            && firstAtRoot.HoverTarget < 0.01f && secondAtRoot.HoverTarget < 0.01f
            && firstActionAtRoot.HoverTarget < 0.01f && secondActionAtRoot.HoverTarget < 0.01f;
        anim.SetHover(first, true);
        bool rowScoped = scene.TryGetInteract(firstReveal, out var firstAtRow)
            && scene.TryGetInteract(secondReveal, out var secondAtRow)
            && scene.TryGetInteract(firstAction, out var firstActionAtRow)
            && scene.TryGetInteract(secondAction, out var secondActionAtRow)
            && firstAtRow.HoverTarget > 0.99f && secondAtRow.HoverTarget < 0.01f
            && firstActionAtRow.HoverTarget > 0.99f && secondActionAtRow.HoverTarget < 0.01f;

        // A lazy card affordance is mounted by the hover-triggered render, after the original enter cascade. It must
        // seed from its nearest interactive ancestor's live HoverWithin scope instead of waiting for a direct hit.
        var lazyScene = new SceneStore();
        var lazyAnim = new AnimEngine(lazyScene);
        var lazyRecon = new TreeReconciler(lazyScene, strings) { Anim = lazyAnim };
        var lazyBefore = new BoxEl { OnClick = static () => { }, Children = [] };
        lazyRecon.ReconcileRoot(lazyBefore, null);
        var lazyRoot = lazyScene.Root;
        lazyScene.SetFlagBits(lazyRoot, NodeFlags.HoverWithin);
        lazyAnim.SetHover(lazyRoot, true);
        lazyRecon.ReconcileRoot(new BoxEl
        {
            OnClick = static () => { },
            Children = [new BoxEl { Key = "lazy-reveal", Opacity = 0f, HoverOpacity = 1f }],
        }, lazyBefore);
        var lazyReveal = lazyScene.FirstChild(lazyRoot);
        bool lazyMountedOn = lazyScene.TryGetInteract(lazyReveal, out var lazyAtMount)
            && lazyAtMount.HoverTarget > 0.99f;

        Check("58b. container hover stops at nested rows but still drives that row's clickable reveal",
            ancestorStopped && rowScoped && lazyMountedOn,
            $"ancestorStopped={ancestorStopped} rowScoped={rowScoped} lazyMountedOn={lazyMountedOn}");

        // ── 58c: the PRESS twin — CASCADE THE REVEAL, NEVER THE CONTROL'S OWN STATE ─────────────────────────────────
        // SUPERSEDES the original 58c, which asserted `boundaryDriven == true` (a nested clickable took its container's
        // PressTarget as long as it owned an interact row). That was the residual left behind when the press cascade
        // gained hover's boundary: both cascades computed the boundary and then drove the child anyway, stopping only
        // BENEATH it. Shipped consequence — press-and-holding a home card rendered its whole Play / Shuffle / ♥ / "…"
        // cluster pressed, and hovering it grew all four, because each carries a Hover/PressScale. Canon was always the
        // other way: backdrop-effects-animation.md §7 scales a node "by the eased hover/press of its nearest interactive
        // ancestor", and a nested button IS its own nearest interactive ancestor.
        //
        // The rule now splits by LEG, which is why this is not simply "boundaries inherit nothing":
        //   · a REVEAL (Hover/PressedOpacity) follows its container ACROSS the boundary — it is the container's own
        //     affordance appearing, and the pointer is by definition not on it (TrackRow's clickable play surface);
        //   · a SCALE follows only a NON-interactive part (a thumb, an AnimatedIcon wrapper, an artwork zoom).
        var pressScene = new SceneStore();
        var pressAnim = new AnimEngine(pressScene);
        var pressRecon = new TreeReconciler(pressScene, strings) { Anim = pressAnim };
        pressRecon.ReconcileRoot(new BoxEl
        {
            OnPointerMoveWithin = static _ => { },
            Children =
            [
                new BoxEl { Key = "container-reveal", Opacity = 0f, PressedOpacity = 1f },
                // A boundary that is ALSO a reveal: clickable, so its own scope — but the reveal leg still follows the
                // container, because that is the whole mechanism behind a hover-revealed row/card button.
                new BoxEl { Key = "boundary-reveal", OnClick = static () => { }, Opacity = 0f, PressedOpacity = 1f },
                new BoxEl
                {
                    Key = "nested-button",
                    OnClick = static () => { }, PressScale = 0.96f,
                    Children = [new BoxEl { Key = "button-glyph", Opacity = 0f, PressedOpacity = 1f }],
                },
            ],
        }, null);
        var pressRoot = pressScene.Root;
        var containerReveal = pressScene.FirstChild(pressRoot);
        var boundaryReveal = pressScene.NextSibling(containerReveal);
        var nestedButton = pressScene.NextSibling(boundaryReveal);
        var buttonGlyph = pressScene.FirstChild(nestedButton);

        pressAnim.SetPress(pressRoot, true);
        bool revealDriven = pressScene.TryGetInteract(containerReveal, out var revealDown) && revealDown.PressTarget > 0.99f;
        bool boundaryRevealDriven = pressScene.TryGetInteract(boundaryReveal, out var brDown) && brDown.PressTarget > 0.99f;
        // THE FIX: a nested control's own press state is not its container's to set.
        bool boundaryScaleQuiet = pressScene.TryGetInteract(nestedButton, out var buttonDown) && buttonDown.PressTarget < 0.01f;
        bool beneathBoundaryQuiet = pressScene.TryGetInteract(buttonGlyph, out var glyphDown) && glyphDown.PressTarget < 0.01f;

        // The button's OWN press edge (what input dispatch delivers on a direct hit) still drives it and its glyph.
        pressAnim.SetPress(nestedButton, true);
        bool ownScopeDrives = pressScene.TryGetInteract(nestedButton, out var buttonOwn) && buttonOwn.PressTarget > 0.99f
            && pressScene.TryGetInteract(buttonGlyph, out var glyphOwn) && glyphOwn.PressTarget > 0.99f;

        pressAnim.SetPress(pressRoot, false);
        bool releases = pressScene.TryGetInteract(containerReveal, out var revealUp) && revealUp.PressTarget < 0.01f
            && pressScene.TryGetInteract(boundaryReveal, out var brUp) && brUp.PressTarget < 0.01f;
        // Symmetry of the same rule: the container RELEASING must not clear a nested control's own press either — the
        // button's release arrives on its own edge from input dispatch.
        bool ownScopeSurvivesContainerRelease =
            pressScene.TryGetInteract(nestedButton, out var buttonAfter) && buttonAfter.PressTarget > 0.99f;

        Check("58c. container press drives a nested boundary's REVEAL but never its own scale (and its release is the button's, not the container's)",
            revealDriven && boundaryRevealDriven && boundaryScaleQuiet && beneathBoundaryQuiet
            && ownScopeDrives && releases && ownScopeSurvivesContainerRelease,
            $"reveal={revealDriven} boundaryReveal={boundaryRevealDriven} boundaryScaleQuiet={boundaryScaleQuiet} " +
            $"beneathQuiet={beneathBoundaryQuiet} ownScope={ownScopeDrives} releases={releases} " +
            $"ownScopeSurvives={ownScopeSurvivesContainerRelease}");

        // ── 58d: the mount path obeys the same rule ─────────────────────────────────────────────────────────────────
        // Reconciler's lazy-affordance seed (a card's play FAB mounts on the hover-triggered render, after the enter
        // cascade) used to call SetHover(node, true) — the force:true arm — so a nested BUTTON mounting or re-keying
        // inside a hovered card lit up with no pointer edge at all. It now applies the cascade's rule instead.
        var mountScene = new SceneStore();
        var mountAnim = new AnimEngine(mountScene);
        var mountRecon = new TreeReconciler(mountScene, strings) { Anim = mountAnim };
        var mountBefore = new BoxEl { OnClick = static () => { }, Children = [] };
        mountRecon.ReconcileRoot(mountBefore, null);
        var mountRoot = mountScene.Root;
        mountScene.SetFlagBits(mountRoot, NodeFlags.HoverWithin);
        mountAnim.SetHover(mountRoot, true);
        mountRecon.ReconcileRoot(new BoxEl
        {
            OnClick = static () => { },
            Children =
            [
                new BoxEl { Key = "lazy-reveal", Opacity = 0f, HoverOpacity = 1f },
                new BoxEl { Key = "lazy-button", OnClick = static () => { }, HoverScale = 1.04f },
            ],
        }, mountBefore);
        var lazyRevealOnMount = mountScene.FirstChild(mountRoot);
        var lazyButtonOnMount = mountScene.NextSibling(lazyRevealOnMount);
        bool mountedRevealOn = mountScene.TryGetInteract(lazyRevealOnMount, out var mrIa) && mrIa.HoverTarget > 0.99f;
        bool mountedButtonQuiet = !mountScene.TryGetInteract(lazyButtonOnMount, out var mbIa) || mbIa.HoverTarget < 0.01f;
        Check("58d. a lazy affordance mounting into a hovered scope seeds a REVEAL but never a nested control's scale",
            mountedRevealOn && mountedButtonQuiet,
            $"reveal={mountedRevealOn} buttonQuiet={mountedButtonQuiet}");
    }

    // ── gate.record.hover-fill-scope: hover/press progress does not LEAK through an interaction scope at record time ──
    // The page ItemsView wraps every row in an interactive ItemContainer that gets HoverWithin for any pointer inside
    // the row (InputDispatcher publishes it for every PointerBit/ClickBit/PressedBit ancestor). SceneRecorder's
    // InheritedState.ForChild only replaced the inherited progress when a node was interactive AND had local progress,
    // so an interactive slot root WITHOUT an InteractionAnim row (a PagedShelf card slot: OnPointerReleased/PressedBit
    // only) passed its ancestor's HoverT=1 down and every card's HoverFill painted at once. The rule is now the
    // cascade's own boundary (IsNestedHoverBoundary): an interactive, non-HoverScopeTransparent node starts its
    // subtree from ITS OWN state — eased progress when it has a row, else its instant Hovered/Pressed flags — never
    // the ancestor's. A HoverScopeTransparent listener is not a scope and still passes the enclosing state through.
    static void HoverFillScopeChecks(StringTable strings)
    {
        static BoxEl Slot(string key, bool transparent = false) => new BoxEl
        {
            Key = key, Width = 100f, Height = 40f,
            OnPointerPressed = static _ => { },                       // PressedBit: a scope in its own right, no anim row
            HoverScopeTransparent = transparent,
            Children = [new BoxEl { Width = 100f, Height = 40f, Fill = ColorF.FromRgba(0, 0, 0), HoverFill = ColorF.FromRgba(255, 255, 255), PressedFill = ColorF.FromRgba(0, 255, 0) }],
        };
        var scene = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 400f, Height = 40f,
            OnPointerPressed = static _ => { },                       // the ItemContainer: PressedBit, HoverWithin below
            Children = [Slot("a"), Slot("b"), Slot("c", transparent: true), Slot("d")],
        });
        var container = scene.Root;
        var slotA = scene.FirstChild(container);
        var slotB = scene.NextSibling(slotA);
        var slotC = scene.NextSibling(slotB);
        var slotD = scene.NextSibling(slotC);
        scene.SetFlagBits(container, NodeFlags.HoverWithin);         // the pointer is somewhere in the row…
        scene.SetFlagBits(slotA, NodeFlags.Hovered);                 // …directly on slot A (leaf hover, no HoverWithin, no row)
        scene.SetFlagBits(slotD, NodeFlags.Pressed);                 // slot D is pressed (instant flag, no row)

        var dl = new DrawList();
        var dev = new HeadlessGpuDevice();
        dl.Reset(); SceneRecorder.Record(scene, dl);
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 40), 1f, ColorF.Transparent));
        // Only the four inner boxes carry a fill; identify each by its x (the slots and container paint nothing).
        ColorF FillAt(float x)
        {
            foreach (var r in dev.LastRects)
                if (MathF.Abs(r.Rect.X + r.Transform.Dx - x) < 0.5f) return r.Fill;
            return ColorF.FromRgba(128, 128, 128);
        }
        ColorF a = FillAt(0f), b = FillAt(100f), c = FillAt(200f), d = FillAt(300f);
        bool aHovered = a.R > 0.99f && a.G > 0.99f;                  // its own slot root is hovered → hover fill
        bool bRest = b.R < 0.01f && b.G < 0.01f;                     // un-hovered scope → the row's HoverWithin does not leak
        bool cPassThrough = c.R > 0.99f && c.G > 0.99f;              // transparent listener → the row's state passes through
        bool dPressed = d.G > 0.99f && d.R < 0.01f;                  // pressed slot root → its own press, not the row's hover
        Check("gate.record.hover-fill-scope an interactive slot root without an anim row scopes its subtree's record-time hover/press to ITS OWN flags (hovered A paints HoverFill, un-hovered B stays at rest under a HoverWithin row container, pressed D paints PressedFill) while a HoverScopeTransparent listener C still passes the row's hover through",
            aHovered && bRest && cPassThrough && dPressed && dev.LastRects.Count == 4,
            $"a=({a.R:0.00},{a.G:0.00}) b=({b.R:0.00},{b.G:0.00}) c=({c.R:0.00},{c.G:0.00}) d=({d.R:0.00},{d.G:0.00}) rects={dev.LastRects.Count}");
    }

    // ── 58e-58h: BoxEl.HoverScopeTransparent — a pointer LISTENER that is not an interaction scope ────────────────
    // The ToolTip wrapper carries OnHoverMove/OnPointerExit/OnPointerPressed/OnFocusChanged, so it owns PointerBit and
    // read as a cascade boundary: a card's hover stopped at the wrapper and never reached the wrapped play FAB, and the
    // lazy-mount seed resolved the wrapper (never hovered) instead of the card. The transparent bit makes the cascade,
    // the seed and the un-hover re-resolve look through it; hit-test and handler delivery are unchanged.
    static void TransparentHoverScopeChecks(StringTable strings)
    {
        static BoxEl Fab() => new BoxEl { Key = "fab", OnClick = static () => { }, Opacity = 0f, HoverOpacity = 1f, HoverScale = 1.04f };
        static BoxEl Listener(BoxEl fab) => new BoxEl
        {
            Key = "listener",
            HoverScopeTransparent = true,
            OnHoverMove = static _ => { },
            OnPointerExit = static () => { },
            OnPointerPressed = static _ => { },
            OnFocusChanged = static _ => { },
            Children = [fab],
        };
        static bool Quiet(SceneStore s, NodeHandle n) => !s.TryGetInteract(n, out var ia) || ia.HoverTarget < 0.01f;
        static bool On(SceneStore s, NodeHandle n) => s.TryGetInteract(n, out var ia) && ia.HoverTarget > 0.99f;

        // 58e — eager: the FAB is already mounted under the transparent listener when the card takes the hover edge.
        {
            var scene = new SceneStore();
            var anim = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = anim };
            recon.ReconcileRoot(new BoxEl
            {
                OnClick = static () => { },
                Children =
                [
                    new BoxEl { Key = "plate", HitTestVisible = false, Opacity = 0f, HoverOpacity = 1f },
                    Listener(Fab()),
                    new BoxEl
                    {
                        Key = "row", OnClick = static () => { },
                        Children = [new BoxEl { Key = "row-reveal", Opacity = 0f, HoverOpacity = 1f }],
                    },
                ],
            }, null);
            var card = scene.Root;
            var plate = scene.FirstChild(card);
            var listener = scene.NextSibling(plate);
            var fab = scene.FirstChild(listener);
            var row = scene.NextSibling(listener);
            var rowReveal = scene.FirstChild(row);

            anim.SetHover(card, true);
            bool plateOn = On(scene, plate);
            bool fabOn = On(scene, fab);                       // through the listener
            bool listenerQuiet = Quiet(scene, listener);        // no reveal/scale of its own: no interact row driven
            bool rowRevealOff = Quiet(scene, rowReveal);        // a real nested scope still stops the cascade
            anim.SetHover(card, false);
            bool fabOff = Quiet(scene, fab) && Quiet(scene, plate);

            Check("58e. a card's hover cascades through a HoverScopeTransparent listener to the wrapped FAB (and back off)",
                plateOn && fabOn && listenerQuiet && rowRevealOff && fabOff,
                $"plate={plateOn} fab={fabOn} listenerQuiet={listenerQuiet} rowRevealOff={rowRevealOff} off={fabOff}");
        }

        // 58f — lazy: the listener + FAB mount AFTER the card's enter edge; the seed walks past the listener to the card.
        {
            var scene = new SceneStore();
            var anim = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = anim };
            var before = new BoxEl { OnClick = static () => { }, Children = [] };
            recon.ReconcileRoot(before, null);
            var card = scene.Root;
            scene.SetFlagBits(card, NodeFlags.Hovered);
            anim.SetHover(card, true);
            recon.ReconcileRoot(new BoxEl { OnClick = static () => { }, Children = [Listener(Fab())] }, before);
            var listener = scene.FirstChild(card);
            var fab = scene.FirstChild(listener);
            bool fabSeeded = On(scene, fab);
            bool listenerQuiet = Quiet(scene, listener);

            Check("58f. a FAB mounting under a HoverScopeTransparent listener inside a hovered card seeds from the card",
                fabSeeded && listenerQuiet, $"fabSeeded={fabSeeded} listenerQuiet={listenerQuiet}");
        }

        // 58g — un-hover re-resolve: the pointer leaves the FAB back onto the still-hovered card; the reveal stays on.
        {
            var scene = new SceneStore();
            var anim = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = anim };
            recon.ReconcileRoot(new BoxEl { OnClick = static () => { }, Children = [Listener(Fab())] }, null);
            var card = scene.Root;
            var fab = scene.FirstChild(scene.FirstChild(card));
            scene.SetFlagBits(card, NodeFlags.HoverWithin);
            anim.SetHover(card, true);
            anim.SetHover(fab, true);
            anim.SetHover(fab, false);
            bool staysOn = On(scene, fab);
            scene.ClearFlagBits(card, NodeFlags.HoverWithin);
            anim.SetHover(card, false);
            anim.SetHover(fab, false);
            bool goesOff = Quiet(scene, fab);

            Check("58g. a reveal losing its own hover stays on while its enclosing scope is hovered, and clears when it is not",
                staysOn && goesOff, $"staysOn={staysOn} goesOff={goesOff}");
        }

        // 58h — the real thing: ToolTip.WrapStable inside a padded card under AppHost; pointer onto the card PADDING.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("tt-scope", new Size2(320, 200), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var fabNode = NodeHandle.Null;
            Element MakeFab() => new BoxEl
            {
                Width = 32f, Height = 32f, Opacity = 0f, HoverOpacity = 1f,
                OnClick = static () => { },
                OnRealized = h => fabNode = h,
            };
            Func<Element> factory = MakeFab;
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 200f, Height = 120f, Padding = Edges4.All(24f), OnClick = static () => { },
                    Children = [ToolTip.WrapStable(factory, "Play")],
                },
            });
            host.RunFrame();
            var card = host.Scene.Root;
            var r = host.Scene.AbsoluteRect(card);
            var padding = new Point2(r.X + 6f, r.Y + 6f);          // inside the card, outside the wrapped FAB
            var outside = new Point2(r.Right + 40f, r.Bottom + 40f);

            window.QueueInput(new InputEvent(InputKind.PointerMove, padding, 0, 0));
            host.RunFrame();
            bool cardHovered = (host.Scene.Flags(card) & NodeFlags.Hovered) != 0;
            bool fabOn = !fabNode.IsNull && On(host.Scene, fabNode);

            window.QueueInput(new InputEvent(InputKind.PointerMove, outside, 0, 0));
            host.RunFrame();
            bool fabOff = !fabNode.IsNull && Quiet(host.Scene, fabNode);

            Check("58h. hovering a card's padding reveals its ToolTip-wrapped FAB; leaving the card hides it",
                cardHovered && fabOn && fabOff, $"cardHovered={cardHovered} fabOn={fabOn} fabOff={fabOff}");
        }
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

    /// <summary>Decompose a composited transform's rotation angle in degrees — the SAME formula as
    /// <c>Accum.FromPaint</c> and the <c>CurrentValue</c> Rotation arm (both in AnimScheduler.cs), so a gate reads
    /// exactly what the engine itself would recover from a live node's paint.</summary>
    static float RotOf(in Affine2D tf)
        => (tf.M11 != 0f || tf.M12 != 0f) ? MathF.Atan2(tf.M12, tf.M11) * (180f / MathF.PI) : 0f;

    // ── the rest-pose-relative While* contract (fanned-cover-stack fix) ────────────────────────────────────────────
    //
    // Before this rework, AnimEngine.SeedTarget folded a gesture-state release with an IDENTITY target (replace:true
    // over PASS2's live-paint accumulator). Because a gesture channel's track OWNS its channel outright once seeded,
    // that permanently wiped an authored static Rotation/OffsetX/OffsetY/Blur the instant hover/press first engaged
    // and then let go — every existing While* call site happened to rest at identity (HoverElevatePaint cards,
    // Interaction.Interactive presets), so nothing caught it until a fanned cover stack needed BOTH an authored rest
    // pose AND a hover delta. MotionTarget's fields are now DELTAS on the node's authored rest pose (offset/rotation/
    // blur ADD, scale/opacity MULTIPLY — see MotionTok.cs), folded in at seed time by SeedTargetOver
    // (AnimScheduler.Structural.cs) so a release animates back to that authored pose, never to identity.
    static void WhileRestPoseChecks(StringTable strings)
    {
        // gate.anim.while.restPose — hover-on settles at rest+delta; hover-off settles back at the AUTHORED rest
        // pose (Rotation=-11, OffsetX=5), not at 0/0.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            recon.ReconcileRoot(new BoxEl
            {
                Width = 40, Height = 40,
                Rotation = -11f, OffsetX = 5f,
                WhileHover = new MotionTarget { Rotation = -5f, OffsetX = -10f },
                Transition = MotionTok.ControlFaster,
            }, null);
            var node = scene.Root;

            engine.ApplyInteractionEdge(node, AnimEngine.InteractKind.Hover, true);
            for (int i = 0; i < 12 && engine.HasActive; i++) engine.Tick(16f);   // > 83ms (ControlFaster) → settled
            float rotOn = RotOf(scene.Paint(node).LocalTransform);
            float txOn = scene.Paint(node).LocalTransform.Dx;
            bool onOk = Near(rotOn, -16f, 0.25f) && Near(txOn, -5f, 0.25f);

            engine.ApplyInteractionEdge(node, AnimEngine.InteractKind.Hover, false);
            for (int i = 0; i < 12 && engine.HasActive; i++) engine.Tick(16f);
            float rotOff = RotOf(scene.Paint(node).LocalTransform);
            float txOff = scene.Paint(node).LocalTransform.Dx;
            bool offOk = Near(rotOff, -11f, 0.25f) && Near(txOff, 5f, 0.25f);   // the AUTHORED rest pose, NOT 0

            Check("gate.anim.while.restPose", onOk && offOk,
                $"on: rot={rotOn:0.0} tx={txOn:0.0} (want -16/-5)  off: rot={rotOff:0.0} tx={txOff:0.0} (want -11/5)");
        }

        // gate.anim.while.cascade — the hover edge on an interaction-boundary PARENT (OnClick) reaches a non-
        // boundary, purely-presentational CHILD's own While* row via AnimScheduler.Hover.SetHoverDescendants' new
        // third leg (the EXISTING reveal/scale walk, extended — no second traversal). The child is
        // HitTestVisible=false and owns no click/pointer handler, so nothing ever edges it directly; without the
        // cascade it sits frozen at rest under a hovered container forever.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            recon.ReconcileRoot(new BoxEl
            {
                OnClick = static () => { },   // an interaction boundary — owns its own hover edge
                Children = [new BoxEl { HitTestVisible = false, WhileHover = new MotionTarget { Rotation = 8f, OffsetX = 4f } }],
            }, null);
            var parent = scene.Root;
            var child = scene.FirstChild(parent);

            engine.SetHover(parent, true);
            engine.ApplyInteractionEdge(parent, AnimEngine.InteractKind.Hover, true);   // AppHost.OnHoverChanged's real edge pair
            for (int i = 0; i < 12 && engine.HasActive; i++) engine.Tick(16f);
            float rotOn = RotOf(scene.Paint(child).LocalTransform);
            float txOn = scene.Paint(child).LocalTransform.Dx;
            bool onOk = Near(rotOn, 8f, 0.25f) && Near(txOn, 4f, 0.25f);

            engine.SetHover(parent, false);
            engine.ApplyInteractionEdge(parent, AnimEngine.InteractKind.Hover, false);
            for (int i = 0; i < 12 && engine.HasActive; i++) engine.Tick(16f);
            float rotOff = RotOf(scene.Paint(child).LocalTransform);
            float txOff = scene.Paint(child).LocalTransform.Dx;
            bool offOk = Near(rotOff, 0f, 0.25f) && Near(txOff, 0f, 0.25f);

            Check("gate.anim.while.cascade", onOk && offOk,
                $"on: rot={rotOn:0.0} tx={txOn:0.0} (want 8/4)  off: rot={rotOff:0.0} tx={txOff:0.0} (want 0/0)");
        }

        // gate.anim.while.boundaryStops — the SAME cascade must NOT reach a nested child that owns its OWN
        // interaction scope (OnClick + its own WhileHover): `if (boundary) continue;` has to run BEFORE the new
        // ApplyInteractionEdgeSelf call, or a nested control's declarative motion would fire off its container's
        // hover edge — reintroducing the "all ellipses" defect the reveal/scale cascade split already fixed once
        // for the older two legs (NestedHoverBoundaryChecks, above).
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            recon.ReconcileRoot(new BoxEl
            {
                OnClick = static () => { },
                Children = [new BoxEl { OnClick = static () => { }, WhileHover = new MotionTarget { Rotation = 8f } }],
            }, null);
            var parent = scene.Root;
            var child = scene.FirstChild(parent);

            engine.SetHover(parent, true);
            engine.ApplyInteractionEdge(parent, AnimEngine.InteractKind.Hover, true);
            bool noCascade = !engine.HasTracks(child);   // never edged at all — the boundary owns no row here
            engine.Tick(16f); engine.Tick(16f);
            float rot = RotOf(scene.Paint(child).LocalTransform);
            Check("gate.anim.while.boundaryStops", noCascade && Near(rot, 0f, 0.25f),
                $"noCascade={noCascade} rot={rot:0.0}");
        }

        // gate.anim.while.reducedMotion — reduced-motion-as-a-value (backdrop-effects-animation.md §5.9): under
        // Motion.ReducedMotion, a MotionTok.ControlNormal fan (Eased/KeepFade) still SNAPS every non-Opacity channel
        // to its end value immediately (ReducedSnap only exempts Opacity from KeepFade), so a fan is already fully
        // deployed on the very SEED frame instead of mid-way through a 250ms ease.
        {
            bool prev = Motion.ReducedMotion;
            Motion.ReducedMotion = true;
            try
            {
                var scene = new SceneStore();
                var engine = new AnimEngine(scene);
                var recon = new TreeReconciler(scene, strings) { Anim = engine };
                recon.ReconcileRoot(new BoxEl
                {
                    WhileHover = new MotionTarget { Rotation = 20f, OffsetX = 12f },
                    Transition = MotionTok.ControlNormal,
                }, null);
                var node = scene.Root;

                engine.ApplyInteractionEdge(node, AnimEngine.InteractKind.Hover, true);
                engine.Tick(0f);   // the SEED frame — a real 250ms ease would still read the REST value (0) at u=0
                float rot = RotOf(scene.Paint(node).LocalTransform);
                float tx = scene.Paint(node).LocalTransform.Dx;
                Check("gate.anim.while.reducedMotion", Near(rot, 20f, 0.25f) && Near(tx, 12f, 0.25f),
                    $"rot={rot:0.0} tx={tx:0.0}");
            }
            finally { Motion.ReducedMotion = prev; }
        }

        // gate.anim.while.snapRestoresAuthoredPose — KeepAlive un-park runs SnapStructuralToLayout (FLIP TranslateX
        // shares the channel with Offset/WhileHover). Cancelling those rows used to reset LocalTransform to identity,
        // stacking Fold covers at the card origin until the next hover retargeted them. Snap must land the AUTHORED
        // rest, not 0, and SnapAuthoredPose must drop a leftover hover fan the same way.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            recon.ReconcileRoot(new BoxEl
            {
                Width = 40, Height = 40,
                Rotation = -11f, OffsetX = 100f, OffsetY = 38f,
                WhileHover = new MotionTarget { Rotation = -5f, OffsetX = -10f, OffsetY = 6f },
                Transition = MotionTok.ControlFaster,
            }, null);
            var node = scene.Root;

            engine.ApplyInteractionEdge(node, AnimEngine.InteractKind.Hover, true);
            for (int i = 0; i < 12 && engine.HasActive; i++) engine.Tick(16f);
            float txFan = scene.Paint(node).LocalTransform.Dx;
            bool wasFan = Near(txFan, 90f, 0.25f);

            engine.SnapStructuralToLayout(node);
            float txAfterFlipSnap = scene.Paint(node).LocalTransform.Dx;
            bool flipKeptRest = Near(txAfterFlipSnap, 100f, 0.25f) || Near(txAfterFlipSnap, 90f, 0.25f);

            engine.SnapAuthoredPose(node);
            float rotRest = RotOf(scene.Paint(node).LocalTransform);
            float txRest = scene.Paint(node).LocalTransform.Dx;
            float tyRest = scene.Paint(node).LocalTransform.Dy;
            bool atRest = Near(rotRest, -11f, 0.25f) && Near(txRest, 100f, 0.25f) && Near(tyRest, 38f, 0.25f);

            Check("gate.anim.while.snapRestoresAuthoredPose", wasFan && flipKeptRest && atRest,
                $"wasFan={wasFan} txFan={txFan:0.0} flipTx={txAfterFlipSnap:0.0} rest rot={rotRest:0.0} tx={txRest:0.0} ty={tyRest:0.0}");
        }
    }

    static void RotationCurrentValueChecks(StringTable strings)
    {
        // gate.anim.rotation.currentValue — CurrentValue's Rotation arm (AnimScheduler.cs) must recover the LIVE
        // angle (atan2 decompose, matching Accum.FromPaint exactly) so an eased `from: null` retarget departs from
        // where the node ALREADY IS, not from 0. Before this arm existed, a rotated node fed through
        // SeedValue/SeedChannel with no explicit `from` would visibly snap to 0° and ease FROM there — a fan would
        // pop to flat before re-fanning.
        var scene = new SceneStore();
        var engine = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, strings) { Anim = engine };
        recon.ReconcileRoot(new BoxEl { Width = 40, Height = 40, Rotation = 30f }, null);   // static authored rotation, no track yet
        var node = scene.Root;
        float live = RotOf(scene.Paint(node).LocalTransform);

        engine.SeedValue(node, AnimChannel.Rotation, 60f, MotionTok.ControlNormal, from: null);
        bool gotTrack = engine.TryGetTrackValue(node, AnimChannel.Rotation, out float seededFrom);

        Check("gate.anim.rotation.currentValue",
            gotTrack && Near(seededFrom, live, 0.25f) && !Near(seededFrom, 0f, 5f),
            $"live={live:0.0} seeded-from={seededFrom:0.0}");
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

        // M4a: the trigger-deactivated return is a ONE-SHOT from the LIVE translate back to 0 — the pure shape. The
        // engine seeds Keyframes from keys[0] (not the live row), so the departure value must be the first key; the
        // duration is paced at 4×Speed and clamped 120..450 ms; an already-home offset degenerates to a 1 ms no-op at 0.
        var homeStyle = new Marquee.Style { Speed = 200f, Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.Hover };
        var (homeKeys, homeDur, homeLoop) = MarqueeScroller.HomeTrack(-120f, homeStyle);
        var (noopKeys, noopDur, noopLoop) = MarqueeScroller.HomeTrack(-0.2f, homeStyle);
        bool homeShape = homeKeys.Length >= 2 && Near(homeKeys[0].Value, -120f, 0.001f) && Near(homeKeys[^1].Value, 0f, 0.001f)
                         && !homeLoop && homeDur >= 120f && homeDur <= 450f;
        bool noopShape = noopKeys.Length >= 2 && noopKeys[0].Value == 0f && noopKeys[^1].Value == 0f && !noopLoop && noopDur <= 1f;
        Check("M4a. marquee HomeTrack returns from the live translate to 0 as a one-shot",
              homeShape && noopShape,
              $"home: k0={homeKeys[0].Value:0.##} kN={homeKeys[^1].Value:0.##} dur={homeDur:0.#} loop={homeLoop}; noop: k0={noopKeys[0].Value:0.##} kN={noopKeys[^1].Value:0.##} dur={noopDur:0.#} loop={noopLoop}");

        // M4b: behavioural — a HOVER-triggered ping-pong marquee (external gate, the player-bar shape) scrolls out while
        // hovered; on hover-leave it must GLIDE home (|translate| strictly non-increasing, through intermediate frames —
        // not a park at -tailDist and not a 0→0 snap) and land at 0, where the host's fade is the right-edge cue only
        // (the left band the mid-scroll frames raised has cleared — the ticker keeps mirroring the live translate).
        var probe4 = new MarqueeHoverHomeProbe();
        var window4 = new HeadlessWindow(new WindowDesc("marquee-home", new Size2(220, 120), 1f)); window4.Show();
        using var host4 = new AppHost(app, window4, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe4);
        for (int i = 0; i < 8; i++) host4.RunFrame();                        // mount at rest, not hovered: translate stays 0
        float restBefore = MaxAbsTrackX(host4, host4.Scene.Root);
        probe4.Hovered.Value = true;                                          // hover enters: the ping-pong track seeds and runs
        float peak = 0f;
        int outFrames = 0;
        while (outFrames++ < 120 && peak <= 20f) { host4.RunFrame(); peak = MaxAbsTrackX(host4, host4.Scene.Root); }
        float leftBandWhileOut = MaxEdgeFadeLeftBand(host4.Scene, host4.Scene.Root);
        probe4.Hovered.Value = false;                                         // hover leaves: glide HOME from the live translate
        float prev = float.MaxValue, last = peak;
        bool monotone = true, glided = false;
        for (int i = 0; i < 40; i++)
        {
            host4.RunFrame();
            float cur = MaxAbsTrackX(host4, host4.Scene.Root);
            if (cur > prev + 0.01f) monotone = false;
            if (cur > 0.5f && cur < peak - 0.5f) glided = true;               // an intermediate frame: a glide, not a snap
            prev = cur; last = cur;
        }
        float leftBandEnd = MaxEdgeFadeLeftBand(host4.Scene, host4.Scene.Root);
        float rightBandEnd = MaxEdgeFadeRightBand(host4.Scene, host4.Scene.Root);
        Check("M4b. marquee hover-leave glides home and the left fade clears",
              restBefore < 0.5f && peak > 20f && leftBandWhileOut > 0.5f && monotone && glided && last < 0.5f
              && leftBandEnd <= 0.001f && rightBandEnd > 0.5f,
              $"rest={restBefore:0.##} peak={peak:0.##} outFrames={outFrames - 1} leftOut={leftBandWhileOut:0.##} monotone={monotone} glided={glided} last={last:0.###} leftEnd={leftBandEnd:0.##} rightEnd={rightBandEnd:0.##}");
    }

    static float MaxEdgeFadeLeftBand(SceneStore s, NodeHandle n)
    {
        float best = 0f;
        if (s.TryGetEdgeFade(n, out var ef)) best = MathF.Max(best, ef.Band(EdgeMask.Left));
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            best = MathF.Max(best, MaxEdgeFadeLeftBand(s, c));
        return best;
    }

    static float MaxEdgeFadeRightBand(SceneStore s, NodeHandle n)
    {
        float best = 0f;
        if (s.TryGetEdgeFade(n, out var ef)) best = MathF.Max(best, ef.Band(EdgeMask.Right));
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            best = MathF.Max(best, MaxEdgeFadeRightBand(s, c));
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
        var first = SceneRecorder.Record(scene, dl, spans: spans, collectSpanReuseMisses: true);
        scene.ClearRecordDirty();

        ap.Fill = ColorF.FromRgba(0x40, 0xC0, 0x70);
        scene.Mark(a, NodeFlags.PaintDirty);
        var dirty = SceneRecorder.Record(scene, dl, spans: spans, collectSpanReuseMisses: true);
        bool dirtyBranch = dirty.SpanReuseDisabledReasons == SpanReuseDisabledReason.None
            && dirty.SpansReused >= 1
            && dirty.SpansReRecorded >= 2
            && dirty.SpanBytesCopied > 0;
        scene.ClearRecordDirty();

        byte[] dirtyBytes = dl.Bytes.ToArray();
        ulong[] dirtySort = dl.SortKeys.ToArray();
        int dirtyCommands = dl.CommandCount;
        var steady = SceneRecorder.Record(scene, dl, spans: spans, collectSpanReuseMisses: true);
        // Retained tiles: a clean root slice is KEPT whole — its arena untouched, zero bytes copied or recorded — and the
        // flattened stream is byte-identical to the last.
        bool steadyCopy = steady.SpanReuseDisabledReasons == SpanReuseDisabledReason.None
            && steady.SpansReused == 1
            && steady.SpansReRecorded == 0
            && steady.SpanBytesCopied == 0
            && steady.Slices.KeptAll && steady.Slices.BytesRecorded == 0
            && dl.CommandCount == dirtyCommands
            && dl.Bytes.SequenceEqual(dirtyBytes)
            && dl.SortKeys.SequenceEqual(dirtySort);

        float steadyFirstDx = FirstFillDx(dl.Bytes);
        ref var rp = ref scene.Paint(root);
        rp.LocalTransform = Affine2D.Translation(25f, 7f);
        scene.Mark(root, NodeFlags.TransformDirty);
        var moved = SceneRecorder.Record(scene, dl, spans: spans);
        float movedFirstDx = FirstFillDx(dl.Bytes);
        // A moved STATIC root re-records (its transform is recorded content); nothing is ever rebased by translation.
        bool transformRecord = moved.SpanReuseDisabledReasons == SpanReuseDisabledReason.None
            && moved.SpansReRecorded == 3
            && dl.CommandCount == dirtyCommands
            && Near(movedFirstDx, steadyFirstDx + 25f);

        // scroll-root-cause-2026-09-23 §5.2 (Part B): the SpanMiss* reason counters are ALWAYS ON now (cheap int
        // increments against state the recorder already computed) in every build configuration — no longer compiled
        // out of Release nor gated behind an opt-in the host never set. `Diag.CompiledIn` no longer has anything to
        // do with whether these are populated, so these two checks no longer branch on it.
        Check("P6.clean-span first record populates spans under the normal recorder path",
            (first.SpanReuseDisabledReasons & SpanReuseDisabledReason.FirstRecord) != 0
            && first.SpansReRecorded >= 3
            && first.SpanReuseMisses.GlobalDisabled >= first.SpansReRecorded,
            $"firstReason={first.SpanReuseDisabledReasons} recorded={first.SpansReRecorded} missDisabled={first.SpanReuseMisses.GlobalDisabled}");
        Check("P6.clean-span dirty child re-records ancestors while reusing a clean sibling",
            dirtyBranch && dirty.SpanReuseMisses.ExactDirty > 0,
            $"reused={dirty.SpansReused} recorded={dirty.SpansReRecorded} copied={dirty.SpanBytesCopied} exactDirty={dirty.SpanReuseMisses.ExactDirty}");
        Check("gate.slices.keep-whole a steady frame KEEPS the clean root slice whole (0 bytes copied or recorded) and flattens byte-identically",
            steadyCopy && steady.SpanReuseMisses == default,
            $"reused={steady.SpansReused} recorded={steady.SpansReRecorded} copied={steady.SpanBytesCopied} keptAll={steady.Slices.KeptAll} bytes={steady.Slices.BytesRecorded} misses={steady.SpanReuseMisses}");
        Check("gate.slices.static-transform-records a moved STATIC root re-records its subtree (a static slice's transform is recorded content — nothing is rebased)",
            transformRecord, $"reused={moved.SpansReused} recorded={moved.SpansReRecorded} firstDx={steadyFirstDx:0.##}->{movedFirstDx:0.##}");

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
        Check("P6.v2 a descendant transform re-records the moved child and its ancestor chain, nothing else",
            descMoved.SpansReRecorded == 2 && Near(descMovedDx, 12f),
            $"recorded={descMoved.SpansReRecorded} firstDx={descMovedDx:0.##}");

        var scrollScene = new SceneStore();
        var viewport = scrollScene.CreateNode(1);
        var content = scrollScene.CreateNode(1);
        var rowA = scrollScene.CreateNode(1);
        var rowB = scrollScene.CreateNode(1);
        scrollScene.Root = viewport;
        scrollScene.AppendChild(viewport, content);
        scrollScene.AppendChild(content, rowA);
        scrollScene.AppendChild(content, rowB);
        scrollScene.SetFlagBits(viewport, NodeFlags.ClipsToBounds);
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
        var scrollRec = new SlicedRecording();
        _ = scrollRec.Record(scrollScene, scrollDl, scrollSpans);
        bool initialScrollColor = SameColor(FirstFillColor(scrollDl.Bytes), rowAColor);
        scrollScene.ClearRecordDirty();

        scrollScene.Paint(content).LocalTransform = Affine2D.Translation(0f, -70f);
        scrollScene.Mark(content, NodeFlags.TransformDirty);
        var scrollMoved = scrollRec.Record(scrollScene, scrollDl, scrollSpans);
        // Retained tiles: the content is a SCROLL slice recorded pose-free; the composite places it at the posed offset and
        // culls the row that left the viewport — the first fill on screen is the entering row, and no row re-recorded.
        bool enteringRowShown = initialScrollColor
            && SameColor(FirstFillColor(scrollDl.Bytes), rowBColor)
            && scrollMoved.SpansReused >= 2;
        Check("gate.slices.scroll-flatten moving scroll content shows the entering row at its posed offset and culls the row that left, re-recording no row",
            enteringRowShown,
            $"recorded={scrollMoved.SpansReRecorded} reused={scrollMoved.SpansReused} firstFill={FirstFillColor(scrollDl.Bytes)}");

        scrollScene.ClearTransformDirty();
        scrollScene.ClearRecordDirty();
        scrollScene.Paint(content).LocalTransform = Affine2D.Translation(0f, -80f);
        var scrollMovedAgain = scrollRec.Record(scrollScene, scrollDl, scrollSpans);
        bool poseOnly = SameColor(FirstFillColor(scrollDl.Bytes), rowBColor)
            && scrollMovedAgain.Slices.KeptAll && scrollMovedAgain.Slices.BytesRecorded == 0
            && Near(FirstFillDy(scrollDl.Bytes), 130f - 80f);
        Check("gate.slices.pose-only-records-nothing a content pose change with nothing else dirty records 0 bytes (every slice kept) and the composite re-places the rows",
            poseOnly,
            $"keptAll={scrollMovedAgain.Slices.KeptAll} bytes={scrollMovedAgain.Slices.BytesRecorded} firstDy={FirstFillDy(scrollDl.Bytes):0.##}");

        static float FirstFillDy(ReadOnlySpan<byte> bytes)
        {
            int pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
                pos += sizeof(int);
                if (op == DrawOp.FillRoundRect)
                    return MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos, Unsafe.SizeOf<FillRoundRectCmd>())).Transform.Dy;
                pos += DrawPayloadSize(op);
            }
            return float.NaN;
        }

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
            s.SetFlagBits(root, NodeFlags.ClipsToBounds);
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

        // gate.span.storeGapHealed — a drag-ghost frame kills span REUSE globally, but it must not also skip the span
        // STORE: SpanTable only accepts an entry recorded on the immediately preceding frame, so one store-less frame
        // rejected the whole table on the frame after it too (a full-canvas re-record for a single ghost frame). The
        // ghost's own ancestor chain is the one thing that must still not store — it records without the lifted row.
        {
            var s = new SceneStore();
            var root = s.CreateNode(1); s.Root = root;
            s.Bounds(root) = new RectF(0, 0, 240, 120);
            var list = SB(s, root, new RectF(0, 0, 120, 120), ColorF.FromRgba(0x10, 0x30, 0x50));
            var other = SB(s, root, new RectF(120, 0, 120, 120), ColorF.FromRgba(0x50, 0x30, 0x10));
            var row = SB(s, list, new RectF(10, 10, 80, 20), ColorF.FromRgba(0x90, 0x90, 0x90));
            _ = SB(s, other, new RectF(10, 10, 80, 20), ColorF.FromRgba(0x30, 0x30, 0x30));

            var dl = new DrawList();
            var spans = new SpanTable();
            _ = SceneRecorder.Record(s, dl, spans: spans);
            s.ClearRecordDirty();
            var steady = SceneRecorder.Record(s, dl, spans: spans);   // baseline: reuse is live before the ghost

            // The ghost frame: reuse dies canvas-wide (GlobalReuseKill), but every unblocked node still stores.
            s.SetFlagBits(row, NodeFlags.DragGhost);
            s.DragGhost = row;
            var ghostFrame = SceneRecorder.Record(s, dl, spans: spans);
            uint gf = spans.CurrentFrameId;
            bool ghostChainWithheld = !spans.StoredAtFrame((int)list.Raw.Index, gf)
                                      && !spans.StoredAtFrame((int)root.Raw.Index, gf);
            bool bystanderStored = spans.StoredAtFrame((int)other.Raw.Index, gf);

            // Drop the ghost. The bystander subtree stored on the ghost frame, so it is still recent enough to reuse;
            // only the ghost's own chain (which withheld its store) has to re-record.
            s.ClearFlagBits(row, NodeFlags.DragGhost);
            s.DragGhost = NodeHandle.Null;
            var afterGhost = SceneRecorder.Record(s, dl, spans: spans);

            Check("gate.span.storeGapHealed",
                steady.SpansReused > 0 && ghostFrame.SpansReused == 0
                && ghostChainWithheld && bystanderStored && afterGhost.SpansReused > 0,
                $"steady={steady.SpansReused} ghostReused={ghostFrame.SpansReused} chainWithheld={ghostChainWithheld} bystanderStored={bystanderStored} after={afterGhost.SpansReused} reasons={ghostFrame.SpanReuseDisabledReasons}");
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

    // ── Scrolled content through the slice partition (retained tiles P1) ────────────────────────────────────────────
    // Scroll content records POSE-FREE into its own slice and the flatten places it at the posed offset — glyph runs,
    // clips and layers (acrylic included) alike, re-recording no row. These gates pin the byte-level result of the
    // flatten (what moved, crisp text, the self-blur layer rect while it moves) and the stationary-neighbour exact-copy.
    // ── The record walk's DEPTH BUDGET ─────────────────────────────────────────────────────────────────────────────
    // SceneRecorder.Walk is recursive with a large frame. Its stack-headroom guard (HasWalkStackHeadroom) degrades by NOT
    // painting the deepest subtree instead of overflowing — a Debug build once painted every detail page's row skins with
    // no row content for a whole session, silently, because the guard tripped at row-content depth on the apphost's
    // 1.5 MB main-thread stack and nothing counted it. Two contracts are pinned here:
    //   · the DEGRADE contract — on a thread whose stack really is too small, the walk survives, the shallow ancestors'
    //     draws are still emitted, the deepest glyph is NOT, and SceneRecordStats.DepthAborts says so (never silent);
    //   · the BUDGET contract — on a thread with the reserve FluentApp.RunCore gives the UI thread (32 MB), a tree far
    //     deeper than any real page (200 levels) records COMPLETELY: DepthAborts == 0 and the deepest glyph is present —
    //     in Debug AND Release, whatever the JIT makes of WalkCore's frame.
    static void RecordDepthBudgetChecks(StringTable strings)
    {
        const int Depth = 200;
        const int UiThreadStackBytes = 32 * 1024 * 1024;   // == FluentApp.UiThreadStackBytes (FluentGpu.Windows; not referenced from the TerraFX-free slice)
        const int TinyStackBytes = 256 * 1024;

        // A Box → Box → … → Text chain, Depth levels deep, every level a real visual so the shallow draws are countable.
        static (SceneStore scene, NodeHandle root, NodeHandle leaf) BuildChain(StringTable strings, int depth)
        {
            var scene = new SceneStore();
            var root = scene.CreateNode(1);
            scene.Root = root;
            scene.Bounds(root) = new RectF(0f, 0f, 400f, 400f);
            ref var rp = ref scene.Paint(root); rp = NodePaint.Default; rp.VisualKind = VisualKind.Box; rp.Fill = ColorF.FromRgba(0x20, 0x20, 0x20);
            var parent = root;
            for (int i = 1; i < depth; i++)
            {
                var n = scene.CreateNode(1);
                scene.AppendChild(parent, n);
                scene.Bounds(n) = new RectF(0.5f, 0.5f, 400f - i, 400f - i);
                ref var p = ref scene.Paint(n); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = ColorF.FromRgba((byte)(i & 0xFF), 0x40, 0x40);
                parent = n;
            }
            var leaf = scene.CreateNode(1);
            scene.AppendChild(parent, leaf);
            scene.Bounds(leaf) = new RectF(0f, 0f, 120f, 20f);
            ref var lp = ref scene.Paint(leaf); lp = NodePaint.Default; lp.VisualKind = VisualKind.Text; lp.Text = strings.Intern("deepest");
            return (scene, root, leaf);
        }

        static bool HasGlyph(ReadOnlySpan<byte> bytes)
        {
            int pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
                pos += sizeof(int);
                if (op == DrawOp.DrawGlyphRun) return true;
                pos += DrawPayloadSize(op);
            }
            return false;
        }

        // Record on a thread of a given stack size and hand back what the walk produced. The recorder is thread-agnostic
        // (a DrawList is bytes; the store is read-only during Record), so the same call on two stack sizes isolates the
        // ONE variable under test.
        static (SceneRecordStats stats, bool glyph, int cmds, Exception? error) RecordOn(int stackBytes, StringTable strings)
        {
            SceneRecordStats stats = default; bool glyph = false; int cmds = 0; Exception? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var (scene, _, _) = BuildChain(strings, Depth);
                    var dl = new DrawList();
                    stats = SceneRecorder.Record(scene, dl);
                    glyph = HasGlyph(dl.Bytes);
                    cmds = dl.CommandCount;
                }
                catch (Exception ex) { error = ex; }
            }, stackBytes) { IsBackground = true, Name = "gate-record-depth" };
            t.Start(); t.Join();
            return (stats, glyph, cmds, error);
        }

        var tiny = RecordOn(TinyStackBytes, strings);
        Check("gate.record.depth-abort-counts a 200-deep chain on a 256 KB stack SURVIVES, still emits the shallow ancestors' draws, drops the deepest glyph, and COUNTS it in SceneRecordStats.DepthAborts (the degrade contract — never a silent blank)",
            tiny.error is null && tiny.stats.DepthAborts > 0 && !tiny.glyph && tiny.cmds > 0,
            $"error={tiny.error?.GetType().Name} depthAborts={tiny.stats.DepthAborts} glyph={tiny.glyph} cmds={tiny.cmds}");

        var ui = RecordOn(UiThreadStackBytes, strings);
        Check("gate.record.depth-budget-ui-stack the same 200-deep chain on the UI thread's 32 MB reserve records COMPLETELY: DepthAborts == 0 and the deepest glyph is emitted (Debug and Release alike)",
            ui.error is null && ui.stats.DepthAborts == 0 && ui.glyph,
            $"error={ui.error?.GetType().Name} depthAborts={ui.stats.DepthAborts} glyph={ui.glyph} cmds={ui.cmds}");
    }

    static void SpanTranslateRebaseChecks(StringTable strings)
    {
        const float Shift = -20f;
        const float InteriorClipW = 150f;

        // Stand-in for the deleted SceneRecorder.MotionSoftFullDip (audit 2026-09-22, cause #2: TextMotionSoftness
        // itself is gone) — these gates only need SOME "the viewport is moving fast" speed to drive TestApplyScroll;
        // text softness no longer reads it, so any big number does.
        const float FastScrollSpeedDip = 1400f;

        // gate.span.textRowScrollRebase — three text rows (Box → interior ClipsToBounds child → Text leaf) plus one
        // self-blur row, all fully inside a scrolling viewport. One content translation ⇒ every row REBASES, and the
        // copied bytes decode to the shifted geometry a fresh record would have emitted: glyph transform, the
        // interior clip rect, and the blur layer's rect. Text's own InMotion is asserted 0 in EVERY frame regardless of
        // scroll speed (cause #2: no more speed-ramped glyph softening).
        {
            var s = BuildRows(strings, LayerKind.Blur, out var content, out _, out _);
            var dl = new DrawList();
            var spans = new SpanTable();
            var rec = new SlicedRecording();
            _ = rec.Record(s, dl, spans);
            s.ClearRecordDirty();

            bool g0 = FirstGlyph(dl.Bytes, out var glyph0);
            bool c0 = ClipOfWidth(dl.Bytes, InteriorClipW, out var clip0);
            bool b0 = LayerOfKind(dl.Bytes, LayerKind.Blur, out var blur0);
            bool have0 = g0 && c0 && b0;
            bool rest0 = glyph0.InMotion == 0;

            TestApplyScroll(s, s.Root, liveSpeedDip: FastScrollSpeedDip);
            s.Paint(content).LocalTransform = Affine2D.Translation(0f, Shift);
            s.Mark(content, NodeFlags.TransformDirty);
            var moved = rec.Record(s, dl, spans);

            bool g1 = FirstGlyph(dl.Bytes, out var glyph1);
            bool c1 = ClipOfWidth(dl.Bytes, InteriorClipW, out var clip1);
            bool b1 = LayerOfKind(dl.Bytes, LayerKind.Blur, out var blur1);
            bool have1 = g1 && c1 && b1;
            // gate.record.textCrispDuringFastScroll: this viewport is moving at FastScrollSpeedDip — the OLD behavior
            // would have shifted glyph1.InMotion to full softness (255) here; text now stays crisp (0) at ANY speed.
            bool glyphShifted = Near(glyph1.Transform.Dy, glyph0.Transform.Dy + Shift)
                                && Near(glyph1.Transform.Dx, glyph0.Transform.Dx)
                                && glyph1.InMotion == 0;
            bool clipShifted = Near(clip1.DeviceRect.Y, clip0.DeviceRect.Y + Shift)
                               && Near(clip1.DeviceRect.X, clip0.DeviceRect.X)
                               && Near(clip1.DeviceRect.H, clip0.DeviceRect.H);
            bool blurShifted = Near(blur1.DeviceRect.Y, blur0.DeviceRect.Y + Shift)
                               && Near(blur1.DeviceRect.X, blur0.DeviceRect.X);

            Check("gate.slices.textRowScrollFlatten a scrolled content slice composites at the shifted geometry a fresh record emits — glyph transform, interior clip rect, self-blur layer rect — with no row re-recorded and text crisp",
                have0 && rest0 && have1 && moved.SpansReused >= 4 && glyphShifted && clipShifted && blurShifted,
                $"reused={moved.SpansReused} recorded={moved.SpansReRecorded} "
                + $"glyphDy={glyph0.Transform.Dy:0.##}->{glyph1.Transform.Dy:0.##}/im{glyph1.InMotion} "
                + $"clipY={clip0.DeviceRect.Y:0.##}->{clip1.DeviceRect.Y:0.##} "
                + $"blurY={blur0.DeviceRect.Y:0.##}->{blur1.DeviceRect.Y:0.##} decoded={have0}/{have1}");
        }

        // gate.span.rebaseSettleResnap — before audit 2026-09-22 (cause #2) a rebased row briefly carried a MOTION-only
        // InMotion=1 byte the settle frame had to re-snap to 0; TextMotionSoftness is deleted, so text is InMotion=0 in
        // the moved, settle AND at-rest frame alike — this gate now just proves rebase + eventual exact-copy reuse
        // still agree on position across all three, with text staying crisp throughout (never a transient soft byte).
        // NB the settle frame must actually REACH the row: an ancestor whose own key is unchanged legitimately
        // exact-copies the motion frame wholesale. The scrollbar fade tick is what walks it in the real engine (FadeT
        // decays for ~450 ms after a gesture and is part of every viewport's span key), so the gate ticks it too.
        {
            var s = BuildRows(strings, LayerKind.Blur, out var content, out var firstRow, out var viewport);
            var dl = new DrawList();
            var spans = new SpanTable();
            var rec = new SlicedRecording();
            _ = rec.Record(s, dl, spans);
            s.ClearRecordDirty();

            // A REAL (persisted) offset, not a hand-poked LocalTransform: TestApplyScroll — both calls below go through
            // it — unconditionally RECOMPUTES the content's LocalTransform from the viewport's CURRENT offset every call
            // (ScrollContentPose.WriteContentTransform), exactly
            // as it does in the real engine (a settle write re-asserts the position the user is actually AT — offset
            // doesn't teleport back to 0 just because the gesture ended). The original hand-set
            // `s.Paint(content).LocalTransform = Affine2D.Translation(0f, Shift)` bypassed the sink for the "moved"
            // frame, so the settle call's real recompute (from the never-changed OffsetY=0) silently undid it —
            // glyphS.Transform.Dy snapped back to the row's UNshifted position instead of matching glyphM's shifted
            // one, the actual root cause of this gate's failure (confirmed via a temporary Dy trace: glyphM.Dy=25 vs
            // glyphS.Dy=45 — the row's true base Y). Setting a real OffsetY BEFORE the "moved" call, and never
            // touching it again, makes the "moved" and "settle" sink calls agree — same offset in, same transform
            // out — exactly like a real scroll that settles AT the position it was scrolled to.
            TestApplyScroll(s, viewport, liveSpeedDip: FastScrollSpeedDip, offsetY: -Shift);   // Dy = -(offset+band) — matches the old hand-set Shift's sign/magnitude
            var moved = rec.Record(s, dl, spans);
            bool movedCrisp = FirstGlyph(dl.Bytes, out var glyphM)
                                 && glyphM.InMotion == 0
                                 && moved.SpansReused >= 1;

            // settle: the transform write is over (the host clears the bits right after record) and the scrollbar fades.
            s.ClearTransformDirty();
            s.ClearRecordDirty();
            TestApplyScroll(s, viewport, liveSpeedDip: 0f);   // OffsetY unchanged since the "moved" call — a real settle, not a snap-to-0
            // scroll-v3: FadeT/ExpandT moved out of ScrollState into the chrome side-table (scroll-v3 plan §3.1),
            // SceneStore.ScrollChrome (a ScrollBarChromeTable). GetOrAddRow is internal (ScrollBarChrome.Tick's own
            // ref-write access) but reachable here via the assembly's InternalsVisibleTo("FluentGpu.VerticalSlice")
            // grant — the same low-level unit poke this gate wanted, bypassing the ScrollBarChrome ticker.
            s.ScrollChrome.GetOrAddRow((int)viewport.Raw.Index).FadeT = 0.5f;
            var settle = rec.Record(s, dl, spans);
            bool resnapped = FirstGlyph(dl.Bytes, out var glyphS) && glyphS.InMotion == 0
                             && Near(glyphS.Transform.Dy, glyphM.Transform.Dy);

            // and once crisp it settles into plain exact-copy — no per-frame re-record tail.
            s.ClearRecordDirty();
            var atRest = rec.Record(s, dl, spans);
            bool steady = atRest.SpansReused >= 1 && atRest.Slices.KeptAll
                          && FirstGlyph(dl.Bytes, out var glyphR) && glyphR.InMotion == 0 && Near(glyphR.Transform.Dy, glyphM.Transform.Dy);

            Check("gate.slices.scrollSettle a scroll that settles keeps its position through the moved, settle and at-rest frames with text crisp throughout, and settles into keeping every slice whole",
                movedCrisp && resnapped && steady,
                $"movedIm={glyphM.InMotion} reused={moved.SpansReused} settleIm={glyphS.InMotion} "
                + $"settleRec={settle.SpansReRecorded} restReused={atRest.SpansReused} restKeptAll={atRest.Slices.KeptAll}");
        }

        // gate.record.textCrispDuringFastScroll (audit 2026-09-22, cause #2) — the actual regression test for the
        // deleted TextMotionSoftness: two INDEPENDENT, structurally identical scenes (a viewport scrolling a content
        // node with one text leaf), one whose viewport reports a slow/idle live speed and one reporting a committed-
        // fling speed (FastScrollSpeedDip) — each recorded fresh into its own DrawList/SceneStore, so there is no
        // span table or dirty-bit history that could short-circuit either walk into a cached result. The OLD behavior
        // would have stamped the fast one's glyph InMotion at full softness (255); text must now be BIT-IDENTICAL
        // (Transform, InMotion, SpanRunId, ForceColor) between the two, proving speed no longer reaches the glyph at
        // all, not just "less blurred than before".
        {
            static (DrawGlyphRunCmd glyph, bool have) RecordAtSpeed(StringTable strings, float speedDip)
            {
                var s = new SceneStore();
                var viewport = s.CreateNode(1);
                var content = s.CreateNode(1);
                s.Root = viewport;
                s.AppendChild(viewport, content);
                s.SetFlagBits(viewport, NodeFlags.ClipsToBounds);
                s.ScrollRef(viewport).ContentNode = content;
                s.Bounds(viewport) = new RectF(0, 0, 200, 400);
                s.Bounds(content) = new RectF(0, 0, 200, 800);
                AddText(s, strings, content, new RectF(8, 6, 120, 18), "scrolling row");

                TestApplyScroll(s, viewport, liveSpeedDip: speedDip);
                var dl = new DrawList();
                _ = SceneRecorder.Record(s, dl);
                bool have = FirstGlyph(dl.Bytes, out var glyph);
                return (glyph, have);
            }

            var (glyphRest, haveRest) = RecordAtSpeed(strings, 0f);
            var (glyphFast, haveFast) = RecordAtSpeed(strings, FastScrollSpeedDip);

            bool bitIdentical = haveRest && haveFast
                && glyphRest.Transform == glyphFast.Transform
                && glyphRest.InMotion == glyphFast.InMotion
                && glyphRest.SpanRunId == glyphFast.SpanRunId
                && glyphRest.ForceColor == glyphFast.ForceColor
                && glyphRest.InMotion == 0;   // and that shared value is crisp, not merely equal to itself

            Check("gate.record.textCrispDuringFastScroll",
                bitIdentical,
                $"restIm={glyphRest.InMotion} fastIm={glyphFast.InMotion} "
                + $"restDy={glyphRest.Transform.Dy:0.##} fastDy={glyphFast.Transform.Dy:0.##} have={haveRest}/{haveFast}");
        }

        // gate.span.acrylicNeverTranslates — an ACRYLIC layer blurs whatever the canvas holds UNDER its rect, so the same
        // bytes at a new position would composite the OLD backdrop. Its span must be refused by the per-payload walk (the
        // partial copy rolled back) and re-recorded, while its plain siblings keep rebasing in the same frame.
        {
            var s = BuildRows(strings, LayerKind.Acrylic, out var content, out _, out _);
            var dl = new DrawList();
            var spans = new SpanTable();
            var rec = new SlicedRecording();
            _ = rec.Record(s, dl, spans);
            s.ClearRecordDirty();

            bool hadAcrylic = LayerOfKind(dl.Bytes, LayerKind.Acrylic, out var acr0);

            s.Paint(content).LocalTransform = Affine2D.Translation(0f, Shift);
            s.Mark(content, NodeFlags.TransformDirty);
            var moved = rec.Record(s, dl, spans);
            // The acrylic is still emitted — FRESHLY, at the new position (a re-record, not a copy).
            bool freshAcrylic = LayerOfKind(dl.Bytes, LayerKind.Acrylic, out var acr1)
                                && Near(acr1.DeviceRect.Y, acr0.DeviceRect.Y + Shift);

            Check("gate.slices.acrylicFlattensAtPose an ACRYLIC row inside scrolled content is an effect slice composited at its posed position (its backdrop is the composite of the slices beneath it there), its siblings re-recording nothing",
                hadAcrylic && freshAcrylic && moved.SpansReused >= 3,
                $"reused={moved.SpansReused} recorded={moved.SpansReRecorded} "
                + $"acrylicY={acr0.DeviceRect.Y:0.##}->{acr1.DeviceRect.Y:0.##}");
        }

        // gate.span.stationaryReusesDuringScroll — sticky/pinned chrome that lives beside the moving content does not
        // move, so it must EXACT-copy (branch A) even while its viewport is mid-gesture. The exact-copy branch used to
        // carry `!scrollInMotion`, which killed reuse for the viewport's whole subtree — every pinned header re-recorded
        // for the duration of every scroll — despite the world transform, inMotion and userScrollActive all being in the
        // span key already, which is what actually stops a MOVED span from matching.
        {
            var s = new SceneStore();
            var viewport = s.CreateNode(1);
            var pinned = s.CreateNode(1);
            var content = s.CreateNode(1);
            var row = s.CreateNode(1);
            s.Root = viewport;
            s.AppendChild(viewport, pinned);
            s.AppendChild(viewport, content);
            s.AppendChild(content, row);
            s.SetFlagBits(viewport, NodeFlags.ClipsToBounds);
            s.ScrollRef(viewport).ContentNode = content;
            s.Bounds(viewport) = new RectF(0, 0, 200, 400);
            s.Bounds(pinned) = new RectF(0, 0, 200, 30);
            s.Bounds(content) = new RectF(0, 40, 200, 800);
            s.Bounds(row) = new RectF(0, 40, 200, 50);
            Paint(s, pinned, VisualKind.Box, ColorF.FromRgba(0x11, 0x22, 0x33));
            Paint(s, row, VisualKind.Box, ColorF.FromRgba(0x44, 0x55, 0x66));
            AddText(s, strings, pinned, new RectF(8, 6, 120, 18), "pinned header");
            AddText(s, strings, row, new RectF(8, 6, 120, 18), "scrolling row");

            var dl = new DrawList();
            var spans = new SpanTable();
            var rec = new SlicedRecording();
            _ = rec.Record(s, dl, spans);
            s.ClearRecordDirty();

            // f2 — the gesture starts: userScrollActive flips, which legitimately re-keys the whole viewport subtree once.
            TestApplyScroll(s, viewport, FluentGpu.Scroll.Motion.MotionKind.Drag);
            s.Paint(content).LocalTransform = Affine2D.Translation(0f, Shift);
            s.Mark(content, NodeFlags.TransformDirty);
            _ = rec.Record(s, dl, spans);

            // f3 — steady scroll: nothing about the pinned header changed. Only the viewport (descendant transform) and
            // the content node itself (the direct moving scroll content) may re-record; the row rebases and the pinned
            // header — with its own text child, never walked — exact-copies.
            s.ClearTransformDirty();
            s.ClearRecordDirty();
            s.Paint(content).LocalTransform = Affine2D.Translation(0f, 2f * Shift);
            s.Mark(content, NodeFlags.TransformDirty);
            var scrolling = rec.Record(s, dl, spans);

            // The pinned header exact-copies, the moving content's row copies inside its own (pose-free) slice, and only
            // the viewport and the content node itself (marked by the hand-written transform) re-record.
            Check("gate.span.stationaryReusesDuringScroll",
                scrolling.SpansReused == 2 && scrolling.SpansReRecorded == 2,
                $"reused={scrolling.SpansReused} recorded={scrolling.SpansReRecorded} reasons={scrolling.SpanReuseDisabledReasons}");
        }

        // A scrolling viewport of text rows: Box row → interior ClipsToBounds child (an emitted PushClip/PopClip pair)
        // → Text leaf, plus one row carrying `special` as a layer. Every row sits well inside the viewport clip at both
        // ends of the shift, so span rebase eligibility (ClipComplete at both ends) is never the thing under test.
        static SceneStore BuildRows(StringTable strings, LayerKind special,
                                    out NodeHandle content, out NodeHandle firstRow, out NodeHandle viewport)
        {
            var s = new SceneStore();
            viewport = s.CreateNode(1);
            content = s.CreateNode(1);
            s.Root = viewport;
            s.AppendChild(viewport, content);
            s.SetFlagBits(viewport, NodeFlags.ClipsToBounds);
            s.ScrollRef(viewport).ContentNode = content;
            s.Bounds(viewport) = new RectF(0, 0, 200, 400);
            s.Bounds(content) = new RectF(0, 0, 200, 800);

            firstRow = NodeHandle.Null;
            for (int i = 0; i < 3; i++)
            {
                var row = s.CreateNode(1);
                s.AppendChild(content, row);
                s.Bounds(row) = new RectF(0, 40 + i * 60, 200, 50);
                Paint(s, row, VisualKind.Box, ColorF.FromRgba((byte)(0x20 + i * 0x10), 0x80, 0xE0));
                var clipChild = s.CreateNode(1);
                s.AppendChild(row, clipChild);
                s.Bounds(clipChild) = new RectF(10, 5, InteriorClipW, 40);
                s.SetFlagBits(clipChild, NodeFlags.ClipsToBounds);
                s.Paint(clipChild) = NodePaint.Default;
                AddText(s, strings, clipChild, new RectF(0, 0, 140, 20), "row text");
                if (i == 0) firstRow = row;
            }

            var layerRow = s.CreateNode(1);
            s.AppendChild(content, layerRow);
            s.Bounds(layerRow) = new RectF(30, 240, 140, 40);
            Paint(s, layerRow, VisualKind.Box, ColorF.FromRgba(0x90, 0x30, 0x30));
            if (special == LayerKind.Acrylic)
                s.SetAcrylic(layerRow, AcrylicSpec.InAppDefault);
            else
                s.Paint(layerRow).BlurSigma = 4f;
            return s;
        }

        static void Paint(SceneStore s, NodeHandle n, VisualKind kind, ColorF fill)
        {
            ref var p = ref s.Paint(n);
            p = NodePaint.Default;
            p.VisualKind = kind;
            p.Fill = fill;
        }

        static NodeHandle AddText(SceneStore s, StringTable strings, NodeHandle parent, RectF bounds, string text)
        {
            var n = s.CreateNode(1);
            s.AppendChild(parent, n);
            s.Bounds(n) = bounds;
            ref var p = ref s.Paint(n);
            p = NodePaint.Default;
            p.VisualKind = VisualKind.Text;
            p.Text = strings.Intern(text);
            return n;
        }

        static bool FirstGlyph(ReadOnlySpan<byte> bytes, out DrawGlyphRunCmd cmd)
        {
            cmd = default;
            int pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
                pos += sizeof(int);
                if (op == DrawOp.DrawGlyphRun) { cmd = MemoryMarshal.Read<DrawGlyphRunCmd>(bytes.Slice(pos)); return true; }
                pos += DrawPayloadSize(op);
            }
            return false;
        }

        static bool ClipOfWidth(ReadOnlySpan<byte> bytes, float width, out ClipCmd cmd)
        {
            cmd = default;
            int pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
                pos += sizeof(int);
                if (op == DrawOp.PushClip)
                {
                    var c = MemoryMarshal.Read<ClipCmd>(bytes.Slice(pos));
                    if (Near(c.DeviceRect.W, width)) { cmd = c; return true; }
                }
                pos += DrawPayloadSize(op);
            }
            return false;
        }

        static bool LayerOfKind(ReadOnlySpan<byte> bytes, LayerKind kind, out PushLayerCmd cmd)
        {
            cmd = default;
            int pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
                pos += sizeof(int);
                if (op == DrawOp.PushLayer)
                {
                    var l = MemoryMarshal.Read<PushLayerCmd>(bytes.Slice(pos));
                    if (l.Kind == (int)kind) { cmd = l; return true; }
                }
                pos += DrawPayloadSize(op);
            }
            return false;
        }
    }
}

// 23r.c probe — the real Skel.Region shape: an EMPTY pending branch (so the region reflows from ZERO) and an
// AUTO-height ready branch, wrapped in an Embed.Comp component anchor, with a plain sibling right below it.
// Deliberately declares NO ClipToBounds and NO SizeAnchor: everything the region gets, it gets from the reconciler's
// smooth-resize enrolment. `Bump` re-renders the wrapping component (an ordinary parent re-render) so the gate can
// drive MirrorParticipation WHILE the reflow is in flight — the window in which the anchor used to be frozen.
sealed class SkelOverpaintProbe : Component
{
    public const float ContentH = 160f;
    public static Loadable<int> Data = Loadable<int>.Pending(0);
    public static readonly Signal<int> Bump = new(0);

    sealed class RegionHost : Component
    {
        public override Element Render()
        {
            _ = Bump.Value;   // an ordinary parent-driven re-render → MirrorParticipation runs again
            return Skel.Region(Data,
                shimmerSource: () => new BoxEl(),                  // PENDING: literally empty ⇒ the reflow starts at 0
                content: _ => new BoxEl                            // READY: AUTO height (declared stays NaN)
                {
                    Direction = 1,
                    Children = [new BoxEl { Height = 40f }, new BoxEl { Height = 40f },
                                new BoxEl { Height = 40f }, new BoxEl { Height = 40f }],
                },
                reveal: SkelReveal.None);
        }
    }

    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            Embed.Comp(() => new RegionHost()),      // [0] the layout-transparent component anchor
            new BoxEl { Height = 40f },              // [1] the sibling the region must not paint over
        ],
    };
}

// 23r.d / 23r.e probe — TWO independent auto-height SizeMode.Reflow ENTRANTS (keyed, so each is a genuine MOUNT and the
// reconciler routes it through PendingEnterReflow → SeedEnterReflow: the only shape that carries AnimFlags.NaturalTarget,
// i.e. the row whose destination IS "whatever the content turns out to be"). `Grown` mounts an extra fixed-height child
// INSIDE entrant A mid-flight (23r.d); `Bump` re-renders the whole tree with identical props while entrant B is in
// flight (23r.e). Deliberately declares NO ClipToBounds: the clip 23r.e asserts survives the re-render is the one
// ReflowSize adds for the life of the row, which is observable in this shape and nowhere else.
sealed class ReflowGrowProbe : Component
{
    public const float BaseH = 40f, ExtraH = 60f, FullH = BaseH + ExtraH;
    public static readonly Signal<bool> MountedA = new(false);
    public static readonly Signal<bool> MountedB = new(false);
    public static readonly Signal<bool> Grown = new(false);
    public static readonly Signal<int> Bump = new(0);

    // Linear + a long duration keeps the per-tick ease delta flat, so "no frame-over-frame jump" is a clean bound
    // rather than a curve-shape argument. Enter carries no terminal (an all-default EnterExit) — `Active` alone is what
    // enrols the mount in the reflow reveal, so the reflow row is the ONLY row on the node.
    static readonly LayoutTransition ReflowEnter = new(
        TransitionChannels.Size,
        TransitionDynamics.Tween(333f, Easing.Linear),
        Size: SizeMode.Reflow,
        Enter: new EnterExit(Active: true));

    public override Element Render()
    {
        _ = Bump.Value;   // an ordinary same-props re-render driver (23r.e)
        var kids = new List<Element>(3);
        if (MountedA.Value)
            kids.Add(new BoxEl
            {
                Key = "grow", Direction = 1, Width = 120f, Animate = ReflowEnter,
                Children = Grown.Value
                    ? [new BoxEl { Width = 120f, Height = BaseH }, new BoxEl { Width = 120f, Height = ExtraH }]
                    : [new BoxEl { Width = 120f, Height = BaseH }],
            });
        if (MountedB.Value)
            kids.Add(new BoxEl
            {
                Key = "inert", Direction = 1, Width = 120f, Animate = ReflowEnter,
                Children = [new BoxEl { Width = 120f, Height = BaseH }],
            });
        kids.Add(new BoxEl { Key = "tail", Width = 120f, Height = 24f });
        return new BoxEl { Direction = 1, AlignItems = FlexAlign.Start, Children = kids.ToArray() };
    }
}

// 23r.f probe — the two RE-DECLARATION shapes, as two independent keyed entrants: `declared` mounts with an explicit
// height (so its row's RestoreTo is a NUMBER and the natural-extent retarget must keep its hands off) and is re-declared
// LARGER mid-flight; `drawer` mounts AUTO (RestoreTo NaN) and is told to CLOSE (declared 0) mid-open — the interrupt that
// used to restore the outgoing row's NaN and flash one full-natural-height frame.
sealed class ReflowDeclaredProbe : Component
{
    public const float NaturalH = 90f;            // 3 x 30 — the auto height both nodes solve at
    public const float DeclaredH1 = 120f, DeclaredH2 = 200f;
    public static readonly Signal<bool> MountedDeclared = new(false);
    public static readonly Signal<float> Declared = new(DeclaredH1);
    public static readonly Signal<bool> MountedDrawer = new(false);
    public static readonly Signal<float> DrawerH = new(float.NaN);

    static readonly LayoutTransition ReflowEnter = new(
        TransitionChannels.Size,
        TransitionDynamics.Tween(333f, Easing.Linear),
        Size: SizeMode.Reflow,
        Enter: new EnterExit(Active: true));

    public override Element Render()
    {
        var kids = new List<Element>(3);
        if (MountedDeclared.Value)
            kids.Add(new BoxEl
            {
                Key = "declared", Direction = 1, Width = 100f, Height = Declared.Value, Animate = ReflowEnter,
                Children = [new BoxEl { Width = 100f, Height = 30f }, new BoxEl { Width = 100f, Height = 30f },
                            new BoxEl { Width = 100f, Height = 30f }],
            });
        if (MountedDrawer.Value)
            kids.Add(new BoxEl
            {
                Key = "drawer", Direction = 1, Width = 100f, Height = DrawerH.Value, Animate = ReflowEnter,
                Children = [new BoxEl { Width = 100f, Height = 30f }, new BoxEl { Width = 100f, Height = 30f },
                            new BoxEl { Width = 100f, Height = 30f }],
            });
        kids.Add(new BoxEl { Key = "tail", Width = 100f, Height = 24f });
        return new BoxEl { Direction = 1, AlignItems = FlexAlign.Start, Children = kids.ToArray() };
    }
}

// 23r.g probe — a column of: the reflowing entrant, a spacer, and a BoundsAnimated sibling BELOW them. While the
// entrant's reveal runs, the sibling's parent-relative Y changes every frame — but the CAUSE is the reflow, so the
// sibling must ride the reflow's own re-solve and never seed a position FLIP. `Noise` makes each ride frame a genuine
// COMMIT frame (ApplyProjections runs with a live reflow); `Shifted` grows the spacer AFTER the reveal settles, which is
// the counter-case: a genuine LOCAL move with no reflow in flight, which must still FLIP (the 23x contract).
sealed class ReflowShoveProbe : Component
{
    public const float EntrantH = 90f;
    public static readonly Signal<bool> Mounted = new(false);
    public static readonly Signal<int> Noise = new(0);
    public static readonly Signal<bool> Shifted = new(false);

    static readonly LayoutTransition ReflowEnter = new(
        TransitionChannels.Size,
        TransitionDynamics.Tween(333f, Easing.Linear),
        Size: SizeMode.Reflow,
        Enter: new EnterExit(Active: true));
    static readonly LayoutTransition Slide = new(TransitionChannels.Position,
        TransitionDynamics.Tween(167f, Easing.FluentPopOpen));

    public override Element Render()
    {
        _ = Noise.Value;   // an unrelated state commit — every ride frame is a real reconcile
        var kids = new List<Element>(3);
        if (Mounted.Value)
            kids.Add(new BoxEl
            {
                Key = "entrant", Direction = 1, Width = 120f, Animate = ReflowEnter,
                Children = [new BoxEl { Width = 120f, Height = EntrantH }],
            });
        kids.Add(new BoxEl { Key = "spacer", Width = 120f, Height = Shifted.Value ? 40f : 0f });
        kids.Add(new BoxEl { Key = "sib", Width = 120f, Height = 30f, Animate = Slide });
        return new BoxEl { Direction = 1, AlignItems = FlexAlign.Start, Children = kids.ToArray() };
    }
}

// 23s.a probe — a BoundsAnimated mover under a spacer whose height the gate steps three times in quick succession.
// Each step is a pure POSITION delta on the mover (a tween reframe), which is the channel the retarget-in-place fix
// owns: the three deltas must fold into ONE flight that keeps the first delta's deadline.
sealed class ReframeRetargetProbe : Component
{
    public const float MoverH = 30f;
    public static readonly Signal<float> Spacer = new(0f);

    static readonly LayoutTransition Slide = new(TransitionChannels.Position,
        TransitionDynamics.Tween(250f, Easing.FluentPopOpen));

    public override Element Render() => new BoxEl
    {
        Direction = 1, AlignItems = FlexAlign.Start,
        Children =
        [
            new BoxEl { Key = "spacer", Width = 100f, Height = Spacer.Value },
            new BoxEl { Key = "mover", Width = 100f, Height = MoverH, Animate = Slide },
        ],
    };
}

// 23r.h probe — Wavee's playlist expander: a measured virtual list whose row 0 wraps the slot in a default-Direction
// (row) box, the ItemContainer shape that made PendingExitReflow pick WIDTH and no-op. The keyed drawer is a
// SizeMode.Reflow + ClipToBounds + Enter/Exit Active node (DrawerReveal). Open mounts it; close orphans it.
sealed class VirtualDrawerExitProbe : Component
{
    public const float RowH = 40f;
    public const float DrawerH = 80f;
    public static readonly Signal<bool> Open = new(false);

    static readonly LayoutTransition DrawerReveal = new(
        TransitionChannels.Size,
        TransitionDynamics.Tween(200f, Easing.Linear),
        Enter: new EnterExit(Active: true),
        Exit: new EnterExit(Active: true),
        ExitDynamics: TransitionDynamics.Tween(200f, Easing.Linear),
        Size: SizeMode.Reflow,
        Anchor: SizeAnchor.Leading,
        SuppressDescendantTransitions: true);

    public override Element Render()
    {
        _ = Open.Value;
        var layout = UseMemo(static () => new MeasuredStackVirtualLayout(RowH), DepKey.Empty);
        return Virtual.Measured(4, layout, i =>
        {
            var skin = new BoxEl { Key = "row", Width = 200f, Height = RowH };
            Element slot = i == 0 && Open.Value
                ? new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children =
                    [
                        skin,
                        new BoxEl
                        {
                            Key = "drawer", Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = DrawerReveal,
                            Children = [new BoxEl { Width = 200f, Height = DrawerH }],
                        },
                    ],
                }
                : new BoxEl { Direction = 1, MinWidth = 0f, Children = [skin] };
            // Default Direction=0 wrapper — the ItemContainer shape that made parent-exit-reflow pick the wrong axis.
            return new BoxEl { Width = 200f, Children = [slot] };
        }, keyOf: i => "r" + i) with { Width = 240f, Height = 360f };
    }
}

/// <summary>The FlipCountdown digit cell, reduced: a clipped fixed cell over ONE keyed child with a declarative
/// Enter/Exit slide. Bumping <see cref="Value"/> is a "tick" — the key changes, the old numeral exit-orphans and the new
/// one enters, which is the exact shape that pinned the real app's compositor clock at 120 Hz.</summary>
sealed class FlipCellProbe : Component
{
    public readonly Signal<int> Value = new(0);
    public override Element Render()
    {
        int v = Value.Value;
        float rise = 14f;
        return new BoxEl
        {
            Width = 40f, Height = 40f, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Key = "d" + v,
                    Width = 40f, Height = 40f,
                    Enter = new EnterExit(Dy: rise, Opacity: 0f, Active: true),
                    Exit = new EnterExit(Dy: -rise, Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlFast,
                },
            ],
        };
    }
}

/// <summary>The M4b root: the M3 ping-pong column, but HOVER-triggered through the external <c>scrollWhen</c> gate (the
/// shared group-hover shape the player bar uses), so the gate flips hover on and off deterministically without
/// synthesising pointer motion. Hover-leave must glide the content home, never park it mid-scroll.</summary>
sealed class MarqueeHoverHomeProbe : Component
{
    public readonly Signal<bool> Hovered = new(false);
    public override Element Render() => new BoxEl
    {
        Width = 150f, Height = 40f, Direction = 1, AlignItems = FlexAlign.Stretch,
        Children =
        [
            Marquee.Of("This is a very long track title that should overflow and scroll",
                new Marquee.Style { FontSize = 14f, StartDelayMs = 0f, Speed = 200f, Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.Hover },
                Hovered),
        ],
    };
}
