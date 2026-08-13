using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>Why an <see cref="AnnotatedScrollBar"/> requested a scroll.</summary>
public enum AnnotatedScrollBarScrollKind : byte
{
    Click = 0,
    Drag = 1,
    IncrementButton = 2,
    DecrementButton = 3,
    Wheel = 4,
}

/// <summary>An annotation at an absolute content offset.</summary>
public readonly record struct AnnotatedScrollBarLabel(float ScrollOffset, string Text);

/// <summary>Live, re-pushed chrome and interaction options for <see cref="AnnotatedScrollBar"/>.</summary>
public sealed record AnnotatedScrollBarOptions
{
    public IReadOnlyList<AnnotatedScrollBarLabel> Labels { get; init; } = [];
    public IReadOnlyList<float> TickOffsets { get; init; } = [];
    public Func<AnnotatedScrollBarLabel, Element>? LabelTemplate { get; init; }
    public Func<float, AnnotatedScrollBarLabel?>? DetailLabelAtOffset { get; init; }
    public Func<AnnotatedScrollBarLabel, Element>? DetailTemplate { get; init; }
    /// <summary>Cancelable request filter. Return false to suppress the controller request.</summary>
    public Func<float, AnnotatedScrollBarScrollKind, bool>? Scrolling { get; init; }
    /// <summary>Explicit control height. A finite value &gt; 0 pins the bar (tests, gallery samples). NaN or ≤0 means
    /// stretch: the root has no explicit Height, fills the parent slot with <c>AlignSelf=Stretch</c> and
    /// <c>Grow=1</c>, and <c>UseMeasuredBounds</c> feeds tick/thumb math only — never written back as Height (that
    /// freeze is why a 280 fallback never grew with the page).</summary>
    public float Height { get; init; } = 280f;
    public TemplateParts? Parts { get; init; }
}

/// <summary>
/// The single geometry oracle for an annotated rail. Every rail position — thumb, ghost, tick, label, pointer decode —
/// shares ONE domain: the legal scroll range <c>[MinimumOffset, MaximumOffset]</c>. Offset = MaximumOffset is the
/// BOTTOM of the track (the last reachable date), not a leftover viewport fraction below it. A label or tick whose
/// content offset sits past Maximum (inside the final viewport) clamps onto that same end pixel. Pointer decode
/// returns a scroll offset; the bottom of the rail is the last reachable date.
/// </summary>
public readonly struct RailMetrics
{
    public RailMetrics(float minimumOffset, float maximumOffset, float viewportLength,
                       float railHeight, float thumbHeight)
    {
        MinimumOffset = Finite(minimumOffset, 0f);
        MaximumOffset = MathF.Max(MinimumOffset, Finite(maximumOffset, MinimumOffset));
        ViewportLength = MathF.Max(0f, Finite(viewportLength, 0f));
        RailHeight = MathF.Max(0f, Finite(railHeight, 0f));
        ThumbHeight = Math.Clamp(Finite(thumbHeight, 0f), 0f, RailHeight);
    }

    public float MinimumOffset { get; }
    public float MaximumOffset { get; }
    public float ViewportLength { get; }
    public float RailHeight { get; }
    public float ThumbHeight { get; }
    public float ScrollRange => MaximumOffset - MinimumOffset;
    public float ThumbTravel => MathF.Max(0f, RailHeight - ThumbHeight);
    public bool IsScrollable => ScrollRange > 0f && RailHeight > 0f;

    public float ClampScrollOffset(float offset)
        => Math.Clamp(Finite(offset, MinimumOffset), MinimumOffset, MaximumOffset);

    /// <summary>The one canonical rail-Y a click/drag/hover decodes against and every marker anchors to (A6): the
    /// CENTER of the thumb-equivalent marker at this offset — Position01×<see cref="ThumbTravel"/> plus half the
    /// thumb's own thickness. <see cref="ScrollOffsetToThumbTop"/>/<see cref="ContentOffsetToTickTop"/> return this
    /// same TOP-anchored scale (their box's own height centers it); this is the shared reference so a pointer
    /// landing on the visual thumb/tick/ghost decodes back to the exact offset that drew it. Previously the
    /// pointer-decode path used a THIRD, independent RailHeight-based scale that disagreed with the thumb/tick scale
    /// by the thumb/rail height delta (~2.3% drift at ThumbHeight=3/RailHeight=130).</summary>
    public float ContentOffsetToRailY(float offset)
    {
        return Position01(offset) * ThumbTravel + ThumbHeight * 0.5f;
    }

    /// <summary>Inverse of <see cref="ContentOffsetToRailY"/> over the legal scroll range — the single pointer-decode
    /// path shared by click, drag, and hover-detail (A6). The bottom of the rail is MaximumOffset (the last reachable
    /// date). Scroll requests still run the result through <see cref="ClampScrollOffset"/>.</summary>
    public float RailYToContentOffset(float railY)
    {
        if (RailHeight <= 0f || ThumbTravel <= 0f || ScrollRange <= 0f) return MinimumOffset;
        float p = Math.Clamp((Finite(railY, 0f) - ThumbHeight * 0.5f) / ThumbTravel, 0f, 1f);
        return MinimumOffset + p * ScrollRange;
    }

    public float ScrollOffsetToThumbTop(float offset)
    {
        return Position01(offset) * ThumbTravel;
    }

    public float ContentOffsetToTickTop(float offset)
        => Position01(offset) * ThumbTravel;

    /// <summary>Labels share the thumb/tick canonical scale (A6) instead of their own independent
    /// RailHeight-minus-labelHeight denominator, then clamp into the label's own legal band so a tall label never
    /// overshoots the rail's bottom edge.</summary>
    public float ContentOffsetToLabelTop(float offset, float labelHeight)
    {
        labelHeight = Math.Clamp(Finite(labelHeight, 0f), 0f, RailHeight);
        float top = Position01(offset) * ThumbTravel;
        return Math.Clamp(top, 0f, MathF.Max(0f, RailHeight - labelHeight));
    }

    /// <summary>Offset → fraction of the legal scroll range. A label/tick past <see cref="MaximumOffset"/> (content
    /// inside the final viewport that cannot be scrolled to the top) clamps to Maximum — the last date sits at the
    /// rail end instead of leaving an unreachable band under it.</summary>
    private float Position01(float offset)
    {
        if (ScrollRange <= 0f) return 0f;
        return (ClampScrollOffset(offset) - MinimumOffset) / ScrollRange;
    }

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}

