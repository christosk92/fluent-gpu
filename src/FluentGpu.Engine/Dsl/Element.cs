using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

/// <summary>Immutable description of a UI node (the "virtual DOM"). Cheap to build, never touches the scene directly.</summary>
public abstract record Element
{
    public string? Key { get; init; }

    /// <summary>Optional shared-element (connected-animation / matched-geometry "Hero") key: a node tagged with a
    /// MorphId is a transition participant — when a like-tagged node mounts on the next route, this node's art flies
    /// from the rect it occupied to the new node's rect (backdrop-effects-animation.md §5.4/§5.6). Drives the engine's
    /// <c>ConnectedAnimation</c> registry. Null = not a participant (the default).</summary>
    public string? MorphId { get => (_ecold ?? ElementCold.Default).MorphId; init { if (!EqualityComparer<string?>.Default.Equals((_ecold ?? ElementCold.Default).MorphId, value)) ECold.MorphId = value; } }

    /// <summary>Scroll-linked effects (scroll rework §8): each entry drives one of this node's compositor channels
    /// (translate / opacity / clip-top / scale) from the enclosing scroller's shown offset — sticky pins, parallax,
    /// fades — evaluated with the SAME arithmetic on the UI thread (publish/hit-test) and the render thread (pixels).
    /// Author with <c>el.OnScroll(ScrollEffect.Sticky(56), scope: "hero")</c> / <c>.Parallax(...)</c> / <c>.Fade(...)</c>
    /// (<see cref="FluentGpu.Scroll.Effects.ScrollEffectDsl"/>). Empty = none.</summary>
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { get => (_ecold ?? ElementCold.Default).ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals((_ecold ?? ElementCold.Default).ScrollEffects, value)) ECold.ScrollEffects = value; } }

    /// <summary>Names this node as a sticky SCOPE (the containing block a <c>ScrollEffect.Sticky(…, scope: name)</c>
    /// descendant clamps against — CSS position:sticky's containing block, declared explicitly so any element, a
    /// component root included, can be the pin's boundary). Null = not a scope.</summary>
    public string? ScrollScope { get => (_ecold ?? ElementCold.Default).ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals((_ecold ?? ElementCold.Default).ScrollScope, value)) ECold.ScrollScope = value; } }

    /// <summary>Presence channel (P1, layout.md §4.7): <c>false</c> removes this node from layout flow AND paint AND
    /// hit-test — a COLLAPSED box (CSS <c>display:none</c>), not merely a hidden one (there is no separate
    /// visibility-only channel; use <see cref="FluentGpu.Animation.MotionTargetsExtensions"/>-style Opacity binds for
    /// that). Default true. Bindable like <see cref="FluentGpu.Dsl.BoxEl.Fill"/>/<c>Opacity</c>: a bound value flips
    /// scoped-relayout without a component re-render (the true→false edge snaps; false→true seeds the node's declared
    /// <see cref="Enter"/> like a fresh mount); a static value is re-asserted on every reconcile, equality-gated so an
    /// identical re-render marks nothing. NOT compatible with <see cref="MorphId"/> on a bound channel (DEBUG-asserted,
    /// <c>BindContract</c>) — a shared-element participant must stay mounted (and thus measurable) to fly; collapsing it
    /// mid-transition would break <c>ConnectedAnimation</c> capture.</summary>
    public Prop<bool> Visible { get; init; } = true;

    /// <summary>Stable per-record-type id for integer type-dispatch in the reconciler (the source-gen'd ElementTypeId).</summary>
    public abstract ushort ElementTypeId { get; }

    // The rarely-set base channels live in one shared, copy-on-write ElementCold (ElementCold.cs), like BoxEl's BoxCold.
    private ElementCold? _ecold;
    private ElementCold ECold => _ecold is { } c && ReferenceEquals(c.Owner, this) ? c : (_ecold = (_ecold ?? ElementCold.Default).CloneFor(this));


    /// <summary>Skeleton-derivation opt-out (the native skeleton-loading kit): <see cref="SkeletonMode.Off"/> ⇒ the
    /// deriver emits a same-sized empty spacer (keeps the slot, no shimmer bar) for this node; <see cref="SkeletonMode.Auto"/>
    /// (default) ⇒ the deriver maps it to a shimmer. Construction-time metadata read ONLY by <c>SkeletonDeriver</c> —
    /// never written to a scene column, so it costs nothing at runtime. Set with <c>el.Skeletonized(false)</c>.</summary>
    public SkeletonMode SkeletonMode { get => (_ecold ?? ElementCold.Default).SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals((_ecold ?? ElementCold.Default).SkeletonMode, value)) ECold.SkeletonMode = value; } }
    /// <summary>A bespoke shimmer subtree the deriver substitutes for this node (overrides the auto-map). Set with
    /// <c>el.Skel(customShimmer)</c>.</summary>
    public Element? SkeletonOverride { get => (_ecold ?? ElementCold.Default).SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals((_ecold ?? ElementCold.Default).SkeletonOverride, value)) ECold.SkeletonOverride = value; } }

    // ── Declarative motion (the rework's authoring surface; on the BASE element so EVERY element shares ONE motion
    //    vocabulary — fixing the per-record HoverScale/BrushTransitionMs duplication). ADDITIVE + inert until the
    //    AnimScheduler switch-over wires the reconciler bake (Reconciler/AnimBake) + the InteractionState resolver.
    /// <summary>Implicit on-change transition (the CSS/SwiftUI primitive): when a bound channel's realized value
    /// changes, interpolate FROM the current value over this motion token instead of snapping — for ANY channel,
    /// incl. Fill/Color (the engine-owned generalization of the per-element <c>BrushTransitionMs</c>). Null = snap.</summary>
    public FluentGpu.Animation.MotionTokenDef? Transition { get => (_ecold ?? ElementCold.Default).Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals((_ecold ?? ElementCold.Default).Transition, value)) ECold.Transition = value; } }
    /// <summary>Gesture-state targets (Framer <c>whileHover</c>/<c>whileTap</c>): the node springs to these while
    /// hovered/pressed/focused and back to rest on release, via the InteractionState priority resolver (generalizing
    /// the discrete <c>HoverScale</c>/<c>HoverOpacity</c> spellings).</summary>
    public FluentGpu.Animation.MotionTarget? WhileHover { get => (_ecold ?? ElementCold.Default).WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals((_ecold ?? ElementCold.Default).WhileHover, value)) ECold.WhileHover = value; } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { get => (_ecold ?? ElementCold.Default).WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals((_ecold ?? ElementCold.Default).WhilePressed, value)) ECold.WhilePressed = value; } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { get => (_ecold ?? ElementCold.Default).WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals((_ecold ?? ElementCold.Default).WhileFocus, value)) ECold.WhileFocus = value; } }
    /// <summary>Declarative enter terminal (Framer <c>initial</c>; CSS <c>@starting-style</c>): the node animates FROM
    /// this to identity on mount, seeded under a Presence boundary.</summary>
    public EnterExit? Enter { get => (_ecold ?? ElementCold.Default).Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals((_ecold ?? ElementCold.Default).Enter, value)) ECold.Enter = value; } }
    /// <summary>Declarative exit terminal (Framer <c>exit</c>; CSS <c>allow-discrete</c>): a removed node animates TO
    /// this before its structural removal (deferred via the DetachedAnimSlab / Presence completion gate).</summary>
    public EnterExit? Exit { get => (_ecold ?? ElementCold.Default).Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals((_ecold ?? ElementCold.Default).Exit, value)) ECold.Exit = value; } }
    /// <summary>Per-child entrance stagger (seconds): under a Presence/list boundary, child <c>i</c>'s Enter delay is
    /// <c>index * Stagger</c>, BAKED at reconcile (no runtime closure, no O(n²) sort).</summary>
    public float Stagger { get => (_ecold ?? ElementCold.Default).Stagger; init { if (!EqualityComparer<float>.Default.Equals((_ecold ?? ElementCold.Default).Stagger, value)) ECold.Stagger = value; } }
    /// <summary>Declarative auto-FLIP on layout change (the first-class spelling of the per-box <c>Animate</c> opt-in).
    /// Any layout move/resize of this node animates via transform only.</summary>
    public LayoutTransition? Layout { get => (_ecold ?? ElementCold.Default).Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals((_ecold ?? ElementCold.Default).Layout, value)) ECold.Layout = value; } }

    /// <summary>Wheel routing target: a header laid out ABOVE its list names the list's scroll handle here so wheel
    /// input over the header drives the LIST instead of the header's own ancestor scroller
    /// (<c>InputDispatcher.RouteWheelTarget</c>). Re-asserted on every reconcile (null clears it). Null = none.</summary>
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { get => (_ecold ?? ElementCold.Default).WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals((_ecold ?? ElementCold.Default).WheelTarget, value)) ECold.WheelTarget = value; } }

    /// <summary>Scroll-viewport line height hint (DIP) — <c>ScrollState.LineDip</c>: one wheel notch travels
    /// <c>WheelScrollLines × ScrollLineDip</c> (Windows semantics) instead of the viewport-fraction rule. Read only on a
    /// viewport element (<c>ScrollEl</c> / <c>VirtualListEl</c>); <c>Virtual.List</c> and the other fixed-extent factories
    /// stamp their item extent. 0 (default) = no hint.</summary>
    public float ScrollLineDip { get => (_ecold ?? ElementCold.Default).ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals((_ecold ?? ElementCold.Default).ScrollLineDip, value)) ECold.ScrollLineDip = value; } }

    /// <summary>FLIP coherence (Framer <c>layout</c> relativeTarget): compute this node's FLIP relative to the frame of
    /// the node carrying this <see cref="MorphId"/> (a shared-layout GROUP anchor) instead of its layout parent — so a
    /// reordered/moved item animates coherently WITH the anchor rather than double-counting the anchor's own motion.
    /// Null = the default parent-relative coherence. Resolved to the target node at FLIP-capture time.</summary>
    public string? RelativeTo { get => (_ecold ?? ElementCold.Default).RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals((_ecold ?? ElementCold.Default).RelativeTo, value)) ECold.RelativeTo = value; } }
}

