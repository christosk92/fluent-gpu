using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

// The rarely-set BoxEl channels, split into six themed blocks so a box that sets none costs six null references and one
// that sets a few clones only the block it touches. Copy-on-write: BoxEl's `with` copies share a block until a setter that
// changes a value runs on the copy, which clones it first (Owner tells a clone made for that element from a shared one). A
// setter that writes the value a channel already holds (the default included) does not clone. Defaults mirror the former
// inline initializers exactly; Owner is excluded from equality. Owner keeps the element it was cloned for alive; that is bounded (one per with-chain link that mutated the block) and dies with the block.

/// <summary>BoxEl's common rich paint (gradient, border brush, shadow, border and clip extras).</summary>
internal sealed class BoxColdPaint : IEquatable<BoxColdPaint>
{
    public static readonly BoxColdPaint Default = new();

    public BoxEl? Owner;

    public ColorF HoverBorderColor;
    public ColorF PressedBorderColor;
    public float BorderDashOn;
    public float BorderDashOff;
    public Prop<ValidationState> Validation = default;
    public ShadowSpec? Shadow;
    public GradientSpec? Gradient;
    public PaintBlend Blend = PaintBlend.SrcOver;
    public LayerBlend LayerBlend = LayerBlend.SrcOver;
    public GradientSpec? BorderBrush;
    public bool RepaintBoundary;
    public float RasterScale = 1f;
    public bool TabShape;
    public float TabFlareRadius = 4f;
    public bool VideoHole;
    public int VideoSurfaceId;
    public PathData? ClipPath;
    public FillRule ClipPathRule = FillRule.NonZero;
    public float ClipPathViewBoxW;
    public float ClipPathViewBoxH;
    public bool HoverElevatePaint;
    public bool HoverElevateClipRoot;

    public BoxColdPaint CloneFor(BoxEl owner)
    {
        var c = (BoxColdPaint)MemberwiseClone();
        c.Owner = owner;
        return c;
    }

    public bool Equals(BoxColdPaint? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null) return false;
        return EqualityComparer<ColorF>.Default.Equals(HoverBorderColor, o.HoverBorderColor)
            && EqualityComparer<ColorF>.Default.Equals(PressedBorderColor, o.PressedBorderColor)
            && EqualityComparer<float>.Default.Equals(BorderDashOn, o.BorderDashOn)
            && EqualityComparer<float>.Default.Equals(BorderDashOff, o.BorderDashOff)
            && EqualityComparer<Prop<ValidationState>>.Default.Equals(Validation, o.Validation)
            && EqualityComparer<ShadowSpec?>.Default.Equals(Shadow, o.Shadow)
            && EqualityComparer<GradientSpec?>.Default.Equals(Gradient, o.Gradient)
            && EqualityComparer<PaintBlend>.Default.Equals(Blend, o.Blend)
            && EqualityComparer<LayerBlend>.Default.Equals(LayerBlend, o.LayerBlend)
            && EqualityComparer<GradientSpec?>.Default.Equals(BorderBrush, o.BorderBrush)
            && EqualityComparer<bool>.Default.Equals(RepaintBoundary, o.RepaintBoundary)
            && EqualityComparer<float>.Default.Equals(RasterScale, o.RasterScale)
            && EqualityComparer<bool>.Default.Equals(TabShape, o.TabShape)
            && EqualityComparer<float>.Default.Equals(TabFlareRadius, o.TabFlareRadius)
            && EqualityComparer<bool>.Default.Equals(VideoHole, o.VideoHole)
            && EqualityComparer<int>.Default.Equals(VideoSurfaceId, o.VideoSurfaceId)
            && EqualityComparer<PathData?>.Default.Equals(ClipPath, o.ClipPath)
            && EqualityComparer<FillRule>.Default.Equals(ClipPathRule, o.ClipPathRule)
            && EqualityComparer<float>.Default.Equals(ClipPathViewBoxW, o.ClipPathViewBoxW)
            && EqualityComparer<float>.Default.Equals(ClipPathViewBoxH, o.ClipPathViewBoxH)
            && EqualityComparer<bool>.Default.Equals(HoverElevatePaint, o.HoverElevatePaint)
            && EqualityComparer<bool>.Default.Equals(HoverElevateClipRoot, o.HoverElevateClipRoot);
    }

    public override bool Equals(object? obj) => Equals(obj as BoxColdPaint);
    public override int GetHashCode() => 0;
}

/// <summary>BoxEl's stateful and effect paint (hover/pressed gradients and borders, acrylic, edge fade, feedback, arc).</summary>
internal sealed class BoxColdPaintFx : IEquatable<BoxColdPaintFx>
{
    public static readonly BoxColdPaintFx Default = new();

    public BoxEl? Owner;

