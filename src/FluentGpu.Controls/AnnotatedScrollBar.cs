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
    public float Height { get; init; } = 280f;
    public TemplateParts? Parts { get; init; }
}

/// <summary>
/// The single geometry oracle for an annotated rail. Every rail position is normalized over the scrollable offset
/// range; labels/ticks beyond the last legal viewport offset clamp to the trailing edge. Element top positions then use
/// their own available travel, so a pointer, ghost, thumb, tick, and label all describe the same offset.
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
    public float ContentSpan => ScrollRange + ViewportLength;
    public float ThumbTravel => MathF.Max(0f, RailHeight - ThumbHeight);
    public bool IsScrollable => ScrollRange > 0f && RailHeight > 0f;

    public float ClampScrollOffset(float offset)
        => Math.Clamp(Finite(offset, MinimumOffset), MinimumOffset, MaximumOffset);

    /// <summary>Maps a content/scroll offset onto the full rail, clamped to the legal scroll range.</summary>
    public float ContentOffsetToRailY(float offset)
    {
        return Position01(offset) * RailHeight;
    }

    /// <summary>Inverse of <see cref="ContentOffsetToRailY"/> over the legal scroll range.</summary>
    public float RailYToContentOffset(float railY)
    {
        if (RailHeight <= 0f || ScrollRange <= 0f) return MinimumOffset;
        float p = Math.Clamp(Finite(railY, 0f) / RailHeight, 0f, 1f);
        return MinimumOffset + p * ScrollRange;
    }

    public float ScrollOffsetToThumbTop(float offset)
    {
        return Position01(offset) * ThumbTravel;
    }

    public float ContentOffsetToTickTop(float offset)
        => Position01(offset) * ThumbTravel;

    public float ContentOffsetToLabelTop(float offset, float labelHeight)
    {
        labelHeight = Math.Clamp(Finite(labelHeight, 0f), 0f, RailHeight);
        return Position01(offset) * MathF.Max(0f, RailHeight - labelHeight);
    }

    private float Position01(float offset)
        => ScrollRange <= 0f ? 0f
            : (ClampScrollOffset(offset) - MinimumOffset) / ScrollRange;

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
    private sealed record LabelLayoutSnapshot(object Source, float RailHeight, float[] Tops, float[] Heights, bool[] Visible);

    public override Element Render()
    {
        var props = UseProps<AnnotatedScrollBar.Props>();
        var controller = props.Controller;
        var options = props.Options;
        var bounds = UseMeasuredBounds();
        float measuredHeight = bounds.Value.H;
        float height = float.IsFinite(options.Height) && options.Height > 0f
            ? options.Height
            : measuredHeight > 0f ? measuredHeight : 280f;
        float railHeight = MathF.Max(0f, height - 2f * AnnotatedScrollBar.ButtonCell);
        // Range/viewport changes are layout events and re-render the rail. Offset is deliberately read only by the
        // thumb binding and event handlers, so ordinary scrolling remains compositor-only.
        float minimumOffset = controller.MinimumOffset.Value;
        float maximumOffset = controller.MaximumOffset.Value;
        float viewportLength = controller.ViewportLength.Value;
        var metrics = new RailMetrics(minimumOffset, maximumOffset, viewportLength,
            railHeight, AnnotatedScrollBar.ThumbHeight);

        var hoverY = UseFloatSignal(float.NaN);
        var dragging = UseRef(false);
        var exitedWhileDragging = UseRef(false);
        var detail = UseSignal<AnnotatedScrollBarLabel?>(null);
        var tipHeight = UseFloatSignal(AnnotatedScrollBar.TooltipMinHeight);
        var tipNode = UseRef(NodeHandle.Null);
        var labelNodes = UseMemo(() => new NodeHandle[options.Labels.Count],
            DepKey.Combine(DepKey.FromRef(options.Labels), DepKey.From(options.Labels.Count)));
        var labelLayout = UseSignal<LabelLayoutSnapshot?>(null);

        void LayoutLabels()
        {
            int count = options.Labels.Count;
            var tops = new float[count];
            var heights = new float[count];
            var measured = new RailLabelContainer[count];
            var scene = Context.Scene;
            for (int i = 0; i < count; i++)
            {
                float labelHeight = MeasureLabelContent(scene, i < labelNodes.Length ? labelNodes[i] : NodeHandle.Null,
                    railHeight);
                float top = metrics.ContentOffsetToLabelTop(options.Labels[i].ScrollOffset, labelHeight);
                tops[i] = top;
                heights[i] = labelHeight;
                measured[i] = new RailLabelContainer(i, top, labelHeight);
            }
            // Labels live in the rail, which is already inset from the control by ButtonCell on each end — that is the
            // legal range. Passing the rail span (not the full control height) keeps first/last clear of the arrows.
            var visible = RailLabelCollision.Collapse(measured, 0f, railHeight);
            labelLayout.Value = new LabelLayoutSnapshot(options.Labels, railHeight, tops, heights, visible);
        }

        var labelDeps = DepKey.Combine(
            DepKey.Combine(DepKey.FromRef(options.Labels, options.LabelTemplate), DepKey.From(options.Labels.Count)),
            DepKey.From(metrics.MinimumOffset, metrics.MaximumOffset, metrics.ViewportLength, 0f));
        UseTimeout(LayoutLabels, AnnotatedScrollBar.ContentLayoutDebounceMs, labelDeps);
        UseTimeout(LayoutLabels, AnnotatedScrollBar.SizeLayoutDebounceMs, DepKey.From(railHeight));

        var currentLayout = labelLayout.Value;
        // Keep the last collision snapshot while a replacement Labels array is debounced. Never fall back to
        // all-visible — Wavee rebuilds the array every render, and that flash is the regression.
        bool hasLayout = currentLayout is not null
            && currentLayout.Tops.Length == options.Labels.Count;

        Element DefaultLabel(AnnotatedScrollBarLabel label) => new TextEl(label.Text ?? string.Empty)
        {
            Size = AnnotatedScrollBar.LabelSize,
            Color = Tok.TextPrimary,
            Margin = new Edges4(0f, -5f, 0f, -2f), // LabelTemplate margin 0,-5,0,-2.
        };

        var labels = UseMemo(() => BuildLabels(options, metrics, labelNodes, currentLayout, hasLayout, DefaultLabel),
            DepKey.Combine(
                DepKey.Combine(DepKey.FromRef(options.Labels, options.LabelTemplate), DepKey.From(options.Labels.Count)),
                DepKey.Combine(DepKey.FromRef(currentLayout), DepKey.From(railHeight))));
        var ticks = UseMemo(() => BuildTicks(options.TickOffsets, metrics),
            DepKey.Combine(DepKey.Combine(DepKey.FromRef(options.TickOffsets), DepKey.From(options.TickOffsets.Count)),
                DepKey.From(metrics.MinimumOffset, metrics.MaximumOffset, metrics.ViewportLength, railHeight)));

        void ResolveDetail(float y)
        {
            hoverY.Value = y;
            var resolve = options.DetailLabelAtOffset;
            var next = resolve?.Invoke(metrics.RailYToContentOffset(y));
            detail.SetIfChanged(next);
        }

        void RequestAtRailY(float y, AnnotatedScrollBarScrollKind kind)
        {
            float target = metrics.ClampScrollOffset(metrics.RailYToContentOffset(y));
            if (options.Scrolling is null || options.Scrolling(target, kind))
                controller.ScrollTo(target);
        }

        void Request(float target, AnnotatedScrollBarScrollKind kind, bool animate = false)
        {
            target = metrics.ClampScrollOffset(target);
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
            if (dragging.Value)
            {
                exitedWhileDragging.Value = true;
                return;
            }
            hoverY.Value = float.NaN;
            detail.SetIfChanged(null);
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

        Func<Affine2D> ghostTransform = () => Affine2D.Translation(0f,
            LiveMetrics(controller, railHeight).ScrollOffsetToThumbTop(
                LiveMetrics(controller, railHeight).RailYToContentOffset(hoverY.Value)));
        Func<float> ghostOpacity = () => float.IsNaN(hoverY.Value) ? 0f : 1f;
        Func<Affine2D> thumbTransform = () => Affine2D.Translation(0f,
            LiveMetrics(controller, railHeight).ScrollOffsetToThumbTop(controller.Offset.Value));
        Func<Affine2D> tipTransform = () =>
        {
            float h = tipHeight.Value;
            if (h <= 0f || h > railHeight * 0.5f) h = AnnotatedScrollBar.TooltipMinHeight;
            float y = float.IsNaN(hoverY.Value) ? 0f : hoverY.Value - h * 0.5f;
            return Affine2D.Translation(0f, Math.Clamp(y, 0f, MathF.Max(0f, railHeight - h)));
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
        var tip = new BoxEl
        {
            Key = AnnotatedScrollBar.PartTip,
            Height = AnnotatedScrollBar.TooltipMinHeight,
            Direction = 0,
            JustifySelf = FlexAlign.End,
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
            Height = height,
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

    private static RailMetrics LiveMetrics(AnnotatedScrollBarController controller, float railHeight)
        => new(controller.MinimumOffset.Peek(), controller.MaximumOffset.Peek(), controller.ViewportLength.Peek(),
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

    private static Element[] BuildLabels(AnnotatedScrollBarOptions options, RailMetrics metrics,
                                         NodeHandle[] nodes, LabelLayoutSnapshot? layout, bool hasLayout,
                                         Func<AnnotatedScrollBarLabel, Element> defaultTemplate)
    {
        var result = new Element[options.Labels.Count];
        var template = options.LabelTemplate ?? defaultTemplate;
        bool[]? seedVisible = null;
        for (int i = 0; i < result.Length; i++)
        {
            int index = i;
            var label = options.Labels[i];
            float height = hasLayout ? layout!.Heights[i] : AnnotatedScrollBar.LabelSize;
            float top = hasLayout ? layout!.Tops[i]
                : metrics.ContentOffsetToLabelTop(label.ScrollOffset, height);
            bool visible;
            if (hasLayout) visible = layout!.Visible[i];
            else
            {
                // First paint (and a count-changing replacement before debounce): collapse with LabelSize so we
                // never flash every label visible. A same-count replacement keeps the previous snapshot instead.
                if (seedVisible is null)
                {
                    seedVisible = new bool[result.Length];
                    var seed = new RailLabelContainer[result.Length];
                    for (int s = 0; s < result.Length; s++)
                    {
                        float sh = AnnotatedScrollBar.LabelSize;
                        seed[s] = new RailLabelContainer(s,
                            metrics.ContentOffsetToLabelTop(options.Labels[s].ScrollOffset, sh), sh);
                    }
                    seedVisible = RailLabelCollision.Collapse(seed, 0f, metrics.RailHeight);
                }
                visible = seedVisible[i];
            }
            result[i] = new BoxEl
            {
                Key = "asb-label:" + i,
                Width = AnnotatedScrollBar.LabelsMinWidth,
                Height = height,
                OffsetY = top,
                Direction = 0,
                Justify = FlexJustify.End,
                HitTestVisible = false,
                Opacity = visible ? 1f : 0f,
                OnRealized = h => nodes[index] = h,
                Children = [template(label)],
            };
        }
        return result;
    }

    private static Element[] BuildTicks(IReadOnlyList<float> offsets, RailMetrics metrics)
    {
        var result = new Element[offsets.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new BoxEl
            {
                Key = "asb-tick:" + i,
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