/// <summary>Per-node skeleton-derivation policy (see <see cref="Element.SkeletonMode"/>).</summary>
public enum SkeletonMode : byte { Auto, Off }

/// <summary>The form-validation visual state of a control surface (form-validation.md). <see cref="Warning"/> is
/// reserved for non-blocking advisory styling; v1 styles only <see cref="Error"/> (red border + message).</summary>
public enum ValidationState : byte { None, Warning, Error }

/// <summary>A box: container/layout node and/or a filled, optionally-clickable surface (VStack/HStack/Button/Box).</summary>
public sealed record BoxEl : Element
{
    public override ushort ElementTypeId => 1;

    // Rarely-set channels live in six shared, copy-on-write blocks (BoxCold.cs), one per theme: a BoxEl is allocated and
    // `with`-copied on every render of every box, so its inline footprint is only the channels most boxes set. A block is a
    // null reference until a setter changes a value; the first such setter on an element clones the block, the rest write the clone.
    private BoxColdPaint? _cPaint;
    private BoxColdPaint CPaint => _cPaint is { } c && ReferenceEquals(c.Owner, this) ? c : (_cPaint = (_cPaint ?? BoxColdPaint.Default).CloneFor(this));
    private BoxColdPaintFx? _cPaintFx;
    private BoxColdPaintFx CPaintFx => _cPaintFx is { } c && ReferenceEquals(c.Owner, this) ? c : (_cPaintFx = (_cPaintFx ?? BoxColdPaintFx.Default).CloneFor(this));
    private BoxColdInput? _cInput;
    private BoxColdInput CInput => _cInput is { } c && ReferenceEquals(c.Owner, this) ? c : (_cInput = (_cInput ?? BoxColdInput.Default).CloneFor(this));
    private BoxColdGesture? _cGesture;
    private BoxColdGesture CGesture => _cGesture is { } c && ReferenceEquals(c.Owner, this) ? c : (_cGesture = (_cGesture ?? BoxColdGesture.Default).CloneFor(this));
    private BoxColdMotion? _cMotion;
    private BoxColdMotion CMotion => _cMotion is { } c && ReferenceEquals(c.Owner, this) ? c : (_cMotion = (_cMotion ?? BoxColdMotion.Default).CloneFor(this));
    private BoxColdMisc? _cMisc;
    private BoxColdMisc CMisc => _cMisc is { } c && ReferenceEquals(c.Owner, this) ? c : (_cMisc = (_cMisc ?? BoxColdMisc.Default).CloneFor(this));

    public byte Direction { get; init; }          // 0 = row, 1 = column
    public float Gap { get; init; }
    public Edges4 Padding { get; init; }
    public Edges4 Margin { get; init; }
    /// <summary>Unified channel (Prop&lt;T&gt;): a static color, a <c>Func&lt;ColorF&gt;</c> thunk, or a concrete signal.</summary>
    public Prop<ColorF> Fill { get; init; } = ColorF.Transparent;
    /// <summary>Bindable like <see cref="Fill"/>: a bound hover/press fill re-fires on RethemeAll (theme/palette
    /// switch) and can read recycle-varying state (e.g. a virtual list's slot index for zebra-aware hover depth)
    /// without remounting the slot. A==0 ⇒ the recorder auto-lightens/darkens <see cref="Fill"/> instead.</summary>
    public Prop<ColorF> HoverFill { get; init; } = ColorF.Transparent;
    public Prop<ColorF> PressedFill { get; init; } = ColorF.Transparent;
    /// <summary>Bindable like <see cref="Fill"/> so retained shell/card borders can follow a live theme switch.</summary>
    public Prop<ColorF> BorderColor { get; init; } = ColorF.Transparent;
    public ColorF HoverBorderColor { get => (_cPaint ?? BoxColdPaint.Default).HoverBorderColor; init { if (!EqualityComparer<ColorF>.Default.Equals((_cPaint ?? BoxColdPaint.Default).HoverBorderColor, value)) CPaint.HoverBorderColor = value; } } // A==0 ⇒ recorder auto-lightens BorderColor on hover; else eases to this exact state token
    public ColorF PressedBorderColor { get => (_cPaint ?? BoxColdPaint.Default).PressedBorderColor; init { if (!EqualityComparer<ColorF>.Default.Equals((_cPaint ?? BoxColdPaint.Default).PressedBorderColor, value)) CPaint.PressedBorderColor = value; } } // A==0 ⇒ recorder auto-darkens BorderColor on press; else eases to this exact state token
    public float BorderWidth { get; init; }
    /// <summary>Dashed (solid) border: the dash ON/OFF run lengths in DIP along the perimeter (e.g. 6/4). Both 0 (the
    /// default) = a solid stroke. Applies to the plain <see cref="BorderColor"/> stroke only (not a gradient border).
    /// The "drop zone" look (and the <c>DropZone</c> control) uses this.</summary>
    public float BorderDashOn { get => (_cPaint ?? BoxColdPaint.Default).BorderDashOn; init { if (!EqualityComparer<float>.Default.Equals((_cPaint ?? BoxColdPaint.Default).BorderDashOn, value)) CPaint.BorderDashOn = value; } }
    public float BorderDashOff { get => (_cPaint ?? BoxColdPaint.Default).BorderDashOff; init { if (!EqualityComparer<float>.Default.Equals((_cPaint ?? BoxColdPaint.Default).BorderDashOff, value)) CPaint.BorderDashOff = value; } }
    // Bindable (like Fill/Opacity): the shell content card squares its rail-side corners while the docked right rail is
    // open — a re-render can't reach it (frozen literal inside OverlayHost.Child), so the corner set must be a bind.
    public Prop<CornerRadius4> Corners { get; init; } = default;
    /// <summary>Form-validation visual state (form-validation.md). Bind it to a field's error memo
    /// (<c>Validation = Prop.Of(() =&gt; field.Error.Value.IsValid ? ValidationState.None : ValidationState.Error)</c>):
    /// on <see cref="ValidationState.Error"/> the reconciler resolves the theme critical color and the recorder swaps
    /// this node's border to it. A bound channel — no re-render per keystroke; the write is equality-gated.</summary>
    public Prop<ValidationState> Validation { get => (_cPaint ?? BoxColdPaint.Default).Validation; init { if (!EqualityComparer<Prop<ValidationState>>.Default.Equals((_cPaint ?? BoxColdPaint.Default).Validation, value)) CPaint.Validation = value; } }