public readonly record struct RailLabelContainer(int Index, float Top, float Height);

/// <summary>Pure WinUI-style endpoint-priority label collision collapse.</summary>
public static class RailLabelCollision
{
    public static bool[] Collapse(ReadOnlySpan<RailLabelContainer> containers, float railHeight)
        => Collapse(containers, 0f, railHeight);

    /// <param name="minY">Inclusive top of the legal range (rail-local). The rail already sits between the
    /// <see cref="AnnotatedScrollBar.ButtonCell"/> end buttons, so callers pass 0 unless they overlay labels on the
    /// full control height.</param>
    /// <param name="maxY">Exclusive-enough bottom of the legal range (a label's bottom must be ≤ this).</param>
    public static bool[] Collapse(ReadOnlySpan<RailLabelContainer> containers, float minY, float maxY)
    {
        var visible = new bool[containers.Length];
        float span = maxY - minY;
        if (containers.Length == 0 || span <= 0f) return visible;

        var ordered = containers.ToArray();
        Array.Sort(ordered, static (a, b) => a.Top.CompareTo(b.Top));

        int last = ordered.Length - 1;
        visible[ordered[last].Index] = true;                  // the bottom label has endpoint priority
        float lowerTop = ordered[last].Top;

        float firstBottom = float.NegativeInfinity;
        if (last == 0)
        {
            visible[ordered[0].Index] = true;
            return visible;
        }

        var first = ordered[0];
        float firstEnd = first.Top + MathF.Max(0f, first.Height);
        if (first.Top >= minY && firstEnd <= lowerTop && firstEnd <= maxY)
        {
            visible[first.Index] = true;                       // first stays unless it collides with the last
            firstBottom = firstEnd;
        }

        // WinUI walks from the bottom upward. A middle label survives only when it is in bounds and fits between the
        // already-kept lower label and the endpoint-priority first label.
        for (int i = last - 1; i > 0; i--)
        {
            var candidate = ordered[i];
            float bottom = candidate.Top + MathF.Max(0f, candidate.Height);
            if (candidate.Top < minY || bottom > maxY || bottom > lowerTop || candidate.Top < firstBottom)
                continue;
            visible[candidate.Index] = true;
            lowerTop = candidate.Top;
        }

        return visible;
    }
}

/// <summary>
/// WinUI AnnotatedScrollBar composition: absolute-positioned labels and ticks, a live accent thumb, pointer detail
/// preview, repeat buttons, and keyboard navigation, connected through <see cref="AnnotatedScrollBarController"/>.
/// </summary>
public static class AnnotatedScrollBar
{
    public const string PartLabels = "asb-labels";
    public const string PartTicks = "asb-ticks";
    public const string PartRail = "asb-rail";
    public const string PartGhost = "asb-ghost";
    public const string PartTip = "asb-tip";
    public const string PartThumb = "asb-thumb";

    // AnnotatedScrollBar_themeresources.xaml / AnnotatedScrollBar.xaml.
    internal const float ThumbWidth = 30f;
    internal const float ThumbHeight = 3f;
    internal const float ThumbRadius = 1.5f;
    internal const float LabelsMinWidth = 44f;
    internal const float LabelSize = 14f;
    internal const float ButtonGlyph = 8f;
    internal const float ButtonCell = 16f;
    internal const float TooltipMaxWidth = 360f;
    internal const float TooltipMinHeight = 40f;
    internal const float ContentLayoutDebounceMs = 50f;
    internal const float SizeLayoutDebounceMs = 500f;
    // A4: the default label template's -5 top margin (WinUI's LabelTemplate Margin) trims 5 DIP off the rendered
    // glyph; the topmost label must sit at least this far from the labels grid's ClipToBounds=true top edge or its
    // ascenders render into the clip.
    internal const float LabelTopBleed = 5f;
    // A11: tick density cap — quantize tick tops to buckets this wide (ThumbHeight+1) and emit at most one tick per
    // bucket, so hundreds of eagerly-built per-day tick Elements collapse to what the rail can actually resolve.
    internal const float MinTickGap = ThumbHeight + 1f;

