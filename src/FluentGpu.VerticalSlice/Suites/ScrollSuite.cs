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
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

sealed class TargetSampleSwapchain : ISwapchain
{
    private ulong _submitSequence, _sampleSequence, _sampleSubmitSequence;
    private GpuRenderSample _sample;
    public TargetSampleSwapchain(Size2 size) => SizePx = size;
    public Size2 SizePx { get; private set; }
    public void Resize(Size2 px) => SizePx = px;
    public void Present() { }
    public void Dispose() { }
    internal void NoteSubmit() => _submitSequence++;
    internal void PublishRetired(double ms, long qpc)
    {
        _sampleSequence++;
        _sampleSubmitSequence = _submitSequence;
        _sample = new GpuRenderSample(ms, _sampleSequence, 0, qpc);
    }
    public bool TryGetGpuRenderSample(out GpuRenderSample sample)
    {
        sample = _sample with { SubmitAge = _submitSequence - _sampleSubmitSequence };
        return sample.Sequence != 0;
    }
}

sealed class TargetSampleGpuDevice : IGpuDevice
{
    private long _qpc = 10_000;
    public double NextExecutionMs { get; set; } = 4.0;
    public bool RetireNextSubmit { get; set; } = true;
    public string BackendName => "target-sample-fake";
    public bool SupportsSecondarySwapchains => true;
    private TargetSampleSwapchain? _primary;
    public ISwapchain CreateSwapchain(in SwapchainDesc desc)
    {
        var sc = new TargetSampleSwapchain(desc.SizePx);
        _primary ??= sc;
        return sc;
    }
    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx) { }
    public bool SupportsComposite => true;
    public void SubmitComposite(in CompositeFrame frame, ISwapchain target)
    {
        for (int i = 0; i < frame.RasterDone.Length; i++) frame.RasterDone[i] = 1;
        if (_primary is not null) SubmitDrawList(default, default, in frame.Info, _primary);
    }
    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx, ISwapchain target)
    {
        if (target is not TargetSampleSwapchain sc) return;
        sc.NoteSubmit();
        if (RetireNextSubmit) sc.PublishRetired(NextExecutionMs, ++_qpc);
        RetireNextSubmit = true;
    }
    public void UploadImage(int imageId, ReadOnlySpan<byte> pbgra8, int w, int h) { }
    public void Dispose() { }
}


    sealed class ResizeBoxProbe : Component
    {
        public override Element Render() => Embed.Comp(() => new OverlayHost
        {
            Child = new BoxEl { Grow = 1f, Fill = ColorF.FromRgba(20, 20, 20) },
        });
    }

    sealed class UnboundedZStackChild : Component
    {
        public override Element Render() => new BoxEl
        {
            Height = 20f,
            JustifySelf = FlexAlign.End,
            MeasureUnboundedWidth = true,
            Children = [new BoxEl { Width = 200f, Height = 20f }],
        };
    }

    // gate.scroll.explicit-measured-correction-* probe: row 2 is corrected to a drawer-sized cached extent only after
    // it has scrolled out of the realized window. CorrectMeasuredExtent must then be able to collapse that UNREALIZED
    // band without remounting/re-rendering this component, while retaining row+within-row as the canonical anchor.
    sealed class ExplicitMeasuredCorrectionProbe : Component
    {
        public const int N = 100;
        public const int CorrectedIndex = 2;
        public const float RowH = 64f;
        public const float ExpandedH = 264f;
        public const float ExtentDelta = ExpandedH - RowH;

        public readonly ItemsViewController Controller = new();
        public readonly MeasuredStackVirtualLayout Layout = new(RowH);
        public int RenderCount;

        public override Element Render()
        {
            RenderCount++;
            return new BoxEl
            {
                Width = 300f,
                Height = 300f,
                Children =
                [
                    ItemsView.CreateBound(N,
                        static _ => new BoxEl { Height = RowH, Fill = ColorF.FromRgba(30, 30, 30) },
                        RepeatLayout.Measured(Layout),
                        new ListOptions
                        {
                            Controller = Controller,
                            SelectionMode = ItemsSelectionMode.None,
                            Grow = 1f,
                        }),
                ],
            };
        }
    }

    // gate.scroll.measured-tail-extent-*: a Grow=1 measured bound list the size of Wavee's two-column playlist
    // (N×56 in a 700 viewport, Overscan=8). The published ContentExtent must be exactly Σ row heights — overscan
    // past ItemCount must not add a phantom tail, and a SizeMode.Reflow drawer that opens then exits must retract
    // its ExtentTable entry once the orphan is gone (otherwise the last row can sit at the TOP of the viewport).
    sealed class MeasuredTailExtentProbe : Component
    {
        public const int N = 40;
        public const float RowH = 56f;
        public const float DrawerH = 160f;
        public const float ExpandedH = RowH + DrawerH;
        public const int ExpandIndex = 5;
        public const int Overscan = 8;
        public const float ViewportH = 700f;

        public readonly ItemsViewController Controller = new();
        public readonly MeasuredStackVirtualLayout Layout = new(RowH);
        public readonly Signal<int> Expanded = new(-1);

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
            var host = this;
            return new BoxEl
            {
                Width = 300f,
                Height = ViewportH,
                Children =
                [
                    ItemsView.CreateBound(N,
                        scope => Embed.Comp(() => new Slot(host, scope.Index)),
                        RepeatLayout.Measured(Layout),
                        new ListOptions
                        {
                            Controller = Controller,
                            SelectionMode = ItemsSelectionMode.None,
                            Grow = 1f,
                        }),
                ],
            };
        }

        sealed class Slot : Component
        {
            readonly MeasuredTailExtentProbe _host;
            readonly IReadSignal<int> _index;
            public Slot(MeasuredTailExtentProbe host, IReadSignal<int> index) { _host = host; _index = index; }

            public override Element Render()
            {
                bool open = _host.Expanded.Value == _index.Value;
                var skin = new BoxEl { Key = "row", Width = 280f, Height = RowH, Fill = ColorF.FromRgba(30, 30, 30) };
                if (!open)
                    return new BoxEl { Direction = 1, MinWidth = 0f, Children = [skin] };
                return new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children =
                    [
                        skin,
                        new BoxEl
                        {
                            Key = "drawer", Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = DrawerReveal,
                            Children = [new BoxEl { Width = 280f, Height = DrawerH }],
                        },
                    ],
                };
            }
        }
    }

static class ScrollSuite
{
    public static void Run(StringTable strings)
    {
        ScrollHoverChecks(strings);
        HoverSubtreeChecks(strings);
        MoveToPxChecks();
        CaptureCancelChecks(strings);
        ScrollChecks(strings);
        BringIntoViewChecks(strings);
        TwoAxisScrollChecks(strings);
        ScrollCrossAxisChecks(strings);
        ScrollOverlayChecks(strings);
        VirtualChecks(strings);
        SlotPoolChecks(strings);
        BoundItemsViewChecks(strings);
        ShelfBindingChecks.Run(strings);
        ExtentTableChecks();
        VariableChecks(strings);
        ScrollParityChecks(strings);
        ScrollV2ValidationChecks(strings);
        E11VirtChecks(strings);
        CompRootPinChecks(strings);
        StickyOnContentGridChecks(strings);
        PagerSnapChecks(strings);
        ListConsolidationChecks(strings);
        D1CollectionHostSizingChecks(strings);
        Cp2ConsolidationChecks(strings);
        D4ScrollBarChecks(strings);
        OcclusionCullChecks();
        ShadowOpacityGateChecks();
        ContainingScrollerChecks(strings);
        // NamedScrollTimelineChecks deleted — gate.scroll.named-timeline / gate.scroll.named-timeline-retire deleted:
        // named scroll-timelines are removed from the DSL entirely (scroll-v3 plan §7.3 authoring collapse); no successor gate.
        MeasuredTailExtentChecks(strings);
    }


    sealed class AsbLabelsHost : Component
    {
        public required AnnotatedScrollBarController Controller;
        public required Signal<IReadOnlyList<AnnotatedScrollBarLabel>> Labels;
        public required float Height;
        public override Element Render()
        {
            var labels = Labels.Value;
            return AnnotatedScrollBar.Create(Controller, new AnnotatedScrollBarOptions
            {
                Labels = labels,
                Height = Height,
            });
        }
    }


    // Overlay plate / zero-overflow: ancestor-only targeting misses a list that still geometrically contains the point.
    static void ContainingScrollerChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        static NodeHandle FindScrollable(SceneStore s, NodeHandle n)
        {
            if (n.IsNull) return NodeHandle.Null;
            if ((s.Flags(n) & NodeFlags.Scrollable) != 0 && s.HasScroll(n)) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                var r = FindScrollable(s, c);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }

        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("overlay-sibling-scroll", new Size2(300, 300), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new OverlaySiblingScrollProbe());
            host.RunFrame();
            for (int i = 0; i < 4 && host.HasActiveWork; i++) host.RunFrame();
            var vp = FindScrollable(host.Scene, host.Scene.Root);
            var hit = host.Input.DiagHitTest(new Point2(100f, 100f));
            bool hitIsNotScroller = !hit.IsNull && hit != vp;

            uint t = 5000;
            void Packet(FluentGpu.Scroll.Runtime.ScrollGesture k, float dy)
            {
                t += 16;
                window.QueueInput(ScrollPhaseEvent(k, new Point2(100f, 100f), 0, 0, ScrollDelta: dy,
                    Pointer: PointerKind.Touchpad, TimestampMs: t, PointerId: 9, DeviceClassRaw: DeviceClassIgnored));
                host.RunFrame();
            }
            Packet(FluentGpu.Scroll.Runtime.ScrollGesture.Begin, 0f);
            Packet(FluentGpu.Scroll.Runtime.ScrollGesture.Sample, 20f);
            Packet(FluentGpu.Scroll.Runtime.ScrollGesture.Sample, 20f);
            Packet(FluentGpu.Scroll.Runtime.ScrollGesture.Sample, 20f);
            bool panLatched = host.Input.GestureActive;
            Packet(FluentGpu.Scroll.Runtime.ScrollGesture.End, 0f);
            host.Scene.TryGetScroll(vp, out var scPan);
            float panOffset = scPan.OffsetY;

            // OffsetY is a RESULT column now (get; private set) — reset via a posted immediate ScrollTo instead of a raw write.
            host.TryGetScrollHandle(vp)?.ScrollTo(0f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            window.QueueInput(WheelEvent(new Point2(100f, 100f), 0, 0, 120f));
            for (int i = 0; i < 12; i++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var scWheel);

            Check("gate.scroll.overlay-sibling-containing a later-sibling non-scrollable plate covering a vertical list still pans and wheels the list (hit leaf is the plate; containing scroller is the viewport)",
                !vp.IsNull && hitIsNotScroller && panLatched && panOffset > 1f && scWheel.OffsetY > 1f,
                $"vp={(vp.IsNull ? "null" : "ok")} hitIsPlate={hitIsNotScroller} panLatch={panLatched} panOff={panOffset:0.##} wheelOff={scWheel.OffsetY:0.##}");
        }

        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("zero-overflow-latch", new Size2(300, 300), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ZeroOverflowScrollProbe());
            host.RunFrame();
            for (int i = 0; i < 4 && host.HasActiveWork; i++) host.RunFrame();
            var vp = FindScrollable(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc0);
            float over = sc0.ContentH - sc0.ViewportH;

            uint t = 8000;
            void Packet(FluentGpu.Scroll.Runtime.ScrollGesture k, float dy)
            {
                t += 16;
                window.QueueInput(ScrollPhaseEvent(k, new Point2(100f, 100f), 0, 0, ScrollDelta: dy,
                    Pointer: PointerKind.Touchpad, TimestampMs: t, PointerId: 11, DeviceClassRaw: DeviceClassIgnored));
                host.RunFrame();
            }
            Packet(FluentGpu.Scroll.Runtime.ScrollGesture.Begin, 0f);
            Packet(FluentGpu.Scroll.Runtime.ScrollGesture.Sample, 20f);
            Packet(FluentGpu.Scroll.Runtime.ScrollGesture.Sample, 20f);
            bool latched = host.Input.GestureActive;
            Packet(FluentGpu.Scroll.Runtime.ScrollGesture.End, 0f);

            Check("gate.scroll.zero-overflow-latch a same-axis Scrollable with content≈viewport still latches a vertical pan (loading/at-edge is not a dead gesture)",
                !vp.IsNull && over <= 0.5f && latched,
                $"vp={(vp.IsNull ? "null" : "ok")} over={over:0.##} latched={latched}");
        }
    }

    // Invisible-shadow cull (SceneRecorder: the shadow emit is gated on the cumulative opacity, exactly like the
    // edge-fade / self-blur / opacity-group decisions beside it). ShadowPipeline's PSMain multiplies its output alpha by
    // the per-instance opacity and blends ONE/INV_SRC_ALPHA premultiplied, so an alpha-0 shadow writes the destination
    // back unchanged — while spanning the LARGEST quad we emit (node ⊕ spread ⊕ 3σ). Wavee's shelf cards rest at
    // Opacity 0 with a blur-16 hover shadow, so this was the majority of a Home shelf's shadow pixels.
    static void ShadowOpacityGateChecks()
    {
        // One box with a hover-sized shadow; `opacity` is the only knob. `spans` exercises the reuse path — the opacity
        // is already an input to ComputeSpanInputSig, so crossing the threshold must invalidate and re-record.
        static (SceneStore Scene, NodeHandle Node) Build(float opacity)
        {
            var scene = new SceneStore();
            var node = scene.CreateNode(1); scene.Root = node;
            ref RectF b = ref scene.Bounds(node); b = new RectF(20f, 20f, 160f, 90f);
            ref NodePaint p = ref scene.Paint(node);
            p.VisualKind = VisualKind.Box;
            p.Fill = new ColorF(0.14f, 0.14f, 0.16f, 1f);
            p.Corners = CornerRadius4.All(8f);
            p.Opacity = opacity;
            scene.SetShadow(node, new ShadowSpec(Blur: 16f, OffsetY: 4f, OffsetX: 0f,
                Color: ColorF.FromRgba(0, 0, 0, 0x40)));
            return (scene, node);
        }

        static int ShadowCount(SceneStore scene, DrawList dl, SpanTable? spans)
        {
            SceneRecorder.Record(scene, dl, spans: spans);
            return dl.OpcodeStats.DrawShadow;
        }

        var dl0 = new DrawList();
        var (sceneOff, _) = Build(0f);
        int atZero = ShadowCount(sceneOff, dl0, null);

        var dl1 = new DrawList();
        var (sceneOn, _) = Build(1f);
        int atOne = ShadowCount(sceneOn, dl1, null);

        // Threshold crossing on ONE live scene through the span cache: 0 → 1 → 0 must track the emit exactly.
        var spans = new SpanTable();
        var dl2 = new DrawList();
        var (scene, node) = Build(0f);
        int span0 = ShadowCount(scene, dl2, spans);
        scene.Paint(node).Opacity = 1f;
        int span1 = ShadowCount(scene, dl2, spans);
        scene.Paint(node).Opacity = 0f;
        int span2 = ShadowCount(scene, dl2, spans);

        bool ok = atZero == 0 && atOne == 1 && span0 == 0 && span1 == 1 && span2 == 0;
        Check("gate.record.shadow-opacity-gate a node whose cumulative opacity is 0 emits NO DrawShadowCmd (the shader multiplies aOut by opacity ⇒ zero-output quad); at opacity 1 it does; crossing the threshold re-records through the span cache",
            ok, $"atZero={atZero} atOne={atOne} spanSeq=[{span0},{span1},{span2}]");
    }

    // Opaque occlusion cull (SceneRecorder.IsOccludedByOpaqueChild, always-on): a node's own fill is dropped when a
    // later-drawn direct child provably, fully, opaquely covers it. These gates replace the retired opt-in env-flag A/B —
    // they pin the space-consistent predicate (child cover rect transformed through the SAME parent world) against the
    // 2026-07-23 regression (a Scale(0.3,1) value-fill must NOT read as covering).
    static void OcclusionCullChecks()
    {
        // Distinct probe colors so FindFillCommandNear pins the exact node's own fill emit.
        var parentFill  = new ColorF(0.80f, 0.12f, 0.16f, 1f);   // opaque red  — the (maybe) covered parent
        var childOpaque = new ColorF(0.12f, 0.62f, 0.24f, 1f);   // opaque green — a covering child
        var childSemi   = new ColorF(0.12f, 0.62f, 0.24f, 0.5f); // same green @ 50% alpha — cannot overwrite

        // Build a parent Box (root) with one direct child; return whether the PARENT's own fill survives the record.
        // The child's alpha / corners / LocalTransform are the per-case knobs. Direct paint writes mirror what a
        // Transform bind's effect does at flush (Reconciler writes _scene.Paint(node).LocalTransform = tb()).
        static bool ParentFillPresent(ColorF parentFill, ColorF childFill, CornerRadius4 parentCorners,
                                      Affine2D childTransform)
        {
            var sq = new CornerRadius4(0f, 0f, 0f, 0f);
            var scene = new SceneStore();
            var parent = scene.CreateNode(1); scene.Root = parent;
            ref RectF pb = ref scene.Bounds(parent); pb = new RectF(0f, 0f, 100f, 40f);
            ref NodePaint pp = ref scene.Paint(parent);
            pp.VisualKind = VisualKind.Box; pp.Fill = parentFill; pp.Corners = parentCorners;

            var child = scene.CreateNode(1); scene.AppendChild(parent, child);
            ref RectF cb = ref scene.Bounds(child); cb = new RectF(0f, 0f, 100f, 40f);
            ref NodePaint cp = ref scene.Paint(child);
            cp.VisualKind = VisualKind.Box; cp.Fill = childFill; cp.Corners = sq;   // child must be square to cover
            cp.LocalTransform = childTransform;

            var dl = new DrawList();
            SceneRecorder.Record(scene, dl);
            return FindFillCommandNear(dl, parentFill).Order >= 0;
        }

        var square  = new CornerRadius4(0f, 0f, 0f, 0f);
        var rounded = CornerRadius4.All(8f);

        // 1. Opaque square child fully covering an opaque square parent → the parent's fill is dead work, dropped.
        //    The identical scene with a semi-transparent child cannot overwrite, so the parent's fill survives.
        bool culled   = !ParentFillPresent(parentFill, childOpaque, square, Affine2D.Identity);
        bool keptSemi =  ParentFillPresent(parentFill, childSemi,   square, Affine2D.Identity);
        Check("gate.record.occlusion-culls opaque square child fully covering the parent drops the parent's own fill (semi-transparent child keeps it)",
            culled && keptSemi, $"culled={culled} keptSemi={keptSemi}");

        // 2. The seek-bar shape: a full-width opaque square child scaled to 30% width (Scale(0.3,1) — the value-fill's
        //    bound transform) covers only part of the rounded rail; its non-identity linear part is rejected, so the
        //    rail keeps its grey track. The truly-covered case (identity) still culls — proving the transform is honored.
        bool railKept    =  ParentFillPresent(parentFill, childOpaque, rounded, Affine2D.Scale(0.3f, 1f));
        bool railCovered = !ParentFillPresent(parentFill, childOpaque, rounded, Affine2D.Identity);
        Check("gate.record.occlusion-respects-transform Scale(0.3,1) value-fill leaves the rail track drawn; an untransformed full cover still culls",
            railKept && railCovered, $"kept={railKept} covered={railCovered}");

        // 3. Dx/Dy-aware math: a child translated off the parent (Dx=60) no longer covers → parent fill drawn; the same
        //    child translated back to Dx=0 covers exactly → parent fill dropped. Pins the new translation modeling.
        bool shiftedKept   =  ParentFillPresent(parentFill, childOpaque, square, Affine2D.Translation(60f, 0f));
        bool alignedCulled = !ParentFillPresent(parentFill, childOpaque, square, Affine2D.Translation(0f, 0f));
        Check("gate.record.occlusion-respects-translation a translated (Dx=60) child leaves the parent drawn; Dx=0 covering drops it",
            shiftedKept && alignedCulled, $"shiftedKept={shiftedKept} alignedCulled={alignedCulled}");
    }

    static void HoverSubtreeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("hover-subtree", new Size2(320, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new HoverSubtreeProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();   // mount + layout

        // Geometry: root pad 50 → wrapper at (50,50) 100×100; wrapper pad 20 → child at (70,70) 60×60.
        var vp = host.Scene.AbsoluteRect(host.Scene.Root);
        var ptOut = new Point2(vp.X + 10f, vp.Y + 10f);       // outside the wrapper subtree (root has no handlers)
        var ptChild = new Point2(vp.X + 100f, vp.Y + 100f);   // inside the interactive child
        var ptChild2 = new Point2(vp.X + 110f, vp.Y + 105f);  // still inside the child
        var ptPad = new Point2(vp.X + 58f, vp.Y + 58f);       // the wrapper's own padding (the wrapper is the leaf here)

        void Move(Point2 p) { window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0)); host.RunFrame(); }

        Move(ptOut);
        probe.WrapperEnter = 0; probe.WrapperExit = 0;

        Move(ptChild);                                        // outside → child (the child is the deepest interactive leaf)
        bool enterOnChild = probe.WrapperEnter == 1 && probe.WrapperExit == 0;
        Move(ptChild2);                                       // move WITHIN the child — no re-enter, no exit
        bool noRefire = probe.WrapperEnter == 1 && probe.WrapperExit == 0;
        Move(ptOut);                                          // leave the subtree
        bool exitOnce = probe.WrapperExit == 1;

        probe.WrapperEnter = 0; probe.WrapperExit = 0;
        Move(ptPad);                                          // onto the wrapper's own padding (wrapper = hovered leaf)
        Move(ptChild);                                        // padding → child: still INSIDE the subtree → no exit
        bool noSelfExit = probe.WrapperExit == 0;
        Move(ptOut);
        bool exitAfter = probe.WrapperExit == 1;

        Check("gate.input.hover-subtree-enter-exit OnHoverMove/OnPointerExit are subtree-scoped for a wrapper around an interactive child (enter on child-hover, no re-fire within, no exit wrapper→child, one exit on leave)",
            enterOnChild && noRefire && exitOnce && noSelfExit && exitAfter,
            $"enterOnChild={enterOnChild} noRefire={noRefire} exitOnce={exitOnce} noSelfExit={noSelfExit} exitAfter={exitAfter} enter={probe.WrapperEnter} exit={probe.WrapperExit}");
    }

    // gate.pal.headless.move-to — IPlatformWindow.MoveToPx (2026-09-22): the pure-move sibling of SetBoundsPx.
    // HeadlessWindow just RECORDS the call (LastMoveToPx/MoveToCount) rather than acting on it — this gate pins that
    // recording contract (multiple calls accumulate the count, the LAST origin wins) and, the part that actually
    // matters for a caller restoring a remembered window position, that it is a PURE move: ClientSizePx is untouched
    // across the call, unlike SetBoundsPx which can also resize.
    static void MoveToPxChecks()
    {
        var window = new HeadlessWindow(new WindowDesc("move-to-px", new Size2(400, 300), 1f));
        var sizeBefore = window.ClientSizePx;

        window.MoveToPx(new Point2(120f, 45f));
        bool firstCall = window.MoveToCount == 1 && window.LastMoveToPx.X == 120f && window.LastMoveToPx.Y == 45f;
        bool sizeUntouched1 = window.ClientSizePx == sizeBefore;

        window.MoveToPx(new Point2(-30f, 700f));   // negative/large origins are legal (multi-monitor virtual-screen coords)
        bool secondCall = window.MoveToCount == 2 && window.LastMoveToPx.X == -30f && window.LastMoveToPx.Y == 700f;
        bool sizeUntouched2 = window.ClientSizePx == sizeBefore;

        Check("gate.pal.headless.move-to MoveToPx records the last origin + a running call count, and never touches ClientSizePx (a pure move, unlike SetBoundsPx)",
            firstCall && sizeUntouched1 && secondCall && sizeUntouched2,
            $"count={window.MoveToCount} last=({window.LastMoveToPx.X},{window.LastMoveToPx.Y}) size {sizeBefore}→{window.ClientSizePx}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // gate.input.capture.* — input-capture hardening (2026-09-22, Win32Platform.cs). The Win32 PAL side of this fix
    // is a real SetCapture on the primary mouse press plus a single CancelPrimaryContact funnel every OS loss path
    // (WM_POINTERLEAVE-while-held, WM_POINTERCAPTURECHANGED, WM_CAPTURECHANGED, WM_CANCELMODE, a move packet whose
    // button bit already cleared) routes through — closing the "pop-out fling" defect where a WM_POINTERLEAVE park
    // move to (-10000,-10000) reached a still-latched drag node as a real sample. That Win32 message plumbing is
    // untestable headlessly; what IS portable — and what these gates pin — is the DISPATCHER-visible CONTRACT the
    // fix depends on: a PointerCancel for the held contact, delivered BEFORE any further move for it, tears the drag
    // down cleanly (no click, no delta beyond the cancel, capture stays released), and a plain click (no drag) never
    // leaves a phantom latch behind either. HeadlessWindow.QueuePointerLeaveWhileDown mirrors the exact Win32
    // emission order (cancel, THEN the off-screen park move) so the ORDER itself — not just the cancel's existence —
    // is under gate.
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    static void CaptureCancelChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // A single CanDrag box: OnClick / OnDragStarted / OnDragDelta / OnDragCompleted / OnDragCanceled counters.
        // OnDragDelta's Absolute.X is watched for the (-10000,-10000) park literal — if a captured drag ever saw it
        // as a real sample, |Absolute.X| would spike past 5000 (the scene is a few hundred DIP wide).
        Component MakeProbe(Action onClick, Action<FluentGpu.Foundation.DragEventArgs> onStarted,
            Action<FluentGpu.Foundation.DragEventArgs> onDelta, Action onCanceled)
            => new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 200, Height = 60, CanDrag = true,
                    OnClick = onClick,
                    OnDragStarted = onStarted,
                    OnDragDelta = onDelta,
                    OnDragCompleted = _ => { },
                    OnDragCanceled = onCanceled,
                },
            };

        // gate.input.capture.leave-while-down-cancels: press, move past the drag box (drag armed), then the exact
        // Win32 WM_POINTERLEAVE-while-held emission order (cancel, then the off-screen park move) in ONE frame. The
        // cancel must land first — OnDragCanceled fires, no click, and the park move that follows in the SAME pump
        // never reaches OnDragDelta as a real sample (the node's capture is already torn down by the time it drains).
        // A subsequent bare hover move changes nothing further (the gesture stays dead, not re-armed by a stray move).
        {
            int clicks = 0, started = 0, deltas = 0, canceled = 0; bool sawExtreme = false;
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("capture-leave", new Size2(320, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
                MakeProbe(() => clicks++, _ => started++,
                    a => { deltas++; if (MathF.Abs(a.Absolute.X) > 5000f) sawExtreme = true; }, () => canceled++));
            host.RunFrame();   // mount
            var center = CenterOf(host.Scene, host.Scene.Root);

            window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(center.X + 50f, center.Y), 0, 0));
            host.RunFrame();
            bool dragArmed = started == 1;

            window.QueuePointerLeaveWhileDown(0, 0);   // enqueues PointerCancel THEN the offscreen PointerMove, in order
            host.RunFrame();
            bool canceledOnce = canceled == 1;
            bool noClick = clicks == 0;
            int deltasAfterLeave = deltas;   // snapshot for the "hover changes nothing further" check below

            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(center.X + 20f, center.Y), 0, 0));
            host.RunFrame();
            bool inertAfter = deltas == deltasAfterLeave;

            Check("gate.input.capture.leave-while-down-cancels a WM_POINTERLEAVE-while-held (cancel, then the offscreen park move, same frame) cancels the drag (no click, delta never saw the park literal); a following hover move changes nothing further",
                dragArmed && canceledOnce && noClick && !sawExtreme && inertAfter,
                $"dragArmed={dragArmed} canceled={canceled} clicks={clicks} sawExtreme={sawExtreme} deltas={deltas}→{deltasAfterLeave} inertAfter={inertAfter}");
        }

        // gate.input.capture.cancel-then-hover-is-inert: a bare PointerCancel (the dispatcher-visible shape every
        // Win32 loss path funnels through — WM_POINTERCAPTURECHANGED/WM_CAPTURECHANGED/WM_CANCELMODE all resolve to
        // exactly this event) kills an in-flight drag; three subsequent hover moves drive nothing (no resurrection,
        // no stray delta); a fresh press+move re-arms the gesture cleanly (the cancel left no residue behind).
        {
            int clicks = 0, started = 0, deltas = 0, canceled = 0;
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("capture-bare-cancel", new Size2(320, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
                MakeProbe(() => clicks++, _ => started++, _ => deltas++, () => canceled++));
            host.RunFrame();
            var center = CenterOf(host.Scene, host.Scene.Root);

            window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(center.X + 50f, center.Y), 0, 0));
            host.RunFrame();
            bool dragArmed = started == 1;

            window.QueueInput(new InputEvent(InputKind.PointerCancel, default, 0, 0));
            host.RunFrame();
            bool canceledOnce = canceled == 1;
            int deltasAtCancel = deltas, startedAtCancel = started;

            for (int i = 1; i <= 3; i++)
            {
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(center.X + 20f + i, center.Y), 0, 0));
                host.RunFrame();
            }
            bool inert = deltas == deltasAtCancel && started == startedAtCancel && clicks == 0;

            window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(center.X + 50f, center.Y), 0, 0));
            host.RunFrame();
            bool reArmed = started == startedAtCancel + 1;

            Check("gate.input.capture.cancel-then-hover-is-inert a bare PointerCancel kills an in-flight drag (no click); hover moves afterward drive nothing; a fresh press+move re-arms the gesture cleanly",
                dragArmed && canceledOnce && inert && reArmed,
                $"dragArmed={dragArmed} canceled={canceled} inert={inert} reArmed={reArmed} started={started}");
        }

        // gate.input.capture.up-clears-latch: a press released BEFORE crossing the drag box is a plain click (the
        // e5dragdrop.1 shape) — no OnDragStarted, no capture ever taken. Moves AFTER the up must drive NOTHING (no
        // phantom OnDrag latch left behind by the press/up pair) — the up cleanly cleared whatever the press armed.
        {
            int clicks = 0, started = 0, deltas = 0;
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("capture-up-clears", new Size2(320, 200), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
                MakeProbe(() => clicks++, _ => started++, _ => deltas++, () => { }));
            host.RunFrame();
            var center = CenterOf(host.Scene, host.Scene.Root);

            window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
            host.RunFrame();
            bool clicked = clicks == 1 && started == 0;

            for (int i = 1; i <= 3; i++)
            {
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(center.X + 20f + i, center.Y), 0, 0));
                host.RunFrame();
            }
            bool noLatch = deltas == 0 && started == 0;

            Check("gate.input.capture.up-clears-latch a press released inside the drag box is a plain click; moves after the up drive no phantom OnDrag latch",
                clicked && noLatch,
                $"clicks={clicks} started={started} deltas={deltas}");
        }
    }

    static void ScrollHoverChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-hover", new Size2(320, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollHoverProbe());
        host.RunFrame();   // mount + layout (SmoothScroll defaults false ⇒ a wheel writes the offset synchronously)

        var vp = host.Scene.AbsoluteRect(host.Scene.Root);
        var pt = new Point2(vp.X + 60f, vp.Y + 20f);   // fixed point over ROW 0 (top 40px), inside the 180px-wide row

        // Warm the hover + scroll + re-eval path (JIT + seed both rows' hover anim-slab channels) OUTSIDE the measured
        // frame, so the zero-alloc assertion measures steady state (mirrors gate.scroll.alloc-zero's warm pass).
        window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0)); host.RunFrame();
        for (int w = 0; w < 2; w++)
        {
            window.QueueInput(WheelEvent(pt, 0, 0, 40f)); host.RunFrame();    // row 0 → row 1
            window.QueueInput(WheelEvent(pt, 0, 0, -40f)); host.RunFrame();   // row 1 → row 0
        }
        for (int i = 0; i < 8; i++) host.RunFrame();   // settle bars/anim back to rest

        // Establish hover on ROW 0 at the fixed point.
        window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0)); host.RunFrame();
        var a = host.Input.HitTest(pt);
        bool hovA = !a.IsNull && (host.Scene.Flags(a) & NodeFlags.Hovered) != 0;

        // MEASURED: wheel one row DOWN with the pointer NOT moving. Offset 0→40 ⇒ row 1 slides under the fixed point.
        var f = WheelDip(host, window, pt, 40f);
        var b = host.Input.HitTest(pt);
        bool contentMoved = !b.IsNull && b != a;                                        // a DIFFERENT node is under the point
        bool hovB = !b.IsNull && (host.Scene.Flags(b) & NodeFlags.Hovered) != 0;         // the NEW node is Hovered
        bool oldCleared = a.IsNull || (host.Scene.Flags(a) & NodeFlags.Hovered) == 0;    // the OLD node is NOT Hovered
        bool zero = f.HotPhaseAllocBytes == 0;                                           // the synthesized re-eval is 0-alloc

        Check("gate.scroll.hover-follows-content a wheel-scroll under a stationary cursor moves NodeFlags.Hovered to the row that slid under the point (old row cleared), with no PointerMove and 0 managed alloc on the re-eval frame",
            hovA && contentMoved && hovB && oldCleared && zero,
            $"a={(a.IsNull ? "null" : a.Raw.Index.ToString())} b={(b.IsNull ? "null" : b.Raw.Index.ToString())} hovA={hovA} moved={contentMoved} hovB={hovB} oldCleared={oldCleared} alloc={f.HotPhaseAllocBytes}B");

        ScrollHoverVirtualCheck(strings);
    }

    static void ScrollChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollProbe());

        host.RunFrame();   // mount + layout → ContentSize published
        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc0);
        bool sized = Near(sc0.ContentH, 800) && Near(sc0.ViewportH, 200);

        // a 200px viewport over 40px rows shows ~5 of 20 — the rest are clip-culled; clip is balanced.
        int drawnAtTop = device.LastRects.Count;
        bool clipped = device.LastClips.Count >= 1 && device.ClipBalance == 0 && drawnAtTop is >= 5 and < 20;

        // wheel 100 DIP down → offset 100, content transform −100 (the glide lands; scrolling is layout-free)
        var center = new Point2(100, 100);
        var f = WheelDip(host, window, center, 100f);
        host.Scene.TryGetScroll(vp, out var sc1);
        bool scrolled = Near(sc1.OffsetY, 100)
            && Near(host.Scene.Paint(sc1.ContentNode).LocalTransform.Dy, -100);

        // fling past the end → clamp to ContentH − ViewportH = 600
        WheelDip(host, window, center, 10000f);
        host.Scene.TryGetScroll(vp, out var sc2);
        bool clamped = Near(sc2.OffsetY, 600);

        Check("36. ScrollView publishes ContentSize + clips overflow", sized && clipped, $"content={sc0.ContentH:0} drawn={drawnAtTop} clips={device.LastClips.Count}");
        Check("37. wheel scrolls via transform (layout-free) + clamps", scrolled && clamped, $"off→{sc1.OffsetY:0}, clamp={sc2.OffsetY:0}");
    }

    /// <summary>gate.scroll.bring-into-view — the ONE programmatic bring-into-view seam (ScrollHandle.BringIntoView),
    /// which every caller used to hand-roll with per-site divergence. Covers all three legs: minimal scroll is a no-op for
    /// an already-visible node, the immediate path shows the offset AND the content transform in the same frame, and the
    /// animated path authors a Glide (the offset moves as the plan is evaluated on later frames).</summary>
    static void BringIntoViewChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("bring-into-view", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollProbe());
        host.RunFrame();   // mount + layout → ContentSize published

        var scene = host.Scene;
        var vp = scene.Root;
        scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        var rowNear = Child(scene, content, 1);    // y 40..80  — inside the 200-tall viewport
        var rowFar = Child(scene, content, 14);    // y 560..600 — well past it

        var handle = host.TryGetScrollHandle(vp)!;
        // The node-level seam (SceneScrollExtensions.BringIntoView → the viewport's ScrollHandle.BringIntoView) resolves
        // the row to content coordinates itself. Returns whether a move was authored (a visible row is a no-op).
        bool Bring(NodeHandle row, float margin = 0f, float alignmentRatio = float.NaN, bool animate = false)
        {
            double destBefore = handle.Plan.Dest;
            bool resolved = FluentGpu.Scroll.Runtime.SceneScrollExtensions.BringIntoView(scene, row, alignmentRatio,
                animate ? FluentGpu.Scroll.Runtime.ScrollMove.Glide : FluentGpu.Scroll.Runtime.ScrollMove.Immediate, margin);
            return resolved && handle.Plan.Dest != destBefore;
        }

        // Already visible ⇒ minimal scroll declines to move (and reports that it did nothing) — no plan is authored, so
        // no frame is needed to observe it.
        bool visibleNoop = !Bring(rowNear) && Near(ReadOffset(scene, vp), 0f);

        // Snap: the row's BOTTOM lands on the viewport's bottom edge — 600 − 200 = 400. The move authors an Immediate plan;
        // the next frame step shows it (offset + content transform).
        bool posted1 = Bring(rowFar);
        host.RunFrame();
        bool snapped = posted1 && Near(ReadOffset(scene, vp), 400f)
                && Near(scene.Paint(content).LocalTransform.Dy, -400f);

        // Aligned: ratio 0 parks the row's TOP at the leading gutter — 560 − 8.
        bool posted2 = Bring(rowFar, margin: 8f, alignmentRatio: 0f);
        host.RunFrame();
        bool aligned = posted2 && Near(ReadOffset(scene, vp), 552f);

        // Animated: `animate:true` authors a Glide plan (velocity-continuous, critically damped) rather than a jump:
        // the offset is untouched before any frame runs, and the viewport reads back a Programmatic motion once one does.
        bool posted3 = Bring(rowNear, alignmentRatio: 0f, animate: true);
        scene.TryGetScroll(vp, out var scBeforeGlide);
        bool untouchedBeforeFrame = Near(scBeforeGlide.OffsetY, 552f);
        host.RunFrame();
        scene.TryGetScroll(vp, out var scA);
        bool armedOk = posted3 && untouchedBeforeFrame && scA.Motion.Kind == FluentGpu.Scroll.Motion.MotionKind.Programmatic;

        Check("gate.scroll.bring-into-view: minimal scroll no-ops when visible; snap writes offset + content transform after a frame; aligned honours the ratio; animate glides (a Glide plan) instead of snapping",
            visibleNoop && snapped && aligned && armedOk,
            $"noop={visibleNoop} snap={snapped} aligned={aligned} armed={armedOk} offAfterArm={scA.OffsetY:0}");
    }

    static float ReadOffset(SceneStore scene, NodeHandle vp)
    {
        scene.TryGetScroll(vp, out var sc);
        return sc.OffsetY;
    }

    /// <summary>WP-G1 — the standardized pager/scroll-snap surface: the DECLARATIVE snap prop (declaration-gated, so a
    /// declaring element owns the columns while a non-declaring one keeps an imperative post-mount write), the PagedShelf
    /// page-snap opt-in (a wheel settle at a fractional offset re-snaps to the boundary; default None never does), and the
    /// dt-determinism of the distance-derived programmatic glide.</summary>
    static void PagerSnapChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // (a) gate.snap.declarative — the declared interval lands in ScrollState AND survives a reconcile that patches a
        // DIFFERENT scroll field; the sibling that declares nothing keeps a post-mount imperative write across the same
        // reconcile (the contract every shipped SnapInterval writer depends on).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("snap-declarative", new Size2(480, 260), 1f)); window.Show();
            var probe = new SnapDeclProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();

            var scene = host.Scene;
            NodeHandle decl = NodeHandle.Null, plain = NodeHandle.Null;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (scene.HasScroll(n)) { if (decl.IsNull) decl = n; else if (plain.IsNull) plain = n; }
                for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) Visit(c);
            }
            Visit(scene.Root);

            bool found = !decl.IsNull && !plain.IsNull;
            scene.TryGetScroll(decl, out var d0);
            bool declared = found && Near(d0.SnapInterval, SnapDeclProbe.DeclaredInterval) && d0.HasSnap;
            // The non-declaring sibling: nothing was written, so the patch left it snapless. Now write it imperatively.
            scene.TryGetScroll(plain, out var p0);
            bool plainClean = found && p0.SnapInterval == 0f && !p0.HasSnap;
            if (found) scene.ScrollRef(plain).SnapInterval = SnapDeclProbe.WrittenInterval;

            // Reconcile with a different scroll field changed (AlwaysShowScrollbar) — the patch must touch neither.
            probe.Toggle.Value = true;
            host.RunFrame();
            for (int i = 0; i < 3 && host.HasActiveWork; i++) host.RunFrame();
            scene.TryGetScroll(decl, out var d1);
            scene.TryGetScroll(plain, out var p1);
            bool declSurvived = found && Near(d1.SnapInterval, SnapDeclProbe.DeclaredInterval) && d1.AlwaysShowBar;
            bool writeSurvived = found && Near(p1.SnapInterval, SnapDeclProbe.WrittenInterval) && p1.AlwaysShowBar;
            Check("gate.snap.declarative ScrollEl.Snap lands the declared interval in ScrollState and is re-asserted across a reconcile that patches another scroll field; a viewport that declares NO Snap is left snapless by the patch and keeps a post-mount imperative SnapInterval write across that same reconcile",
                declared && plainClean && declSurvived && writeSurvived,
                $"declared={d0.SnapInterval:0.##}→{d1.SnapInterval:0.##} plain={p0.SnapInterval:0.##}→{p1.SnapInterval:0.##} bar={d1.AlwaysShowBar}/{p1.AlwaysShowBar} found={found}");
        }

        // (b) gate.snap.shelf-page — ShelfSnap.Page: the viewport's snap interval becomes the LIVE page stride, and a WHEEL
        // settle at a fractional offset (the engine hard-clamps wheels and never snaps them) re-snaps to the nearest page
        // boundary afterwards. The default (ShelfSnap.None) shelf, same geometry, rests exactly where the wheel left it.
        {
            float snapped = WheelShelfSettle(strings, fonts, snap: true, out float snapInterval, out float pages);
            float free = WheelShelfSettle(strings, fonts, snap: false, out float freeInterval, out _);
            bool intervalIsStride = Near(snapInterval, PagedShelfSnapProbe.PageW, 0.5f);
            bool reSnapped = Near(snapped, PagedShelfSnapProbe.PageW, 0.75f);            // 400 → the page-1 boundary 330
            bool freeUntouched = freeInterval == 0f && free > PagedShelfSnapProbe.PageW + 20f;   // still mid-page (~400)
            Check("gate.snap.shelf-page ShelfSnap.Page writes the LIVE page stride as the viewport's SnapInterval and re-snaps a settled WHEEL offset to the nearest page boundary (the engine snaps flings only); ShelfSnap.None keeps no interval and rests mid-page",
                intervalIsStride && reSnapped && freeUntouched,
                $"interval={snapInterval:0.##}/stride={PagedShelfSnapProbe.PageW:0.##} snappedOff={snapped:0.##} freeOff={free:0.##} freeInterval={freeInterval:0.##} pages={pages:0}");
        }

        // (c) gate.snap.page-glide-dt-invariant — the programmatic page glide is a closed form of ABSOLUTE time: replayed at
        // frame steps dt ∈ {8.33,16.67,33.3} ms the SAME glide is authored (same travel/velocity/rate), it is at the same
        // position 200 ms after it was authored, every frame SHOWS exactly the plan at that frame's present time (nothing
        // is integrated per frame, so there is no dt to accumulate error in), and it lands exactly on the page boundary.
        {
            var g833 = PageGlideTrace(strings, 8.33f);
            var g1667 = PageGlideTrace(strings, 16.67f);
            var g333 = PageGlideTrace(strings, 33.3f);
            bool sameGlide = Math.Abs(g833.Travel - g1667.Travel) < 1e-9 && Math.Abs(g1667.Travel - g333.Travel) < 1e-9
                          && Math.Abs(g833.V0 - g1667.V0) < 1e-9 && Math.Abs(g1667.V0 - g333.V0) < 1e-9
                          && g833.K == g1667.K && g1667.K == g333.K;
            bool midMatch = Math.Abs(g833.Mid - g1667.Mid) < 1e-6 && Math.Abs(g1667.Mid - g333.Mid) < 1e-6;
            bool inFlight = g1667.Mid > 1.0 && g1667.Mid < PagedShelfSnapProbe.PageW - 1.0;   // a real glide at +200 ms
            bool posedFromPlan = Math.Max(g833.WorstPoseErr, Math.Max(g1667.WorstPoseErr, g333.WorstPoseErr)) < 1e-6;
            bool landed = Near(g833.Final, PagedShelfSnapProbe.PageW, 0.5f)
                       && Near(g1667.Final, PagedShelfSnapProbe.PageW, 0.5f)
                       && Near(g333.Final, PagedShelfSnapProbe.PageW, 0.5f);
            Check("gate.snap.page-glide-dt-invariant the programmatic page glide is a closed form of absolute time: at frame steps dt ∈ {8.33,16.67,33.3}ms the same glide is authored, it is at the same offset 200ms after authoring, every frame shows exactly the plan at its present time, and it lands exactly on the page boundary",
                sameGlide && midMatch && inFlight && posedFromPlan && landed,
                $"travel=({g833.Travel:0.##},{g1667.Travel:0.##},{g333.Travel:0.##}) mid=({g833.Mid:0.##},{g1667.Mid:0.##},{g333.Mid:0.##}) inFlight={inFlight} poseErr={Math.Max(g833.WorstPoseErr, Math.Max(g1667.WorstPoseErr, g333.WorstPoseErr)):0.######} final=({g833.Final:0.##},{g1667.Final:0.##},{g333.Final:0.##}) target={PagedShelfSnapProbe.PageW:0.##}");
        }

        // (d) gate.snap.shelf-subhalf — the SUB-HALF-STRIDE wheel: 120 DIP over a 330 stride. The rounded page is still 0,
        // so a projection keyed on the page ALONE produces no key change, the change-only observer never fires, and the
        // strip rests 120 off-grid — the exact hole ShelfSnap.Page exists to close. The coarse OFFSET term in the settled
        // observer's projection is what makes this settle back on the boundary.
        {
            float off = WheelShelfSettle(strings, fonts, snap: true, out float interval, out _, wheelDip: 120f);
            Check("gate.snap.shelf-subhalf a wheel SHORTER than half a page stride (120 of 330 — the rounded page never changes) still settles on a page boundary: the settled-offset projection carries a coarse offset term, so a settle that leaves the page unchanged still fires the change-only re-snap",
                Near(off, 0f, 0.75f) && Near(interval, PagedShelfSnapProbe.PageW, 0.5f),
                $"off={off:0.##} (expect 0) interval={interval:0.##}/{PagedShelfSnapProbe.PageW:0.##}");
        }

        // (e) gate.snap.shelf-chart-rows — the rows:5 VIRTUALIZED chart. Page snapping must work through the ItemsView
        // controller seam (not just the measured ScrollEl path), the snap grid must be BOUNDED at the last whole boundary,
        // and — the multi-row half — NOTHING in the shelf subtree may arm hover-elevate: a dense chart has no lift and no
        // halo, so a park/hoist there only lets the hovered row escape the strip's clip over the column beside it.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("shelf-chart-rows", new Size2(640, 360), 1f)); window.Show();
            var probe = new PagedShelfChartProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            for (int i = 0; i < 12; i++) host.RunFrame();   // measure → fit → realize the multi-row strip

            var vp = FindScrollable(host.Scene, host.Scene.Root);
            float pageW = PagedShelfChartProbe.PageW(PagedShelfChartProbe.WideW);
            host.Scene.TryGetScroll(vp, out var s0);
            bool intervalIsStride = Near(s0.SnapInterval, pageW, 0.5f);
            // Bounded end grid: SnapEnd is the last WHOLE boundary ≤ maxX, so a fling into a partial last page can never
            // retarget past the content clamp. Open (End ≤ Start) would leave that hole.
            float maxX = MathF.Max(0f, s0.ContentW - s0.ViewportW);
            bool endBounded = s0.SnapEnd > s0.SnapStart && OnGrid(s0.SnapEnd, pageW, 0.5f) && s0.SnapEnd <= maxX + 0.5f;
            int elevate = CountHoverElevate(host.Scene, host.Scene.Root);

            bool previousReduced = Motion.ReducedMotion;
            try
            {
                Motion.ReducedMotion = true;
                // 400 over a 330 stride: the rounded page is 1, so a NON-ZERO boundary is the expected rest (0 would pass
                // an on-grid test trivially, which is why `settled > 1f` rides along in the assertion).
                window.QueueInput(WheelEvent(new Point2(150f, 60f), 0, 0, 400f));
                SettleScroll(host, vp);
            }
            finally { Motion.ReducedMotion = previousReduced; }
            host.Scene.TryGetScroll(vp, out var rested);
            float settled = rested.OffsetX;

            Check("gate.snap.shelf-chart-rows a rows:5 VIRTUALIZED page shelf writes the live page stride as its SnapInterval through the ItemsView controller seam, bounds the grid at the last WHOLE boundary, re-snaps a settled wheel onto the grid, and arms NO hover-elevate park/hoist anywhere in its subtree (a multi-row chart has no lift and no halo to make room for)",
                intervalIsStride && endBounded && elevate == 0 && settled > 1f && OnGrid(settled, pageW, 0.75f),
                $"interval={s0.SnapInterval:0.##}/{pageW:0.##} snapEnd={s0.SnapEnd:0.##} maxX={maxX:0.##} elevateNodes={elevate} settled={settled:0.##}");
        }

        // (f) gate.snap.shelf-refit-reseat — a fit change UNDER a strip resting mid-page. Narrowing the shelf breaks a
        // column (3 → 2), which moves every page boundary: the offset that WAS page 1's boundary is now mid-page, and
        // without a re-seat the strip rests off the new grid until the user scrolls again. The re-seat is a CORRECTION,
        // not a navigation, so it must not animate — but the assertion here is the resting position, which is what the
        // user sees: on the NEW grid, inside the NEW clamp.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("shelf-refit-reseat", new Size2(640, 360), 1f)); window.Show();
            var probe = new PagedShelfChartProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            for (int i = 0; i < 12; i++) host.RunFrame();

            var vp = FindScrollable(host.Scene, host.Scene.Root);
            float wideStride = PagedShelfChartProbe.PageW(PagedShelfChartProbe.WideW);
            float narrowStride = PagedShelfChartProbe.PageW(PagedShelfChartProbe.NarrowW);

            bool previousReduced = Motion.ReducedMotion;
            float before = 0f;
            try
            {
                Motion.ReducedMotion = true;
                probe.Pager.Next();                      // page 0 → 1: rest ON the wide grid first
                SettleScroll(host, vp);
                host.Scene.TryGetScroll(vp, out var mid);
                before = mid.OffsetX;

                probe.Width.Value = PagedShelfChartProbe.NarrowW;   // 3 columns → 2: every boundary moves
                SettleScroll(host, vp);
            }
            finally { Motion.ReducedMotion = previousReduced; }

            host.Scene.TryGetScroll(vp, out var reseated);
            float after = reseated.OffsetX;
            float newMaxX = MathF.Max(0f, reseated.ContentW - reseated.ViewportW);
            bool startedOnWideGrid = OnGrid(before, wideStride, 1f) && before > 1f;
            bool intervalRefit = Near(reseated.SnapInterval, narrowStride, 0.5f);
            bool onNewGrid = OnGrid(after, narrowStride, 1f) && after >= -0.5f && after <= newMaxX + 0.5f;
            Check("gate.snap.shelf-refit-reseat a resize that breaks a column under a strip RESTING mid-page re-seats it onto the NEW page grid (the fit is in the bring-into-view dep) and re-fits the snap interval to the new stride — the strip never rests between boundaries after a re-fit",
                startedOnWideGrid && intervalRefit && onNewGrid,
                $"before={before:0.##} (wideStride={wideStride:0.##}) after={after:0.##} (narrowStride={narrowStride:0.##}) interval={reseated.SnapInterval:0.##} maxX={newMaxX:0.##}");
        }

        // (g) gate.snap.shelf-live-phase — WP-ε1, the ROOT of the touchpad snap-back defect. The settled-offset observer's
        // gate must be the scroll MOTION KIND (a live contact is Drag for its whole life), not "did the offset move this
        // frame": that goes false in ANY micro-pause of a live two-finger pan. Re-snapping there would author a Glide INTO the
        // live contact and yank the strip back. Reproduced structurally: hold a real touchpad contact open at a FRACTIONAL
        // offset (a paused pan) and let the observer re-project.
        // Reduced motion pins the pre-fix failure to an immediate direct write, so the assertion is exact, not time-integrated.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("shelf-live-phase", new Size2(640, 240), 1f)); window.Show();
            var probe = new PagedShelfSnapProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            for (int i = 0; i < 8; i++) host.RunFrame();      // measure → fit → realize the strip (same warm-up as (b))

            var vp = FindScrollable(host.Scene, host.Scene.Root);
            const float MidPan = 400f;   // mid-page over the 330 stride — a re-snap would target the boundary at 330
            bool previousReduced = Motion.ReducedMotion;
            try
            {
                Motion.ReducedMotion = true;
                // Offset and motion are RESULT columns (the host's frame step writes them from the plan), so they
                // cannot be hand-forced. A REAL, genuinely-held touchpad contact instead: drag to MidPan, then
                // hold the contact OPEN (no ContactEnd, no further deltas) for well past the debounce window — Activity
                // stays Drag for as long as the contact is live, exactly reproducing the "paused two-finger pan" the
                // pre-fix defect mis-classified, without needing to fake any column directly.
                // y=100 (stale): the shelf's viewport is only 68 DIP tall (card 44 + ShadowClearance 12 + LiftClearance
                // 12 — both added after this gate was written), so a contact at y=100 misses the viewport entirely and
                // the router's cross-axis fallback never resolves a target (verified: AbsoluteRect(vp) = {Y=0,H=68} at
                // this geometry). y=30 matches WheelShelfSettle's already-working convention for the same probe.
                var prod = new HeadlessScrollProducer(window, host, new Point2(150f, 30f)) { Device = DeviceClassIgnored };
                prod.ContactBegin(0f); prod.Step(16);
                for (int i = 0; i < 20 && i * 20f < MidPan; i++) { prod.ContactUpdate(20f); prod.Step(16); }
                for (int i = 0; i < 40; i++) host.RunFrame();   // hold the contact open — well past PagedShelf.SnapGraceMs
            }
            finally { Motion.ReducedMotion = previousReduced; }

            host.Scene.TryGetScroll(vp, out var held);
            bool stillTracking = held.Motion.Kind == FluentGpu.Scroll.Motion.MotionKind.Drag;   // the case was really exercised
            bool notResnapped = held.OffsetX > MidPan - 60f && held.OffsetX < MidPan + 60f;   // nowhere near the 0/330 boundaries a re-snap would target
            Check("gate.snap.shelf-live-phase a page shelf must NOT re-snap while the viewport is still in a LIVE gesture (Activity==Drag): a re-snap there would overwrite the live drag with a programmatic chase and kill the gesture",
                stillTracking && notResnapped,
                $"motion={held.Motion.Kind} (want Drag) off={held.OffsetX:0.##} (want ~{MidPan:0.##})");
        }

        // (h) gate.snap.shelf-directional-commit — WP-ε3, the commit RULE as pure math (PagedShelfCore<int>.CommitPage). Driving a
        // real release velocity through the dispatcher headlessly would pin the sampler, not the rule; the rule is what has to
        // hold. Four properties: a lift past CommitFraction advances; a short slow lift springs BACK (this is the case the
        // old nearest-boundary rule got wrong — 50% of a ~612 DIP page is unreachable by panning, so every pan was yanked
        // home); velocity carries a short FLICK past the threshold anyway; and the commit is RAILED to ±1 page. Plus the
        // degenerate contract every non-gesture settle depends on: no anchor ⇒ the nearest page, verbatim.
        {
            const float PageW = 612f;      // the artist-chart page stride the defect was measured on
            const int PageCount = 4;
            // 30% of a page with a forward lift → the NEXT page.
            int fwd30 = PagedShelfCore<int>.CommitPage(0.30f * PageW, 0f, 400f, PageW, PageCount, 0);
            // 10% with ZERO velocity → back to the page it started on (a slow, short drag is not a navigation).
            int slow10 = PagedShelfCore<int>.CommitPage(0.10f * PageW, 0f, 0f, PageW, PageCount, 0);
            // 10% but FLICKED: the projected resting offset (v / ScrollTuning.FlickProjectK ≈ v/5.7) carries it past 25%.
            int flick10 = PagedShelfCore<int>.CommitPage(0.10f * PageW, 0f, 1500f, PageW, PageCount, 0);
            // Backward: anchored on page 2, dragged 30% toward page 1 with a backward lift → page 1.
            int back = PagedShelfCore<int>.CommitPage(2f * PageW - 0.30f * PageW, 2f * PageW, -300f, PageW, PageCount, 2);
            // RAIL: three pages of travel in one gesture still advances exactly one.
            int railed = PagedShelfCore<int>.CommitPage(3f * PageW, 0f, 0f, PageW, PageCount, 3);
            // No anchor (a wheel notch / keyboard / chevron re-arm / a glide's own settle) ⇒ the nearest page, untouched.
            int noAnchor = PagedShelfCore<int>.CommitPage(0.90f * PageW, float.NaN, 9999f, PageW, PageCount, 1);
            // A tiny nudge that ENDS just past a page midpoint (so the anchor rounds UP to that page) must not read as
            // "0.4 pages backwards" and commit backward: the step has to agree with the gesture's net travel.
            int nudge = PagedShelfCore<int>.CommitPage(0.62f * PageW, 0.60f * PageW, 0f, PageW, PageCount, 1);
            Check("gate.snap.shelf-directional-commit the page a settled gesture commits to is anchor-relative, velocity-projected, travel-signed and railed to ±1 (a 30% lift advances, a 10% slow lift springs back, a 10% FLICK still advances, three pages of travel advances one, a nudge past a midpoint never commits backward) — and with no gesture anchor it degenerates to the nearest page verbatim, so every wheel/keyboard/chevron settle keeps its pre-existing behaviour",
                fwd30 == 1 && slow10 == 0 && flick10 == 1 && back == 1 && railed == 1 && noAnchor == 1 && nudge == 1,
                $"fwd30={fwd30}(1) slow10={slow10}(0) flick10={flick10}(1) back={back}(1) railed={railed}(1) noAnchor={noAnchor}(1) nudge={nudge}(1)");
        }
    }

    /// <summary>Count nodes in a subtree that arm EITHER hover-elevate bit (the deferral PARK on a cell, or the clip-root
    /// HOIST on a viewport). The pair arms together or not at all, so a multi-row shelf must report exactly 0.</summary>
    static int CountHoverElevate(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return 0;
        const uint both = InteractionInfo.HoverElevatePaintBit | InteractionInfo.HoverElevateClipRootBit;
        int count = (s.Interaction(n).HandlerMask & both) != 0 ? 1 : 0;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) count += CountHoverElevate(s, c);
        return count;
    }

    /// <summary>Wheel a page-shelf a FRACTIONAL distance (<paramref name="wheelDip"/> over a 330 stride), let the chase
    /// settle, and report the resting offset. Reduced motion pins the re-snap to a direct write so the assertion is exact
    /// rather than time-integrated (the same choice the sibling PagedShelf gate makes).</summary>
    static float WheelShelfSettle(StringTable strings, HeadlessFontSystem fonts, bool snap, out float snapInterval, out float pages,
                                  float wheelDip = 400f)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(snap ? "shelf-pagesnap" : "shelf-freepan", new Size2(640, 240), 1f));
        window.Show();
        Component probe = snap ? new PagedShelfSnapProbe() : (Component)new PagedShelfMeasuredProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame();
        for (int i = 0; i < 8; i++) host.RunFrame();      // fixed warm-up: measure → fit → realize the strip

        var vp = FindScrollable(host.Scene, host.Scene.Root);
        pages = probe is PagedShelfSnapProbe ps ? ps.Pager.PageCount : ((PagedShelfMeasuredProbe)probe).Pager.PageCount;

        bool previousReduced = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = true;
            // A vertical wheel over a chain with NO vertical scroller falls back to the horizontal shelf (WinUI semantics).
            // 400 DIP lands well inside page 1 (boundaries 0/330/660) — distinguishable from both the boundary and 0.
            window.QueueInput(WheelEvent(new Point2(150f, 30f), 0, 0, wheelDip));
            SettleScroll(host, vp);
        }
        finally { Motion.ReducedMotion = previousReduced; }

        host.Scene.TryGetScroll(vp, out var settled);
        snapInterval = settled.SnapInterval;
        return settled.OffsetX;
    }

    /// <summary>Run frames until this viewport is idle, then a fixed tail so the post-settle observer (and any re-snap it
    /// arms, plus THAT glide's own settle) lands. One helper so every shelf-snap gate settles identically.
    /// <para>The idle test is the viewport's <c>ScrollState.Motion</c> (the plan's motion kind, settled ⇒ Idle).</para></summary>
    static void SettleScroll(AppHost host, NodeHandle vp)
    {
        for (int round = 0; round < 4; round++)
        {
            for (int i = 0; i < 400; i++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                if (s.Motion.IsMoving == false && !s.UserScrollActive && i > 8) break;
            }
            // FORCED tail — Paint, not RunFrame. The shelf's post-settle re-snap is LIFT-DEBOUNCED (PagedShelf.SnapGraceMs
            // ≈ 180 ms of wall clock: the grace window that keeps a micro-paused two-finger pan from being snapped
            // mid-gesture), and a settled host has no active work, so RunFrame takes its idle early-out — which skips both
            // the observer pass AND the headless frame clock (the timer base advances only inside Paint). The clock would
            // freeze and the debounce could never come due. host.Paint(0) is the established headless idiom for exactly
            // this (see gate.timer.*); 28 × 16 ms ≈ 450 ms is comfortably past the grace window plus the glide it arms.
            for (int i = 0; i < 28; i++) host.Paint(0);
        }
    }

    /// <summary>Is <paramref name="off"/> on the page grid — a whole multiple of <paramref name="stride"/> within
    /// <paramref name="tol"/>? Compares the distance to the NEAREST multiple, so it is symmetric about a boundary
    /// (a remainder test alone reads 329.8 of a 330 stride as maximally off-grid).</summary>
    static bool OnGrid(float off, float stride, float tol)
        => stride > 1f && MathF.Abs(off - MathF.Round(off / stride) * stride) <= tol;

    /// <summary>Drive one programmatic page glide at a FIXED timestep: the offset after <paramref name="midFrames"/> ticks
    /// (mid-flight) and after full settle. Every host runs the identical frame script, so only dt differs.</summary>
    /// <summary>One page-glide nav at frame step <paramref name="dtMs"/>. Returns the authored glide's shape relative to
    /// its own start (travel, initial velocity, rate — absolute start time excluded, it is a clock reading), its position
    /// 200 ms after it was authored, the worst disagreement between any frame's SHOWN offset and the plan evaluated at
    /// that frame's present time, and the settled offset.</summary>
    static (double Travel, double V0, double K, double Mid, double WorstPoseErr, float Final) PageGlideTrace(StringTable strings, float dtMs)
    {
        var fonts = new HeadlessFontSystem(strings);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("page-glide-dt", new Size2(640, 240), 1f)); window.Show();
        var probe = new PagedShelfSnapProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe,
                                     frameTime: new FixedFrameTimeSource(dtMs));
        host.RunFrame();
        for (int i = 0; i < 8; i++) host.RunFrame();      // fixed warm-up

        var vp = FindScrollable(host.Scene, host.Scene.Root);
        var handle = host.TryGetScrollHandle(vp)!;
        probe.Pager.Next();                               // page 0 → 1: a 330 DIP programmatic glide, authored by the
        host.RunFrame();                                  // bring-into-view layout effect of THIS reconcile
        var plan = handle.Plan;
        var seg = plan.S0;
        double mid = plan.Eval(seg.T0 + 0.200, out _, out _);

        double worst = 0.0;
        float final = 0f;
        for (int i = 0; i < 4000; i++)
        {
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var s);
            if (!s.Motion.IsMoving) { final = s.OffsetX; break; }
            // The shown offset IS the plan at this frame's present time — no integrated state, whatever the frame step.
            double presentSec = FluentGpu.Hooks.FrameClock.PresentQpc / (double)System.Diagnostics.Stopwatch.Frequency;
            double expected = handle.Plan.Eval(presentSec, out _, out _);
            worst = Math.Max(worst, Math.Abs(s.Offset - expected));
            final = s.OffsetX;
        }
        return (seg.P1 - seg.P0, seg.V0, seg.K, mid, worst, final);
    }

    static void TwoAxisScrollChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("twoaxis", new Size2(220, 220), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new NestedScrollProbe());
        host.RunFrame();

        var outer = host.Scene.Root;                    // the vertical page scroller
        NodeHandle inner = NodeHandle.Null;             // the nested horizontal scroller
        void Visit(NodeHandle n)
        {
            if (n.IsNull) return;
            if (host.Scene.TryGetScroll(n, out var s) && s.Orientation == 1) inner = n;
            for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
        }
        Visit(outer);

        var pos = new Point2(90, 30);   // over the inner horizontal box (top strip; it stays there through both wheels)

        // Horizontal wheel over the inner box → the BOX scrolls on X; the page Y stays 0 (the swipe never leaks vertical).
        WheelDip(host, window, pos, 60f, horizontal: true);
        host.Scene.TryGetScroll(outer, out var o1);
        host.Scene.TryGetScroll(inner, out var i1);
        bool hWheelScrollsBox = Near(i1.OffsetX, 60) && Near(o1.OffsetY, 0);

        // Vertical wheel over the same box → the PAGE scrolls on Y (climbing past the horizontal box); the box X is unchanged.
        WheelDip(host, window, pos, 80f);
        host.Scene.TryGetScroll(outer, out var o2);
        host.Scene.TryGetScroll(inner, out var i2);
        bool vWheelScrollsPage = Near(o2.OffsetY, 80) && Near(i2.OffsetX, 60);

        Check("37b. wheel-axis routing: horizontal wheel scrolls a nested horizontal box (not the page); vertical wheel scrolls the page past it",
            !inner.IsNull && hWheelScrollsBox && vWheelScrollsPage,
            $"box.X={i2.OffsetX:0} page.Y={o2.OffsetY:0} (after-h: box.X={i1.OffsetX:0} page.Y={o1.OffsetY:0})");

        // gate.touchpad.axis-from-first-movement: a DirectManipulation-shaped two-finger contact — a Begin carrying NO
        // delta, then samples — is routed by its first MOVEMENT (dominant axis + direction), not by the empty Begin: a
        // horizontal swipe over the box scrolls the BOX on X (the page never moves), and a vertical swipe over the same
        // box scrolls the PAGE on Y.
        {
            host.TryGetScrollHandle(outer)!.ScrollTo(0.0, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            host.TryGetScrollHandle(inner)!.ScrollTo(0.0, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            for (int i = 0; i < 2; i++) host.RunFrame();
            uint ms = 90_000;
            void Swipe(uint id, float dx, float dy, int steps)
            {
                window.QueueInput(ScrollPhaseEvent(FluentGpu.Scroll.Runtime.ScrollGesture.Begin, pos, TimestampMs: ms, PointerId: id)); host.RunFrame();
                for (int i = 0; i < steps; i++)
                {
                    ms += 16;
                    window.QueueInput(ScrollPhaseEvent(FluentGpu.Scroll.Runtime.ScrollGesture.Sample, pos, ScrollDelta: dy, ScrollDeltaX: dx, TimestampMs: ms, PointerId: id));
                    host.RunFrame();
                }
                ms += 200;   // rest before the lift: no fling, the contact settles where it is
                for (int i = 0; i < 12; i++) host.RunFrame();
                window.QueueInput(ScrollPhaseEvent(FluentGpu.Scroll.Runtime.ScrollGesture.End, pos, TimestampMs: ms, PointerId: id));
                for (int i = 0; i < 30; i++) host.RunFrame();
            }
            Swipe(41, 6f, 0.4f, 8);   // mostly horizontal (a real two-finger swipe is never perfectly axis-aligned)
            host.Scene.TryGetScroll(outer, out var oh);
            host.Scene.TryGetScroll(inner, out var ih);
            bool hSwipeBox = ih.OffsetX > 30f && Near(oh.OffsetY, 0f);
            Swipe(42, 0.4f, 6f, 8);   // mostly vertical
            host.Scene.TryGetScroll(outer, out var ov);
            host.Scene.TryGetScroll(inner, out var iv);
            bool vSwipePage = ov.OffsetY > 30f && Near(iv.OffsetX, ih.OffsetX, 0.5f);
            Check("gate.touchpad.axis-from-first-movement a delta-less contact Begin is routed by its first movement: a horizontal two-finger swipe over a nested horizontal box scrolls the box (never the page); a vertical one scrolls the page",
                hSwipeBox && vSwipePage,
                $"after-h: box.X={ih.OffsetX:0.#} page.Y={oh.OffsetY:0.#} | after-v: box.X={iv.OffsetX:0.#} page.Y={ov.OffsetY:0.#}");
        }
    }

    static void ScrollCrossAxisChecks(StringTable strings)
    {
        const string longLine =
            "This intentionally unwrapped caption is long enough to exceed the viewport width and must not widen the vertical scroll content or nested virtual grids.";

        var tree = Ui.ScrollView(new BoxEl
        {
            Direction = 1,
            Gap = 12f,
            Padding = Edges4.All(24f),
            Children =
            [
                new TextEl(longLine) { Size = 14f },
                new BoxEl
                {
                    Height = 260f,
                    Children =
                    [
                        Virtual.Grid(1000, columns: 4, itemHeight: 110f, gap: 12f,
                            renderItem: i => new BoxEl { Fill = ColorF.FromRgba(40, 40, 40) },
                            keyOf: i => "g" + i),
                    ],
                },
            ],
        });

        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root, new Size2(480f, 320f));

        scene.TryGetScroll(scene.Root, out var outer);
        NodeHandle gridViewport = NodeHandle.Null;
        void Visit(NodeHandle n)
        {
            if (n.IsNull) return;
            if (scene.TryGetScroll(n, out var sc) && sc.ItemCount == 1000) gridViewport = n;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) Visit(c);
        }
        Visit(scene.Root);

        scene.TryGetScroll(gridViewport, out var gridScroll);
        var firstCard = scene.FirstChild(gridScroll.ContentNode);
        var grid = scene.AbsoluteRect(gridViewport);
        var card = scene.AbsoluteRect(firstCard);

        bool contentClamped = Near(outer.ContentW, outer.ViewportW) && Near(scene.Bounds(outer.ContentNode).W, outer.ViewportW);
        bool gridConstrained = !gridViewport.IsNull && Near(grid.W, 432f) && card.W <= 101f;

        Check("37a. vertical ScrollView clamps cross-axis content before nested virtual layout",
            contentClamped && gridConstrained,
            $"contentW={outer.ContentW:0} viewportW={outer.ViewportW:0} gridW={grid.W:0} cardW={card.W:0}");
    }

    static void ScrollOverlayChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scrollbar", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollProbe());

        static RectF DeviceRect(FillRoundRectCmd cmd) => cmd.Transform.TransformBounds(cmd.Rect);
        static bool LaneRect(FillRoundRectCmd cmd, out RectF r)
        {
            r = DeviceRect(cmd);
            return r.X >= 186f && r.X <= 200.5f && r.W <= 13.5f && r.H > 20f;
        }

        host.RunFrame();

        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(198f, 100f), 0, 0));
        ElapseFrames(host, 40);   // time passes through the lane dwell (no frames owed while it counts)

        bool expandedGutter = false, expandedThumb = false;
        foreach (var rect in device.LastRects)
        {
            if (!LaneRect(rect, out var r)) continue;
            expandedGutter |= r.W >= 10f && r.H >= 190f;
            expandedThumb |= r.W >= 5f && r.W < 10f && r.H >= 30f && r.H < 190f;
        }
        bool hoverSettledIdle = !host.HasActiveWork;

        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(260f, 260f), 0, 0));
        ElapseFrames(host, 30);

        bool collapsedGutter = false, collapsedThumb = false;
        foreach (var rect in device.LastRects)
        {
            if (!LaneRect(rect, out var r)) continue;
            collapsedGutter |= r.W >= 10f && r.H >= 190f;
            collapsedThumb |= r.W <= 3f && r.H >= 30f;   // WinUI resting thumb = 2px visible (8 − 6 stroke)
        }

        ElapseFrames(host, 90);

        bool anyScrollbar = false;
        foreach (var rect in device.LastRects)
        {
            anyScrollbar |= LaneRect(rect, out _);
        }

        Check("38a. overlay scrollbar expands, collapses to a visible thumb, then auto-hides",
            expandedGutter && expandedThumb && !collapsedGutter && collapsedThumb && !anyScrollbar,
            $"expanded=({expandedGutter},{expandedThumb}) collapsed=({collapsedGutter},{collapsedThumb}) hidden={!anyScrollbar}");
        Check("38b. hover-visible scrollbar settles without keeping frames active",
            hoverSettledIdle, $"active={host.HasActiveWork}");
    }

    static void BoundItemsViewChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("bound-iv", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new BoundItemsViewProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        host.RunFrame();
        var scene = host.Scene;
        var vp = FindScrollNode(scene, scene.Root);
        scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;

        // slot root (the AccentPill BoxEl) → [ lv-content → [text] , lv-pill ].
        var slot0 = scene.FirstChild(content);
        var lane0 = scene.FirstChild(slot0);
        var text0 = scene.FirstChild(lane0);
        var pill0 = scene.NextSibling(lane0);

        float pillRest = scene.Paint(pill0).Opacity;
        int callsBase = probe.TemplateCalls;

        // 1. Selecting a visible row flips its bound pill opacity 0→1 with NO row-template re-run (no rebuild = no flash).
        probe.Selection.Select(0);
        host.RunFrame();
        float pillSel = scene.Paint(pill0).Opacity;
        int callsSel = probe.TemplateCalls;
        Check("IV-bound 1. select flips the bound pill in place (no template re-run = no flash)",
            pillRest < 0.01f && pillSel > 0.99f && callsSel == callsBase,
            $"pill {pillRest:0.00}→{pillSel:0.00} templateCalls {callsBase}→{callsSel}");

        // 2. The selection re-skin frame allocates 0 managed bytes on the hot (paint) phases — it is a bound opacity write,
        //    not a rebuild.
        probe.Selection.Deselect(0);
        var fDe = host.RunFrame();
        Check("IV-bound 2. selection re-skin is 0-alloc on the hot phases",
            fDe.HotPhaseAllocBytes == 0, $"{fDe.HotPhaseAllocBytes} bytes");

        // 3. Now-playing recolours the bound title in place, again with NO row-template re-run.
        var colorRest = scene.Paint(text0).TextColor;
        int callsNowBase = probe.TemplateCalls;
        probe.NowPlaying.Value = 0;
        host.RunFrame();
        var colorNow = scene.Paint(text0).TextColor;
        Check("IV-bound 3. now-playing recolours the title in place (no template re-run)",
            !colorRest.Equals(colorNow) && probe.TemplateCalls == callsNowBase,
            $"colorChanged={!colorRest.Equals(colorNow)} templateCalls {callsNowBase}→{probe.TemplateCalls}");

        // 4. Exactly ONE realized slot carries the roving tab stop (NodeFlags.Focusable) — slot 0 via the no-current
        //    fallback (the others are Focusable=false, so the list has a single tab stop, moved without a re-render).
        int focusable = 0; NodeHandle theStop = NodeHandle.Null;
        for (var c = scene.FirstChild(content); !c.IsNull; c = scene.NextSibling(c))
            if ((scene.Flags(c) & NodeFlags.Focusable) != 0) { focusable++; theStop = c; }
        Check("IV-bound 4. exactly one realized slot holds the roving tab stop (no-current fallback = slot 0)",
            focusable == 1 && theStop == slot0, $"focusable={focusable} isSlot0={theStop == slot0}");

        // 5. A PROGRAMMATIC scroll (ItemsViewController.ScrollBy) moves the viewport WITHOUT re-rendering the ItemsView
        //    component. ScrollByDelta writes the offset + content transform + VirtualRangeDirty straight onto the
        //    retained scene and only needs a frame WAKE; re-rendering rebuilt a byte-identical element tree (the list
        //    element, every template/rowBind closure, and the keyed diff of the result) on every single scroll step and
        //    was the entire UI-thread allocation of a controller-driven scroll. The engine's own wheel/fling path never
        //    re-rendered here, so this is also what makes the two scroll paths behave alike.
        host.RunFrame();                                   // quiesce, so the baseline is a settled frame
        scene.TryGetScroll(vp, out var scBefore);
        float offBefore = scBefore.OffsetY;
        int callsScrollBase = probe.TemplateCalls;
        probe.Controller.ScrollBy(BoundItemsViewProbe.RowH * 5f);
        var fScroll = host.RunFrame();
        scene.TryGetScroll(vp, out var scAfter);
        Check("IV-bound 4b. programmatic ScrollBy moves the viewport with ZERO component re-renders (wake, don't re-render)",
            scAfter.OffsetY > offBefore + 1f && fScroll.ComponentsRendered == 0 && probe.TemplateCalls == callsScrollBase,
            $"offset {offBefore:0.0}→{scAfter.OffsetY:0.0} comps={fScroll.ComponentsRendered} templateCalls {callsScrollBase}→{probe.TemplateCalls}");

        // Same-count replacement: title is not special. Title, artist, album and duration are four separate mounted
        // binds, and all four must update on the same persistent row without re-running the template or replacing nodes.
        using var atomicApp = new HeadlessPlatformApp();
        var atomicWindow = new HeadlessWindow(new WindowDesc("bound-atomic", new Size2(640, 480), 1f));
        atomicWindow.Show();
        var atomicProbe = new BoundAtomicItemsProbe();
        using var atomicHost = new AppHost(atomicApp, atomicWindow, new HeadlessGpuDevice(), fonts, strings, atomicProbe);
        atomicHost.RunFrame();
        int atomicCalls = atomicProbe.TemplateCalls;
        var oldTitle = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "old-title");
        var oldArtist = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "old-artist");
        var oldAlbum = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "old-album");
        var oldDuration = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "101");
        atomicProbe.Items.Value = new BoundAtomicItem[]
        {
            new("new-title", "new-artist", "new-album", 303),
            new("other-new-title", "other-new-artist", "other-new-album", 404),
        };
        atomicHost.RunFrame();
        var newTitle = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "new-title");
        var newArtist = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "new-artist");
        var newAlbum = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "new-album");
        var newDuration = FindTextNode(atomicHost.Scene, strings, atomicHost.Scene.Root, "303");
        bool currentActionItem = atomicProbe.Source.TryPeek(0, out var current) && current.Title == "new-title";
        Check("IV-bound 4a. same-count source replacement atomically updates every cell on persistent row nodes",
            !oldTitle.IsNull && newTitle == oldTitle && newArtist == oldArtist && newAlbum == oldAlbum && newDuration == oldDuration
            && atomicProbe.TemplateCalls == atomicCalls && currentActionItem,
            $"sameNodes={newTitle == oldTitle && newArtist == oldArtist && newAlbum == oldAlbum && newDuration == oldDuration} " +
            $"templateCalls={atomicCalls}->{atomicProbe.TemplateCalls} currentItem={current.Title}");

        // 5/6. A clickable child + an inline link inside a COMPONENT-wrapped bound row must receive the click — not have
        //      it fall through to the row's tap/double-tap. Mirrors the exact Wavee row shape (skin → lane → component → grid).
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("bound-iv-hit", new Size2(640, 480), 1f));
        window2.Show();
        var hitProbe = new RowButtonHitProbe();
        using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, hitProbe);
        host2.RunFrame();
        var s2 = host2.Scene;
        var skin2 = s2.FirstChild(s2.Root);
        var lane2 = s2.FirstChild(skin2);
        var comp2 = s2.FirstChild(lane2);
        var grid2 = s2.FirstChild(comp2);
        var heartCell = s2.NextSibling(s2.FirstChild(grid2));    // grid child 1 (after the # cell)
        var heart = s2.FirstChild(heartCell);                    // the clickable heart BoxEl
        var hr = s2.AbsoluteRect(heart);
        var heartCenter = new Point2(hr.X + hr.W / 2f, hr.Y + hr.H / 2f);
        var hitNode = host2.Input.HitTest(heartCenter);
        window2.QueueInput(new InputEvent(InputKind.PointerDown, heartCenter, 0, 0));
        window2.QueueInput(new InputEvent(InputKind.PointerUp, heartCenter, 0, 0));
        host2.RunFrame();
        Check("IV-bound 5. a clickable child in a component-wrapped bound row gets the click (not the row's tap)",
            hitProbe.HeartClick == 1 && hitProbe.RowPress == 0 && hitNode == heart,
            $"heartClick={hitProbe.HeartClick} rowPress={hitProbe.RowPress} hitIsHeart={hitNode == heart}");

        var linkCell = s2.NextSibling(s2.NextSibling(heartCell)); // grid child 3 (#, heart, title, album)
        var span = s2.FirstChild(linkCell);                       // the SpanTextEl link node
        var lr = s2.AbsoluteRect(span);
        var linkPt = new Point2(lr.X + 4f, lr.Y + lr.H / 2f);     // over the link glyphs (left edge)
        window2.QueueInput(new InputEvent(InputKind.PointerDown, linkPt, 0, 0));
        window2.QueueInput(new InputEvent(InputKind.PointerUp, linkPt, 0, 0));
        host2.RunFrame();
        Check("IV-bound 6. an inline link in a component-wrapped bound row navigates (not the row's tap)",
            hitProbe.LinkClick == 1 && hitProbe.RowPress == 0,
            $"linkClick={hitProbe.LinkClick} rowPress={hitProbe.RowPress}");

        // 6b. A hyperlink span INSIDE a clickable card: the link is the click. Before the fix both fired — clicking the
        //     artist link on Wavee's search top-result card navigated AND played the song.
        using var appL = new HeadlessPlatformApp();
        var windowL = new HeadlessWindow(new WindowDesc("card-link-hit", new Size2(640, 480), 1f));
        windowL.Show();
        var cardProbe = new CardLinkHitProbe();
        using var hostL = new AppHost(appL, windowL, new HeadlessGpuDevice(), fonts, strings, cardProbe);
        hostL.RunFrame();
        var sL = hostL.Scene;
        var cardNode = sL.FirstChild(sL.Root);
        var cardSpan = sL.NextSibling(sL.FirstChild(cardNode));    // card child 1 = the SpanTextEl (after the title)
        var spanRect = sL.AbsoluteRect(cardSpan);
        // The link is the FIRST span ("Alex C.") — aim at the left edge of the laid run, over its glyphs (the IV-bound 6 shape).
        var cardLinkPt = new Point2(spanRect.X + 4f, spanRect.Y + spanRect.H / 2f);
        var cardLinkHit = hostL.Input.HitTest(cardLinkPt);
        windowL.QueueInput(new InputEvent(InputKind.PointerDown, cardLinkPt, 0, 0));
        windowL.QueueInput(new InputEvent(InputKind.PointerUp, cardLinkPt, 0, 0));
        hostL.RunFrame();
        int linkOnly = cardProbe.LinkClick, cardAfterLink = cardProbe.CardClick;
        var cardRect = sL.AbsoluteRect(cardNode);
        var cardBodyPt = new Point2(cardRect.X + cardRect.W - 20f, cardRect.Y + cardRect.H - 12f);   // bottom-right padding: no text there
        windowL.QueueInput(new InputEvent(InputKind.PointerDown, cardBodyPt, 0, 0));
        windowL.QueueInput(new InputEvent(InputKind.PointerUp, cardBodyPt, 0, 0));
        hostL.RunFrame();
        Check("IV-bound 6b. a hyperlink span inside a clickable card fires only the link; the card body still clicks",
            linkOnly == 1 && cardAfterLink == 0 && cardProbe.CardClick == 1 && cardProbe.LinkClick == 1,
            $"linkAfterLinkClick={linkOnly} cardAfterLinkClick={cardAfterLink} cardAfterBodyClick={cardProbe.CardClick} linkAfterBodyClick={cardProbe.LinkClick} hitIsSpan={cardLinkHit == cardSpan}");

        // 7. The VIRTUALIZED bound path: a clickable child on a realized CreateBound SLOT row must get the tap (not the row).
        using var app3 = new HeadlessPlatformApp();
        var window3 = new HeadlessWindow(new WindowDesc("bound-iv-vhit", new Size2(640, 480), 1f));
        window3.Show();
        var vProbe = new BoundListHitProbe();
        using var host3 = new AppHost(app3, window3, new HeadlessGpuDevice(), fonts, strings, vProbe);
        host3.RunFrame();
        vProbe.Now.Value = 0;     // trigger a row-content re-render first (rebuilds the child + its handler) — the Wavee path
        host3.RunFrame();
        var s3 = host3.Scene;
        var vp3 = FindScrollNode(s3, s3.Root);
        s3.TryGetScroll(vp3, out var sc3);
        var slotR = s3.FirstChild(sc3.ContentNode);          // slot 0 root (the row skin)
        var laneR = s3.FirstChild(slotR);                    // content lane
        var compR = s3.FirstChild(laneR);                    // the row-content component anchor
        var cellR = s3.FirstChild(compR);                    // the # cell (ZStack: reveal layer + click-catcher)
        var catcher = s3.NextSibling(s3.FirstChild(cellR));  // the Grow=1f click-catcher (2nd child)
        var cr = s3.AbsoluteRect(cellR);
        var cellCenter = new Point2(cr.X + cr.W / 2f, cr.Y + cr.H / 2f);
        var vHit = host3.Input.HitTest(cellCenter);
        window3.QueueInput(new InputEvent(InputKind.PointerDown, cellCenter, 0, 0));
        window3.QueueInput(new InputEvent(InputKind.PointerUp, cellCenter, 0, 0));
        host3.RunFrame();
        // …and again near the cell EDGE — proving the Grow=1f catcher FILLS the cell (not just a centered point).
        var cellEdge = new Point2(cr.X + 3f, cr.Y + cr.H - 3f);
        window3.QueueInput(new InputEvent(InputKind.PointerDown, cellEdge, 0, 0));
        window3.QueueInput(new InputEvent(InputKind.PointerUp, cellEdge, 0, 0));
        host3.RunFrame();
        Check("IV-bound 7. the # cell play-catcher (Grow=1f over a HoverOpacity reveal) takes the click across the cell, not the row",
            vProbe.ChildClick == 2 && vProbe.RowPress == 0 && vHit == catcher,
            $"childClick={vProbe.ChildClick} rowPress={vProbe.RowPress} hitIsCatcher={vHit == catcher} cellRect=({cr.X:0},{cr.Y:0},{cr.W:0},{cr.H:0})");

        // 8. THE REPORTED BUG: hovering an interactive CHILD (the # cell catcher) must KEEP the row's HoverWithin set, so
        //    the # cell's HoverOpacity reveal stays revealed (the play glyph must not snap back to the number when you
        //    move the pointer onto it to click). Hover the empty row body first, then move onto the catcher.
        var skinR = s3.FirstChild(sc3.ContentNode);
        var skinRect = s3.AbsoluteRect(skinR);
        window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(skinRect.X + 200f, skinRect.Y + skinRect.H / 2f), 0, 0));
        host3.RunFrame();
        bool bodyHover = (s3.Flags(skinR) & NodeFlags.Hovered) != 0;
        var catcherRect = s3.AbsoluteRect(catcher);
        window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(catcherRect.X + catcherRect.W / 2f, catcherRect.Y + catcherRect.H / 2f), 0, 0));
        host3.RunFrame();
        bool withinSet = (s3.Flags(skinR) & NodeFlags.HoverWithin) != 0;
        Check("IV-bound 8. hovering the # cell catcher keeps the row HoverWithin (so the play glyph stays revealed)",
            bodyHover && withinSet, $"bodyHover={bodyHover} rowHoverWithin={withinSet}");

        // 9. THE FIX (engine): with the pointer ON the catcher (from gate 8), the # cell's HoverOpacity reveal layer must
        //    stay DRIVEN (hover target 1 — it does not collapse to the number); it decays only when the pointer leaves
        //    the row entirely. Verifies the InteractionAnimator honors the container's HoverWithin.
        var revealLayer = s3.FirstChild(cellR);   // the HoverOpacity reveal layer (sibling of the catcher)
        bool revealOnCatcher = s3.TryGetInteract(revealLayer, out var raOn) && raOn.HoverTarget > 0.99f;
        window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(skinRect.X + skinRect.W + 80f, skinRect.Y + skinRect.H + 80f), 0, 0));
        host3.RunFrame();
        bool revealOffOutside = s3.TryGetInteract(revealLayer, out var raOff) && raOff.HoverTarget < 0.01f;
        Check("IV-bound 9. the # cell reveal stays driven while the pointer is on the play button, decays only off the row",
            revealOnCatcher && revealOffOutside, $"onCatcher={revealOnCatcher} offRow={revealOffOutside}");

        using var app4 = new HeadlessPlatformApp();
        var window4 = new HeadlessWindow(new WindowDesc("bound-iv-count-signal", new Size2(640, 480), 1f));
        window4.Show();
        var countProbe = new BoundCountSignalProbe();
        using var host4 = new AppHost(app4, window4, new HeadlessGpuDevice(), fonts, strings, countProbe);
        host4.RunFrame();
        var s4 = host4.Scene;
        var vp4 = FindScrollNode(s4, s4.Root);
        s4.TryGetScroll(vp4, out var sc4a);
        int slotsA = s4.ChildCount(sc4a.ContentNode);
        countProbe.Count.Value = 6;
        host4.RunFrame();
        s4.TryGetScroll(vp4, out var sc4b);
        int slotsB = s4.ChildCount(sc4b.ContentNode);
        countProbe.Count.Value = 3;
        host4.RunFrame();
        s4.TryGetScroll(vp4, out var sc4c);
        int slotsC = s4.ChildCount(sc4c.ContentNode);
        Check("IV-bound 10. bound ItemCount can change through a signal without remounting the list wrapper",
            sc4a.ItemCount == 4 && sc4b.ItemCount == 6 && sc4c.ItemCount == 3 && slotsA == 4 && slotsB == 6 && slotsC == 3,
            $"counts {sc4a.ItemCount}->{sc4b.ItemCount}->{sc4c.ItemCount} slots {slotsA}->{slotsB}->{slotsC}");
    }

    static void VirtualChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("virt", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new VirtualProbe());

        host.RunFrame();
        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc0);
        var content = sc0.ContentNode;
        int realized = host.Scene.ChildCount(content);
        bool windowed = realized >= 10 && realized < 40;
        bool contentSize = Near(sc0.ContentH, VirtualProbe.N * 40f);
        Check("38. virtualizes 10k rows to a small window", windowed && contentSize, $"realized={realized}/{VirtualProbe.N} content={sc0.ContentH:0}");

        // in-window (sub-extent) scroll = transform-only frame: no re-render, window unchanged, content shifted.
        int firstA = sc0.FirstRealized;
        var ptr = new Point2(150, 200);
        var f1 = WheelDip(host, window, ptr, 10f);
        host.Scene.TryGetScroll(vp, out var sc1);
        bool transformOnly = sc1.FirstRealized == firstA
            && Near(host.Scene.Paint(content).LocalTransform.Dy, -10f) && f1.MeasureCount == 0 && f1.ArrangeCount == 0;
        Check("39. in-window scroll is transform-only (no realize/relayout)", transformOnly, $"rendered={f1.Rendered} first={sc1.FirstRealized}");

        // boundary-crossing fling to the end: window re-realizes directly; recycle keeps live nodes bounded (no leak).
        long live0 = host.Scene.LiveCount;
        for (int s = 0; s < 60; s++) WheelDip(host, window, ptr, 7000f);
        host.Scene.TryGetScroll(vp, out var sc2);
        long liveEnd = host.Scene.LiveCount;
        bool reachedEnd = sc2.FirstRealized > 9000;
        bool bounded = liveEnd < 400 && liveEnd - live0 < 300;   // bounded by the velocity-sized window (≤ 2400 DIP of lead), never the list
        Check("40. fling recycles via free-list (bounded live nodes, no leak)", reachedEnd && bounded, $"first={sc2.FirstRealized} live {live0}→{liveEnd}");

        var counted = new CountingVirtualProbe();
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("virt-overlap", new Size2(640, 480), 1f));
        window2.Show();
        using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, counted);
        host2.RunFrame();
        int calls0 = counted.RenderItemCalls;
        var guardFrame = WheelDip(host2, window2, new Point2(150, 100), 80f);
        int guardCalls = counted.RenderItemCalls - calls0;
        host2.Scene.TryGetScroll(host2.Scene.Root, out var guardScroll);
        bool guardHeld = guardScroll.FirstRealized == 0 && guardCalls <= 2;   // an 80-DIP scroll (2 rows) realizes at most 2 entering rows

        // A 240-DIP scroll (6 rows) moves the window's trailing edge past row 0 (the behind floor is
        // MotionFeel.OverscanMinPx = 200 DIP, so the window's first row advances) — only the ~6 entering rows run the
        // template; every row still in the overlap keeps its element (a full window would be 15+ calls).
        const float shiftDip = 240f;
        WheelDip(host2, window2, new Point2(150, 100), shiftDip);
        int calls1 = counted.RenderItemCalls - calls0 - guardCalls;
        host2.Scene.TryGetScroll(host2.Scene.Root, out var countedScroll);
        int windowRows = countedScroll.LastRealized - countedScroll.FirstRealized;
        bool reusedOverlap = guardHeld && countedScroll.FirstRealized > 0 && calls1 <= (int)(shiftDip / 40f) + 1 && calls1 < windowRows;
        Check("40a. a small virtual scroll realizes at most its entering rows and reuses overlapping item elements",
            reusedOverlap, $"guardCalls={guardCalls} first={countedScroll.FirstRealized} newTemplateCalls={calls1} window={windowRows}");

        // Far jump (zero overlap — the scrollbar thumb-drag storm): the window's scene NODES are recycled in place
        // (columns rebound to the new items), not mounted/removed — the drag path becomes a column rewrite.
        var beforeNodes = new List<NodeHandle>();
        for (var c = host.Scene.FirstChild(content); !c.IsNull; c = host.Scene.NextSibling(c)) beforeNodes.Add(c);
        long liveBeforeJump = host.Scene.LiveCount;
        WheelDip(host, window, ptr, -400_000f);   // end → top: no overlap with the old window
        var afterNodes = new HashSet<NodeHandle>();
        for (var c = host.Scene.FirstChild(content); !c.IsNull; c = host.Scene.NextSibling(c)) afterNodes.Add(c);
        host.Scene.TryGetScroll(vp, out var scTop);
        int recycledCount = 0;
        foreach (var n in beforeNodes) if (afterNodes.Contains(n)) recycledCount++;
        var firstRowText = host.Scene.FirstChild(host.Scene.FirstChild(content));
        string rebound = strings.Resolve(host.Scene.Paint(firstRowText).Text);
        bool recycledOk = scTop.FirstRealized == 0 && recycledCount == afterNodes.Count
            && host.Scene.LiveCount <= liveBeforeJump && rebound == "row 0";
        Check("40b. far-jump realize recycles the window's scene nodes (rebind, no mount/remove)",
            recycledOk, $"first={scTop.FirstRealized} recycled={recycledCount}/{beforeNodes.Count} live {liveBeforeJump}→{host.Scene.LiveCount} text='{rebound}'");

        // 40b2 — theme correctness ON a recycled node: the default text color is a mount-time singleton-brush binding
        // that PERSISTS across recycle (Update rewrites columns, never bindings — recyclability of the bound default
        // rides that identity). A retheme AFTER the far-jump recycle must repaint the reused node via the re-fired
        // binding — the recycled row follows the live theme, not the color it was mounted under.
        {
            ColorF darkPrimary = Tok.TextPrimary;
            bool darkOk = host.Scene.Paint(firstRowText).TextColor == darkPrimary;
            Tok.Use(ThemeKind.Light);
            host.RunFrame();
            ColorF lightPrimary = Tok.TextPrimary;
            var t2 = host.Scene.FirstChild(host.Scene.FirstChild(content));
            bool lightOk = lightPrimary != darkPrimary && host.Scene.Paint(t2).TextColor == lightPrimary;
            Tok.Use(ThemeKind.Dark);
            host.RunFrame();
            Check("40b2. a recycled default-colored row follows a retheme (the persisted singleton-brush binding re-fires on the reused node)",
                darkOk && lightOk, $"dark={darkOk} light={lightOk}");
        }

        // Streaming thousands of unique row strings must NOT accrete in the interner: scrolled-out text releases its
        // ref, the map entry drops immediately, and the slot clears behind the reader quarantine (StringTable.Tick).
        int mapBase = strings.MapCount;
        for (int s = 0; s < 40; s++) WheelDip(host, window, ptr, 5_000f);
        for (int s = 0; s < 25; s++) { window.QueueInput(WheelEvent(ptr, 0, 0, s % 2 == 0 ? 1f : -1f)); host.RunFrame(); }   // settle past the quarantine (painted frames tick the table)
        int mapAfter = strings.MapCount;
        host.Scene.TryGetScroll(vp, out var scStream);
        bool streamed = scStream.FirstRealized > 3000;
        bool reclaimed = mapAfter - mapBase < 200 && strings.PendingReclaim < 200;
        Check("40c. scrolled-out row text is reclaimed by the interner (no per-row string accretion)",
            streamed && reclaimed, $"first={scStream.FirstRealized} map {mapBase}→{mapAfter} pending={strings.PendingReclaim}");

        // BOUND list (Virtual.ListBound): the template runs once per visible slot; a far jump rebinds index SIGNALS
        // only — same nodes, same elements, zero template re-runs, text/fill rebound in the same frame.
        var bound = new BoundVirtualProbe();
        using var app3 = new HeadlessPlatformApp();
        var window3 = new HeadlessWindow(new WindowDesc("virt-bound", new Size2(640, 480), 1f));
        window3.Show();
        using var host3 = new AppHost(app3, window3, new HeadlessGpuDevice(), fonts, strings, bound);
        host3.RunFrame();
        var vp3 = host3.Scene.Root;
        host3.Scene.TryGetScroll(vp3, out var bsc0);
        var content3 = bsc0.ContentNode;

        // First jump to mid-list (the window stabilizes at full size: overscan extends both directions), THEN measure.
        WheelDip(host3, window3, new Point2(150, 200), 100_000f);
        int slots0 = host3.Scene.ChildCount(content3);
        int templateCalls0 = bound.TemplateCalls;
        var slotNodes = new HashSet<NodeHandle>();
        for (var c = host3.Scene.FirstChild(content3); !c.IsNull; c = host3.Scene.NextSibling(c)) slotNodes.Add(c);

        WheelDip(host3, window3, new Point2(150, 200), 100_000f);   // far jump: zero overlap
        host3.Scene.TryGetScroll(vp3, out var bsc1);
        int slots1 = host3.Scene.ChildCount(content3);
        bool sameNodes = true;
        for (var c = host3.Scene.FirstChild(content3); !c.IsNull; c = host3.Scene.NextSibling(c)) sameNodes &= slotNodes.Contains(c);
        var boundFirstText = strings.Resolve(host3.Scene.Paint(host3.Scene.FirstChild(host3.Scene.FirstChild(content3))).Text);
        bool boundOk = bsc1.FirstRealized > 4000 && bound.TemplateCalls == templateCalls0 && slots1 == slots0
            && boundFirstText == $"row {bsc1.FirstRealized}";   // sameNodes: the pool re-attaches parked slots — a subset, not the identical set
        Check("40d. bound list rebinds via index signals on a far jump (no template re-run, no node churn)",
            boundOk, $"first={bsc1.FirstRealized} template {templateCalls0}→{bound.TemplateCalls} slots {slots0}→{slots1} sameNodes={sameNodes} text='{boundFirstText}'");

        // Scrollbar THUMB DRAG on a bound list (the 100k storm path): grab the thumb, drag in small steps — the
        // offset must track the thumb the whole way (the drag must never silently disengage mid-travel).
        WheelDip(host3, window3, new Point2(150, 200), -10_000_000f);   // back to the top
        float laneX = 294f;
        window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(laneX, 10f), 0, 0));
        host3.RunFrame();
        window3.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(laneX, 10f), 0, 0));
        host3.RunFrame();
        float lastOff = 0f;
        int advanceSteps = 0;
        const int dragSteps = 120;
        for (int s = 1; s <= dragSteps; s++)
        {
            window3.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(laneX, 10f + s * 2.5f), 0, 0));
            host3.RunFrame();
            host3.Scene.TryGetScroll(vp3, out var dsc);
            if (dsc.OffsetY > lastOff) advanceSteps++;
            lastOff = dsc.OffsetY;
        }
        window3.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(laneX, 10f + dragSteps * 2.5f), 0, 0));
        host3.RunFrame();
        host3.Scene.TryGetScroll(vp3, out var dragEnd);
        bool dragOk = advanceSteps >= dragSteps - 5 && dragEnd.OffsetY > 100_000f && dragEnd.FirstRealized > 2000;
        Check("40e. scrollbar thumb drag tracks the full travel on a bound list (never disengages)",
            dragOk, $"advanced {advanceSteps}/{dragSteps} off={dragEnd.OffsetY:0} first={dragEnd.FirstRealized}");
    }

    static void ExtentTableChecks()
    {
        var t = new ExtentTable(5, 10f);   // 5 items × 10 = 50
        bool init = Near((float)t.Total, 50) && Near(t.OffsetOf(0), 0) && Near(t.OffsetOf(2), 20) && Near(t.OffsetOf(5), 50);
        bool indexAt = t.IndexAt(0) == 0 && t.IndexAt(15) == 1 && t.IndexAt(25) == 2 && t.IndexAt(49) == 4;
        t.SetExtent(2, 30f);   // correct item 2: 10 → 30 (total 50 → 70)
        bool corrected = Near((float)t.Total, 70) && Near(t.OffsetOf(3), 50) && t.IndexAt(45) == 2 && t.IndexAt(55) == 3;
        Check("41. extent table: O(log n) offset↔index + correction", init && indexAt && corrected, $"total={t.Total:0}");
    }

    static void VariableChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("var", new Size2(640, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new VarProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        host.RunFrame();
        var vp = host.Scene.Root;
        host.Scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;

        // rows positioned by cumulative measured heights (OffsetOf), and the layout's own extent table
        // (IMeasuredVirtualLayout — Virtual.Measured drives the layout directly; no longer scene-owned) corrected for the window
        var r0 = Child(host.Scene, content, 0);
        var r1 = Child(host.Scene, content, 1);
        var r2 = Child(host.Scene, content, 2);
        var r3 = Child(host.Scene, content, 3);
        bool positions = Near(host.Scene.Bounds(r0).Y, 0)
            && Near(host.Scene.Bounds(r1).Y, VarProbe.H(0))
            && Near(host.Scene.Bounds(r2).Y, VarProbe.H(0) + VarProbe.H(1))
            && Near(host.Scene.Bounds(r3).Y, VarProbe.H(0) + VarProbe.H(1) + VarProbe.H(2));
        var layout = probe.Layout;
        bool measured = layout is not null
            && Near(layout.OffsetOf(1, 0f) - layout.OffsetOf(0, 0f), VarProbe.H(0))
            && Near(layout.OffsetOf(3, 0f) - layout.OffsetOf(2, 0f), VarProbe.H(2));
        Check("42. variable rows positioned by measured extents", positions && measured, $"y0..3={host.Scene.Bounds(r0).Y:0},{host.Scene.Bounds(r1).Y:0},{host.Scene.Bounds(r2).Y:0},{host.Scene.Bounds(r3).Y:0}");

        // scroll into the middle → the anchor is the first FULLY visible item (its start at or just below the viewport
        // top, the row before it starting above it) and the offset is clamped to the content
        for (int s = 0; s < 6; s++) { window.QueueInput(WheelEvent(new Point2(150, 150), 0, 0, 350f)); host.RunFrame(); }
        host.Scene.TryGetScroll(vp, out var sc2);
        int anchor = sc2.AnchorIndex;
        float band0 = layout!.OffsetOf(anchor, 0f), bandPrev = anchor > 0 ? layout.OffsetOf(anchor - 1, 0f) : float.NegativeInfinity;
        bool anchored = band0 >= sc2.OffsetY - 0.5f && bandPrev < sc2.OffsetY - 0.5f + 1f
            && sc2.OffsetY <= sc2.ContentH - sc2.ViewportH + 1f && sc2.FirstRealized > 0;
        Check("43. variable scroll anchors on the first fully visible item (clamped)", anchored, $"anchor={anchor} starts={band0:0} prev={bandPrev:0} off={sc2.OffsetY:0} content={sc2.ContentH:0} first={sc2.FirstRealized}");
    }


    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // gate.virt.slotPool* — the bound-slot pool at the high-water mark + receding-side retention (virtualization.md
    // §6.1a "slot pool"). A Wavee fling census read 8–12 COLD TableSlot mounts per frame against a pool of ~37 slots:
    // the realized window shrank on every budget-limited / direction-reversal frame (surplus slots removed) and grew
    // back over the following frames (fresh rowBind + Mount, 50–90 KB each). Now a surplus slot is PARKED (detached,
    // NodeFlags.Parked, images unpinned, index signal kept so no channel evaluates for an invisible row) and taken back
    // before rowBind — an exact index match is a zero-write re-attach — and the clip keeps receding rows under motion
    // so the window never shrinks mid-fling in the first place.
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    static void SlotPoolChecks(StringTable strings)
    {
        const double FlingRestDipPerS = 1.0;   // a coasting plan below this speed reads as at rest for the gates below
        static bool Reaches(SceneStore s, NodeHandle n, NodeHandle target)
        {
            for (var p = n; !p.IsNull; p = s.Parent(p)) if (p == target) return true;
            return false;
        }
        static NodeHandle LastChild(SceneStore s, NodeHandle parent)
        {
            var last = NodeHandle.Null;
            for (var c = s.FirstChild(parent); !c.IsNull; c = s.NextSibling(c)) last = c;
            return last;
        }
        static void Settle(AppHost host, int max = 40) { for (int i = 0; i < max && host.HasActiveWork; i++) host.RunFrame(); }

        // ── (a) grow → shrink → grow mounts nothing on the second grow; (c) parked slots are detached, Parked, unreachable
        // from the root, and hold no focus. The window is resized at REST through the viewport height (a signal), so the
        // shrink is the reconciler's own surplus path, not a scroll artefact. ─────────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-slot-pool", new Size2(640, 480), 1f)); window.Show();
            var probe = new SlotPoolOverscanProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
            host.RunFrame();
            Settle(host);
            var vp = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc0);
            var content = sc0.ContentNode;
            int slots0 = host.Scene.ChildCount(content), template0 = probe.TemplateCalls, spare0 = host.Reconciler.SpareSlotCount(vp);

            // Focus the halo's tail row: it leaves the window on the shrink, so the park must take focus with it.
            var tail = LastChild(host.Scene, content);
            host.Input.SetFocus(tail);
            host.RunFrame();
            bool tailFocused = host.Input.Focused == tail;

            probe.Vh.Value = 120f;
            host.RunFrame();
            Settle(host);
            host.Scene.TryGetScroll(vp, out var sc1);
            int slots1 = host.Scene.ChildCount(content), template1 = probe.TemplateCalls, spare1 = host.Reconciler.SpareSlotCount(vp);
            bool shrank = slots1 < slots0 && sc1.LastRealized - sc1.FirstRealized == slots1;
            bool conserved = slots1 + spare1 == slots0 && template1 == template0;   // parked, not removed; nothing rebuilt

            bool parkedOk = spare1 > 0, focusOff = true, tailParked = false;
            for (int i = 0; i < spare1; i++)
            {
                if (!host.Reconciler.TryGetSpareSlotRoot(vp, i, out var r)) { parkedOk = false; break; }
                bool detached = host.Scene.IsLive(r) && host.Scene.Parent(r).IsNull && !Reaches(host.Scene, r, host.Scene.Root);
                bool parked = (host.Scene.Flags(r) & NodeFlags.Parked) != 0;
                bool focusable = (host.Scene.Flags(r) & (NodeFlags.Focused | NodeFlags.Hovered | NodeFlags.Pressed)) != 0;
                parkedOk &= detached && parked && !focusable;
                if (r == tail) tailParked = true;
                if (!host.Input.Focused.IsNull && Reaches(host.Scene, host.Input.Focused, r)) focusOff = false;
            }
            bool focusCleared = host.Input.Focused != tail;

            int nodes0 = host.Reconciler.MountedNodes;
            probe.Vh.Value = 400f;
            host.RunFrame();
            Settle(host);
            host.Scene.TryGetScroll(vp, out var sc2);
            int slots2 = host.Scene.ChildCount(content), template2 = probe.TemplateCalls, spare2 = host.Reconciler.SpareSlotCount(vp);
            int mounted = host.Reconciler.MountedNodes - nodes0;
            var lastRow = LastChild(host.Scene, content);
            string lastText = lastRow.IsNull ? "" : strings.Resolve(host.Scene.Paint(host.Scene.FirstChild(lastRow)).Text);
            bool regrew = slots2 == slots0 && spare2 == 0 && sc2.LastRealized - sc2.FirstRealized == slots2;
            bool rebound = lastText == $"row {sc2.LastRealized - 1}";   // a taken slot reads its NEW item after the flush

            Check("gate.virt.slotPoolGrowShrinkGrow a bound list that shrinks its window at rest parks the surplus slots (Slots + Spare == the high-water window, zero removals) and the next grow takes them back with zero rowBind calls and zero mounts",
                shrank && conserved && regrew && template2 == template0 && mounted == 0 && rebound,
                $"slots {slots0}->{slots1}->{slots2} spare {spare0}->{spare1}->{spare2} template {template0}->{template1}->{template2} mounted={mounted} lastText='{lastText}' realized=[{sc2.FirstRealized},{sc2.LastRealized})");
            Check("gate.virt.slotPoolParkedInvisibleUnfocusable every parked spare slot is live but detached (no parent, unreachable from the scene root ⇒ no layout/paint/hit-test), carries NodeFlags.Parked, holds no hover/press/focus flag, and the focused halo row lost focus when it was parked",
                parkedOk && tailFocused && tailParked && focusCleared && focusOff,
                $"spares={spare1} parkedOk={parkedOk} tailFocused={tailFocused} tailParked={tailParked} focusCleared={focusCleared} focusOff={focusOff} focus={host.Input.Focused.Raw.Index}");
        }

        // ── (d) the pool trims when ItemCount falls below it — at rest, on the same realize pass; the pool ends at
        // exactly ItemCount slots (Slots + Spare == ItemCount), so a list that shrank for good frees its spares. ──────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-slot-pool-trim", new Size2(640, 480), 1f)); window.Show();
            var probe = new BoundCountSignalProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
            host.RunFrame();
            Settle(host);
            var vp = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc);
            var content = sc.ContentNode;
            probe.Count.Value = 6;
            host.RunFrame(); Settle(host);
            int live6 = host.Scene.ChildCount(content), spare6 = host.Reconciler.SpareSlotCount(vp);
            probe.Count.Value = 3;
            host.RunFrame(); Settle(host);
            int live3 = host.Scene.ChildCount(content), spare3 = host.Reconciler.SpareSlotCount(vp);
            bool clean = (host.Scene.Flags(vp) & NodeFlags.VirtualRangeDirty) == 0;
            probe.Count.Value = 5;
            host.RunFrame(); Settle(host);
            int live5 = host.Scene.ChildCount(content), spare5 = host.Reconciler.SpareSlotCount(vp);
            Check("gate.virt.slotPoolTrimsOnIdleCountShrink an idle bound list whose ItemCount falls below its slot pool frees the surplus spares on that pass (pool == ItemCount, list clean), and a later count growth mounts fresh slots again",
                live6 == 6 && spare6 == 0 && live3 == 3 && spare3 == 0 && clean && live5 == 5 && spare5 == 0,
                $"6: live={live6} spare={spare6}; 3: live={live3} spare={spare3} clean={clean}; 5: live={live5} spare={spare5}");
        }

        // ── (b) a direction reversal mid-fling produces no cold mounts: the velocity-sized realize window keeps the
        // receding rows covered while the plan moves, so the halo swing recycles slots instead of removing and
        // re-mounting them. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-slot-pool-reversal", new Size2(640, 480), 1f)); window.Show();
            var probe = new BoundVirtualProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
            host.RunFrame();
            Settle(host);
            var vp = host.Scene.Root;
            // Mid-list, so neither halo is clipped by a content edge and the pool sits at the full E5 fixed-sum width.
            host.TryGetScrollHandle(vp)?.ScrollTo(200_000f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            for (int i = 0; i < 50; i++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var scWarm);
            int poolWarm = (scWarm.LastRealized - scWarm.FirstRealized) + host.Reconciler.SpareSlotCount(vp);

            var prod = new HeadlessScrollProducer(window, host, new Point2(150, 200)) { Device = DeviceClassIgnored };
            prod.ContactBegin(0f); prod.Step(16);
            for (int i = 0; i < 10; i++) { prod.ContactUpdate(60f); prod.Step(16); }
            prod.ContactEnd();
            int coastFrames = 0;
            for (int f = 0; f < 12; f++) { prod.Step(16); host.Scene.TryGetScroll(vp, out var scC); if (Math.Abs(scC.Velocity) <= FlingRestDipPerS) break; coastFrames++; }
            host.Scene.TryGetScroll(vp, out var scFwd);
            bool movingForward = scFwd.Velocity > FlingRestDipPerS;

            int template0 = probe.TemplateCalls, nodes0 = host.Reconciler.MountedNodes;
            int minWidth = int.MaxValue, maxPool = 0, reverseFrames = 0;
            bool blanked = false, reversed = false;
            prod.ContactBegin(0f); prod.Step(16);
            for (int i = 0; i < 10; i++) { prod.ContactUpdate(-60f); prod.Step(16); }
            prod.ContactEnd();
            for (int f = 0; f < 90; f++)
            {
                prod.Step(16);
                host.Scene.TryGetScroll(vp, out var s);
                if (s.Velocity < -FlingRestDipPerS) reversed = true;
                int width = s.LastRealized - s.FirstRealized;
                minWidth = Math.Min(minWidth, width);
                maxPool = Math.Max(maxPool, width + host.Reconciler.SpareSlotCount(vp));
                int vFirst = (int)MathF.Floor(s.OffsetY / 40f), vLast = Math.Min(BoundVirtualProbe.N, (int)MathF.Ceiling((s.OffsetY + s.ViewportH) / 40f));
                if (!(s.FirstRealized <= vFirst && s.LastRealized >= vLast)) blanked = true;
                if (Math.Abs(s.Velocity) <= FlingRestDipPerS && f > 12) break;
                reverseFrames++;
            }
            int coldTemplates = probe.TemplateCalls - template0, mounted = host.Reconciler.MountedNodes - nodes0;
            Check("gate.virt.slotPoolReversalNoColdMounts a direction reversal mid-fling runs zero rowBind calls and zero mounts (receding rows are retained under motion, the window never narrows below its pool, the pool never exceeds its high-water mark by more than the one-row viewport-alignment flutter) and never blanks a visible row",
                movingForward && reversed && coldTemplates == 0 && mounted == 0 && !blanked,
                $"poolWarm={poolWarm} minWidth={minWidth} maxPool={maxPool} coldTemplates={coldTemplates} mounted={mounted} blanked={blanked} coast={coastFrames} reverseFrames={reverseFrames} fwd={movingForward} rev={reversed}");
        }

        // ── (e) hot-phase allocation during a synthetic fling over a 10k bound list, once warm. Ceiling: 0 bytes in frame
        // phases 6–13 — the standing corpus contract (the pool adds no allocation category there: park/take are a Detach,
        // an AppendChild, flag/pin bookkeeping and at most one signal write). The whole-frame delta is reported in the
        // detail for the census, not gated: the reconcile edge is "bounded Gen0" by canon, not zero. ─────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-slot-pool-alloc", new Size2(640, 480), 1f)); window.Show();
            // Fill-only rows: a bound TEXT channel formats a string on every recycle ($"row {i}"), which is the
            // template's own allocation and the reason BoundVirtualFillOnlyProbe exists — this gate measures the pool.
            var probe = new SlotPoolFillOnlyProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
            host.RunFrame();
            Settle(host);
            var vp = host.Scene.Root;
            host.TryGetScrollHandle(vp)?.ScrollTo(100_000f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            for (int i = 0; i < 40; i++) host.RunFrame();
            var prod = new HeadlessScrollProducer(window, host, new Point2(150, 200)) { Device = DeviceClassIgnored };

            (long WorstHot, long WorstFrame, int Templates) Fling(float dy, bool measure)
            {
                long worstHot = 0, worstFrame = 0; int t0 = probe.TemplateCalls;
                void Acc(FrameStats f, long frameBytes)
                {
                    if (!measure) return;
                    if (f.HotPhaseAllocBytes > worstHot) worstHot = f.HotPhaseAllocBytes;
                    if (frameBytes > worstFrame) worstFrame = frameBytes;
                }
                prod.ContactBegin(0f); Acc(prod.Step(16), 0);
                for (int i = 0; i < 10; i++) { prod.ContactUpdate(dy); long b = GC.GetAllocatedBytesForCurrentThread(); var f = prod.Step(16); Acc(f, GC.GetAllocatedBytesForCurrentThread() - b); }
                prod.ContactEnd();
                for (int f = 0; f < 120; f++)
                {
                    long b = GC.GetAllocatedBytesForCurrentThread();
                    var fs = prod.Step(16);
                    Acc(fs, GC.GetAllocatedBytesForCurrentThread() - b);
                    host.Scene.TryGetScroll(vp, out var s);
                    if (Math.Abs(s.Velocity) <= FlingRestDipPerS) break;
                }
                return (worstHot, worstFrame, probe.TemplateCalls - t0);
            }
            Fling(60f, measure: false);    // warm: the pool reaches its high-water mark, every code path JITs
            Fling(-60f, measure: false);
            var fwd = Fling(60f, measure: true);
            var rev = Fling(-60f, measure: true);
            Check("gate.virt.slotPoolFlingAllocCeiling a warm forward+reverse fling over a 10k bound list allocates 0 bytes in frame phases 6–13 on every frame and runs zero rowBind calls (whole-frame worst reported for the census, not gated)",
                fwd.WorstHot == 0 && rev.WorstHot == 0 && fwd.Templates == 0 && rev.Templates == 0,
                $"hotWorst fwd={fwd.WorstHot}B rev={rev.WorstHot}B; frameWorst fwd={fwd.WorstFrame}B rev={rev.WorstFrame}B; templates fwd={fwd.Templates} rev={rev.Templates}");
        }
    }


    static void ScrollParityChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // (The wheel's per-notch distance is the feel's fixed WheelNotchDip, accumulated onto the live glide's destination:
        // ScrollMotionTests.WheelNotch_* in Engine.Tests + gate.scroll.notch-to-present.)

        // gate.scroll.empty-show-overlay-yields: a Flow.Show overlay layer that is CLOSED must not eat the wheel.
        // The boundary node stays live with no child, ArrangeZStack stretches an auto-sized child to the whole slot, and
        // CreateNode hands every node HitTestVisible — so without MirrorParticipation's pass-through the empty anchor is
        // a full-bleed hittable node above the scroller. `Hit` (handler-gated) walks past it, so CLICKS keep working;
        // `HitTestAny` (handler-less — wheel targets, drop targets, middle-click) returns it for every point and the
        // wheel finds no Scrollable ancestor. Shape taken verbatim from the app shell's overlay ZStack.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("empty-show-overlay", new Size2(300, 300), 1f)); window.Show();
            var probe = new EmptyShowOverlayScrollProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            for (int i = 0; i < 4 && host.HasActiveWork; i++) host.RunFrame();

            var viewport = FindScrollable(host.Scene, host.Scene.Root);
            window.QueueInput(WheelEvent(new Point2(100, 100), 0, 0, 120f));
            for (int i = 0; i < 12; i++) host.RunFrame();
            host.Scene.TryGetScroll(viewport, out var scClosed);
            float closedOffset = scClosed.OffsetY;

            // Control: with the overlay OPEN the layer is a real full-bleed box and legitimately owns the point, so the
            // same wheel must NOT reach the scroller — proving the fix yields the hit rather than disabling the layer.
            probe.OverlayOpen.Value = true;
            for (int i = 0; i < 4; i++) host.RunFrame();
            // OffsetY is a RESULT column now (get; private set) — reset via a posted immediate ScrollTo instead of a raw write.
            host.TryGetScrollHandle(viewport)?.ScrollTo(0f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            window.QueueInput(WheelEvent(new Point2(100, 100), 0, 0, 120f));
            for (int i = 0; i < 12; i++) host.RunFrame();
            host.Scene.TryGetScroll(viewport, out var scOpen);
            float openOffset = scOpen.OffsetY;

            Check("gate.scroll.empty-show-overlay-yields a CLOSED Flow.Show overlay layer in a ZStack keeps its live child-less anchor out of the handler-less hit walk, so a wheel over it still reaches the scroller beneath; the same layer OPEN consumes OnPointerWheel (Handled) and blocks the list",
                !viewport.IsNull && closedOffset > 1f && openOffset <= 0.01f,
                $"viewport={(viewport.IsNull ? "null" : "ok")} closedOffsetY={closedOffset:0.##} (expect >0) openOffsetY={openOffset:0.##} (expect 0)");
        }

        // gate.touch.flick-velocity-windowed: the windowed least-squares velocity sampler reads a fast constant-velocity
        // flick's TRUE terminal speed (the 50ms EMA under-read it — gain dt/(dt+50) lags before convergence), AND a finger
        // that moves fast then HOLDS STILL before lift decays to ~0 (the old EMA's stationary up-sample bias kept stale
        // momentum). Drives the dispatcher directly and reads PointerVelocity.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("flick-vel", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new TouchFlingSettleProbe());
            host.RunFrame();
            uint t = s_touchClockMs;
            var ev = new InputEvent[1];
            ev[0] = Touch(InputKind.PointerDown, new Point2(150, 384), t, 21); host.Input.Dispatch(ev); host.RunFrame();
            float y = 384f;
            for (int i = 0; i < 10; i++) { t += 8; y -= 16f; ev[0] = Touch(InputKind.PointerMove, new Point2(150, y), t, 21); host.Input.Dispatch(ev); host.RunFrame(); }
            float vFast = host.Input.PointerVelocity.Y;   // ≈ -2000 px/s (16px / 8ms, upward ⇒ negative)
            bool accurate = vFast <= -1700f;              // near the true -2000 (the EMA lagged to ~-1500); regression reads true
            // Now HOLD the finger still for several samples → the in-window slope goes to 0 (no stale momentum carried).
            for (int i = 0; i < 8; i++) { t += 8; ev[0] = Touch(InputKind.PointerMove, new Point2(150, y), t, 21); host.Input.Dispatch(ev); host.RunFrame(); }
            float vRest = host.Input.PointerVelocity.Y;
            bool restZero = MathF.Abs(vRest) < 60f;
            ev[0] = Touch(InputKind.PointerUp, new Point2(150, y), t + 8, 21); host.Input.Dispatch(ev); host.RunFrame();
            s_touchClockMs = t + 1000;
            Check("gate.touch.flick-velocity-windowed the windowed-regression velocity sampler reads a fast flick's TRUE terminal speed (no EMA under-read) and decays a paused-then-held finger to ~0 (the stationary up-sample bias is gone)",
                accurate && restZero, $"fastV={vFast:0} (true≈-2000, want ≤-1700) heldStillV={vRest:0} (want ~0)");
        }

        // (Release-velocity seeding, the engine-owned fling and the rubber band are closed-form plan authoring now —
        // PlanAuthor.FollowEnd — pinned by ScrollMotionTests.FollowEnd_* / Eval_IsContinuousAcrossSegmentBoundaries,
        // ScrollRuntimeTests.Handle_ContactSamplesAheadOfThePlanClock_*, gate.touch4.snap-fling-dt-invariant and
        // gate.touch4.overscroll-springback.)
    }

    static void ScrollV2ValidationChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // Properties the deleted per-tick integrator's gates used to pin now hold BY CONSTRUCTION of the closed-form plan
        // (a scroll position is p(t) of one absolute time — there is no dt, no resampler, no catch-up), and are pinned by:
        //   single writer        — PlanSlots has one writer (ScrollHandle): ScrollRuntimeTests.PlanSlots_*;
        //   dt / sub-pixel       — ScrollMotionTests.Eval_AtSharedTimes_*, gate.snap.page-glide-dt-invariant,
        //                          gate.scroll.precision-deep, gate.scroll.sticky-grid;
        //   contact 1:1          — gate.scroll.anchor-holds, gate.scroll.explicit-measured-correction-direct-touch;
        //   coast / impulse      — ScrollMotionTests.FollowEnd_*, gate.touch.flick-decay-settle;
        //   overscroll           — ScrollMotionTests.FollowEnd_LiftedWhileOverpanned_*, gate.touch4.overscroll-springback;
        //   pointer-down stops   — ScrollRuntimeTests.Handle_Stop_*, gate.touch.tap-vs-pan (tap-to-stop);
        //   wheel lines/accum    — ScrollMotionTests.WheelNotch_*, gate.scroll.notch-to-present;
        //   alloc                — gate.scroll.100k-flat-zero-alloc.

        // Seed the explicit-correction repro identically for each phase arm: scroll far enough that row 2 is no longer
        // realized, then cache a 264-DIP "drawer" extent there. The visible row+within-row anchor must move +200 DIP,
        // while the probe component itself remains run-once. Arm one idle integrator tick to drain the setup re-pin so
        // each arm starts with a clean PendingAnchorShift and measures only its collapse correction.
        // host.ScrollIntegratorForTest.Arm(viewport) is gone — the kernel doesn't need a manual "arm" call; the two
        // RunFrame()s below already let it drain the setup's own re-pin. The old ScrollState.PendingAnchorShift==0
        // "clean baseline" check is replaced by an explicit stability probe: run one more frame and require the offset
        // hasn't drifted, which is the observable proxy for "the setup shift already settled to zero".
        static bool PrimeExplicitCorrection(AppHost host, ExplicitMeasuredCorrectionProbe probe,
                                              out NodeHandle viewport, out ScrollState state)
        {
            host.RunFrame(); host.RunFrame();
            viewport = probe.Controller.Viewport;
            if (viewport.IsNull) { state = default; return false; }

            probe.Controller.ScrollBy(20f * ExplicitMeasuredCorrectionProbe.RowH + 7f);
            host.RunFrame(); host.RunFrame();
            if (!host.Scene.TryGetScroll(viewport, out var beforeGrow)) { state = default; return false; }
            int anchor = probe.Layout.IndexAt(beforeGrow.OffsetY, beforeGrow.ViewportW);
            float within = beforeGrow.OffsetY - probe.Layout.OffsetOf(anchor, beforeGrow.ViewportW);
            int renders = probe.RenderCount;

            bool grew = probe.Controller.CorrectMeasuredExtent(probe.Layout,
                ExplicitMeasuredCorrectionProbe.CorrectedIndex, ExplicitMeasuredCorrectionProbe.ExpandedH);
            host.RunFrame(); host.RunFrame();
            if (!host.Scene.TryGetScroll(viewport, out state)) return false;

            int grownAnchor = probe.Layout.IndexAt(state.OffsetY, state.ViewportW);
            float grownWithin = state.OffsetY - probe.Layout.OffsetOf(grownAnchor, state.ViewportW);
            host.RunFrame();
            bool stableBaseline = host.Scene.TryGetScroll(viewport, out var afterStabilityFrame)
                && Near(afterStabilityFrame.OffsetY, state.OffsetY, 0.05f);
            return grew
                && beforeGrow.FirstRealized > ExplicitMeasuredCorrectionProbe.CorrectedIndex
                && grownAnchor == anchor && Near(grownWithin, within, 0.01f)
                && Near(state.ContentH, ExplicitMeasuredCorrectionProbe.N * ExplicitMeasuredCorrectionProbe.RowH
                    + ExplicitMeasuredCorrectionProbe.ExtentDelta, 0.01f)
                && stableBaseline
                && probe.RenderCount == renders;
        }

        // Explicit correction during a PROGRAMMATIC chase. The destination was resolved in the expanded coordinate
        // space, so collapsing an off-screen row above both the viewport and destination must translate the live offset,
        // Target, and PendingTarget together by -200. The chase must then settle on the same logical item in the new
        // coordinate space, with no component re-render caused by the correction.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("explicit-correction-programmatic", new Size2(360, 460), 1f));
            window.Show();
            var probe = new ExplicitMeasuredCorrectionProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            bool primed = PrimeExplicitCorrection(host, probe, out var vp, out _);

            const int Destination = 60;
            probe.Controller.StartBringItemIntoView(Destination, alignmentRatio: 0f, animate: true);
            // NOTE (assumption): the old integrator wrote PendingTargetY synchronously on the dispatch call itself; the
            // new kernel arms via a posted ScrollInput and only applies it on Tick, so one RunFrame is needed here
            // before the "before" snapshot is meaningful. Flagged as an assumption in the report.
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var before);
            float cross = before.ViewportW;
            int anchor = probe.Layout.IndexAt(before.OffsetY, cross);
            float within = before.OffsetY - probe.Layout.OffsetOf(anchor, cross);
            int renders = probe.RenderCount;

            var handleX = host.TryGetScrollHandle(vp)!;
            float beforeNow = (float)handleX.OffsetNow;   // the SAME clock the post-correction read below uses
            anchor = probe.Layout.IndexAt(beforeNow, cross);
            within = beforeNow - probe.Layout.OffsetOf(anchor, cross);
            bool corrected = probe.Controller.CorrectMeasuredExtent(probe.Layout,
                ExplicitMeasuredCorrectionProbe.CorrectedIndex, ExplicitMeasuredCorrectionProbe.RowH);
            // CorrectMeasuredExtent shifts the live plan in the SAME frame (Virtualizer.ApplyMeasured → ScrollHandle.ShiftFrame):
            // read the shifted offset off the handle at this very instant (no frame has run, so no chase advanced), then
            // run one frame for the layout-published ContentH.
            float shiftedNow = (float)handleX.OffsetNow;
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var after);
            int afterAnchor = probe.Layout.IndexAt(shiftedNow, cross);
            float afterWithin = shiftedNow - probe.Layout.OffsetOf(afterAnchor, cross);
            float d = -ExplicitMeasuredCorrectionProbe.ExtentDelta;
            bool active = before.Motion.Kind == FluentGpu.Scroll.Motion.MotionKind.Programmatic;
            bool immediate = corrected && afterAnchor == anchor && Near(afterWithin, within, 0.01f)
                && Near(shiftedNow, beforeNow + d, 0.5f)
                && Near(after.ContentH, ExplicitMeasuredCorrectionProbe.N * ExplicitMeasuredCorrectionProbe.RowH, 0.01f)
                && probe.RenderCount == renders;

            bool settled = false;
            for (int i = 0; i < 600; i++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                if (s.Motion.IsMoving == false) { settled = true; break; }
            }
            host.Scene.TryGetScroll(vp, out var fin);
            float expected = probe.Layout.OffsetOf(Destination, fin.ViewportW);
            bool landed = settled && Near(fin.OffsetY, expected, 0.6f) && probe.RenderCount == renders;
            Check("gate.scroll.explicit-measured-correction-programmatic collapsing an UNREALIZED measured row above the viewport during a programmatic chase preserves row+within-row, rebases Offset/ContentH by the exact extent delta immediately, and settles on the same logical destination without a component re-render",
                primed && active && immediate && landed,
                $"primed={primed} active={active} immediate={immediate} landed={landed} anchor={anchor}->{afterAnchor} within={within:0.##}->{afterWithin:0.##} off={before.OffsetY:0.##}->{after.OffsetY:0.##} content={after.ContentH:0.##} renders={renders}->{probe.RenderCount}");
        }

        // Same correction during a real smooth WHEEL chase (the non-programmatic arm of WheelAnimating). A later tick
        // must consume the rebased target rather than chasing the pre-collapse coordinate and dragging the view forward.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("explicit-correction-wheel", new Size2(360, 460), 1f));
            window.Show();
            var probe = new ExplicitMeasuredCorrectionProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            bool primed = PrimeExplicitCorrection(host, probe, out var vp, out _);

            window.QueueInput(WheelEvent(new Point2(150f, 150f), 0, 0,
                WheelNotch: 2f, Pointer: PointerKind.Mouse, TimestampMs: 1000));
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var before);
            float cross = before.ViewportW;
            int anchor = probe.Layout.IndexAt(before.OffsetY, cross);
            float within = before.OffsetY - probe.Layout.OffsetOf(anchor, cross);
            int renders = probe.RenderCount;

            var handleX = host.TryGetScrollHandle(vp)!;
            float beforeNow = (float)handleX.OffsetNow;   // the SAME clock the post-correction read below uses
            anchor = probe.Layout.IndexAt(beforeNow, cross);
            within = beforeNow - probe.Layout.OffsetOf(anchor, cross);
            bool corrected = probe.Controller.CorrectMeasuredExtent(probe.Layout,
                ExplicitMeasuredCorrectionProbe.CorrectedIndex, ExplicitMeasuredCorrectionProbe.RowH);
            // CorrectMeasuredExtent shifts the live plan in the SAME frame (Virtualizer.ApplyMeasured → ScrollHandle.ShiftFrame):
            // read the shifted offset off the handle at this very instant (no frame has run, so no chase advanced), then
            // run one frame for the layout-published ContentH.
            float shiftedNow = (float)handleX.OffsetNow;
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var after);
            int afterAnchor = probe.Layout.IndexAt(shiftedNow, cross);
            float afterWithin = shiftedNow - probe.Layout.OffsetOf(afterAnchor, cross);
            float d = -ExplicitMeasuredCorrectionProbe.ExtentDelta;
            bool active = before.Motion.Kind == FluentGpu.Scroll.Motion.MotionKind.Wheel;
            bool immediate = corrected && afterAnchor == anchor && Near(afterWithin, within, 0.01f)
                && Near(shiftedNow, beforeNow + d, 0.5f)
                && Near(after.ContentH, ExplicitMeasuredCorrectionProbe.N * ExplicitMeasuredCorrectionProbe.RowH, 0.01f)
                && probe.RenderCount == renders;

            bool settled = false;
            for (int i = 0; i < 300; i++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                if (s.Motion.IsMoving == false) { settled = true; break; }
            }
            host.Scene.TryGetScroll(vp, out var fin);
            float maxOff = MathF.Max(0f, fin.ContentH - fin.ViewportH);
            bool landed = settled && fin.OffsetY >= shiftedNow - 0.5f && fin.OffsetY <= maxOff + 0.5f && probe.RenderCount == renders;
            Check("gate.scroll.explicit-measured-correction-wheel collapsing an UNREALIZED measured row above the viewport during a wheel chase preserves row+within-row, rebases Offset/ContentH by the exact extent delta immediately, and the live wheel chase continues (never reverses) to a clamped rest without a component re-render",
                primed && active && immediate && landed,
                $"primed={primed} active={active} immediate={immediate} landed={landed} anchor={anchor}->{afterAnchor} within={within:0.##}->{afterWithin:0.##} off={before.OffsetY:0.##}->{after.OffsetY:0.##} final={fin.OffsetY:0.##}/max={maxOff:0.##} renders={renders}->{probe.RenderCount}");
        }

        // Direct WM_POINTER continuation is the subtle producer-side case: PendingRawOffset can be shifted correctly at
        // collapse time yet the NEXT move can overwrite it from the dispatcher's original press anchor. The per-node
        // TouchPanAnchorOffset must therefore move with the correction too; another 16-DIP upward move advances exactly
        // 16 DIP from the corrected offset, never jumping 200 DIP back into the old coordinate space.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("explicit-correction-direct-touch", new Size2(360, 460), 1f));
            window.Show();
            var probe = new ExplicitMeasuredCorrectionProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            bool primed = PrimeExplicitCorrection(host, probe, out var vp, out _);

            RectF vr = host.Scene.AbsoluteRect(vp);
            float x = vr.X + vr.W * 0.5f;
            float y0 = vr.Y + MathF.Min(220f, vr.H - 20f);
            uint t = s_touchClockMs;
            const uint PointerId = 91;
            var ev = new InputEvent[1];
            ev[0] = Touch(InputKind.PointerDown, new Point2(x, y0), t, PointerId);
            host.Input.Dispatch(ev); host.RunFrame();
            t += 16;
            // 16 DIP/16ms — the SAME rate as the "continued" move below (was 20, a different rate): ResampleContact's
            // ≥3-sample branch is a least-squares fit through the trailing history, which is exact only for evenly
            // spaced, CONSTANT-velocity samples (a straight line's own regression). A rate change between the two
            // segments is real smoothing input, not noise, and the resampled continuation legitimately deviates from
            // a naive "add the last raw delta" prediction by several px — matching the finger's OWN two different
            // speeds is what makes the post-correction continuation gate a clean 1:1 check again.
            ev[0] = Touch(InputKind.PointerMove, new Point2(x, y0 - 16f), t, PointerId);
            host.Input.Dispatch(ev); host.RunFrame();
            host.Scene.TryGetScroll(vp, out var before);
            float cross = before.ViewportW;
            int anchor = probe.Layout.IndexAt(before.OffsetY, cross);
            float within = before.OffsetY - probe.Layout.OffsetOf(anchor, cross);
            int renders = probe.RenderCount;

            var handleX = host.TryGetScrollHandle(vp)!;
            float beforeNow = (float)handleX.OffsetNow;   // the SAME clock the post-correction read below uses
            anchor = probe.Layout.IndexAt(beforeNow, cross);
            within = beforeNow - probe.Layout.OffsetOf(anchor, cross);
            bool corrected = probe.Controller.CorrectMeasuredExtent(probe.Layout,
                ExplicitMeasuredCorrectionProbe.CorrectedIndex, ExplicitMeasuredCorrectionProbe.RowH);
            // CorrectMeasuredExtent shifts the live plan in the SAME frame (Virtualizer.ApplyMeasured → ScrollHandle.ShiftFrame):
            // read the shifted offset off the handle at this very instant (no frame has run, so no chase advanced), then
            // run one frame for the layout-published ContentH.
            float shiftedNow = (float)handleX.OffsetNow;
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var after);
            int afterAnchor = probe.Layout.IndexAt(shiftedNow, cross);
            float afterWithin = shiftedNow - probe.Layout.OffsetOf(afterAnchor, cross);
            float d = -ExplicitMeasuredCorrectionProbe.ExtentDelta;
            bool active = before.Motion.Kind == FluentGpu.Scroll.Motion.MotionKind.Drag && before.UserScrollActive;
            bool immediate = corrected && afterAnchor == anchor && Near(afterWithin, within, 0.01f)
                && Near(shiftedNow, beforeNow + d, 0.5f)
                && Near(after.ContentH, ExplicitMeasuredCorrectionProbe.N * ExplicitMeasuredCorrectionProbe.RowH, 0.01f)
                && probe.RenderCount == renders;

            t += 16;
            ev[0] = Touch(InputKind.PointerMove, new Point2(x, y0 - 32f), t, PointerId);   // another 16 DIP at the SAME rate
            host.Input.Dispatch(ev); host.RunFrame();
            host.Scene.TryGetScroll(vp, out var continued);
            bool continuedOneToOne = Near(continued.OffsetY, shiftedNow + 16f, 0.75f)
                && continued.Motion.Kind == FluentGpu.Scroll.Motion.MotionKind.Drag
                && probe.RenderCount == renders;
            ev[0] = Touch(InputKind.PointerUp, new Point2(x, y0 - 32f), t + 16, PointerId);
            host.Input.Dispatch(ev);
            s_touchClockMs = t + 1000;

            Check("gate.scroll.explicit-measured-correction-direct-touch collapsing an UNREALIZED measured row above a live direct-touch pan preserves row+within-row, rebases Offset/ContentH by the exact extent delta immediately, and the next pointer move continues 1:1 in corrected coordinates without a component re-render",
                primed && active && immediate && continuedOneToOne,
                $"primed={primed} active={active} immediate={immediate} continued={continuedOneToOne} anchor={anchor}->{afterAnchor} within={within:0.##}->{afterWithin:0.##} off={before.OffsetY:0.##}->{after.OffsetY:0.##}->{continued.OffsetY:0.##} renders={renders}->{probe.RenderCount}");
        }

        // gate.scroll.anchor-repin-under-gesture (the homepage touchpad-jitter repro): jump DEEP into a MEASURED virtual
        // list (rows above the jump target stay UNREALIZED at their 40px estimate), then drag UPWARD — rows entering from
        // above realize at 64px, and every correction to a row strictly ABOVE the anchor is written through the ONE extent
        // path (Virtualizer.ApplyMeasured → PlanSlots.Shift) with a +24 delta WHILE the touchpad contact is live. (A
        // downward drag from the top is vacuous: corrections land at or below the anchor and never move its offset.) The
        // shift rebases the live Follow plan's coordinate frame (ring samples included) in the same call, so the
        // finger-driven offset moves WITH the correction instead of being overwritten/fought on the next sample.
        // Decomposition: the finger leg is reconstructed INDEPENDENTLY of the offset — the integrated 1:1 delta stream plus
        // the contact ring's constant present-time prediction lead (the scripted packets are device-timed, so the ring
        // shows them through Android's bounded resampling: min(ResampleMaxPredictionS, gap/2) of travel at the finger's
        // speed; the headless present is one refresh after each sample). The remaining term Σshift = off − latch − finger − lead
        // must then be a genuine accumulation of extent-correction shifts:
        //   • FIRED (maxShift ≥ ~one 24-DIP correction) — the scenario can't go vacuous;
        //   • NON-NEGATIVE (over-measure rows only grow content above the anchor; a dropped shift makes it lag);
        //   • MONOTONE non-decreasing (a fought shift oscillates — the felt jitter);
        //   • BOUNDED by the total possible above-correction (rowsAbove·overMeasure).
        // Σshift is NOT compared to an exact predicted value: which above-viewport rows correct on which frame is the
        // realize schedule's business. Tolerance 0.5 DIP.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("anchor-repin-gesture", new Size2(360, 460), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new AnchorRepinProbe());
            host.RunFrame();
            var vp = host.Scene.Root;
            // Jump deep without realizing the rows above: rows < ~400 keep their 40px estimate, so the upward drag
            // below realizes them mid-gesture and fires genuine corrections above the anchor.
            const float seed = 16000f;
            host.TryGetScrollHandle(vp)?.ScrollTo(seed, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
            for (int i = 0; i < 12; i++) host.RunFrame();   // let the realize window + local corrections settle at the seed
            var prod = new HeadlessScrollProducer(window, host, new Point2(150, 150)) { Device = DeviceClassIgnored };
            const float vel = 0.9f;   // DIP/ms upward — ~860 DIP over the run, ~20 estimate-priced rows entering from above
            const float overMeasure = AnchorRepinProbe.Real - AnchorRepinProbe.Estimate;   // 24 DIP per row
            host.Scene.TryGetScroll(vp, out var sL); float latchOff = sL.OffsetY;   // settle may itself have re-pinned; latch from live
            double t0 = prod.FrameMs;
            prod.ContactBegin(0f); prod.Frame(16f);
            bool shiftNonNeg = true, shiftMonotone = true, shiftBounded = true;
            float prevShift = 0f, maxShift = 0f; bool havePrev = false; int frames = 0;
            // A touchpad producer's deltas are FRAME-ALIGNED and applied 1:1 onto the contact ring, so the independent
            // finger model is the integrated delta stream plus the ring's constant (bounded) prediction lead.
            float lead = (float)(-vel * 1000.0 * Math.Min(FluentGpu.Scroll.Motion.ContactRing.ResampleMaxPredictionS, 0.5 * 0.016));
            for (int kf = 0; kf < 60; kf++)
            {
                prod.Ms = (uint)prod.FrameMs;                  // deliver one packet AT the present time (interpolation regime)
                prod.ContactUpdate(-vel * 16f);                // UPWARD — toward the unrealized estimate-priced region
                double present = prod.FrameMs;
                prod.Frame(16f);
                host.Scene.TryGetScroll(vp, out var s);
                if (kf < 3 || s.Motion.Kind != FluentGpu.Scroll.Motion.MotionKind.Drag) continue;
                if (!(s.OffsetY > 1f)) continue;                                                 // well clear of the top clamp
                float finger = (float)(-vel * (present - t0));                      // INDEPENDENT finger position (1:1 frame deltas)
                float shift = s.OffsetY - latchOff - finger - lead;                              // observed Σ(correction shifts)
                if (havePrev && shift - prevShift < -0.5f) shiftMonotone = false;    // a fought shift oscillates (the felt jitter)
                if (shift < -0.5f) shiftNonNeg = false;
                if (shift > s.AnchorIndex * overMeasure + 1f) shiftBounded = false;  // ≤ total possible above-correction
                maxShift = MathF.Max(maxShift, shift);
                prevShift = shift; havePrev = true; frames++;
            }
            bool firedRepin = maxShift >= overMeasure - 4f;   // ≥ ~one genuine 24-DIP re-pin — the scenario can't go vacuous
            Check("gate.scroll.anchor-repin-under-gesture an upward monotone touchpad gesture from a deep seed realizes estimate-priced rows above the anchor mid-gesture, and the offset stays = latch + finger + prediction lead + a FIRED/non-negative/monotone/bounded Σ(extent-correction shifts) — each correction shifts the live contact plan in the same call, never dropped or fought",
                firedRepin && shiftNonNeg && shiftMonotone && shiftBounded && frames >= 20,
                $"frames={frames} maxRepinShift={maxShift:0.00} firedRepin={firedRepin} shiftNonNeg={shiftNonNeg} shiftMonotone={shiftMonotone} shiftBounded={shiftBounded}");
        }

        // (A destination past a shrinking/growing extent: ScrollHandle.ScrollTo lands at today's max and keeps the raw
        // target latched until the extent can hold it — ScrollRuntimeTests.RestoreLatch_* and gate e11virt.2's end clamp.)
    }


    static void E11VirtChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // e11virt.1/2 — the IMeasuredVirtualLayout seam (E11-L0): estimate-then-correct + scroll anchoring.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-measured", new Size2(640, 480), 1f));
            window.Show();
            var probe = new MeasuredSeamProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();

            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            var content = sc0.ContentNode;
            var layout = probe.Layout!;
            const float cross = 300f;

            // Realized rows correct from the 40px estimate to their measured extents at arrange (SetMeasured);
            // positions are the corrected prefix sums (OffsetOf) — virtualization.md §6.2 through the USER seam.
            var r1 = Child(host.Scene, content, 1);
            var r2 = Child(host.Scene, content, 2);
            var r3 = Child(host.Scene, content, 3);
            float h01 = MeasuredSeamProbe.H(0) + MeasuredSeamProbe.H(1);
            bool corrected = Near(host.Scene.Bounds(r1).Y, MeasuredSeamProbe.H(0))
                && Near(host.Scene.Bounds(r2).Y, h01)
                && Near(host.Scene.Bounds(r3).Y, h01 + MeasuredSeamProbe.H(2))
                && Near(layout.ItemRect(1, cross).H, MeasuredSeamProbe.H(1))
                && Near(layout.OffsetOf(3, cross), h01 + MeasuredSeamProbe.H(2));

            // Unrealized rows still report the estimate; published ContentSize = corrected window + estimate tail.
            float expected = 0f;
            for (int i = 0; i < sc0.LastRealized; i++) expected += MeasuredSeamProbe.H(i);
            expected += (MeasuredSeamProbe.N - sc0.LastRealized) * MeasuredSeamProbe.Estimate;
            bool estimated = Near(layout.ItemRect(MeasuredSeamProbe.N - 1, cross).H, MeasuredSeamProbe.Estimate)
                && Near(sc0.ContentH, expected, 1f);
            Check("e11virt.1 measured seam: realized rows correct to measured extents (positions = corrected prefix sums); unrealized keep the estimate",
                corrected && estimated, $"y1..3={host.Scene.Bounds(r1).Y:0},{host.Scene.Bounds(r2).Y:0},{host.Scene.Bounds(r3).Y:0} content={sc0.ContentH:0} expected={expected:0}");

            // Anchoring: scroll into the middle — across the realize+correction waves the anchor is the first FULLY
            // visible item (corrections above it shift the frame, so the rows being read never jump).
            var ptr = new Point2(150, 150);
            for (int s = 0; s < 8; s++) { window.QueueInput(WheelEvent(ptr, 0, 0, 400f)); host.RunFrame(); }
            host.Scene.TryGetScroll(vp, out var sc1);
            int anchor = sc1.AnchorIndex;
            float band0 = layout.OffsetOf(anchor, cross), band1 = anchor > 0 ? layout.OffsetOf(anchor - 1, cross) : float.NegativeInfinity;
            bool anchored = sc1.FirstRealized > 0 && band0 >= sc1.OffsetY - 0.5f && band1 < sc1.OffsetY + 0.5f;

            // Fling to the end: each fling clamps against the content published SO FAR; the realize wave then corrects
            // the freshly measured rows and EXTENDS the content (estimate-then-correct), so the true end takes a
            // couple of flings — after which the offset clamps to the fully corrected extent and realize reaches row N.
            host.TryGetScrollHandle(vp)!.ScrollTo(1_000_000f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            for (int s = 0; s < 60; s++) host.RunFrame();   // the latched end target chases the extent as each realize wave corrects it
            host.Scene.TryGetScroll(vp, out var sc2);
            bool clamped = sc2.LastRealized == MeasuredSeamProbe.N && Near(sc2.OffsetY, sc2.ContentH - sc2.ViewportH, 2f);
            Check("e11virt.2 measured seam anchoring: mid-list the anchor is the first fully visible item; end-fling clamps to corrected content",
                anchored && clamped, $"anchor={anchor} off={sc1.OffsetY:0} anchorStart={band0:0} prevStart={band1:0} end={sc2.OffsetY:0}/{sc2.ContentH - sc2.ViewportH:0}");
        }

        // e11virt.2b — THE COUNT-CHANGE CONTRACT. A measured layout whose rows are NOT all the same height must not
        // throw away what it already measured when the item count moves: re-seeding every row to one estimate makes the
        // content extent jump by the whole (measured − estimate) sum at once, so the scroll anchor re-pins against a
        // stale offset and every row above AND below the edit visibly shuffles while it re-measures. That is the sidebar
        // folder expand/collapse flicker, and it is a LAYOUT bug, not an app bug — one toggle discarded a 16/24/28/32/40/
        // 44/48/56/72/88-DIP ladder. The seam answers it in two shapes: a bare count change RESIZES (survivors keep
        // their extents, the appended tail seeds), and a disclosure — which knows its band — SPLICES, so the TAIL below
        // the edit survives at its shifted indices too. The analytic seed removes the guess entirely for a host whose
        // heights are a pure function of its model.
        {
            const float cross = 300f;
            float[] ladder = [16f, 24f, 28f, 32f, 40f, 44f, 48f, 56f, 72f, 88f, 36f, 20f];
            const float Est = 44f;
            const int At = 5, Ins = 3;

            static MeasuredStackVirtualLayout Mixed(float[] ladder, float cross)
            {
                var l = new MeasuredStackVirtualLayout(Est);
                _ = l.ContentExtent(ladder.Length, cross);
                for (int i = 0; i < ladder.Length; i++) l.SetMeasured(i, ladder[i], cross);
                return l;
            }

            var resized = Mixed(ladder, cross);
            float measuredTotal = resized.ContentExtent(ladder.Length, cross);
            var before = new RectF[ladder.Length];
            for (int i = 0; i < ladder.Length; i++) before[i] = resized.ItemRect(i, cross);

            // (a) a bare count change (three rows appended): every surviving row keeps its measured extent, and the
            //     published content grows by exactly the three estimates — not by (n × estimate − measuredTotal).
            _ = resized.ContentExtent(ladder.Length + Ins, cross);
            bool kept = true;
            for (int i = 0; i < ladder.Length; i++)
            {
                var r = resized.ItemRect(i, cross);
                kept &= Near(r.H, before[i].H) && Near(r.Y, before[i].Y);
            }
            bool grewByEstimate = Near(resized.ContentExtent(ladder.Length + Ins, cross), measuredTotal + Ins * Est, 0.05f);

            // (b) the DISCLOSURE shape — three rows inserted in the MIDDLE. Nothing above the edit moves at all; the
            //     inserted band seeds; the whole tail keeps its extent and slides down by exactly the band.
            var spliced = Mixed(ladder, cross);
            spliced.Splice(At, 0, Ins);
            _ = spliced.ContentExtent(ladder.Length + Ins, cross);
            bool above = true, band = true, tail = true;
            for (int i = 0; i < At; i++)
            {
                var r = spliced.ItemRect(i, cross);
                above &= Near(r.H, before[i].H) && Near(r.Y, before[i].Y);
            }
            for (int i = At; i < At + Ins; i++) band &= Near(spliced.ItemRect(i, cross).H, Est);
            for (int i = At; i < ladder.Length; i++)
            {
                var r = spliced.ItemRect(i + Ins, cross);
                tail &= Near(r.H, before[i].H) && Near(r.Y, before[i].Y + Ins * Est);
            }
            // …and the collapse mirror: splicing the band back out restores the original geometry exactly.
            spliced.Splice(At, Ins, 0);
            _ = spliced.ContentExtent(ladder.Length, cross);
            bool restored = Near(spliced.ContentExtent(ladder.Length, cross), measuredTotal, 0.05f);
            for (int i = 0; i < ladder.Length; i++)
            {
                var r = spliced.ItemRect(i, cross);
                restored &= Near(r.H, before[i].H) && Near(r.Y, before[i].Y);
            }

            // (c) the ANALYTIC seed (RepeatLayout.Extents): an unmeasured row starts at its REAL height, so an insert
            //     needs no measure pass at all to land the rows below it correctly.
            var analytic = new MeasuredStackVirtualLayout(Est, false, i => ladder[i % ladder.Length]);
            _ = analytic.ContentExtent(ladder.Length, cross);
            bool seeded = Near(analytic.ContentExtent(ladder.Length, cross), measuredTotal, 0.05f);
            for (int i = 0; i < ladder.Length; i++) seeded &= Near(analytic.ItemRect(i, cross).H, ladder[i]);
            analytic.Splice(4, 0, 2);
            _ = analytic.ContentExtent(ladder.Length + 2, cross);
            bool seededBand = Near(analytic.ItemRect(4, cross).H, ladder[4])
                && Near(analytic.ItemRect(5, cross).H, ladder[5])
                && Near(analytic.ItemRect(6, cross).H, ladder[4]);

            // (d) the alloc tripwire: once the backing arrays are warm, a splice + re-query round trip allocates NOTHING.
            var hot = Mixed(ladder, cross);
            _ = hot.ContentExtent(ladder.Length + Ins, cross);
            hot.Splice(At, 0, Ins); _ = hot.ContentExtent(ladder.Length + Ins, cross);
            hot.Splice(At, Ins, 0); _ = hot.ContentExtent(ladder.Length, cross);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            float sink = 0f;
            for (int k = 0; k < 16; k++)
            {
                hot.Splice(At, 0, Ins);
                sink += hot.ContentExtent(ladder.Length + Ins, cross);
                sink += hot.ItemRect(ladder.Length, cross).Y;
                sink += hot.OffsetOf(9, cross) + hot.IndexAt(320f, cross);
                hot.Splice(At, Ins, 0);
                sink += hot.ContentExtent(ladder.Length, cross);
            }
            long spliceBytes = GC.GetAllocatedBytesForCurrentThread() - a0;

            Check("e11virt.2b measured count change PRESERVES measured extents: a bare resize keeps every survivor (content grows by the estimates, not by a whole re-seed), a mid-list splice moves the tail by exactly the inserted band and moves nothing above it, the collapse mirror restores the original geometry, an analytic seed needs no measure pass, and a warm splice round trip is 0-alloc",
                kept && grewByEstimate && above && band && tail && restored && seeded && seededBand
                && spliceBytes == 0 && sink != 0f,
                $"kept={kept} grew={grewByEstimate} above={above} band={band} tail={tail} restored={restored} "
                + $"seeded={seeded} seededBand={seededBand} spliceBytes={spliceBytes} "
                + $"measuredTotal={measuredTotal:0.##} resized={resized.ContentExtent(ladder.Length + Ins, cross):0.##}");
        }

        // e11virt.3 — LinedFlowLayout (the WinUI ItemsView photo-wall): uniform-height lines, width = aspect × lineHeight
        // clamped to the cross size, wrap when the next item + MinItemSpacing would overflow, O(1) line-stride windowing.
        // Defaults LineSpacing 0 / MinItemSpacing 0 (LinedFlowLayout.h s_defaultLineSpacing/s_defaultMinItemSpacing).
        {
            float[] aspects = [2f, 1f, 0.5f, 4f];
            var lf = new LinedFlowLayout(lineHeight: 100f, aspectRatio: i => aspects[i % 4], lineSpacing: 10f, minItemSpacing: 5f);
            const float cross = 350f;
            // Flow on cross 350: line0=[0:w200@0, 1:w100@205] line1=[2:w50@0] line2=[3:w350@0 (clamped)]
            //                    line3=[4:w200@0, 5:w100@205] line4=[6:w50@0] line5=[7:w350@0] → 6 lines.
            float extent = lf.ContentExtent(8, cross);
            var i0 = lf.ItemRect(0, cross); var i1 = lf.ItemRect(1, cross); var i2 = lf.ItemRect(2, cross);
            var i3 = lf.ItemRect(3, cross); var i5 = lf.ItemRect(5, cross); var i7 = lf.ItemRect(7, cross);
            bool widths = Near(i0.W, 200f) && Near(i1.W, 100f) && Near(i2.W, 50f) && Near(i3.W, 350f) && Near(i0.H, 100f);
            bool flow = Near(i0.X, 0f) && Near(i0.Y, 0f)
                && Near(i1.X, 205f) && Near(i1.Y, 0f)            // 200 + MinItemSpacing 5
                && Near(i2.X, 0f) && Near(i2.Y, 110f)            // wrapped (305+5+50 > 350); line stride = 100+10
                && Near(i3.X, 0f) && Near(i3.Y, 220f)            // over-wide item → its own full-width line
                && Near(i5.X, 205f) && Near(i5.Y, 330f)
                && Near(i7.Y, 550f) && Near(extent, 650f);       // 6×100 + 5×10
            lf.Window(8, cross, 200f, 115f, 0, out int f0, out int l0);   // band [115,315] → lines 1..3 → items [2,6)
            lf.Window(8, cross, 200f, 115f, 2, out int f1, out int l1);   // ±2 items of overscan, clamped
            lf.Window(8, cross, 200f, 0f, 0, out int f2, out int l2);     // top: lines 0..2 → items [0,4)
            bool windows = f0 == 2 && l0 == 6 && f1 == 0 && l1 == 8 && f2 == 0 && l2 == 4;
            // WinUI defaults: no spacing — 3 unit-aspect items pack one 100px line.
            var lfDef = new LinedFlowLayout(100f);
            bool defaults = Near(lfDef.ContentExtent(3, cross), 100f) && Near(lfDef.ItemRect(2, cross).X, 200f);
            Check("e11virt.3 LinedFlow: aspect widths (cross-clamped), spacing-aware wrap, line-stride rects + windowing, 0-spacing defaults",
                widths && flow && windows && defaults, $"extent={extent:0} w0..3={i0.W:0},{i1.W:0},{i2.W:0},{i3.W:0} win=({f0},{l0})/({f1},{l1})/({f2},{l2})");
        }

        // e11virt.4 — GroupedListVirtualLayout (E11-L0 grouping): headers are a measured item KIND at their own flat
        // indices; StickyHeaderIndexAt = last header at-or-above the offset band (−1 above the first header), and the
        // pivot tracks estimate-then-correct band moves.
        {
            var gl = new GroupedListVirtualLayout([0, 6, 13], headerExtent: 32f, itemEstimate: 48f);
            const int n = 20; const float cross = 300f;
            float total = gl.ContentExtent(n, cross);            // 3×32 + 17×48 = 912
            bool seeded = Near(total, 912f) && gl.IsHeader(6) && !gl.IsHeader(7)
                && Near(gl.OffsetOf(6, cross), 272f)             // 32 + 5×48
                && Near(gl.ItemRect(6, cross).H, 32f) && Near(gl.ItemRect(7, cross).H, 48f);
            bool sticky = gl.StickyHeaderIndexAt(0f) == 0
                && gl.StickyHeaderIndexAt(100f) == 0
                && gl.StickyHeaderIndexAt(272f) == 6             // exactly at the group-2 band start
                && gl.StickyHeaderIndexAt(591f) == 6             // last row of group 2 (header 13 starts at 592)
                && gl.StickyHeaderIndexAt(593f) == 13;
            gl.SetMeasured(3, 80f, cross);                       // estimate-then-correct: row 3 48 → 80 (+32)
            bool correctedG = Near(gl.ContentExtent(n, cross), 944f)
                && Near(gl.OffsetOf(6, cross), 304f)
                && gl.StickyHeaderIndexAt(300f) == 0 && gl.StickyHeaderIndexAt(305f) == 6;
            var gl2 = new GroupedListVirtualLayout([4], 32f, 48f);
            _ = gl2.ContentExtent(10, cross);
            bool none = gl2.StickyHeaderIndexAt(0f) == -1 && gl2.StickyHeaderIndexAt(4 * 48f + 1f) == 4;
            Check("e11virt.4 GroupedList: header extents seeded, sticky index per offset band (−1 above first), correction moves the pivot",
                seeded && sticky && correctedG && none, $"total={total:0}→{gl.ContentExtent(n, cross):0} hdr6@{gl.OffsetOf(6, cross):0}");

            // W1.2c — MeasuredVersion: bumped only on a real delta (compare-before-set), and a "drawer" case — a
            // mid-group row growing into a much taller band (an expander) — shifts the FOLLOWING header's offset by
            // exactly that delta while StickyHeaderIndexAt/IndexAt/OffsetOf all stay correct over the enlarged band.
            int versionAfterFirstSet = gl.MeasuredVersion;
            gl.SetMeasured(3, 80f, cross);                       // same value again: compare-before-set must NOT bump
            bool noBumpOnRepeat = gl.MeasuredVersion == versionAfterFirstSet;
            float hdr13Before = gl.OffsetOf(13, cross);          // 624
            int sticky700Before = gl.StickyHeaderIndexAt(700f);  // still inside group 3's (un-enlarged) band
            gl.SetMeasured(8, 400f, cross);                      // drawer opens mid-group-2 (row 8: 48 → 400, +352)
            bool versionBumped = gl.MeasuredVersion == versionAfterFirstSet + 1;
            float hdr13After = gl.OffsetOf(13, cross);
            bool headerShifted = Near(hdr13After - hdr13Before, 352f);
            int sticky700After = gl.StickyHeaderIndexAt(700f);   // the enlarged group-2 band now covers it
            bool stickyCorrect = sticky700Before == 13 && sticky700After == 6;
            int atRow = gl.IndexAt(700f, cross);
            bool roundTrip = gl.OffsetOf(atRow, cross) <= 700f
                && (atRow + 1 >= n || gl.OffsetOf(atRow + 1, cross) > 700f);
            Check("e11virt.4b GroupedList drawer: MeasuredVersion bumps only on a real delta, the following header's offset shifts by exactly that delta, and StickyHeaderIndexAt/IndexAt/OffsetOf stay correct across the enlarged band (W1.2c)",
                noBumpOnRepeat && versionBumped && headerShifted && stickyCorrect && roundTrip,
                $"version={versionAfterFirstSet}→{gl.MeasuredVersion} hdr13 {hdr13Before:0}→{hdr13After:0} sticky700 {sticky700Before}→{sticky700After} atRow={atRow}");
        }

        // e11virt.pagedshelf-measured — the REAL realize-all PagedShelf branch. Its ScrollEl must remain the root
        // column's direct cross-stretch child: the old default-row clip-root wrapper gave this Grow=0 scroller a 0px
        // cross-axis width, so the strip retained its measured height but every card was record-culled. ScrollEl is now
        // the native hover clip-escape root, preserving both viewport geometry and the elevated-card paint contract.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pagedshelf-measured", new Size2(640, 480), 1f));
            window.Show();
            var probe = new PagedShelfMeasuredProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();

            var scene = host.Scene;
            var viewport = FindScrollable(scene, scene.Root);
            ScrollState sc0 = default;
            bool hasScroll = !viewport.IsNull && scene.TryGetScroll(viewport, out sc0);
            var vr0 = viewport.IsNull ? default : scene.AbsoluteRect(viewport);
            var dlRest = new DrawList();
            SceneRecorder.Record(scene, dlRest);
            var restRed = FindFillCommandNear(dlRest, PagedShelfMeasuredProbe.FirstFill);
            var restBlue = FindFillCommandNear(dlRest, PagedShelfMeasuredProbe.OtherFill);
            bool initialGeometry = hasScroll
                && Near(vr0.X, -12f) && Near(vr0.W, 344f) && Near(vr0.H, 68f)
                && Near(sc0.ViewportW, 344f) && Near(sc0.ContentW, 1114f)
                && probe.Pager.Page == 0 && probe.Pager.PageCount == 4;
            bool restPaints = restRed.Order >= 0 && restBlue.Order >= 0
                && restRed.Order < restBlue.Order && restRed.ClipDepth == restBlue.ClipDepth;
            Check("e11virt.pagedshelf-measured direct ScrollEl keeps the realize-all shelf viewport stretched (344×68), all-card extent measurable, and card paint visible",
                initialGeometry && restPaints,
                $"vp=({vr0.X:0},{vr0.Y:0},{vr0.W:0}×{vr0.H:0}) scroll={hasScroll} contentW={(hasScroll ? sc0.ContentW : -1):0} pages={probe.Pager.PageCount} draws=r{restRed.Order}/b{restBlue.Order}");

            // Hover through the real dispatcher. The cell should defer after its later sibling and, because the
            // ScrollEl itself owns HoverElevateClipRoot, record after the scroller's clip/fade scopes have closed.
            var firstCard = FindFillNode(scene, scene.Root, PagedShelfMeasuredProbe.FirstFill);
            if (!firstCard.IsNull)
            {
                var cardR = scene.AbsoluteRect(firstCard);
                window.QueueInput(new InputEvent(InputKind.PointerMove,
                    new Point2(cardR.X + cardR.W / 2f, cardR.Y + cardR.H / 2f), 0, 0));
                host.RunFrame();
            }
            var dlHover = new DrawList();
            SceneRecorder.Record(scene, dlHover);
            var hoverRed = FindFillCommandNear(dlHover, PagedShelfMeasuredProbe.FirstFill);
            var hoverBlue = FindFillCommandNear(dlHover, PagedShelfMeasuredProbe.OtherFill);
            bool hoverEscapes = !firstCard.IsNull
                && hoverRed.Order >= 0 && hoverBlue.Order >= 0
                && hoverRed.Order > hoverBlue.Order && hoverRed.ClipDepth < hoverBlue.ClipDepth;
            Check("e11virt.pagedshelf-measured ScrollEl-native clip root hoists the hovered measured cell outside its viewport clip/edge-fade scope",
                hoverEscapes,
                $"card={!firstCard.IsNull} hov=r{hoverRed.Order}@d{hoverRed.ClipDepth} b{hoverBlue.Order}@d{hoverBlue.ClipDepth}");

            // Refit the same mounted shelf, then exercise its public pager. Reduced motion makes the offset assertion
            // deterministic; this is the same navigation target path, only snapped instead of time-integrated.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(630f, 470f), 0, 0));
            host.RunFrame();
            probe.Width.Value = 540f;
            host.RunFrame();
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();
            var viewport2 = FindScrollable(scene, scene.Root);
            ScrollState sc1 = default;
            bool resizedScroll = !viewport2.IsNull && scene.TryGetScroll(viewport2, out sc1);
            var vr1 = viewport2.IsNull ? default : scene.AbsoluteRect(viewport2);
            bool resized = resizedScroll && Near(vr1.X, -12f) && Near(vr1.W, 564f)
                && Near(sc1.ViewportW, 564f) && Near(sc1.ContentW, 1114f)
                && probe.Pager.Page == 0 && probe.Pager.PageCount == 2;

            bool previousReduced = Motion.ReducedMotion;
            try
            {
                Motion.ReducedMotion = true;
                probe.Pager.Next();
                host.RunFrame();
                for (int i = 0; i < 4 && host.HasActiveWork; i++) host.RunFrame();
            }
            finally
            {
                Motion.ReducedMotion = previousReduced;
            }
            ScrollState sc2 = default;
            bool navigatedScroll = !viewport2.IsNull && scene.TryGetScroll(viewport2, out sc2);
            var dlPage1 = new DrawList();
            SceneRecorder.Record(scene, dlPage1);
            var pageBlue = FindFillCommandNear(dlPage1, PagedShelfMeasuredProbe.OtherFill);
            bool navigated = navigatedScroll && Near(sc2.OffsetX, 550f, 0.75f)
                && probe.Pager.Page == 1 && pageBlue.Order >= 0;
            Check("e11virt.pagedshelf-measured resize refits 3→5 columns without collapsing the viewport; Next snaps to the clamped measured-strip target and still paints cards",
                resized && navigated,
                $"vpW={vr1.W:0}/scrollW={(resizedScroll ? sc1.ViewportW : -1):0} pages={probe.Pager.PageCount} page={probe.Pager.Page} off={(navigatedScroll ? sc2.OffsetX : -1):0.0} draw={pageBlue.Order}");
        }

        // e11virt.fillrow — FillRowVirtualLayout (the viewport-aware "fill the width with equal cards" shelf via the
        // IViewportVirtualLayout seam): SetViewport feeds the main-axis viewport; the fit is COUNT-INDEPENDENT, so a
        // handful of items render at the normal fitted width (≤ maxCardW) instead of ballooning to fill — the regression
        // guard for the Home shelf card-balloon bug (3 items in a wide viewport must NOT become ~458px cards).
        {
            var fr = new FillRowVirtualLayout(minCardW: 150f, maxCardW: 200f, gap: 12f);
            const float cross = 240f;
            fr.SetViewport(1400f, cross);                        // perPage=floor(1412/162)=8, cardW=(1400-84)/8=164.5
            bool fit = fr.PerPage == 8 && Near(fr.CardW, 164.5f) && fr.CardW <= 200.01f;
            // Count-independent: 3 items → a SHORT strip at the fitted width (NOT 3 cards stretched across 1400px).
            float ext3 = fr.ContentExtent(3, cross);             // 3×164.5 + 2×12 = 517.5
            var c0 = fr.ItemRect(0, cross); var c1 = fr.ItemRect(1, cross);
            bool fewItems = Near(ext3, 517.5f) && Near(c0.W, 164.5f) && Near(c1.X, 176.5f) && Near(c0.H, cross);
            fr.SetViewport(324f, cross);                          // refit: perPage=floor(336/162)=2, cardW=(324-12)/2=156
            bool refit = fr.PerPage == 2 && Near(fr.CardW, 156f);
            // Override: pin 2 columns on a wide viewport → unclamped card would be 694, capped to maxCardW 200.
            var frOv = new FillRowVirtualLayout(150f, 200f, 12f, perPageOverride: 2);
            frOv.SetViewport(1400f, cross);
            bool capped = frOv.PerPage == 2 && Near(frOv.CardW, 200f);
            Check("e11virt.fillrow FillRow: viewport-fed fit (≤maxCardW), count-independent (no balloon), refit on resize, perPage-override cap",
                fit && fewItems && refit && capped, $"fit={fr.PerPage}@{fr.CardW:0.0} ext3={ext3:0.0} cap={frOv.CardW:0}");
        }

        // e11virt.fillrow-margin — the FlexLayout stretch behavior the halo-bleed viewport (Feature A / PagedShelf) relies
        // on: a cross-STRETCH child with a NEGATIVE horizontal margin and no explicit width arranges WIDER than its parent
        // content box by |lead|+|trail| and starts |lead| to the LEFT of it (FlexLayout.cs:373 Max(0, availCross−crossMargin)
        // + the arrange leading-margin). This is how the shelf viewport widens 2×HaloBleed into the gutters without moving
        // the shelf's layout box. Column parent 300 wide; child Margin(-12,0,-12,0) ⇒ width 324 at x −12.
        {
            var mt = LayoutTree(strings, new BoxEl
            {
                Direction = 1, Width = 300f, Height = 80f,
                Children = [ new BoxEl { Height = 40f, Margin = new Edges4(-12f, 0f, -12f, 0f) } ],
            });
            var childR = mt.AbsoluteRect(Child(mt, mt.Root, 0));
            Check("e11virt.fillrow-margin negative horizontal margin widens a cross-stretch child by 2×|m| at x −|m| (halo-bleed viewport)",
                Near(childR.W, 324f) && Near(childR.X, -12f), $"w={childR.W:0.0} x={childR.X:0.0}");
        }

        // e11virt.fillrow-insets — the MAIN-AXIS content insets Feature A adds to FillRowVirtualLayout: the fed viewport is
        // widened by Lead+Trail, the fit uses the INNER width (viewport − insets) so cardW is UNCHANGED, item i shifts by
        // LeadInset, and ContentExtent carries both gutters. Insets default 0 ⇒ existing gates keep their geometry.
        {
            const float cross = 240f, lead = 12f, trail = 12f;
            var frBase = new FillRowVirtualLayout(150f, 200f, 12f);
            frBase.SetViewport(1400f, cross);                                 // no insets: perPage 8, cardW 164.5
            var frIns = new FillRowVirtualLayout(150f, 200f, 12f, leadInset: lead, trailInset: trail);
            frIns.SetViewport(1400f + lead + trail, cross);                   // widened viewport; inner = 1400 ⇒ SAME fit
            bool sameFit = frIns.PerPage == frBase.PerPage && Near(frIns.CardW, frBase.CardW);
            var r0 = frIns.ItemRect(0, cross); var r1 = frIns.ItemRect(1, cross);
            bool shifted = Near(r0.X, lead) && Near(r1.X, lead + frIns.CardW + 12f);
            bool extent = Near(frIns.ContentExtent(5, cross), frBase.ContentExtent(5, cross) + lead + trail);
            frIns.Window(64, cross, 1400f + lead + trail, 0f, 0, out int f0, out int l0);   // offset 0 still realizes item 0
            Check("e11virt.fillrow-insets Lead/Trail: item0 at LeadInset, extent +Lead+Trail, fit uses inner width (cardW unchanged), window realizes from 0",
                sameFit && shifted && extent && f0 == 0 && l0 >= 8,
                $"cardW={frIns.CardW:0.0}/{frBase.CardW:0.0} r0={r0.X:0.0} ext+={frIns.ContentExtent(5, cross) - frBase.ContentExtent(5, cross):0.0} win=[{f0},{l0})");
        }

        // e11virt.elevate — Element.HoverElevatePaint (Feature B): a flagged child on the hover path is DEFERRED to paint
        // AFTER its later siblings (the design's z-index:2, so a hovered card's lift halo is not overpainted). Two
        // overlapping ZStack children; the EARLIER (red, flagged) one is hovered → it records AFTER the later (blue)
        // sibling. At rest it keeps document order. The deferred-path record allocates 0 managed bytes.
        {
            var fonts2 = new HeadlessFontSystem(strings);
            ColorF red = ColorF.FromRgba(220, 40, 40), blue = ColorF.FromRgba(40, 90, 220);
            Element Tree() => new BoxEl
            {
                Width = 200f, Height = 100f, ZStack = true,
                Children =
                [
                    // HoverFill = Fill (identity): without it ResolveSurface auto-lightens a hovered interactive fill
                    // 8%, and the exact-color probe below would miss the drawn command.
                    new BoxEl { Key = "elev", Width = 100f, Height = 100f, Fill = red, HoverFill = red, HoverElevatePaint = true, OnClick = static () => { } },
                    new BoxEl { Key = "sib",  Width = 100f, Height = 100f, Fill = blue },
                ],
            };
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(Tree(), null);
            new FlexLayout(scene, fonts2).Run(scene.Root);
            var elev = Child(scene, scene.Root, 0);

            var dlRest = new DrawList();
            SceneRecorder.Record(scene, dlRest);
            var restRed = FindFillCommand(dlRest, red); var restBlue = FindFillCommand(dlRest, blue);
            bool restOrder = restRed.Order >= 0 && restBlue.Order >= 0 && restRed.Order < restBlue.Order;   // document order at rest

            scene.SetFlagBits(elev, NodeFlags.Hovered);   // pointer on the flagged (earlier) child
            var dlHov = new DrawList();
            SceneRecorder.Record(scene, dlHov);
            var hovRed = FindFillCommand(dlHov, red); var hovBlue = FindFillCommand(dlHov, blue);
            bool elevated = hovRed.Order >= 0 && hovBlue.Order >= 0 && hovRed.Order > hovBlue.Order;   // deferred above sibling

            SceneRecorder.Record(scene, dlHov);   // warm DrawList buffers
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            SceneRecorder.Record(scene, dlHov);
            long recBytes = GC.GetAllocatedBytesForCurrentThread() - a0;
            Check("e11virt.elevate HoverElevatePaint defers a hovered flagged child above its later siblings (z-index:2); document order at rest; 0-alloc deferred record",
                restOrder && elevated && recBytes == 0,
                $"rest r{restRed.Order}<b{restBlue.Order} hov r{hovRed.Order}>b{hovBlue.Order} bytes={recBytes}");
        }

        // e11virt.elevate-cell — the NESTED (shelf) shape end-to-end through the REAL input dispatcher: the flagged
        // node is a NON-interactive cell whose interactive CARD wins the hit (PagedShelf's containerFactory shape).
        // HoverElevatePaintBit joins UpdateHoverWithin's ancestor mask, so hovering the card stamps HoverWithin on the
        // flagged cell — and the recorder then defers the whole cell above its sibling cells.
        {
            ColorF red = ColorF.FromRgba(220, 40, 40), blue = ColorF.FromRgba(40, 90, 220);
            using var appE = new HeadlessPlatformApp();
            var windowE = new HeadlessWindow(new WindowDesc("elevate-cell", new Size2(640, 480), 1f));
            windowE.Show();
            using var hostE = new AppHost(appE, windowE, new HeadlessGpuDevice(), fonts, strings, new ElevateCellProbe());
            hostE.RunFrame();
            var sE = hostE.Scene;
            // Descend to the two-cell row (the probe's root box), then its cells and cell 0's card.
            var row = sE.Root;
            while (!row.IsNull && (sE.FirstChild(row).IsNull || sE.NextSibling(sE.FirstChild(row)).IsNull))
                row = sE.FirstChild(row);
            var cell0 = sE.FirstChild(row); var cell1 = sE.NextSibling(cell0);
            var card0 = sE.FirstChild(cell0);
            var r0 = sE.AbsoluteRect(card0);
            windowE.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(r0.X + r0.W / 2f, r0.Y + r0.H / 2f), 0, 0));
            hostE.RunFrame();
            bool cellWithin = (sE.Flags(cell0) & NodeFlags.HoverWithin) != 0;   // the dispatcher-mask contract
            var dlCell = new DrawList();
            SceneRecorder.Record(sE, dlCell);
            // Near-match: the hovered card's fill went through the real dispatcher → InteractionAnim → LerpLinear,
            // which is not bit-exact even at HoverFill==Fill identity.
            var cRed = FindFillCommandNear(dlCell, red); var cBlue = FindFillCommandNear(dlCell, blue);
            bool cellElevated = cRed.Order >= 0 && cBlue.Order >= 0 && cRed.Order > cBlue.Order;
            Check("e11virt.elevate-cell a flagged NON-interactive cell gets HoverWithin from its hovered card (dispatcher mask) and defers above sibling cells",
                cellWithin && cellElevated, $"within={cellWithin} r{cRed.Order}>b{cBlue.Order}");
        }

        // e11virt.elevate-escape — clip-ESCAPE (Element.HoverElevateClipRoot): with a flagged clipping viewport around
        // the cell row, the hovered cell is HOISTED out of the viewport's whole scope — recorded AFTER its PopClip,
        // against the OUTER clip (lower clip depth) — so the lift + halo paint outside the strip, while the resting
        // sibling stays inside the viewport clip. At rest both record inside (document order, same depth).
        {
            ColorF red = ColorF.FromRgba(220, 40, 40), blue = ColorF.FromRgba(40, 90, 220);
            using var appV = new HeadlessPlatformApp();
            var windowV = new HeadlessWindow(new WindowDesc("elevate-escape", new Size2(640, 480), 1f));
            windowV.Show();
            using var hostV = new AppHost(appV, windowV, new HeadlessGpuDevice(), fonts, strings, new ElevateEscapeProbe());
            hostV.RunFrame();
            var sV = hostV.Scene;
            var dlRest2 = new DrawList();
            SceneRecorder.Record(sV, dlRest2);
            var rRed = FindFillCommandNear(dlRest2, red); var rBlue = FindFillCommandNear(dlRest2, blue);
            bool restInside = rRed.Order >= 0 && rBlue.Order >= 0 && rRed.Order < rBlue.Order && rRed.ClipDepth == rBlue.ClipDepth;

            // Hover card 0 through the real dispatcher, then record: red must hoist out (later order, SHALLOWER clip).
            var rootV = sV.Root;
            while (!rootV.IsNull && (sV.FirstChild(rootV).IsNull || sV.NextSibling(sV.FirstChild(rootV)).IsNull))
                rootV = sV.FirstChild(rootV);   // descend to the two-cell row
            var card0V = sV.FirstChild(sV.FirstChild(rootV));
            var rV = sV.AbsoluteRect(card0V);
            windowV.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(rV.X + rV.W / 2f, rV.Y + rV.H / 2f), 0, 0));
            hostV.RunFrame();
            var dlHov2 = new DrawList();
            SceneRecorder.Record(sV, dlHov2);
            var hRed = FindFillCommandNear(dlHov2, red); var hBlue = FindFillCommandNear(dlHov2, blue);
            bool escaped = hRed.Order >= 0 && hBlue.Order >= 0 && hRed.Order > hBlue.Order && hRed.ClipDepth < hBlue.ClipDepth;
            Check("e11virt.elevate-escape a hovered flagged cell hoists OUT of the HoverElevateClipRoot viewport clip (records after its pop, shallower depth); rest stays inside",
                restInside && escaped,
                $"rest r{rRed.Order}@d{rRed.ClipDepth} b{rBlue.Order}@d{rBlue.ClipDepth} hov r{hRed.Order}@d{hRed.ClipDepth} b{hBlue.Order}@d{hBlue.ClipDepth}");
        }

        // e11virt.5 — ItemsRepeater lifecycle (E11-L2, ItemsRepeater.idl:186-188): ElementPrepared on entering the
        // realized window, ElementClearing on leaving (recycle = Clearing(old)+Prepared(new)), visible-range prefetch;
        // a steady in-window scroll fires NOTHING (transform-only frames never realize).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-lifecycle", new Size2(640, 480), 1f));
            window.Show();
            var probe = new LifecycleRepeaterProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();

            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            bool mountSequential = probe.Prepared.Count == sc0.LastRealized && probe.Cleared.Count == 0;
            for (int i = 0; i < probe.Prepared.Count; i++) mountSequential &= probe.Prepared[i] == i;
            bool mountRange = probe.Ranges.Count > 0 && probe.Ranges[^1] == (0, sc0.LastRealized);

            // sub-extent scroll: in-window → no realize → no lifecycle.
            int p0 = probe.Prepared.Count, c0 = probe.Cleared.Count, rg0 = probe.Ranges.Count;
            var ptr = new Point2(150, 200);
            WheelDip(host, window, ptr, 2f);
            bool quiet = probe.Prepared.Count == p0 && probe.Cleared.Count == c0 && probe.Ranges.Count == rg0;

            // boundary-crossing scroll: 400px over 40px rows → window [0,14) → [6,24): Clearing 0..5, Prepared 14..23.
            WheelDip(host, window, ptr, 398f);
            host.Scene.TryGetScroll(vp, out var sc1);
            var live = new HashSet<int>();
            foreach (var i in probe.Prepared) live.Add(i);
            foreach (var i in probe.Cleared) live.Remove(i);
            bool windowSet = live.Count == sc1.LastRealized - sc1.FirstRealized && sc1.FirstRealized > 0;
            for (int i = sc1.FirstRealized; i < sc1.LastRealized; i++) windowSet &= live.Contains(i);
            bool conserved = probe.Prepared.Count - probe.Cleared.Count == sc1.LastRealized - sc1.FirstRealized
                && probe.Cleared.Count == sc1.FirstRealized                          // exactly the rows that left the top
                && probe.Ranges[^1] == (sc1.FirstRealized, sc1.LastRealized);
            Check("e11virt.5 ItemsRepeater lifecycle: Prepared/Clearing mirror the realized window across a recycle; in-window scroll fires nothing",
                mountSequential && mountRange && quiet && windowSet && conserved,
                $"mount=[0,{sc0.LastRealized}) → [{sc1.FirstRealized},{sc1.LastRealized}) prepared={probe.Prepared.Count} cleared={probe.Cleared.Count}");
        }

        // e11virt.5b — window in-place diff (component row state survives a VirtualListEl update): a parent re-render
        // swaps in a REBUILT VirtualListEl over an unchanged window, so RealizeWindow(reuseOverlap:false) re-renders
        // every realized slot. Same-slot type+key pairs must diff in place (general Update → keyed child reconcile →
        // ComponentEl same-type reuse) — the hosted row components keep instance identity and state, and NOTHING
        // remounts. (Component-hosting rows are not IsRecyclable, so without the in-place branch every row took the
        // mount+remove path: RowConstructions would grow by the window size and Rows[i] would be fresh instances.)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-inplace", new Size2(640, 480), 1f));
            window.Show();
            var probe = new WindowInPlaceDiffProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            host.RunFrame();   // let the mount-deferred overscan trickle settle (E4 budget) before snapshotting
            host.RunFrame();

            var vp = host.Scene.Root;
            host.Scene.TryGetScroll(vp, out var sc0);
            int built0 = probe.RowConstructions;
            int renders0 = probe.ParentRenders;
            var row0 = probe.Rows[sc0.FirstRealized];

            // Control: per-row state machinery is live BEFORE the parent re-render (granular row re-render path).
            row0.Local.Value = 7;
            host.RunFrame();

            // The parent re-render: a NEW VirtualListEl, same window → every slot gets a fresh Element from RenderItem.
            probe.Rev.Value = 1;
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc1);

            bool reRendered = probe.ParentRenders > renders0;                        // the Update→RealizeWindow path ran
            bool windowStable = sc1.FirstRealized == sc0.FirstRealized && sc1.LastRealized == sc0.LastRealized;
            bool noRemount = probe.RowConstructions == built0;                       // ZERO fresh row-component instances
            bool identity = ReferenceEquals(probe.Rows[sc0.FirstRealized], row0);    // same live instance in the slot
            bool stateKept = row0.Local.Peek() == 7;                                 // per-instance state survived
            Check("e11virt.5b window in-place diff: a parent re-render rebuilds every realized row Element; same-slot type+key rows update in place — component instances/state survive, nothing remounts",
                reRendered && windowStable && noRemount && identity && stateKept,
                $"renders={renders0}->{probe.ParentRenders} built={built0}->{probe.RowConstructions} window=[{sc1.FirstRealized},{sc1.LastRealized}) state={row0.Local.Peek()}");
        }

        // e11virt.5c — persistent prefix: the first two bound slots remain attached at the head while a deep normal
        // window recycles. Their handles and fixed index signals survive; the realized census is window + exactly 2.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-prefix", new Size2(640, 480), 1f));
            window.Show();
            var probe = new PersistentPrefixProbe();
            var device = new HeadlessGpuDevice();
            using var host = new AppHost(app, window, device, fonts, strings, probe);
            host.RunFrame();

            var vp = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc0);
            var content = sc0.ContentNode;
            var p0 = host.Scene.FirstChild(content);
            var p1 = host.Scene.NextSibling(p0);
            int normal0 = sc0.LastRealized - sc0.FirstRealized;
            int census0 = host.Scene.ChildCount(content);

            var ptr = new Point2(160f, 220f);
            window.QueueInput(WheelEvent(ptr, 0, 0, 2400f));
            for (int i = 0; i < 8; i++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc1);
            var q0 = host.Scene.FirstChild(content);
            var q1 = host.Scene.NextSibling(q0);
            int normal1 = sc1.LastRealized - sc1.FirstRealized;
            int census1 = host.Scene.ChildCount(content);

            bool fixedSignals = probe.PrefixSignals.Count == 2
                && probe.PrefixSignals[0].Peek() == 0 && probe.PrefixSignals[1].Peek() == 1;
            bool stableRoots = p0 == q0 && p1 == q1;
            bool bounded = census0 == normal0 + 2 && census1 == normal1 + 2;
            bool bandState = host.Scene.TryGetVirtualItemBand(
                    content, out int bandPrefix, out float bandInset, out float bandFade)
                && bandPrefix == 2 && Near(bandInset, 80f) && Near(bandFade, 22f);
            var vpRect = host.Scene.AbsoluteRect(vp);
            bool bandClip = false;
            for (int i = 0; i < device.LastClips.Count; i++)
            {
                var r = device.LastClips[i].DeviceRect;
                if (Near(r.Y, vpRect.Y + 80f, 0.5f) && Near(r.H, MathF.Max(0f, vpRect.H - 80f), 0.5f))
                {
                    bandClip = true;
                    break;
                }
            }
            (int Order, int ClipDepth) RectDepth(ColorF fill)
            {
                for (int i = 0; i < device.LastRects.Count; i++)
                {
                    var c = device.LastRects[i].Fill;
                    if (Near(c.R, fill.R, 0.006f) && Near(c.G, fill.G, 0.006f)
                        && Near(c.B, fill.B, 0.006f) && Near(c.A, fill.A, 0.006f))
                        return (i, device.LastRectClipDepths[i]);
                }
                return (-1, -1);
            }
            var heroDraw = RectDepth(PersistentPrefixProbe.HeroFill);
            var chromeDraw = RectDepth(PersistentPrefixProbe.ChromeFill);
            var rowDraw = RectDepth(PersistentPrefixProbe.RowFill);
            bool suffixOnlyClip = heroDraw.Order >= 0 && chromeDraw.Order >= 0 && rowDraw.Order >= 0
                && heroDraw.ClipDepth == chromeDraw.ClipDepth
                && rowDraw.ClipDepth == heroDraw.ClipDepth + 1;
            bool suffixFade = false;
            for (int i = 0; i < device.LastLayers.Count; i++)
            {
                var layer = device.LastLayers[i];
                if (layer.Kind == (int)LayerKind.EdgeFade
                    && layer.FadeEdges == (int)EdgeMask.Top
                    && Near(layer.DeviceRect.Y, vpRect.Y + 80f, 0.5f)
                    && Near(layer.FadeBandT, 22f, 0.5f))
                {
                    suffixFade = true;
                    break;
                }
            }

            var heroHit = host.Input.HitTest(new Point2(160f, 20f));
            var chromeHit = host.Input.HitTest(new Point2(160f, 60f));
            var rowHit = host.Input.HitTest(new Point2(160f, 100f));
            bool hitBand = heroHit == p0 && chromeHit == p1 && !rowHit.IsNull && rowHit != p0 && rowHit != p1;

            Check("e11virt.5c persistent prefix stays fixed/hittable while one shared item-band clip bounds the recyclable suffix of a deep virtual window",
                sc0.PersistentPrefixCount == 2 && sc1.PersistentPrefixCount == 2 && sc1.FirstRealized > 2
                && fixedSignals && stableRoots && bounded && bandState && bandClip && suffixOnlyClip && suffixFade && hitBand
                && device.ClipBalance == 0 && device.LayerBalance == 0,
                $"first={sc1.FirstRealized} prefixSignals={probe.PrefixSignals.Count} roots={stableRoots} census={census0}/{normal0}+2->{census1}/{normal1}+2 " +
                $"band={bandState}/{bandClip}/{suffixFade} depths={heroDraw.ClipDepth}/{chromeDraw.ClipDepth}/{rowDraw.ClipDepth} hits={heroHit.Raw.Index}/{chromeHit.Raw.Index}/{rowHit.Raw.Index}");
        }

        // e11virt.prefix-disp — the reorder displacement seed maps a realized-child ORDINAL to its item index with the
        // canonical FlexLayout.VirtualIndex rule (ord < prefix ? ord : FirstRealized + ord − prefix). The old flat
        // FirstRealized + ord ignored the sticky prefix, so on a prefixed list the gap opened `prefix` rows off AND the
        // sticky hero/chrome rows themselves were displaced. The predicate here targets items [F, F+3): under the
        // canonical mapping that is realized ordinals 2,3,4 and NEVER the two prefix ordinals; under the flat mapping it
        // would be ordinals 0 (prefix!), 1 (prefix!) and 2.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("prefix-disp", new Size2(320, 360), 1f));
            window.Show();
            var probe = new PrefixDisplacementProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var vp = FindScrollable(scene, scene.Root);

            window.QueueInput(WheelEvent(new Point2(150, 150), 0, 0, ScrollDelta: 2000f));
            for (int i = 0; i < 90; i++) host.RunFrame();
            scene.TryGetScroll(vp, out var sc);
            int first = sc.FirstRealized;
            bool deepWindow = first > PrefixDisplacementProbe.Prefix + 2;

            probe.Displacement = i => i >= first && i < first + 3 ? (0f, 24f) : (0f, 0f);
            probe.Ver.Value = probe.Ver.Peek() + 1;
            for (int i = 0; i < 60; i++) host.RunFrame();

            Span<float> dy = stackalloc float[6];
            int ord = 0;
            for (var n = scene.FirstChild(sc.ContentNode); !n.IsNull && ord < dy.Length; n = scene.NextSibling(n), ord++)
                dy[ord] = scene.Paint(n).LocalTransform.Dy;
            bool prefixUntouched = Near(dy[0], 0f, 0.5f) && Near(dy[1], 0f, 0.5f);
            bool windowDisplaced = Near(dy[2], 24f, 0.5f) && Near(dy[3], 24f, 0.5f) && Near(dy[4], 24f, 0.5f);
            bool boundedBelow = Near(dy[5], 0f, 0.5f);

            Check("e11virt.prefix-disp the ItemsView displacement seed maps realized ordinals through the canonical prefix rule — the sticky persistent prefix is never displaced and the first recyclable ordinal resolves to FirstRealized (a flat FirstRealized+ord displaces the hero and lands the gap `prefix` rows off)",
                deepWindow && ord == 6 && prefixUntouched && windowDisplaced && boundedBelow,
                $"first={first} realizedOrds={ord} dy=[{dy[0]:0.#},{dy[1]:0.#},{dy[2]:0.#},{dy[3]:0.#},{dy[4]:0.#},{dy[5]:0.#}]");
        }

        // e11virt.insertion — the WHOLE declarative sortable core end to end (design ruling (f) level 4). The probe
        // declares intent only: accept kinds, same-list, the dragged display rows, the insertable range and a deposit.
        // Every coordinate — viewport rect, live scroll offset, the MEASURED leading extent of the two prefix items,
        // the slot, the exact gap, the line and the preview position — is the view's own. Hover opens the gap, the
        // sources HIDE (virtual removal), the trailing rows never move, drop reports the raw slot, the optimistic
        // membership handoff closes the gap and reports the landing, and everything restores.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("insertion", new Size2(320, 400), 1f));
            window.Show();
            var probe = new InsertionProbe
            {
                SameList = true, Sources = [0, 2], DraggedCount = 2,
                // Keep the commit IN FLIGHT: only a mutation that can still publish a membership snapshot owns the
                // handoff the gap waits for (a commit that resolves without issuing one must tear down at once).
                Pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            for (int i = 0; i < 4; i++) host.RunFrame();
            var scene = host.Scene;
            var vp = FindScrollable(scene, scene.Root);
            scene.TryGetScroll(vp, out var sc0);
            var rect = scene.AbsoluteRect(vp);
            // Item 2 starts one measured prefix (2 x 40) into the content; the pointer sits 20 DIP past its centre,
            // so the NN/g rule claims slot 1 — i.e. insert before item 3.
            var p = new Point2(rect.X + 100f, rect.Y + 100f);

            bool began = host.Input.DragDrop.ExternalBegin("res", "payload", p, KeyModifiers.None);
            host.Input.DragDrop.Move(host.Input.DiagHitTest(p), p, 0f, 0f, KeyModifiers.None);
            for (int i = 0; i < 60; i++) host.RunFrame();

            Span<float> dy = stackalloc float[8];
            Span<float> op = stackalloc float[8];
            int ord = 0;
            for (var n = scene.FirstChild(sc0.ContentNode); !n.IsNull && ord < dy.Length; n = scene.NextSibling(n), ord++)
            {
                dy[ord] = scene.Paint(n).LocalTransform.Dy;
                op[ord] = scene.Paint(n).Opacity;
            }
            // Gap = EXACTLY 2 x 40; item 2 is a source ABOVE the slot (it vacates in place), item 3 takes the gap
            // minus that one removal, item 4 is the second source, and by item 5 both removals cancel the gap — the
            // content height never changed (the A4 "one row too big" gap is arithmetically impossible now).
            bool leadUntouched = Near(dy[0], 0f, 0.5f) && Near(dy[1], 0f, 0.5f);
            bool reflow = Near(dy[2], 0f, 0.5f) && Near(dy[3], 40f, 0.5f)
                && Near(dy[4], 40f, 0.5f) && Near(dy[5], 0f, 0.5f);
            bool hidden = op[2] < 0.05f && op[4] < 0.05f && op[3] > 0.95f && op[5] > 0.95f;
            var previewCard = FindFillNode(scene, scene.Root, InsertionProbe.PreviewFill);
            bool previewMounted = !previewCard.IsNull;
            bool effect = host.Input.DragDrop.Session.Effect == DropEffect.Move;

            // e11virt.insertion.previewpos (B1) — the preview must be POSITIONED in the gap, not merely mounted.
            // The preview host is a ZStack sibling of the list, so its transform carries a VIEWPORT-space Y:
            //   PreviewY = leading + (slot − removedAboveSlot)·extent − scrollOffset = 80 + (1−1)·40 − 0 = 80.
            // (leading = the MEASURED content offset of item First=2 = 2·40; sources [0,2] → absolute [2,4], and one of
            // them sits above SlotItem=3.) The gap's own leading edge is item 3's presented top minus the one removal
            // that vacated above it — identical arithmetic, which is the point: line, gap and preview share one plan.
            // A bind-shape flip on the reused preview node (idle branch static, active branch bound — wiring is
            // mount-only) leaves LocalTransform at identity and parks the card at the viewport's top-left instead.
            var previewBox = scene.Parent(previewCard);
            float previewDy = previewMounted ? scene.Paint(previewBox).LocalTransform.Dy : float.NaN;
            float previewTop = previewMounted ? scene.AbsoluteRect(previewBox).Y - rect.Y : float.NaN;
            bool previewPlaced = previewMounted && Near(previewDy, 80f, 0.5f) && Near(previewTop, 80f, 0.5f);
            Check("e11virt.insertion.previewpos the in-gap preview is POSITIONED by its bound transform at the plan's viewport-space gap edge (not merely mounted): its presented rect sits exactly at PreviewY, so the card cannot render at the list's top while the line and the gap are elsewhere (B1 — the bound/static flip on the reused preview node)",
                previewPlaced,
                $"mounted={previewMounted} dy={previewDy:0.##} top={previewTop:0.##} expected=80");

            // Steady-state Over: a pointer move INSIDE the current slot re-runs the whole geometry (viewport rect,
            // measured band lookup, plan, the bound line/preview offset) and must allocate nothing — the gap projection
            // may not perturb the cadence of the gesture it is drawing.
            var hit = host.Input.DiagHitTest(p);
            for (int k = 0; k < 4; k++) host.Input.DragDrop.Move(hit, p, 0f, 0f, KeyModifiers.None);   // warm
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int k = 0; k < 16; k++)
                host.Input.DragDrop.Move(hit, new Point2(p.X, p.Y + (k % 5) * 0.5f), 0f, 0f, KeyModifiers.None);
            long overAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            bool dropped = host.Input.DragDrop.TryDrop(p, KeyModifiers.None, out _);
            for (int i = 0; i < 4; i++) host.RunFrame();
            // The deposit reports the RAW slot the user aimed at (the backend move convention discounts the rows
            // removed above it — correcting here would move the block twice), and the gap is HELD until the
            // optimistic membership snapshot lands.
            bool deposit = probe.Deposits == 1 && probe.DepositSlot == 1;
            bool held = !FindFillNode(scene, scene.Root, InsertionProbe.PreviewFill).IsNull;

            probe.Ctl.ObserveInsertionMembership(new object());
            probe.Pending!.TrySetResult(true);
            for (int i = 0; i < 60; i++) host.RunFrame();
            ord = 0;
            for (var n = scene.FirstChild(sc0.ContentNode); !n.IsNull && ord < dy.Length; n = scene.NextSibling(n), ord++)
            {
                dy[ord] = scene.Paint(n).LocalTransform.Dy;
                op[ord] = scene.Paint(n).Opacity;
            }
            bool tornDown = FindFillNode(scene, scene.Root, InsertionProbe.PreviewFill).IsNull
                && Near(dy[3], 0f, 0.5f) && Near(dy[4], 0f, 0.5f)
                && op[2] > 0.95f && op[4] > 0.95f;
            bool landed = probe.LandedSlot == 1 && probe.LandedCount == 2;

            Check("e11virt.insertion a list that declares only InsertionOptions gets the whole premiere destination from its OWN geometry: the slot resolves against the MEASURED prefix leading extent, virtual removal opens an exact N-extent gap with the source rows hidden, rows outside the insertable range never move, the preview mounts in the gap, the drop reports the raw slot and holds the gap until the membership handoff, which closes it and reports the landing",
                began && dropped && leadUntouched && reflow && hidden && previewMounted && effect
                && deposit && held && tornDown && landed && overAlloc == 0,
                $"began={began} lead={leadUntouched} reflow={reflow} hidden={hidden} preview={previewMounted} effect={effect} deposit=({probe.Deposits},{probe.DepositSlot}) held={held} down={tornDown} landed=({probe.LandedSlot},{probe.LandedCount}) overAlloc={overAlloc}B");
        }

        // e11virt.insertion.empty — S5 cause 2. An EMPTY / still-loading destination used to swallow the drop: the
        // lane bailed on a zero-extent viewport and never cleared, so the gesture ended in silence. The framework
        // resolves slot 0 against the leading edge and the deposit APPENDS.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("insertion-empty", new Size2(320, 260), 1f));
            window.Show();
            var probe = new EmptyInsertionProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            for (int i = 0; i < 4; i++) host.RunFrame();
            var scene = host.Scene;
            var vp = FindScrollable(scene, scene.Root);
            var rect = scene.AbsoluteRect(vp);
            var p = new Point2(rect.X + 40f, rect.Y + 120f);
            bool began = host.Input.DragDrop.ExternalBegin("res", "payload", p, KeyModifiers.None);
            host.Input.DragDrop.Move(host.Input.DiagHitTest(p), p, 0f, 0f, KeyModifiers.None);
            for (int i = 0; i < 3; i++) host.RunFrame();
            bool over = host.Input.DragDrop.OverTarget.IsNull == false;
            bool dropped = host.Input.DragDrop.TryDrop(p, KeyModifiers.None, out _);
            for (int i = 0; i < 3; i++) host.RunFrame();
            Check("e11virt.insertion.empty an EMPTY destination still accepts the drop at slot 0 (append) instead of silently discarding it — the S5 'cannot drop in this mode' cause that had no cue at all",
                began && over && dropped && probe.Deposits == 1 && probe.DepositSlot == 0,
                $"began={began} over={over} dropped={dropped} deposits={probe.Deposits} slot={probe.DepositSlot}");
        }

        // e11virt.6 DELETED (scroll-v3 WP-R3): demoed the deleted generic Repeater.ItemsRepeater<T>((i,item) =>
        // Element) front door, eagerly inspecting its returned BoxEl outside any host — ItemsView.Create has no
        // equivalent (always Component-wrapped via Embed.Comp; CreateBound<T> is a different, persistent-slot
        // contract) and no other live gate demoed the deleted factory shape, so nothing else needs retargeting here.

        // e11virt.7 — SelectionModel Single (SingleSelector.cpp:25-57): Select REPLACES; Ctrl+interact toggles;
        // plain focus follows (m_followFocus default true); Ctrl+focus moves without selecting.
        {
            var m = new SelectionModel { ItemCount = 100 };
            int events = 0;
            m.SelectionChanged = () => events++;
            bool def = m.Mode == ItemsSelectionMode.Single && m.SelectedCount == 0 && m.AnchorIndex == -1;   // ItemsView.h s_defaultSelectionMode
            m.Select(3); m.Select(7);
            bool replaces = m.IsSelected(7) && !m.IsSelected(3) && m.SelectedCount == 1 && events == 2;
            m.OnInteractedAction(7, ctrl: true, shift: false);    // selected + Ctrl → deselect (cpp:39-43)
            bool ctrlOff = m.SelectedCount == 0 && events == 3;
            m.OnInteractedAction(7, ctrl: true, shift: false);    // unselected + Ctrl → select (cpp:35-38)
            bool ctrlOn = m.IsSelected(7) && events == 4;
            m.OnFocusedAction(8, ctrl: false, shift: false);      // follow-focus (cpp:46-57)
            bool follow = m.IsSelected(8) && m.SelectedCount == 1;
            m.OnFocusedAction(9, ctrl: true, shift: false);       // Ctrl+focus: no selection change
            bool ctrlFocus = m.IsSelected(8) && !m.IsSelected(9) && m.Version.Peek() == events;
            Check("e11virt.7 SelectionModel Single: replace-on-select, ctrl-toggle, focus-follow, ctrl-focus inert (SingleSelector.cpp:25-57)",
                def && replaces && ctrlOff && ctrlOn && follow && ctrlFocus, $"events={events} version={m.Version.Peek()}");
        }

        // e11virt.8 — SelectionModel Multiple (MultipleSelector.cpp:18-92): toggle without modifiers; Shift extends or
        // deselects the anchor range by the ANCHOR's state (only when the states differ); Shift with NO anchor is a
        // NO-OP (the toggle is cpp's `else` — it never runs while Shift is held); plain focus moves never select.
        {
            var m = new SelectionModel { ItemCount = 100, Mode = ItemsSelectionMode.Multiple };
            m.OnInteractedAction(4, ctrl: false, shift: true);    // no anchor yet → no-op (cpp:24-63)
            bool shiftNoAnchor = m.SelectedCount == 0 && m.AnchorIndex == -1;
            m.OnInteractedAction(2, false, false);                // toggle on → anchor 2
            m.OnInteractedAction(6, false, true);                 // anchor selected, 6 not → SelectRangeFromAnchorTo
            bool shiftRange = m.SelectedCount == 5 && m.RangeCount == 1 && m.GetRange(0) == (2, 6);
            m.OnInteractedAction(4, false, true);                 // anchor and 4 BOTH selected → states equal → nothing (cpp:44-52)
            bool statesEqual = m.SelectedCount == 5;
            m.OnFocusedAction(8, false, false);                   // plain focus move never selects (cpp:65-92)
            bool focusInert = m.SelectedCount == 5 && !m.IsSelected(8);
            m.OnInteractedAction(2, false, false);                // toggle the anchor itself OFF (anchor stays 2)
            m.OnInteractedAction(5, false, true);                 // anchor UNselected, 5 selected → DeselectRangeFromAnchorTo
            bool shiftDeselect = !m.IsSelected(3) && !m.IsSelected(5) && m.IsSelected(6) && m.SelectedCount == 1;
            Check("e11virt.8 SelectionModel Multiple: modifier-free toggle, shift range by anchor state, shift-no-anchor no-op (MultipleSelector.cpp:18-92)",
                shiftNoAnchor && shiftRange && statesEqual && focusInert && shiftDeselect,
                $"count={m.SelectedCount} ranges={m.RangeCount}");
        }

        // e11virt.9 — SelectionModel Extended (ExtendedSelector.cpp:18-83): plain replaces ONLY on an unselected item;
        // Ctrl toggles; Shift replaces with the anchor range; focus: Shift+Ctrl additive, Shift replace, plain replace,
        // Ctrl alone moves without selecting.
        {
            var m = new SelectionModel { ItemCount = 100, Mode = ItemsSelectionMode.Extended };
            m.OnInteractedAction(2, false, false);                // plain → clear+select, anchor 2
            m.OnInteractedAction(6, false, true);                 // Shift → replace with [anchor..6] (cpp:23-32)
            bool range = m.SelectedCount == 5 && m.GetRange(0) == (2, 6) && m.AnchorIndex == 2;
            m.OnInteractedAction(9, true, false);                 // Ctrl → additive toggle (cpp:33-43)
            bool ctrlAdd = m.SelectedCount == 6 && m.IsSelected(9);
            m.OnInteractedAction(4, false, false);                // plain on a SELECTED item → keep (cpp:46 "Only clear ... different item")
            bool keepOnSelected = m.SelectedCount == 6;
            m.OnInteractedAction(20, false, false);               // plain on unselected → clear+select
            bool replace = m.SelectedCount == 1 && m.IsSelected(20) && m.AnchorIndex == 20;
            m.OnFocusedAction(23, false, true);                   // Shift+focus → replace with the anchor range (cpp:66-75)
            bool focusShift = m.SelectedCount == 4 && m.GetRange(0) == (20, 23) && m.AnchorIndex == 20;
            m.OnFocusedAction(30, true, false);                   // Ctrl+focus → nothing (cpp falls through)
            bool focusCtrl = m.SelectedCount == 4;
            m.OnFocusedAction(28, true, true);                    // Shift+Ctrl+focus → ADDITIVE anchor range (cpp:59-65)
            bool focusCtrlShift = m.SelectedCount == 9 && m.GetRange(0) == (20, 28);
            m.OnFocusedAction(40, false, false);                  // plain focus → clear+select (cpp:76-80)
            bool focusPlain = m.SelectedCount == 1 && m.IsSelected(40);
            Check("e11virt.9 SelectionModel Extended: plain/ctrl/shift interact + the four focus chords (ExtendedSelector.cpp:18-83)",
                range && ctrlAdd && keepOnSelected && replace && focusShift && focusCtrl && focusCtrlShift && focusPlain,
                $"count={m.SelectedCount}");
        }

        // e11virt.10 — selection is DECOUPLED from realization: SelectAll over 10k stores ONE inclusive range
        // (never walks indices), deselect splits it, invert complements it, shrinking ItemCount trims it.
        {
            var m = new SelectionModel { ItemCount = 10_000, Mode = ItemsSelectionMode.Extended };
            int events = 0;
            m.SelectionChanged = () => events++;
            m.SelectAll();
            bool one = m.RangeCount == 1 && m.GetRange(0) == (0, 9_999) && m.SelectedCount == 10_000 && events == 1;
            m.SelectAll();                                       // no actual change → no event (WinUI raises only on change)
            bool idempotent = events == 1;
            m.DeselectRange(100, 199);
            bool split = m.RangeCount == 2 && m.SelectedCount == 9_900 && !m.IsSelected(150) && events == 2;
            m.InvertSelection();
            bool inverted = m.RangeCount == 1 && m.GetRange(0) == (100, 199) && m.SelectedCount == 100;
            m.ItemCount = 150;                                   // shrink trims out-of-range selection
            bool trimmed = m.SelectedCount == 50 && m.GetRange(0) == (100, 149);
            Check("e11virt.10 SelectionModel ranges: select-all-over-10k = ONE range (realizes nothing), split/invert/trim stay range-shaped",
                one && idempotent && split && inverted && trimmed, $"events={events} ranges={m.RangeCount} count={m.SelectedCount}");
        }

        // e11virt.10b — selection follows ITEM identity across a RemoveAt+Insert reorder (ListViewBase::ReorderItemsTo),
        // including range splits, instead of staying on the old slot.
        {
            var single = new SelectionModel { ItemCount = 8 };
            int singleEvents = 0;
            single.SelectionChanged = () => singleEvents++;
            single.Select(4);
            single.RemapMove(4, 2);
            bool selectedItemMoved = single.IsSelected(2) && !single.IsSelected(4) && single.FirstSelectedIndex == 2
                && single.AnchorIndex == 2 && singleEvents == 2;

            var range = new SelectionModel { ItemCount = 8, Mode = ItemsSelectionMode.Multiple };
            range.SelectRange(4, 5);
            range.AnchorIndex = 4;
            range.RemapMove(4, 2);
            bool splitRange = range.RangeCount == 2 && range.GetRange(0) == (2, 2) && range.GetRange(1) == (5, 5)
                && range.AnchorIndex == 2;

            var all = new SelectionModel { ItemCount = 10, Mode = ItemsSelectionMode.Extended };
            int allEvents = 0;
            all.SelectionChanged = () => allEvents++;
            all.SelectAll();
            all.RemapMove(8, 2);
            bool allStillCompact = all.RangeCount == 1 && all.GetRange(0) == (0, 9) && allEvents == 1;

            Check("e11virt.10b SelectionModel RemapMove preserves selected item identity across reorder (single, split range, select-all compact)",
                selectedItemMoved && splitRange && allStillCompact,
                $"single={single.FirstSelectedIndex} events={singleEvents} split={range.RangeCount} allEvents={allEvents}");
        }

        // e11virt.11 — ItemContainer state ARGB, BOTH themes (full #AARRGGBB; ItemContainer_themeresources.xaml:5-18
        // dark / :37-49 light → Common_themeresources_any.xaml) + the selected dual-stroke geometry + checkbox plate
        // + disabled collapse.
        {
            bool all = true;
            var details = new System.Text.StringBuilder();
            try
            {
                foreach (var (kind, hover, pressed, ring, inner, plate, plateStroke) in new (ThemeKind, ColorF, ColorF, ColorF, ColorF, ColorF, ColorF)[]
                {
                    // DARK — PointerOver = SubtleFillColorSecondary #0FFFFFFF, Pressed = SubtleFillColorTertiary
                    // #0AFFFFFF (Common_themeresources_any.xaml:26-27); SelectionVisual = AccentFillColorDefault =
                    // SystemAccentColorLight2 #60CDFF (:125); SelectedInnerBorder = ControlSolidFillColorDefault
                    // #454545 (:24); checkbox plate = ControlOnImageFillColorDefault #B31C1C1C (:34); plate stroke =
                    // CheckBoxCheckBackgroundStrokeUnchecked → ControlStrongStrokeColorDefault #8BFFFFFF (:48).
                    (ThemeKind.Dark,
                     ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F), ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0A),
                     ColorF.FromRgba(0x60, 0xCD, 0xFF), ColorF.FromRgba(0x45, 0x45, 0x45),
                     ColorF.FromRgba(0x1C, 0x1C, 0x1C, 0xB3), ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x8B)),
                    // LIGHT — Secondary #09000000 / Tertiary #06000000 (:230-231); accent = SystemAccentColorDark1
                    // #005FB8 (:329); inner = #FFFFFF (:228); plate = #C9FFFFFF (:238); stroke = #72000000 (:252).
                    (ThemeKind.Light,
                     ColorF.FromRgba(0x00, 0x00, 0x00, 0x09), ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
                     ColorF.FromRgba(0x00, 0x5F, 0xB8), ColorF.FromRgba(0xFF, 0xFF, 0xFF),
                     ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xC9), ColorF.FromRgba(0x00, 0x00, 0x00, 0x72)),
                })
                {
                    Tok.Use(kind);
                    const float tol = 1.5f / 255f;

                    // Selected + multi-select + unchecked plate → [ic-content, ic-ring, ic-common, ic-check].
                    var scene = LayoutTree(strings, ItemContainer.Build(new BoxEl(), isSelected: true,
                        onInteraction: (t, mods) => { }, showSelectionCheckbox: true, isChecked: false, width: 120f, height: 48f));
                    var root = scene.Root;
                    ref var p = ref scene.Paint(root);
                    var ringN = Child(scene, root, 1);
                    var commonN = Child(scene, root, 2);
                    var plateN = Child(scene, Child(scene, root, 3), 0);
                    bool states = ColorClose(p.Fill, ColorF.Transparent, tol)                      // ItemContainerBackground = SubtleFillColorTransparent (:5/:37)
                        && ColorClose(scene.Paint(commonN).HoverFill, hover, tol) && ColorClose(scene.Paint(commonN).PressedFill, pressed, tol)
                        && ColorClose(scene.Paint(ringN).BorderColor, ring, tol) && Near(scene.Paint(ringN).BorderWidth, 3f)   // PART_SelectionVisual (ItemContainer.xaml:116-126)
                        && ColorClose(scene.Paint(commonN).BorderColor, inner, tol) && Near(scene.Paint(commonN).BorderWidth, 1f);
                    var ir = scene.AbsoluteRect(commonN);
                    var pr = scene.AbsoluteRect(plateN);
                    bool geometry = Near(ir.X, 2f) && Near(ir.Y, 2f) && Near(ir.W, 116f) && Near(ir.H, 44f)   // ItemContainerSelectedInnerMargin 2 (themeresources:57)
                        && Near(pr.W, 20f) && Near(pr.H, 20f) && Near(pr.X, 96f) && Near(pr.Y, -2f);          // 20px checkbox, top-right, Margin 4,−2 (:56,:59-60)
                    bool plateColors = ColorClose(scene.Paint(plateN).Fill, plate, tol)
                        && ColorClose(scene.Paint(plateN).BorderColor, plateStroke, tol)
                        && FindPolylineStrokeNode(scene, root).IsNull;                            // unchecked → no checkmark glyph

                    // Checked plate flips to the accent fill + drawn checkmark.
                    var checkedScene = LayoutTree(strings, ItemContainer.Build(new BoxEl(), true,
                        (t, mods) => { }, showSelectionCheckbox: true, isChecked: true, width: 120f, height: 48f));
                    var cPlate = Child(checkedScene, Child(checkedScene, checkedScene.Root, 3), 0);
                    bool checkedOk = ColorClose(checkedScene.Paint(cPlate).Fill, ring, tol)        // CheckBoxCheckBackgroundFillChecked = AccentFillColorDefault (CheckBox_themeresources.xaml:57)
                        && !FindPolylineStrokeNode(checkedScene, checkedScene.Root).IsNull;

                    // Disabled: Opacity 0.3 (ItemContainerDisabledOpacity, :54) and PART_SelectionVisual collapses
                    // (ItemContainer.xaml:108-110) → only the content layer remains.
                    var disabledScene = LayoutTree(strings, ItemContainer.Build(new BoxEl(), true,
                        (t, mods) => { }, isEnabled: false, width: 120f, height: 48f));
                    bool disabled = Near(disabledScene.Paint(disabledScene.Root).Opacity, 0.3f, 0.001f)
                        && disabledScene.ChildCount(disabledScene.Root) == 1;

                    all &= states && geometry && plateColors && checkedOk && disabled;
                    details.Append($"{kind}: states={states} geo={geometry} plate={plateColors} checked={checkedOk} disabled={disabled}; ");
                }
            }
            finally { Tok.Use(ThemeKind.Dark); }
            Check("e11virt.11 ItemContainer: WinUI state ARGB both themes + dual-stroke geometry + checkbox plate + disabled collapse", all, details.ToString());
        }

        // e11virt.12 — the selection ring and multi-select checkbox FADE IN when they appear: opacity 0 → 1 over
        // ControlFastAnimationDuration 167ms / KeySpline 0,0,0,1 (ItemContainer.xaml:54-56 SelectedPointerOver and
        // :93-99 the checkbox storyboard) — the engine carries both as enter transitions on the keyed child nodes.
        {
            var scene = new SceneStore();
            var engine = new AnimEngine(scene);
            var recon = new TreeReconciler(scene, strings) { Anim = engine };
            Element Tree(bool multi, bool selected) => new BoxEl
            {
                Width = 200, Height = 60,
                Children = [ItemContainer.Build(new BoxEl(), isSelected: selected, onInteraction: (t, mods) => { },
                                                showSelectionCheckbox: multi, isChecked: false, width: 200f, height: 60f)],
            };
            var t0 = Tree(false, false);
            recon.ReconcileRoot(t0, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var container = Child(scene, scene.Root, 0);
            bool noChrome = scene.ChildCount(container) == 2;     // unselected + single → content + CommonVisual only

            recon.ReconcileRoot(Tree(true, true), t0);            // select + flip to Multiple → ring + checkbox enter
            new FlexLayout(scene, fonts).Run(scene.Root);
            var ring = Child(scene, container, 1);
            var check = Child(scene, container, 3);
            engine.Tick(16f);
            float ring16 = scene.Paint(ring).Opacity, check16 = scene.Paint(check).Opacity;
            for (int i = 0; i < 4; i++) engine.Tick(16f);         // t = 80ms — mid-flight on the 167ms tween
            float mid = scene.Paint(check).Opacity;
            for (int i = 0; i < 30; i++) engine.Tick(16f);        // past 167ms → settled
            bool settled = Near(scene.Paint(check).Opacity, 1f, 0.001f) && Near(scene.Paint(ring).Opacity, 1f, 0.001f);
            bool entering = ring16 < 0.95f && check16 < 0.95f && mid > check16 && mid < 1f;

            // the authored spec is exactly the WinUI storyboard: a 167ms decelerate TWEEN entering from opacity 0.
            BoxEl? checkEl = null;
            foreach (var child in ItemContainer.Build(new BoxEl(), false, (t, mods) => { }, showSelectionCheckbox: true).Children)
                if (child is BoxEl { Key: "ic-check" } b) { checkEl = b; break; }
            bool spec = checkEl?.Animate is { } a && a.Dynamics.Kind == DynamicsKind.Tween && Near(a.Dynamics.DurationMs, 167f)
                && a.Dynamics.Easing == Easing.FluentDecelerate && a.Enter.Active && Near(a.Enter.Opacity, 0f)
                && (a.Channels & TransitionChannels.Opacity) != 0;
            Check("e11virt.12 ItemContainer ring + checkbox enter-fade 0→1 over the 167ms ControlFastAnimationDuration tween",
                noChrome && entering && settled && spec, $"op16=({ring16:0.00},{check16:0.00}) op80={mid:0.00} settled={settled} spec={spec}");
        }

        // e11virt.13/14/15 — ItemsView (E11-L3) keyboard surface on a virtualized stack.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-iv", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewKeyboardProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            var sel = ctl.Selection!;

            NodeHandle vp = NodeHandle.Null;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (host.Scene.TryGetScroll(n, out var s) && s.ItemCount == ItemsViewKeyboardProbe.N) vp = n;
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            ScrollState Sc() { host.Scene.TryGetScroll(vp, out var s); return s; }
            void Press(float x, float y, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(x, y), 0, 0, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(x, y), 0, 0, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, mods));
                host.RunFrame();
            }

            bool def = ctl.CurrentItemIndex == -1 && sel.Mode == ItemsSelectionMode.Single;   // CurrentItemIndex default −1 (ItemsView.idl:46-47)
            Press(180f, 100f, 1_000);                                    // row 2
            bool click = ctl.CurrentItemIndex == 2 && sel.IsSelected(2) && sel.SelectedCount == 1;
            Key(Keys.Down);
            bool down = ctl.CurrentItemIndex == 3 && sel.IsSelected(3) && sel.SelectedCount == 1;   // selection follows focus (SingleSelector)
            Key(Keys.Up);
            bool up = ctl.CurrentItemIndex == 2 && sel.IsSelected(2);
            Key(Keys.PageDown);                                          // viewport jump (cpp:1103+): 80 + 320 → row 10
            bool pgdn = ctl.CurrentItemIndex == 10;
            Key(Keys.PageUp);
            bool pgup = ctl.CurrentItemIndex == 2;
            Key(Keys.End);                                               // End: bottom edge-aligned (cpp:1009-1016, ratio 1)
            var scEnd = Sc();
            var focusedEnd = FocusedNode(host.Scene, host.Scene.Root);
            var fr = host.Scene.AbsoluteRect(focusedEnd);
            bool end = ctl.CurrentItemIndex == 99 && sel.IsSelected(99)
                && Near(scEnd.OffsetY, ItemsViewKeyboardProbe.N * 40f - 320f)
                && Near(fr.Y, 280f) && Near(fr.H, 40f);                  // the realized last container carries keyboard focus
            Key(Keys.Home);                                              // Home: top edge-aligned (ratio 0)
            bool home = ctl.CurrentItemIndex == 0 && Near(Sc().OffsetY, 0f);
            Check("e11virt.13 ItemsView keyboard: arrows follow focus, PageUp/Down jump a viewport, Home/End edge-align + focus the realized container",
                def && click && down && up && pgdn && pgup && end && home,
                $"def={def} click={click} down={down} up={up} pgdn={pgdn} pgup={pgup} end={end} home={home} cur={ctl.CurrentItemIndex} end-off={scEnd.OffsetY:0} focusY={fr.Y:0} focusH={fr.H:0}");

            Key(Keys.A, KeyModifiers.Ctrl);                              // Ctrl+A gated OFF in Single (ItemsViewInteractions.cpp:35-50)
            bool noSelectAll = sel.SelectedCount == 1;
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
            host.RunFrame();
            bool typeahead = ctl.CurrentItemIndex == 57 && sel.IsSelected(57)
                && Near(Sc().OffsetY, 57f * 40f + 40f - 320f);           // minimal scroll realizes it at the bottom edge
            int invoked0 = probe.InvokedCount;
            Key(Keys.Enter);                                             // EnterKey invokes (ItemsView.cpp:423-426)
            bool enterInvokes = probe.InvokedCount == invoked0 + 1 && probe.LastInvoked == 57;
            Key(Keys.Space);                                             // SpaceKey selects WITHOUT invoking
            bool spaceSilent = probe.InvokedCount == invoked0 + 1 && sel.IsSelected(57);
            Press(180f, 100f, 80_000);                                   // Tap selects only (no invoke) — row 52 at this offset
            bool tapSilent = probe.InvokedCount == invoked0 + 1 && ctl.CurrentItemIndex == 52;
            Press(180f, 100f, 80_100);                                   // ClickCount 2 → DoubleTap invokes
            bool dblInvokes = probe.InvokedCount == invoked0 + 2 && probe.LastInvoked == 52;
            Check("e11virt.14 ItemsView typeahead jumps to the prefix match (+min-scroll realize); invoke matrix: Enter/DoubleTap yes, Tap/Space no; Ctrl+A gated in Single",
                noSelectAll && typeahead && enterInvokes && spaceSilent && tapSilent && dblInvokes,
                $"type→{ctl.CurrentItemIndex} invoked={probe.InvokedCount} last={probe.LastInvoked}");

            ctl.StartBringItemIntoView(10, 0f);                          // explicit edge-align (ratio 0)
            host.RunFrame();
            var scB = Sc();
            bool bring = Near(scB.OffsetY, 400f) && scB.FirstRealized <= 10 && scB.LastRealized > 10
                && ctl.CurrentItemIndex == 52;                           // StartBringItemIntoView never moves focus (ItemsView.cpp:119-127)
            ctl.StartBringItemIntoView(12);                              // already visible + default options → minimal scroll = no-op
            host.RunFrame();
            bool minimal = Near(Sc().OffsetY, 400f);
            Check("e11virt.15 StartBringItemIntoView: realizes + edge-aligns by ratio without moving focus; in-view target is a minimal-scroll no-op",
                bring && minimal, $"off={scB.OffsetY:0} window=[{scB.FirstRealized},{scB.LastRealized}) cur={ctl.CurrentItemIndex}");
        }

        // e11virt.16 — grid arrows: Left/Right = index ±1, Up/Down = ±columns (ItemsViewInteractions.cpp:1051-1067).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-grid", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewGridProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            void Key(int key)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
                host.RunFrame();
            }
            // cell 1 of a 4-col grid on 360 cross: colW = (360 − 3×8)/4 = 84 → cell 1 spans x [92,176), y [0,72).
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(134f, 36f), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(134f, 36f), 0, 0));
            host.RunFrame();
            bool click = ctl.CurrentItemIndex == 1;
            Key(Keys.Right); bool right = ctl.CurrentItemIndex == 2;
            Key(Keys.Down); bool gdown = ctl.CurrentItemIndex == 6;
            Key(Keys.Left); bool left = ctl.CurrentItemIndex == 5;
            Key(Keys.Up); bool gup = ctl.CurrentItemIndex == 1;
            Check("e11virt.16 ItemsView grid arrows: Left/Right ±1, Up/Down ±columns (index-based orientation path)",
                click && right && gdown && left && gup, $"cur 1→2→6→5→{ctl.CurrentItemIndex}");
        }

        // e11virt.17 — Extended mode end-to-end through pointer chords + Shift+arrow + Ctrl+A.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-ext", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewExtendedProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            var sel = ctl.Selection!;
            void Press(int row, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                var pt = new Point2(180f, row * 40f + 20f);
                window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, pt, 0, 0, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, mods));
                host.RunFrame();
            }

            Press(2, 1_000);                                             // plain → {2}
            bool plain = sel.SelectedCount == 1 && sel.IsSelected(2) && ctl.CurrentItemIndex == 2;
            Press(6, 2_000, KeyModifiers.Shift);                         // Shift → replace with [2..6]
            bool shiftRange = sel.SelectedCount == 5 && sel.GetRange(0) == (2, 6);
            Press(7, 3_000, KeyModifiers.Ctrl);                          // Ctrl → additive {2..6, 7}
            bool ctrlAdd = sel.SelectedCount == 6 && sel.IsSelected(7);
            Press(4, 4_000);                                             // plain on SELECTED → selection kept
            bool keep = sel.SelectedCount == 6 && ctl.CurrentItemIndex == 4;
            Key(Keys.Down, KeyModifiers.Shift);                          // Shift+arrow → replace with [5..anchor(7)]
            bool shiftArrow = ctl.CurrentItemIndex == 5 && sel.SelectedCount == 3 && sel.GetRange(0) == (5, 7);
            Key(Keys.A, KeyModifiers.Ctrl);                              // Ctrl+A allowed in Extended
            bool selectAll = sel.SelectedCount == ItemsViewExtendedProbe.N && sel.RangeCount == 1;
            bool events = probe.SelectionChangedCount == 5;              // one SelectionChanged per actual change
            Check("e11virt.17 ItemsView Extended: plain/shift/ctrl pointer chords, plain-on-selected keeps, Shift+arrow anchor range, Ctrl+A",
                plain && shiftRange && ctrlAdd && keep && shiftArrow && selectAll && events,
                $"count={sel.SelectedCount} ranges={sel.RangeCount} changes={probe.SelectionChangedCount}");
        }

        // e11virt.18 — Multiple over 10k: toggle clicks re-skin the window; Ctrl+A selects ALL via one stored range
        // while realizing nothing (bounded realized children + bounded template re-runs), and every realized
        // container shows the selected chrome + checkbox.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e11-multi", new Size2(480, 360), 1f));
            window.Show();
            var probe = new ItemsViewMultipleProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var ctl = probe.Controller;
            var sel = ctl.Selection!;
            int calls0 = probe.TemplateCalls;
            void Press(int row, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                var pt = new Point2(180f, row * 40f + 20f);
                window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, pt, 0, 0, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            Press(3, 1_000);
            bool on = sel.IsSelected(3) && sel.SelectedCount == 1;       // Multiple: plain click toggles
            Press(3, 2_500);
            bool off = !sel.IsSelected(3) && sel.SelectedCount == 0;
            Press(3, 4_000);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.A, KeyModifiers.Ctrl));
            host.RunFrame();
            bool allSel = sel.SelectedCount == ItemsViewMultipleProbe.N && sel.RangeCount == 1
                && sel.GetRange(0) == (0, ItemsViewMultipleProbe.N - 1);

            NodeHandle vp = NodeHandle.Null;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (host.Scene.TryGetScroll(n, out var s) && s.ItemCount == ItemsViewMultipleProbe.N) vp = n;
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
            }
            Visit(host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc);
            int realized = host.Scene.ChildCount(sc.ContentNode);
            int templateDelta = probe.TemplateCalls - calls0;
            bool bounded = realized < 40 && templateDelta > 0 && templateDelta < realized * 12;   // window-only re-skin per change
            var firstContainer = host.Scene.FirstChild(sc.ContentNode);
            bool chrome = host.Scene.ChildCount(firstContainer) == 4;    // content + ring + inner + checkbox
            Check("e11virt.18 ItemsView Multiple over 10k: toggle clicks, Ctrl+A = ONE range realizing nothing (bounded window re-skin) + checkbox chrome",
                on && off && allSel && bounded && chrome,
                $"count={sel.SelectedCount} ranges={sel.RangeCount} realized={realized} templateΔ={templateDelta}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // e11virt.comp-pin — THE FOOTGUN, DOCUMENTED BY A TEST: a scroll bind must sit on a RAW element, never on a
    // component's rendered root.
    //
    // A sticky effect with no named scope clamps to its IMMEDIATE parent (`limit = parent.H − node.H`) and a component
    // anchor MIRRORS its rendered child's size (`Reconciler.MirrorParticipation`), so a `.Collapse`/`.Sticky` placed on
    // what a component RETURNS sees `limit == 0`: it never translates, `NodeFlags.StickyPinned` is never set,
    // `ScrollState.StuckTopBit` never lights and the `:stuck` callback never fires. Wavee's playlist/Liked hero arm hit
    // exactly this — its two persistent prefix slots are `Embed.Comp(...)` and the binds had migrated onto the item
    // component's root, leaving an empty band under the toolbar where a pinned hero + chrome were still being reserved.
    // The cure, and the shape this gate pins down: wrap the component in a raw `BoxEl` and put the bind on the WRAPPER.
    // Both arms are mounted here, identical but for that one placement, so the negative twin keeps the trap visible.
    // Neighbours: e11virt.5c (the persistent prefix itself), e11virt.prefix-disp.
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    static void CompRootPinChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        (bool HeroPinned, bool ChromePinned, float HeroDy, float ChromeDy) Arm(bool onCompRoot)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("comp-pin", new Size2(640, 480), 1f));
            window.Show();
            var probe = new CompRootPinProbe(onCompRoot);
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();

            var vp = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc0);
            var content = sc0.ContentNode;

            // Past the hero's collapse distance (HeroH − BandH = 144) and then some: both prefix slots are pinned by now.
            window.QueueInput(WheelEvent(new Point2(160f, 240f), 0, 0, 1600f));
            for (int i = 0; i < 24 && host.HasActiveWork; i++) host.RunFrame();
            for (int i = 0; i < 4; i++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc1);

            // The BIND TARGETS: the two slot roots. In the raw-wrapper arm those roots ARE the wrappers; in the
            // component-root arm they are the component anchors, whose rendered child carries the (never-pinning) bind.
            var r0 = host.Scene.FirstChild(content);
            var r1 = host.Scene.NextSibling(r0);
            var b0 = onCompRoot ? host.Scene.FirstChild(r0) : r0;
            var b1 = onCompRoot ? host.Scene.FirstChild(r1) : r1;
            return ((host.Scene.Flags(b0) & NodeFlags.StickyPinned) != 0,
                    (host.Scene.Flags(b1) & NodeFlags.StickyPinned) != 0,
                    host.Scene.Paint(b0).LocalTransform.Dy, host.Scene.Paint(b1).LocalTransform.Dy);
        }

        var ok = Arm(onCompRoot: false);
        Check("e11virt.comp-pin a collapse/sticky bind on a RAW WRAPPER around an Embed.Comp prefix slot really pins: StickyPinned on both wrapper roots exactly once",
            ok.HeroPinned && ok.ChromePinned
            && ok.HeroDy > 1f && ok.ChromeDy > 1f,
            $"hero={ok.HeroPinned}/dy={ok.HeroDy:0.#} chrome={ok.ChromePinned}/dy={ok.ChromeDy:0.#}");
    }

    /// <summary>Two persistent prefix slots (a 200-DIP hero, a 56-DIP chrome band) whose content is an
    /// <c>Embed.Comp</c> child, over an ordinary recyclable 40-DIP row window. <paramref name="onComponentRoot"/> is the
    /// ONE difference between the two arms: false puts the collapse/sticky binds on the raw <see cref="BoxEl"/> wrapper
    /// (correct), true puts the identical rows on the component's rendered root (the footgun).</summary>
    sealed class CompRootPinProbe : Component
    {
        public const int N = 400;
        public const float HeroH = 200f;
        public const float BandH = 56f;
        public const float RowH = 40f;
        static ColorF RowFill => ColorF.FromRgba(38, 44, 52);

        readonly bool _onCompRoot;
        readonly RepeatLayout _layout = RepeatLayout.Extents(ExtentOf, RowH);

        public CompRootPinProbe(bool onComponentRoot) => _onCompRoot = onComponentRoot;

        static float ExtentOf(int i) => i == 0 ? HeroH : i == 1 ? BandH : RowH;

        /// <summary>The prefix slot's CONTENT — an autonomous component, exactly as Wavee's <c>TableVerticalItem</c> is.
        /// It carries the binds only in the negative arm.</summary>
        sealed class PrefixBody : Component
        {
            readonly float _h;
            readonly FluentGpu.Scroll.Effects.ScrollEffectSpec[] _effects;
            public PrefixBody(float h, FluentGpu.Scroll.Effects.ScrollEffectSpec[] effects) { _h = h; _effects = effects; }
            public override Element Render() => new BoxEl { Height = _h, Fill = RowFill, ScrollEffects = _effects };
        }

        public override Element Render()
            => ItemsView.CreateBound(N,
                scope =>
                {
                    int initial = scope.Index.Peek();          // the prefix never recycles, so this is its identity
                    if (initial > 1) return new BoxEl { Height = RowH, Fill = RowFill };
                    float h = initial == 0 ? HeroH : BandH;
                    // Item 0 = the hero's pin (the PIN half of `.Collapse`); item 1 = the chrome's `.Sticky` at the band.
                    FluentGpu.Scroll.Effects.ScrollEffectSpec[] effects = initial == 0
                        ? [new(FluentGpu.Scroll.Effects.ScrollEffect.Sticky(0f))]
                        : [new(FluentGpu.Scroll.Effects.ScrollEffect.Sticky(BandH))];
                    var empty = Array.Empty<FluentGpu.Scroll.Effects.ScrollEffectSpec>();
                    Element child = Embed.Comp(() => new PrefixBody(h, _onCompRoot ? effects : empty));
                    return _onCompRoot
                        ? child                                                    // ✗ effect on the component's root
                        : new BoxEl { Direction = 1, Height = h, Children = [child], ScrollEffects = effects };   // ✓ raw wrapper
                },
                _layout,
                new ListOptions { PersistentPrefixCount = 2 });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // gate.scroll.sticky-on-content-grid — plan item A (scroll-itch audit 2026-09-22, cause #1): the content transform
    // (ScrollContentPose), the sticky pin shift and the sticky-clip line (ScrollEffectEval, evaluated by the poser at the
    // SNAPPED position) must all derive from the ONE snapped translation (ScrollEffectEval.SnapToDevicePixel) instead of
    // each re-deriving/re-rounding their own — otherwise a pinned
    // header (or its clip line) drifts up to a whole device pixel against the content it rides on at a fractional
    // device scale (125/150/175%).
    //
    // Geometry: a single ScrollEl whose content is [header (PinTop=0, first child, yN=0), clipTarget (tall, second
    // child, ClipTopAtViewport inset = HeaderH, yN=HeaderH — inset and yN cancel, so both the pin's target line and the
    // clip's target line reduce algebraically to the SAME quantity: the snapped content translation T)]. That algebraic
    // cancellation is exactly what the bug broke: with two independently-rounded T's the cancellation is only
    // approximate (±1 device px); with one shared T it is exact to float epsilon.
    //
    // A slow scroll of 0.37-DIP steps (never a whole device pixel at any tested scale) drives the offset by
    // ScrollInput.ScrollBy(immediate) rather than wheel/kernel physics, so every step lands on an arbitrary fractional
    // DIP value — the case that exposes a grid mismatch, not the case (whole-pixel offsets) that hides it.
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class StickyGridProbe : Component
    {
        public const float HeaderH = 48f;
        public const float ClipTargetH = 4000f;   // tall enough to stay "clipping, not fully hidden" for the whole test range

        public override Element Render() => new ScrollEl
        {
            Width = 400f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Height = HeaderH, Fill = ColorF.FromRgba(30, 34, 40) }.Sticky(0f),
                    new BoxEl { Height = ClipTargetH, Fill = ColorF.FromRgba(20, 22, 26) }.StickyClip(HeaderH),
                ],
            },
        };
    }

    static void StickyOnContentGridChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        const int Steps = 200;
        const float StepDip = 0.37f;

        void RunAtScale(float scale)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sticky-grid", new Size2(800, 600), scale));
            window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new StickyGridProbe());
            host.RunFrame();

            var vp = FindScrollNode(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(vp, out var sc0);
            var content = sc0.ContentNode;
            var header = host.Scene.FirstChild(content);
            var clipTarget = host.Scene.NextSibling(header);

            bool everPinned = false;
            float pinnedScreenYBaseline = 0f;
            float maxScreenYDrift = 0f;
            int pinnedFrames = 0;
            int clipCheckedFrames = 0;
            float maxClipDrift = 0f;
            bool geometryOk = true;

            for (int i = 0; i < Steps; i++)
            {
                host.TryGetScrollHandle(vp)!.ScrollBy(StepDip, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
                host.RunFrame();
                host.RunFrame();

                host.Scene.TryGetScroll(vp, out var sc);
                float scaleUsed = host.Scene.DeviceScale;
                float t = -ScrollContentPose.Translate(sc.WindowOrigin, sc.Offset, scaleUsed);   // the snapped OFFSET the content rides

                float contentDy = host.Scene.Paint(content).LocalTransform.Dy;
                bool pinned = (host.Scene.Flags(header) & NodeFlags.StickyPinned) != 0;
                if (pinned)
                {
                    float headerDy = host.Scene.Paint(header).LocalTransform.Dy;
                    float headerLayoutY = host.Scene.Bounds(header).Y;
                    float screenY = contentDy + headerDy + headerLayoutY;
                    if (!everPinned) { everPinned = true; pinnedScreenYBaseline = screenY; }
                    else maxScreenYDrift = MathF.Max(maxScreenYDrift, MathF.Abs(screenY - pinnedScreenYBaseline));
                    pinnedFrames++;
                }

                var clipRect = host.Scene.Paint(clipTarget).ClipRect;
                if (!clipRect.IsInfinite)
                {
                    // Reproduce ApplyStickyClip's own expected line from the OUTSIDE, off the same shared primitive:
                    // inset (HeaderH) and yN (HeaderH, clipTarget's layout Y right after the header) cancel, so the
                    // expected clip top is exactly T.
                    float clipYN = host.Scene.Bounds(clipTarget).Y;
                    if (!Near(clipYN, StickyGridProbe.HeaderH, 0.01f)) geometryOk = false;
                    float expectedTop = t + StickyGridProbe.HeaderH - clipYN;
                    bool fullyHidden = expectedTop >= StickyGridProbe.ClipTargetH;
                    if (!fullyHidden)
                    {
                        maxClipDrift = MathF.Max(maxClipDrift, MathF.Abs(clipRect.Y - expectedTop));
                        clipCheckedFrames++;
                    }
                }
            }

            Check($"gate.scroll.sticky-on-content-grid@{scale:0.00} pinned header screen-Y is constant within 0.01px across a {Steps}-step 0.37-DIP scroll once pinned",
                geometryOk && everPinned && pinnedFrames > Steps / 2 && maxScreenYDrift <= 0.01f,
                $"everPinned={everPinned} pinnedFrames={pinnedFrames} maxScreenYDrift={maxScreenYDrift:0.####}");

            Check($"gate.scroll.sticky-on-content-grid@{scale:0.00} clip top == SnappedTranslation(offset,band,scale)+inset-yN within 0.01px at every checked step",
                geometryOk && clipCheckedFrames > Steps / 2 && maxClipDrift <= 0.01f,
                $"clipCheckedFrames={clipCheckedFrames} maxClipDrift={maxClipDrift:0.####}");
        }

        RunAtScale(1.25f);
        RunAtScale(1.5f);
        RunAtScale(1.75f);
    }

    static void ListConsolidationChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        NodeHandle FindVp(SceneStore s, int count)
        {
            NodeHandle found = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (s.TryGetScroll(n, out var sc) && sc.ItemCount == count) found = n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Visit(c);
            }
            Visit(s.Root);
            return found;
        }
        int LiveChildren(SceneStore s, NodeHandle vp)
        {
            if (vp.IsNull || !s.TryGetScroll(vp, out var sc) || sc.ContentNode.IsNull) return 0;
            int n = 0;
            for (var c = s.FirstChild(sc.ContentNode); !c.IsNull; c = s.NextSibling(c)) n++;
            return n;
        }
        void ScrollTo(AppHost h, HeadlessWindow w, NodeHandle vp, float y)
        {
            // An Immediate plan: the next frame step shows it and the virtualizer realizes the whole present-time window in
            // that same frame; the extra frames let bound signals / keep-alive parking settle before the gate reads.
            h.TryGetScrollHandle(vp)?.ScrollTo(y, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            w.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
            for (int k = 0; k < 8; k++) h.RunFrame();
        }

        // ── gate.list.options-parity: a representative old-arg scenario (selection + invoke + overscan) reproduced via
        //    ListOptions produces the correct realized window + selection behaviour. ────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("lo-parity", new Size2(360, 360), 1f));
            window.Show();
            var model = new SelectionModel();
            var ctl = new ItemsViewController();
            int invoked = -1;
            var probe = new ListOptProbe
            {
                Count = 200, Extent = 40f, Vh = 200f, Bound = false,
                Options = new ListOptions
                {
                    SelectionMode = ItemsSelectionMode.Single, Selection = model, Controller = ctl,
                    IsItemInvokedEnabled = true, OnInvoked = i => invoked = i, Grow = 1f,
                },
            };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            for (int k = 0; k < 6; k++) host.RunFrame();   // settle overscan
            var vp = FindVp(host.Scene, 200);
            host.Scene.TryGetScroll(vp, out var sc);
            int realized = sc.LastRealized - sc.FirstRealized;
            // Options landed: provided model wired through the controller; overscan honored (visible 5 + overscan,
            // bounded ≪ 200); a programmatic selection re-skins with the model this list was given.
            bool modelWired = ReferenceEquals(ctl.Selection, model);
            // Windowed ≪ Count is the invariant: the mount realizes against the height Hint (ItemsView forwards no
            // explicit VirtualListEl.Height ⇒ Hint 1024 ⇒ ~26 visible + overscan), and virtualization never TRIMS a
            // still-covering window, so the steady realized band is bounded but larger than the 200px-viewport minimum.
            bool windowBounded = realized >= 5 && realized < 100;   // ASSERTION: windowed, not the full 200
            model.ItemCount = 200; model.Select(3);
            bool selects = model.IsSelected(3);
            Check("gate.list.options-parity ListOptions reproduces selection-model wiring + overscan-bounded realized window + invoke wiring",
                modelWired && windowBounded && selects,
                $"realized={realized} modelWired={modelWired} selects={selects} invokeWired={(probe.Options!.OnInvoked is not null)}");
        }

        // ── gate.list.visible-range: ListOptions.OnVisibleRange actually REACHES the engine through ItemsView, on BOTH
        //    virtual construction paths (bound RowBind + templated RenderItem). Before this the only way to get at
        //    VirtualListEl.OnVisibleRange was to drop to the raw Virtual.Measured factory, which forfeits selection,
        //    keyboard nav, the recycle pools and the entrance/insertion choreography ItemsView owns — so a viewport-
        //    hydrated long list (fetch extended metadata for the rows the user scrolled to) could not use CreateBound.
        //    The gate pins the two semantics the doc promises: the pair is the REALIZED window (overscan halo included,
        //    NOT the strictly-visible band) and it fires only on a window CHANGE (no consecutive duplicate ranges, and
        //    an idle frame after settle is silent). ─────────────────────────────────────────────────────────────────
        {
            (bool Ok, string Detail) ProbeVisibleRange(bool bound)
            {
                const int N = 400;
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc(bound ? "lo-vr-bound" : "lo-vr-tpl", new Size2(360, 360), 1f));
                window.Show();
                var ranges = new List<(int First, int Last)>();
                var probe = new ListOptProbe
                {
                    Count = N, Extent = 40f, Vh = 200f, Bound = bound,
                    Options = new ListOptions
                    {
                        Grow = 1f,
                        OnVisibleRange = (f, l) => ranges.Add((f, l)),
                    },
                };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
                host.RunFrame();
                for (int k = 0; k < 8; k++) host.RunFrame();   // settle the E4 budget-spread realize waves
                var vp = FindVp(host.Scene, N);
                host.Scene.TryGetScroll(vp, out var sc0);

                // (a) the hook landed on the built VirtualListEl and the last pair IS the realized window.
                bool fired = ranges.Count > 0;
                bool matchesRealized = fired && ranges[^1] == (sc0.FirstRealized, sc0.LastRealized);
                // (b) the halo semantic: the reported band is wider than the ~5 strictly-visible rows of a 200px viewport.
                bool halo = fired && ranges[^1].Last - ranges[^1].First > 5;
                // (c) CHANGE-only: an idle frame over an unmoved window is silent.
                int n0 = ranges.Count;
                host.RunFrame();
                bool idleQuiet = ranges.Count == n0;
                // (d) a window-MOVING scroll delivers the new realized window.
                ScrollTo(host, window, vp, 4_000f);
                host.Scene.TryGetScroll(vp, out var sc1);
                bool moved = sc1.FirstRealized > sc0.FirstRealized;
                bool tracked = moved && ranges.Count > n0 && ranges[^1] == (sc1.FirstRealized, sc1.LastRealized);
                // (e) CHANGE-only across the WHOLE log: no recorded pair repeats its predecessor.
                bool noDupes = true;
                for (int i = 1; i < ranges.Count; i++) noDupes &= ranges[i] != ranges[i - 1];

                return (fired && matchesRealized && halo && idleQuiet && tracked && noDupes,
                        $"{(bound ? "bound" : "tpl")} calls={ranges.Count} last={(fired ? ranges[^1].ToString() : "-")} "
                      + $"realized=[{sc0.FirstRealized},{sc0.LastRealized})->[{sc1.FirstRealized},{sc1.LastRealized}) "
                      + $"match={matchesRealized} halo={halo} idleQuiet={idleQuiet} moved={moved} tracked={tracked} noDupes={noDupes}");
            }

            var boundRes = ProbeVisibleRange(bound: true);
            var tplRes = ProbeVisibleRange(bound: false);
            Check("gate.list.visible-range ListOptions.OnVisibleRange reaches VirtualListEl through BOTH ItemsView paths: the delivered pair is the realized window (overscan included) and it fires only on a window change",
                boundRes.Ok && tplRes.Ok,
                $"{boundRes.Detail} | {tplRes.Detail}");
        }

        // ── gate.list.bound-overlap-recycler: a small contiguous window shift rotates retained slots around the
        //    overlap. Exactly |delta| entering rows rebind; every overlapping logical item keeps its scene root. ───
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("lo-overlap", new Size2(360, 240), 1f));
            window.Show();
            var probe = new BoundOverlapProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            host.RunFrame();
            var vp = FindVp(host.Scene, BoundOverlapProbe.N);

            List<NodeHandle> Roots()
            {
                var roots = new List<NodeHandle>();
                if (!vp.IsNull && host.Scene.TryGetScroll(vp, out var state) && !state.ContentNode.IsNull)
                    for (var root = host.Scene.FirstChild(state.ContentNode); !root.IsNull; root = host.Scene.NextSibling(root))
                        roots.Add(root);
                return roots;
            }

            FrameStats MoveToRow(int row)
            {
                // Place the offset so `row` is the realize window's FIRST row: the window trails the shown offset by the
                // feel's behind floor (MotionFeel.OverscanMinPx), so offset = row·40 + floor + half a row lands the
                // window's leading edge mid-row `row`; the window size stays constant across these moves (none clamps
                // at the content start). An Immediate plan applies in THIS RunFrame's frame step. Unlike the
                // control-level BringIntoView helper this does not invalidate the ItemsView component itself, so the
                // measured frame isolates the engine's slot rotation/rebind path.
                double floor = FluentGpu.Scroll.Diag.ScrollTunables.Current.OverscanMinPx;
                host.TryGetScrollHandle(vp)!.ScrollTo(row * 40.0 + floor + 20.0, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
                return host.RunFrame();
            }

            // Warm the largest signal fan-out once so the runtime queues are at capacity before measuring allocation.
            MoveToRow(500);
            MoveToRow(0);
            MoveToRow(1);
            MoveToRow(0);
            MoveToRow(3);
            MoveToRow(0);

            host.Scene.TryGetScroll(vp, out var initialState);
            var initialRoots = Roots();
            var rootSignals = new Dictionary<NodeHandle, IReadSignal<int>>();
            bool paired = initialRoots.Count > 0;
            for (int i = 0; i < initialRoots.Count; i++)
            {
                int logicalIndex = initialState.FirstRealized + i;
                IReadSignal<int>? match = null;
                for (int j = 0; j < probe.SlotSignals.Count; j++)
                    if (probe.SlotSignals[j].Peek() == logicalIndex)
                    {
                        match = probe.SlotSignals[j];
                        break;
                    }
                if (match is null)
                {
                    paired = false;
                    continue;
                }
                rootSignals[initialRoots[i]] = match;
            }

            Dictionary<int, NodeHandle> Snapshot()
            {
                var map = new Dictionary<int, NodeHandle>();
                foreach (var root in Roots())
                    if (rootSignals.TryGetValue(root, out var signal))
                        map[signal.Peek()] = root;
                return map;
            }

            static bool OverlapPreserved(Dictionary<int, NodeHandle> before, Dictionary<int, NodeHandle> after)
            {
                foreach (var (index, root) in before)
                    if (after.TryGetValue(index, out var next) && next != root)
                        return false;
                return true;
            }

            // ATTACHED slots only (index inside the published window at snapshot time). A slot the pool parked on an
            // earlier shrink keeps its stale index off-window; when a grow takes it back for an entering row its signal
            // changes exactly as a fresh mount's would have been CREATED before the pool existed — an entering row's
            // rebind, not an overlap violation — so it is excluded the same way a brand-new signal always was.
            Dictionary<IReadSignal<int>, int> SignalSnapshot()
            {
                host.Scene.TryGetScroll(vp, out var win);
                var values = new Dictionary<IReadSignal<int>, int>(probe.SlotSignals.Count);
                for (int i = 0; i < probe.SlotSignals.Count; i++)
                {
                    int v = probe.SlotSignals[i].Peek();
                    if (v >= win.FirstRealized && v < win.LastRealized) values[probe.SlotSignals[i]] = v;
                }
                return values;
            }

            int ChangedSignals(Dictionary<IReadSignal<int>, int> before)
            {
                int changed = 0;
                foreach (var (signal, value) in before)
                    if (signal.Peek() != value) changed++;
                return changed;
            }

            int first0 = initialState.FirstRealized;
            int windowSize = initialState.LastRealized - initialState.FirstRealized;
            var map0 = Snapshot();

            var signals0 = SignalSnapshot();
            var forwardFrame = MoveToRow(first0 + 1);
            host.Scene.TryGetScroll(vp, out var forwardState);
            var map1 = Snapshot();
            int forwardChanges = ChangedSignals(signals0);
            bool forward = forwardState.FirstRealized == first0 + 1 &&
                           forwardChanges == 1 &&
                           OverlapPreserved(map0, map1);

            var signals1 = SignalSnapshot();
            var reverseFrame = MoveToRow(first0);
            host.Scene.TryGetScroll(vp, out var reverseState);
            var map2 = Snapshot();
            int reverseChanges = ChangedSignals(signals1);
            bool reverse = reverseState.FirstRealized == first0 &&
                           reverseChanges == 1 &&
                           OverlapPreserved(map1, map2);

            var signals2 = SignalSnapshot();
            var kFrame = MoveToRow(first0 + 3);
            host.Scene.TryGetScroll(vp, out var kState);
            var map3 = Snapshot();
            int kChanges = ChangedSignals(signals2);
            // At most k rebinds: rows parked by the earlier shifts come back still bound to the entering index (the
            // slot pool's exact-match reservation), so the rebind count is an upper bound, never a fixed 3.
            bool shiftK = kState.FirstRealized == first0 + 3 &&
                          kChanges >= 1 && kChanges <= 3 &&
                          OverlapPreserved(map2, map3);

            var signals3 = SignalSnapshot();
            var farFrame = MoveToRow(500);
            host.Scene.TryGetScroll(vp, out var farState);
            int farChanges = ChangedSignals(signals3);
            int farWindowSize = farState.LastRealized - farState.FirstRealized;
            bool far = farState.FirstRealized == 500 && farChanges == farWindowSize;
            Check("gate.list.bound-overlap-recycler ±1/k shifts rebind only entering rows and preserve overlap roots; a no-overlap jump rebinds the full window",
                paired && map0.Count == windowSize && forward && reverse && shiftK && far,
                $"paired={paired} roots/signals={initialRoots.Count}/{probe.SlotSignals.Count} W={windowSize} " +
                $"forward={forward} reverse={reverse} k3={shiftK} far={far} first={first0}->{forwardState.FirstRealized}->{reverseState.FirstRealized}->{kState.FirstRealized}->{farState.FirstRealized} " +
                $"changes={forwardChanges}/{reverseChanges}/{kChanges}/{farChanges} farW={farState.LastRealized - farState.FirstRealized} " +
                $"alloc={forwardFrame.HotPhaseAllocBytes}/{reverseFrame.HotPhaseAllocBytes}/{kFrame.HotPhaseAllocBytes}/{farFrame.HotPhaseAllocBytes}");
        }

        // ── gate.list.bound-zero-alloc: a bound list scrolled over recycled slots stays 0-alloc on phases 6–13 (the
        //    recycling contract re-asserted through ItemsView.CreateBound + ListOptions). ──────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("lo-boundzero", new Size2(360, 360), 1f));
            window.Show();
            var probe = new ListOptProbe { Count = 1000, Extent = 40f, Vh = 240f, Bound = true };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var vp = FindVp(host.Scene, 1000);
            ScrollTo(host, window, vp, 4000f);   // recycle slots over a big jump
            int buildsAfterScroll = probe.Builds;
            // Settle, then a steady frame allocates 0 on the paint phases (slot rebind is a signal write, not a rebuild).
            for (int k = 0; k < 4; k++) host.RunFrame();
            var warm = host.RunFrame();
            var steady = host.RunFrame();
            host.Scene.TryGetScroll(vp, out var sc);
            bool recycledNotRebuilt = probe.Builds == buildsAfterScroll;   // no fresh builds on a steady scrolled frame
            bool zero = steady.HotPhaseAllocBytes == 0;
            Check("gate.list.bound-zero-alloc a bound ItemsView.CreateBound list scrolled far rebinds slots (no rebuild) with 0 hot-phase alloc on a steady frame",
                zero && recycledNotRebuilt && sc.FirstRealized > 0,
                $"{steady.HotPhaseAllocBytes} bytes (warm={warm.HotPhaseAllocBytes}) builds@scroll={buildsAfterScroll} builds@steady={probe.Builds} first={sc.FirstRealized}");
        }

        // ── gate.list.keepalive-slot: a keep-alive row's slot stays bound to its item off-window (state retained); a
        //    plain row's slot recycles; the bounded bucket cap evicts LRU (no leak). ────────────────────────────────
        {
            // (a) keep-alive item 0: its slot parks (index signal stays 0) after scrolling far off-window.
            using var appK = new HeadlessPlatformApp();
            var winK = new HeadlessWindow(new WindowDesc("lo-ka", new Size2(360, 240), 1f));
            winK.Show();
            var kProbe = new ListOptProbe
            {
                Count = 300, Extent = 40f, Vh = 200f, Bound = true, CaptureSig0 = true,
                Options = new ListOptions { KeepAlive = i => i == 0, Grow = 1f },
            };
            using var hostK = new AppHost(appK, winK, new HeadlessGpuDevice(), fonts, strings, kProbe);
            hostK.RunFrame();
            var vpK = FindVp(hostK.Scene, 300);
            var sig0 = kProbe.Sig0;
            ScrollTo(hostK, winK, vpK, 6000f);   // item 0 far off-window
            bool keptBound = sig0 is not null && sig0.Peek() == 0;   // parked: never index-rebound away from its item

            // (b) plain (no keep-alive): the item-0 slot's signal is rebound to a visible far item.
            using var appP = new HeadlessPlatformApp();
            var winP = new HeadlessWindow(new WindowDesc("lo-ka-plain", new Size2(360, 240), 1f));
            winP.Show();
            var pProbe = new ListOptProbe { Count = 300, Extent = 40f, Vh = 200f, Bound = true, CaptureSig0 = true };
            using var hostP = new AppHost(appP, winP, new HeadlessGpuDevice(), fonts, strings, pProbe);
            hostP.RunFrame();
            var vpP = FindVp(hostP.Scene, 300);
            var sig0P = pProbe.Sig0;
            ScrollTo(hostP, winP, vpP, 6000f);
            bool plainRecycled = sig0P is not null && sig0P.Peek() != 0;   // recycled: rebound to a far item

            // (c) bounded bucket + LRU eviction: ALL items keep-alive, cap 3 — a top→bottom sweep must NOT leak slots
            //     (live content children stay bounded ≈ window + cap, not growing toward Count).
            using var appC = new HeadlessPlatformApp();
            var winC = new HeadlessWindow(new WindowDesc("lo-ka-cap", new Size2(360, 240), 1f));
            winC.Show();
            var cProbe = new ListOptProbe
            {
                Count = 200, Extent = 40f, Vh = 200f, Bound = true,
                Options = new ListOptions { KeepAlive = i => true, Grow = 1f },
            };
            using var hostC = new AppHost(appC, winC, new HeadlessGpuDevice(), fonts, strings, cProbe);
            hostC.RunFrame();
            var vpC = FindVp(hostC.Scene, 200);
            for (int step = 1; step <= 20; step++) ScrollTo(hostC, winC, vpC, step * 300f);
            int live = LiveChildren(hostC.Scene, vpC);
            bool bounded = live <= 5 + 3 + 12;   // ~visible(5) + cap(3) + the 200-DIP min-overscan band both sides; NOT leaking toward 200

            Check("gate.list.keepalive-slot keep-alive slot parks bound to its item off-window (state retained); a plain slot recycles; the bucket cap bounds retained slots (LRU-evicted, no leak)",
                keptBound && plainRecycled && bounded,
                $"keptBound={keptBound}(sig0={sig0?.Peek()}) plainRecycled={plainRecycled}(sig0P={sig0P?.Peek()}) capLive={live}");
        }

        // ── gate.list.contenttype-pools: overlapping logical rows retain their roots. Only a newly entering row whose
        //    recycled leaving slot has an incompatible content type rebuilds. ───────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("lo-ctype", new Size2(360, 240), 1f));
            window.Show();
            var probe = new ListOptProbe
            {
                // Vh 160: the realize window is Vh + 2·OverscanMinPx = 560 DIP ⇒ 15 rows at rest — an ODD count, so a
                // 1-row shift trades a leaving row for an entering row of the OTHER parity (the incompatible-type case)
                // while a 2-row shift trades one of each (types preserved).
                Count = 400, Extent = 40f, Vh = 160f, Bound = true,
                ConstantBoundText = true,
                Options = new ListOptions { ContentType = i => i % 2, Grow = 1f },
            };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var vp = FindVp(host.Scene, 400);
            // Settle at a stable window at the REAL viewport (row 40) — clear of the mount-time height-Hint over-realize.
            ScrollTo(host, window, vp, 40 * 40f);
            for (int k = 0; k < 6; k++) host.RunFrame();
            host.Scene.TryGetScroll(vp, out var settledWin);
            int windowRows = settledWin.LastRealized - settledWin.FirstRealized;
            // A 2-row jump preserves each slot's parity (type) => cheap rebind, no rebuild.
            int b0 = probe.Builds;
            ScrollTo(host, window, vp, 42 * 40f);
            int sameTypeBuilds = probe.Builds - b0;
            // A 1-row jump retains every overlap exactly; only the one entering row can require a rebuild.
            int b1 = probe.Builds;
            ScrollTo(host, window, vp, 43 * 40f);
            int crossTypeBuilds = probe.Builds - b1;

            // Repeated parity-preserving two-row shifts are the steady content-type hot path: retained roots rotate and
            // bind without allocating scratch arrays/lists at each boundary.
            long worstSteadyAlloc = 0;
            for (int step = 0; step < 8; step++)
            {
                float y = (45 + step * 2) * 40f;
                host.TryGetScrollHandle(vp)?.ScrollTo(y, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
                var frame = host.RunFrame();
                if (frame.HotPhaseAllocBytes > worstSteadyAlloc) worstSteadyAlloc = frame.HotPhaseAllocBytes;
            }
            Check("gate.list.contenttype-pools overlap rows retain their logical roots; a type-preserving shift rebuilds 0 rows and a type-incompatible entering row rebuilds exactly once",
                windowRows % 2 == 1 && sameTypeBuilds == 0 && crossTypeBuilds == 1 && worstSteadyAlloc == 0,
                $"windowRows={windowRows} sameTypeBuilds={sameTypeBuilds} crossTypeBuilds={crossTypeBuilds} worstSteadyAlloc={worstSteadyAlloc}B");
        }

        // ── gate.list.layout-presets: LinedFlow / SpanGrid / GroupedList reached through RepeatLayout presets render
        //    with the same geometry as the Virtual.* forms (spot-checks on the realized item rects). ───────────────
        {
            (SceneStore scene, NodeHandle content) Build(RepeatLayout layout, int count, Func<int, float>? rowHeight = null)
            {
                var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("lo-presets", new Size2(400, 300), 1f));
                window.Show();
                var probe = new ListOptProbe
                {
                    Count = count, Vw = 400f, Vh = 300f, Bound = false, ExplicitLayout = layout, RowHeightOf = rowHeight,
                    Options = new ListOptions { Selector = SelectorVisual.None, Grow = 1f },
                };
                var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
                host.RunFrame();
                for (int k = 0; k < 4; k++) host.RunFrame();
                var vp = FindVp(host.Scene, count);
                host.Scene.TryGetScroll(vp, out var sc);
                return (host.Scene, sc.ContentNode);
            }

            // LinedFlow: item 0 is a square line-height cell (aspect 1 × 100) at the top-left (the WinUI photo wall).
            var (sLf, cLf) = Build(RepeatLayout.LinedFlow(100f, aspectRatio: static _ => 1f), 60);
            var lf0 = Child(sLf, cLf, 0);
            bool linedFlow = !lf0.IsNull && Near(sLf.Bounds(lf0).W, 100f, 1f) && Near(sLf.Bounds(lf0).H, 100f, 1f) && Near(sLf.Bounds(lf0).Y, 0f, 1f);

            // SpanGrid: item 0 spans all 4 columns ⇒ full cross width (the hero row).
            var (sSg, cSg) = Build(RepeatLayout.SpanGrid(4, 80f, 0f, static i => i == 0 ? 4 : 1), 40);
            var sg0 = Child(sSg, cSg, 0);
            var sg1 = Child(sSg, cSg, 1);
            bool spanGrid = !sg0.IsNull && !sg1.IsNull && sSg.Bounds(sg0).W > sSg.Bounds(sg1).W * 3f && Near(sSg.Bounds(sg0).H, 80f, 1f);

            // GroupedList: index 0 is a header (48h), index 1 a normal item (40h). The measured seam corrects each row
            // to its CONTENT extent, so the header/item content heights must match the layout's seeded estimates.
            var (sGl, cGl) = Build(RepeatLayout.GroupedList(new[] { 0, 20 }, 48f, 40f), 60,
                rowHeight: static i => i == 0 || i == 20 ? 48f : 40f);
            var gl0 = Child(sGl, cGl, 0);
            var gl1 = Child(sGl, cGl, 1);
            bool grouped = !gl0.IsNull && !gl1.IsNull && Near(sGl.Bounds(gl0).H, 48f, 1f) && Near(sGl.Bounds(gl1).H, 40f, 1f);

            Check("gate.list.layout-presets LinedFlow/SpanGrid/GroupedList via RepeatLayout presets render with the Virtual.* geometry (square photo cells / full-width hero span / seeded header extents)",
                linedFlow && spanGrid && grouped,
                $"linedFlow={linedFlow} spanGrid={spanGrid} grouped={grouped}");
        }
    }

    static void D1CollectionHostSizingChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        NodeHandle FindViewport(SceneStore s, int count)
        {
            NodeHandle found = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (s.TryGetScroll(n, out var sc) && sc.ItemCount == count) found = n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Visit(c);
            }
            Visit(s.Root);
            return found;
        }

        // cp1.a — the EXACT gallery ItemsView List preset shape (the former CollectionsMenusPages.cs ListView card): a Width=280 bordered card with NO
        // height anywhere above the list — must size naturally (8 × 44 = 352) and realize all 8 rows at W=280.
        string[] coffees = { "Cappuccino", "Latte", "Espresso", "Macchiato", "Americano", "Mocha", "Flat White", "Cortado" };
        var selected = new Signal<int>(0);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("d1-listview", new Size2(640, 480), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 280, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Padding = new Edges4(0, 4, 0, 4), Children = [ItemsView.List(coffees, selected)],
            },
        });
        host.RunFrame();
        var lv = FindViewport(host.Scene, coffees.Length);
        var lsc = default(ScrollState);
        if (!lv.IsNull) host.Scene.TryGetScroll(lv, out lsc);
        var lvRect = lv.IsNull ? default : host.Scene.AbsoluteRect(lv);
        int lvRows = lv.IsNull ? 0 : host.Scene.ChildCount(lsc.ContentNode);
        var row0 = default(RectF);
        if (lvRows > 0) row0 = host.Scene.Bounds(host.Scene.FirstChild(lsc.ContentNode));
        // The {4,2,4,2} ListViewItem backplate margin now insets the item within its 44 slot (WinUI parity): 280−8=272 wide,
        // 44−4=40 tall. The 44 SLOT stride (content height 8×44=352, viewport) is unchanged — only the backplate insets.
        bool lvOk = !lv.IsNull && Near(lvRect.W, 280f) && Near(lvRect.H, 8 * ItemsView.ListItemExtent)
            && Near(lsc.ViewportW, 280f) && Near(lsc.ViewportH, 352f) && Near(lsc.ContentH, 352f)
            && lvRows == 8 && Near(row0.W, 272f) && Near(row0.H, ItemsView.ListItemExtent - 4f);
        Check("cp1.a — gallery ItemsView List preset (280-wide card, no height above) sizes naturally to 8×44 slots; backplates inset {4,2,4,2} → 272×40",
            lvOk, $"vp={lvRect.W:0}x{lvRect.H:0} viewport={lsc.ViewportW:0}x{lsc.ViewportH:0} content={lsc.ContentH:0} rows={lvRows} row0={row0.W:0}x{row0.H:0}");

        // cp1.b — the gallery ItemsView shape (MiscPages.cs): legacy Create(items, columns:4) in an AUTO-HEIGHT,
        // Start-aligned row (no stretch, no height anywhere). The view must measure to its grid's ContentExtent
        // (2 rows × 80 + 1 gap × 8 = 168) and realize all 8 tiles at the 4-column width ((420 − 3×8)/4 = 99).
        string[] photos = { "Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8" };
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("d1-itemsview", new Size2(640, 480), 1f));
        window2.Show();
        using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 420, Direction = 0, AlignItems = FlexAlign.Start,
                Children = [ItemsView.Create(photos, columns: 4)],
            },
        });
        host2.RunFrame();
        var iv = FindViewport(host2.Scene, photos.Length);
        var isc = default(ScrollState);
        if (!iv.IsNull) host2.Scene.TryGetScroll(iv, out isc);
        var ivRect = iv.IsNull ? default : host2.Scene.AbsoluteRect(iv);
        int tiles = iv.IsNull ? 0 : host2.Scene.ChildCount(isc.ContentNode);
        var tile0 = default(RectF);
        if (tiles > 0) tile0 = host2.Scene.Bounds(host2.Scene.FirstChild(isc.ContentNode));
        const float gridExtent = 2 * 80f + 8f;   // GridVirtualLayout(4, 80, 8).ContentExtent(8) — the legacy demo grid
        bool ivOk = !iv.IsNull && Near(ivRect.W, 420f) && Near(ivRect.H, gridExtent) && Near(isc.ContentH, gridExtent)
            && tiles == 8 && Near(tile0.W, (420f - 3 * 8f) / 4f) && Near(tile0.H, 80f);
        Check("cp1.b — gallery ItemsView (legacy 4-col grid, auto-height Start row) sizes to ContentExtent=168 and realizes 8 tiles",
            ivOk, $"vp={ivRect.W:0}x{ivRect.H:0} content={isc.ContentH:0} tiles={tiles} tile0={tile0.W:0}x{tile0.H:0}");

        // cp1.c — realize-after-layout: a 10k-row ItemsView List preset FILLING a 400px host stays windowed (<40 realized — the
        // Grow gate keeps the hard-viewport path; content = 10k × 44 = 440000); growing the host to 3000px must
        // publish the new viewport AND re-realize the window to cover it in the SAME frame.
        var hostH = new Signal<float>(400f);
        using var app3 = new HeadlessPlatformApp();
        var window3 = new HeadlessWindow(new WindowDesc("d1-grow", new Size2(640, 480), 1f));
        window3.Show();
        using var host3 = new AppHost(app3, window3, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Width = 360, Height = hostH.Value,
                Children = [ItemsView.List(10_000, i => new BoxEl(), grow: 1f)],
            },
        });
        host3.RunFrame();
        var big = FindViewport(host3.Scene, 10_000);
        var bsc0 = default(ScrollState);
        if (!big.IsNull) host3.Scene.TryGetScroll(big, out bsc0);
        int realized0 = big.IsNull ? 0 : host3.Scene.ChildCount(bsc0.ContentNode);
        bool windowed = !big.IsNull && realized0 > 0 && realized0 < 40
            && Near(bsc0.ViewportH, 400f) && Near(bsc0.ContentH, 10_000 * ItemsView.ListItemExtent, 1f);

        hostH.Value = 3000f;   // grow the host — ONE RunFrame must both publish 3000 and re-realize to cover it
        host3.RunFrame();
        var bsc1 = default(ScrollState);
        if (!big.IsNull) host3.Scene.TryGetScroll(big, out bsc1);
        int realized1 = big.IsNull ? 0 : host3.Scene.ChildCount(bsc1.ContentNode);
        bool covered = Near(bsc1.ViewportH, 3000f) && bsc1.FirstRealized == 0
            && bsc1.LastRealized * ItemsView.ListItemExtent >= 3000f && realized1 > realized0 && realized1 < 120;
        Check("cp1.c — 10k rows stay windowed (<40) at 400px; growing the host re-realizes to cover in the SAME frame",
            windowed && covered, $"realized {realized0}→{realized1} viewport {bsc0.ViewportH:0}→{bsc1.ViewportH:0} last={bsc1.LastRealized} content={bsc0.ContentH:0}");
    }

    static void Cp2ConsolidationChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // The realized window's ord-th container for an item index (resting order: ord = index − FirstRealized;
        // mirrors ItemsView.FocusIndex / the displacement seed's ord math). Non-virtual hosts have no scroll state.
        static NodeHandle RealizedRow(SceneStore s, NodeHandle vp, int index)
        {
            if (vp.IsNull || !s.IsLive(vp)) return NodeHandle.Null;
            NodeHandle first; int ord;
            if (s.TryGetScroll(vp, out var sc))
            {
                ord = index - sc.FirstRealized;
                if (ord < 0 || index >= sc.LastRealized) return NodeHandle.Null;
                first = s.FirstChild(sc.ContentNode);
            }
            else { ord = index; first = s.FirstChild(vp); }
            var n = first;
            for (int k = 0; k < ord && !n.IsNull; k++) n = s.NextSibling(n);
            return n;
        }

        // Depth-first structural finder for a selector-chrome part (the SelectorVisuals builders carry no reconciler
        // Key into the scene, so match on geometry/fill rather than Key — more robust than a Key probe anyway).
        static NodeHandle FindBox(SceneStore s, NodeHandle n, Func<NodePaint, bool> pred)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (pred(s.Paint(n))) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                var r = FindBox(s, c, pred);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }

        NodeHandle FindViewport(SceneStore s, int count)
        {
            NodeHandle found = default;
            void Visit(NodeHandle n)
            {
                if (n.IsNull) return;
                if (s.TryGetScroll(n, out var sc) && sc.ItemCount == count) found = n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Visit(c);
            }
            Visit(s.Root);
            return found;
        }

        // ── C1 — cp2.reorder2d: the GridView 2-D reorder math folded into ReorderList (Begin2D/Update2D/OffsetFor2D),
        // proving GridReorder's logic survived the consolidation. Pure unit, no host (scroll/realize-agnostic). ──────
        {
            var rl = new ReorderList { DwellMs = ReorderList.GridDwellMs };
            bool dwell300 = ReorderList.GridDwellMs == 300f && rl.DwellMs == 300f;
            rl.Begin2D(0, 8, columns: 4);                              // 8 tiles, 4 cols → rows of {0,1,2,3},{4,5,6,7}
            bool init = rl.IsActive && rl.DraggedIndex == 0 && rl.Columns == 4 && rl.PendingIndex == 0 && rl.TargetIndex == 0;
            // Drag tile 0 to slot 5 (col 1, row 1): totalDx≈100 → +1 col, totalDy≈100 → +1 row, on a 100×100 grid.
            bool moved = rl.Update2D(100f, 100f, colWidth: 100f, rowStride: 100f) && rl.PendingIndex == 5;
            bool dwellHeld = !rl.Advance(299f) && rl.TargetIndex == 0;
            bool dwellFire = rl.Advance(1f) && rl.TargetIndex == 5;
            // Forward drag 0→5: tiles (0,5] each shift ONE slot toward the vacated source (row-major). Tile 4 sits at
            // (col0,row1) and shifts to slot 3 = (col3,row0): a ROW WRAP — dx = +3 cols, dy = −1 row.
            rl.OffsetFor2D(4, 100f, 100f, out float dx4, out float dy4);
            bool wrap = Near(dx4, 300f) && Near(dy4, -100f);
            // Tile 1 (col1,row0) shifts to slot 0 (col0,row0): one column back, same row.
            rl.OffsetFor2D(1, 100f, 100f, out float dx1, out float dy1);
            bool oneCol = Near(dx1, -100f) && Near(dy1, 0f);
            rl.OffsetFor2D(0, 100f, 100f, out float dxD, out float dyD);   // the dragged tile never displaces
            bool draggedZero = dxD == 0f && dyD == 0f;
            rl.OffsetFor2D(6, 100f, 100f, out float dx6, out float dy6);   // tile 6 is OUTSIDE (0,5] → no shift
            bool outsideZero = dx6 == 0f && dy6 == 0f;
            int dest = rl.Complete();
            bool committed = dest == 5 && !rl.IsActive;
            Check("cp2.reorder2d ReorderList 2-D slot math + 300ms grid dwell + OffsetFor2D row-wrap (GridReorder folded in)",
                dwell300 && init && moved && dwellHeld && dwellFire && wrap && oneCol && draggedZero && outsideZero && committed,
                $"300={dwell300} init={init} moved={moved} dwell={dwellHeld}/{dwellFire} wrap=({dx4:0},{dy4:0}) oneCol=({dx1:0},{dy1:0}) draggedZ={draggedZero} outZ={outsideZero} dest={dest}");
        }

        // ── C2 — cp2.displace: THE core-defect proof. A displacement on a realized row reaches the node as ANIMATED
        // LocalTransform motion (mid-flight → settled), NOT a static jump and NOT discarded behind the autonomous
        // ItemsView boundary. Synthetic itemDisplacement=(0,40) for index 2 + a displacementVersion the test bumps. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-displace", new Size2(420, 360), 1f));
            window.Show();
            var ver = new Signal<int>(0);
            int dispTarget = -1;                                       // armed below (so the mount frame seeds nothing)
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 360, Height = 280,
                    Children =
                    [
                        ItemsView.Create(8,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Selector = SelectorVisual.AccentPill,
                                Reorder = new ReorderOptions { ItemDisplacement = i => i == dispTarget ? (0f, 40f) : (0f, 0f), DisplacementVersion = ver },
                            }),
                    ],
                },
            });
            host.RunFrame();                                          // mount + realize (no displacement armed yet)
            var vp = FindViewport(host.Scene, 8);
            var row2 = RealizedRow(host.Scene, vp, 2);
            bool found = !row2.IsNull;
            float authoredOffset = found ? host.Scene.Paint(row2).LocalTransform.Dy : -1f;   // 0 before any seed
            bool restZero = found && Near(authoredOffset, 0f);

            dispTarget = 2;                                           // arm (0,40) for index 2, then bump the version
            ver.Value = ver.Peek() + 1;
            host.RunFrame();                                          // re-render ItemsView → seed the TranslateY track (elapsed 0)
            for (int i = 0; i < 3; i++) host.RunFrame();             // advance the 250ms track a few 16ms ticks → mid-flight
            float midDy = found ? host.Scene.Paint(row2).LocalTransform.Dy : 0f;
            bool midFlight = midDy > 0.5f && midDy < 40f;            // animated, not an instant jump (no exact ms asserted)
            for (int i = 0; i < 40; i++) host.RunFrame();            // let the 250ms FluentDecelerate track settle
            float settledDy = found ? host.Scene.Paint(row2).LocalTransform.Dy : 0f;
            bool settled = Near(settledDy, 40f, 0.6f);
            // The element carries NO authored OffsetY — the motion lives on the AnimEngine track, not a static offset
            // (so it survives reconcile; the dragged-ghost rule). A non-displaced realized row stays put.
            var row0 = RealizedRow(host.Scene, vp, 0);
            bool neighborStill = !row0.IsNull && Near(host.Scene.Paint(row0).LocalTransform.Dy, 0f, 0.6f);
            Check("cp2.displace ItemsView host-seeds an ANIMATED translate on a displaced realized row (mid-drag part-to-make-room; not a static jump)",
                found && restZero && midFlight && settled && neighborStill,
                $"found={found} rest={authoredOffset:0.0} mid={midDy:0.0} settled={settledDy:0.0} neighbor={neighborStill}");
        }

        // ── C3 — cp2.dragstill: stage A's `HasActiveWork |= Drag.IsActive` keep-alive — a live drag keeps RunFrame
        // pumping so the FrameClock dwell ticker keeps getting frames even on a MOTIONLESS pointer (without it RunFrame
        // would early-return at the !HasActiveWork gate, AppHost.cs:351, and the dwell would freeze). The keep-alive
        // (HasActiveWork true across still frames) + the gesture surviving the still hold to commit on release are fully
        // deterministic; the dwell MATH itself is pinned at the unit level (e5dragdrop.6/.7 Advance). NOTE: this check
        // does NOT read the realized-row displacement, but the live ItemsView.List/Grid preset's displacement IS LIVE:
        // the inline fix has the itemDisplacement closure capture ONLY the memoized ReorderList (OffsetFor/OffsetFor2D
        // read live and return 0 while idle), so it survives the Rule-#2 freeze at the inner autonomous ItemsView's
        // mount — a per-render `reordering` local would instead freeze to false there. Proven on the LIVE preset path by
        // cp2.matrix.listview / cp2.matrix.gridview's lvParted/gvParted assertions; cp2.displace / cp2.scrollslot /
        // cp2.matrix.itemsview supply additional channel proofs. ─────────────────────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-still", new Size2(360, 360), 1f));
            window.Show();
            var drinks = new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" };
            int committedFrom = -1, committedTo = -1;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.List(drinks.Count,
                            i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                            canReorderItems: true, onReorder: (f, t) => { committedFrom = f; committedTo = t; },
                            keyOf: i => drinks[i], itemText: i => drinks[i]),
                    ],
                },
            });
            host.RunFrame();
            var vp = FindViewport(host.Scene, drinks.Count);
            var dragRow = RealizedRow(host.Scene, vp, 0);
            var c = CenterOf(host.Scene, dragRow);
            // Promote a drag on row 0 (PointerDown, then a >4px move) — 0-stamp ⇒ deterministic snap-track ghost.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            // Move the dragged centre (row-0 centre 22) DOWN past row 1's midpoint (66): +50 ⇒ centre 72 > 66 → pending 1.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X, c.Y + 50f), 0, 0));
            host.RunFrame();
            bool promoted = host.HasActiveWork;                       // a live drag → work pending
            // Hold STILL: no further pointer input. HasActiveWork must stay true EVERY frame (the keep-alive) so the
            // dwell ticker keeps getting frames. Space the frames with real time so wall-clock actually advances.
            bool stayedAlive = true;
            for (int i = 0; i < 5; i++)
            {
                System.Threading.Thread.Sleep(110);                  // let Environment.TickCount64 advance past a dwell step
                host.RunFrame();
                if (!host.HasActiveWork) stayedAlive = false;
            }
            // Release: the gesture stayed live through the motionless hold, so it completes and commits the reorder
            // (pending slot 1 at the latest pointer position). HasActiveWork from the drag then drains.
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X, c.Y + 50f), 0, 0));
            host.RunFrame();
            bool committedAfterStill = committedFrom == 0 && committedTo == 1;
            Check("cp2.dragstill HasActiveWork keep-alive holds frames across a MOTIONLESS pointer (no early-return); the gesture survives the still hold + commits on release",
                promoted && stayedAlive && committedAfterStill,
                $"promoted={promoted} alive={stayedAlive} commit=({committedFrom}->{committedTo})");
        }

        // ── C4 — cp2.invokerelease: a promoted drag is a REORDER gesture (commits a move on release); a plain
        // press-release is a CLICK (selects + raises ItemClick + does NOT reorder).
        // NOTE (WinUI-faithful divergence, documented + verified against the real code): this engine selects AND raises
        // ItemClick at the PRESS edge via OnPointerPressed→Tap (ItemContainer.cs:41-45 / SelectorVisuals.AccentPill +
        // ListView.Chrome's interact wrapper — "Win32 lists select on button-down; the visual outcome is identical to
        // WinUI's PointerReleased selection"). So the orders' literal "a drag does not select / drag suppresses the
        // click" cannot hold against press-edge handling — both a click and a grab touch the row at press. What a
        // completed drag uniquely does is take the DRAG path (commit a reorder) and suppress the RELEASE-edge click
        // (InputDispatcher.cs:332-345); a plain release takes the CLICK path. This asserts that truthful discrimination
        // (the spirit of parity:420/430 — a drag is a drag, a click is a click). ─────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-invoke", new Size2(360, 360), 1f));
            window.Show();
            var drinks = new List<string> { "Water", "Juice", "Lemonade", "Soda", "Coffee", "Tea" };
            int clicked = -1, clickCount = 0, reorderFrom = -1, reorderTo = -1;
            var model = new SelectionModel();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.List(drinks.Count,
                            i => new TextEl(drinks[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                            selection: model,
                            onItemClick: i => { clicked = i; clickCount++; },
                            canReorderItems: true, onReorder: (f, t) => { reorderFrom = f; reorderTo = t; }, keyOf: i => drinks[i]),
                    ],
                },
            });
            host.RunFrame();
            var vp = FindViewport(host.Scene, drinks.Count);

            // (a) DRAG row 1 UP past row 0's midpoint (row-1 centre 66 → −50 ⇒ 16 < row-0 mid 22 → pending 0), release:
            // the gesture is a REORDER, committing 1→0 (Complete uses the latest pending, no dwell needed for the drop).
            var r1 = RealizedRow(host.Scene, vp, 1);
            var c1 = CenterOf(host.Scene, r1);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c1, 0, 0));
            host.RunFrame();
            bool dragPressDeferred = !model.IsSelected(1) && clickCount == 0;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c1.X, c1.Y - 50f), 0, 0));   // >4px up → promote, cross row-0 mid
            host.RunFrame();
            bool active = host.HasActiveWork;
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c1.X, c1.Y - 50f), 0, 0));
            host.RunFrame();
            bool dragReordered = reorderFrom == 1 && reorderTo == 0;  // the drag committed a move, not a tap-invoke
            bool dragDidNotInvoke = !model.IsSelected(1) && clickCount == 0;

            // (b) PLAIN click on row 2 (no threshold cross): ItemClick fires + the row selects (press-edge), NO reorder.
            int reorders0From = reorderFrom;
            var r2 = RealizedRow(host.Scene, vp, 2);
            var c2 = CenterOf(host.Scene, r2);
            int clicks1 = clickCount;
            window.QueueInput(new InputEvent(InputKind.PointerDown, c2, 0, 0));
            host.RunFrame();
            bool clickPressDeferred = !model.IsSelected(2) && clickCount == clicks1;
            window.QueueInput(new InputEvent(InputKind.PointerUp, c2, 0, 0));
            host.RunFrame();
            bool plainClicks = clickCount == clicks1 + 1 && clicked == 2 && model.IsSelected(2) && reorderFrom == reorders0From;
            Check("cp2.invokerelease pointer-down defers selection; a promoted drag never selects/invokes; only a clean release selects + raises ItemClick",
                dragPressDeferred && dragDidNotInvoke && clickPressDeferred && active && dragReordered && plainClicks,
                $"dragDeferred={dragPressDeferred} dragSilent={dragDidNotInvoke} clickDeferred={clickPressDeferred} active={active} dragReorder=({reorderFrom}->{reorderTo}) plain={plainClicks} clicked={clicked} sel2={model.IsSelected(2)}");
        }

        // ── C5 — cp2.scrollslot: under a SCROLLED viewport (FirstRealized>0) the displacement seed lands on the
        // CORRECT realized node (index→ord via FirstRealized — the seed's ord math must respect scroll, not blindly
        // use the absolute index). Synthetic displacement on a realized-but-scrolled index. ──────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-scroll", new Size2(360, 360), 1f));
            window.Show();
            var ver = new Signal<int>(0);
            int dispTarget = -1;
            var ctl = new ItemsViewController();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 200,                        // 200 / 40 ⇒ ~5 rows visible over 100 items
                    Children =
                    [
                        ItemsView.Create(100,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Controller = ctl,
                                Selector = SelectorVisual.AccentPill,
                                Reorder = new ReorderOptions { ItemDisplacement = i => i == dispTarget ? (0f, 24f) : (0f, 0f), DisplacementVersion = ver },
                            }),
                    ],
                },
            });
            host.RunFrame();
            ctl.StartBringItemIntoView(40, 0f);                       // scroll so item 40 is at the top edge
            host.RunFrame();
            var vp = FindViewport(host.Scene, 100);
            host.Scene.TryGetScroll(vp, out var sc);
            bool scrolled = sc.FirstRealized >= 30;                   // genuinely past the top (ord != index now)
            int target = sc.FirstRealized + 1;                        // a realized item with ord = 1
            var targetNode = RealizedRow(host.Scene, vp, target);
            bool nodeFound = !targetNode.IsNull;

            dispTarget = target;
            ver.Value = ver.Peek() + 1;
            host.RunFrame();
            for (int i = 0; i < 40; i++) host.RunFrame();            // settle
            float tdy = nodeFound ? host.Scene.Paint(targetNode).LocalTransform.Dy : 0f;
            bool landedOnTarget = nodeFound && Near(tdy, 24f, 0.6f);
            // The ord-0 realized node (a DIFFERENT item index than `target`) must NOT have moved — proving the seed
            // mapped index→ord through FirstRealized rather than smearing onto the wrong (absolute-index) child.
            var ord0 = RealizedRow(host.Scene, vp, sc.FirstRealized);
            bool othersStill = !ord0.IsNull && Near(host.Scene.Paint(ord0).LocalTransform.Dy, 0f, 0.6f);
            Check("cp2.scrollslot displaced offset lands on the correct realized node under a scrolled viewport (index→ord via FirstRealized)",
                scrolled && nodeFound && landedOnTarget && othersStill,
                $"first={sc.FirstRealized} target={target} tdy={tdy:0.0} othersStill={othersStill}");
        }

        // ── C6 — cp2.matrix: the SAME logical reorder (drag item 0 → slot 2) run THREE ways — the List preset, the Grid
        // preset, and ItemsView (synthetic, like a preset) — proving "every capability in every combination". ALL THREE
        // arms pin part-to-make-room: the (i) List preset and (ii) Grid preset arms drive a REAL pointer drag through the LIVE preset
        // wiring and assert (1) the model move 0→2 commits on release, (2) the dragged node's translate is the DRAG's
        // (ghost rides the pointer), (3) a displaced sibling's translate parts toward the vacated slot mid-drag. That
        // third assertion guards the closure-freeze regression: itemDisplacement is a constructor arg of the inner
        // autonomous ItemsView, so it freezes at mount (engine Rule #2) — it must capture only STABLE state (the
        // memoized ReorderList), never a per-render local; a frozen `reordering ? … : (0,0)` once shipped green here
        // while the live displacement was dead. The (iii) ItemsView arm pins the channel itself deterministically. ────
        {
            // (i) ListView ─────────────────────────────────────────────────────────────────────────────────────────
            int lvFrom = -1, lvTo = -1;
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("cp2-mx-lv", new Size2(360, 360), 1f));
                window.Show();
                var items = new List<string> { "A", "B", "C", "D", "E" };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 320, Height = 280,
                        Children =
                        [
                            ItemsView.List(items.Count,
                                i => new TextEl(items[i]) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f },
                                canReorderItems: true, onReorder: (f, t) => { lvFrom = f; lvTo = t; }, keyOf: i => items[i]),
                        ],
                    },
                });
                host.RunFrame();
                var vp = FindViewport(host.Scene, items.Count);
                var dragRow = RealizedRow(host.Scene, vp, 0);
                var c = CenterOf(host.Scene, dragRow);
                window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
                host.RunFrame();
                // Move the dragged centre PAST row 2's midpoint (row-0 centre 22; row-2 mid = 44*2+22 = 110 ⇒ +92 → centre 114 > 110 → pending 2).
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X, c.Y + 92f), 0, 0));
                host.RunFrame();
                // A few frames while held (real time advances the dwell ticker; keep-alive keeps frames coming).
                bool aliveLv = host.HasActiveWork;
                for (int i = 0; i < 3; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); aliveLv &= host.HasActiveWork; }
                // The 200ms dwell committed target=2 during the loop; the LIVE preset's ItemDisplacement channel seeded
                // rows 1,2 with the animated -44 part (MotionTok.ItemPlacement) — SETTLE it, then read row 1.
                // Sleep-free on purpose: AnimClock.Advance treats a post-wait resume as idle and advances by the 1/60
                // quantum, so a Sleep(110) frame buys 16.7ms of ANIMATION time, not 110ms. Three such frames left the
                // track ~50ms in — which only ever read as "parted" because the removed FluentDecelerate literal is
                // extremely front-loaded (79% of travel by t=0.2). Frames, not sleeps, are what settle a track.
                for (int i = 0; i < 40; i++) host.RunFrame();
                var row1Lv = RealizedRow(host.Scene, vp, 1);
                float lvRow1Dy = row1Lv.IsNull ? 0f : host.Scene.Paint(row1Lv).LocalTransform.Dy;
                bool lvParted = !row1Lv.IsNull && lvRow1Dy < -30f;   // a displaced sibling parts up toward -44 on the LIVE path (directional: the glide settles asymptotically)
                // Re-fetch FRESH: the ListView re-renders + re-realizes every drag frame (its FrameClock dwell ticker).
                var dragRowNow = RealizedRow(host.Scene, vp, 0);
                float draggedDy = dragRowNow.IsNull ? 0f : host.Scene.Paint(dragRowNow).LocalTransform.Dy;   // ghost rides the pointer (+~92)
                // TWO-SIDED: the dragged node rides the pointer at the gesture delta (+92) and STAYS there across the
                // held frames — it must NOT run away. Pre-fix, the ItemsView displacement seed planted a Replace
                // TranslateY track on the dragged ghost (DraggedSlot is unwired in every preset) that fought
                // DragController.RetargetFromRest into an unbounded per-frame runaway (hundreds→thousands of px, the
                // ghost flying off the page); the old `draggedDy > 20f` lower bound passed on that runaway. The upper
                // bound is the actual regression guard — the seed now skips the DragGhost-flagged node.
                bool lvGhost = !dragRowNow.IsNull && MathF.Abs(draggedDy - 92f) < 20f;   // ≈ +92 gesture delta, not a runaway
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X, c.Y + 92f), 0, 0));
                host.RunFrame();
                bool lvCommit = lvFrom == 0 && lvTo == 2;
                Check("cp2.matrix.listview drag 0→2: model commits, displaced row parts mid-drag on the LIVE path, dragged node owned by the drag, keep-alive holds",
                    lvCommit && lvGhost && aliveLv && lvParted,
                    $"commit=({lvFrom}->{lvTo}) row1Dy={lvRow1Dy:0.0} draggedDy={draggedDy:0.0} alive={aliveLv}");
            }

            // (ii) GridView (Check selector + ReorderList 2-D) ──────────────────────────────────────────────────────
            int gvFrom = -1, gvTo = -1;
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("cp2-mx-gv", new Size2(480, 360), 1f));
                window.Show();
                var items = new List<string> { "A", "B", "C", "D", "E", "F", "G", "H" };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 440, Height = 320,
                        Children =
                        [
                            ItemsView.Grid(items.Count, i => new BoxEl { Children = [new TextEl(items[i]) { Size = 13f }] },
                                columns: 4, tileHeight: 96f,
                                canReorderItems: true, onReorder: (f, t) => { gvFrom = f; gvTo = t; }, keyOf: i => items[i]),
                        ],
                    },
                });
                host.RunFrame();
                var vp = FindViewport(host.Scene, items.Count);
                var dragTile = RealizedRow(host.Scene, vp, 0);
                var c = CenterOf(host.Scene, dragTile);
                var c2 = CenterOf(host.Scene, RealizedRow(host.Scene, vp, 2));   // target slot 2 (same row, 2 cols right)
                window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
                host.RunFrame();
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c2.X, c2.Y), 0, 0));   // over tile-2 centre
                host.RunFrame();
                bool aliveGv = host.HasActiveWork;
                for (int i = 0; i < 4; i++) { System.Threading.Thread.Sleep(110); host.RunFrame(); aliveGv &= host.HasActiveWork; }   // 300ms grid dwell
                // The 300ms dwell committed target=2; the LIVE preset's channel seeded tiles 1,2 with the animated
                // one-column-left part (MotionTok.ItemPlacement) — SETTLE it, then read tile 1 (a column ≈ 110px).
                // Sleep-free for the same reason as the ListView arm: frames advance the AnimClock, wall time does not.
                for (int i = 0; i < 40; i++) host.RunFrame();
                var tile1Gv = RealizedRow(host.Scene, vp, 1);
                float gvTile1Dx = tile1Gv.IsNull ? 0f : host.Scene.Paint(tile1Gv).LocalTransform.Dx;
                bool gvParted = !tile1Gv.IsNull && gvTile1Dx < -60f;   // a displaced tile parts one column left on the LIVE path
                // Re-fetch FRESH (the GridView re-renders + re-realizes every drag frame via its dwell ticker).
                var dragTileNow = RealizedRow(host.Scene, vp, 0);
                float draggedTx = dragTileNow.IsNull ? 0f : host.Scene.Paint(dragTileNow).LocalTransform.Dx;
                // TWO-SIDED: the dragged tile rides the pointer at the injected horizontal gesture delta (≈2 columns)
                // and STAYS there — it must NOT run away. Pre-fix the displacement seed stomped BOTH tile axes
                // (OffsetFor2D returns (0,0) for the dragged tile, but the seed animated its live translate back to 0),
                // and the runaway was on the X axis for this same-row drag; the old `draggedTx > 20f` lower bound
                // masked it. The upper bound (anchored to the real injected delta) is the regression guard.
                float gvExpectTx = c2.X - c.X;
                bool gvGhost = !dragTileNow.IsNull && MathF.Abs(draggedTx - gvExpectTx) < 16f;   // tile rides the pointer, not a runaway
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c2.X, c2.Y), 0, 0));
                host.RunFrame();
                bool gvCommit = gvFrom == 0 && gvTo == 2;
                Check("cp2.matrix.gridview drag 0→2 (2-D): model commits, displaced tile parts mid-drag on the LIVE path, dragged tile owned by the drag, keep-alive holds",
                    gvCommit && gvGhost && aliveGv && gvParted,
                    $"commit=({gvFrom}->{gvTo}) tile1Dx={gvTile1Dx:0.0} draggedTx={draggedTx:0.0} alive={aliveGv}");
            }

            // (iii) ItemsView wired like a preset (AccentPill + synthetic ReorderList-fed displacement) ──────────────
            using (var app = new HeadlessPlatformApp())
            {
                var window = new HeadlessWindow(new WindowDesc("cp2-mx-iv", new Size2(360, 360), 1f));
                window.Show();
                var rl = new ReorderList { DwellMs = 0f };           // 0 dwell ⇒ target follows pending immediately (test-deterministic)
                var ver = new Signal<int>(0);
                int from = -1, to = -1;
                rl.OnCommit = (f, t) => { from = f; to = t; };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 320, Height = 280,
                        Children =
                        [
                            ItemsView.Create(5,
                                i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                                RepeatLayout.Stack(40f),
                                new ListOptions
                                {
                                    Selector = SelectorVisual.AccentPill,
                                    Reorder = new ReorderOptions { ItemDisplacement = i => { rl.OffsetFor2D(i, 0f, 0f, out _, out _); return (0f, rl.OffsetFor(i)); }, DisplacementVersion = ver },
                                }),
                        ],
                    },
                });
                host.RunFrame();
                // Drive the substrate like a preset would: Begin, Update past row 2's midpoint, commit the target.
                rl.Begin(0, 5, itemExtent: 40f);
                rl.Update(88f);                                       // dragged centre 20+88=108 > mid-2 (100) → pending 2
                rl.Advance(0f);                                       // 0 dwell ⇒ target = 2 now (OffsetFor parts rows 1,2)
                ver.Value = ver.Peek() + 1;
                host.RunFrame();
                for (int i = 0; i < 40; i++) host.RunFrame();        // settle the seeded translate
                var vp = FindViewport(host.Scene, 5);
                var row1 = RealizedRow(host.Scene, vp, 1);
                float row1Dy = row1.IsNull ? 0f : host.Scene.Paint(row1).LocalTransform.Dy;
                bool ivDisplace = !row1.IsNull && Near(row1Dy, -40f, 0.8f);   // row 1 parts up by one extent
                int dest = rl.Complete();
                bool ivCommit = dest == 2 && from == 0 && to == 2;
                Check("cp2.matrix.itemsview drag 0→2 (preset-wired): displacement parts the block + the substrate commits 0→2",
                    ivDisplace && ivCommit, $"row1Dy={row1Dy:0.0} commit=({from}->{to}) dest={dest}");
            }
        }

        // ── C7 — cp2.selectorpresets: each SelectorVisual preset builds the correct selected chrome, exercised through
        // the PUBLIC ItemsView selector switch (the SelectorVisuals builders are `internal` with no InternalsVisibleTo
        // to this project, so the public path is the only reachable seam — and it additionally proves the BCore switch
        // wiring + recyclability, since virtualized realize rejects any non-recyclable container). ───────────────────
        {
            // Build a one-item ItemsView in a given selector + selection state and return its realized container subtree.
            (SceneStore scene, NodeHandle container) BuildSel(SelectorVisual sel, ItemsSelectionMode mode, bool selected)
            {
                var model = new SelectionModel { ItemCount = 1, Mode = mode };
                if (selected) model.Select(0);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("cp2-sel", new Size2(240, 160), 1f));
                window.Show();
                var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 200, Height = 120,
                        Children =
                        [
                            ItemsView.Create(1, i => new BoxEl { Width = 120, Height = 40 }, RepeatLayout.Stack(44f),
                                new ListOptions { SelectionMode = mode, Selection = model, Selector = sel }),
                        ],
                    },
                });
                host.RunFrame();
                var vp = FindViewport(host.Scene, 1);
                var container = RealizedRow(host.Scene, vp, 0);
                // Detach the scene from the (disposing) host: AbsoluteRect/Paint stay valid on the retained store.
                var s = host.Scene;
                host.Dispose();
                return (s, container);
            }

            // The accent pill is uniquely identified on NodePaint by (Fill≈AccentDefault ∧ Corners.TopLeft≈1.5); laid-out
            // W/H live in _bounds (read via AbsoluteRect), not NodePaint.Size, so verify geometry separately.
            static bool IsPill(NodePaint p) => Near(p.Corners.TopLeft, 1.5f) && ColorClose(p.Fill, Tok.AccentDefault, 0.02f);

            // (a) AccentPill selected (Single) → the 3×16 r1.5 accent pill exists.
            var (pillScene, pillRoot) = BuildSel(SelectorVisual.AccentPill, ItemsSelectionMode.Single, selected: true);
            var pill = FindBox(pillScene, pillRoot, IsPill);
            var pillRect = pill.IsNull ? default : pillScene.AbsoluteRect(pill);
            bool pillOk = !pill.IsNull && Near(pillRect.W, 3f) && Near(pillRect.H, 16f);

            // (b) AccentPill selected in MULTIPLE → the pill is SUPPRESSED (checkbox-OR-pill); the inline 20×20 check
            // plate is present (the only BorderWidth≈1 ∧ Corners.TopLeft≈3 node in the AccentPill row).
            var (multiScene, multiRoot) = BuildSel(SelectorVisual.AccentPill, ItemsSelectionMode.Multiple, selected: true);
            var noPill = FindBox(multiScene, multiRoot, IsPill);
            var checkPlate = FindBox(multiScene, multiRoot, p => Near(p.BorderWidth, 1f) && Near(p.Corners.TopLeft, 3f));
            var checkRect = checkPlate.IsNull ? default : multiScene.AbsoluteRect(checkPlate);
            bool multiOk = noPill.IsNull && !checkPlate.IsNull && Near(checkRect.W, 20f) && Near(checkRect.H, 20f);

            // (c) Check selected (Single) → the 2px accent border on the plate + the inset 1px ControlSolid inner ring.
            var (checkScene, checkRoot) = BuildSel(SelectorVisual.Check, ItemsSelectionMode.Single, selected: true);
            bool plateBorder = Near(checkScene.Paint(checkRoot).BorderWidth, 2f)
                && ColorClose(checkScene.Paint(checkRoot).BorderColor, Tok.AccentDefault, 0.02f);
            var innerRing = FindBox(checkScene, checkRoot, p => Near(p.BorderWidth, 1f)
                && ColorClose(p.BorderColor, Tok.FillControlSolid, 0.02f) && Near(p.Corners.TopLeft, 3f));
            bool checkOk = plateBorder && !innerRing.IsNull;

            // (d) FullRow superset selected → NO left pill, but the selected subtle plate reads as the full-bleed fill.
            var (fullScene, fullRoot) = BuildSel(SelectorVisual.FullRow, ItemsSelectionMode.Single, selected: true);
            var fullPill = FindBox(fullScene, fullRoot, IsPill);
            bool fullOk = fullPill.IsNull && ColorClose(fullScene.Paint(fullRoot).Fill, Tok.FillSubtleSecondary, 0.02f);

            // (e) None → a bare container: no pill, no accent ring (app draws its own selection).
            var (noneScene, noneRoot) = BuildSel(SelectorVisual.None, ItemsSelectionMode.Single, selected: true);
            bool noneOk = FindBox(noneScene, noneRoot, IsPill).IsNull
                && FindBox(noneScene, noneRoot, p => Near(p.BorderWidth, 3f)).IsNull;

            Check("cp2.selectorpresets AccentPill/Check/FullRow/None build the correct selected chrome (pill 3×16, Multiple-suppression, Check dual-border, FullRow full-bleed, None bare)",
                pillOk && multiOk && checkOk && fullOk && noneOk,
                $"pill={pillOk} multiSuppress={multiOk} check={checkOk} fullRow={fullOk} none={noneOk}");
        }

        // ── C8 — cp2.treedwell: TreeView consumes the SAME reorder substrate. The dwell + 1s auto-expand advance on a
        // still drag (stage A keep-alive + BTree left the ticker intact); the realized-handle map prune (BT2) keeps the
        // realized-row count bounded across a Roots rebuild; reparent is DEFERRED (default) so a drop commits SIBLING-only. ─
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-tree", new Size2(360, 400), 1f));
            window.Show();
            var rootsA = new List<TreeNode>
            {
                new("Alpha"), new("Bravo"), new("Charlie"), new("Delta"), new("Echo"),
            };
            var roots = new Signal<IReadOnlyList<TreeNode>>(rootsA);
            TreeNode? commitParent = null; int tFrom = -1, tTo = -1; bool reparent = false;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 360,
                    Children =
                    [
                        TreeView.Create(roots.Value, itemTemplate: null, canReorderItems: true,
                            onReorder: (parent, f, t) => { commitParent = parent; tFrom = f; tTo = t; reparent = parent is not null; }),
                    ],
                },
            });
            host.RunFrame();

            // Count realized tree rows via role scan (each TreeViewItem row carries the Button automation role).
            int RowCount() => Roles(host.Scene, AutomationRole.Button).Count;
            int rows0 = RowCount();
            bool initialRows = rows0 >= 5;                            // 5 roots realized as Button-role rows

            // Promote a drag on root 0, hold still through the dwell, assert the keep-alive + that work stays pending.
            var firstRow = Roles(host.Scene, AutomationRole.Button) is { Count: > 0 } rs ? rs[0] : NodeHandle.Null;
            bool aliveDuringDrag = true, dwellAdvanced = false;
            if (!firstRow.IsNull)
            {
                var c = CenterOf(host.Scene, firstRow);
                window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
                host.RunFrame();
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X, c.Y + 60f), 0, 0));   // past sibling 1
                host.RunFrame();
                for (int i = 0; i < 4; i++)
                {
                    System.Threading.Thread.Sleep(110);
                    host.RunFrame();
                    if (!host.HasActiveWork) aliveDuringDrag = false;
                }
                // After the dwell, the projected sibling order moved root 0 down: the FLIP re-rendered the keyed rows,
                // so the row that is now FIRST in the realized column is no longer the originally-dragged node's slot.
                // Proxy: the dragged ghost node carries a non-zero translate (it rides the pointer) — proves the drag
                // engine + dwell are live (the keep-alive pumped frames).
                dwellAdvanced = host.HasActiveWork && MathF.Abs(host.Scene.Paint(firstRow).LocalTransform.Dy) > 0.5f;
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X, c.Y + 60f), 0, 0));
                host.RunFrame();
            }
            bool siblingCommit = !reparent && tFrom == 0 && tTo >= 1 && commitParent is null;   // root-level sibling move

            // BT2 prune: rebuild Roots to a SMALLER set; the realized rows must shrink (no leaked handles inflate it).
            roots.Value = new List<TreeNode> { new("Alpha"), new("Bravo") };
            host.RunFrame();
            int rows1 = RowCount();
            bool prunedBounded = rows1 <= rows0 && rows1 >= 2;       // realized rows track the live projection (bounded)

            Check("cp2.treedwell TreeView reorder: dwell/keep-alive advance on a still drag, sibling-only commit (reparent deferred), realized rows stay bounded after a Roots rebuild",
                initialRows && aliveDuringDrag && dwellAdvanced && siblingCommit && prunedBounded,
                $"rows {rows0}→{rows1} alive={aliveDuringDrag} dwell={dwellAdvanced} commit=({tFrom}->{tTo},parent={(commitParent is null ? "root" : "node")},reparent={reparent})");
        }

        // ── cp2.dragalloc: the displacement SEED is edge-triggered (it fires only when DisplacementVersion changes),
        // so a steady frame with NO version bump is 0-alloc on the hot phases. (A LIVE List/Grid preset reorder is NOT
        // per-frame 0-alloc — its FrameClock dwell ticker re-renders every drag frame to advance the 200/300ms timer,
        // an inherent cost of the live-reorder timer, NOT of the displacement seed; the raw drag-move path's 0-alloc is
        // already pinned by e5dragdrop.8b. So this isolates the SEED on the ItemsView path, which has no dwell ticker:
        // once seeded and settled, an unbumped frame allocates nothing.) ────────────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-alloc", new Size2(360, 360), 1f));
            window.Show();
            var ver = new Signal<int>(0);
            int dispTarget = 2;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.Create(8,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Selector = SelectorVisual.AccentPill,
                                Reorder = new ReorderOptions { ItemDisplacement = i => i == dispTarget ? (0f, 24f) : (0f, 0f), DisplacementVersion = ver },
                            }),
                    ],
                },
            });
            host.RunFrame();
            ver.Value = ver.Peek() + 1;          // seed the displacement track once (edge), then let it fully settle
            for (int i = 0; i < 50; i++) host.RunFrame();
            // A steady frame with NO version bump: the seed effect doesn't re-run, the AnimEngine track has settled and
            // been reclaimed, the ItemsView doesn't re-render — phases 6–13 must allocate 0.
            var warm = host.RunFrame();
            var steady = host.RunFrame();
            bool zero = steady.HotPhaseAllocBytes == 0;
            Check("cp2.dragalloc the displacement seed is edge-triggered — a steady ItemsView frame with no version bump is 0-alloc on phases 6–13",
                zero, $"{steady.HotPhaseAllocBytes} bytes (warm={warm.HotPhaseAllocBytes})");
        }

        // ── cp2.partdelta.alloc (S1b hazard fix): the PartDelta VALUE seam adds NO per-realize allocation in steady
        // state. The delta lambda is a PURE value function (no new/box/Animate/OnRealized — it returns a readonly
        // record struct of nullable property values), resolved ONCE per realize inside chrome construction and applied
        // as `??` init-property swaps — so a settled, steadily-painting frame allocates 0 on phases 6–13 even with a
        // non-null partDelta. This is the CI proof that per-item VARIATION-as-VALUES replaces the banned per-item part
        // modifier without re-introducing the recycled-scroll allocation hazard (docs/guide/control-fidelity.md §6). ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-pd-alloc", new Size2(360, 360), 1f));
            window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.Create(8,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Selector = SelectorVisual.AccentPill,
                                PartDelta = (i, st) => new PartDelta(Fill: i % 2 == 0 ? Tok.FillSubtleSecondary : ColorF.Transparent),
                            }),
                    ],
                },
            });
            host.RunFrame();
            for (int i = 0; i < 50; i++) host.RunFrame();   // let everything settle (no animations seeded by a pure delta)
            var warm = host.RunFrame();
            var steady = host.RunFrame();
            bool zero = steady.HotPhaseAllocBytes == 0;
            Check("cp2.partdelta.alloc a steady ItemsView frame with a custom PartDelta value seam is 0-alloc on phases 6–13 (no per-realize allocation)",
                zero, $"{steady.HotPhaseAllocBytes} bytes (warm={warm.HotPhaseAllocBytes})");
        }

        // ── cp2.partdelta.shape (S1b hazard fix): PartDelta-applied chrome is SHAPE-STABLE across recycling. The delta
        // is applied ONLY as value swaps (fill/corner/etc), NEVER as a child add/remove — so a row recycled to a new
        // item index keeps identical child arity. In a DEBUG build Reconciler.AssertRecycleShapeStable would Debug.Fail
        // (and crash this harness) on a shape-corrupting recycle; the harness running to completion through a forced
        // scroll-recycle IS the guard's green proof. We additionally assert the realized window stays bounded and a
        // recycled row's child arity is unchanged before/after the scroll (the delta never feeds a child-count decision).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp2-pd-shape", new Size2(360, 280), 1f));
            window.Show();
            var ctl = new ItemsViewController();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 320, Height = 280,
                    Children =
                    [
                        ItemsView.Create(200,
                            i => new BoxEl { Children = [new TextEl($"row {i}") { Size = 13f }] },
                            RepeatLayout.Stack(40f),
                            new ListOptions
                            {
                                Controller = ctl,
                                Selector = SelectorVisual.FullRow,
                                Grow = 1f,
                                PartDelta = (i, st) => new PartDelta(
                                    Fill: i % 3 == 0 ? Tok.FillSubtleTertiary : ColorF.Transparent,
                                    Corners: CornerRadius4.All(6f)),
                            }),
                    ],
                },
            });
            host.RunFrame();
            var vp = FindViewport(host.Scene, 200);
            host.Scene.TryGetScroll(vp, out var sc0);
            int realized0 = vp.IsNull ? 0 : host.Scene.ChildCount(sc0.ContentNode);
            // Child arity of the first realized row BEFORE the scroll-recycle (FullRow chrome — fixed-shape plate/content).
            var row0Before = RealizedRow(host.Scene, vp, sc0.FirstRealized);
            int arityBefore = row0Before.IsNull ? -1 : host.Scene.ChildCount(row0Before);
            bool windowed = realized0 > 0 && realized0 < 40;   // 200 rows windowed over a 280px viewport

            // Force several recycles: bring progressively deeper items to the top edge (each re-realizes the window,
            // recycling rows onto new item indices — exactly the recycled-scroll path the hazard guards).
            for (int s = 1; s <= 5; s++) { ctl.StartBringItemIntoView(s * 20, 0f); host.RunFrame(); for (int k = 0; k < 3; k++) host.RunFrame(); }

            host.Scene.TryGetScroll(vp, out var sc1);
            int realized1 = vp.IsNull ? 0 : host.Scene.ChildCount(sc1.ContentNode);
            bool scrolled = sc1.FirstRealized > sc0.FirstRealized;     // genuinely recycled onto deeper item indices
            var row0After = RealizedRow(host.Scene, vp, sc1.FirstRealized);
            int arityAfter = row0After.IsNull ? -1 : host.Scene.ChildCount(row0After);
            // No DEBUG shape-mismatch assert fired (the harness reached here) AND the recycled row's arity is stable
            // AND the window stayed bounded after recycling — the delta re-applies as values, never as structure.
            bool shapeStable = arityBefore > 0 && arityAfter == arityBefore && realized1 > 0 && realized1 < 40;
            Check("cp2.partdelta.shape PartDelta chrome is shape-stable across scroll-recycling (realizer shape guard never trips; recycled-row child arity unchanged)",
                windowed && scrolled && shapeStable,
                $"realized {realized0}→{realized1} arity {arityBefore}→{arityAfter} first {sc0.FirstRealized}→{sc1.FirstRealized} (no Debug.Fail = shape guard passed)");
        }

        // ── cp2.partdelta.epochcache (S1b hazard fix — INDIRECT): TemplateParts.TryApplyCached (the apply-once
        // list-uniform prototype cache) is `internal` and FluentGpu.Dsl carries NO InternalsVisibleTo("FluentGpu.VerticalSlice")
        // (verified — the SelectorVisuals builders are likewise tested only through the PUBLIC ItemsView surface, see
        // cp2.selectorpresets). So we cannot call TryApplyCached directly. We DO verify its invalidation key — the
        // public TemplateParts.Epoch — which is the cache's correctness foundation: a fresh map is epoch 0; EVERY
        // modifier-map mutation (Set<T>, the box indexer set, and a null-removal) bumps the epoch, so changing a
        // list-uniform modifier provably invalidates the (part, epoch)-keyed prototype. The apply-ONCE half is covered
        // by cp2.partdelta.alloc (steady-state 0-alloc through the value seam). NOTE THE LIMITATION: making the
        // apply-once count directly observable would need TryApplyCached promoted to public or an InternalsVisibleTo.
        {
            var parts = new TemplateParts();
            int e0 = parts.Epoch;                                  // fresh map: epoch 0
            parts.Set<BoxEl>("Header", b => b with { Fill = Tok.FillSubtleSecondary });
            int e1 = parts.Epoch;                                  // Set<T> bumps
            parts["Header"] = b => b with { Fill = Tok.FillSubtleTertiary };   // box indexer set bumps (replace modifier)
            int e2 = parts.Epoch;
            parts["Header"] = null;                                // null-removal bumps (invalidation on removal)
            int e3 = parts.Epoch;
            bool monotonic = e0 == 0 && e1 > e0 && e2 > e1 && e3 > e2;
            Check("cp2.partdelta.epochcache TemplateParts.Epoch (the apply-once cache's invalidation key) bumps on every modifier-map mutation — fresh=0, Set/indexer/removal each invalidate",
                monotonic, $"epoch 0={e0} setT={e1} indexer={e2} removal={e3} (TryApplyCached internal; apply-once covered by cp2.partdelta.alloc)");
        }
    }

    /// <summary>Stand-alone AnnotatedScrollBar gates (no list mounted): publish a viewport geometry and a shown offset on
    /// the controller's UNBOUND handle — exactly the signals a bound viewport would drive.</summary>
    static void SetAsbValues(AnnotatedScrollBarController c, float maxOffset, float offset, float viewportLength)
    {
        c.Handle.SetExtent(maxOffset + viewportLength, viewportLength);
        c.Handle.ApplyShown(offset, 0.0, FluentGpu.Scroll.Motion.MotionKind.Idle, settled: true);
    }

    static void D4ScrollBarChecks(StringTable strings)
    {
        // ── ScrollBar.Anatomy: reserved arrow cells, instant signal-bound position, debounced 167ms expand ──
        //
        // The 400/500ms conscious DWELLS are host ONE-SHOTS (UseTimeout on the HostTimerQueue), not a per-frame
        // countdown, so this block drives them the way the engine's other timer gates do: clock.Advance(ms) then
        // host.Paint(0). That is not a shortcut — a pending-but-not-due timer sets NO wake bit (the whole point: the
        // loop idles to the deadline), so RunFrame takes its idle early-out BEFORE Paint, and Paint is both the only
        // place the headless frame clock advances and the only HostTimerQueue.Drain site. The conscious ticker (mounted
        // ONLY while the 167ms/83ms tween or a held page repeat is live) samples that SAME host clock, so Pump advances
        // it one 16ms frame per Paint; Jump moves it in one leap to hit a dwell deadline.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-sb", new Size2(320, 280), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var pos = new FloatSignal(0f);
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                    Children = [ScrollBar.Create(0.25f, pos, p => pos.Value = p, 200f)],
                },
            };
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();   // settle the mount (incl. the 0ms arm)

            void Pump(int n) { for (int i = 0; i < n; i++) { clock.Advance(16f); host.Paint(0); } }   // 16ms frames — the ticker reads the host clock
            void Jump(float ms) { clock.Advance(ms); host.Paint(0); }            // move the host timer clock + drain
            var pollerSb = new System.Text.StringBuilder();
            string Pollers() { pollerSb.Clear(); host.DescribeFrameClockPollers(pollerSb); return pollerSb.ToString().Trim(); }

            var bar = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var column = Child(host.Scene, bar, 2);                 // root ZStack = [track, strip, column(, ticker)]
            var arrowUp = Child(host.Scene, column, 0);             // column = [arrowUp, thumb, grow, arrowDown]
            var thumb = Child(host.Scene, column, 1);
            var barR = host.Scene.AbsoluteRect(bar);
            var t0 = host.Scene.AbsoluteRect(thumb);
            // Length 200 − 2×12 reserved arrow cells → track 176; fraction 0.25 → thumbLen max(30, 44) = 44; travel 132.
            Check("cp4.1 — arrow cells ALWAYS reserved; collapsed thumb 2px, fill right edge inset 3 (stroke-trick math)",
                Near(host.Scene.Bounds(arrowUp).H, 12f) && Near(t0.W, 2f) && Near(t0.H, 44f)
                && Near(t0.Y, barR.Y + 12f) && Near(t0.Right, barR.Right - 3f),
                $"cellH={host.Scene.Bounds(arrowUp).H:0.#} thumb={t0.W:0.#}x{t0.H:0.#} y={t0.Y - barR.Y:0.#} rightInset={barR.Right - t0.Right:0.#}");

            // Position 0 → 0.5 while COLLAPSED: the TransformBind moves the thumb next frame — no 400ms begin-time lag.
            pos.Value = 0.5f;
            host.RunFrame();
            var t1 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.2 — position 0→0.5 moves the thumb the NEXT frame (no expand-delay on position; width untouched)",
                Near(t1.Y, barR.Y + 12f + 66f, 1f) && Near(t1.W, 2f),
                $"y={t1.Y - barR.Y:0.#} (expect 78) w={t1.W:0.#}");

            // Hover the lane: nothing changes immediately (the 400ms dwell debounces the expand)…
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(barR.X + 6f, barR.Y + 100f), 0, 0));
            host.RunFrame();
            bool noInstantExpand = Near(host.Scene.AbsoluteRect(thumb).W, 2f);

            // ── gate.scroll.sb-dwell-quiet (leg 1: the EXPAND dwell) ───────────────────────────────────────────────
            // 10 pumped frames inside the 400ms dwell: nothing on screen changes, so nothing may re-render and NOTHING
            // may hold a FrameClock.Tick subscription. This is the regression the one-shot exists for — the measured
            // `[wake] frameClockPoller=500 | sole: frameClockPoller=244` was this component counting down a timer.
            int dwellRenders = 0;
            for (int i = 0; i < 10; i++) { clock.Advance(20f); dwellRenders += host.Paint(0).ComponentsRendered; }
            int dwellPollers = host.FrameClockPollerCount;
            string dwellPollerLine = Pollers();
            bool expandDwellQuiet = dwellRenders == 0 && dwellPollers == 0
                && !dwellPollerLine.Contains("ScrollBar", StringComparison.Ordinal)
                && Near(host.Scene.AbsoluteRect(thumb).W, 2f);   // 200ms in: still collapsed, still waiting

            // …the deadline itself: 90% of the 400ms begin is still collapsed (200ms already spent above).
            Jump(159f);
            bool notAt90Pct = Near(host.Scene.AbsoluteRect(thumb).W, 2f);
            Jump(45f);                                          // 404ms — the one-shot pops, the tween mounts
            int flipPollers = host.FrameClockPollerCount;       // the ticker is live ONLY now, for the 167ms tween
            Pump(14);                                           // 14 × 16ms = 224ms > 167ms
            var t2 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.3 — lane dwell 400ms then 167ms expand: thumb 2px→6px, right edge stays anchored (inset 3)",
                noInstantExpand && Near(t2.W, 6f) && Near(t2.Right, barR.Right - 3f),
                $"instantExpand={!noInstantExpand} w={t2.W:0.#} rightInset={barR.Right - t2.Right:0.#}");
            Check("cp4.4 — hovering changes neither thumb Y/length nor the track (no geometry jump from arrow cells)",
                Near(t2.Y, t1.Y) && Near(t2.H, t1.H) && Near(host.Scene.Bounds(arrowUp).H, 12f),
                $"y={t2.Y - barR.Y:0.#} len={t2.H:0.#} cellH={host.Scene.Bounds(arrowUp).H:0.#}");
            Check("cp4.5 — expanded chrome faded IN (arrow opacity 1 — the 83ms linear fade after the same begin time)",
                Near(host.Scene.Paint(arrowUp).Opacity, 1f, 0.02f),
                $"opacity={host.Scene.Paint(arrowUp).Opacity:0.00}");

            // The tween has settled → the ticker unmounted itself, so the REVEALED-and-settled bar is as quiet as the
            // dwell was. (The old per-frame stepper could not distinguish the two: it polled through both.)
            int settledRenders = 0;
            for (int i = 0; i < 10; i++) settledRenders += host.Paint(0).ComponentsRendered;
            bool settledQuiet = settledRenders == 0 && host.FrameClockPollerCount == 0;

            // Leave the bar: the contract begins after 500ms and plays 167ms; the chrome fades back out.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(barR.Right + 80f, barR.Y + 100f), 0, 0));
            host.RunFrame();

            // ── gate.scroll.sb-dwell-quiet (leg 2: the CONTRACT dwell, after the reveal fade completed) ────────────
            int hideRenders = 0;
            for (int i = 0; i < 10; i++) { clock.Advance(20f); hideRenders += host.Paint(0).ComponentsRendered; }
            int hidePollers = host.FrameClockPollerCount;
            string hidePollerLine = Pollers();
            bool contractDwellQuiet = hideRenders == 0 && hidePollers == 0
                && !hidePollerLine.Contains("ScrollBar", StringComparison.Ordinal)
                && Near(host.Scene.AbsoluteRect(thumb).W, 6f);   // 200ms in: still expanded, still waiting

            Check("gate.scroll.sb-dwell-quiet the conscious 400/500ms dwells produce NO frames: across 10 pumped frames inside the expand dwell, inside the contract dwell, and on a revealed+settled bar, ComponentsRendered is 0 and FrameClockPollerCount is 0 (no ScrollBar poller is named in the [wake] census) — a wall-clock wait rides the host one-shot, never FrameClock.Tick",
                expandDwellQuiet && settledQuiet && contractDwellQuiet,
                $"expandDwell renders={dwellRenders} pollers={dwellPollers} line=\"{dwellPollerLine}\"; "
                + $"settled renders={settledRenders} pollers={host.FrameClockPollerCount}; "
                + $"contractDwell renders={hideRenders} pollers={hidePollers} line=\"{hidePollerLine}\"");

            // …the 500ms contract deadline: 90% of it is still expanded (200ms already spent above).
            Jump(249f);
            bool hideNotAt90Pct = Near(host.Scene.AbsoluteRect(thumb).W, 6f);
            Jump(55f);                                          // 504ms — the one-shot pops
            Pump(14);
            var t3 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.6 — leave contracts to 2px after the 500ms begin; chrome fades out",
                Near(t3.W, 2f) && Near(host.Scene.Paint(arrowUp).Opacity, 0f, 0.02f),
                $"w={t3.W:0.#} opacity={host.Scene.Paint(arrowUp).Opacity:0.00}");
            Check("gate.scroll.sb-dwell-deadline the dwell fires ON the host clock and not a frame earlier: at 90% of the 400ms expand begin the thumb is still 2px and at 90% of the 500ms contract begin it is still 6px; the frame the one-shot pops is the FIRST frame a FrameClock.Tick poller exists (exactly one — the 167ms tween stepper)",
                notAt90Pct && hideNotAt90Pct && flipPollers == 1,
                $"expand@90%={notAt90Pct} contract@90%={hideNotAt90Pct} pollersAtFlip={flipPollers}");
            Check("cp4.7 — conscious ticker unmounts: the frame loop idles once settled", !host.HasActiveWork);
        }

        // ── The dwell one-shot's RE-ARM edges: a lane re-entry restarts the full begin time, and a re-hover mid-fade
        //    re-reveals FROM the live eased width (interruption continuity) rather than snapping back. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-sb-rearm", new Size2(320, 280), 1f));
            window.Show();
            var pos = new FloatSignal(0f);
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                    Children = [ScrollBar.Create(0.25f, pos, p => pos.Value = p, 200f)],
                },
            };
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root, frameTime: clock);
            host.RunFrame();
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();

            var bar = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var column = Child(host.Scene, bar, 2);
            var thumb = Child(host.Scene, column, 1);
            var barR = host.Scene.AbsoluteRect(bar);
            float W() => host.Scene.AbsoluteRect(thumb).W;
            void Pump(int n) { for (int i = 0; i < n; i++) { clock.Advance(16f); host.Paint(0); } }   // 16ms frames — the ticker reads the host clock
            void Jump(float ms) { clock.Advance(ms); host.Paint(0); }
            void Enter() { window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(barR.X + 6f, barR.Y + 100f), 0, 0)); host.RunFrame(); }
            void Exit() { window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(barR.Right + 80f, barR.Y + 100f), 0, 0)); host.RunFrame(); }

            // (c) Re-entering the lane RESTARTS the expand dwell — the banked time is only carried across a
            // same-dispatch strip↔arrow crossing (where the host clock has not moved), never across a real frame.
            Enter();
            Jump(300f);                       // 300ms of the 400ms expand dwell banked
            Exit();                           // collapsed already ⇒ nothing pending ⇒ the one-shot is cancelled
            Jump(10f);
            Enter();                          // a FRESH 400ms begins here
            Jump(300f);                       // 610ms since the first enter, 300ms since the re-enter
            bool notFlippedOnOldClock = Near(W(), 2f);
            Jump(110f);                       // 410ms since the re-enter → the one-shot pops
            Pump(14);
            bool flippedOnNewClock = Near(W(), 6f);
            Check("gate.scroll.sb-dwell-rearm leaving and re-entering the lane restarts the FULL 400ms expand begin on the host one-shot (610ms of wall time since the first enter does not expand; 410ms since the re-entry does)",
                notFlippedOnOldClock && flippedOnNewClock,
                $"atOldDeadline={W():0.##} flipped={flippedOnNewClock}");

            // (d) Re-hover DURING the contract fade re-reveals: the 400ms dwell re-arms, and the flip retargets from
            // the LIVE eased width, so the bar never snaps back to 2px on its way out and back.
            Exit();
            Jump(505f);                       // the contract one-shot pops → the 167ms fade-out starts
            Pump(2);                          // 32ms into the 167ms contract tween — mid-flight, not settled
            float midFade = W();
            bool midFadeIsMidFlight = midFade > 2.05f && midFade < 5.95f;
            Enter();                          // re-reveal mid-fade
            Jump(405f);                       // the re-armed 400ms expand begin pops
            float atReveal = W();
            bool noSnapBack = atReveal > 2.05f;   // retargeted from the live value, not from the collapsed 2px
            Pump(14);
            bool backToExpanded = Near(W(), 6f);
            Check("gate.scroll.sb-reveal-mid-fade a lane re-entry during the 167ms contract fade re-arms the expand one-shot and the flip retargets from the LIVE eased width — the bar re-reveals to 6px without ever snapping back to the collapsed 2px",
                midFadeIsMidFlight && noSnapBack && backToExpanded,
                $"midFade={midFade:0.##} atReveal={atReveal:0.##} final={W():0.##}");
            Check("gate.scroll.sb-rearm-idle the re-arm sequence leaves the loop idle: no ticker, no armed dwell", !host.HasActiveWork);
        }

        // ── gate.scroll.sb-refresh-invariant: the conscious ticker times the held track-press repeat and the 167ms
        //    expand on the HOST clock, never a per-frame constant. At a 240 Hz frame step (4ms) a fixed 16ms/tick ran
        //    them 4× fast: the 500ms RepeatButton delay elapsed in 32 frames (~128ms), so an ordinary click paged twice. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-sb-hz", new Size2(320, 280), 1f));
            window.Show();
            var pos = new FloatSignal(0f);
            int pages = 0;
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                    Children = [ScrollBar.Create(0.25f, pos, p => { pages++; pos.Value = p; }, 200f)],
                },
            };
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root, frameTime: clock);
            host.RunFrame();
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();

            var bar = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var thumb = Child(host.Scene, Child(host.Scene, bar, 2), 1);
            var barR = host.Scene.AbsoluteRect(bar);
            void Frames240(int n) { for (int i = 0; i < n; i++) { clock.Advance(4f); host.Paint(0); } }

            // Hover then press-and-hold the track 2px above the down-arrow cell (below the thumb until the very end).
            var pt = new Point2(barR.X + 6f, barR.Bottom - 14f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0));
            host.RunFrame();
            int atPress = pages;                    // the press-edge page
            Frames240(110);                         // 440ms of 4ms frames: inside the 500ms delay; the 400ms dwell flipped ~40ms ago
            int inDelay = pages;
            float midTween = host.Scene.AbsoluteRect(thumb).W;
            Frames240(20);                          // 520ms: the delay elapsed → exactly ONE repeat (the next is due at 550ms)
            int afterDelay = pages;
            Check("gate.scroll.sb-refresh-invariant at a 240 Hz frame step the held track press waits the full 500ms RepeatButton delay (1 page at 440ms, 2 at 520ms) and the 167ms expand is still mid-flight 40ms after its flip — the conscious ticker samples the host clock, not 16ms per frame",
                atPress == 1 && inDelay == 1 && afterDelay == 2 && midTween > 2.5f && midTween < 5.5f,
                $"press={atPress} @440ms={inDelay} @520ms={afterDelay} widthAt+40ms={midTween:0.##}");
        }

        // ── AnnotatedScrollBar: 44px right-rail template geometry + jump/step interactions ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-asb", new Size2(360, 340), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var controller = new AnnotatedScrollBarController();
            SetAsbValues(controller, 800f, 200f, 200f);
            float requested = float.NaN;
            var lastKind = (AnnotatedScrollBarScrollKind)255;
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                    Width = 360f, Height = 340f,
                    Children =
                    [
                        new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Width = 200f, Height = 280f, Fill = Tok.FillSubtleSecondary },
                        AnnotatedScrollBar.Create(controller, new AnnotatedScrollBarOptions
                        {
                            Height = 280f,
                            Labels =
                            [
                                new AnnotatedScrollBarLabel(40f, "A"),
                                new AnnotatedScrollBarLabel(500f, "M"),
                                new AnnotatedScrollBarLabel(960f, "Z"),
                            ],
                            TickOffsets = [250f, 500f, 750f],
                            DetailLabelAtOffset = _ => new AnnotatedScrollBarLabel(0f, "Now"),
                            Scrolling = (to, kind) => { lastKind = kind; requested = to; SetAsbValues(controller, 800f, to, 200f); return true; },
                        }),
                    ],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var asb = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var asbR = host.Scene.AbsoluteRect(asb);
            var btnUp = Child(host.Scene, asb, 0);                  // root column = [btnUp, rail, btnDown]
            var rail = Child(host.Scene, asb, 1);
            var railR = host.Scene.AbsoluteRect(rail);
            var thumb = Child(host.Scene, host.Scene.LastChild(rail), 0);   // last rail layer = the live-thumb row
            var thumbR = host.Scene.AbsoluteRect(thumb);
            var asbWrap = host.Scene.Parent(asb);
            var row = host.Scene.Parent(asbWrap);
            var neighbor = Child(host.Scene, row, 0);
            if (neighbor == asbWrap) neighbor = Child(host.Scene, row, 1);
            float asbW0 = asbR.W, neighborW0 = neighbor.IsNull ? 0f : host.Scene.AbsoluteRect(neighbor).W;
            // Height 280 − 2×16 button cells → rail 248; offset 200 / max 800 → thumb Y = .25 × (248−3).
            Check("cp4.8 — AnnotatedScrollBar hugs the 44px LabelsGridMinWidth (no full-width panel)",
                Near(asbR.W, 44f, 0.6f), $"w={asbR.W:0.#}");
            Check("cp4.9 — 30×3 accent thumb uses RailMetrics' scroll-range mapping",
                Near(thumbR.W, 30f) && Near(thumbR.H, 3f) && Near(thumbR.Right, railR.Right, 0.6f)
                && Near(thumbR.Y, railR.Y + new RailMetrics(0f, 800f, 200f, 248f, 3f).ScrollOffsetToThumbTop(200f), 1f),
                $"thumb={thumbR.W:0.#}x{thumbR.H:0.#} rightGap={railR.Right - thumbR.Right:0.#} y={thumbR.Y - railR.Y:0.#}");
            var mLabel = FindTextNode(host.Scene, strings, asb, "M");
            var mR = mLabel.IsNull ? default(RectF) : host.Scene.AbsoluteRect(mLabel);
            Check("cp4.10 — label text right-aligns to the labels-column edge through the default element factory",
                !mLabel.IsNull && Near(mR.Right, railR.Right, 1.5f),
                $"rightGap={railR.Right - mR.Right:0.#} y={mR.Y - railR.Y:0.#}");

            // Hover the rail → the ghost preview row stays mounted (always-on tip is an Opacity bind, not a remount).
            int layers0 = host.Scene.ChildCount(rail);
            var tip = Child(host.Scene, rail, 4); // labels, ticks, tooltip rail, ghost, tip, thumb
            var tipInner = Child(host.Scene, tip, 0);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(railR.X + 22f, railR.Y + 60f), 0, 0));
            host.RunFrame();
            var asbHover = host.Scene.AbsoluteRect(asb);
            var neighborHover = host.Scene.AbsoluteRect(neighbor);
            var tipR = host.Scene.AbsoluteRect(tipInner);
            float tipLocalY = tipR.Y - railR.Y;
            Check("cp4.11 — rail hover keeps the compositor ghost mounted", host.Scene.ChildCount(rail) == layers0,
                $"layers {layers0}→{host.Scene.ChildCount(rail)}");
            Check("gate.scroll.annotated-tip-compact hover keeps the 44px footprint, a compact flag, and pointer-following Y",
                Near(asbHover.W, asbW0, 0.6f) && Near(asbHover.W, 44f, 0.6f)
                && neighborW0 > 40f && Near(neighborHover.W, neighborW0, 0.6f)
                && Near(tipR.H, 40f, 2f)
                && tipR.H < railR.H * 0.5f
                && Near(host.Scene.Paint(tip).Opacity, 1f, 0.05f)
                && Near(tipLocalY, 60f - 40f * 0.5f, 3f),
                $"asbW {asbW0:0.#}→{asbHover.W:0.#} neighborW {neighborW0:0.#}→{neighborHover.W:0.#} tipH={tipR.H:0.#} tipY={tipLocalY:0.#} op={host.Scene.Paint(tip).Opacity:0.00}");
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(railR.X + 22f, railR.Y + 180f), 0, 0));
            host.RunFrame();
            float tipLocalY2 = host.Scene.AbsoluteRect(tipInner).Y - railR.Y;
            bool followed = tipLocalY2 > tipLocalY + 40f;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
            host.RunFrame();
            Check("gate.scroll.annotated-tip-follow leaving the rail clears the flag (opacity 0) after Y tracked the pointer",
                followed && Near(host.Scene.Paint(tip).Opacity, 0f, 0.05f),
                $"y1={tipLocalY:0.#} y2={tipLocalY2:0.#} followed={followed} op={host.Scene.Paint(tip).Opacity:0.00}");

            // Rail click-to-jump: local Y 124 of 248 → scroll-range position 0.5, kind=Click; thumb and ghost agree.
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(railR.X + 22f, railR.Y + 124f), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(railR.X + 22f, railR.Y + 124f), 0, 0));
            host.RunFrame();
            thumb = Child(host.Scene, host.Scene.LastChild(rail), 0);
            var thumbR2 = host.Scene.AbsoluteRect(thumb);
            Check("cp4.12 — rail click maps to absolute offset 400, requests it through the controller, and the thumb follows the same geometry next frame",
                Near(requested, 400f, 0.01f) && lastKind == AnnotatedScrollBarScrollKind.Click
                && Near(thumbR2.Y, railR.Y + new RailMetrics(0f, 800f, 200f, 248f, 3f).ScrollOffsetToThumbTop(400f), 1f),
                $"pos={controller.Offset.Peek():0.00} kind={lastKind} y={thumbR2.Y - railR.Y:0.#}");

            // Top (increment) ScrollButton: a 16×16 right-aligned transparent cell stepping by SmallChange (0.05).
            var b = host.Scene.AbsoluteRect(btnUp);
            Check("cp4.13 — ScrollButtons are 16×16 right-aligned cells",
                Near(b.W, 16f) && Near(b.H, 16f) && Near(b.Right, asbR.Right, 0.6f),
                $"btn={b.W:0.#}x{b.H:0.#} rightGap={asbR.Right - b.Right:0.#}");
            window.QueueInput(new InputEvent(InputKind.PointerDown, CenterOf(host.Scene, btnUp), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, CenterOf(host.Scene, btnUp), 0, 0));
            host.RunFrame();
            Check("cp4.14 — top button is VerticalDecrement and steps by −viewport/8",
                Near(requested, 375f, 0.01f) && lastKind == AnnotatedScrollBarScrollKind.DecrementButton,
                $"pos={controller.Offset.Peek():0.00} kind={lastKind}");

            var ghostLayer = Child(host.Scene, rail, 3); // labels, ticks, tooltip rail, ghost, tip, thumb
            var start = new Point2(railR.X + 22f, railR.Y + 80f);
            var outside = new Point2(railR.X + 22f, railR.Bottom + 80f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, start, 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, outside, 0, 0)); host.RunFrame();
            bool heldPreview = Near(host.Scene.Paint(ghostLayer).Opacity, 1f);
            window.QueueInput(new InputEvent(InputKind.PointerUp, outside, 0, 0)); host.RunFrame();
            Check("gate.scroll.annotated-drag-continuity captured OnDrag tracks beyond the rail; pointer exit cannot kill the preview mid-drag and requests clamp at max",
                Near(requested, 800f) && lastKind == AnnotatedScrollBarScrollKind.Drag && heldPreview,
                $"requested={requested:0.#} kind={lastKind} heldPreview={heldPreview}");
            Check("gate.scroll.annotated-tip-drag-release drag-release after an outside move clears the detail flag",
                Near(host.Scene.Paint(tip).Opacity, 0f, 0.05f),
                $"op={host.Scene.Paint(tip).Opacity:0.00}");
        }

        // ── Label collision with measured content heights + snapshot stability across Labels identity changes ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-asb-labels", new Size2(360, 340), 1f));
            window.Show();
            var controller = new AnnotatedScrollBarController();
            SetAsbValues(controller, 800f, 0f, 200f);
            AnnotatedScrollBarLabel[] MakeLabels() =>
            [
                new AnnotatedScrollBarLabel(0f, "January"),
                new AnnotatedScrollBarLabel(30f, "February"),
                new AnnotatedScrollBarLabel(60f, "March"),
                new AnnotatedScrollBarLabel(400f, "June"),
                new AnnotatedScrollBarLabel(800f, "December"),
            ];
            var labelsSig = new Signal<IReadOnlyList<AnnotatedScrollBarLabel>>(MakeLabels());
            using var host = new AppHost(app, window, new HeadlessGpuDevice(),
                new HeadlessFontSystem(strings), strings, new AsbLabelsHost
                {
                    Controller = controller,
                    Labels = labelsSig,
                    Height = 280f,
                });
            host.RunFrame();
            for (int i = 0; i < 8; i++) host.RunFrame(); // ContentLayoutDebounceMs=50; headless step 16ms

            var asb = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var btnUp = Child(host.Scene, asb, 0);
            var rail = Child(host.Scene, asb, 1);
            var btnDown = Child(host.Scene, asb, 2);
            var labelsGrid = Child(host.Scene, rail, 0);
            int n = host.Scene.ChildCount(labelsGrid);
            var visible = new bool[n];
            var compact = true;
            for (int i = 0; i < n; i++)
            {
                var node = Child(host.Scene, labelsGrid, i);
                visible[i] = host.Scene.Paint(node).Opacity > 0.5f;
                compact &= host.Scene.Bounds(node).H < 40f;
            }
            var jan = FindTextNode(host.Scene, strings, asb, "January");
            var dec = FindTextNode(host.Scene, strings, asb, "December");
            var janWrap = jan.IsNull ? NodeHandle.Null : host.Scene.Parent(jan);
            var decWrap = dec.IsNull ? NodeHandle.Null : host.Scene.Parent(dec);
            var janR = janWrap.IsNull ? default(RectF) : host.Scene.AbsoluteRect(janWrap);
            var decR = decWrap.IsNull ? default(RectF) : host.Scene.AbsoluteRect(decWrap);
            var upR = host.Scene.AbsoluteRect(btnUp);
            var downR = host.Scene.AbsoluteRect(btnDown);
            bool firstLast = n >= 5 && visible[0] && visible[n - 1];
            bool middlesCollapsed = n >= 5 && !visible[1];
            bool clearOfButtons = !jan.IsNull && !dec.IsNull && !janR.Overlaps(upR) && !decR.Overlaps(downR);
            Check("gate.scroll.annotated-label-measured-collapse first/last stay visible, overlapping middles collapse, labels stay clear of the 16×16 end buttons",
                compact && firstLast && middlesCollapsed && clearOfButtons,
                $"visible={string.Join(',', visible.Select(v => v ? 1 : 0))} compact={compact} jan={janR} dec={decR} up={upR} down={downR}");

            var before = (bool[])visible.Clone();
            labelsSig.Value = MakeLabels(); // new instance, same contents (Wavee re-render)
            host.RunFrame();
            bool flashed = false;
            int visibleCount = 0;
            for (int i = 0; i < n; i++)
            {
                var node = Child(host.Scene, labelsGrid, i);
                bool now = host.Scene.Paint(node).Opacity > 0.5f;
                if (now) visibleCount++;
                if (now != before[i]) flashed = true;
            }
            Check("gate.scroll.annotated-label-snapshot-stable a replacement Labels array keeps the previous collision layout until debounce; never all-visible",
                !flashed && visibleCount < n && visibleCount > 0,
                $"flashed={flashed} visibleCount={visibleCount}/{n}");
        }

        // ── External AnnotatedScrollBar wheel routing: the rail is a SIBLING, so it must bridge raw wheel DIP to the
        // viewport controller instead of relying on scroll-ancestor discovery (there is none on this hit path). ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-wheel-external-rail", new Size2(320, 280), 1f));
            window.Show();
            var controller = new AnnotatedScrollBarController();
            var wheelKind = (AnnotatedScrollBarScrollKind)255;
            float wheelTarget = float.NaN;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(),
                new HeadlessFontSystem(strings), strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Direction = 0, Width = 320f, Height = 240f,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                                Children =
                                [
                                    ItemsView.Create(80,
                                        i => new BoxEl { Height = 40f, Children = [new TextEl($"row {i}") { Size = 13f }] },
                                        RepeatLayout.Stack(40f),
                                        new ListOptions
                                        {
                                            SelectionMode = ItemsSelectionMode.None,
                                            Selector = SelectorVisual.None,
                                            Grow = 1f,
                                            Scroll = new ScrollOptions
                                            {
                                                SuppressScrollBar = true,
                                                Handle = controller.Handle,
                                            },
                                        }),
                                ],
                            },
                            AnnotatedScrollBar.Create(controller, new AnnotatedScrollBarOptions
                            {
                                Height = 240f,
                                Scrolling = (target, kind) =>
                                {
                                    wheelTarget = target;
                                    wheelKind = kind;
                                    return true;
                                },
                            }),
                        ],
                    },
                });
            host.RunFrame();
            host.RunFrame();
            var vp = FindScrollable(host.Scene, host.Scene.Root);
            var bar = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var barR = host.Scene.AbsoluteRect(bar);
            var onRail = new Point2(barR.X + barR.W * 0.5f, barR.Y + barR.H * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, onRail, 0, 0));
            host.RunFrame();
            var rail = Child(host.Scene, bar, 1);
            var ghost = Child(host.Scene, rail, 3);
            bool ghostShown = host.Scene.Paint(ghost).Opacity > 0.5f;
            window.QueueInput(WheelEvent(onRail, 0, 0, ScrollDelta: 48f, TimestampMs: 16));
            host.RunFrame();
            host.RunFrame();
            host.Scene.TryGetScroll(vp, out var midSc);
            // Animated chase, not a snap: two frames in, the offset is en route — moving but short of the target.
            bool glides = midSc.OffsetY > 0.5f && midSc.OffsetY < 47.5f;
            float settled = float.NaN;
            for (int i = 0; i < 120; i++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var s);
                settled = s.OffsetY;
                if (MathF.Abs(settled - 48f) < 0.5f) break;
            }
            bool ghostHeld = host.Scene.Paint(ghost).Opacity > 0.5f;
            Check("gate.scroll.annotated-wheel-external-rail wheel input over a sibling AnnotatedScrollBar runs the cancelable Wheel request, rides the WheelAnimating chase to the accumulated DIP target, and keeps the stationary pointer's ghost preview",
                glides && Near(settled, 48f, 0.5f)
                && wheelKind == AnnotatedScrollBarScrollKind.Wheel && Near(wheelTarget, 48f, 0.5f)
                && ghostShown && ghostHeld,
                $"bar={barR} mid={midSc.OffsetY:0.#} settled={settled:0.#} request={wheelKind}/{wheelTarget:0.#} ghost={ghostShown}->{ghostHeld}");
        }

        // ── W-bug-1: the detail flyout must re-resolve when the scroll geometry changes UNDER a stationary pointer.
        // Momentum/extent correction moves the offset↔rail mapping while the dispatcher never re-fires hover for an
        // unchanged node — so the control's own geometry-tracking effect is the only thing keeping the date truthful. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-asb-tip-live", new Size2(360, 340), 1f));
            window.Show();
            var controller = new AnnotatedScrollBarController();
            SetAsbValues(controller, 800f, 0f, 200f);   // scroll range 800
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                        Width = 360f, Height = 340f,
                        Children =
                        [
                            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Width = 200f, Height = 280f },
                            AnnotatedScrollBar.Create(controller, new AnnotatedScrollBarOptions
                            {
                                Height = 280f,
                                DetailLabelAtOffset = offset => new AnnotatedScrollBarLabel(0f,
                                    offset < 900f ? "Early" : "Late"),
                            }),
                        ],
                    },
                });
            host.RunFrame();
            var asb = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var rail = Child(host.Scene, asb, 1);
            var railR = host.Scene.AbsoluteRect(rail);
            // Hover at 60% of the rail: decode ≈ 0.6 × range 800 ≈ 480 ⇒ "Early".
            window.QueueInput(new InputEvent(InputKind.PointerMove,
                new Point2(railR.X + 22f, railR.Y + railR.H * 0.6f), 0, 0));
            host.RunFrame();
            host.RunFrame();
            bool early = !FindTextNode(host.Scene, strings, asb, "Early").IsNull;
            // Extent correction with the pointer STATIONARY: range 800 → 2400, the same rail Y now names ≈ 1440 ⇒ "Late".
            SetAsbValues(controller, 2400f, 0f, 200f);
            host.RunFrame();
            host.RunFrame();
            bool late = !FindTextNode(host.Scene, strings, asb, "Late").IsNull;
            Check("gate.scroll.annotated-tip-tracks-live-geometry an extent correction under a STATIONARY hover re-resolves the detail flyout without a pointer move",
                early && late, $"early={early} late={late}");
        }

        // Last reachable date at the rail END: offset == Max places the thumb at ThumbTravel; hovering the bottom
        // of the track resolves that last header (not a leftover viewport band with a different date).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-asb-last-end", new Size2(360, 340), 1f));
            window.Show();
            var controller = new AnnotatedScrollBarController();
            SetAsbValues(controller, 800f, 800f, 200f);
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(20f),
                        Width = 360f, Height = 340f,
                        Children =
                        [
                            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Width = 200f, Height = 280f },
                            AnnotatedScrollBar.Create(controller, new AnnotatedScrollBarOptions
                            {
                                Height = 280f,
                                Labels =
                                [
                                    new AnnotatedScrollBarLabel(0f, "Aug"),
                                    new AnnotatedScrollBarLabel(800f, "May 15"),
                                ],
                                TickOffsets = [0f, 800f],
                                DetailLabelAtOffset = offset => new AnnotatedScrollBarLabel(0f,
                                    offset < 400f ? "Aug flag" : "May 15 flag"),
                            }),
                        ],
                    },
                });
            host.RunFrame();
            var asb = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var rail = Child(host.Scene, asb, 1);
            var railR = host.Scene.AbsoluteRect(rail);
            var thumb = Child(host.Scene, host.Scene.LastChild(rail), 0);
            var thumbR = host.Scene.AbsoluteRect(thumb);
            var expect = new RailMetrics(0f, 800f, 200f, 248f, 3f);
            bool thumbAtEnd = Near(thumbR.Y, railR.Y + expect.ScrollOffsetToThumbTop(800f), 1f)
                && Near(expect.ScrollOffsetToThumbTop(800f), expect.ThumbTravel);
            window.QueueInput(new InputEvent(InputKind.PointerMove,
                new Point2(railR.X + 22f, railR.Y + railR.H - 1f), 0, 0));
            host.RunFrame();
            host.RunFrame();
            bool lastDate = !FindTextNode(host.Scene, strings, asb, "May 15 flag").IsNull;
            Check("gate.scroll.annotated-last-header-at-end last-header offset equal to MaximumOffset puts the thumb at the rail bottom, and a pointer at the track end resolves that last date",
                thumbAtEnd && lastDate, $"thumbY={thumbR.Y - railR.Y:0.#}/{expect.ThumbTravel:0.#} lastDate={lastDate}");
        }

        // NaN Height fills the parent row's cross size. Recents wraps the bar in a stretch COLUMN so the
        // ComponentEl's mirrored Grow=1 fills height instead of stealing HStack width from the list.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-asb-stretch", new Size2(400, 500), 1f));
            window.Show();
            var controller = new AnnotatedScrollBarController();
            SetAsbValues(controller, 800f, 0f, 200f);
            const float SlotH = 420f;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(),
                new HeadlessFontSystem(strings), strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Direction = 0, Width = 300f, Height = SlotH, AlignItems = FlexAlign.Stretch,
                        Children =
                        [
                            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f },
                            new BoxEl
                            {
                                Direction = 1, AlignSelf = FlexAlign.Stretch, MinHeight = 0f,
                                Children =
                                [
                                    AnnotatedScrollBar.Create(controller, new AnnotatedScrollBarOptions
                                    {
                                        Height = float.NaN,
                                        Labels =
                                        [
                                            new AnnotatedScrollBarLabel(0f, "Aug"),
                                            new AnnotatedScrollBarLabel(800f, "May"),
                                        ],
                                    }),
                                ],
                            },
                        ],
                    },
                });
            host.RunFrame();
            host.RunFrame();
            var asb = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var asbR = host.Scene.AbsoluteRect(asb);
            Check("gate.scroll.annotated-stretch NaN Height fills a stretch column beside a Grow=1 list",
                Near(asbR.H, SlotH, 1f), $"h={asbR.H:0.#} slot={SlotH}");
        }

        // ── Wheel-through header (S5): Element.WheelTarget glides the LIST from a header laid out ABOVE it. The header
        //    names the list's IScrollController as its WheelTarget; one device notch over the header must (a) travel
        //    exactly WheelNotchDip (the feel's per-notch DIP) — the same distance a notch over the rows gets, (b) arrive as a
        //    GLIDE (the kernel's Driven|Wheel chase: the offset changes over ≥ 3 frames, never in one), (c) leave the
        //    header itself where it was laid out, and (d) keep the header clickable (no pass-through, no OnPointerWheel).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp4-wheel-header", new Size2(320, 280), 1f));
            window.Show();
            var rail = new AnnotatedScrollBarController();   // its Handle is the list's viewport handle AND the header's WheelTarget
            int headerClicks = 0;
            const float Band = 48f;
            var headerFill = ColorF.FromRgba(0x2A, 0x3B, 0x4C);
            using var host = new AppHost(app, window, new HeadlessGpuDevice(),
                new HeadlessFontSystem(strings), strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 280f, Height = 240f, Direction = 1, ClipToBounds = true,
                        Children =
                        [
                            new BoxEl
                            {
                                Height = Band, Fill = headerFill, WheelTarget = rail.Handle,
                                Children = [new BoxEl { Height = Band, Grow = 1f, OnClick = () => headerClicks++ }],
                            },
                            ItemsView.Create(40,
                                i => new BoxEl { Height = 40f, Children = [new TextEl($"row {i}") { Size = 13f }] },
                                RepeatLayout.Stack(40f),
                                new ListOptions
                                {
                                    SelectionMode = ItemsSelectionMode.None,
                                    Selector = SelectorVisual.None,
                                    Grow = 1f,
                                    Scroll = new ScrollOptions { Handle = rail.Handle },
                                }),
                        ],
                    },
                });
            host.RunFrame();
            host.RunFrame();
            var vp = FindScrollable(host.Scene, host.Scene.Root);
            var header = FindFillNode(host.Scene, host.Scene.Root, headerFill);
            var headerBefore = host.Scene.AbsoluteRect(header);
            host.Scene.TryGetScroll(vp, out var sc0);
            float start = sc0.OffsetY;
            float expected = (float)FluentGpu.Scroll.Diag.ScrollTunables.Current.WheelNotchDip;
            var onHeader = new Point2(140f, 24f);
            window.QueueInput(WheelEvent(onHeader, 0, 0, WheelNotch: 1f, TimestampMs: 16));
            // Follow the glide frame by frame: count the frames that moved the offset and stop once it has rested for
            // three consecutive frames after moving (bounded — a notch settles in ~10 frames in this scene).
            int movingFrames = 0, restFrames = 0;
            float prev = start, firstStep = 0f;
            for (int i = 0; i < 90 && restFrames < 3; i++)
            {
                host.RunFrame();
                host.Scene.TryGetScroll(vp, out var sci);
                float step = sci.OffsetY - prev;
                if (MathF.Abs(step) > 0.01f)
                {
                    if (movingFrames == 0) firstStep = step;
                    movingFrames++;
                    restFrames = 0;
                }
                else if (movingFrames > 0) restFrames++;
                prev = sci.OffsetY;
            }
            host.Scene.TryGetScroll(vp, out var scEnd);
            float travelled = scEnd.OffsetY - start;
            var headerAfter = host.Scene.AbsoluteRect(header);
            bool headerStill = Near(headerAfter.Y, headerBefore.Y, 0.01f) && Near(headerAfter.H, headerBefore.H, 0.01f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, onHeader, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, onHeader, 0, 0));
            host.RunFrame();
            Check("gate.scroll.wheel-through-sticky-overlay a header laid out above its list names the list's ScrollHandle as WheelTarget: one notch over the header glides the list over ≥ 3 frames (not in one) to exactly WheelNotchDip, the header stays put, and it still clicks",
                sc0.LineDip > 0f && expected > 0f
                && movingFrames >= 3 && MathF.Abs(firstStep) < expected - 0.5f
                && Near(travelled, expected, 1f)
                && headerStill && headerClicks == 1,
                $"lineDip={sc0.LineDip:0.#} expected={expected:0.#} travelled={travelled:0.##} movingFrames={movingFrames} firstStep={firstStep:0.##} headerY={headerBefore.Y:0.#}→{headerAfter.Y:0.#} clicks={headerClicks}");
        }

        // ── GroupedListVirtualLayout sticky vs IndexAt after a programmatic jump (Issue 2 candidate a) ──
        {
            var gl = new GroupedListVirtualLayout([0, 8, 16], headerExtent: 32f, itemEstimate: 48f);
            const int n = 24; const float cross = 300f;
            _ = gl.ContentExtent(n, cross);
            float jumped = gl.OffsetOf(16, cross) + 20f;
            gl.Window(n, cross, 200f, jumped, 2, out _, out _);
            int sticky = gl.StickyHeaderIndexAt(jumped);
            int at = gl.IndexAt(jumped, cross);
            Check("gate.scroll.grouped-sticky-after-jump StickyHeaderIndexAt after ContentExtent/Window at a later-group offset returns that group's header, not 0",
                sticky == 16 && sticky != 0 && at >= 16,
                $"sticky={sticky} indexAt={at} jumped={jumped:0.#} hdr16={gl.OffsetOf(16, cross):0.#}");
        }
    }
    static void MeasuredTailExtentChecks(StringTable strings)
    {
        const float Cross = 300f;
        const float Expected = MeasuredTailExtentProbe.N * MeasuredTailExtentProbe.RowH;
        var fonts = new HeadlessFontSystem(strings);

        static float SumExtents(MeasuredStackVirtualLayout layout)
        {
            _ = layout.ContentExtent(MeasuredTailExtentProbe.N, Cross);
            float sum = 0f;
            for (int i = 0; i < MeasuredTailExtentProbe.N; i++)
                sum += layout.ItemRect(i, Cross).H;
            return sum;
        }

        static string ExtentTail(MeasuredStackVirtualLayout layout, int around)
        {
            _ = layout.ContentExtent(MeasuredTailExtentProbe.N, Cross);
            var parts = new List<string>(8);
            int lo = Math.Max(0, around - 2), hi = Math.Min(MeasuredTailExtentProbe.N - 1, around + 2);
            for (int i = lo; i <= hi; i++)
                parts.Add($"[{i}]={layout.ItemRect(i, Cross).H:0.##}");
            parts.Add($"last={layout.ItemRect(MeasuredTailExtentProbe.N - 1, Cross).H:0.##}");
            return string.Join(' ', parts);
        }

        static bool ScrollToEnd(AppHost host, MeasuredTailExtentProbe probe)
        {
            var vp = probe.Controller.Viewport;
            if (vp.IsNull) return false;
            // Walk the list so every row is realized at least once (estimate tail must be replaced, not just the
            // end window). Then snap the last row to the viewport end.
            for (int i = 0; i < MeasuredTailExtentProbe.N; i += 4)
            {
                probe.Controller.StartBringItemIntoView(i, alignmentRatio: 0f, animate: false);
                host.RunFrame();
            }
            probe.Controller.StartBringItemIntoView(MeasuredTailExtentProbe.N - 1, alignmentRatio: 1f, animate: false);
            for (int i = 0; i < 8; i++) host.RunFrame();
            return host.Scene.TryGetScroll(vp, out _);
        }

        static bool LastRowFlushAtBottom(in ScrollState sc, MeasuredStackVirtualLayout layout)
        {
            float lastTop = layout.OffsetOf(MeasuredTailExtentProbe.N - 1, Cross);
            float lastExtent = layout.ItemRect(MeasuredTailExtentProbe.N - 1, Cross).H;
            float maxOff = MathF.Max(0f, sc.ContentH - sc.ViewportH);
            return Near(sc.OffsetY, maxOff, 1.5f)
                && Near(lastTop + lastExtent, sc.ContentH, 1.5f)
                && Near(sc.OffsetY + sc.ViewportH, lastTop + lastExtent, 1.5f);
        }

        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("measured-tail-no-expand", new Size2(360, 760), 1f));
            window.Show();
            var probe = new MeasuredTailExtentProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame(); host.RunFrame();
            bool scrolled = ScrollToEnd(host, probe);
            var vp = probe.Controller.Viewport;
            host.Scene.TryGetScroll(vp, out var sc);
            float content = probe.Layout.ContentExtent(MeasuredTailExtentProbe.N, Cross);
            float sum = SumExtents(probe.Layout);
            bool extentExact = Near(content, Expected, 0.5f) && Near(sc.ContentH, Expected, 0.5f) && Near(sum, Expected, 0.5f);
            bool noOverscanTail = sc.LastRealized == MeasuredTailExtentProbe.N && sc.ItemCount == MeasuredTailExtentProbe.N;
            bool lastFlush = LastRowFlushAtBottom(in sc, probe.Layout);
            Check("gate.scroll.measured-tail-extent-no-expand a measured bound list scrolled to the end publishes ContentExtent == Σ row heights (every realized row replaced its estimate; overscan past ItemCount adds nothing) and the last row sits flush at the viewport bottom",
                scrolled && extentExact && noOverscanTail && lastFlush,
                $"scrolled={scrolled} content={sc.ContentH:0.##}/{content:0.##} sum={sum:0.##} want={Expected:0} vp={sc.ViewportH:0.##} off={sc.OffsetY:0.##} max={sc.ContentH - sc.ViewportH:0.##} lastRealized={sc.LastRealized}/{sc.ItemCount} {ExtentTail(probe.Layout, MeasuredTailExtentProbe.N - 1)}");
        }

        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("measured-tail-after-collapse", new Size2(360, 760), 1f));
            window.Show();
            var probe = new MeasuredTailExtentProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame(); host.RunFrame();

            probe.Expanded.Value = MeasuredTailExtentProbe.ExpandIndex;
            for (int i = 0; i < 20; i++) host.RunFrame();   // settle the 200ms enter
            float openExtent = probe.Layout.ItemRect(MeasuredTailExtentProbe.ExpandIndex, Cross).H;
            bool opened = openExtent > MeasuredTailExtentProbe.RowH + 8f;

            probe.Expanded.Value = -1;
            host.RunFrame();   // remove → exit orphan
            for (int i = 0; i < 40; i++)
            {
                host.RunFrame();
                if (host.Scene.OrphanCount == 0) break;
            }
            for (int i = 0; i < 4; i++) host.RunFrame();   // one last measure after reclaim
            float closedExtent = probe.Layout.ItemRect(MeasuredTailExtentProbe.ExpandIndex, Cross).H;
            bool orphansGone = host.Scene.OrphanCount == 0;
            bool rowRetracted = Near(closedExtent, MeasuredTailExtentProbe.RowH, 0.5f);

            bool scrolled = ScrollToEnd(host, probe);
            var vp = probe.Controller.Viewport;
            host.Scene.TryGetScroll(vp, out var sc);
            float content = probe.Layout.ContentExtent(MeasuredTailExtentProbe.N, Cross);
            float sum = SumExtents(probe.Layout);
            bool extentExact = Near(content, Expected, 0.5f) && Near(sc.ContentH, Expected, 0.5f) && Near(sum, Expected, 0.5f);
            bool lastFlush = LastRowFlushAtBottom(in sc, probe.Layout);
            Check("gate.scroll.measured-tail-extent-after-collapse expanding then collapsing a measured row (SizeMode.Reflow exit orphan finishes) retracts that row's ExtentTable entry to the closed height, ContentExtent == Σ row heights, and the last row sits flush at the viewport bottom",
                opened && orphansGone && rowRetracted && scrolled && extentExact && lastFlush,
                $"opened={opened} openH={openExtent:0.##} orphansGone={orphansGone} rowH={closedExtent:0.##} content={sc.ContentH:0.##}/{content:0.##} sum={sum:0.##} want={Expected:0} vp={sc.ViewportH:0.##} off={sc.OffsetY:0.##} max={sc.ContentH - sc.ViewportH:0.##} lastRealized={sc.LastRealized} {ExtentTail(probe.Layout, MeasuredTailExtentProbe.ExpandIndex)}");
        }

        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("measured-tail-offscreen-collapse", new Size2(360, 760), 1f));
            window.Show();
            var probe = new MeasuredTailExtentProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame(); host.RunFrame();

            probe.Expanded.Value = MeasuredTailExtentProbe.ExpandIndex;
            for (int i = 0; i < 20; i++) host.RunFrame();
            float openExtent = probe.Layout.ItemRect(MeasuredTailExtentProbe.ExpandIndex, Cross).H;
            bool opened = openExtent > MeasuredTailExtentProbe.RowH + 8f;

            probe.Controller.StartBringItemIntoView(MeasuredTailExtentProbe.N - 1, alignmentRatio: 1f, animate: false);
            for (int i = 0; i < 8; i++) host.RunFrame();
            bool offscreen = !probe.Controller.IsItemRealized(MeasuredTailExtentProbe.ExpandIndex);

            probe.Expanded.Value = -1;
            for (int i = 0; i < 8; i++) host.RunFrame();
            float leftover = probe.Layout.ItemRect(MeasuredTailExtentProbe.ExpandIndex, Cross).H;
            bool cachedTall = leftover > MeasuredTailExtentProbe.RowH + 8f;

            bool corrected = probe.Controller.CorrectMeasuredExtent(probe.Layout,
                MeasuredTailExtentProbe.ExpandIndex, MeasuredTailExtentProbe.RowH);
            host.RunFrame(); host.RunFrame();
            float closedExtent = probe.Layout.ItemRect(MeasuredTailExtentProbe.ExpandIndex, Cross).H;
            bool retracted = Near(closedExtent, MeasuredTailExtentProbe.RowH, 0.5f);

            bool scrolled = ScrollToEnd(host, probe);
            host.Scene.TryGetScroll(probe.Controller.Viewport, out var sc);
            float content = probe.Layout.ContentExtent(MeasuredTailExtentProbe.N, Cross);
            bool extentExact = Near(content, Expected, 0.5f) && Near(sc.ContentH, Expected, 0.5f);
            bool lastFlush = LastRowFlushAtBottom(in sc, probe.Layout);
            Check("gate.scroll.measured-tail-extent-offscreen-collapse an UNREALIZED measured row keeps its drawer extent until CorrectMeasuredExtent retracts it; after that ContentExtent == Σ row heights and the last row sits flush at the viewport bottom",
                opened && offscreen && cachedTall && corrected && retracted && scrolled && extentExact && lastFlush,
                $"opened={opened} offscreen={offscreen} leftover={leftover:0.##} corrected={corrected} rowH={closedExtent:0.##} content={sc.ContentH:0.##}/{content:0.##} want={Expected:0} lastFlush={lastFlush} {ExtentTail(probe.Layout, MeasuredTailExtentProbe.ExpandIndex)}");
        }
    }

    static void ScrollHoverVirtualCheck(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-hover-virt", new Size2(320, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ScrollHoverVirtualProbe());
        host.RunFrame();   // mount + layout (SmoothScroll defaults false ⇒ a wheel writes the offset synchronously)

        var vp = host.Scene.Root;
        var pt = new Point2(60f, 20f);   // fixed point 20px down (top row of the 200px viewport), inside the 180px-wide row

        // Scroll PAST the overscan buffer so FirstRealized is off zero — then the ordinal under a fixed screen point is
        // pinned at `overscan` (first = floor(offset/extent) − overscan, so index_under_point − first == overscan for any
        // offset), i.e. the SAME child HANDLE stays under the point across every re-realize. Warm the rebind + re-eval path.
        for (int i = 0; i < 12; i++) { window.QueueInput(WheelEvent(pt, 0, 0, 40f)); host.RunFrame(); }
        for (int i = 0; i < 8; i++) host.RunFrame();   // settle bars/anim back to rest

        // Establish hover on the row currently under the fixed point.
        window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0)); host.RunFrame();
        var a = host.Input.HitTest(pt);
        bool hovA = !a.IsNull && (host.Scene.Flags(a) & NodeFlags.Hovered) != 0;
        host.Scene.TryGetScroll(vp, out var scA);
        int firstA = scA.FirstRealized;

        // MEASURED: scroll DOWN several rows with the pointer NOT moving — enough to force a re-realize. The overlap
        // recycler keeps each logical row's root, so the newly visible row under this fixed point has a different handle.
        // RefreshHoverAfterScroll must move Hovered from the departing row to that new hit.
        WheelDip(host, window, pt, 200f);
        var b = host.Input.HitTest(pt);
        host.Scene.TryGetScroll(vp, out var scB);
        bool reRealized = scB.FirstRealized > firstA;                                   // the realize window shifted (rebind ran)
        bool logicalHitChanged = !b.IsNull && b != a;                                   // a different logical row now occupies the point
        bool hovB = !b.IsNull && (host.Scene.Flags(b) & NodeFlags.Hovered) != 0;         // hover followed the visible content
        bool oldCleared = a.IsNull || !host.Scene.IsLive(a) || (host.Scene.Flags(a) & NodeFlags.Hovered) == 0;

        Check("gate.scroll.hover-follows-content.recycled-slot a boundary-crossing virtual scroll moves NodeFlags.Hovered to the overlapping logical row now under a stationary cursor",
            hovA && reRealized && logicalHitChanged && hovB && oldCleared,
            $"a={(a.IsNull ? "null" : a.Raw.Index.ToString())} b={(b.IsNull ? "null" : b.Raw.Index.ToString())} firstA={firstA} firstB={scB.FirstRealized} hovA={hovA} reRealized={reRealized} logicalHitChanged={logicalHitChanged} hovB={hovB} oldCleared={oldCleared}");
    }

}