    // Optional rich paint (carried into sparse scene side-tables by the reconciler; default = none).
    public ShadowSpec? Shadow { get => (_cPaint ?? BoxColdPaint.Default).Shadow; init { if (!EqualityComparer<ShadowSpec?>.Default.Equals((_cPaint ?? BoxColdPaint.Default).Shadow, value)) CPaint.Shadow = value; } } // soft drop shadow / elevation, drawn beneath the fill
    public ArcSpec? Arc { get => (_cPaintFx ?? BoxColdPaintFx.Default).Arc; init { if (!EqualityComparer<ArcSpec?>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).Arc, value)) CPaintFx.Arc = value; } } // circular-arc stroke (ProgressRing) — SDF ring trimmed to a sweep
    public GradientSpec? Gradient { get => (_cPaint ?? BoxColdPaint.Default).Gradient; init { if (!EqualityComparer<GradientSpec?>.Default.Equals((_cPaint ?? BoxColdPaint.Default).Gradient, value)) CPaint.Gradient = value; } } // gradient fill — supersedes Fill at record time when set
    /// <summary>Optional bindable radial-gradient origin in normalized element coordinates. A non-finite static value
    /// keeps <see cref="GradientSpec.RadialCenter"/> as the source of truth; a signal updates paint only, without a
    /// component render or gradient-spec rebuild (pointer-driven spotlight/reveal effects).</summary>
    public Prop<Point2> RadialGradientCenter { get => (_cPaintFx ?? BoxColdPaintFx.Default).RadialGradientCenter; init { if (!EqualityComparer<Prop<Point2>>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).RadialGradientCenter, value)) CPaintFx.RadialGradientCenter = value; } }
    /// <summary>A second gradient the fill blends toward by <see cref="GradientMix"/> (0 = <see cref="Gradient"/>, 1 = this).
    /// Must share <see cref="Gradient"/>'s stop count (the <see cref="HoverGradient"/> rule; a different count blends only
    /// the shared prefix). Static; the blend happens at record time on stack locals, so a palette cross-fade costs no
    /// re-render and no allocation.</summary>
    public GradientSpec? GradientTo { get => (_cPaintFx ?? BoxColdPaintFx.Default).GradientTo; init { if (!EqualityComparer<GradientSpec?>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).GradientTo, value)) CPaintFx.GradientTo = value; } }
    /// <summary>The 0..1 blend from <see cref="Gradient"/> toward <see cref="GradientTo"/>. Bindable and paint-only (no
    /// relayout): a signal moves the colours without rebuilding a <see cref="GradientSpec"/>.</summary>
    public Prop<float> GradientMix { get => (_cPaintFx ?? BoxColdPaintFx.Default).GradientMix; init { if (!EqualityComparer<Prop<float>>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).GradientMix, value)) CPaintFx.GradientMix = value; } }
    /// <summary>How this box's subtree paints onto what is under it: <see cref="PaintBlend.Additive"/> adds light (glow,
    /// particles) for every rect, gradient, series and sprite field below it. Glyphs, images and paths stay source-over.
    /// Put it on a child of a <see cref="RepaintBoundary"/>, not on the boundary itself: a boundary (also a Feedback or
    /// RasterScale box) records its subtree into its own slice, which starts source-over — an additive ancestor never
    /// reaches into it.</summary>
    public PaintBlend Blend { get => (_cPaint ?? BoxColdPaint.Default).Blend; init { if (!EqualityComparer<PaintBlend>.Default.Equals((_cPaint ?? BoxColdPaint.Default).Blend, value)) CPaint.Blend = value; } }
    /// <summary>How a <see cref="RepaintBoundary"/> slice composites onto the back buffer: <see cref="LayerBlend.Screen"/>
    /// = <c>1 − (1 − s)(1 − d)</c> (soft clouds that brighten what they overlap). Needs <see cref="RepaintBoundary"/>;
    /// detached windows (no layer route) fold it to source-over.</summary>
    public LayerBlend LayerBlend { get => (_cPaint ?? BoxColdPaint.Default).LayerBlend; init { if (!EqualityComparer<LayerBlend>.Default.Equals((_cPaint ?? BoxColdPaint.Default).LayerBlend, value)) CPaint.LayerBlend = value; } }
    /// <summary>Make this box a FEEDBACK boundary (visualizer F6; see <see cref="FeedbackSpec"/>). Implies a repaint boundary
    /// at <see cref="FeedbackSpec.RasterScale"/>. Detached windows (no layer route) draw the fresh content only.</summary>
    public FeedbackSpec? Feedback { get => (_cPaintFx ?? BoxColdPaintFx.Default).Feedback; init { if (!EqualityComparer<FeedbackSpec?>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).Feedback, value)) CPaintFx.Feedback = value; } }
    /// <summary>The per-advance warp of the previous frame about the box centre (zoom / rotate / drift), DIP. Bindable.</summary>
    public Prop<Affine2D> FeedbackTransform { get => (_cPaintFx ?? BoxColdPaintFx.Default).FeedbackTransform; init { if (!EqualityComparer<Prop<Affine2D>>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).FeedbackTransform, value)) CPaintFx.FeedbackTransform = value; } }
    /// <summary>Overrides <see cref="FeedbackSpec.Decay"/> per advance (NaN = the spec's). Bindable: a kick can burst the trail.</summary>
    public Prop<float> FeedbackDecay { get => (_cPaintFx ?? BoxColdPaintFx.Default).FeedbackDecay; init { if (!EqualityComparer<Prop<float>>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).FeedbackDecay, value)) CPaintFx.FeedbackDecay = value; } }
    public GradientSpec? BorderBrush { get => (_cPaint ?? BoxColdPaint.Default).BorderBrush; init { if (!EqualityComparer<GradientSpec?>.Default.Equals((_cPaint ?? BoxColdPaint.Default).BorderBrush, value)) CPaint.BorderBrush = value; } } // gradient border stroke (WinUI ControlElevationBorderBrush); needs BorderWidth > 0
    // Stateful gradient variants: the recorder per-frame interpolates the resting gradient's stops toward these by the
    // eased hover/press progress (same HoverT/PressT that cross-fades a solid Fill). Must share the resting stop count.
    public GradientSpec? HoverGradient { get => (_cPaintFx ?? BoxColdPaintFx.Default).HoverGradient; init { if (!EqualityComparer<GradientSpec?>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).HoverGradient, value)) CPaintFx.HoverGradient = value; } }
    public GradientSpec? PressedGradient { get => (_cPaintFx ?? BoxColdPaintFx.Default).PressedGradient; init { if (!EqualityComparer<GradientSpec?>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).PressedGradient, value)) CPaintFx.PressedGradient = value; } }
    public GradientSpec? HoverBorderBrush { get => (_cPaintFx ?? BoxColdPaintFx.Default).HoverBorderBrush; init { if (!EqualityComparer<GradientSpec?>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).HoverBorderBrush, value)) CPaintFx.HoverBorderBrush = value; } }
    public GradientSpec? PressedBorderBrush { get => (_cPaintFx ?? BoxColdPaintFx.Default).PressedBorderBrush; init { if (!EqualityComparer<GradientSpec?>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).PressedBorderBrush, value)) CPaintFx.PressedBorderBrush = value; } }
    public AcrylicSpec? Acrylic { get => (_cPaintFx ?? BoxColdPaintFx.Default).Acrylic; init { if (!EqualityComparer<AcrylicSpec?>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).Acrylic, value)) CPaintFx.Acrylic = value; } } // per-node frosted-glass backdrop (blur + tint + noise)
    /// <summary>Record this subtree into its OWN retained slice (Flutter's RepaintBoundary, CSS <c>will-change</c>): a
    /// change inside it — a looping keyframe, a bound transform/opacity, a per-frame visualizer — re-rasters only this
    /// slice's tiles, and a change around it never re-rasters this one. Put it on the root of content that animates
    /// continuously while the content painted under and over it stays still. Spends the effect-slice budget
    /// (<c>SliceRecorder.EffectSliceCap</c>); past it, or inside an inline group layer, the subtree records inline as
    /// before (identical pixels either way).</summary>
    public bool RepaintBoundary { get => (_cPaint ?? BoxColdPaint.Default).RepaintBoundary; init { if (!EqualityComparer<bool>.Default.Equals((_cPaint ?? BoxColdPaint.Default).RepaintBoundary, value)) CPaint.RepaintBoundary = value; } }
    /// <summary>The raster resolution of a <see cref="RepaintBoundary"/> slice relative to the window (1 = full; snapped
    /// to 1/2, 1/4 or 1/8). Below 1 the slice holds NO tiles: each change replays it once into one surface at that scale
    /// and the composite upsamples it bilinearly — a fraction of the raster work and memory. Only for SOFT content whose
    /// look survives the upsample (large radial gradients, drifting colour fields, heavily blurred art); text and crisp
    /// edges soften. Ignored without <see cref="RepaintBoundary"/>.</summary>
    public float RasterScale { get => (_cPaint ?? BoxColdPaint.Default).RasterScale; init { if (!EqualityComparer<float>.Default.Equals((_cPaint ?? BoxColdPaint.Default).RasterScale, value)) CPaint.RasterScale = value; } }
    public bool TabShape { get => (_cPaint ?? BoxColdPaint.Default).TabShape; init { if (!EqualityComparer<bool>.Default.Equals((_cPaint ?? BoxColdPaint.Default).TabShape, value)) CPaint.TabShape = value; } } // selected TabView header: rounded top + bottom flares
    public float TabFlareRadius { get => (_cPaint ?? BoxColdPaint.Default).TabFlareRadius; init { if (!EqualityComparer<float>.Default.Equals((_cPaint ?? BoxColdPaint.Default).TabFlareRadius, value)) CPaint.TabFlareRadius = value; } }
    /// <summary>Punch a VIDEO HOLE at this box (DrawOp.DrawVideo): instead of painting, the box ERASES the UI pixels
    /// already painted under it toward premultiplied zero, so the DComp video visual composited BELOW the swapchain shows
    /// through. Painter-ordered — anything recorded after it (letterbox bars, transport chrome) paints back OVER the
    /// video. Supersedes <see cref="Fill"/> for this node; see gpu-renderer.md §7.3 for the offscreen-layer limitation
    /// (inside an opacity/blur/acrylic layer the erase hits the layer RT, not the back buffer).</summary>
    public bool VideoHole { get => (_cPaint ?? BoxColdPaint.Default).VideoHole; init { if (!EqualityComparer<bool>.Default.Equals((_cPaint ?? BoxColdPaint.Default).VideoHole, value)) CPaint.VideoHole = value; } }
    /// <summary>The video registry slot token this hole belongs to (diagnostic at replay — the presenter positions the
    /// visual itself). Rides <c>NodePaint.ImageId</c>, so it is meaningful only with <see cref="VideoHole"/> set.</summary>
    public int VideoSurfaceId { get => (_cPaint ?? BoxColdPaint.Default).VideoSurfaceId; init { if (!EqualityComparer<int>.Default.Equals((_cPaint ?? BoxColdPaint.Default).VideoSurfaceId, value)) CPaint.VideoSurfaceId = value; } }
    /// <summary>Per-element edge fade (gpu-renderer.md): feather this element's content alpha to transparent (+ optional
    /// blur) near the chosen edges, following its rounded <see cref="Corners"/> (the curve) — it dissolves into whatever
    /// is behind. One offscreen RT per faded element. Null = none.</summary>
    public EdgeFadeSpec? EdgeFade { get => (_cPaintFx ?? BoxColdPaintFx.Default).EdgeFade; init { if (!EqualityComparer<EdgeFadeSpec?>.Default.Equals((_cPaintFx ?? BoxColdPaintFx.Default).EdgeFade, value)) CPaintFx.EdgeFade = value; } }

    public Action? OnClick { get; init; }
    public Action<KeyEventArgs>? OnKeyDown { get => (_cInput ?? BoxColdInput.Default).OnKeyDown; init { if (!EqualityComparer<Action<KeyEventArgs>?>.Default.Equals((_cInput ?? BoxColdInput.Default).OnKeyDown, value)) CInput.OnKeyDown = value; } }
    /// <summary>Text (character) input — the IME/layout-resolved codepoint, routed to the focused node and bubbled
    /// (distinct from <see cref="OnKeyDown"/>'s raw virtual-key). Set by editable controls (EditableText/ComboBox).</summary>
    public Action<CharEventArgs>? OnCharInput { get => (_cGesture ?? BoxColdGesture.Default).OnCharInput; init { if (!EqualityComparer<Action<CharEventArgs>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnCharInput, value)) CGesture.OnCharInput = value; } }
    // Position-aware pointer (local coords) — for sliders/scrollbars: OnPointerDown fires on press, OnDrag while held.
    public Action<Point2>? OnPointerDown { get => (_cGesture ?? BoxColdGesture.Default).OnPointerDown; init { if (!EqualityComparer<Action<Point2>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnPointerDown, value)) CGesture.OnPointerDown = value; } }
    public Action<Point2>? OnDrag { get => (_cGesture ?? BoxColdGesture.Default).OnDrag; init { if (!EqualityComparer<Action<Point2>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnDrag, value)) CGesture.OnDrag = value; } }
    /// <summary>Marks an <see cref="OnDrag"/> node a CROSS-AXIS content pan (SwipeControl row swipe, FlipView page drag):
    /// instead of eagerly capturing the contact on touch-down — the Slider/EditableText scrub default — this drag enrolls
    /// an AXIS-LOCKED gesture-arena member that competes with an enclosing scroller's Pan (input-a11y.md §7A). It wins the
    /// contact only when the gesture runs ALONG its own axis and yields (the list scrolls) on a cross-axis drag — the
    /// declarative form of <c>DragController.YieldsToPan</c>. Its axis is inferred from this node's main axis
    /// (<see cref="Direction"/>): a row box (Direction=0) is a horizontal swipe, a column box a vertical one. No effect
    /// without <see cref="OnDrag"/>, and no effect on the mouse path (mouse drag still captures immediately).</summary>
    public bool DragYieldsToPan { get => (_cGesture ?? BoxColdGesture.Default).DragYieldsToPan; init { if (!EqualityComparer<bool>.Default.Equals((_cGesture ?? BoxColdGesture.Default).DragYieldsToPan, value)) CGesture.DragYieldsToPan = value; } }
    /// <summary>Position-aware press carrying click count (double/triple-click), modifier chord, button and device kind —
    /// the text-selection / list-interaction press handler. Fires alongside <see cref="OnPointerDown"/> on left press.</summary>
    public Action<PointerEventArgs>? OnPointerPressed { get => (_cInput ?? BoxColdInput.Default).OnPointerPressed; init { if (!EqualityComparer<Action<PointerEventArgs>?>.Default.Equals((_cInput ?? BoxColdInput.Default).OnPointerPressed, value)) CInput.OnPointerPressed = value; } }
    /// <summary>Typed clean-release edge. Fires only when the primary pointer releases over its original press target;
    /// pan, drag, hold, capture loss, cancellation, and release outside suppress it.</summary>
    public Action<PointerEventArgs>? OnPointerReleased { get => (_cInput ?? BoxColdInput.Default).OnPointerReleased; init { if (!EqualityComparer<Action<PointerEventArgs>?>.Default.Equals((_cInput ?? BoxColdInput.Default).OnPointerReleased, value)) CInput.OnPointerReleased = value; } }
    /// <summary>Context-menu request (WinUI ContextRequested): right-click release over this node, the Menu key /
    /// Shift+F10 while it has focus, or a touch long-press. The <see cref="ContextRequestEventArgs"/> carries the
    /// node-LOCAL position (keyboard invocations pass the node's centre) and the <see cref="ContextRequestTrigger"/>
    /// so a handler can open AT the pointer/contact but anchor to the element rect for a keyboard invocation.</summary>
    public Action<ContextRequestEventArgs>? OnContextRequested { get => (_cInput ?? BoxColdInput.Default).OnContextRequested; init { if (!EqualityComparer<Action<ContextRequestEventArgs>?>.Default.Equals((_cInput ?? BoxColdInput.Default).OnContextRequested, value)) CInput.OnContextRequested = value; } }
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
    public bool ClickRequestsContext { get => (_cInput ?? BoxColdInput.Default).ClickRequestsContext; init { if (!EqualityComparer<bool>.Default.Equals((_cInput ?? BoxColdInput.Default).ClickRequestsContext, value)) CInput.ClickRequestsContext = value; } }
    /// <summary>Keyboard-accelerator chord (WinUI KeyboardAccelerator): invokes <see cref="OnClick"/> from anywhere once
    /// focused routing leaves the chord unhandled (e.g. Ctrl+W close-tab).</summary>
    public KeyAccelerator? Accelerator { get => (_cGesture ?? BoxColdGesture.Default).Accelerator; init { if (!EqualityComparer<KeyAccelerator?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).Accelerator, value)) CGesture.Accelerator = value; } }
    /// <summary>Access-key mnemonic (WinUI AccessKey): Alt+letter invokes <see cref="OnClick"/>. Uppercase 'A'..'Z'/'0'..'9'.</summary>
    public char AccessKey { get => (_cGesture ?? BoxColdGesture.Default).AccessKey; init { if (!EqualityComparer<char>.Default.Equals((_cGesture ?? BoxColdGesture.Default).AccessKey, value)) CGesture.AccessKey = value; } }
    /// <summary>Pointer cursor shown while hovering this node or any cursor-less descendant (WinUI SetCursor). Null =
    /// inherit from the nearest declaring ancestor, else the system arrow — clickability does NOT imply the hand. An
    /// explicit value (Arrow included) terminates the lookup, masking an ancestor's I-beam/hand (the TextBox delete
    /// button / PasswordBox reveal force Arrow over the field's I-beam — TextBox_Partial.cpp:884).</summary>
    public CursorId? Cursor { get; init; }
    /// <summary>Element-level wheel hook (WinUI PointerWheelChanged), consulted BEFORE the enclosing viewport scrolls;
    /// set <c>Handled</c> to consume (NumberBox value stepping). Unhandled keeps walking up (routed-event semantics).</summary>
    public Action<WheelEventArgs>? OnPointerWheel { get => (_cGesture ?? BoxColdGesture.Default).OnPointerWheel; init { if (!EqualityComparer<Action<WheelEventArgs>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnPointerWheel, value)) CGesture.OnPointerWheel = value; } }
    /// <summary>Position-aware BARE hover (local coords), fired on pointer move while hovering with no button down —
    /// e.g. RatingControl filling stars to the cursor on hover. Makes the node hit-testable so it receives hover.</summary>
    public Action<Point2>? OnHoverMove { get => (_cInput ?? BoxColdInput.Default).OnHoverMove; init { if (!EqualityComparer<Action<Point2>?>.Default.Equals((_cInput ?? BoxColdInput.Default).OnHoverMove, value)) CInput.OnHoverMove = value; } }
    /// <summary>Routed mouse/pen move in this node's local coordinates while the pointer is anywhere in its subtree.
    /// Delivered leaf-to-root, including when an interactive child is the hit leaf. Suppressed for touch and capture/
    /// drag paths. Intended for allocation-free container effects such as a pointer-tracked spotlight.</summary>
    public Action<Point2>? OnPointerMoveWithin { get => (_cGesture ?? BoxColdGesture.Default).OnPointerMoveWithin; init { if (!EqualityComparer<Action<Point2>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnPointerMoveWithin, value)) CGesture.OnPointerMoveWithin = value; } }
    /// <summary>Fired when the pointer LEAVES this node (loses hover) — to reset a hover preview to its resting state
    /// (RatingControl reverting to the committed rating, a ToolTip dismissing). Makes the node hit-testable.</summary>
    public Action? OnPointerExit { get => (_cInput ?? BoxColdInput.Default).OnPointerExit; init { if (!EqualityComparer<Action?>.Default.Equals((_cInput ?? BoxColdInput.Default).OnPointerExit, value)) CInput.OnPointerExit = value; } }
    /// <summary>Fired when the dispatcher moves keyboard/pointer focus ONTO (true) or OFF (false) this node — the WinUI
    /// GotFocus/LostFocus pair. Delivered by <c>InputDispatcher.SetFocus</c> directly (never via hit-testing); editable
    /// controls use it to arm the caret blinker / IME and capture the Escape-revert snapshot.</summary>
    public Action<bool>? OnFocusChanged { get => (_cInput ?? BoxColdInput.Default).OnFocusChanged; init { if (!EqualityComparer<Action<bool>?>.Default.Equals((_cInput ?? BoxColdInput.Default).OnFocusChanged, value)) CInput.OnFocusChanged = value; } }
    /// <summary>Marks this box a drag-reorder source (WinUI CanDragItems/CanReorderItems item container): a left press
    /// on it (or any non-draggable descendant) arms the engine's DragController; pointer travel past the 4px drag box
    /// (per-axis, ListViewBaseItem_Partial.cpp:1864-1878) promotes the press to a drag — the node follows the pointer
    /// at WinUI ListViewItemDragThemeOpacity 0.80 (ListViewItem_themeresources.xaml:7) with a lifted shadow, stops
    /// hit-testing, and the eventual release SUPPRESSES the click.</summary>
    public bool CanDrag { get => (_cGesture ?? BoxColdGesture.Default).CanDrag; init { if (!EqualityComparer<bool>.Default.Equals((_cGesture ?? BoxColdGesture.Default).CanDrag, value)) CGesture.CanDrag = value; } }
    /// <summary>Drag lifecycle (needs <see cref="CanDrag"/>): fired once when the press crosses the drag box (WinUI
    /// DragItemsStarting). The args instance is reused for the whole gesture — copy what you keep.</summary>
    public Action<DragEventArgs>? OnDragStarted { get => (_cGesture ?? BoxColdGesture.Default).OnDragStarted; init { if (!EqualityComparer<Action<DragEventArgs>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnDragStarted, value)) CGesture.OnDragStarted = value; } }
    /// <summary>Every pointer move while the drag is active: accumulated gesture deltas + smoothed velocity — feed
    /// <c>ReorderList.Update(e.TotalDy)</c> and re-render with its projected order / offset hints.</summary>
    public Action<DragEventArgs>? OnDragDelta { get => (_cGesture ?? BoxColdGesture.Default).OnDragDelta; init { if (!EqualityComparer<Action<DragEventArgs>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnDragDelta, value)) CGesture.OnDragDelta = value; } }
    /// <summary>Release after an active drag (WinUI DragItemsCompleted): commit the reorder here
    /// (<c>ReorderList.Complete()</c>); the drop-glide and the displaced-sibling FLIP retarget off this commit.</summary>
    public Action<DragEventArgs>? OnDragCompleted { get => (_cGesture ?? BoxColdGesture.Default).OnDragCompleted; init { if (!EqualityComparer<Action<DragEventArgs>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnDragCompleted, value)) CGesture.OnDragCompleted = value; } }
    /// <summary>The drag aborted (Escape / pointer-capture loss / window blur): drop hints without committing.</summary>
    public Action? OnDragCanceled { get => (_cGesture ?? BoxColdGesture.Default).OnDragCanceled; init { if (!EqualityComparer<Action?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnDragCanceled, value)) CGesture.OnDragCanceled = value; } }
    /// <summary>E5-L2 typed drag SOURCE (the Flutter Draggable / react-beautiful-dnd model — deliberately NOT WinUI
    /// OLE, per the 2026-06-10 user ruling): marks this box draggable (implies <see cref="CanDrag"/> — the L1 gesture
    /// armer) with a string Kind discriminator + a payload factory the engine resolves ONCE when the press promotes
    /// past the drag box. The live <see cref="DragSession"/> then routes to the nearest accepting
    /// <see cref="DropTarget"/> under the pointer on every move.</summary>
    public DragSource? Draggable { get => (_cGesture ?? BoxColdGesture.Default).Draggable; init { if (!EqualityComparer<DragSource?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).Draggable, value)) CGesture.Draggable = value; } }
    /// <summary>E5-L2 drop TARGET (Flutter DragTarget / SwiftUI dropDestination): accepts sessions whose Kind is in
    /// <c>AcceptKinds</c> — OnEnter/OnOver/OnLeave fire on hover transitions, OnDrop on release over it (before the
    /// L1 completion; <c>SettleOnDrop</c> keeps the drop-glide for reorder targets). Discovery is hit-test-CHAIN
    /// based (nearest accepting ancestor of the node under the pointer) — the spec alone does NOT make this box
    /// click/pointer hit-testable.</summary>
    public DropTargetSpec? DropTarget { get => (_cGesture ?? BoxColdGesture.Default).DropTarget; init { if (!EqualityComparer<DropTargetSpec?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).DropTarget, value)) CGesture.DropTarget = value; } }
    /// <summary>Opt this clickable node into auto-repeat: while held, the host's RepeatTicker re-invokes <see cref="OnClick"/>
    /// after an initial delay, then at a fixed interval (WinUI RepeatButton). Pauses while the held pointer leaves the
    /// node (fresh delay on re-entry — RepeatButton_Partial.cpp:530-574); a held Space arms the same engine timer.</summary>
    public bool Repeats { get => (_cGesture ?? BoxColdGesture.Default).Repeats; init { if (!EqualityComparer<bool>.Default.Equals((_cGesture ?? BoxColdGesture.Default).Repeats, value)) CGesture.Repeats = value; } }
    /// <summary>WinUI RepeatButton <c>Delay</c>/<c>Interval</c> (ms) for <see cref="Repeats"/> nodes. NaN = the WinUI
    /// DP defaults (500/33); the ScrollBar template arrows use Interval=50 (ScrollBar_themeresources.xaml).</summary>
    public float RepeatDelayMs { get => (_cGesture ?? BoxColdGesture.Default).RepeatDelayMs; init { if (!EqualityComparer<float>.Default.Equals((_cGesture ?? BoxColdGesture.Default).RepeatDelayMs, value)) CGesture.RepeatDelayMs = value; } }
    public float RepeatIntervalMs { get => (_cGesture ?? BoxColdGesture.Default).RepeatIntervalMs; init { if (!EqualityComparer<float>.Default.Equals((_cGesture ?? BoxColdGesture.Default).RepeatIntervalMs, value)) CGesture.RepeatIntervalMs = value; } }
    /// <summary>WinUI <c>KeyPress::Button bAcceptsReturn</c>: false = Enter does NOT activate this clickable (it falls
    /// through to normal key routing) — CheckBox (CheckBox_Partial.cpp:27), RadioButton (RadioButton_Partial.cpp:30)
    /// and ToggleSwitch (ToggleSwitch_Partial.cpp:1002-1007) toggle on Space only. Space activation is unaffected.</summary>
    public bool ActivateOnEnter { get => (_cGesture ?? BoxColdGesture.Default).ActivateOnEnter; init { if (!EqualityComparer<bool>.Default.Equals((_cGesture ?? BoxColdGesture.Default).ActivateOnEnter, value)) CGesture.ActivateOnEnter = value; } }
    /// <summary>WinUI <c>AllowFocusOnInteraction</c>: false = a pointer press never moves focus to this focusable (and
    /// never falls past it to an ancestor — focus stays where it was, AppBarButton_themeresources.xaml:136); keyboard
    /// Tab still reaches it. True (default) = press focuses the nearest focusable self-or-ancestor.</summary>
    public bool AllowFocusOnInteraction { get => (_cInput ?? BoxColdInput.Default).AllowFocusOnInteraction; init { if (!EqualityComparer<bool>.Default.Equals((_cInput ?? BoxColdInput.Default).AllowFocusOnInteraction, value)) CInput.AllowFocusOnInteraction = value; } }
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
    public bool HitTestPassThrough { get => (_cInput ?? BoxColdInput.Default).HitTestPassThrough; init { if (!EqualityComparer<bool>.Default.Equals((_cInput ?? BoxColdInput.Default).HitTestPassThrough, value)) CInput.HitTestPassThrough = value; } }
    /// <summary>An opaque, input-blocking surface (a modal dialog card, a light-dismiss popup plate) sits geometrically
    /// ON TOP of whatever page content is laid out beneath it, but it is a Z-STACK SIBLING of that content, not an
    /// ancestor. The scroll dispatcher's containing-scroller fallback (<c>InputDispatcher.ContainingScrollerForAxis</c>)
    /// walks the WHOLE tree by geometry alone when no scrollable ANCESTOR of the hit point exists — it has no notion of
    /// paint order, so it can find and wheel-scroll a background list whose LAID-OUT bounds happen to sit under a
    /// covering-but-non-scrollable overlay (a dialog's plain message text, say). Set true on a covering surface to make
    /// that fallback treat it as opaque: any scrollable candidate found in an EARLIER sibling is discarded once this
    /// node is reached, though the fallback still recurses into ITS OWN children (a dialog with genuine scrollable
    /// Content is still found normally, since that inner scroller is a DESCENDANT, not blocked by this reset).</summary>
    public bool BlocksBackgroundScroll { get => (_cGesture ?? BoxColdGesture.Default).BlocksBackgroundScroll; init { if (!EqualityComparer<bool>.Default.Equals((_cGesture ?? BoxColdGesture.Default).BlocksBackgroundScroll, value)) CGesture.BlocksBackgroundScroll = value; } }
    /// <summary>Input-enabled (the default). When false the engine gates this node's interaction: it does not hit-test,
    /// focus, take keyboard activation, repeat, drag, or click — so control factories no longer null their handlers by
    /// hand. Disabled <em>visuals</em> stay control-chosen (pick the disabled token via <c>StateBrush.Resting(enabled)</c>).</summary>
    public bool IsEnabled { get; init; } = true;
    public bool Focusable { get; init; }
    /// <summary>WinUI <c>Control.IsTabStop</c>: null = auto (clickable nodes are focusable), false = NEVER keyboard
    /// focusable even when clickable (the light-dismiss catcher layer — WinUI's dismiss layer is not a tab stop),
    /// true = force focusable.</summary>
    public bool? TabStop { get => (_cInput ?? BoxColdInput.Default).TabStop; init { if (!EqualityComparer<bool?>.Default.Equals((_cInput ?? BoxColdInput.Default).TabStop, value)) CInput.TabStop = value; } }
    public int TabIndex { get => (_cGesture ?? BoxColdGesture.Default).TabIndex; init { if (!EqualityComparer<int>.Default.Equals((_cGesture ?? BoxColdGesture.Default).TabIndex, value)) CGesture.TabIndex = value; } }
    /// <summary>WinUI FocusVisualMargin: negative values push the keyboard-focus ring OUTSIDE the bounds. Null = the
    /// WinUI template default (−3 all around); Slider uses −7,0,−7,0.</summary>
    public Edges4? FocusVisualMargin { get => (_cInput ?? BoxColdInput.Default).FocusVisualMargin; init { if (!EqualityComparer<Edges4?>.Default.Equals((_cInput ?? BoxColdInput.Default).FocusVisualMargin, value)) CInput.FocusVisualMargin = value; } }
    /// <summary>Semantic control role (set by the control factories; a button IS a BoxEl). Surfaced to a11y/devtools/tests.</summary>
    public AutomationRole Role { get; init; }

    // Composited (animate without relayout): transform (offset/scale/rotate about the transform origin) + opacity, applied to this node + subtree.
    public float OffsetX { get => (_cMotion ?? BoxColdMotion.Default).OffsetX; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).OffsetX, value)) CMotion.OffsetX = value; } }
    public float OffsetY { get => (_cMotion ?? BoxColdMotion.Default).OffsetY; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).OffsetY, value)) CMotion.OffsetY = value; } }
    public float ScaleX { get => (_cMotion ?? BoxColdMotion.Default).ScaleX; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).ScaleX, value)) CMotion.ScaleX = value; } }
    public float ScaleY { get => (_cMotion ?? BoxColdMotion.Default).ScaleY; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).ScaleY, value)) CMotion.ScaleY = value; } }
    public float Rotation { get => (_cMotion ?? BoxColdMotion.Default).Rotation; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).Rotation, value)) CMotion.Rotation = value; } } // degrees
    /// <summary>Unified channel (Prop&lt;T&gt;): a static opacity, a thunk, or a concrete signal.</summary>
    public Prop<float> Opacity { get; init; } = 1f;
    public float HoverOpacity { get => (_cMotion ?? BoxColdMotion.Default).HoverOpacity; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).HoverOpacity, value)) CMotion.HoverOpacity = value; } }
    public float PressedOpacity { get => (_cMotion ?? BoxColdMotion.Default).PressedOpacity; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).PressedOpacity, value)) CMotion.PressedOpacity = value; } }
    /// <summary>Flat opacity group (WinUI Composition LayerVisual semantics): when set and the resolved opacity is
    /// &lt; 1, the subtree renders at FULL alpha into a pooled offscreen RT and composites ONCE at the group alpha —
    /// overlapping children don't double-blend (a fading dialog plate + its buttons). Default false = per-node
    /// multiplied opacity (WinUI's plain Visual.Opacity behavior). Engine primitive: PushLayer{Opacity} (E9).</summary>
    public bool OpacityGroup { get => (_cMotion ?? BoxColdMotion.Default).OpacityGroup; init { if (!EqualityComparer<bool>.Default.Equals((_cMotion ?? BoxColdMotion.Default).OpacityGroup, value)) CMotion.OpacityGroup = value; } }
    /// <summary>Per-node self-blur radius σ (px) — the Expressive Motion Kit's perceptual softener. &gt; 0 wraps the
    /// node's subtree in a PushLayer{Blur} (subtree → pooled offscreen RT → separable Gaussian → composite at the group
    /// alpha), so the node's OWN pixels blur (CSS <c>filter: blur()</c>, not the backdrop). Animate it via
    /// <c>AnimChannel.BlurSigma</c> (UseTransition/UseKeyframes) for the transitions.dev recipes (number pop-in, skeleton
    /// reveal, icon swap, page slide, …). 0 = no blur (the default). Composited only — never relayout.</summary>
    public float Blur { get => (_cMotion ?? BoxColdMotion.Default).Blur; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).Blur, value)) CMotion.Blur = value; } }
    /// <summary>Transform origin (normalized 0..1 of the box). Composited scale/rotate (and animated ScaleX/Y) pivot here;
    /// default centre (0.5,0.5). Set OriginY=0 to scale/unfold from the TOP edge (a flyout/menu), 1 for the bottom.</summary>
    public float TransformOriginX { get => (_cMotion ?? BoxColdMotion.Default).TransformOriginX; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).TransformOriginX, value)) CMotion.TransformOriginX = value; } }
    public float TransformOriginY { get => (_cMotion ?? BoxColdMotion.Default).TransformOriginY; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).TransformOriginY, value)) CMotion.TransformOriginY = value; } }

    // Interaction-driven composited scale (1 = none): grows/shrinks this node about its centre by the eased hover/press
    // progress at record time (a WinUI slider/scrollbar thumb that pops on hover). Needs a pointer handler to receive the
    // hover/press flags. Composited only — never changes layout or hit-testing.
    public float HoverScale { get => (_cMotion ?? BoxColdMotion.Default).HoverScale; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).HoverScale, value)) CMotion.HoverScale = value; } }
    public float PressScale { get => (_cMotion ?? BoxColdMotion.Default).PressScale; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).PressScale, value)) CMotion.PressScale = value; } }
    public float HoverDurationMs { get => (_cMotion ?? BoxColdMotion.Default).HoverDurationMs; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).HoverDurationMs, value)) CMotion.HoverDurationMs = value; } }
    public float PressDurationMs { get => (_cMotion ?? BoxColdMotion.Default).PressDurationMs; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).PressDurationMs, value)) CMotion.PressDurationMs = value; } }
    public EasingSpec HoverEasing { get => (_cMotion ?? BoxColdMotion.Default).HoverEasing; init { if (!EqualityComparer<EasingSpec>.Default.Equals((_cMotion ?? BoxColdMotion.Default).HoverEasing, value)) CMotion.HoverEasing = value; } }
    public EasingSpec PressEasing { get => (_cMotion ?? BoxColdMotion.Default).PressEasing; init { if (!EqualityComparer<EasingSpec>.Default.Equals((_cMotion ?? BoxColdMotion.Default).PressEasing, value)) CMotion.PressEasing = value; } }

    /// <summary>Implicit brush transition (WinUI <c>BrushTransition</c>): when a re-render changes Fill/BorderColor on
    /// this LIVE node (a logical state flip — checked, selected…), the displayed color cross-fades over this duration
    /// instead of snapping. NaN = snap (the default); WinUI control templates use 83ms.</summary>
    public float BrushTransitionMs { get => (_cMotion ?? BoxColdMotion.Default).BrushTransitionMs; init { if (!EqualityComparer<float>.Default.Equals((_cMotion ?? BoxColdMotion.Default).BrushTransitionMs, value)) CMotion.BrushTransitionMs = value; } }

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
    public Prop<Affine2D> Transform { get; init; } = default;

    // Legacy *Bind spellings — write-only init-aliases into the unified channel props, deleted per-channel by the
    // migration waves. A null assignment leaves the channel static (preserves the `cond ? null : bind` idiom).

    /// <summary>Called once when this box is realized into the scene, with its node handle — for a control factory to
    /// capture the handle (e.g. to wire a signal binding that needs the live node). Fires at mount only.</summary>
    public Action<NodeHandle>? OnRealized { get; init; }
    /// <summary>Called after layout when this node's arranged local bounds change. Intended for retained leaf controls
    /// that need their own laid-out width/height without subscribing to raw viewport changes.</summary>
    public Action<RectF>? OnBoundsChanged { get => (_cGesture ?? BoxColdGesture.Default).OnBoundsChanged; init { if (!EqualityComparer<Action<RectF>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).OnBoundsChanged, value)) CGesture.OnBoundsChanged = value; } }

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
    public Func<NodeHandle>? FollowRect { get => (_cGesture ?? BoxColdGesture.Default).FollowRect; init { if (!EqualityComparer<Func<NodeHandle>?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).FollowRect, value)) CGesture.FollowRect = value; } }

    public Element[] Children { get; init; } = [];

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
    public PathData? ClipPath { get => (_cPaint ?? BoxColdPaint.Default).ClipPath; init { if (!EqualityComparer<PathData?>.Default.Equals((_cPaint ?? BoxColdPaint.Default).ClipPath, value)) CPaint.ClipPath = value; } }
    /// <summary>Winding rule for <see cref="ClipPath"/> (mirrors <c>PathEl.Rule</c>). Ignored when ClipPath is null.</summary>
    public FillRule ClipPathRule { get => (_cPaint ?? BoxColdPaint.Default).ClipPathRule; init { if (!EqualityComparer<FillRule>.Default.Equals((_cPaint ?? BoxColdPaint.Default).ClipPathRule, value)) CPaint.ClipPathRule = value; } }
    /// <summary>0 (default) = <see cref="ClipPath"/> is already node-local DIP. Paired with
    /// <see cref="ClipPathViewBoxH"/> &gt; 0, bakes the uniform-fit (min-axis) scale into the clip at record time —
    /// the same contract <c>PathEl.ViewBoxW/H</c> has, so one authored silhouette clips any box size.</summary>
    public float ClipPathViewBoxW { get => (_cPaint ?? BoxColdPaint.Default).ClipPathViewBoxW; init { if (!EqualityComparer<float>.Default.Equals((_cPaint ?? BoxColdPaint.Default).ClipPathViewBoxW, value)) CPaint.ClipPathViewBoxW = value; } }
    public float ClipPathViewBoxH { get => (_cPaint ?? BoxColdPaint.Default).ClipPathViewBoxH; init { if (!EqualityComparer<float>.Default.Equals((_cPaint ?? BoxColdPaint.Default).ClipPathViewBoxH, value)) CPaint.ClipPathViewBoxH = value; } }

    /// <summary>Paint-order opt-in (the declarative <c>z-index</c> of a hovered card): while the pointer hover path
    /// passes through this element (it is hovered, or hover-within, or its hover fade is still decaying), it paints
    /// AFTER (above) its non-elevated siblings, so its elevation halo is not overpainted by a later sibling. LAYOUT and
    /// HIT-TESTING are unaffected (pure record order). At most one sibling holds the hover path, so the recorder defers
    /// it with O(1) space and zero allocation; at rest (no hover) the element paints in normal document order.</summary>
    public bool HoverElevatePaint { get => (_cPaint ?? BoxColdPaint.Default).HoverElevatePaint; init { if (!EqualityComparer<bool>.Default.Equals((_cPaint ?? BoxColdPaint.Default).HoverElevatePaint, value)) CPaint.HoverElevatePaint = value; } }

    /// <summary>Drag-ARM barrier: a press that lands on (or inside) this element never arms a DRAGGABLE ANCESTOR's
    /// gesture. A draggable row/card normally lets a press on any of its children start the drag (the WinUI
    /// item-container rule, implemented as an upward walk from the press target); set this on a child that is its own
    /// affordance — a card's play FAB, its "…" corner button, an inline toggle — so pressing it stays a press on THAT
    /// control instead of becoming a handle for dragging the card. Purely a discriminator: layout, hit-testing, and
    /// this element's own click/press handling are unaffected, and it says nothing about whether the element itself is
    /// draggable (set <c>Draggable</c>/<c>CanDrag</c> for that).</summary>
    public bool BlocksDragArm { get => (_cInput ?? BoxColdInput.Default).BlocksDragArm; init { if (!EqualityComparer<bool>.Default.Equals((_cInput ?? BoxColdInput.Default).BlocksDragArm, value)) CInput.BlocksDragArm = value; } }

    /// <summary>A pointer LISTENER that is not an interaction scope (the ToolTip service wrapper): it carries pointer
    /// handlers, so it is hit-testable and receives them normally, but the hover cascade, the lazy-mount hover seed and
    /// the un-hover re-resolve look THROUGH it to the nearest real interactive ancestor. A card's hover then still
    /// reveals a wrapped play FAB. Hit-testing and handler delivery are unchanged.</summary>
    public bool HoverScopeTransparent { get => (_cInput ?? BoxColdInput.Default).HoverScopeTransparent; init { if (!EqualityComparer<bool>.Default.Equals((_cInput ?? BoxColdInput.Default).HoverScopeTransparent, value)) CInput.HoverScopeTransparent = value; } }

    /// <summary>Clip-ESCAPE root for a hover-elevated descendant (pairs with <see cref="HoverElevatePaint"/>): set on a
    /// clipping viewport (a shelf's paged strip) to let the hovered card's lift + halo paint OUTSIDE this clip. The
    /// recorder HOISTS the deferred elevated descendant out of this node's whole record scope — its clip AND its
    /// edge-fade — and records it after the scope closes, against the clip in effect outside this node. Resting content
    /// still clips exactly here (nothing else escapes). Innermost flagged ancestor wins; layout/hit-testing unaffected.</summary>
    public bool HoverElevateClipRoot { get => (_cPaint ?? BoxColdPaint.Default).HoverElevateClipRoot; init { if (!EqualityComparer<bool>.Default.Equals((_cPaint ?? BoxColdPaint.Default).HoverElevateClipRoot, value)) CPaint.HoverElevateClipRoot = value; } }

    /// <summary>Layout firewall (opt-in): declare that this box's size is PARENT-determined (it fills/clips and is never
    /// content-sized), so a re-render or state change deep inside its subtree re-solves ONLY this subtree (scoped layout)
    /// instead of falling back to a full-tree layout from the root. Use on a page/content host that fills the shell content
    /// region. Contract: only set this where the box truly cannot need to change its own outer size from a descendant — the
    /// scoped relayout reuses its current bounds. A window resize still triggers a full layout, so resize stays correct.</summary>
    public bool IsolateLayout { get => (_cMisc ?? BoxColdMisc.Default).IsolateLayout; init { if (!EqualityComparer<bool>.Default.Equals((_cMisc ?? BoxColdMisc.Default).IsolateLayout, value)) CMisc.IsolateLayout = value; } }

    /// <summary>Opt this box into general layout-change animation: the host diffs its presented rect vs its new
    /// laid-out rect each commit and drives the residual through the spec's channels/dynamics (no relayout, no
    /// per-frame re-render). Null ⇒ snap (the default). See <see cref="FluentGpu.Foundation.LayoutTransition"/>.</summary>
    public LayoutTransition? Animate { get => (_cGesture ?? BoxColdGesture.Default).Animate; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals((_cGesture ?? BoxColdGesture.Default).Animate, value)) CGesture.Animate = value; } }

    /// <summary>Opt this child OUT of a <see cref="FluentGpu.Foundation.SizeMode.ScaleCorrect"/> ancestor's scale: the
    /// recorder applies the inverse scale so the child stays undistorted (Framer-Motion projection correction).</summary>
    public bool CounterScale { get => (_cMotion ?? BoxColdMotion.Default).CounterScale; init { if (!EqualityComparer<bool>.Default.Equals((_cMotion ?? BoxColdMotion.Default).CounterScale, value)) CMotion.CounterScale = value; } }

    // Flexbox
    /// <summary>Unified channels (Prop&lt;T&gt;): static size, thunk, or concrete signal (bound ⇒ scoped relayout).</summary>
    public Prop<float> Width { get; init; } = float.NaN;
    public Prop<float> Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get => (_cMisc ?? BoxColdMisc.Default).MaxHeight; init { if (!EqualityComparer<float>.Default.Equals((_cMisc ?? BoxColdMisc.Default).MaxHeight, value)) CMisc.MaxHeight = value; } }
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
    public bool MeasureUnboundedWidth { get => (_cMisc ?? BoxColdMisc.Default).MeasureUnboundedWidth; init { if (!EqualityComparer<bool>.Default.Equals((_cMisc ?? BoxColdMisc.Default).MeasureUnboundedWidth, value)) CMisc.MeasureUnboundedWidth = value; } }
    /// <summary>CSS <c>aspect-ratio</c> (width÷height): derive the missing extent for a fluid box. NaN (default) = off.
    /// When exactly one of Width/Height is set the other is derived; when both are fluid, the box takes the offered
    /// width and derives its height. Both explicit ⇒ aspect ignored. Routed through the shared <c>LayoutInput.AspectRatio</c>
    /// column (the ImageEl precedent), so an aspect-sized box is NOT a layout boundary (one dimension stays NaN).</summary>
    public float AspectRatio { get => (_cMisc ?? BoxColdMisc.Default).AspectRatio; init { if (!EqualityComparer<float>.Default.Equals((_cMisc ?? BoxColdMisc.Default).AspectRatio, value)) CMisc.AspectRatio = value; } }
}