    public ArcSpec? Arc;
    public Prop<Point2> RadialGradientCenter = new Point2(float.NaN, float.NaN);
    public GradientSpec? GradientTo;
    public Prop<float> GradientMix = 0f;
    public FeedbackSpec? Feedback;
    public Prop<Affine2D> FeedbackTransform = Affine2D.Identity;
    public Prop<float> FeedbackDecay = float.NaN;
    public GradientSpec? HoverGradient;
    public GradientSpec? PressedGradient;
    public GradientSpec? HoverBorderBrush;
    public GradientSpec? PressedBorderBrush;
    public AcrylicSpec? Acrylic;
    public EdgeFadeSpec? EdgeFade;

    public BoxColdPaintFx CloneFor(BoxEl owner)
    {
        var c = (BoxColdPaintFx)MemberwiseClone();
        c.Owner = owner;
        return c;
    }

    public bool Equals(BoxColdPaintFx? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null) return false;
        return EqualityComparer<ArcSpec?>.Default.Equals(Arc, o.Arc)
            && EqualityComparer<Prop<Point2>>.Default.Equals(RadialGradientCenter, o.RadialGradientCenter)
            && EqualityComparer<GradientSpec?>.Default.Equals(GradientTo, o.GradientTo)
            && EqualityComparer<Prop<float>>.Default.Equals(GradientMix, o.GradientMix)
            && EqualityComparer<FeedbackSpec?>.Default.Equals(Feedback, o.Feedback)
            && EqualityComparer<Prop<Affine2D>>.Default.Equals(FeedbackTransform, o.FeedbackTransform)
            && EqualityComparer<Prop<float>>.Default.Equals(FeedbackDecay, o.FeedbackDecay)
            && EqualityComparer<GradientSpec?>.Default.Equals(HoverGradient, o.HoverGradient)
            && EqualityComparer<GradientSpec?>.Default.Equals(PressedGradient, o.PressedGradient)
            && EqualityComparer<GradientSpec?>.Default.Equals(HoverBorderBrush, o.HoverBorderBrush)
            && EqualityComparer<GradientSpec?>.Default.Equals(PressedBorderBrush, o.PressedBorderBrush)
            && EqualityComparer<AcrylicSpec?>.Default.Equals(Acrylic, o.Acrylic)
            && EqualityComparer<EdgeFadeSpec?>.Default.Equals(EdgeFade, o.EdgeFade);
    }

    public override bool Equals(object? obj) => Equals(obj as BoxColdPaintFx);
    public override int GetHashCode() => 0;
}

/// <summary>BoxEl's common interaction handlers (key, hover, focus, press, context) and focus extras.</summary>
internal sealed class BoxColdInput : IEquatable<BoxColdInput>
{
    public static readonly BoxColdInput Default = new();

    public BoxEl? Owner;

    public Action<KeyEventArgs>? OnKeyDown;
    public Action<PointerEventArgs>? OnPointerPressed;
    public Action<PointerEventArgs>? OnPointerReleased;
    public Action<ContextRequestEventArgs>? OnContextRequested;
    public bool ClickRequestsContext;
    public Action<Point2>? OnHoverMove;
    public Action? OnPointerExit;
    public Action<bool>? OnFocusChanged;
    public bool AllowFocusOnInteraction = true;
    public bool HitTestPassThrough;
    public bool? TabStop;
    public Edges4? FocusVisualMargin;
    public bool BlocksDragArm;
    public bool HoverScopeTransparent;

    public BoxColdInput CloneFor(BoxEl owner)
    {
        var c = (BoxColdInput)MemberwiseClone();
        c.Owner = owner;
        return c;
    }

    public bool Equals(BoxColdInput? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null) return false;
        return EqualityComparer<Action<KeyEventArgs>?>.Default.Equals(OnKeyDown, o.OnKeyDown)
            && EqualityComparer<Action<PointerEventArgs>?>.Default.Equals(OnPointerPressed, o.OnPointerPressed)
            && EqualityComparer<Action<PointerEventArgs>?>.Default.Equals(OnPointerReleased, o.OnPointerReleased)
            && EqualityComparer<Action<ContextRequestEventArgs>?>.Default.Equals(OnContextRequested, o.OnContextRequested)
            && EqualityComparer<bool>.Default.Equals(ClickRequestsContext, o.ClickRequestsContext)
            && EqualityComparer<Action<Point2>?>.Default.Equals(OnHoverMove, o.OnHoverMove)
            && EqualityComparer<Action?>.Default.Equals(OnPointerExit, o.OnPointerExit)
            && EqualityComparer<Action<bool>?>.Default.Equals(OnFocusChanged, o.OnFocusChanged)
            && EqualityComparer<bool>.Default.Equals(AllowFocusOnInteraction, o.AllowFocusOnInteraction)
            && EqualityComparer<bool>.Default.Equals(HitTestPassThrough, o.HitTestPassThrough)
            && EqualityComparer<bool?>.Default.Equals(TabStop, o.TabStop)
            && EqualityComparer<Edges4?>.Default.Equals(FocusVisualMargin, o.FocusVisualMargin)
            && EqualityComparer<bool>.Default.Equals(BlocksDragArm, o.BlocksDragArm)
            && EqualityComparer<bool>.Default.Equals(HoverScopeTransparent, o.HoverScopeTransparent);
    }

    public override bool Equals(object? obj) => Equals(obj as BoxColdInput);
    public override int GetHashCode() => 0;
}