    public static Element Create(AnnotatedScrollBarController controller, AnnotatedScrollBarOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return Embed.Comp(new Props(controller, options ?? new AnnotatedScrollBarOptions()),
            static () => new AnnotatedScrollBarCore());
    }

    /// <summary>String convenience surface; positions remain absolute content offsets.</summary>
    public static Element Create(AnnotatedScrollBarController controller,
                                 IReadOnlyList<(string Label, float ScrollOffset)> labels,
                                 IReadOnlyList<float>? ticks = null,
                                 Func<float, string?>? detailLabelAtOffset = null,
                                 float height = 280f,
                                 Func<float, AnnotatedScrollBarScrollKind, bool>? scrolling = null,
                                 TemplateParts? parts = null)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var converted = new AnnotatedScrollBarLabel[labels.Count];
        for (int i = 0; i < labels.Count; i++)
            converted[i] = new AnnotatedScrollBarLabel(labels[i].ScrollOffset, labels[i].Label ?? string.Empty);

        Func<float, AnnotatedScrollBarLabel?>? detail = detailLabelAtOffset is null
            ? null
            : offset => detailLabelAtOffset(offset) is { } text
                // The convenience detail identity is its text bucket, not the per-pixel pointer offset. Keeping the
                // stored offset stable means a pointer move inside one bucket updates only compositor bindings.
                ? new AnnotatedScrollBarLabel(0f, text)
                : null;
        return Create(controller, new AnnotatedScrollBarOptions
        {
            Labels = converted,
            TickOffsets = ticks ?? [],
            DetailLabelAtOffset = detail,
            Height = height,
            Scrolling = scrolling,
            Parts = parts,
        });
    }

    internal sealed record Props(AnnotatedScrollBarController Controller, AnnotatedScrollBarOptions Options);
}

internal sealed class AnnotatedScrollBarCore : Component
{
    /// <summary>Measured label content heights only. Tops and collision visibility are cheap pure math over the LIVE
    /// <see cref="RailMetrics"/> and are recomputed synchronously each render — during sustained extent correction
    /// (a long fling realizing rows) the old debounced tops froze at a stale mapping while the ticks and thumb kept
    /// moving. Heights are the one part that needs realized nodes, so only they stay behind the measure debounce.
    /// <see cref="Source"/>/<see cref="RailHeight"/> gate staleness by identity, not by count.</summary>
    private sealed record LabelLayoutSnapshot(object Source, float RailHeight, float[] Heights);