/// <summary>
/// A CSS-Grid container: column tracks (Pixel/Star/Auto) + gaps; children auto-flow row-major into cells. Rows take
/// <see cref="RowHeight"/> (or the tallest cell when NaN). True tracks — every cell in a column shares its width
/// (unlike nested-flex faking). Width-aware: resolves inside a ScrollView. (layout.md §7.)
/// </summary>
public sealed record GridEl : Element
{
    public override ushort ElementTypeId => 9;

    public TrackSize[] Columns { get; init; } = [];
    public float ColGap { get; init; }
    public float RowGap { get; init; }
    public float RowHeight { get; init; } = float.NaN;   // NaN = auto (tallest cell in the row)
    public float MinColWidth { get; init; }              // > 0 = auto-fill: as many 1fr columns as fit at this min width (Columns ignored)
    /// <summary>Auto-fill mode only: the column count never exceeds this (0 = unlimited). The capped tracks still
    /// share the full width (they grow past <see cref="MinColWidth"/>) — CSS <c>repeat(auto-fill, …)</c> with a max
    /// column count. Ignored by fixed-track grids.</summary>
    public int MaxColumns { get; init; }
    public Element[] Children { get; init; } = [];

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
}

/// <summary>A text run. <see cref="FontFamily"/> selects the face — a system family name ("Segoe UI",
/// "Segoe Fluent Icons") or a custom file as <c>"path/to.ttf#Family Name"</c> (the WinUI syntax). Null = the theme
/// body font. Combine with an icon-font family + a private-use glyph to render icons (see <c>Ui.Icon</c>).</summary>
/// <summary>A leaf stroked polyline, drawn analytically by the renderer and revealable through stroke trim.</summary>
public sealed record PolylineStrokeEl : Element
{
    public override ushort ElementTypeId => 11;

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
public sealed record PathEl : Element
{
    // ElementTypeId 16. IDs 1-5 and 7-15 are already taken by other Element records (see the grep of every
    // `ElementTypeId =>` in this assembly). 6 was documented (by the approved plan this element implements) as "a
    // free hole from a retired type — do NOT recycle it" — but as-built, 6 is CURRENTLY LIVE (VirtualListEl,
    // Reconciler/VirtualListEl.cs), so that premise did not match this tree at the time this was authored. It does
    // not change the outcome: 1-15 are all taken regardless, so 16 remains the correct next-free id. Left here so a
    // future reader isn't confused by a stale "6 is free" claim anywhere upstream of this file.
    public override ushort ElementTypeId => 16;

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
}

public sealed record TextEl(Prop<string> Text) : Element
{
    // The rarely-set channels (state colors, wipe, selection, underline, ...) live in one shared copy-on-write TextCold
    // (TextCold.cs), like BoxEl's blocks: a TextEl is allocated and `with`-copied on every render of every label.
    private TextCold? _cold;
    private TextCold Cold => _cold is { } c && ReferenceEquals(c.Owner, this) ? c : (_cold = (_cold ?? TextCold.Default).CloneFor(this));
    public override ushort ElementTypeId => 2;

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
    public ushort ResolvedWeight => Weight != 0 ? Weight : Bold ? (ushort)700 : (ushort)400;
    /// <summary>WinUI <c>TextElement.CharacterSpacing</c>: tracking in 1/1000 em (negative = tighter), applied as a
    /// per-glyph trailing advance adjustment after shaping (e.g. Pivot headers use −25).</summary>
    public float CharSpacing { get => (_cold ?? TextCold.Default).CharSpacing; init { if (!EqualityComparer<float>.Default.Equals((_cold ?? TextCold.Default).CharSpacing, value)) Cold.CharSpacing = value; } }
    /// <summary>WinUI <c>TextBlock.LineHeight</c> in DIP (NaN = font-natural); interpreted per <see cref="LineStacking"/>.</summary>
    public float LineHeight { get; init; } = float.NaN;
    /// <summary>WinUI <c>TextBlock.LineStackingStrategy</c> (default MaxHeight — TextBlock_themeresources.xaml:16):
    /// how an explicit <see cref="LineHeight"/> combines with the font-natural line box.</summary>
    public LineStacking LineStacking { get => (_cold ?? TextCold.Default).LineStacking; init { if (!EqualityComparer<LineStacking>.Default.Equals((_cold ?? TextCold.Default).LineStacking, value)) Cold.LineStacking = value; } }
    /// <summary>WinUI <c>TextBlock.TextLineBounds</c> (default Full — TextBlock_themeresources.xaml:17): Tight trims
    /// the measured line box to cap-height..baseline so vertical centering is optical (PersonPicture initials).</summary>
    public TextLineBounds LineBounds { get => (_cold ?? TextCold.Default).LineBounds; init { if (!EqualityComparer<TextLineBounds>.Default.Equals((_cold ?? TextCold.Default).LineBounds, value)) Cold.LineBounds = value; } }
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
    public ColorF HoverColor { get => (_cold ?? TextCold.Default).HoverColor; init { if (!EqualityComparer<ColorF>.Default.Equals((_cold ?? TextCold.Default).HoverColor, value)) Cold.HoverColor = value; } }
    public ColorF PressedColor { get => (_cold ?? TextCold.Default).PressedColor; init { if (!EqualityComparer<ColorF>.Default.Equals((_cold ?? TextCold.Default).PressedColor, value)) Cold.PressedColor = value; } }
    public ColorF DisabledColor { get => (_cold ?? TextCold.Default).DisabledColor; init { if (!EqualityComparer<ColorF>.Default.Equals((_cold ?? TextCold.Default).DisabledColor, value)) Cold.DisabledColor = value; } }
    public ColorF FocusedColor { get => (_cold ?? TextCold.Default).FocusedColor; init { if (!EqualityComparer<ColorF>.Default.Equals((_cold ?? TextCold.Default).FocusedColor, value)) Cold.FocusedColor = value; } }
    /// <summary>WinUI <c>TextDecorations.Underline</c>: the recorder draws the face-metric underline bar (DWrite
    /// underlinePosition/underlineThickness, cached at measure on the scene's TextMeasureCache) under the run, in the
    /// SAME resolved foreground as the glyphs (hover/press ramps + BrushTransition). Single-line frame — per-line
    /// decoration of wrapped runs is the rich-text (SpanTextEl/RichTextBlock) pass. HyperlinkButton drives this from
    /// its HyperlinkUnderlineVisible/HighContrast gate (HyperLinkButton_Partial.cpp:207-212).</summary>
    public bool Underline { get => (_cold ?? TextCold.Default).Underline; init { if (!EqualityComparer<bool>.Default.Equals((_cold ?? TextCold.Default).Underline, value)) Cold.Underline = value; } }
    /// <summary>WinUI <c>TextDecorations.Strikethrough</c>: the face-metric strikethrough bar (reuses the underline
    /// thickness, the DWrite convention) — e.g. CalendarView blackout dates.</summary>
    public bool Strikethrough { get => (_cold ?? TextCold.Default).Strikethrough; init { if (!EqualityComparer<bool>.Default.Equals((_cold ?? TextCold.Default).Strikethrough, value)) Cold.Strikethrough = value; } }
    /// <summary>Implicit brush transition for the resting <see cref="Color"/>: a re-render that changes it on this LIVE
    /// node cross-fades over this duration (WinUI BrushTransition, 83ms in templates). NaN = snap.</summary>
    public float BrushTransitionMs { get => (_cold ?? TextCold.Default).BrushTransitionMs; init { if (!EqualityComparer<float>.Default.Equals((_cold ?? TextCold.Default).BrushTransitionMs, value)) Cold.BrushTransitionMs = value; } }
    /// <summary>Optional left→right glyph WIPE fill (a general text-reveal — the lyrics karaoke uses it): glyphs left of
    /// <see cref="GlyphWipe.Split"/> use <see cref="GlyphWipe.Before"/>, right use <see cref="GlyphWipe.After"/>, with a
    /// soft boundary + optional per-glyph lift. Null = off. Carried in a sparse scene side-table (NOT on the hot paint
    /// struct), emitted as a gradient glyph run; advancing the split per frame is reshape-free.</summary>
    public GlyphWipe? Wipe { get => (_cold ?? TextCold.Default).Wipe; init { if (!EqualityComparer<GlyphWipe?>.Default.Equals((_cold ?? TextCold.Default).Wipe, value)) Cold.Wipe = value; } }