/// <summary>BoxEl's rarely-used gestures (drag and drop, repeat, accelerators, pointer-down/drag/wheel) and the layout animation.</summary>
internal sealed class BoxColdGesture : IEquatable<BoxColdGesture>
{
    public static readonly BoxColdGesture Default = new();

    public BoxEl? Owner;

    public Action<CharEventArgs>? OnCharInput;
    public Action<Point2>? OnPointerDown;
    public Action<Point2>? OnDrag;
    public bool DragYieldsToPan;
    public KeyAccelerator? Accelerator;
    public char AccessKey;
    public Action<WheelEventArgs>? OnPointerWheel;
    public Action<Point2>? OnPointerMoveWithin;
    public bool CanDrag;
    public Action<DragEventArgs>? OnDragStarted;
    public Action<DragEventArgs>? OnDragDelta;
    public Action<DragEventArgs>? OnDragCompleted;
    public Action? OnDragCanceled;
    public DragSource? Draggable;
    public DropTargetSpec? DropTarget;
    public bool Repeats;
    public float RepeatDelayMs = float.NaN;
    public float RepeatIntervalMs = float.NaN;
    public bool ActivateOnEnter = true;
    public bool BlocksBackgroundScroll;
    public int TabIndex;
    public Action<RectF>? OnBoundsChanged;
    public Func<NodeHandle>? FollowRect;
    public LayoutTransition? Animate;

    public BoxColdGesture CloneFor(BoxEl owner)
    {
        var c = (BoxColdGesture)MemberwiseClone();
        c.Owner = owner;
        return c;
    }

    public bool Equals(BoxColdGesture? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null) return false;
        return EqualityComparer<Action<CharEventArgs>?>.Default.Equals(OnCharInput, o.OnCharInput)
            && EqualityComparer<Action<Point2>?>.Default.Equals(OnPointerDown, o.OnPointerDown)
            && EqualityComparer<Action<Point2>?>.Default.Equals(OnDrag, o.OnDrag)
            && EqualityComparer<bool>.Default.Equals(DragYieldsToPan, o.DragYieldsToPan)
            && EqualityComparer<KeyAccelerator?>.Default.Equals(Accelerator, o.Accelerator)
            && EqualityComparer<char>.Default.Equals(AccessKey, o.AccessKey)
            && EqualityComparer<Action<WheelEventArgs>?>.Default.Equals(OnPointerWheel, o.OnPointerWheel)
            && EqualityComparer<Action<Point2>?>.Default.Equals(OnPointerMoveWithin, o.OnPointerMoveWithin)
            && EqualityComparer<bool>.Default.Equals(CanDrag, o.CanDrag)
            && EqualityComparer<Action<DragEventArgs>?>.Default.Equals(OnDragStarted, o.OnDragStarted)
            && EqualityComparer<Action<DragEventArgs>?>.Default.Equals(OnDragDelta, o.OnDragDelta)
            && EqualityComparer<Action<DragEventArgs>?>.Default.Equals(OnDragCompleted, o.OnDragCompleted)
            && EqualityComparer<Action?>.Default.Equals(OnDragCanceled, o.OnDragCanceled)
            && EqualityComparer<DragSource?>.Default.Equals(Draggable, o.Draggable)
            && EqualityComparer<DropTargetSpec?>.Default.Equals(DropTarget, o.DropTarget)
            && EqualityComparer<bool>.Default.Equals(Repeats, o.Repeats)
            && EqualityComparer<float>.Default.Equals(RepeatDelayMs, o.RepeatDelayMs)
            && EqualityComparer<float>.Default.Equals(RepeatIntervalMs, o.RepeatIntervalMs)
            && EqualityComparer<bool>.Default.Equals(ActivateOnEnter, o.ActivateOnEnter)
            && EqualityComparer<bool>.Default.Equals(BlocksBackgroundScroll, o.BlocksBackgroundScroll)
            && EqualityComparer<int>.Default.Equals(TabIndex, o.TabIndex)
            && EqualityComparer<Action<RectF>?>.Default.Equals(OnBoundsChanged, o.OnBoundsChanged)
            && EqualityComparer<Func<NodeHandle>?>.Default.Equals(FollowRect, o.FollowRect)
            && EqualityComparer<LayoutTransition?>.Default.Equals(Animate, o.Animate);
    }

    public override bool Equals(object? obj) => Equals(obj as BoxColdGesture);
    public override int GetHashCode() => 0;
}

