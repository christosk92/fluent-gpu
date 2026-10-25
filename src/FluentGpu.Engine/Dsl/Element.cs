using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;


/// <summary>Per-node skeleton-derivation policy (see <see cref="Element.SkeletonMode"/>).</summary>
public enum SkeletonMode : byte { Auto, Off }

/// <summary>The form-validation visual state of a control surface (form-validation.md). <see cref="Warning"/> is
/// reserved for non-blocking advisory styling; v1 styles only <see cref="Error"/> (red border + message).</summary>
public enum ValidationState : byte { None, Warning, Error }

/// <summary>A box: container/layout node and/or a filled, optionally-clickable surface (VStack/HStack/Button/Box).</summary>
public partial struct BoxEl : IElementKind<BoxEl>, IEquatable<BoxEl>
{
    public const ushort TypeId = 1;
    static ushort IElementKind<BoxEl>.Kind => 1;
    public static implicit operator Element(BoxEl value) => Element.Push(in value);
    public static explicit operator BoxEl(Element element) => element.As<BoxEl>();
    public BoxEl() { }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public readonly ushort ElementTypeId => 1;

    // Rarely-set channels live in seven shared, copy-on-write blocks (BoxCold.cs), one per theme: a BoxEl is allocated and
    // `with`-copied on every render of every box, so its inline footprint is only the channels most boxes set. A block is a
    // null reference until a setter changes a value; the first such setter on an element clones the block, the rest write the clone.
    internal ColdRef<BoxColdPaint> _cPaint;
    internal ColdRef<BoxColdPaintFx> _cPaintFx;
    internal ColdRef<BoxColdInput> _cInput;
    internal ColdRef<BoxColdGesture> _cGesture;
    internal ColdRef<BoxColdMotion> _cMotion;
    internal ColdRef<BoxColdHover> _cHover;
    internal ColdRef<BoxColdMisc> _cMisc;