    public override Element Render()
    {
        var props = UseProps<AnnotatedScrollBar.Props>();
        var controller = props.Controller;
        var options = props.Options;
        var bounds = UseMeasuredBounds();
        float measuredHeight = bounds.Value.H;
        bool pinHeight = float.IsFinite(options.Height) && options.Height > 0f;
        float height = pinHeight ? options.Height
            : measuredHeight > 0f ? measuredHeight : 280f;
        float railHeight = MathF.Max(0f, height - 2f * AnnotatedScrollBar.ButtonCell);
        // Range/viewport changes are layout events and re-render the rail. Offset is deliberately read only by the
        // thumb binding and event handlers, so ordinary scrolling remains compositor-only.
        float minimumOffset = controller.MinimumOffset.Value;
        float maximumOffset = controller.MaximumOffset.Value;
        float viewportLength = controller.ViewportLength.Value;
        var metrics = new RailMetrics(minimumOffset, maximumOffset, viewportLength,
            railHeight, AnnotatedScrollBar.ThumbHeight);
        // Bind thunks keep the first Func they were mounted with — a captured `railHeight` local would freeze the
        // thumb on the frame-one fallback while a stretched track kept growing. The signal is the live denominator.
        var liveRailHeight = UseFloatSignal(railHeight);
        liveRailHeight.SetIfChanged(railHeight);

        var hoverY = UseFloatSignal(float.NaN);
        var dragging = UseRef(false);
        var exitedWhileDragging = UseRef(false);
        var detail = UseSignal<AnnotatedScrollBarLabel?>(null);
        var tipHeight = UseFloatSignal(AnnotatedScrollBar.TooltipMinHeight);
        var tipNode = UseRef(NodeHandle.Null);
        var labelNodes = UseMemo(() => new NodeHandle[options.Labels.Count],
            DepKey.Combine(DepKey.FromRef(options.Labels), DepKey.From(options.Labels.Count)));
        var labelLayout = UseSignal<LabelLayoutSnapshot?>(null);

        void MeasureLabels()
        {
            int count = options.Labels.Count;
            var heights = new float[count];
            var scene = Context.Scene;
            for (int i = 0; i < count; i++)
                heights[i] = MeasureLabelContent(scene, i < labelNodes.Length ? labelNodes[i] : NodeHandle.Null,
                    railHeight);
            var prior = labelLayout.Peek();
            if (prior is not null && ReferenceEquals(prior.Source, options.Labels)
                && prior.RailHeight == railHeight && prior.Heights.AsSpan().SequenceEqual(heights)) return;
            labelLayout.Value = new LabelLayoutSnapshot(options.Labels, railHeight, heights);
        }

        // The measure debounce deps deliberately EXCLUDE Min/Max/Viewport: a trailing debounce restarts on every dep
        // change, and extent corrections land every frame during a long fling — keying the timer on the metrics
        // starved it and froze the labels at a stale mapping. Tops/collision no longer need the timer at all (pure
        // math in BuildLabels); only node measurement waits for realized labels.
        var labelDeps = DepKey.Combine(DepKey.FromRef(options.Labels, options.LabelTemplate),
            DepKey.From(options.Labels.Count));
        UseTimeout(MeasureLabels, AnnotatedScrollBar.ContentLayoutDebounceMs, labelDeps);
        UseTimeout(MeasureLabels, AnnotatedScrollBar.SizeLayoutDebounceMs, DepKey.From(railHeight));

        var currentLayout = labelLayout.Value;
        // Keep the last measured heights while a replacement Labels array is debounced. Heights are the only
        // measured part left (tops/visibility are recomputed from the live metrics every render), and a height is a
        // position-independent content measurement — so count + rail height is a safe reuse gate for a same-count
        // replacement (Wavee rebuilds the array on every measured-extent bump). Unmeasured labels fall back to
        // LabelSize; the collision pass always runs, so there is no all-visible flash either way.
        bool hasHeights = currentLayout is not null
            && currentLayout.RailHeight == railHeight
            && currentLayout.Heights.Length == options.Labels.Count;

        Element DefaultLabel(AnnotatedScrollBarLabel label) => new TextEl(label.Text ?? string.Empty)
        {
            Size = AnnotatedScrollBar.LabelSize,
            Color = Tok.TextPrimary,
            Margin = new Edges4(0f, -5f, 0f, -2f), // LabelTemplate margin 0,-5,0,-2.
        };

        var labels = UseMemo(() => BuildLabels(options, metrics, labelNodes,
                hasHeights ? currentLayout!.Heights : null, DefaultLabel),
            DepKey.Combine(
                DepKey.Combine(DepKey.FromRef(options.Labels, options.LabelTemplate), DepKey.From(options.Labels.Count)),
                DepKey.Combine(DepKey.FromRef(currentLayout),
                    DepKey.From(metrics.MinimumOffset, metrics.MaximumOffset, metrics.ViewportLength, railHeight))));
        var ticks = UseMemo(() => BuildTicks(options.TickOffsets, metrics),
            DepKey.Combine(DepKey.Combine(DepKey.FromRef(options.TickOffsets), DepKey.From(options.TickOffsets.Count)),
                DepKey.From(metrics.MinimumOffset, metrics.MaximumOffset, metrics.ViewportLength, railHeight)));

        // Event handlers and the re-resolve effect decode with the LIVE controller geometry, never the render-frame
        // `metrics` closure: extent corrections land every frame during a fling/realization, and a click decoded with
        // the previous frame's max lands rows away from where the (live-metric) thumb then draws. `metrics` stays the
        // element-construction snapshot only (labels/ticks memo keys re-render on geometry change anyway).
        void ResolveDetailAt(float y)
        {
            var resolve = options.DetailLabelAtOffset;
            var next = resolve?.Invoke(LiveMetrics(controller, liveRailHeight.Value).RailYToContentOffset(y));
            detail.SetIfChanged(next);
        }

        void ResolveDetail(float y)
        {
            hoverY.Value = y;
            ResolveDetailAt(y);
        }

        // W-bug-1: the flyout is a pointer preview, but the mapping UNDER the stationary pointer moves during
        // momentum and extent correction — and the dispatcher never re-fires OnHoverMove for an unchanged hovered
        // node. Auto-tracked effect: any controller-geometry change re-resolves the date at the current hover Y, so
        // the tip stays glued to the live mapping. SetIfChanged bounds the write; the body is allocation-free while
        // the tip is hidden (NaN early-out).
        UseEffect(() =>
        {
            float y = hoverY.Value;
            _ = controller.Offset.Value;
            _ = controller.MaximumOffset.Value;
            _ = controller.ViewportLength.Value;
            _ = liveRailHeight.Value;
            if (float.IsNaN(y)) return;
            ResolveDetailAt(y);
        });

        void RequestAtRailY(float y, AnnotatedScrollBarScrollKind kind)
        {
            var live = LiveMetrics(controller, liveRailHeight.Value);
            float target = live.ClampScrollOffset(live.RailYToContentOffset(y));
            // 112: annotated-rail pointer decode. f0=railY, i1=kind, i2=(int)target, f1=railHeight.
            if (ScrollTrace.CompiledIn && ScrollTrace.Enabled)
                ScrollTrace.Note(112, y, (int)kind, (int)target, live.RailHeight);
            if (options.Scrolling is null || options.Scrolling(target, kind))
                controller.ScrollTo(target);
        }

        void Request(float target, AnnotatedScrollBarScrollKind kind, bool animate = false)
        {
            target = LiveMetrics(controller, liveRailHeight.Value).ClampScrollOffset(target);
            if (options.Scrolling is null || options.Scrolling(target, kind))
                controller.ScrollTo(target, animate);
        }

        void PointerDown(Point2 p)
        {
            dragging.Value = true;
            exitedWhileDragging.Value = false;
            ResolveDetail(p.Y);
            RequestAtRailY(p.Y, AnnotatedScrollBarScrollKind.Click);
        }

        void Drag(Point2 p)
        {
            dragging.Value = true;
            if (p.Y < 0f || p.Y > railHeight) exitedWhileDragging.Value = true;
            ResolveDetail(p.Y);
            RequestAtRailY(p.Y, AnnotatedScrollBarScrollKind.Drag);
        }

        void Release()
        {
            dragging.Value = false;
            if (!exitedWhileDragging.Value) return;
            exitedWhileDragging.Value = false;
            hoverY.Value = float.NaN;
            detail.SetIfChanged(null);
        }
        void Exit()
        {
            // Engine contract (InputDispatcher.CancelWorkingContact): a captured OnDrag node learns its gesture died
            // through OnPointerExit — capture loss has NO release edge, so keeping `dragging` latched here left the
            // ghost/tip pinned forever after an alt-tab or window-blur mid-drag. Reset unconditionally: a drag that
            // merely strayed off the rail self-heals, because its very next OnDrag sample re-arms `dragging` and
            // re-resolves the detail.
            dragging.Value = false;
            exitedWhileDragging.Value = false;
            hoverY.Value = float.NaN;
            detail.SetIfChanged(null);
        }

        void Wheel(WheelEventArgs e)
        {
            // An annotated rail is commonly a SIBLING of its viewport, not a descendant. Native wheel routing therefore
            // cannot discover the viewport from a hit on this control; without this bridge the rail visibly advertises
            // more range while wheel input over it is silently dropped. Preserve the engine's raw signed DIP delta and
            // route through the same controller seam used by sticky overlays and other external scroll chrome.
            if (!float.IsFinite(e.Delta) || e.Delta == 0f) return;   // leave horizontal-only input available to ancestors
            float target = LiveMetrics(controller, railHeight)
                .ClampScrollOffset(controller.Offset.Peek() + e.Delta);
            if (options.Scrolling is null || options.Scrolling(target, AnnotatedScrollBarScrollKind.Wheel))
            {
                // The ghost/tip deliberately survive the wheel: the pointer has not moved, and the re-resolve effect
                // keeps the tip's date glued to the live mapping as the content scrolls under it. Clearing them here
                // made the flyout vanish mid-gesture and — hover never re-fires on an unchanged node — never return.
                // Animated: the viewport's own wheel path is a WheelAnimating chase with accumulation, and a hard
                // snap here both felt alien next to it and ARRESTED any in-flight fling. (Residual: the rail forwards
                // the raw DIP delta — the notch → max(48, 10%·viewport) scaling lives in the dispatcher's viewport
                // path, which element wheel routing has no seam to reach yet.)
                controller.ScrollBy(e.Delta, animate: true);
            }
            e.Handled = true;
        }

        void OnKey(KeyEventArgs e)
        {
            float offset = controller.Offset.Peek();
            float viewport = MathF.Max(0f, controller.ViewportLength.Peek());
            float small = viewport / 8f; // AnnotatedScrollBar.cpp s_defaultViewportToSmallChangeRatio = 8.
            float target;
            AnnotatedScrollBarScrollKind kind;
            switch (e.KeyCode)
            {
                case Keys.Up:
                case Keys.Left:
                    target = offset - small; kind = AnnotatedScrollBarScrollKind.DecrementButton; break;
                case Keys.Down:
                case Keys.Right:
                    target = offset + small; kind = AnnotatedScrollBarScrollKind.IncrementButton; break;
                case Keys.PageUp:
                    target = offset - viewport; kind = AnnotatedScrollBarScrollKind.DecrementButton; break;
                case Keys.PageDown:
                    target = offset + viewport; kind = AnnotatedScrollBarScrollKind.IncrementButton; break;
                case Keys.Home:
                    target = controller.MinimumOffset.Peek(); kind = AnnotatedScrollBarScrollKind.Click; break;
                case Keys.End:
                    target = controller.MaximumOffset.Peek(); kind = AnnotatedScrollBarScrollKind.Click; break;
                default:
                    return;
            }
            Request(target, kind);
            e.Handled = true;
        }

        // Round-trip through the live scroll-range oracle so the ghost sits on the pointer even after an extent
        // correction (hover Y → offset → thumb top; both legs share Position01).
        Func<Affine2D> ghostTransform = () =>
        {
            var live = LiveMetrics(controller, liveRailHeight.Value);
            return Affine2D.Translation(0f, live.ScrollOffsetToThumbTop(live.RailYToContentOffset(hoverY.Value)));
        };
        Func<float> ghostOpacity = () => float.IsNaN(hoverY.Value) ? 0f : 1f;
        Func<Affine2D> thumbTransform = () => Affine2D.Translation(0f,
            LiveMetrics(controller, liveRailHeight.Value).ScrollOffsetToThumbTop(controller.Offset.Value));
        Func<Affine2D> tipTransform = () =>
        {
            float rh = liveRailHeight.Value;
            float h = tipHeight.Value;
            if (h <= 0f || h > rh * 0.5f) h = AnnotatedScrollBar.TooltipMinHeight;
            float y = float.IsNaN(hoverY.Value) ? 0f : hoverY.Value - h * 0.5f;
            return Affine2D.Translation(0f, Math.Clamp(y, 0f, MathF.Max(0f, rh - h)));
        };
        Func<float> tipOpacity = () =>
            float.IsNaN(hoverY.Value) || detail.Value is null ? 0f : 1f;

        var labelsGrid = new BoxEl
        {
            Key = AnnotatedScrollBar.PartLabels,
            ZStack = true,
            Width = AnnotatedScrollBar.LabelsMinWidth,
            Height = railHeight,
            ClipToBounds = true,
            HitTestVisible = false,
            Children = labels,
        };
        if (options.Parts is { } partsLabels)
            labelsGrid = partsLabels.Apply(AnnotatedScrollBar.PartLabels, labelsGrid) with
            {
                Key = AnnotatedScrollBar.PartLabels,
                Children = labels,
            };

        var ticksGrid = new BoxEl
        {
            Key = AnnotatedScrollBar.PartTicks,
            ZStack = true,
            Width = AnnotatedScrollBar.ThumbWidth,
            Height = railHeight,
            JustifySelf = FlexAlign.End,
            HitTestVisible = false,
            Children = ticks,
        };
        if (options.Parts is { } partsTicks)
            ticksGrid = partsTicks.Apply(AnnotatedScrollBar.PartTicks, ticksGrid) with
            {
                Key = AnnotatedScrollBar.PartTicks,
                Children = ticks,
            };

        var tooltipRail = new BoxEl
        {
            Key = AnnotatedScrollBar.PartRail,
            Direction = 0,
            Height = railHeight,
            Justify = FlexJustify.End,
            HitTestVisible = false,
            Children = [new BoxEl { Width = 1f, Height = railHeight }],
        };
        if (options.Parts is { } partsRail)
            tooltipRail = partsRail.Apply(AnnotatedScrollBar.PartRail, tooltipRail) with
            {
                Key = AnnotatedScrollBar.PartRail,
            };

        var ghost = new BoxEl
        {
            Key = AnnotatedScrollBar.PartGhost,
            Direction = 0,
            Justify = FlexJustify.End,
            Height = AnnotatedScrollBar.ThumbHeight,
            HitTestVisible = false,
            Transform = ghostTransform,
            Opacity = Prop.Of(ghostOpacity),
            Children =
            [
                new BoxEl
                {
                    Width = AnnotatedScrollBar.ThumbWidth,
                    Height = AnnotatedScrollBar.ThumbHeight,
                    Corners = CornerRadius4.All(AnnotatedScrollBar.ThumbRadius),
                    Fill = Tok.AccentDisabled,
                },
            ],
        };
        if (options.Parts is { } partsGhost)
            ghost = partsGhost.Apply(AnnotatedScrollBar.PartGhost, ghost) with
            {
                Key = AnnotatedScrollBar.PartGhost,
                Transform = ghostTransform,
                Opacity = Prop.Of(ghostOpacity),
            };

        var thumb = new BoxEl
        {
            Key = AnnotatedScrollBar.PartThumb,
            Direction = 0,
            Justify = FlexJustify.End,
            Height = AnnotatedScrollBar.ThumbHeight,
            HitTestVisible = false,
            Transform = thumbTransform,
            Children =
            [
                new BoxEl
                {
                    Width = AnnotatedScrollBar.ThumbWidth,
                    Height = AnnotatedScrollBar.ThumbHeight,
                    Corners = CornerRadius4.All(AnnotatedScrollBar.ThumbRadius),
                    Fill = Tok.AccentDefault,
                },
            ],
        };
        if (options.Parts is { } partsThumb)
            thumb = partsThumb.Apply(AnnotatedScrollBar.PartThumb, thumb) with
            {
                Key = AnnotatedScrollBar.PartThumb,
                Transform = thumbTransform,
            };

        Element tipContent;
        if (options.DetailTemplate is { } tmpl)
        {
            tipContent = detail.Value is { } d
                ? tmpl(d)
                : new BoxEl { MinHeight = AnnotatedScrollBar.TooltipMinHeight, MinWidth = 0f };
        }
        else
        {
            tipContent = new TextEl(Prop.Of(() => detail.Value?.Text ?? string.Empty))
            {
                Size = AnnotatedScrollBar.LabelSize,
                Weight = 600,
                Color = Tok.TextPrimary,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            };
        }

        Action<NodeHandle> captureTipInner = h => tipNode.Value = h;
        var tipInner = new BoxEl
        {
            MaxWidth = AnnotatedScrollBar.TooltipMaxWidth,
            MinWidth = 0f,
            Height = AnnotatedScrollBar.TooltipMinHeight,
            Shrink = 0f,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(12f, 6f, 12f, 8f),
            Corners = Radii.ControlAll,
            Fill = Tok.AcrylicFlyout.Fallback,
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Shadow = Elevation.Tooltip,
            OnRealized = captureTipInner,
            Children = [tipContent],
        };
        // Auto width + End alignment: the flag takes its desired width and hangs off the leading edge of the
        // 44px rail (ArrangeZStack desiredW). The rail itself stays Width=44 so hover cannot reflow the list.
        // MeasureUnboundedWidth (A3): report the tooltip's real text width instead of the ~20 DIP the rail's own
        // Width=44 would otherwise squeeze it to, which is what forced CharacterEllipsis down to "M…".
        var tip = new BoxEl
        {
            Key = AnnotatedScrollBar.PartTip,
            Height = AnnotatedScrollBar.TooltipMinHeight,
            Direction = 0,
            JustifySelf = FlexAlign.End,
            MeasureUnboundedWidth = true,
            HitTestVisible = false,
            Transform = tipTransform,
            Opacity = Prop.Of(tipOpacity),
            Children = [tipInner],
        };
        if (options.Parts is { } partsTip)
        {
            var modifiedTip = partsTip.Apply(AnnotatedScrollBar.PartTip, tip);
            tip = modifiedTip with
            {
                Key = AnnotatedScrollBar.PartTip,
                Height = AnnotatedScrollBar.TooltipMinHeight,
                JustifySelf = FlexAlign.End,
                MeasureUnboundedWidth = true,
                Transform = tipTransform,
                Opacity = Prop.Of(tipOpacity),
            };
        }

        // Tip sits before the thumb so LastChild(rail) stays the live-thumb row (cp4.12). Always mounted: hover is
        // an Opacity bind (layout-noop) and cannot change child count or control width.
        Element[] layers = [labelsGrid, ticksGrid, tooltipRail, ghost, tip, thumb];

        UseLayoutEffect(() =>
        {
            var node = tipNode.Value;
            if (Context.Scene is { } scene && !node.IsNull && scene.IsLive(node))
            {
                float h = scene.Bounds(node).H;
                if (h > 0f && h <= railHeight * 0.5f)
                    tipHeight.SetIfChanged(h);
            }
        });

        bool interactive = controller.IsScrollable.Value && metrics.IsScrollable;
        var rail = new BoxEl
        {
            ZStack = true,
            Width = AnnotatedScrollBar.LabelsMinWidth,
            Height = railHeight,
            MinWidth = AnnotatedScrollBar.LabelsMinWidth,
            // Mechanics stay inside Width=44; the detail flag hangs off the leading edge and must not be clipped.
            ClipToBounds = false,
            Cursor = CursorId.Arrow,
            IsEnabled = interactive,
            OnPointerDown = PointerDown,
            OnDrag = Drag,
            OnClick = Release, // OnClick is the captured OnDrag gesture's release edge.
            OnHoverMove = p => ResolveDetail(p.Y),
            OnPointerExit = Exit,
            Children = layers,
        };

        return new BoxEl
        {
            Direction = 1,
            Width = AnnotatedScrollBar.LabelsMinWidth,
            Height = pinHeight ? height : float.NaN,
            AlignSelf = FlexAlign.Stretch,
            Grow = pinHeight ? 0f : 1f,
            Basis = pinHeight ? float.NaN : 0f,
            MinWidth = AnnotatedScrollBar.LabelsMinWidth,
            MinHeight = 0f,
            Shrink = 1f,
            ClipToBounds = false,
            Cursor = CursorId.Arrow,
            Role = AutomationRole.ScrollBar,
            Focusable = interactive,
            TabStop = interactive,
            IsEnabled = interactive,
            OnKeyDown = OnKey,
            OnPointerWheel = interactive ? Wheel : null,
            Children =
            [
                ScrollButton(up: true, interactive
                    ? () => Request(controller.Offset.Peek() - MathF.Max(0f, controller.ViewportLength.Peek()) / 8f,
                        AnnotatedScrollBarScrollKind.DecrementButton)
                    : null),
                rail,
                ScrollButton(up: false, interactive
                    ? () => Request(controller.Offset.Peek() + MathF.Max(0f, controller.ViewportLength.Peek()) / 8f,
                        AnnotatedScrollBarScrollKind.IncrementButton)
                    : null),
            ],
        };
    }