    /// <summary>Called once when this glyph run is realized into the scene, with its node handle — lets a control drive the
    /// node directly (the lyrics ticker advances THIS run's <see cref="Wipe"/> split per frame on the scene side-table).</summary>
    public Action<NodeHandle>? OnRealized { get; init; }
    /// <summary>Read-only text selection (rtb-02): mouse drag selects, double-click selects the word, triple-click all,
    /// Ctrl+C copies via the clipboard seam; the highlight reuses the editor's selection-rect path. Default FALSE —
    /// WinUI TextBlock selection is opt-in (TextBlock.cpp:583 IsTextSelectionEnabled property change creates the
    /// selection manager on demand); RichTextBlock turns it on by default (RichTextBlock.cpp:1730). A selectable run
    /// is focusable (Ctrl+C routes to it) and shows the I-beam cursor.</summary>
    public bool IsTextSelectionEnabled { get => (_cold ?? TextCold.Default).IsTextSelectionEnabled; init { if (!EqualityComparer<bool>.Default.Equals((_cold ?? TextCold.Default).IsTextSelectionEnabled, value)) Cold.IsTextSelectionEnabled = value; } }
    /// <summary>Per-control selection highlight (api-04, WinUI <c>TextBlock.SelectionHighlightColor</c> —
    /// TextBlock.cpp:266/330). A==0 (default) = the engine/theme brush (the system accent,
    /// TextSelectionManager.cpp:52-56 GetDefaultSelectionHighlightColor → GetSystemAccentColor ≡ the host's
    /// TextEditStyle.SelectionFill).</summary>
    public ColorF SelectionHighlightColor { get => (_cold ?? TextCold.Default).SelectionHighlightColor; init { if (!EqualityComparer<ColorF>.Default.Equals((_cold ?? TextCold.Default).SelectionHighlightColor, value)) Cold.SelectionHighlightColor = value; } }
    public string? FontFamily { get; init; }
    public DynamicTextKind DynamicText { get => (_cold ?? TextCold.Default).DynamicText; init { if (!EqualityComparer<DynamicTextKind>.Default.Equals((_cold ?? TextCold.Default).DynamicText, value)) Cold.DynamicText = value; } }
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
    public float MinSize { get => (_cold ?? TextCold.Default).MinSize; init { if (!EqualityComparer<float>.Default.Equals((_cold ?? TextCold.Default).MinSize, value)) Cold.MinSize = value; } }

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
public sealed record SpanTextEl(Prop<TextSpans> Spans) : Element
{
    public override ushort ElementTypeId => 12;

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
}

/// <summary>
/// An async image (album art): shows <see cref="Placeholder"/> until the decode lands, then the bitmap. Decode is
/// off-thread + cached + residency-pinned while on screen (see <c>ImageCache</c>).
/// </summary>
public sealed record ImageEl : Element
{
    public override ushort ElementTypeId => 8;

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
    /// <summary>Decode-size hint (target px) used when the layout extent is fluid (<see cref="Width"/> is <c>NaN</c>, so the
    /// real box size isn't known at request time). Ignored when <see cref="Width"/> is explicit (that drives the decode).
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
}

/// <summary>A single ThemedIcon vector layer (leaf): a colorless coverage mask (interned in
/// <c>IconGeometryTable.Shared</c> as <see cref="PathId"/>) painted at <see cref="Tint"/>. A layered icon is a
/// <c>ZStack</c> of these (built by <c>ThemedIcon.Create</c>). <see cref="Tint"/> is always a BOUND <c>Prop</c>
/// (a role→color thunk reading <c>Tok</c>) so <c>RethemeAll</c> live-recolors it with no re-raster — never a frozen
/// ctor color (component-props-contract.md). Reconciled onto <c>VisualKind.IconLayer</c> where <c>ImageId</c> doubles
/// as the PathId; the recorder emits <c>DrawIconMask</c>.</summary>
public sealed record IconLayerEl : Element
{
    public override ushort ElementTypeId => 15;

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
}

/// <summary>
/// A clipping, scrolling viewport over a single (possibly oversized) content child. Scroll is layout-free
/// (layout.md §6): layout arranges the content at the content-box origin and publishes <c>ContentSize</c>; the
/// <c>-ScrollOffset</c> translation is the content's <c>LocalTransform</c>, written by Input on wheel/drag.
/// </summary>
public sealed record ScrollEl : Element
{
    public override ushort ElementTypeId => 5;

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
}