    public byte Direction { get; init; }          // 0 = row, 1 = column
    public float Gap { get; init; }
    public Edges4 Padding { get; init; }
    public Edges4 Margin { get; init; }
    /// <summary>Unified channel (Prop&lt;T&gt;): a static color, a <c>Func&lt;ColorF&gt;</c> thunk, or a concrete signal.</summary>
    public Prop<ColorF> Fill { get; init; } = ColorF.Transparent;
    /// <summary>Bindable like <see cref="Fill"/>: a bound hover/press fill re-fires on RethemeAll (theme/palette
    /// switch) and can read recycle-varying state (e.g. a virtual list's slot index for zebra-aware hover depth)
    /// without remounting the slot. A==0 ⇒ the recorder auto-lightens/darkens <see cref="Fill"/> instead.</summary>
    public Prop<ColorF> HoverFill { readonly get => _cHover.Value.HoverFill; init { if (!EqualityComparer<Prop<ColorF>>.Default.Equals(_cHover.Value.HoverFill, value)) { var c = _cHover.Value; c.HoverFill = value; _cHover = ColdRef<BoxColdHover>.Of(in c); } } }
    public Prop<ColorF> PressedFill { readonly get => _cHover.Value.PressedFill; init { if (!EqualityComparer<Prop<ColorF>>.Default.Equals(_cHover.Value.PressedFill, value)) { var c = _cHover.Value; c.PressedFill = value; _cHover = ColdRef<BoxColdHover>.Of(in c); } } }
    /// <summary>Bindable like <see cref="Fill"/> so retained shell/card borders can follow a live theme switch.</summary>
    public Prop<ColorF> BorderColor { get; init; } = ColorF.Transparent;
    public ColorF HoverBorderColor { readonly get => _cPaint.Value.HoverBorderColor; init { if (!EqualityComparer<ColorF>.Default.Equals(_cPaint.Value.HoverBorderColor, value)) { var c = _cPaint.Value; c.HoverBorderColor = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } } // A==0 ⇒ recorder auto-lightens BorderColor on hover; else eases to this exact state token
    public ColorF PressedBorderColor { readonly get => _cPaint.Value.PressedBorderColor; init { if (!EqualityComparer<ColorF>.Default.Equals(_cPaint.Value.PressedBorderColor, value)) { var c = _cPaint.Value; c.PressedBorderColor = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } } // A==0 ⇒ recorder auto-darkens BorderColor on press; else eases to this exact state token
    public float BorderWidth { get; init; }
    /// <summary>Dashed (solid) border: the dash ON/OFF run lengths in DIP along the perimeter (e.g. 6/4). Both 0 (the
    /// default) = a solid stroke. Applies to the plain <see cref="BorderColor"/> stroke only (not a gradient border).
    /// The "drop zone" look (and the <c>DropZone</c> control) uses this.</summary>
    public float BorderDashOn { readonly get => _cPaint.Value.BorderDashOn; init { if (!EqualityComparer<float>.Default.Equals(_cPaint.Value.BorderDashOn, value)) { var c = _cPaint.Value; c.BorderDashOn = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    public float BorderDashOff { readonly get => _cPaint.Value.BorderDashOff; init { if (!EqualityComparer<float>.Default.Equals(_cPaint.Value.BorderDashOff, value)) { var c = _cPaint.Value; c.BorderDashOff = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    // Bindable (like Fill/Opacity): the shell content card squares its rail-side corners while the docked right rail is
    // open — a re-render can't reach it (frozen literal inside OverlayHost.Child), so the corner set must be a bind.
    public Prop<CornerRadius4> Corners { get; init; } = default;
    /// <summary>Form-validation visual state (form-validation.md). Bind it to a field's error memo
    /// (<c>Validation = Prop.Of(() =&gt; field.Error.Value.IsValid ? ValidationState.None : ValidationState.Error)</c>):
    /// on <see cref="ValidationState.Error"/> the reconciler resolves the theme critical color and the recorder swaps
    /// this node's border to it. A bound channel — no re-render per keystroke; the write is equality-gated.</summary>
    public Prop<ValidationState> Validation { readonly get => _cPaint.Value.Validation; init { if (!EqualityComparer<Prop<ValidationState>>.Default.Equals(_cPaint.Value.Validation, value)) { var c = _cPaint.Value; c.Validation = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }

    // Optional rich paint (carried into sparse scene side-tables by the reconciler; default = none).
    public ShadowSpec? Shadow { readonly get => _cPaint.Value.Shadow; init { if (!EqualityComparer<ShadowSpec?>.Default.Equals(_cPaint.Value.Shadow, value)) { var c = _cPaint.Value; c.Shadow = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } } // soft drop shadow / elevation, drawn beneath the fill
    public ArcSpec? Arc { readonly get => _cPaintFx.Value.Arc; init { if (!EqualityComparer<ArcSpec?>.Default.Equals(_cPaintFx.Value.Arc, value)) { var c = _cPaintFx.Value; c.Arc = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } } // circular-arc stroke (ProgressRing) — SDF ring trimmed to a sweep
    public GradientSpec? Gradient { readonly get => _cPaint.Value.Gradient; init { if (!EqualityComparer<GradientSpec?>.Default.Equals(_cPaint.Value.Gradient, value)) { var c = _cPaint.Value; c.Gradient = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } } // gradient fill — supersedes Fill at record time when set
    /// <summary>Optional bindable radial-gradient origin in normalized element coordinates. A non-finite static value
    /// keeps <see cref="GradientSpec.RadialCenter"/> as the source of truth; a signal updates paint only, without a
    /// component render or gradient-spec rebuild (pointer-driven spotlight/reveal effects).</summary>
    public Prop<Point2> RadialGradientCenter { readonly get => _cPaintFx.Value.RadialGradientCenter; init { if (!EqualityComparer<Prop<Point2>>.Default.Equals(_cPaintFx.Value.RadialGradientCenter, value)) { var c = _cPaintFx.Value; c.RadialGradientCenter = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    /// <summary>A second gradient the fill blends toward by <see cref="GradientMix"/> (0 = <see cref="Gradient"/>, 1 = this).
    /// Must share <see cref="Gradient"/>'s stop count (the <see cref="HoverGradient"/> rule; a different count blends only
    /// the shared prefix). Static; the blend happens at record time on stack locals, so a palette cross-fade costs no
    /// re-render and no allocation.</summary>
    public GradientSpec? GradientTo { readonly get => _cPaintFx.Value.GradientTo; init { if (!EqualityComparer<GradientSpec?>.Default.Equals(_cPaintFx.Value.GradientTo, value)) { var c = _cPaintFx.Value; c.GradientTo = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    /// <summary>The 0..1 blend from <see cref="Gradient"/> toward <see cref="GradientTo"/>. Bindable and paint-only (no
    /// relayout): a signal moves the colours without rebuilding a <see cref="GradientSpec"/>.</summary>
    public Prop<float> GradientMix { readonly get => _cPaintFx.Value.GradientMix; init { if (!EqualityComparer<Prop<float>>.Default.Equals(_cPaintFx.Value.GradientMix, value)) { var c = _cPaintFx.Value; c.GradientMix = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    /// <summary>How this box's subtree paints onto what is under it: <see cref="PaintBlend.Additive"/> adds light (glow,
    /// particles) for every rect, gradient, series and sprite field below it. Glyphs, images and paths stay source-over.
    /// Put it on a child of a <see cref="RepaintBoundary"/>, not on the boundary itself: a boundary (also a Feedback or
    /// RasterScale box) records its subtree into its own slice, which starts source-over — an additive ancestor never
    /// reaches into it.</summary>
    public PaintBlend Blend { readonly get => _cPaint.Value.Blend; init { if (!EqualityComparer<PaintBlend>.Default.Equals(_cPaint.Value.Blend, value)) { var c = _cPaint.Value; c.Blend = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    /// <summary>How a <see cref="RepaintBoundary"/> slice composites onto the back buffer: <see cref="LayerBlend.Screen"/>
    /// = <c>1 − (1 − s)(1 − d)</c> (soft clouds that brighten what they overlap). Needs <see cref="RepaintBoundary"/>;
    /// detached windows (no layer route) fold it to source-over.</summary>
    public LayerBlend LayerBlend { readonly get => _cPaint.Value.LayerBlend; init { if (!EqualityComparer<LayerBlend>.Default.Equals(_cPaint.Value.LayerBlend, value)) { var c = _cPaint.Value; c.LayerBlend = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    /// <summary>Make this box a FEEDBACK boundary (visualizer F6; see <see cref="FeedbackSpec"/>). Implies a repaint boundary
    /// at <see cref="FeedbackSpec.RasterScale"/>. Detached windows (no layer route) draw the fresh content only.</summary>
    public FeedbackSpec? Feedback { readonly get => _cPaintFx.Value.Feedback; init { if (!EqualityComparer<FeedbackSpec?>.Default.Equals(_cPaintFx.Value.Feedback, value)) { var c = _cPaintFx.Value; c.Feedback = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    /// <summary>The per-advance warp of the previous frame about the box centre (zoom / rotate / drift), DIP. Bindable.</summary>
    public Prop<Affine2D> FeedbackTransform { readonly get => _cPaintFx.Value.FeedbackTransform; init { if (!EqualityComparer<Prop<Affine2D>>.Default.Equals(_cPaintFx.Value.FeedbackTransform, value)) { var c = _cPaintFx.Value; c.FeedbackTransform = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    /// <summary>Overrides <see cref="FeedbackSpec.Decay"/> per advance (NaN = the spec's). Bindable: a kick can burst the trail.</summary>
    public Prop<float> FeedbackDecay { readonly get => _cPaintFx.Value.FeedbackDecay; init { if (!EqualityComparer<Prop<float>>.Default.Equals(_cPaintFx.Value.FeedbackDecay, value)) { var c = _cPaintFx.Value; c.FeedbackDecay = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    public GradientSpec? BorderBrush { readonly get => _cPaint.Value.BorderBrush; init { if (!EqualityComparer<GradientSpec?>.Default.Equals(_cPaint.Value.BorderBrush, value)) { var c = _cPaint.Value; c.BorderBrush = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } } // gradient border stroke (WinUI ControlElevationBorderBrush); needs BorderWidth > 0
    // Stateful gradient variants: the recorder per-frame interpolates the resting gradient's stops toward these by the
    // eased hover/press progress (same HoverT/PressT that cross-fades a solid Fill). Must share the resting stop count.
    public GradientSpec? HoverGradient { readonly get => _cPaintFx.Value.HoverGradient; init { if (!EqualityComparer<GradientSpec?>.Default.Equals(_cPaintFx.Value.HoverGradient, value)) { var c = _cPaintFx.Value; c.HoverGradient = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    public GradientSpec? PressedGradient { readonly get => _cPaintFx.Value.PressedGradient; init { if (!EqualityComparer<GradientSpec?>.Default.Equals(_cPaintFx.Value.PressedGradient, value)) { var c = _cPaintFx.Value; c.PressedGradient = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    public GradientSpec? HoverBorderBrush { readonly get => _cPaintFx.Value.HoverBorderBrush; init { if (!EqualityComparer<GradientSpec?>.Default.Equals(_cPaintFx.Value.HoverBorderBrush, value)) { var c = _cPaintFx.Value; c.HoverBorderBrush = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    public GradientSpec? PressedBorderBrush { readonly get => _cPaintFx.Value.PressedBorderBrush; init { if (!EqualityComparer<GradientSpec?>.Default.Equals(_cPaintFx.Value.PressedBorderBrush, value)) { var c = _cPaintFx.Value; c.PressedBorderBrush = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }
    public AcrylicSpec? Acrylic { readonly get => _cPaintFx.Value.Acrylic; init { if (!EqualityComparer<AcrylicSpec?>.Default.Equals(_cPaintFx.Value.Acrylic, value)) { var c = _cPaintFx.Value; c.Acrylic = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } } // per-node frosted-glass backdrop (blur + tint + noise)
    /// <summary>Record this subtree into its OWN retained slice (Flutter's RepaintBoundary, CSS <c>will-change</c>): a
    /// change inside it — a looping keyframe, a bound transform/opacity, a per-frame visualizer — re-rasters only this
    /// slice's tiles, and a change around it never re-rasters this one. Put it on the root of content that animates
    /// continuously while the content painted under and over it stays still. Spends the effect-slice budget
    /// (<c>SliceRecorder.EffectSliceCap</c>); past it, or inside an inline group layer, the subtree records inline as
    /// before (identical pixels either way).</summary>
    public bool RepaintBoundary { readonly get => _cPaint.Value.RepaintBoundary; init { if (!EqualityComparer<bool>.Default.Equals(_cPaint.Value.RepaintBoundary, value)) { var c = _cPaint.Value; c.RepaintBoundary = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    /// <summary>The raster resolution of a <see cref="RepaintBoundary"/> slice relative to the window (1 = full; snapped
    /// to 1/2, 1/4 or 1/8). Below 1 the slice holds NO tiles: each change replays it once into one surface at that scale
    /// and the composite upsamples it bilinearly — a fraction of the raster work and memory. Only for SOFT content whose
    /// look survives the upsample (large radial gradients, drifting colour fields, heavily blurred art); text and crisp
    /// edges soften. Ignored without <see cref="RepaintBoundary"/>.</summary>
    public float RasterScale { readonly get => _cPaint.Value.RasterScale; init { if (!EqualityComparer<float>.Default.Equals(_cPaint.Value.RasterScale, value)) { var c = _cPaint.Value; c.RasterScale = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    /// <summary>A POSED IMAGE LAYER: the box's own ScaleX/ScaleY/TranslateX/TranslateY (static, bound or render-owned
    /// rows) are applied at COMPOSITE time instead of being baked into the slice's tiles, so a slow pan/zoom of one
    /// full-bleed photo costs one bilinear quad per step and no tile raster. Implies <see cref="RepaintBoundary"/>.
    /// Contract: the subtree paints exactly ONE image (optionally under rectangular clips, with no corner radii, overlay,
    /// mask or saturation) and the pose is axis-aligned (scale + translate). Anything else falls back to the ordinary
    /// boundary route — identical pixels, tile raster per change — and the recorder counts the fallback. The box's own
    /// opacity stays an ordinary image opacity (it multiplies the quad's alpha).</summary>
    public bool CompositePose { readonly get => _cPaint.Value.CompositePose; init { if (!EqualityComparer<bool>.Default.Equals(_cPaint.Value.CompositePose, value)) { var c = _cPaint.Value; c.CompositePose = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    public bool TabShape { readonly get => _cPaint.Value.TabShape; init { if (!EqualityComparer<bool>.Default.Equals(_cPaint.Value.TabShape, value)) { var c = _cPaint.Value; c.TabShape = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } } // selected TabView header: rounded top + bottom flares
    public float TabFlareRadius { readonly get => _cPaint.Value.TabFlareRadius; init { if (!EqualityComparer<float>.Default.Equals(_cPaint.Value.TabFlareRadius, value)) { var c = _cPaint.Value; c.TabFlareRadius = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    /// <summary>Punch a VIDEO HOLE at this box (DrawOp.DrawVideo): instead of painting, the box ERASES the UI pixels
    /// already painted under it toward premultiplied zero, so the DComp video visual composited BELOW the swapchain shows
    /// through. Painter-ordered — anything recorded after it (letterbox bars, transport chrome) paints back OVER the
    /// video. Supersedes <see cref="Fill"/> for this node; see gpu-renderer.md §7.3 for the offscreen-layer limitation
    /// (inside an opacity/blur/acrylic layer the erase hits the layer RT, not the back buffer).</summary>
    public bool VideoHole { readonly get => _cPaint.Value.VideoHole; init { if (!EqualityComparer<bool>.Default.Equals(_cPaint.Value.VideoHole, value)) { var c = _cPaint.Value; c.VideoHole = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    /// <summary>The video registry slot token this hole belongs to (diagnostic at replay — the presenter positions the
    /// visual itself). Rides <c>NodePaint.ImageId</c>, so it is meaningful only with <see cref="VideoHole"/> set.</summary>
    public int VideoSurfaceId { readonly get => _cPaint.Value.VideoSurfaceId; init { if (!EqualityComparer<int>.Default.Equals(_cPaint.Value.VideoSurfaceId, value)) { var c = _cPaint.Value; c.VideoSurfaceId = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    /// <summary>Per-element edge fade (gpu-renderer.md): feather this element's content alpha to transparent (+ optional
    /// blur) near the chosen edges, following its rounded <see cref="Corners"/> (the curve) — it dissolves into whatever
    /// is behind. One offscreen RT per faded element. Null = none.</summary>
    public EdgeFadeSpec? EdgeFade { readonly get => _cPaintFx.Value.EdgeFade; init { if (!EqualityComparer<EdgeFadeSpec?>.Default.Equals(_cPaintFx.Value.EdgeFade, value)) { var c = _cPaintFx.Value; c.EdgeFade = value; _cPaintFx = ColdRef<BoxColdPaintFx>.Of(in c); } } }

    public Action? OnClick { get; init; }
    public Action<KeyEventArgs>? OnKeyDown { readonly get => _cInput.Value.OnKeyDown; init { if (!EqualityComparer<Action<KeyEventArgs>?>.Default.Equals(_cInput.Value.OnKeyDown, value)) { var c = _cInput.Value; c.OnKeyDown = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Text (character) input — the IME/layout-resolved codepoint, routed to the focused node and bubbled
    /// (distinct from <see cref="OnKeyDown"/>'s raw virtual-key). Set by editable controls (EditableText/ComboBox).</summary>
    public Action<CharEventArgs>? OnCharInput { readonly get => _cGesture.Value.OnCharInput; init { if (!EqualityComparer<Action<CharEventArgs>?>.Default.Equals(_cGesture.Value.OnCharInput, value)) { var c = _cGesture.Value; c.OnCharInput = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    // Position-aware pointer (local coords) — for sliders/scrollbars: OnPointerDown fires on press, OnDrag while held.
    public Action<Point2>? OnPointerDown { readonly get => _cGesture.Value.OnPointerDown; init { if (!EqualityComparer<Action<Point2>?>.Default.Equals(_cGesture.Value.OnPointerDown, value)) { var c = _cGesture.Value; c.OnPointerDown = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    public Action<Point2>? OnDrag { readonly get => _cGesture.Value.OnDrag; init { if (!EqualityComparer<Action<Point2>?>.Default.Equals(_cGesture.Value.OnDrag, value)) { var c = _cGesture.Value; c.OnDrag = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Marks an <see cref="OnDrag"/> node a CROSS-AXIS content pan (SwipeControl row swipe, FlipView page drag):
    /// instead of eagerly capturing the contact on touch-down — the Slider/EditableText scrub default — this drag enrolls
    /// an AXIS-LOCKED gesture-arena member that competes with an enclosing scroller's Pan (input-a11y.md §7A). It wins the
    /// contact only when the gesture runs ALONG its own axis and yields (the list scrolls) on a cross-axis drag — the
    /// declarative form of <c>DragController.YieldsToPan</c>. Its axis is inferred from this node's main axis
    /// (<see cref="Direction"/>): a row box (Direction=0) is a horizontal swipe, a column box a vertical one. No effect
    /// without <see cref="OnDrag"/>, and no effect on the mouse path (mouse drag still captures immediately).</summary>
    public bool DragYieldsToPan { readonly get => _cGesture.Value.DragYieldsToPan; init { if (!EqualityComparer<bool>.Default.Equals(_cGesture.Value.DragYieldsToPan, value)) { var c = _cGesture.Value; c.DragYieldsToPan = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Position-aware press carrying click count (double/triple-click), modifier chord, button and device kind —
    /// the text-selection / list-interaction press handler. Fires alongside <see cref="OnPointerDown"/> on left press.</summary>
    public Action<PointerEventArgs>? OnPointerPressed { readonly get => _cInput.Value.OnPointerPressed; init { if (!EqualityComparer<Action<PointerEventArgs>?>.Default.Equals(_cInput.Value.OnPointerPressed, value)) { var c = _cInput.Value; c.OnPointerPressed = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Typed clean-release edge. Fires only when the primary pointer releases over its original press target;
    /// pan, drag, hold, capture loss, cancellation, and release outside suppress it.</summary>
    public Action<PointerEventArgs>? OnPointerReleased { readonly get => _cInput.Value.OnPointerReleased; init { if (!EqualityComparer<Action<PointerEventArgs>?>.Default.Equals(_cInput.Value.OnPointerReleased, value)) { var c = _cInput.Value; c.OnPointerReleased = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Context-menu request (WinUI ContextRequested): right-click release over this node, the Menu key /
    /// Shift+F10 while it has focus, or a touch long-press. The <see cref="ContextRequestEventArgs"/> carries the
    /// node-LOCAL position (keyboard invocations pass the node's centre) and the <see cref="ContextRequestTrigger"/>
    /// so a handler can open AT the pointer/contact but anchor to the element rect for a keyboard invocation.</summary>
    public Action<ContextRequestEventArgs>? OnContextRequested { readonly get => _cInput.Value.OnContextRequested; init { if (!EqualityComparer<Action<ContextRequestEventArgs>?>.Default.Equals(_cInput.Value.OnContextRequested, value)) { var c = _cInput.Value; c.OnContextRequested = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Declares this node a CONTEXT-INVOKER (input-a11y.md §6.5.1): a left-click / touch-tap / Space-Enter
    /// activation on it re-enters the context-request funnel STARTING AT this node — the dispatcher walks ancestors for
    /// the nearest <see cref="OnContextRequested"/> and raises it exactly as a right-click would, so the same menu,
    /// selection semantics, and light-dismiss result by construction. It is the declarative "this button opens the
    /// row's context menu" affordance (a track row's "…" button), replacing the app-side OnRealized-capture +
    /// RedispatchContextAt pattern that went stale on re-render.
    ///
    /// The activation is dispatched with <see cref="ContextRequestTrigger.Invoke"/> (pointer-originated ⇒ menu does NOT
    /// focus its first item; rect-anchored on this node — the button — via <see cref="ContextRequestEventArgs.Source"/>),
    /// EXCEPT a Space/Enter keyboard activation, which dispatches <see cref="ContextRequestTrigger.Keyboard"/> so the
    /// first item IS focused. If no ancestor handles the request, nothing opens.
    ///
    /// IMPLIES <see cref="OnClick"/>'s hit-test / press / hover / focusable footprint (it presses, hovers and takes
    /// focus like a button) — declare <c>Cursor = CursorId.Hand</c> yourself if you want the hand. MUTUALLY EXCLUSIVE
    /// with <see cref="OnClick"/> (a node is a click target OR a context-invoker, not both; this prop wins and a DEBUG
    /// assert fires if both are set). Disabled (<see cref="IsEnabled"/> = false) suppresses it like any activation.</summary>
    public bool ClickRequestsContext { readonly get => _cInput.Value.ClickRequestsContext; init { if (!EqualityComparer<bool>.Default.Equals(_cInput.Value.ClickRequestsContext, value)) { var c = _cInput.Value; c.ClickRequestsContext = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Keyboard-accelerator chord (WinUI KeyboardAccelerator): invokes <see cref="OnClick"/> from anywhere once
    /// focused routing leaves the chord unhandled (e.g. Ctrl+W close-tab).</summary>
    public KeyAccelerator? Accelerator { readonly get => _cGesture.Value.Accelerator; init { if (!EqualityComparer<KeyAccelerator?>.Default.Equals(_cGesture.Value.Accelerator, value)) { var c = _cGesture.Value; c.Accelerator = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Access-key mnemonic (WinUI AccessKey): Alt+letter invokes <see cref="OnClick"/>. Uppercase 'A'..'Z'/'0'..'9'.</summary>
    public char AccessKey { readonly get => _cGesture.Value.AccessKey; init { if (!EqualityComparer<char>.Default.Equals(_cGesture.Value.AccessKey, value)) { var c = _cGesture.Value; c.AccessKey = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Pointer cursor shown while hovering this node or any cursor-less descendant (WinUI SetCursor). Null =
    /// inherit from the nearest declaring ancestor, else the system arrow — clickability does NOT imply the hand. An
    /// explicit value (Arrow included) terminates the lookup, masking an ancestor's I-beam/hand (the TextBox delete
    /// button / PasswordBox reveal force Arrow over the field's I-beam — TextBox_Partial.cpp:884).</summary>
    public CursorId? Cursor { get; init; }
    /// <summary>Element-level wheel hook (WinUI PointerWheelChanged), consulted BEFORE the enclosing viewport scrolls;
    /// set <c>Handled</c> to consume (NumberBox value stepping). Unhandled keeps walking up (routed-event semantics).</summary>
    public Action<WheelEventArgs>? OnPointerWheel { readonly get => _cGesture.Value.OnPointerWheel; init { if (!EqualityComparer<Action<WheelEventArgs>?>.Default.Equals(_cGesture.Value.OnPointerWheel, value)) { var c = _cGesture.Value; c.OnPointerWheel = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Position-aware BARE hover (local coords), fired on pointer move while hovering with no button down —
    /// e.g. RatingControl filling stars to the cursor on hover. Makes the node hit-testable so it receives hover.</summary>
    public Action<Point2>? OnHoverMove { readonly get => _cInput.Value.OnHoverMove; init { if (!EqualityComparer<Action<Point2>?>.Default.Equals(_cInput.Value.OnHoverMove, value)) { var c = _cInput.Value; c.OnHoverMove = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Routed mouse/pen move in this node's local coordinates while the pointer is anywhere in its subtree.
    /// Delivered leaf-to-root, including when an interactive child is the hit leaf. Suppressed for touch and capture/
    /// drag paths. Intended for allocation-free container effects such as a pointer-tracked spotlight.</summary>
    public Action<Point2>? OnPointerMoveWithin { readonly get => _cGesture.Value.OnPointerMoveWithin; init { if (!EqualityComparer<Action<Point2>?>.Default.Equals(_cGesture.Value.OnPointerMoveWithin, value)) { var c = _cGesture.Value; c.OnPointerMoveWithin = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Fired when the pointer LEAVES this node (loses hover) — to reset a hover preview to its resting state
    /// (RatingControl reverting to the committed rating, a ToolTip dismissing). Makes the node hit-testable.</summary>
    public Action? OnPointerExit { readonly get => _cInput.Value.OnPointerExit; init { if (!EqualityComparer<Action?>.Default.Equals(_cInput.Value.OnPointerExit, value)) { var c = _cInput.Value; c.OnPointerExit = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Fired when the dispatcher moves keyboard/pointer focus ONTO (true) or OFF (false) this node — the WinUI
    /// GotFocus/LostFocus pair. Delivered by <c>InputDispatcher.SetFocus</c> directly (never via hit-testing); editable
    /// controls use it to arm the caret blinker / IME and capture the Escape-revert snapshot.</summary>
    public Action<bool>? OnFocusChanged { readonly get => _cInput.Value.OnFocusChanged; init { if (!EqualityComparer<Action<bool>?>.Default.Equals(_cInput.Value.OnFocusChanged, value)) { var c = _cInput.Value; c.OnFocusChanged = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Marks this box a drag-reorder source (WinUI CanDragItems/CanReorderItems item container): a left press
    /// on it (or any non-draggable descendant) arms the engine's DragController; pointer travel past the 4px drag box
    /// (per-axis, ListViewBaseItem_Partial.cpp:1864-1878) promotes the press to a drag — the node follows the pointer
    /// at WinUI ListViewItemDragThemeOpacity 0.80 (ListViewItem_themeresources.xaml:7) with a lifted shadow, stops
    /// hit-testing, and the eventual release SUPPRESSES the click.</summary>
    public bool CanDrag { readonly get => _cGesture.Value.CanDrag; init { if (!EqualityComparer<bool>.Default.Equals(_cGesture.Value.CanDrag, value)) { var c = _cGesture.Value; c.CanDrag = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Drag lifecycle (needs <see cref="CanDrag"/>): fired once when the press crosses the drag box (WinUI
    /// DragItemsStarting). The args instance is reused for the whole gesture — copy what you keep.</summary>
    public Action<DragEventArgs>? OnDragStarted { readonly get => _cGesture.Value.OnDragStarted; init { if (!EqualityComparer<Action<DragEventArgs>?>.Default.Equals(_cGesture.Value.OnDragStarted, value)) { var c = _cGesture.Value; c.OnDragStarted = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Every pointer move while the drag is active: accumulated gesture deltas + smoothed velocity — feed
    /// <c>ReorderList.Update(e.TotalDy)</c> and re-render with its projected order / offset hints.</summary>
    public Action<DragEventArgs>? OnDragDelta { readonly get => _cGesture.Value.OnDragDelta; init { if (!EqualityComparer<Action<DragEventArgs>?>.Default.Equals(_cGesture.Value.OnDragDelta, value)) { var c = _cGesture.Value; c.OnDragDelta = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Release after an active drag (WinUI DragItemsCompleted): commit the reorder here
    /// (<c>ReorderList.Complete()</c>); the drop-glide and the displaced-sibling FLIP retarget off this commit.</summary>
    public Action<DragEventArgs>? OnDragCompleted { readonly get => _cGesture.Value.OnDragCompleted; init { if (!EqualityComparer<Action<DragEventArgs>?>.Default.Equals(_cGesture.Value.OnDragCompleted, value)) { var c = _cGesture.Value; c.OnDragCompleted = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>The drag aborted (Escape / pointer-capture loss / window blur): drop hints without committing. Also the abort
    /// edge of an <c>OnDrag</c> capture owner whose contact dies with no release (its OnClick commit never comes).</summary>
    public Action? OnDragCanceled { readonly get => _cGesture.Value.OnDragCanceled; init { if (!EqualityComparer<Action?>.Default.Equals(_cGesture.Value.OnDragCanceled, value)) { var c = _cGesture.Value; c.OnDragCanceled = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>E5-L2 typed drag SOURCE (the Flutter Draggable / react-beautiful-dnd model — deliberately NOT WinUI
    /// OLE, per the 2026-06-10 user ruling): marks this box draggable (implies <see cref="CanDrag"/> — the L1 gesture
    /// armer) with a string Kind discriminator + a payload factory the engine resolves ONCE when the press promotes
    /// past the drag box. The live <see cref="DragSession"/> then routes to the nearest accepting
    /// <see cref="DropTarget"/> under the pointer on every move.</summary>
    public DragSource? Draggable { readonly get => _cGesture.Value.Draggable; init { if (!EqualityComparer<DragSource?>.Default.Equals(_cGesture.Value.Draggable, value)) { var c = _cGesture.Value; c.Draggable = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>E5-L2 drop TARGET (Flutter DragTarget / SwiftUI dropDestination): accepts sessions whose Kind is in
    /// <c>AcceptKinds</c> — OnEnter/OnOver/OnLeave fire on hover transitions, OnDrop on release over it (before the
    /// L1 completion; <c>SettleOnDrop</c> keeps the drop-glide for reorder targets). Discovery is hit-test-CHAIN
    /// based (nearest accepting ancestor of the node under the pointer) — the spec alone does NOT make this box
    /// click/pointer hit-testable.</summary>
    public DropTargetSpec? DropTarget { readonly get => _cGesture.Value.DropTarget; init { if (!EqualityComparer<DropTargetSpec?>.Default.Equals(_cGesture.Value.DropTarget, value)) { var c = _cGesture.Value; c.DropTarget = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Opt this clickable node into auto-repeat: while held, the host's RepeatTicker re-invokes <see cref="OnClick"/>
    /// after an initial delay, then at a fixed interval (WinUI RepeatButton). Pauses while the held pointer leaves the
    /// node (fresh delay on re-entry — RepeatButton_Partial.cpp:530-574); a held Space arms the same engine timer.</summary>
    public bool Repeats { readonly get => _cGesture.Value.Repeats; init { if (!EqualityComparer<bool>.Default.Equals(_cGesture.Value.Repeats, value)) { var c = _cGesture.Value; c.Repeats = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>WinUI RepeatButton <c>Delay</c>/<c>Interval</c> (ms) for <see cref="Repeats"/> nodes. NaN = the WinUI
    /// DP defaults (500/33); the ScrollBar template arrows use Interval=50 (ScrollBar_themeresources.xaml).</summary>
    public float RepeatDelayMs { readonly get => _cGesture.Value.RepeatDelayMs; init { if (!EqualityComparer<float>.Default.Equals(_cGesture.Value.RepeatDelayMs, value)) { var c = _cGesture.Value; c.RepeatDelayMs = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    public float RepeatIntervalMs { readonly get => _cGesture.Value.RepeatIntervalMs; init { if (!EqualityComparer<float>.Default.Equals(_cGesture.Value.RepeatIntervalMs, value)) { var c = _cGesture.Value; c.RepeatIntervalMs = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>WinUI <c>KeyPress::Button bAcceptsReturn</c>: false = Enter does NOT activate this clickable (it falls
    /// through to normal key routing) — CheckBox (CheckBox_Partial.cpp:27), RadioButton (RadioButton_Partial.cpp:30)
    /// and ToggleSwitch (ToggleSwitch_Partial.cpp:1002-1007) toggle on Space only. Space activation is unaffected.</summary>
    public bool ActivateOnEnter { readonly get => _cGesture.Value.ActivateOnEnter; init { if (!EqualityComparer<bool>.Default.Equals(_cGesture.Value.ActivateOnEnter, value)) { var c = _cGesture.Value; c.ActivateOnEnter = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>WinUI <c>AllowFocusOnInteraction</c>: false = a pointer press never moves focus to this focusable (and
    /// never falls past it to an ancestor — focus stays where it was, AppBarButton_themeresources.xaml:136); keyboard
    /// Tab still reaches it. True (default) = press focuses the nearest focusable self-or-ancestor.</summary>
    public bool AllowFocusOnInteraction { readonly get => _cInput.Value.AllowFocusOnInteraction; init { if (!EqualityComparer<bool>.Default.Equals(_cInput.Value.AllowFocusOnInteraction, value)) { var c = _cInput.Value; c.AllowFocusOnInteraction = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Bindable like <see cref="Fill"/>/<see cref="Visible"/> (E15, home-redesign-remediation.md §2): a
    /// resolved <c>false</c> clears <c>NodeFlags.HitTestVisible</c> on this node WITHOUT a component re-render (a bind
    /// effect owns the flag, wired at mount by <c>Reconciler.BindNode</c>) — the same bind-scoped shape as
    /// <see cref="Visible"/>'s presence channel, but for hit-testing alone (the node stays laid out, painted, and its
    /// subtree still hit-tests; only THIS node stops being a target/blocking chain link — see
    /// <see cref="HitTestPassThrough"/> for "yield to what's behind" instead of "exclude the subtree"). A static
    /// (unbound) value is re-asserted every reconcile exactly like the pre-existing <c>bool</c> — the implicit
    /// <c>bool</c>→<c>Prop&lt;bool&gt;</c> conversion keeps every existing <c>HitTestVisible = expr</c> call site
    /// compiling unchanged.</summary>
    public Prop<bool> HitTestVisible { get; init; } = true;
    /// <summary>Input pass-through (WinUI <c>OverlayInputPassThroughElement</c>): when true, this node yields the hit to
    /// whatever is BEHIND it wherever none of its OWN children are hit — so a full-bleed floating overlay can host an
    /// interactive child (a command bar) while clicks in its empty area fall through to the page beneath. (Unlike
    /// <see cref="HitTestVisible"/>=false, which excludes the whole subtree and would make the child unclickable.)</summary>
    public bool HitTestPassThrough { readonly get => _cInput.Value.HitTestPassThrough; init { if (!EqualityComparer<bool>.Default.Equals(_cInput.Value.HitTestPassThrough, value)) { var c = _cInput.Value; c.HitTestPassThrough = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>An opaque, input-blocking surface (a modal dialog card, a light-dismiss popup plate) sits geometrically
    /// ON TOP of whatever page content is laid out beneath it, but it is a Z-STACK SIBLING of that content, not an
    /// ancestor. The scroll dispatcher's containing-scroller fallback (<c>InputDispatcher.ContainingScrollerForAxis</c>)
    /// walks the WHOLE tree by geometry alone when no scrollable ANCESTOR of the hit point exists — it has no notion of
    /// paint order, so it can find and wheel-scroll a background list whose LAID-OUT bounds happen to sit under a
    /// covering-but-non-scrollable overlay (a dialog's plain message text, say). Set true on a covering surface to make
    /// that fallback treat it as opaque: any scrollable candidate found in an EARLIER sibling is discarded once this
    /// node is reached, though the fallback still recurses into ITS OWN children (a dialog with genuine scrollable
    /// Content is still found normally, since that inner scroller is a DESCENDANT, not blocked by this reset).</summary>
    public bool BlocksBackgroundScroll { readonly get => _cGesture.Value.BlocksBackgroundScroll; init { if (!EqualityComparer<bool>.Default.Equals(_cGesture.Value.BlocksBackgroundScroll, value)) { var c = _cGesture.Value; c.BlocksBackgroundScroll = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Input-enabled (the default). When false the engine gates this node's interaction: it does not hit-test,
    /// focus, take keyboard activation, repeat, drag, or click — so control factories no longer null their handlers by
    /// hand. Disabled <em>visuals</em> stay control-chosen (pick the disabled token via <c>StateBrush.Resting(enabled)</c>).</summary>
    public bool IsEnabled { get; init; } = true;
    public bool Focusable { get; init; }
    /// <summary>WinUI <c>Control.IsTabStop</c>: null = auto (clickable nodes are focusable), false = NEVER keyboard
    /// focusable even when clickable (the light-dismiss catcher layer — WinUI's dismiss layer is not a tab stop),
    /// true = force focusable.</summary>
    public bool? TabStop { readonly get => _cInput.Value.TabStop; init { if (!EqualityComparer<bool?>.Default.Equals(_cInput.Value.TabStop, value)) { var c = _cInput.Value; c.TabStop = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    public int TabIndex { readonly get => _cGesture.Value.TabIndex; init { if (!EqualityComparer<int>.Default.Equals(_cGesture.Value.TabIndex, value)) { var c = _cGesture.Value; c.TabIndex = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>WinUI FocusVisualMargin: negative values push the keyboard-focus ring OUTSIDE the bounds. Null = the
    /// WinUI template default (−3 all around); Slider uses −7,0,−7,0.</summary>
    public Edges4? FocusVisualMargin { readonly get => _cInput.Value.FocusVisualMargin; init { if (!EqualityComparer<Edges4?>.Default.Equals(_cInput.Value.FocusVisualMargin, value)) { var c = _cInput.Value; c.FocusVisualMargin = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Hit slop: how far (DIP, per side) this box's HIT rect reaches outside its laid-out box, for a small target that must still be
    /// easy to press (a 28 glyph button at a touch tier). No effect on layout or paint. The slop is clipped like the box itself: a clipping or
    /// scrolling ancestor cuts it, and a real hit always wins over a slop hit (a neighbour whose own box holds the point takes it, so slops
    /// never steal from what is drawn there). Adds to the host's floor (<c>InputDispatcher.MinHitSize</c>). Default none.</summary>
    public Edges4 HitSlop { readonly get => _cInput.Value.HitSlop; init { if (!EqualityComparer<Edges4>.Default.Equals(_cInput.Value.HitSlop, value)) { var c = _cInput.Value; c.HitSlop = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>Semantic control role (set by the control factories; a button IS a BoxEl). Surfaced to a11y/devtools/tests.</summary>
    public AutomationRole Role { get; init; }

    // Composited (animate without relayout): transform (offset/scale/rotate about the transform origin) + opacity, applied to this node + subtree.
    public float OffsetX { readonly get => _cMotion.Value.OffsetX; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.OffsetX, value)) { var c = _cMotion.Value; c.OffsetX = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public float OffsetY { readonly get => _cMotion.Value.OffsetY; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.OffsetY, value)) { var c = _cMotion.Value; c.OffsetY = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public float ScaleX { readonly get => _cMotion.Value.ScaleX; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.ScaleX, value)) { var c = _cMotion.Value; c.ScaleX = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public float ScaleY { readonly get => _cMotion.Value.ScaleY; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.ScaleY, value)) { var c = _cMotion.Value; c.ScaleY = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public float Rotation { readonly get => _cMotion.Value.Rotation; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.Rotation, value)) { var c = _cMotion.Value; c.Rotation = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } } // degrees
    /// <summary>Unified channel (Prop&lt;T&gt;): a static opacity, a thunk, or a concrete signal.</summary>
    public Prop<float> Opacity { get; init; } = 1f;
    public float HoverOpacity { readonly get => _cMotion.Value.HoverOpacity; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.HoverOpacity, value)) { var c = _cMotion.Value; c.HoverOpacity = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public float PressedOpacity { readonly get => _cMotion.Value.PressedOpacity; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.PressedOpacity, value)) { var c = _cMotion.Value; c.PressedOpacity = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    /// <summary>Flat opacity group (WinUI Composition LayerVisual semantics): when set and the resolved opacity is
    /// &lt; 1, the subtree renders at FULL alpha into a pooled offscreen RT and composites ONCE at the group alpha —
    /// overlapping children don't double-blend (a fading dialog plate + its buttons). Default false = per-node
    /// multiplied opacity (WinUI's plain Visual.Opacity behavior). Engine primitive: PushLayer{Opacity} (E9).</summary>
    public bool OpacityGroup { readonly get => _cMotion.Value.OpacityGroup; init { if (!EqualityComparer<bool>.Default.Equals(_cMotion.Value.OpacityGroup, value)) { var c = _cMotion.Value; c.OpacityGroup = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    /// <summary>Per-node self-blur radius σ (px) — the Expressive Motion Kit's perceptual softener. &gt; 0 wraps the
    /// node's subtree in a PushLayer{Blur} (subtree → pooled offscreen RT → separable Gaussian → composite at the group
    /// alpha), so the node's OWN pixels blur (CSS <c>filter: blur()</c>, not the backdrop). Animate it via
    /// <c>AnimChannel.BlurSigma</c> (UseTransition/UseKeyframes) for the transitions.dev recipes (number pop-in, skeleton
    /// reveal, icon swap, page slide, …). 0 = no blur (the default). Composited only — never relayout.</summary>
    public float Blur { readonly get => _cMotion.Value.Blur; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.Blur, value)) { var c = _cMotion.Value; c.Blur = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    /// <summary>Transform origin (normalized 0..1 of the box). Composited scale/rotate (and animated ScaleX/Y) pivot here;
    /// default centre (0.5,0.5). Set OriginY=0 to scale/unfold from the TOP edge (a flyout/menu), 1 for the bottom.</summary>
    public float TransformOriginX { readonly get => _cMotion.Value.TransformOriginX; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.TransformOriginX, value)) { var c = _cMotion.Value; c.TransformOriginX = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public float TransformOriginY { readonly get => _cMotion.Value.TransformOriginY; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.TransformOriginY, value)) { var c = _cMotion.Value; c.TransformOriginY = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }

    // Interaction-driven composited scale (1 = none): grows/shrinks this node about its centre by the eased hover/press
    // progress at record time (a WinUI slider/scrollbar thumb that pops on hover). Needs a pointer handler to receive the
    // hover/press flags. Composited only — never changes layout or hit-testing.
    public float HoverScale { readonly get => _cMotion.Value.HoverScale; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.HoverScale, value)) { var c = _cMotion.Value; c.HoverScale = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public float PressScale { readonly get => _cMotion.Value.PressScale; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.PressScale, value)) { var c = _cMotion.Value; c.PressScale = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public float HoverDurationMs { readonly get => _cMotion.Value.HoverDurationMs; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.HoverDurationMs, value)) { var c = _cMotion.Value; c.HoverDurationMs = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public float PressDurationMs { readonly get => _cMotion.Value.PressDurationMs; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.PressDurationMs, value)) { var c = _cMotion.Value; c.PressDurationMs = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public EasingSpec HoverEasing { readonly get => _cMotion.Value.HoverEasing; init { if (!EqualityComparer<EasingSpec>.Default.Equals(_cMotion.Value.HoverEasing, value)) { var c = _cMotion.Value; c.HoverEasing = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }
    public EasingSpec PressEasing { readonly get => _cMotion.Value.PressEasing; init { if (!EqualityComparer<EasingSpec>.Default.Equals(_cMotion.Value.PressEasing, value)) { var c = _cMotion.Value; c.PressEasing = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }

    /// <summary>Implicit brush transition (WinUI <c>BrushTransition</c>): when a re-render changes Fill/BorderColor on
    /// this LIVE node (a logical state flip — checked, selected…), the displayed color cross-fades over this duration
    /// instead of snapping. NaN = snap (the default); WinUI control templates use 83ms.</summary>
    public float BrushTransitionMs { readonly get => _cMotion.Value.BrushTransitionMs; init { if (!EqualityComparer<float>.Default.Equals(_cMotion.Value.BrushTransitionMs, value)) { var c = _cMotion.Value; c.BrushTransitionMs = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }

    // ── Fine-grained reactive bindings (signals-first). Every bindable channel is ONE Prop<T> property: a static
    // value (re-asserted each reconcile iff not bound), a Func<T> thunk reading signals, or a concrete signal — the
    // reconciler wires a bound channel into a mount-time effect that writes only this node's column + marks the
    // matching dirty axis (no component re-render, no reconcile). Transform/Opacity/Fill are compositor-only;
    // Width/Height bind layout and trigger a scoped relayout.
    /// <summary>The whole-matrix transform channel — a thunk/signal producing the full Affine2D, or a plain static
    /// matrix. Either spelling WINS over the decomposed OffsetX/Y/ScaleX/Y/Rotation floats above, which are the
    /// convenience path for the common translate/scale/rotate cases.
    /// ONE transform owner per node: never combine this with the decomposed statics, with a transform-owning scroll
    /// effect (<c>.Sticky</c> / <c>.Parallax</c> / a TransX|TransY|ScaleXY <c>ScrollEffect</c>), or with transform-channel
    /// animations. A DEBUG
    /// tripwire in the reconciler turns each of those into a stack trace at the offending element.
    /// NOTE <c>default(Affine2D)</c> is all-zeros, not identity — the reconciler treats "differs from default" as
    /// "declared", so leaving this unset costs nothing.</summary>
    public Prop<Affine2D> Transform { readonly get => _cMotion.Value.Transform; init { if (!EqualityComparer<Prop<Affine2D>>.Default.Equals(_cMotion.Value.Transform, value)) { var c = _cMotion.Value; c.Transform = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }

    // Legacy *Bind spellings — write-only init-aliases into the unified channel props, deleted per-channel by the
    // migration waves. A null assignment leaves the channel static (preserves the `cond ? null : bind` idiom).

    /// <summary>Called once when this box is realized into the scene, with its node handle — for a control factory to
    /// capture the handle (e.g. to wire a signal binding that needs the live node). Fires at mount only.</summary>
    public Action<NodeHandle>? OnRealized { get; init; }
    /// <summary>Called after layout when this node's arranged local bounds change. Intended for retained leaf controls
    /// that need their own laid-out width/height without subscribing to raw viewport changes.</summary>
    public Action<RectF>? OnBoundsChanged { readonly get => _cGesture.Value.OnBoundsChanged; init { if (!EqualityComparer<Action<RectF>?>.Default.Equals(_cGesture.Value.OnBoundsChanged, value)) { var c = _cGesture.Value; c.OnBoundsChanged = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }

    /// <summary>Follow another node's on-screen rect: answers the node this box must cover RIGHT NOW (typically a hollow
    /// reservation captured through <see cref="OnRealized"/>), or <see cref="NodeHandle.Null"/> for "not following". While it
    /// answers a live node the host rewrites this box's layout Width/Height and paint translation from that node's rect in
    /// the SAME frame, after layout and the animation tick and before the video geometry scan and record, so the box tracks
    /// the target through resizes AND paint-only motion (a slide, a page transition) that fire no bounds edge; while it follows, the
    /// animation engine keeps translate/scale/rotate rows on the follower and target chains UI-ticked (a render-owned row would
    /// not advance the UI-side pose the pass reads). This box's own
    /// Width/Height/Transform bindings stand down while it follows and next apply when one of their sources changes after it
    /// stops (so let the thunk's answer be a function of a signal those bindings read too). The thunk runs on the UI thread
    /// outside any reactive scope (read signals with Peek), must not allocate, and must be the SAME delegate every render
    /// (a fresh closure per render re-diffs the box). The target must not be inside this box's own subtree.</summary>
    public Func<NodeHandle>? FollowRect { readonly get => _cGesture.Value.FollowRect; init { if (!EqualityComparer<Func<NodeHandle>?>.Default.Equals(_cGesture.Value.FollowRect, value)) { var c = _cGesture.Value; c.FollowRect = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }
    /// <summary>Declarative window drag region (custom-frame windows; sugar <c>el.WindowDragArea()</c> /
    /// <c>el.NoWindowDrag()</c>). <see cref="WindowDragRole.Drag"/>: this box's VISIBLE rect (ancestor clips + scroll /
    /// transform pose) moves the window — the OS move loop, snap, double-click maximize when resizable; a right-click
    /// raises the nearest <see cref="OnContextRequested"/> under the point, else the system menu.
    /// <see cref="WindowDragRole.NoDrag"/>: an interactive island inside a drag area stays client. The host re-collects
    /// every reachable declared box each frame (phase 7.8 — scroll, resize and parked pages are followed with no effect
    /// of yours) and pushes the merged region list only when it changed; app <c>InputHooks.SetTitleBarRegions</c>
    /// pushes (the <c>TitleBar</c> control) compose with it: app islands/buttons first, then engine islands, engine drag
    /// bands, and the app catch-all caption band last. Does not make the box hit-testable.</summary>
    public WindowDragRole WindowDrag { readonly get => _cGesture.Value.WindowDrag; init { if (!EqualityComparer<WindowDragRole>.Default.Equals(_cGesture.Value.WindowDrag, value)) { var c = _cGesture.Value; c.WindowDrag = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }

    public ElementList Children { get; init; } = [];

    /// <summary>Z-stack: children overlay at this box's origin (each filling it unless sized), painted in order
    /// (last on top) — for overlays, scrims, flyouts, the NavigationView Minimal pane. (A flexbox container otherwise.)</summary>
    public bool ZStack { get; init; }
    public bool ClipToBounds { get; init; }

    /// <summary>Clip this node AND its whole subtree to an arbitrary <c>PathData</c> silhouette (gpu-renderer.md §6's
    /// tier-3 stencil clip) instead of the rectangle <see cref="ClipToBounds"/> gives. Setting it IMPLIES
    /// <see cref="ClipToBounds"/> — the node's device box still bounds the scope as the scissor, and the geometry's
    /// AABB narrows it further. Null (the default) leaves clipping exactly as it was.
    /// <para>HARD EDGE in v1 (the mask discards coverage below 0.5) — an anti-aliased path clip is the offscreen-layer
    /// route (§7.1), not this one. It also clips HIT-TESTING: a point inside the box but outside the geometry takes no
    /// hit on this node or anything under it, so click and pixels agree.</para></summary>
    public PathData? ClipPath { readonly get => _cPaint.Value.ClipPath; init { if (!EqualityComparer<PathData?>.Default.Equals(_cPaint.Value.ClipPath, value)) { var c = _cPaint.Value; c.ClipPath = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    /// <summary>Winding rule for <see cref="ClipPath"/> (mirrors <c>PathEl.Rule</c>). Ignored when ClipPath is null.</summary>
    public FillRule ClipPathRule { readonly get => _cPaint.Value.ClipPathRule; init { if (!EqualityComparer<FillRule>.Default.Equals(_cPaint.Value.ClipPathRule, value)) { var c = _cPaint.Value; c.ClipPathRule = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    /// <summary>0 (default) = <see cref="ClipPath"/> is already node-local DIP. Paired with
    /// <see cref="ClipPathViewBoxH"/> &gt; 0, bakes the uniform-fit (min-axis) scale into the clip at record time —
    /// the same contract <c>PathEl.ViewBoxW/H</c> has, so one authored silhouette clips any box size.</summary>
    public float ClipPathViewBoxW { readonly get => _cPaint.Value.ClipPathViewBoxW; init { if (!EqualityComparer<float>.Default.Equals(_cPaint.Value.ClipPathViewBoxW, value)) { var c = _cPaint.Value; c.ClipPathViewBoxW = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }
    public float ClipPathViewBoxH { readonly get => _cPaint.Value.ClipPathViewBoxH; init { if (!EqualityComparer<float>.Default.Equals(_cPaint.Value.ClipPathViewBoxH, value)) { var c = _cPaint.Value; c.ClipPathViewBoxH = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }

    /// <summary>Paint-order opt-in (the declarative <c>z-index</c> of a hovered card): while the pointer hover path
    /// passes through this element (it is hovered, or hover-within, or its hover fade is still decaying), it paints
    /// AFTER (above) its non-elevated siblings, so its elevation halo is not overpainted by a later sibling. LAYOUT and
    /// HIT-TESTING are unaffected (pure record order). At most one sibling holds the hover path, so the recorder defers
    /// it with O(1) space and zero allocation; at rest (no hover) the element paints in normal document order.</summary>
    public bool HoverElevatePaint { readonly get => _cPaint.Value.HoverElevatePaint; init { if (!EqualityComparer<bool>.Default.Equals(_cPaint.Value.HoverElevatePaint, value)) { var c = _cPaint.Value; c.HoverElevatePaint = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }

    /// <summary>Drag-ARM barrier: a press that lands on (or inside) this element never arms a DRAGGABLE ANCESTOR's
    /// gesture. A draggable row/card normally lets a press on any of its children start the drag (the WinUI
    /// item-container rule, implemented as an upward walk from the press target); set this on a child that is its own
    /// affordance — a card's play FAB, its "…" corner button, an inline toggle — so pressing it stays a press on THAT
    /// control instead of becoming a handle for dragging the card. Purely a discriminator: layout, hit-testing, and
    /// this element's own click/press handling are unaffected, and it says nothing about whether the element itself is
    /// draggable (set <c>Draggable</c>/<c>CanDrag</c> for that).</summary>
    public bool BlocksDragArm { readonly get => _cInput.Value.BlocksDragArm; init { if (!EqualityComparer<bool>.Default.Equals(_cInput.Value.BlocksDragArm, value)) { var c = _cInput.Value; c.BlocksDragArm = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }

    /// <summary>A pointer LISTENER that is not an interaction scope (the ToolTip service wrapper): it carries pointer
    /// handlers, so it is hit-testable and receives them normally, but the hover cascade, the lazy-mount hover seed and
    /// the un-hover re-resolve look THROUGH it to the nearest real interactive ancestor. A card's hover then still
    /// reveals a wrapped play FAB. Hit-testing and handler delivery are unchanged.</summary>
    public bool HoverScopeTransparent { readonly get => _cInput.Value.HoverScopeTransparent; init { if (!EqualityComparer<bool>.Default.Equals(_cInput.Value.HoverScopeTransparent, value)) { var c = _cInput.Value; c.HoverScopeTransparent = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>A per-node behaviour WITHOUT a component (<see cref="NodeAttachment"/>): the reconciler binds the spec to this box's
    /// node and keeps its per-node state while the node lives; the state's handlers are this box's wherever it declares none of its
    /// own. A tooltip is one: idle on every box it wraps but the one under the pointer, so it costs a spec and a small state, not a
    /// mounted component per box.</summary>
    public NodeAttachment? Attachment { readonly get => _cInput.Value.Attachment; init { if (!EqualityComparer<NodeAttachment?>.Default.Equals(_cInput.Value.Attachment, value)) { var c = _cInput.Value; c.Attachment = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }
    /// <summary>This box HOSTS the keyboard-focus ring of its nearest focused ancestor: where the focused node is a wide container (a virtual list's
    /// slot root spanning the whole viewport) and only part of it is the item the user sees (a row standing between inset spacers), the ring is drawn
    /// on this box's rect instead of the focused node's, with the focused node's <see cref="FocusVisualMargin"/>. The first host found under the focused
    /// node wins (a shallow search, at most a few levels down); none found: the focused node's own ring, as before. Paint only: focus, hit-testing and
    /// navigation are unchanged.</summary>
    public bool FocusRingHost { readonly get => _cInput.Value.FocusRingHost; init { if (!EqualityComparer<bool>.Default.Equals(_cInput.Value.FocusRingHost, value)) { var c = _cInput.Value; c.FocusRingHost = value; _cInput = ColdRef<BoxColdInput>.Of(in c); } } }

    /// <summary>Clip-ESCAPE root for a hover-elevated descendant (pairs with <see cref="HoverElevatePaint"/>): set on a
    /// clipping viewport (a shelf's paged strip) to let the hovered card's lift + halo paint OUTSIDE this clip. The
    /// recorder HOISTS the deferred elevated descendant out of this node's whole record scope — its clip AND its
    /// edge-fade — and records it after the scope closes, against the clip in effect outside this node. Resting content
    /// still clips exactly here (nothing else escapes). Innermost flagged ancestor wins; layout/hit-testing unaffected.</summary>
    public bool HoverElevateClipRoot { readonly get => _cPaint.Value.HoverElevateClipRoot; init { if (!EqualityComparer<bool>.Default.Equals(_cPaint.Value.HoverElevateClipRoot, value)) { var c = _cPaint.Value; c.HoverElevateClipRoot = value; _cPaint = ColdRef<BoxColdPaint>.Of(in c); } } }

    /// <summary>Layout firewall (opt-in): declare that this box's size is PARENT-determined (it fills/clips and is never
    /// content-sized), so a re-render or state change deep inside its subtree re-solves ONLY this subtree (scoped layout)
    /// instead of falling back to a full-tree layout from the root. Use on a page/content host that fills the shell content
    /// region. Contract: only set this where the box truly cannot need to change its own outer size from a descendant — the
    /// scoped relayout reuses its current bounds. A window resize still triggers a full layout, so resize stays correct.</summary>
    public bool IsolateLayout { readonly get => _cMisc.Value.IsolateLayout; init { if (!EqualityComparer<bool>.Default.Equals(_cMisc.Value.IsolateLayout, value)) { var c = _cMisc.Value; c.IsolateLayout = value; _cMisc = ColdRef<BoxColdMisc>.Of(in c); } } }

    /// <summary>Opt this box into general layout-change animation: the host diffs its presented rect vs its new
    /// laid-out rect each commit and drives the residual through the spec's channels/dynamics (no relayout, no
    /// per-frame re-render). Null ⇒ snap (the default). See <see cref="FluentGpu.Foundation.LayoutTransition"/>.</summary>
    public LayoutTransition? Animate { readonly get => _cGesture.Value.Animate; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_cGesture.Value.Animate, value)) { var c = _cGesture.Value; c.Animate = value; _cGesture = ColdRef<BoxColdGesture>.Of(in c); } } }

    /// <summary>Opt this child OUT of a <see cref="FluentGpu.Foundation.SizeMode.ScaleCorrect"/> ancestor's scale: the
    /// recorder applies the inverse scale so the child stays undistorted (Framer-Motion projection correction).</summary>
    public bool CounterScale { readonly get => _cMotion.Value.CounterScale; init { if (!EqualityComparer<bool>.Default.Equals(_cMotion.Value.CounterScale, value)) { var c = _cMotion.Value; c.CounterScale = value; _cMotion = ColdRef<BoxColdMotion>.Of(in c); } } }

    // Flexbox
    /// <summary>Unified channels (Prop&lt;T&gt;): static size, thunk, or concrete signal (bound ⇒ scoped relayout).</summary>
    public Prop<float> Width { get; init; } = float.NaN;
    public Prop<float> Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { readonly get => _cMisc.Value.MaxHeight; init { if (!EqualityComparer<float>.Default.Equals(_cMisc.Value.MaxHeight, value)) { var c = _cMisc.Value; c.MaxHeight = value; _cMisc = ColdRef<BoxColdMisc>.Of(in c); } } }
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    /// <summary>ZStack-only horizontal self-placement (CSS <c>justify-self</c>, WinUI <c>HorizontalAlignment</c> on an
    /// overlay child): where this child sits on the X axis of a <c>ZStack = true</c> parent.
    /// <see cref="FlexAlign.Auto"/> = inherit the stack's <c>Justify</c>. Ignored outside a ZStack (a flex container's
    /// main axis is distributed by the container's <c>Justify</c>, not per-child).</summary>
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public FlexJustify Justify { get; init; } = FlexJustify.Start;
    public FlexAlign AlignItems { get; init; } = FlexAlign.Stretch;
    public bool Wrap { get; init; }
    /// <summary>ZStack-only measure opt-out (A3): this child ignores the stack's own constrained width and measures
    /// at PositiveInfinity instead, reporting its NATURAL content width — for a layer that must overflow its ZStack
    /// parent's bounds (a rail tooltip whose stack pins a fixed narrow width) rather than being squeezed to it.
    /// Ignored outside a ZStack; the parent's own size is unaffected by it (an explicit parent Width still wins).</summary>
    public bool MeasureUnboundedWidth { readonly get => _cMisc.Value.MeasureUnboundedWidth; init { if (!EqualityComparer<bool>.Default.Equals(_cMisc.Value.MeasureUnboundedWidth, value)) { var c = _cMisc.Value; c.MeasureUnboundedWidth = value; _cMisc = ColdRef<BoxColdMisc>.Of(in c); } } }
    /// <summary>CSS <c>aspect-ratio</c> (width÷height): derive the missing extent for a fluid box. NaN (default) = off.
    /// When exactly one of Width/Height is set the other is derived; when both are fluid, the box takes the offered
    /// width and derives its height. Both explicit ⇒ aspect ignored. Routed through the shared <c>LayoutInput.AspectRatio</c>
    /// column (the ImageEl precedent), so an aspect-sized box is NOT a layout boundary (one dimension stays NaN).</summary>
    public float AspectRatio { readonly get => _cMisc.Value.AspectRatio; init { if (!EqualityComparer<float>.Default.Equals(_cMisc.Value.AspectRatio, value)) { var c = _cMisc.Value; c.AspectRatio = value; _cMisc = ColdRef<BoxColdMisc>.Of(in c); } } }
    static void IElementKind<BoxEl>.FreezeMembers(ref BoxEl v)
    {
        v._ecold = v._ecold.Frozen();
        v._cPaint = v._cPaint.Frozen();
        v._cPaintFx = v._cPaintFx.Frozen();
        v._cInput = v._cInput.Frozen();
        v._cGesture = v._cGesture.Frozen();
        v._cMotion = v._cMotion.Frozen();
        v._cHover = v._cHover.Frozen();
        v._cMisc = v._cMisc.Frozen();
        v = v with { Children = v.Children.Freeze() };
    }

    public readonly bool Equals(BoxEl o) => EqualsIn(in o);
    internal readonly bool EqualsIn(in BoxEl o) =>
        EqualityComparer<string?>.Default.Equals(Key, o.Key)
        && EqualityComparer<Prop<bool>>.Default.Equals(Visible, o.Visible)
        && EqualityComparer<byte>.Default.Equals(Direction, o.Direction)
        && EqualityComparer<float>.Default.Equals(Gap, o.Gap)
        && EqualityComparer<Edges4>.Default.Equals(Padding, o.Padding)
        && EqualityComparer<Edges4>.Default.Equals(Margin, o.Margin)
        && EqualityComparer<Prop<ColorF>>.Default.Equals(Fill, o.Fill)
        && EqualityComparer<Prop<ColorF>>.Default.Equals(BorderColor, o.BorderColor)
        && EqualityComparer<float>.Default.Equals(BorderWidth, o.BorderWidth)
        && EqualityComparer<Prop<CornerRadius4>>.Default.Equals(Corners, o.Corners)
        && EqualityComparer<Action?>.Default.Equals(OnClick, o.OnClick)
        && EqualityComparer<CursorId?>.Default.Equals(Cursor, o.Cursor)
        && EqualityComparer<Prop<bool>>.Default.Equals(HitTestVisible, o.HitTestVisible)
        && EqualityComparer<bool>.Default.Equals(IsEnabled, o.IsEnabled)
        && EqualityComparer<bool>.Default.Equals(Focusable, o.Focusable)
        && EqualityComparer<AutomationRole>.Default.Equals(Role, o.Role)
        && EqualityComparer<Prop<float>>.Default.Equals(Opacity, o.Opacity)
        && EqualityComparer<Action<NodeHandle>?>.Default.Equals(OnRealized, o.OnRealized)
        && EqualityComparer<ElementList>.Default.Equals(Children, o.Children)
        && EqualityComparer<bool>.Default.Equals(ZStack, o.ZStack)
        && EqualityComparer<bool>.Default.Equals(ClipToBounds, o.ClipToBounds)
        && EqualityComparer<Prop<float>>.Default.Equals(Width, o.Width)
        && EqualityComparer<Prop<float>>.Default.Equals(Height, o.Height)
        && EqualityComparer<float>.Default.Equals(MinWidth, o.MinWidth)
        && EqualityComparer<float>.Default.Equals(MinHeight, o.MinHeight)
        && EqualityComparer<float>.Default.Equals(MaxWidth, o.MaxWidth)
        && EqualityComparer<float>.Default.Equals(Grow, o.Grow)
        && EqualityComparer<float>.Default.Equals(Shrink, o.Shrink)
        && EqualityComparer<float>.Default.Equals(Basis, o.Basis)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignSelf, o.AlignSelf)
        && EqualityComparer<FlexAlign>.Default.Equals(JustifySelf, o.JustifySelf)
        && EqualityComparer<FlexJustify>.Default.Equals(Justify, o.Justify)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignItems, o.AlignItems)
        && EqualityComparer<bool>.Default.Equals(Wrap, o.Wrap)
        && _ecold.ValueEquals(in o._ecold)
        && _cPaint.ValueEquals(in o._cPaint)
        && _cPaintFx.ValueEquals(in o._cPaintFx)
        && _cInput.ValueEquals(in o._cInput)
        && _cGesture.ValueEquals(in o._cGesture)
        && _cMotion.ValueEquals(in o._cMotion)
        && _cHover.ValueEquals(in o._cHover)
        && _cMisc.ValueEquals(in o._cMisc);
    public override readonly bool Equals(object? obj) => obj is BoxEl { } o && EqualsIn(in o);
    public override readonly int GetHashCode() => HashCode.Combine(1, Key);
}

/// <summary>
/// A CSS-Grid container: column tracks (Pixel/Star/Auto) + gaps; children auto-flow row-major into cells. Rows take
/// <see cref="RowHeight"/> (or the tallest cell when NaN). True tracks — every cell in a column shares its width
/// (unlike nested-flex faking). Width-aware: resolves inside a ScrollView. (layout.md §7.)
/// </summary>
public partial struct GridEl : IElementKind<GridEl>, IEquatable<GridEl>
{
    public const ushort TypeId = 9;
    static ushort IElementKind<GridEl>.Kind => 9;
    public static implicit operator Element(GridEl value) => Element.Push(in value);
    public static explicit operator GridEl(Element element) => element.As<GridEl>();
    public GridEl() { }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public readonly ushort ElementTypeId => 9;

    public TrackSize[] Columns { get; init; } = [];
    public float ColGap { get; init; }
    public float RowGap { get; init; }
    public float RowHeight { get; init; } = float.NaN;   // NaN = auto (tallest cell in the row)
    public float MinColWidth { get; init; }              // > 0 = auto-fill: as many 1fr columns as fit at this min width (Columns ignored)
    /// <summary>Auto-fill mode only: the column count never exceeds this (0 = unlimited). The capped tracks still
    /// share the full width (they grow past <see cref="MinColWidth"/>) — CSS <c>repeat(auto-fill, …)</c> with a max
    /// column count. Ignored by fixed-track grids.</summary>
    public int MaxColumns { get; init; }
    public ElementList Children { get; init; } = [];

    /// <summary>The auto-fill column count for an inner width — THE formula the layout engine uses
    /// (<c>FlexLayout.GridColCount</c>), exposed so app-side form rules (a cell count that depends on the column
    /// count) compute the exact number the grid will lay out: <c>max(1, ⌊(innerW + colGap) / (minColWidth + colGap)⌋)</c>,
    /// clamped to <paramref name="maxColumns"/> when that is &gt; 0. Unknown width (≤ 0, NaN, ∞) or a non-positive
    /// <paramref name="minColWidth"/> ⇒ 1.</summary>
    public static int AutoFillColumnCount(float innerW, float minColWidth, float colGap, int maxColumns = 0)
    {
        if (minColWidth <= 0f || !(innerW > 0f) || float.IsInfinity(innerW)) return 1;
        int count = System.Math.Max(1, (int)((innerW + colGap) / (minColWidth + colGap)));
        return maxColumns > 0 && count > maxColumns ? maxColumns : count;
    }

    // sizing/participation
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    /// <summary>ZStack-only horizontal self-placement (CSS <c>justify-self</c>, WinUI <c>HorizontalAlignment</c> on an
    /// overlay child): where this child sits on the X axis of a <c>ZStack = true</c> parent.
    /// <see cref="FlexAlign.Auto"/> = inherit the stack's <c>Justify</c>. Ignored outside a ZStack (a flex container's
    /// main axis is distributed by the container's <c>Justify</c>, not per-child).</summary>
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    public Edges4 Padding { get; init; }
    static void IElementKind<GridEl>.FreezeMembers(ref GridEl v)
    {
        v._ecold = v._ecold.Frozen();
        v = v with { Children = v.Children.Freeze() };
    }

    public readonly bool Equals(GridEl o) => EqualsIn(in o);
    internal readonly bool EqualsIn(in GridEl o) =>
        EqualityComparer<string?>.Default.Equals(Key, o.Key)
        && EqualityComparer<Prop<bool>>.Default.Equals(Visible, o.Visible)
        && EqualityComparer<TrackSize[]>.Default.Equals(Columns, o.Columns)
        && EqualityComparer<float>.Default.Equals(ColGap, o.ColGap)
        && EqualityComparer<float>.Default.Equals(RowGap, o.RowGap)
        && EqualityComparer<float>.Default.Equals(RowHeight, o.RowHeight)
        && EqualityComparer<float>.Default.Equals(MinColWidth, o.MinColWidth)
        && EqualityComparer<int>.Default.Equals(MaxColumns, o.MaxColumns)
        && EqualityComparer<ElementList>.Default.Equals(Children, o.Children)
        && EqualityComparer<float>.Default.Equals(Width, o.Width)
        && EqualityComparer<float>.Default.Equals(Height, o.Height)
        && EqualityComparer<float>.Default.Equals(Grow, o.Grow)
        && EqualityComparer<float>.Default.Equals(Shrink, o.Shrink)
        && EqualityComparer<float>.Default.Equals(Basis, o.Basis)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignSelf, o.AlignSelf)
        && EqualityComparer<FlexAlign>.Default.Equals(JustifySelf, o.JustifySelf)
        && EqualityComparer<Edges4>.Default.Equals(Margin, o.Margin)
        && EqualityComparer<Edges4>.Default.Equals(Padding, o.Padding)
        && _ecold.ValueEquals(in o._ecold);
    public override readonly bool Equals(object? obj) => obj is GridEl { } o && EqualsIn(in o);
    public override readonly int GetHashCode() => HashCode.Combine(9, Key);
}

/// <summary>A text run. <see cref="FontFamily"/> selects the face — a system family name ("Segoe UI",
/// "Segoe Fluent Icons") or a custom file as <c>"path/to.ttf#Family Name"</c> (the WinUI syntax). Null = the theme
/// body font. Combine with an icon-font family + a private-use glyph to render icons (see <c>Ui.Icon</c>).</summary>
/// <summary>A leaf stroked polyline, drawn analytically by the renderer and revealable through stroke trim.</summary>
public partial struct PolylineStrokeEl : IElementKind<PolylineStrokeEl>, IEquatable<PolylineStrokeEl>
{
    public const ushort TypeId = 11;
    static ushort IElementKind<PolylineStrokeEl>.Kind => 11;
    public static implicit operator Element(PolylineStrokeEl value) => Element.Push(in value);
    public static explicit operator PolylineStrokeEl(Element element) => element.As<PolylineStrokeEl>();
    public PolylineStrokeEl() { }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public readonly ushort ElementTypeId => 11;

    public Point2 P0 { get; init; }
    public Point2 P1 { get; init; }
    public Point2 P2 { get; init; }
    public Point2 P3 { get; init; }
    public int PointCount { get; init; } = 2;
    public ColorF Color { get; init; }
    public float Thickness { get; init; } = 1f;
    public float TrimStart { get; init; }
    public float TrimEnd { get; init; } = 1f;
    public bool RoundCaps { get; init; } = true;

    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    public float ScaleX { get; init; } = 1f;
    public float ScaleY { get; init; } = 1f;
    public float Rotation { get; init; }
    public float Opacity { get; init; } = 1f;
    public float TransformOriginX { get; init; } = 0.5f;
    public float TransformOriginY { get; init; } = 0.5f;
    public float HoverScale { get; init; } = 1f;
    public float PressScale { get; init; } = 1f;

    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    /// <summary>ZStack-only horizontal self-placement (CSS <c>justify-self</c>, WinUI <c>HorizontalAlignment</c> on an
    /// overlay child): where this child sits on the X axis of a <c>ZStack = true</c> parent.
    /// <see cref="FlexAlign.Auto"/> = inherit the stack's <c>Justify</c>. Ignored outside a ZStack (a flex container's
    /// main axis is distributed by the container's <c>Justify</c>, not per-child).</summary>
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    static void IElementKind<PolylineStrokeEl>.FreezeMembers(ref PolylineStrokeEl v)
    {
        v._ecold = v._ecold.Frozen();
    }

    public readonly bool Equals(PolylineStrokeEl o) => EqualsIn(in o);
    internal readonly bool EqualsIn(in PolylineStrokeEl o) =>
        EqualityComparer<string?>.Default.Equals(Key, o.Key)
        && EqualityComparer<Prop<bool>>.Default.Equals(Visible, o.Visible)
        && EqualityComparer<Point2>.Default.Equals(P0, o.P0)
        && EqualityComparer<Point2>.Default.Equals(P1, o.P1)
        && EqualityComparer<Point2>.Default.Equals(P2, o.P2)
        && EqualityComparer<Point2>.Default.Equals(P3, o.P3)
        && EqualityComparer<int>.Default.Equals(PointCount, o.PointCount)
        && EqualityComparer<ColorF>.Default.Equals(Color, o.Color)
        && EqualityComparer<float>.Default.Equals(Thickness, o.Thickness)
        && EqualityComparer<float>.Default.Equals(TrimStart, o.TrimStart)
        && EqualityComparer<float>.Default.Equals(TrimEnd, o.TrimEnd)
        && EqualityComparer<bool>.Default.Equals(RoundCaps, o.RoundCaps)
        && EqualityComparer<float>.Default.Equals(OffsetX, o.OffsetX)
        && EqualityComparer<float>.Default.Equals(OffsetY, o.OffsetY)
        && EqualityComparer<float>.Default.Equals(ScaleX, o.ScaleX)
        && EqualityComparer<float>.Default.Equals(ScaleY, o.ScaleY)
        && EqualityComparer<float>.Default.Equals(Rotation, o.Rotation)
        && EqualityComparer<float>.Default.Equals(Opacity, o.Opacity)
        && EqualityComparer<float>.Default.Equals(TransformOriginX, o.TransformOriginX)
        && EqualityComparer<float>.Default.Equals(TransformOriginY, o.TransformOriginY)
        && EqualityComparer<float>.Default.Equals(HoverScale, o.HoverScale)
        && EqualityComparer<float>.Default.Equals(PressScale, o.PressScale)
        && EqualityComparer<float>.Default.Equals(Width, o.Width)
        && EqualityComparer<float>.Default.Equals(Height, o.Height)
        && EqualityComparer<float>.Default.Equals(MinWidth, o.MinWidth)
        && EqualityComparer<float>.Default.Equals(MinHeight, o.MinHeight)
        && EqualityComparer<float>.Default.Equals(MaxWidth, o.MaxWidth)
        && EqualityComparer<float>.Default.Equals(MaxHeight, o.MaxHeight)
        && EqualityComparer<float>.Default.Equals(Grow, o.Grow)
        && EqualityComparer<float>.Default.Equals(Shrink, o.Shrink)
        && EqualityComparer<float>.Default.Equals(Basis, o.Basis)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignSelf, o.AlignSelf)
        && EqualityComparer<FlexAlign>.Default.Equals(JustifySelf, o.JustifySelf)
        && EqualityComparer<Edges4>.Default.Equals(Margin, o.Margin)
        && _ecold.ValueEquals(in o._ecold);
    public override readonly bool Equals(object? obj) => obj is PolylineStrokeEl { } o && EqualsIn(in o);
    public override readonly int GetHashCode() => HashCode.Combine(11, Key);
}

/// <summary>
/// A tessellated vector-path leaf (gpu-renderer.md §5): its own fill (<see cref="Fill"/>/<see cref="Rule"/>) and/or
/// stroke (<see cref="StrokeColor"/>/<see cref="Stroke"/>) over an authored <see cref="Geometry"/>.
///
/// <para>A NEW LEAF, not a <c>BoxEl.Path</c>-style decoration. Contrast <see cref="ArcSpec"/>, which IS a
/// <see cref="BoxEl"/> decoration (<see cref="BoxEl.Arc"/>): an arc is drawn ON a box that may ALSO carry its own
/// fill/border/corners (ProgressRing's track — the ring and the track are two visuals on one node). A path with its
/// own view-box, fill rule AND stroke is instead the node's ENTIRE visual — <c>Fill</c>/<c>Corners</c>/
/// <c>BorderWidth</c> on the same node would be meaningless-but-legal (which fill? which corners, when the path is
/// itself an arbitrary silhouette?). Group transform/opacity/clip already exist on an ordinary <see cref="BoxEl"/>/
/// ZStack parent, so this element carries only the per-leaf transform/flex boilerplate below (copied verbatim from
/// <see cref="PolylineStrokeEl"/>) plus its own geometry fields.</para>
/// </summary>
public partial struct PathEl : IElementKind<PathEl>, IEquatable<PathEl>
{
    public const ushort TypeId = 16;
    static ushort IElementKind<PathEl>.Kind => 16;
    public static implicit operator Element(PathEl value) => Element.Push(in value);
    public static explicit operator PathEl(Element element) => element.As<PathEl>();
    public PathEl() { }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    // ElementTypeId 16. IDs 1-5 and 7-15 are already taken by other Element records (see the grep of every
    // `ElementTypeId =>` in this assembly). 6 was documented (by the approved plan this element implements) as "a
    // free hole from a retired type — do NOT recycle it" — but as-built, 6 is CURRENTLY LIVE (VirtualListEl,
    // Reconciler/VirtualListEl.cs), so that premise did not match this tree at the time this was authored. It does
    // not change the outcome: 1-15 are all taken regardless, so 16 remains the correct next-free id. Left here so a
    // future reader isn't confused by a stale "6 is free" claim anywhere upstream of this file.
    public readonly ushort ElementTypeId => 16;

    /// <summary>The authored vector geometry (parallel verb/point streams + fill rule + content epoch). Null or empty
    /// (<see cref="PathData.VerbCount"/> == 0) draws nothing.</summary>
    public PathData? Geometry { get; init; }
    public ColorF Fill { get; init; }
    public FillRule Rule { get; init; } = FillRule.NonZero;
    public ColorF StrokeColor { get; init; }
    public StrokeStyle Stroke { get; init; }
    /// <summary>Authored stroke-trim fractions (0..1); overridden per frame by a live <c>AnimChannel.StrokeTrimStart/
    /// End</c> track (the NaN-sentinel convention <c>DrawArc</c>/<c>DrawPolylineStroke</c> already use) without ever
    /// reaching the tessellation-realization cache key.</summary>
    public float TrimStart { get; init; }
    public float TrimEnd { get; init; } = 1f;
    /// <summary>Mirrors <c>PathTrimSpace</c> (Render/PathTessellator.cs): 0 = PerContour (default), 1 = WholePath.</summary>
    public byte TrimMode { get; init; }
    /// <summary>0 (default) = <see cref="Geometry"/> is already node-local DIP. Paired with <see cref="ViewBoxH"/> &gt; 0,
    /// bakes a uniform-fit scale into this node's world transform at record time, so one authored path (e.g. a
    /// 24x24-unit icon) renders correctly at any box size.</summary>
    public float ViewBoxW { get; init; }
    public float ViewBoxH { get; init; }
    /// <summary>Opt in to hit-testing against the filled geometry itself (honouring <see cref="FillRule"/>) instead of
    /// the node's bounding box, so a point in a donut's hole falls through to whatever is beneath it. Default
    /// <c>false</c> keeps plain box behaviour. This is the ONE licensed exception to the engine's
    /// "paint-derived hit-testing stays deliberately absent" rule (gpu-renderer.md §5.1: "hit-test shares the fill
    /// RULE, not just the vertices") — see <c>InputDispatcher.PathGeometryAdmits</c>, which mirrors the recorder's
    /// <see cref="ViewBoxW"/>/<see cref="ViewBoxH"/> fit-scale so the click and the pixels agree.
    /// <para>Note: a <c>PathEl</c> cannot itself own a click today — the reconciler only writes
    /// <c>InteractionInfo.HandlerMask</c>/<c>NodeFlags.HitTestVisible</c> for <see cref="BoxEl"/> — so this currently
    /// affects the <c>HitAny</c> lane (drag-drop targeting, scroll-target and gesture resolution). Wrap the path in a
    /// <see cref="BoxEl"/> if you need an actual click handler.</para></summary>
    public bool HitTestGeometry { get; init; }

    /// <summary>Called once when this path is realized into the scene, with its node handle (mirrors
    /// <see cref="BoxEl.OnRealized"/>) — the ONLY way to obtain the <c>NodeHandle</c> a caller needs to drive this
    /// path's own <see cref="AnimChannel.StrokeTrimStart"/>/<see cref="AnimChannel.StrokeTrimEnd"/>/transform/opacity
    /// tracks via <c>AnimEngine.Keyframes</c> (an authored draw-on stroke-trim loop, e.g.) — without it a <c>PathEl</c>
    /// could declare <see cref="TrimStart"/>/<see cref="TrimEnd"/> statics but never an animated timeline. Fires at
    /// mount only.</summary>
    public Action<NodeHandle>? OnRealized { get; init; }

    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    public float ScaleX { get; init; } = 1f;
    public float ScaleY { get; init; } = 1f;
    public float Rotation { get; init; }
    public float Opacity { get; init; } = 1f;
    public float TransformOriginX { get; init; } = 0.5f;
    public float TransformOriginY { get; init; } = 0.5f;
    public float HoverScale { get; init; } = 1f;
    public float PressScale { get; init; } = 1f;

    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    static void IElementKind<PathEl>.FreezeMembers(ref PathEl v)
    {
        v._ecold = v._ecold.Frozen();
    }

    public readonly bool Equals(PathEl o) => EqualsIn(in o);
    internal readonly bool EqualsIn(in PathEl o) =>
        EqualityComparer<string?>.Default.Equals(Key, o.Key)
        && EqualityComparer<Prop<bool>>.Default.Equals(Visible, o.Visible)
        && EqualityComparer<PathData?>.Default.Equals(Geometry, o.Geometry)
        && EqualityComparer<ColorF>.Default.Equals(Fill, o.Fill)
        && EqualityComparer<FillRule>.Default.Equals(Rule, o.Rule)
        && EqualityComparer<ColorF>.Default.Equals(StrokeColor, o.StrokeColor)
        && EqualityComparer<StrokeStyle>.Default.Equals(Stroke, o.Stroke)
        && EqualityComparer<float>.Default.Equals(TrimStart, o.TrimStart)
        && EqualityComparer<float>.Default.Equals(TrimEnd, o.TrimEnd)
        && EqualityComparer<byte>.Default.Equals(TrimMode, o.TrimMode)
        && EqualityComparer<float>.Default.Equals(ViewBoxW, o.ViewBoxW)
        && EqualityComparer<float>.Default.Equals(ViewBoxH, o.ViewBoxH)
        && EqualityComparer<bool>.Default.Equals(HitTestGeometry, o.HitTestGeometry)
        && EqualityComparer<Action<NodeHandle>?>.Default.Equals(OnRealized, o.OnRealized)
        && EqualityComparer<float>.Default.Equals(OffsetX, o.OffsetX)
        && EqualityComparer<float>.Default.Equals(OffsetY, o.OffsetY)
        && EqualityComparer<float>.Default.Equals(ScaleX, o.ScaleX)
        && EqualityComparer<float>.Default.Equals(ScaleY, o.ScaleY)
        && EqualityComparer<float>.Default.Equals(Rotation, o.Rotation)
        && EqualityComparer<float>.Default.Equals(Opacity, o.Opacity)
        && EqualityComparer<float>.Default.Equals(TransformOriginX, o.TransformOriginX)
        && EqualityComparer<float>.Default.Equals(TransformOriginY, o.TransformOriginY)
        && EqualityComparer<float>.Default.Equals(HoverScale, o.HoverScale)
        && EqualityComparer<float>.Default.Equals(PressScale, o.PressScale)
        && EqualityComparer<float>.Default.Equals(Width, o.Width)
        && EqualityComparer<float>.Default.Equals(Height, o.Height)
        && EqualityComparer<float>.Default.Equals(MinWidth, o.MinWidth)
        && EqualityComparer<float>.Default.Equals(MinHeight, o.MinHeight)
        && EqualityComparer<float>.Default.Equals(MaxWidth, o.MaxWidth)
        && EqualityComparer<float>.Default.Equals(MaxHeight, o.MaxHeight)
        && EqualityComparer<float>.Default.Equals(Grow, o.Grow)
        && EqualityComparer<float>.Default.Equals(Shrink, o.Shrink)
        && EqualityComparer<float>.Default.Equals(Basis, o.Basis)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignSelf, o.AlignSelf)
        && EqualityComparer<FlexAlign>.Default.Equals(JustifySelf, o.JustifySelf)
        && EqualityComparer<Edges4>.Default.Equals(Margin, o.Margin)
        && _ecold.ValueEquals(in o._ecold);
    public override readonly bool Equals(object? obj) => obj is PathEl { } o && EqualsIn(in o);
    public override readonly int GetHashCode() => HashCode.Combine(16, Key);
}

public partial struct TextEl : IElementKind<TextEl>, IEquatable<TextEl>
{
    public const ushort TypeId = 2;
    static ushort IElementKind<TextEl>.Kind => 2;
    public static implicit operator Element(TextEl value) => Element.Push(in value);
    public static explicit operator TextEl(Element element) => element.As<TextEl>();
    public Prop<string> Text { get; init; }
    public TextEl(Prop<string> Text) { this.Text = Text; }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    // The rarely-set channels (state colors, wipe, selection, underline, ...) live in one shared copy-on-write TextCold
    // (TextCold.cs), like BoxEl's blocks: a TextEl is allocated and `with`-copied on every render of every label.
    internal ColdRef<TextCold> _cold;
    public readonly ushort ElementTypeId => 2;

    // Unified channels: Text/Color each take a static value, a Func<T> thunk, or a concrete signal (the positional
    // ctor keeps `new TextEl("hi")` compiling via the string → Prop<string> conversion). Legacy *Bind init-aliases
    // below are deleted by the migration waves.

    public float Size { get; init; } = 14f;
    /// <summary>Bold sugar — kept for the many existing call sites; equivalent to <see cref="Weight"/> = 700.
    /// Ignored when <see cref="Weight"/> is set explicitly.</summary>
    public bool Bold { get; init; }
    /// <summary>Numeric font weight (WinUI FontWeight, 1–999 — the value IS the DWrite weight; the WinUI type ramp is
    /// SemiBold 600, BaseTextBlockStyle FontWeight, TextBlock_themeresources.xaml:13). 0 = unset → resolves from
    /// <see cref="Bold"/> (700) or Normal (400); see <see cref="ResolvedWeight"/>.</summary>
    public ushort Weight { get; init; }
    /// <summary>The weight the text pipeline shapes with: <see cref="Weight"/> when set, else Bold→700 / 400.</summary>
    public readonly ushort ResolvedWeight => Weight != 0 ? Weight : Bold ? (ushort)700 : (ushort)400;
    /// <summary>WinUI <c>TextElement.CharacterSpacing</c>: tracking in 1/1000 em (negative = tighter), applied as a
    /// per-glyph trailing advance adjustment after shaping (e.g. Pivot headers use −25).</summary>
    public float CharSpacing { readonly get => _cold.Value.CharSpacing; init { if (!EqualityComparer<float>.Default.Equals(_cold.Value.CharSpacing, value)) { var c = _cold.Value; c.CharSpacing = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>WinUI <c>TextBlock.LineHeight</c> in DIP (NaN = font-natural); interpreted per <see cref="LineStacking"/>.</summary>
    public float LineHeight { get; init; } = float.NaN;
    /// <summary>WinUI <c>TextBlock.LineStackingStrategy</c> (default MaxHeight — TextBlock_themeresources.xaml:16):
    /// how an explicit <see cref="LineHeight"/> combines with the font-natural line box.</summary>
    public LineStacking LineStacking { readonly get => _cold.Value.LineStacking; init { if (!EqualityComparer<LineStacking>.Default.Equals(_cold.Value.LineStacking, value)) { var c = _cold.Value; c.LineStacking = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>WinUI <c>TextBlock.TextLineBounds</c> (default Full — TextBlock_themeresources.xaml:17): Tight trims
    /// the measured line box to cap-height..baseline so vertical centering is optical (PersonPicture initials).</summary>
    public TextLineBounds LineBounds { readonly get => _cold.Value.LineBounds; init { if (!EqualityComparer<TextLineBounds>.Default.Equals(_cold.Value.LineBounds, value)) { var c = _cold.Value; c.LineBounds = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>Defaults to the live theme's <c>TextFillColorPrimary</c> (WinUI TextBlock default foreground —
    /// dark #FFFFFF / light #E4000000). The semantic brush is bound so text retained inside a stateful control still
    /// follows <c>Tok.Epoch</c> re-themes instead of freezing the construction-time color. This bound default stays
    /// virtualization-recyclable via the shared-singleton identity carve-out (<c>Ui.IsThemeTextBrush</c> / the
    /// reconciler's <c>IsRecyclable</c>), so default-colored list rows recycle rather than mount/remove.</summary>
    public Prop<ColorF> Color { get; init; } = Ui.PrimaryTextBrush;
    // Stateful foreground ramps (WinUI dims/recolors label & glyph foreground on hover/press/disabled/focus). A==0 ⇒
    // "no state color" → the recorder leaves Color/ColorBind untouched. Hover/Pressed ease with the nearest interactive
    // ancestor's progress (the same eased HoverT/PressT that cross-fades the box fill — no per-control animator).
    // Disabled/Focused are steps gated by the ancestor's NodeFlags.Disabled / this node's NodeFlags.Focused.
    public ColorF HoverColor { readonly get => _cold.Value.HoverColor; init { if (!EqualityComparer<ColorF>.Default.Equals(_cold.Value.HoverColor, value)) { var c = _cold.Value; c.HoverColor = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    public ColorF PressedColor { readonly get => _cold.Value.PressedColor; init { if (!EqualityComparer<ColorF>.Default.Equals(_cold.Value.PressedColor, value)) { var c = _cold.Value; c.PressedColor = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    public ColorF DisabledColor { readonly get => _cold.Value.DisabledColor; init { if (!EqualityComparer<ColorF>.Default.Equals(_cold.Value.DisabledColor, value)) { var c = _cold.Value; c.DisabledColor = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    public ColorF FocusedColor { readonly get => _cold.Value.FocusedColor; init { if (!EqualityComparer<ColorF>.Default.Equals(_cold.Value.FocusedColor, value)) { var c = _cold.Value; c.FocusedColor = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>WinUI <c>TextDecorations.Underline</c>: the recorder draws the face-metric underline bar (DWrite
    /// underlinePosition/underlineThickness, cached at measure on the scene's TextMeasureCache) under the run, in the
    /// SAME resolved foreground as the glyphs (hover/press ramps + BrushTransition). Single-line frame — per-line
    /// decoration of wrapped runs is the rich-text (SpanTextEl/RichTextBlock) pass. HyperlinkButton drives this from
    /// its HyperlinkUnderlineVisible/HighContrast gate (HyperLinkButton_Partial.cpp:207-212).</summary>
    public bool Underline { readonly get => _cold.Value.Underline; init { if (!EqualityComparer<bool>.Default.Equals(_cold.Value.Underline, value)) { var c = _cold.Value; c.Underline = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>WinUI <c>TextDecorations.Strikethrough</c>: the face-metric strikethrough bar (reuses the underline
    /// thickness, the DWrite convention) — e.g. CalendarView blackout dates.</summary>
    public bool Strikethrough { readonly get => _cold.Value.Strikethrough; init { if (!EqualityComparer<bool>.Default.Equals(_cold.Value.Strikethrough, value)) { var c = _cold.Value; c.Strikethrough = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>Implicit brush transition for the resting <see cref="Color"/>: a re-render that changes it on this LIVE
    /// node cross-fades over this duration (WinUI BrushTransition, 83ms in templates). NaN = snap.</summary>
    public float BrushTransitionMs { readonly get => _cold.Value.BrushTransitionMs; init { if (!EqualityComparer<float>.Default.Equals(_cold.Value.BrushTransitionMs, value)) { var c = _cold.Value; c.BrushTransitionMs = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>Optional left→right glyph WIPE fill (a general text-reveal — the lyrics karaoke uses it): glyphs left of
    /// <see cref="GlyphWipe.Split"/> use <see cref="GlyphWipe.Before"/>, right use <see cref="GlyphWipe.After"/>, with a
    /// soft boundary + optional per-glyph lift. Null = off. Carried in a sparse scene side-table (NOT on the hot paint
    /// struct), emitted as a gradient glyph run; advancing the split per frame is reshape-free.</summary>
    public GlyphWipe? Wipe { readonly get => _cold.Value.Wipe; init { if (!EqualityComparer<GlyphWipe?>.Default.Equals(_cold.Value.Wipe, value)) { var c = _cold.Value; c.Wipe = value; _cold = ColdRef<TextCold>.Of(in c); } } }

    /// <summary>Called once when this glyph run is realized into the scene, with its node handle — lets a control drive the
    /// node directly (the lyrics ticker advances THIS run's <see cref="Wipe"/> split per frame on the scene side-table).</summary>
    public Action<NodeHandle>? OnRealized { get; init; }
    /// <summary>Read-only text selection (rtb-02): mouse drag selects, double-click selects the word, triple-click all,
    /// Ctrl+C copies via the clipboard seam; the highlight reuses the editor's selection-rect path. Default FALSE —
    /// WinUI TextBlock selection is opt-in (TextBlock.cpp:583 IsTextSelectionEnabled property change creates the
    /// selection manager on demand); RichTextBlock turns it on by default (RichTextBlock.cpp:1730). A selectable run
    /// is focusable (Ctrl+C routes to it) and shows the I-beam cursor.</summary>
    public bool IsTextSelectionEnabled { readonly get => _cold.Value.IsTextSelectionEnabled; init { if (!EqualityComparer<bool>.Default.Equals(_cold.Value.IsTextSelectionEnabled, value)) { var c = _cold.Value; c.IsTextSelectionEnabled = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>Per-control selection highlight (api-04, WinUI <c>TextBlock.SelectionHighlightColor</c> —
    /// TextBlock.cpp:266/330). A==0 (default) = the engine/theme brush (the system accent,
    /// TextSelectionManager.cpp:52-56 GetDefaultSelectionHighlightColor → GetSystemAccentColor ≡ the host's
    /// TextEditStyle.SelectionFill).</summary>
    public ColorF SelectionHighlightColor { readonly get => _cold.Value.SelectionHighlightColor; init { if (!EqualityComparer<ColorF>.Default.Equals(_cold.Value.SelectionHighlightColor, value)) { var c = _cold.Value; c.SelectionHighlightColor = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    public string? FontFamily { get; init; }
    public DynamicTextKind DynamicText { readonly get => _cold.Value.DynamicText; init { if (!EqualityComparer<DynamicTextKind>.Default.Equals(_cold.Value.DynamicText, value)) { var c = _cold.Value; c.DynamicText = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>Line-break behavior (WinUI TextWrapping): NoWrap / Wrap / WrapWholeWords.</summary>
    public TextWrap Wrap { get; init; } = TextWrap.NoWrap;
    /// <summary>Overflow trimming (WinUI TextTrimming): None / Clip / CharacterEllipsis / WordEllipsis.</summary>
    public TextTrim Trim { get; init; } = TextTrim.None;
    /// <summary>Cap the visible line count (0 = unlimited); the last line trims per <see cref="Trim"/>.</summary>
    public int MaxLines { get; init; }
    /// <summary>Auto-fit (WinUI has no analogue; a Viewbox-for-text): when set (and &lt; <see cref="Size"/>, with
    /// <see cref="MaxLines"/> &gt; 0, a wrapping style, and a definite width), the layout SHRINKS the font from
    /// <see cref="Size"/> down to this floor to make the run fit in <see cref="MaxLines"/> at the available width —
    /// minimizing wraps and avoiding trimming. The largest size that fits wins; if even this floor doesn't fit,
    /// <see cref="Trim"/> ellipsis applies at the floor. Use a font-natural line height (leave <see cref="LineHeight"/>
    /// unset) so the chosen size's spacing scales with it. NaN = off (no auto-fit; the default).</summary>
    public float MinSize { readonly get => _cold.Value.MinSize; init { if (!EqualityComparer<float>.Default.Equals(_cold.Value.MinSize, value)) { var c = _cold.Value; c.MinSize = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>How <see cref="MinSize"/> picks its size (<see cref="TextFitMode"/>). <see cref="TextFitMode.Lines"/> (the default): the
    /// largest size that fits <see cref="MaxLines"/>. <see cref="TextFitMode.OneLineFirst"/>: the largest size that fits ONE line; a run
    /// that does not fit one line even at the floor wraps at the floor over up to <see cref="MaxLines"/> lines, then trims, and an
    /// authored <see cref="LineHeight"/> scales with the size chosen. For a display title that should shrink before it wraps.</summary>
    public TextFitMode FitMode { readonly get => _cold.Value.FitMode; init { if (!EqualityComparer<TextFitMode>.Default.Equals(_cold.Value.FitMode, value)) { var c = _cold.Value; c.FitMode = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>Case transform applied at SHAPING (E19), culture-invariantly and length-preserving: the string is stored and
    /// interned as written, the glyph run is shaped from its lower/upper-cased form, and no string is allocated per render.
    /// Part of the measure key (<c>TextStyle.Case</c>). Default <see cref="TextCase.AsWritten"/>. For theme-written labels
    /// (Wavee's <c>R.Case.For(LabelKind)</c>); content strings (titles, lyrics, user text) keep the default.</summary>
    public TextCase Case { readonly get => _cold.Value.Case; init { if (!EqualityComparer<TextCase>.Default.Equals(_cold.Value.Case, value)) { var c = _cold.Value; c.Case = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>Horizontal alignment of each line inside the text's box (WinUI <c>TextAlignment</c>): Start (default) /
    /// Center / End / Justify. Works for single-line text in a box wider than the text (Grow / Width / stretch — no
    /// wrapper <c>BoxEl</c> needed) and per line for wrapped text; trailing spaces never shift an End-aligned line.
    /// Start = left, End = right (no RTL paragraph direction yet). Selection, caret, hit-test and decorations follow.</summary>
    public TextAlign TextAlign { readonly get => _cold.Value.TextAlign; init { if (!EqualityComparer<TextAlign>.Default.Equals(_cold.Value.TextAlign, value)) { var c = _cold.Value; c.TextAlign = value; _cold = ColdRef<TextCold>.Of(in c); } } }
    /// <summary>OpenType features for the run — e.g. <c>FontFeatures.TabularFigures</c> so ticking numbers keep a
    /// fixed width. Fonts without a requested feature ignore it.</summary>
    public FontFeatures FontFeatures { readonly get => _cold.Value.FontFeatures; init { if (!EqualityComparer<FontFeatures>.Default.Equals(_cold.Value.FontFeatures, value)) { var c = _cold.Value; c.FontFeatures = value; _cold = ColdRef<TextCold>.Of(in c); } } }

    // Leaf layout participation. Text needs the same sizing/flex knobs as other leaves so wrapped runs can be
    // constrained by their container instead of contributing their full single-line width to parent measure.
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    /// <summary>ZStack-only horizontal self-placement (CSS <c>justify-self</c>, WinUI <c>HorizontalAlignment</c> on an
    /// overlay child): where this child sits on the X axis of a <c>ZStack = true</c> parent.
    /// <see cref="FlexAlign.Auto"/> = inherit the stack's <c>Justify</c>. Ignored outside a ZStack (a flex container's
    /// main axis is distributed by the container's <c>Justify</c>, not per-child).</summary>
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    static void IElementKind<TextEl>.FreezeMembers(ref TextEl v)
    {
        v._ecold = v._ecold.Frozen();
        v._cold = v._cold.Frozen();
    }

    public readonly bool Equals(TextEl o) => EqualsIn(in o);
    internal readonly bool EqualsIn(in TextEl o) =>
        EqualityComparer<Prop<string>>.Default.Equals(Text, o.Text)
        && EqualityComparer<string?>.Default.Equals(Key, o.Key)
        && EqualityComparer<Prop<bool>>.Default.Equals(Visible, o.Visible)
        && EqualityComparer<float>.Default.Equals(Size, o.Size)
        && EqualityComparer<bool>.Default.Equals(Bold, o.Bold)
        && EqualityComparer<ushort>.Default.Equals(Weight, o.Weight)
        && EqualityComparer<float>.Default.Equals(LineHeight, o.LineHeight)
        && EqualityComparer<Prop<ColorF>>.Default.Equals(Color, o.Color)
        && EqualityComparer<Action<NodeHandle>?>.Default.Equals(OnRealized, o.OnRealized)
        && EqualityComparer<string?>.Default.Equals(FontFamily, o.FontFamily)
        && EqualityComparer<TextWrap>.Default.Equals(Wrap, o.Wrap)
        && EqualityComparer<TextTrim>.Default.Equals(Trim, o.Trim)
        && EqualityComparer<int>.Default.Equals(MaxLines, o.MaxLines)
        && EqualityComparer<float>.Default.Equals(Width, o.Width)
        && EqualityComparer<float>.Default.Equals(Height, o.Height)
        && EqualityComparer<float>.Default.Equals(MinWidth, o.MinWidth)
        && EqualityComparer<float>.Default.Equals(MinHeight, o.MinHeight)
        && EqualityComparer<float>.Default.Equals(MaxWidth, o.MaxWidth)
        && EqualityComparer<float>.Default.Equals(MaxHeight, o.MaxHeight)
        && EqualityComparer<float>.Default.Equals(Grow, o.Grow)
        && EqualityComparer<float>.Default.Equals(Shrink, o.Shrink)
        && EqualityComparer<float>.Default.Equals(Basis, o.Basis)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignSelf, o.AlignSelf)
        && EqualityComparer<FlexAlign>.Default.Equals(JustifySelf, o.JustifySelf)
        && EqualityComparer<Edges4>.Default.Equals(Margin, o.Margin)
        && _ecold.ValueEquals(in o._ecold)
        && _cold.ValueEquals(in o._cold);
    public override readonly bool Equals(object? obj) => obj is TextEl { } o && EqualsIn(in o);
    public override readonly int GetHashCode() => HashCode.Combine(2, Key);
}

/// <summary>
/// A rich-text PARAGRAPH of typed inline runs (rtb-01 — the WinUI <c>TextBlock.Inlines</c>/RichTextBlock paragraph
/// model: Run/Bold/Hyperlink): the spans concatenate and shape as ONE flow (one wrap pass — a styled word never
/// re-flows independently of its sentence). Per-span weight/size/family/color/underline/strikethrough overlay the
/// base style here; a span with <c>OnClick</c> is a HYPERLINK — the engine resolves the Hand cursor over its laid
/// rects and fires the action on click (RichTextBlock.cpp:2995 / TextBlock.cpp:3488 SetCursor(MouseCursorHand));
/// accent foreground + underline are the CALLER's styling (HyperlinkForeground, generic.xaml:1120-1122).
/// No per-span italic: the shaper has no style axis yet (faces resolve by family+weight only).
/// </summary>
public partial struct SpanTextEl : IElementKind<SpanTextEl>, IEquatable<SpanTextEl>
{
    public const ushort TypeId = 12;
    static ushort IElementKind<SpanTextEl>.Kind => 12;
    public static implicit operator Element(SpanTextEl value) => Element.Push(in value);
    public static explicit operator SpanTextEl(Element element) => element.As<SpanTextEl>();
    public Prop<TextSpans> Spans { get; init; }
    public SpanTextEl(Prop<TextSpans> Spans) { this.Spans = Spans; }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public readonly ushort ElementTypeId => 12;

    /// <summary>Ctor sugar for a plain array (every pre-P2 <c>new SpanTextEl(spans)</c> call site keeps compiling
    /// unbound — the common case for a static paragraph). Bind a reactive <see cref="Foundation.SpanBuffer"/> fill
    /// via <c>Spans = Prop.Of(() =&gt; buffer.Current)</c> or the P3 <c>item.Spans(...)</c> authoring sugar instead.</summary>
    public SpanTextEl(TextSpan[] spans) : this((TextSpans)spans) { }

    /// <summary>Index-resolved hyperlink handler (P2, "bound spans with index-resolved clicks"): fires when the
    /// clicked span carries no <see cref="TextSpan.OnClick"/> of its own but is marked <see cref="TextSpan.IsLink"/>
    /// — the bound-row case, where minting a fresh <c>Action</c> closure per link per row per recycle would defeat
    /// the zero-alloc rebind story. The dispatcher resolves <c>spans[i].OnClick</c> first, else this, with the
    /// CURRENT clicked index. Mount-static (a sparse <c>SceneStore</c> table, not a bound <see cref="Prop{T}"/>
    /// channel) — same category as an ordinary <c>OnClick</c> handler.</summary>
    public Action<int>? OnSpanClick { get; init; }

    /// <summary>
    /// Optional atomic tail shown only when a finite wrapping <see cref="MaxLines"/> clips <see cref="Spans"/>.
    /// The text seam reserves the tail on the last visible line and trims the body at a legal break before it, so
    /// affordances such as “&#x2026; More” stay inline and their ordinary <see cref="TextSpan.OnClick"/> hit rects remain
    /// exact. Hidden when the body fits or when <see cref="MaxLines"/> is unlimited.
    /// </summary>
    public TextSpan[]? OverflowSuffix { get; init; }

    // ── base (paragraph) style — every TextSpan field that is unset inherits these ──
    public float Size { get; init; } = 14f;
    /// <summary>Base numeric font weight (0 = Normal 400); spans override per range.</summary>
    public ushort Weight { get; init; }
    /// <summary>Defaults to the live theme's <c>TextFillColorPrimary</c> (the TextEl default), resolved at construction.</summary>
    public ColorF Color { get; init; } = Tok.TextPrimary;
    public string? FontFamily { get; init; }
    public float CharSpacing { get; init; }
    public float LineHeight { get; init; } = float.NaN;
    public LineStacking LineStacking { get; init; } = LineStacking.MaxHeight;
    public TextLineBounds LineBounds { get; init; } = TextLineBounds.Full;
    /// <summary>Paragraphs WRAP by default — the WinUI BaseRichTextBlockStyle sets TextWrapping=Wrap
    /// (generic.xaml:13286-13295); pass NoWrap explicitly for a single-line run of spans.</summary>
    public TextWrap Wrap { get; init; } = TextWrap.Wrap;
    public TextTrim Trim { get; init; } = TextTrim.None;
    public int MaxLines { get; init; }
    /// <summary>Per-line horizontal alignment of the paragraph (see <see cref="TextEl.TextAlign"/>).</summary>
    public TextAlign TextAlign { get; init; }
    /// <summary>OpenType features for the whole paragraph (see <see cref="TextEl.FontFeatures"/>).</summary>
    public FontFeatures FontFeatures { get; init; }

    /// <summary>Read-only selection (rtb-02): drag selects across the whole paragraph flow, Ctrl+C copies. OPT-IN here
    /// (like WinUI TextBlock, TextBlock.cpp:583); the RichTextBlock control turns it on by default
    /// (IsTextSelectionEnabled=TRUE by default on CRichTextBlock, RichTextBlock.cpp:1730).</summary>
    public bool IsTextSelectionEnabled { get; init; }
    /// <summary>Per-control selection highlight (api-04). A==0 = the engine/theme accent brush
    /// (TextSelectionManager.cpp:52-56).</summary>
    public ColorF SelectionHighlightColor { get; init; }

    // Leaf layout participation (same knobs as TextEl so paragraphs are container-constrained).
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    /// <summary>ZStack-only horizontal self-placement (CSS <c>justify-self</c>, WinUI <c>HorizontalAlignment</c> on an
    /// overlay child): where this child sits on the X axis of a <c>ZStack = true</c> parent.
    /// <see cref="FlexAlign.Auto"/> = inherit the stack's <c>Justify</c>. Ignored outside a ZStack (a flex container's
    /// main axis is distributed by the container's <c>Justify</c>, not per-child).</summary>
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    static void IElementKind<SpanTextEl>.FreezeMembers(ref SpanTextEl v)
    {
        v._ecold = v._ecold.Frozen();
    }

    public readonly bool Equals(SpanTextEl o) => EqualsIn(in o);
    internal readonly bool EqualsIn(in SpanTextEl o) =>
        EqualityComparer<Prop<TextSpans>>.Default.Equals(Spans, o.Spans)
        && EqualityComparer<string?>.Default.Equals(Key, o.Key)
        && EqualityComparer<Prop<bool>>.Default.Equals(Visible, o.Visible)
        && EqualityComparer<Action<int>?>.Default.Equals(OnSpanClick, o.OnSpanClick)
        && EqualityComparer<TextSpan[]?>.Default.Equals(OverflowSuffix, o.OverflowSuffix)
        && EqualityComparer<float>.Default.Equals(Size, o.Size)
        && EqualityComparer<ushort>.Default.Equals(Weight, o.Weight)
        && EqualityComparer<ColorF>.Default.Equals(Color, o.Color)
        && EqualityComparer<string?>.Default.Equals(FontFamily, o.FontFamily)
        && EqualityComparer<float>.Default.Equals(CharSpacing, o.CharSpacing)
        && EqualityComparer<float>.Default.Equals(LineHeight, o.LineHeight)
        && EqualityComparer<LineStacking>.Default.Equals(LineStacking, o.LineStacking)
        && EqualityComparer<TextLineBounds>.Default.Equals(LineBounds, o.LineBounds)
        && EqualityComparer<TextWrap>.Default.Equals(Wrap, o.Wrap)
        && EqualityComparer<TextTrim>.Default.Equals(Trim, o.Trim)
        && EqualityComparer<int>.Default.Equals(MaxLines, o.MaxLines)
        && EqualityComparer<TextAlign>.Default.Equals(TextAlign, o.TextAlign)
        && EqualityComparer<FontFeatures>.Default.Equals(FontFeatures, o.FontFeatures)
        && EqualityComparer<bool>.Default.Equals(IsTextSelectionEnabled, o.IsTextSelectionEnabled)
        && EqualityComparer<ColorF>.Default.Equals(SelectionHighlightColor, o.SelectionHighlightColor)
        && EqualityComparer<float>.Default.Equals(Width, o.Width)
        && EqualityComparer<float>.Default.Equals(Height, o.Height)
        && EqualityComparer<float>.Default.Equals(MinWidth, o.MinWidth)
        && EqualityComparer<float>.Default.Equals(MinHeight, o.MinHeight)
        && EqualityComparer<float>.Default.Equals(MaxWidth, o.MaxWidth)
        && EqualityComparer<float>.Default.Equals(MaxHeight, o.MaxHeight)
        && EqualityComparer<float>.Default.Equals(Grow, o.Grow)
        && EqualityComparer<float>.Default.Equals(Shrink, o.Shrink)
        && EqualityComparer<float>.Default.Equals(Basis, o.Basis)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignSelf, o.AlignSelf)
        && EqualityComparer<FlexAlign>.Default.Equals(JustifySelf, o.JustifySelf)
        && EqualityComparer<Edges4>.Default.Equals(Margin, o.Margin)
        && _ecold.ValueEquals(in o._ecold);
    public override readonly bool Equals(object? obj) => obj is SpanTextEl { } o && EqualsIn(in o);
    public override readonly int GetHashCode() => HashCode.Combine(12, Key);
}

/// <summary>
/// An async image (album art): shows <see cref="Placeholder"/> until the decode lands, then the bitmap. Decode is
/// off-thread + cached + residency-pinned while on screen (see <c>ImageCache</c>).
/// </summary>
public partial struct ImageEl : IElementKind<ImageEl>, IEquatable<ImageEl>
{
    public const ushort TypeId = 8;
    static ushort IElementKind<ImageEl>.Kind => 8;
    public static implicit operator Element(ImageEl value) => Element.Push(in value);
    public static explicit operator ImageEl(Element element) => element.As<ImageEl>();
    public ImageEl() { }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public readonly ushort ElementTypeId => 8;

    /// <summary>Unified channel: a static path/URL, a thunk (bound virtual rows re-request art when the thunk's
    /// signals change — the recycled slot swaps without an element rebuild), or a concrete signal.</summary>
    public Prop<string> Source { get; init; } = "";
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    /// <summary>Width÷height ratio (CSS <c>aspect-ratio</c>). When set, a fluid image (one or both of
    /// <see cref="Width"/>/<see cref="Height"/> left <c>NaN</c>) <i>derives</i> the missing dimension — e.g. a tile that
    /// fills its column width and stays square (<c>1f</c>). The thing WinUI lacks (no SizeChanged/Viewbox hacks).
    /// <c>NaN</c> = off (use explicit <see cref="Width"/>/<see cref="Height"/>).</summary>
    public float AspectRatio { get; init; } = float.NaN;
    /// <summary>How the decoded pixels map into the layout box — see <see cref="ImageFit"/>. Default
    /// <see cref="ImageFit.Cover"/> (aspect-preserving crop), what most grids want; harmless for square-source/square-box.</summary>
    public ImageFit Fit { get; init; } = ImageFit.Cover;
    /// <summary>Focal point for Cover crops, normalized 0..1. Default 0.5,0.5 centres the decoded image.</summary>
    public float FocusX { get; init; } = 0.5f;
    public float FocusY { get; init; } = 0.5f;
    /// <summary>Where in the box a Cover crop places the focal point (<see cref="FocusX"/>, <see cref="FocusY"/>), normalized 0..1 of the box. Default
    /// 0.5,0.5: the focal point at the box's centre. A subject known to sit at a point of the photograph (a face) is placed where the layout leaves
    /// it in view (above a title over the photograph's foot, say: <c>FocusY = face, FocusFrameY = 0.3</c>). The crop is clamped so the image always
    /// covers the box: a focal point near the image's edge lands as close to its frame point as the image allows.</summary>
    public float FocusFrameX { get; init; } = 0.5f;
    public float FocusFrameY { get; init; } = 0.5f;
    /// <summary>Decode-size hint (target px) used when the layout extent is fluid (<see cref="Width"/> is <c>NaN</c>, so the
    /// real box size isn't known at request time) — PHYSICAL px, the caller applies the device scale. Ignored when <see cref="Width"/> is explicit (the decode is then Width × the device scale, rounded up).
    /// <c>NaN</c> ⇒ decode at source resolution.</summary>
    public float DecodePx { get; init; } = float.NaN;
    public CornerRadius4 Corners { get; init; }
    /// <summary>Unified channel: static tint, thunk, or signal (pairs with a bound <see cref="Source"/>).</summary>
    public Prop<ColorF> Placeholder { get; init; } = ColorF.FromRgba(0x33, 0x33, 0x33);
    /// <summary>Optional BlurHash string — a tiny blurred LQIP preview shown instantly (decoded to a small texture)
    /// until the full-res art lands. Falls back to the flat <see cref="Placeholder"/> tint when null.</summary>
    public string? BlurHash { get; init; }
    /// <summary>Override the placeholder→image reveal transition (duration + easing). Null ⇒ <see cref="ImageTransition.Default"/>;
    /// pass <see cref="ImageTransition.None"/> to disable the fade (instant). Distinct from the base
    /// <see cref="Element.Transition"/> (the declarative motion token that governs bound-channel interpolation).</summary>
    public ImageTransition? RevealTransition { get; init; }
    /// <summary>Optional static bitmap blur. The engine derives a persistent image once; scrolling then remains one
    /// ordinary image draw instead of a per-frame scene blur layer.</summary>
    public BakedBlurSpec? BakedBlur { get; init; }
    /// <summary>Keep this image's texture resident while the window is hidden: the Deep hidden stage releases the textures of
    /// everything on screen to give the memory back, and this one it leaves alone. For the one or two images a hidden window
    /// still shows or restores instantly (a player bar's now-playing artwork); every other image is restored, inside a held
    /// first frame, when the window returns.</summary>
    public bool KeepWhileHidden { get; init; }
    /// <summary>Source-over color applied in the image shader after sampling. Transparent disables it.</summary>
    public ColorF ColorOverlay { get; init; }
    /// <summary>Optional leaf-local alpha feather evaluated in the image shader (no offscreen layer).</summary>
    public ImageMaskSpec? Mask { get; init; }
    /// <summary>Luminance-preserving saturation multiplier applied in the image shader after sampling. 1 = unchanged,
    /// 0 = grayscale, &gt;1 = boosted (Apple Music oversaturates album art under its hero scrim).</summary>
    public float Saturation { get; init; } = 1f;
    public Edges4 Margin { get; init; }
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    /// <summary>ZStack-only horizontal self-placement (CSS <c>justify-self</c>, WinUI <c>HorizontalAlignment</c> on an
    /// overlay child): where this child sits on the X axis of a <c>ZStack = true</c> parent.
    /// <see cref="FlexAlign.Auto"/> = inherit the stack's <c>Justify</c>. Ignored outside a ZStack (a flex container's
    /// main axis is distributed by the container's <c>Justify</c>, not per-child).</summary>
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    static void IElementKind<ImageEl>.FreezeMembers(ref ImageEl v)
    {
        v._ecold = v._ecold.Frozen();
    }

    public readonly bool Equals(ImageEl o) => EqualsIn(in o);
    internal readonly bool EqualsIn(in ImageEl o) =>
        EqualityComparer<string?>.Default.Equals(Key, o.Key)
        && EqualityComparer<Prop<bool>>.Default.Equals(Visible, o.Visible)
        && EqualityComparer<Prop<string>>.Default.Equals(Source, o.Source)
        && EqualityComparer<float>.Default.Equals(Width, o.Width)
        && EqualityComparer<float>.Default.Equals(Height, o.Height)
        && EqualityComparer<float>.Default.Equals(AspectRatio, o.AspectRatio)
        && EqualityComparer<ImageFit>.Default.Equals(Fit, o.Fit)
        && EqualityComparer<float>.Default.Equals(FocusX, o.FocusX)
        && EqualityComparer<float>.Default.Equals(FocusY, o.FocusY)
        && EqualityComparer<float>.Default.Equals(FocusFrameX, o.FocusFrameX)
        && EqualityComparer<float>.Default.Equals(FocusFrameY, o.FocusFrameY)
        && EqualityComparer<float>.Default.Equals(DecodePx, o.DecodePx)
        && EqualityComparer<CornerRadius4>.Default.Equals(Corners, o.Corners)
        && EqualityComparer<Prop<ColorF>>.Default.Equals(Placeholder, o.Placeholder)
        && EqualityComparer<string?>.Default.Equals(BlurHash, o.BlurHash)
        && EqualityComparer<ImageTransition?>.Default.Equals(RevealTransition, o.RevealTransition)
        && EqualityComparer<BakedBlurSpec?>.Default.Equals(BakedBlur, o.BakedBlur)
        && EqualityComparer<bool>.Default.Equals(KeepWhileHidden, o.KeepWhileHidden)
        && EqualityComparer<ColorF>.Default.Equals(ColorOverlay, o.ColorOverlay)
        && EqualityComparer<ImageMaskSpec?>.Default.Equals(Mask, o.Mask)
        && EqualityComparer<float>.Default.Equals(Saturation, o.Saturation)
        && EqualityComparer<Edges4>.Default.Equals(Margin, o.Margin)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignSelf, o.AlignSelf)
        && EqualityComparer<FlexAlign>.Default.Equals(JustifySelf, o.JustifySelf)
        && _ecold.ValueEquals(in o._ecold);
    public override readonly bool Equals(object? obj) => obj is ImageEl { } o && EqualsIn(in o);
    public override readonly int GetHashCode() => HashCode.Combine(8, Key);
}

/// <summary>A single ThemedIcon vector layer (leaf): a colorless coverage mask (interned in
/// <c>IconGeometryTable.Shared</c> as <see cref="PathId"/>) painted at <see cref="Tint"/>. A layered icon is a
/// <c>ZStack</c> of these (built by <c>ThemedIcon.Create</c>). <see cref="Tint"/> is always a BOUND <c>Prop</c>
/// (a role→color thunk reading <c>Tok</c>) so <c>RethemeAll</c> live-recolors it with no re-raster — never a frozen
/// ctor color (component-props-contract.md). Reconciled onto <c>VisualKind.IconLayer</c> where <c>ImageId</c> doubles
/// as the PathId; the recorder emits <c>DrawIconMask</c>.</summary>
public partial struct IconLayerEl : IElementKind<IconLayerEl>, IEquatable<IconLayerEl>
{
    public const ushort TypeId = 15;
    static ushort IElementKind<IconLayerEl>.Kind => 15;
    public static implicit operator Element(IconLayerEl value) => Element.Push(in value);
    public static explicit operator IconLayerEl(Element element) => element.As<IconLayerEl>();
    public IconLayerEl() { }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public readonly ushort ElementTypeId => 15;

    /// <summary>Interned geometry id (<c>IconGeometryTable.Shared.Register</c>). 0 = nothing to draw.</summary>
    public int PathId { get; init; }
    /// <summary>Square icon box (DIP). The mask rasterizes at <c>Size × frameScale</c> device px on the atlas miss.</summary>
    public float Size { get; init; } = 16f;
    /// <summary>The theme-resolved layer color — bind it (role thunk reading <c>Tok</c>) for live recolor.</summary>
    public Prop<ColorF> Tint { get; init; } = ColorF.Transparent;
    public Edges4 Margin { get; init; }
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    /// <summary>ZStack-only horizontal self-placement (CSS <c>justify-self</c>, WinUI <c>HorizontalAlignment</c> on an
    /// overlay child): where this child sits on the X axis of a <c>ZStack = true</c> parent.
    /// <see cref="FlexAlign.Auto"/> = inherit the stack's <c>Justify</c>. Ignored outside a ZStack (a flex container's
    /// main axis is distributed by the container's <c>Justify</c>, not per-child).</summary>
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    static void IElementKind<IconLayerEl>.FreezeMembers(ref IconLayerEl v)
    {
        v._ecold = v._ecold.Frozen();
    }

    public readonly bool Equals(IconLayerEl o) => EqualsIn(in o);
    internal readonly bool EqualsIn(in IconLayerEl o) =>
        EqualityComparer<string?>.Default.Equals(Key, o.Key)
        && EqualityComparer<Prop<bool>>.Default.Equals(Visible, o.Visible)
        && EqualityComparer<int>.Default.Equals(PathId, o.PathId)
        && EqualityComparer<float>.Default.Equals(Size, o.Size)
        && EqualityComparer<Prop<ColorF>>.Default.Equals(Tint, o.Tint)
        && EqualityComparer<Edges4>.Default.Equals(Margin, o.Margin)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignSelf, o.AlignSelf)
        && EqualityComparer<FlexAlign>.Default.Equals(JustifySelf, o.JustifySelf)
        && _ecold.ValueEquals(in o._ecold);
    public override readonly bool Equals(object? obj) => obj is IconLayerEl { } o && EqualsIn(in o);
    public override readonly int GetHashCode() => HashCode.Combine(15, Key);
}

/// <summary>
/// A clipping, scrolling viewport over a single (possibly oversized) content child. Scroll is layout-free
/// (layout.md §6): layout arranges the content at the content-box origin and publishes <c>ContentSize</c>; the
/// <c>-ScrollOffset</c> translation is the content's <c>LocalTransform</c>, written by Input on wheel/drag.
/// </summary>
public partial struct ScrollEl : IElementKind<ScrollEl>, IEquatable<ScrollEl>
{
    public const ushort TypeId = 5;
    static ushort IElementKind<ScrollEl>.Kind => 5;
    public static implicit operator Element(ScrollEl value) => Element.Push(in value);
    public static explicit operator ScrollEl(Element element) => element.As<ScrollEl>();
    public ScrollEl() { }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public readonly ushort ElementTypeId => 5;

    public Element Content { get; init; } = new BoxEl();
    public bool Horizontal { get; init; }     // false = vertical scroll (the common case)
    /// <summary>
    /// Measure to content when auto-sized, then clamp by Min/Max. Default false keeps ScrollView a hard viewport
    /// boundary for app/page/navigation scrolling; popup lists (ComboBox/MenuFlyout/AutoSuggest) opt in so short lists
    /// size to their rows and tall lists scroll after MaxHeight.
    /// </summary>
    public bool ContentSized { get; init; }

    /// <summary>Opt into pinch-zoom (WinUI <c>ScrollingZoomMode.Enabled</c> — default off, mirroring
    /// ScrollPresenter's <c>s_defaultZoomMode = Disabled</c>). When set, a SECOND touch contact over the viewport scales
    /// the content about the gesture midpoint (transform-only; never a relayout) and a single-finger remainder continues
    /// as a pan. <see cref="MinZoom"/>/<see cref="MaxZoom"/> bound the factor.</summary>
    public bool Zoomable { get; init; }
    /// <summary>Minimum pinch-zoom factor (WinUI <c>s_defaultMinZoomFactor = 0.1</c>, ScrollPresenter.h:63). Ignored unless
    /// <see cref="Zoomable"/>.</summary>
    public float MinZoom { get; init; } = 0.1f;
    /// <summary>Maximum pinch-zoom factor (WinUI <c>s_defaultMaxZoomFactor = 10.0</c>, ScrollPresenter.h:64). Ignored unless
    /// <see cref="Zoomable"/>.</summary>
    public float MaxZoom { get; init; } = 10f;

    // The viewport participates in its parent's layout like a box (size + flex + margin + a backing fill).
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    /// <summary>ZStack-only horizontal self-placement (CSS <c>justify-self</c>, WinUI <c>HorizontalAlignment</c> on an
    /// overlay child): where this child sits on the X axis of a <c>ZStack = true</c> parent.
    /// <see cref="FlexAlign.Auto"/> = inherit the stack's <c>Justify</c>. Ignored outside a ZStack (a flex container's
    /// main axis is distributed by the container's <c>Justify</c>, not per-child).</summary>
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    public Edges4 Padding { get; init; }
    public ColorF Fill { get; init; }
    public CornerRadius4 Corners { get; init; }

    /// <summary>Scroll-edge cues (controls.md §8.3): the analytic edge feather at any edge with more content past it, so a
    /// clipped list signals there is more below the fold (<see cref="ScrollEdgeCueResolver"/>: a Fade cue is
    /// <see cref="AutoEdgeFade"/> with the standard band unless an explicit <see cref="EdgeFade"/> is authored).
    /// <see cref="ScrollEdgeCues.Auto"/> (default) resolves to <see cref="ScrollEdgeCuesDefaults.Default"/> (ON,
    /// fade-only); set <see cref="ScrollEdgeCues.None"/> to opt out.</summary>
    public ScrollEdgeCues EdgeCues { get; init; } = ScrollEdgeCues.Auto;
    /// <summary>Explicit edge fade on the viewport (e.g. <c>EdgeFadeSpec.Horizontal()</c>) — the premium alpha-mask cue:
    /// content dissolves into anything behind, following the corners. One offscreen RT. Null = none.</summary>
    public EdgeFadeSpec? EdgeFade { get; init; }
    /// <summary>Auto edge fade: feather only the edges that currently OVERFLOW (more content past them), ramped with the
    /// scroll offset — the discoverable-overflow affordance as a true alpha fade. Ignored when <see cref="EdgeFade"/> is set.</summary>
    public bool AutoEdgeFade { get; init; }
    /// <summary>Feather WIDTH in DIP for <see cref="AutoEdgeFade"/>. 0 (default) = the engine's standard band, so every
    /// call site that only flips the bool is unchanged. Ignored unless <see cref="AutoEdgeFade"/> is set. A surface whose
    /// trailing content must stay crisp (a table's duration column) narrows the band here rather than dropping the fade.</summary>
    public float AutoEdgeFadeBand { get; init; }
    /// <summary>Keep the scrollbar VISIBLE (a persistent thin rail) whenever the content overflows, instead of the default
    /// auto-hide that only reveals on hover/scroll. Hover still expands the rail to the full draggable bar. For navigation
    /// surfaces (a sidebar) where a discoverable, always-present scroll affordance is wanted (WinUI 11 nav behavior).</summary>
    public bool AlwaysShowScrollbar { get; init; }

    /// <summary>Declarative scroll-snap points for this viewport — <c>SnapSpec.Every(rowHeight)</c> for a uniform grid,
    /// <c>SnapSpec.At(...)</c> for an explicit set. A touch/touchpad FLING then retargets its friction decay to land
    /// EXACTLY on a snap value; a wheel/keyboard/programmatic offset stays hard-clamped (never snapped), so a control that
    /// wants a wheel to REST on a boundary re-snaps itself through the programmatic path.
    /// <para>Null (default) means the reconciler NEVER touches this viewport's snap fields, so a control that writes
    /// <c>ScrollState.SnapInterval</c> onto the scene after mount keeps it across every reconcile. A non-null value makes
    /// THIS element the owner: it is re-asserted on every patch, so an interval computed per render stays current. Only
    /// declare a value the element can recompute each render — an interval derived from a frozen options record cannot.</para></summary>
    public FluentGpu.Scene.SnapSpec? Snap { get; init; }


    /// <summary>Scroll-position restoration key: a STABLE per-content identity (e.g. a route key like <c>"artist:&lt;uri&gt;"</c>).
    /// When set, the engine saves this viewport's offset under it and restores it when the same content is shown again —
    /// even after the page was evicted from KeepAlive (cold remount), seeded BEFORE the first layout so there is no
    /// scroll-to-top flash (<c>ScrollHandle.Restore</c>). Distinct content (a different key) starts at the top; compose
    /// the tab/slot identity into the key when the same content can be open twice. Null ⇒ no restoration.</summary>
    public string? ScrollKey { get; init; }
    /// <summary>Never draw the conscious scrollbar for this viewport (parity with <see cref="VirtualListEl"/>); the offset
    /// is still programmatically scrollable. Used to hide the rail while a region is loading its skeleton.</summary>
    public bool SuppressScrollBar { get; init; }
    /// <summary>Called once when this viewport is realized into the scene, with its node handle. Lets composing controls
    /// drive the viewport programmatically while still using the engine's scroll animator.</summary>
    public Action<NodeHandle>? OnRealized { get; init; }

    /// <summary>The ONE app-facing handle over this viewport (scroll rework §9): an author-supplied
    /// <see cref="FluentGpu.Scroll.Runtime.ScrollHandle"/> the host binds to this node when it realizes (and unbinds on
    /// unmount). Null (default) ⇒ the host mints an internal handle. Supply your own to hold the handle OUTSIDE the
    /// subtree (a sibling toolbar's PageUp/PageDown, a parent driving <c>ScrollTo</c> before the viewport mounted — a
    /// move on an unbound handle is latched and applied at bind).</summary>
    public FluentGpu.Scroll.Runtime.ScrollHandle? Handle { get; init; }
    static void IElementKind<ScrollEl>.FreezeMembers(ref ScrollEl v)
    {
        v._ecold = v._ecold.Frozen();
        v = v with { Content = v.Content.Freeze() };
    }

    public readonly bool Equals(ScrollEl o) => EqualsIn(in o);
    internal readonly bool EqualsIn(in ScrollEl o) =>
        EqualityComparer<string?>.Default.Equals(Key, o.Key)
        && EqualityComparer<Prop<bool>>.Default.Equals(Visible, o.Visible)
        && EqualityComparer<Element>.Default.Equals(Content, o.Content)
        && EqualityComparer<bool>.Default.Equals(Horizontal, o.Horizontal)
        && EqualityComparer<bool>.Default.Equals(ContentSized, o.ContentSized)
        && EqualityComparer<bool>.Default.Equals(Zoomable, o.Zoomable)
        && EqualityComparer<float>.Default.Equals(MinZoom, o.MinZoom)
        && EqualityComparer<float>.Default.Equals(MaxZoom, o.MaxZoom)
        && EqualityComparer<float>.Default.Equals(Width, o.Width)
        && EqualityComparer<float>.Default.Equals(Height, o.Height)
        && EqualityComparer<float>.Default.Equals(MinWidth, o.MinWidth)
        && EqualityComparer<float>.Default.Equals(MinHeight, o.MinHeight)
        && EqualityComparer<float>.Default.Equals(MaxWidth, o.MaxWidth)
        && EqualityComparer<float>.Default.Equals(MaxHeight, o.MaxHeight)
        && EqualityComparer<float>.Default.Equals(Grow, o.Grow)
        && EqualityComparer<float>.Default.Equals(Shrink, o.Shrink)
        && EqualityComparer<float>.Default.Equals(Basis, o.Basis)
        && EqualityComparer<FlexAlign>.Default.Equals(AlignSelf, o.AlignSelf)
        && EqualityComparer<FlexAlign>.Default.Equals(JustifySelf, o.JustifySelf)
        && EqualityComparer<Edges4>.Default.Equals(Margin, o.Margin)
        && EqualityComparer<Edges4>.Default.Equals(Padding, o.Padding)
        && EqualityComparer<ColorF>.Default.Equals(Fill, o.Fill)
        && EqualityComparer<CornerRadius4>.Default.Equals(Corners, o.Corners)
        && EqualityComparer<ScrollEdgeCues>.Default.Equals(EdgeCues, o.EdgeCues)
        && EqualityComparer<EdgeFadeSpec?>.Default.Equals(EdgeFade, o.EdgeFade)
        && EqualityComparer<bool>.Default.Equals(AutoEdgeFade, o.AutoEdgeFade)
        && EqualityComparer<float>.Default.Equals(AutoEdgeFadeBand, o.AutoEdgeFadeBand)
        && EqualityComparer<bool>.Default.Equals(AlwaysShowScrollbar, o.AlwaysShowScrollbar)
        && EqualityComparer<FluentGpu.Scene.SnapSpec?>.Default.Equals(Snap, o.Snap)
        && EqualityComparer<string?>.Default.Equals(ScrollKey, o.ScrollKey)
        && EqualityComparer<bool>.Default.Equals(SuppressScrollBar, o.SuppressScrollBar)
        && EqualityComparer<Action<NodeHandle>?>.Default.Equals(OnRealized, o.OnRealized)
        && EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(Handle, o.Handle)
        && _ecold.ValueEquals(in o._ecold);
    public override readonly bool Equals(object? obj) => obj is ScrollEl { } o && EqualsIn(in o);
    public override readonly int GetHashCode() => HashCode.Combine(5, Key);
}