    /// <summary>Live geometry at the moment of the call. Deliberately `.Value` (tracked) reads, not `.Peek()`:
    /// evaluated inside a tracked scope (the thumb/ghost transform binding Effects, the detail re-resolve effect)
    /// they subscribe it — an extent correction alone must re-place the thumb even when the offset never moved
    /// (`.Peek()` there kept the stale denominator until the next offset write). In plain event handlers there is no
    /// tracking scope and a `.Value` read is just the live value.</summary>
    private static RailMetrics LiveMetrics(AnnotatedScrollBarController controller, float railHeight)
        => new(controller.MinimumOffset.Value, controller.MaximumOffset.Value, controller.ViewportLength.Value,
            railHeight, AnnotatedScrollBar.ThumbHeight);

    /// <summary>Content height of a label, never a ZStack-stretched wrapper (those report <paramref name="railHeight"/>).</summary>
    private static float MeasureLabelContent(SceneStore? scene, NodeHandle wrapper, float railHeight)
    {
        float fallback = AnnotatedScrollBar.LabelSize;
        if (scene is null || wrapper.IsNull || !scene.IsLive(wrapper)) return fallback;
        var inner = scene.FirstChild(wrapper);
        var node = !inner.IsNull && scene.IsLive(inner) ? inner : wrapper;
        float h = scene.Bounds(node).H;
        if (h <= 0f || h >= railHeight * 0.5f) return fallback;
        return h;
    }