/// <summary>BoxEl's hover/press motion and the composited transform.</summary>
internal sealed class BoxColdMotion : IEquatable<BoxColdMotion>
{
    public static readonly BoxColdMotion Default = new();

    public BoxEl? Owner;

    public float OffsetX;
    public float OffsetY;
    public float ScaleX = 1f;
    public float ScaleY = 1f;
    public float Rotation;
    public float HoverOpacity = float.NaN;
    public float PressedOpacity = float.NaN;
    public bool OpacityGroup;
    public float Blur;
    public float TransformOriginX = 0.5f;
    public float TransformOriginY = 0.5f;
    public float HoverScale = 1f;
    public float PressScale = 1f;
    public float HoverDurationMs = float.NaN;
    public float PressDurationMs = float.NaN;
    public EasingSpec HoverEasing = Easing.FluentPopOpen;
    public EasingSpec PressEasing = Easing.FluentPopOpen;
    public float BrushTransitionMs = float.NaN;
    public bool CounterScale;

    public BoxColdMotion CloneFor(BoxEl owner)
    {
        var c = (BoxColdMotion)MemberwiseClone();
        c.Owner = owner;
        return c;
    }

    public bool Equals(BoxColdMotion? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null) return false;
        return EqualityComparer<float>.Default.Equals(OffsetX, o.OffsetX)
            && EqualityComparer<float>.Default.Equals(OffsetY, o.OffsetY)
            && EqualityComparer<float>.Default.Equals(ScaleX, o.ScaleX)
            && EqualityComparer<float>.Default.Equals(ScaleY, o.ScaleY)
            && EqualityComparer<float>.Default.Equals(Rotation, o.Rotation)
            && EqualityComparer<float>.Default.Equals(HoverOpacity, o.HoverOpacity)
            && EqualityComparer<float>.Default.Equals(PressedOpacity, o.PressedOpacity)
            && EqualityComparer<bool>.Default.Equals(OpacityGroup, o.OpacityGroup)
            && EqualityComparer<float>.Default.Equals(Blur, o.Blur)
            && EqualityComparer<float>.Default.Equals(TransformOriginX, o.TransformOriginX)
            && EqualityComparer<float>.Default.Equals(TransformOriginY, o.TransformOriginY)
            && EqualityComparer<float>.Default.Equals(HoverScale, o.HoverScale)
            && EqualityComparer<float>.Default.Equals(PressScale, o.PressScale)
            && EqualityComparer<float>.Default.Equals(HoverDurationMs, o.HoverDurationMs)
            && EqualityComparer<float>.Default.Equals(PressDurationMs, o.PressDurationMs)
            && EqualityComparer<EasingSpec>.Default.Equals(HoverEasing, o.HoverEasing)
            && EqualityComparer<EasingSpec>.Default.Equals(PressEasing, o.PressEasing)
            && EqualityComparer<float>.Default.Equals(BrushTransitionMs, o.BrushTransitionMs)
            && EqualityComparer<bool>.Default.Equals(CounterScale, o.CounterScale);
    }

    public override bool Equals(object? obj) => Equals(obj as BoxColdMotion);
    public override int GetHashCode() => 0;
}

/// <summary>BoxEl's layout and other extras.</summary>
internal sealed class BoxColdMisc : IEquatable<BoxColdMisc>
{
    public static readonly BoxColdMisc Default = new();

    public BoxEl? Owner;

    public bool IsolateLayout;
    public float MaxHeight = float.NaN;
    public bool MeasureUnboundedWidth;
    public float AspectRatio = float.NaN;

    public BoxColdMisc CloneFor(BoxEl owner)
    {
        var c = (BoxColdMisc)MemberwiseClone();
        c.Owner = owner;
        return c;
    }

    public bool Equals(BoxColdMisc? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null) return false;
        return EqualityComparer<bool>.Default.Equals(IsolateLayout, o.IsolateLayout)
            && EqualityComparer<float>.Default.Equals(MaxHeight, o.MaxHeight)
            && EqualityComparer<bool>.Default.Equals(MeasureUnboundedWidth, o.MeasureUnboundedWidth)
            && EqualityComparer<float>.Default.Equals(AspectRatio, o.AspectRatio);
    }

    public override bool Equals(object? obj) => Equals(obj as BoxColdMisc);
    public override int GetHashCode() => 0;
}