    /// <summary>Tops and collision visibility are pure math over the CURRENT metrics — computed here, every memo
    /// refresh, so the labels ride the same live mapping as the ticks and thumb. <paramref name="heights"/> is the
    /// debounced measured snapshot (null/short = LabelSize fallback); the collision pass always runs, so a fresh
    /// label set never flashes all-visible.</summary>
    private static Element[] BuildLabels(AnnotatedScrollBarOptions options, RailMetrics metrics,
                                         NodeHandle[] nodes, float[]? heights,
                                         Func<AnnotatedScrollBarLabel, Element> defaultTemplate)
    {
        int count = options.Labels.Count;
        var result = new Element[count];
        var template = options.LabelTemplate ?? defaultTemplate;
        // A4: only the DEFAULT template's -5 top margin bleeds ascenders past y=0 under ClipToBounds=true; a
        // caller-supplied LabelTemplate owns its own margins and must not be shifted on our behalf.
        bool usingDefaultTemplate = options.LabelTemplate is null;
        var measured = new RailLabelContainer[count];
        for (int i = 0; i < count; i++)
        {
            float height = heights is not null && i < heights.Length ? heights[i] : AnnotatedScrollBar.LabelSize;
            float top = metrics.ContentOffsetToLabelTop(options.Labels[i].ScrollOffset, height);
            if (i == 0 && usingDefaultTemplate) top = MathF.Max(top, AnnotatedScrollBar.LabelTopBleed);
            measured[i] = new RailLabelContainer(i, top, height);
        }
        // Labels live in the rail, which is already inset from the control by ButtonCell on each end — that is the
        // legal range. Passing the rail span (not the full control height) keeps first/last clear of the arrows.
        var visible = RailLabelCollision.Collapse(measured, 0f, metrics.RailHeight);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            result[i] = new BoxEl
            {
                Key = "asb-label:" + i,
                Width = AnnotatedScrollBar.LabelsMinWidth,
                Height = measured[i].Height,
                OffsetY = measured[i].Top,
                Direction = 0,
                Justify = FlexJustify.End,
                HitTestVisible = false,
                Opacity = visible[i] ? 1f : 0f,
                OnRealized = h => nodes[index] = h,
                Children = [template(options.Labels[i])],
            };
        }
        return result;
    }

    /// <summary>A11 density cap: hundreds of eagerly-built per-day tick Elements is wasted work once several land
    /// within the same few DIP of rail. Quantizes tick tops into <see cref="AnnotatedScrollBar.MinTickGap"/>-wide
    /// buckets and keeps at most one winner per bucket, always preserving the two endpoints (offsets[0]/[^1]) even
    /// if a middle tick already claimed their bucket — the visible ends of the range must never disappear. Keyed by
    /// BUCKET index (not source index) so the emitted tree stays stable as the offsets identity changes underneath.</summary>
    private static Element[] BuildTicks(IReadOnlyList<float> offsets, RailMetrics metrics)
    {
        int count = offsets.Count;
        if (count == 0) return [];
        int bucketCount = metrics.RailHeight > 0f
            ? Math.Max(1, (int)MathF.Ceiling(metrics.RailHeight / AnnotatedScrollBar.MinTickGap))
            : 1;
        var winner = new int[bucketCount];   // source index of the bucket's chosen tick; -1 = empty
        Array.Fill(winner, -1);

        int BucketOf(float offset)
        {
            int b = (int)(metrics.ContentOffsetToTickTop(offset) / AnnotatedScrollBar.MinTickGap);
            return Math.Clamp(b, 0, bucketCount - 1);
        }

        for (int i = 0; i < count; i++)
        {
            int bucket = BucketOf(offsets[i]);
            if (winner[bucket] < 0) winner[bucket] = i;
        }
        // Endpoints have priority over whatever density-collapse already claimed their bucket.
        winner[BucketOf(offsets[0])] = 0;
        winner[BucketOf(offsets[count - 1])] = count - 1;

        int kept = 0;
        for (int b = 0; b < bucketCount; b++) if (winner[b] >= 0) kept++;
        var result = new Element[kept];
        int w = 0;
        for (int b = 0; b < bucketCount; b++)
        {
            int i = winner[b];
            if (i < 0) continue;
            result[w++] = new BoxEl
            {
                Key = "asb-tick:" + b,
                Width = AnnotatedScrollBar.ThumbWidth,
                Height = AnnotatedScrollBar.ThumbHeight,
                OffsetY = metrics.ContentOffsetToTickTop(offsets[i]),
                Direction = 0,
                Justify = FlexJustify.End,
                JustifySelf = FlexAlign.End,
                HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Width = AnnotatedScrollBar.ThumbHeight,
                        Height = AnnotatedScrollBar.ThumbHeight,
                        Corners = CornerRadius4.All(AnnotatedScrollBar.ThumbRadius),
                        Fill = Tok.TextTertiary,
                    },
                ],
            };
        }
        return result;
    }

    private static Element ScrollButton(bool up, Action? onClick)
        => new BoxEl
        {
            Width = AnnotatedScrollBar.ButtonCell,
            Height = AnnotatedScrollBar.ButtonCell,
            AlignSelf = FlexAlign.End,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Fill = ColorF.Transparent,
            Cursor = CursorId.Arrow,
            Repeats = true,
            TabStop = false,
            OnClick = onClick,
            IsEnabled = onClick is not null,
            Children =
            [
                new TextEl(up ? IconGlyphs.CaretUpSolid8 : IconGlyphs.CaretDownSolid8)
                {
                    Size = AnnotatedScrollBar.ButtonGlyph,
                    FontFamily = Theme.IconFont,
                    Color = onClick is null ? Tok.TextDisabled : Tok.TextPrimary,
                    HoverColor = Tok.TextSecondary,
                    PressedColor = Tok.TextTertiary,
                },
            ],
        };
}
