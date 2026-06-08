using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A horizontal slider (seek/volume). WinUI 3 geometry: a 4px rail (ControlStrongFillColorDefault, radius 2) with an
/// accent value-fill, and an 18px thumb — a solid ring (ControlSolidFillColorDefault) + 1px elevation border + a 12px
/// accent inner dot — that grows on hover / shrinks on press. <paramref name="value"/> is 0..1; press/drag reports the
/// new value (track-relative). Controlled: the caller owns the value.
/// </summary>
public static partial class Slider
{
    public sealed record Style
    {
        public float TrackHeight { get; init; } = 4f;                    // SliderTrackThemeHeight
        public float TrackCornerRadius { get; init; } = 2f;             // SliderTrackCornerRadius
        public ColorF RailFill { get; init; }                          // unfilled rail (ControlStrongFillColorDefault)
        public ColorF ValueFill { get; init; }                         // filled portion (AccentFillColorDefault)
        public float ThumbDiameter { get; init; } = 18f;               // SliderHorizontalThumbWidth/Height
        public float InnerThumbDiameter { get; init; } = 12f;          // SliderInnerThumbWidth/Height (rest)
        public ColorF ThumbRing { get; init; }                         // outer ring (ControlSolidFillColorDefault)
        public ColorF ThumbFill { get; init; }                         // inner accent dot
        public GradientSpec? ThumbBorder { get; init; }                // ControlElevationBorderBrush
        public float ThumbBorderWidth { get; init; } = 1f;
        public float ThumbCornerRadius { get; init; } = 10f;           // SliderThumbCornerRadius (pill, not circle)
        public float ThumbHoverScale { get; init; } = 1.167f;          // 14/12 inner-thumb grow on hover (Slider_themeresources.xaml:222)
        public float ThumbPressScale { get; init; } = 0.71f;           // 8.5/12 inner-thumb shrink on press (line 234)
        public float ThumbDisabledScale { get; init; } = 1.167f;       // disabled inner-thumb scale (line 246; same as hover)
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        RailFill = Tok.FillControlStrong,
        ValueFill = Tok.AccentDefault,
        ThumbRing = Tok.FillControlSolid,
        ThumbFill = Tok.AccentDefault,
        ThumbBorder = Tok.ControlElevationBorder,
    };

    /// <summary>Extended slider configuration: a value range, optional step snapping, tick marks, and orientation.</summary>
    public sealed record Options
    {
        public float Min { get; init; } = 0f;
        public float Max { get; init; } = 1f;
        public float Step { get; init; }            // 0 = continuous
        public float TickFrequency { get; init; }   // 0 = no ticks
        public bool Vertical { get; init; }
    }

    /// <summary>
    /// A slider over an arbitrary <see cref="Options.Min"/>..<see cref="Options.Max"/> range, with optional step snapping,
    /// tick marks, and vertical orientation. <paramref name="value"/> is in range units; <paramref name="length"/> is the
    /// track length (px) and <paramref name="thickness"/> the cross size. The 0..1 <see cref="Create(float,Action{float},float,float,Style?)"/>
    /// and <see cref="Bind"/> overloads are unchanged.
    /// </summary>
    public static BoxEl Ranged(float value, Action<float> onChange, Options o, float length = 220f, float thickness = 32f, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        float range = MathF.Max(o.Max - o.Min, 1e-5f);
        float t = Math.Clamp((value - o.Min) / range, 0f, 1f);
        float thumbD = s.ThumbDiameter;

        void Set(Point2 p)
        {
            float raw = Math.Clamp(o.Vertical ? 1f - p.Y / MathF.Max(length, 1f) : p.X / MathF.Max(length, 1f), 0f, 1f);
            float v = o.Min + raw * range;
            if (o.Step > 0f) v = o.Min + MathF.Round((v - o.Min) / o.Step) * o.Step;
            onChange(Math.Clamp(v, o.Min, o.Max));
        }

        Element Ticks(bool vertical)
        {
            if (o.TickFrequency <= 0f) return new BoxEl { Height = 0f };
            var marks = new List<Element>();
            for (float tv = o.Min; tv <= o.Max + o.TickFrequency * 0.01f; tv += o.TickFrequency)
            {
                float tt = Math.Clamp((tv - o.Min) / range, 0f, 1f);
                marks.Add(vertical
                    ? new BoxEl { Width = 6f, Height = 1f, Fill = Tok.FillControlStrong, OffsetY = (1f - tt) * length }
                    : new BoxEl { Width = 1f, Height = 6f, Fill = Tok.FillControlStrong, OffsetX = tt * length });
            }
            return vertical
                ? new BoxEl { ZStack = true, Width = 6f, Height = length, Children = marks.ToArray() }
                : new BoxEl { ZStack = true, Width = length, Height = 6f, Children = marks.ToArray() };
        }

        if (o.Vertical)
        {
            var track = new BoxEl
            {
                Width = thickness, Height = length, ZStack = true, Role = AutomationRole.Slider,
                OnPointerDown = Set, OnDrag = Set,
                Children =
                [
                    new BoxEl { Width = s.TrackHeight, Height = length, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = s.RailFill, OffsetX = (thickness - s.TrackHeight) * 0.5f },
                    new BoxEl { Width = s.TrackHeight, Height = t * length, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = s.ValueFill, OffsetX = (thickness - s.TrackHeight) * 0.5f, OffsetY = (1f - t) * length },
                    new BoxEl
                    {
                        Width = thumbD, Height = thumbD, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = CornerRadius4.All(s.ThumbCornerRadius), Fill = s.ThumbRing, BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
                        HoverScale = s.ThumbHoverScale, PressScale = s.ThumbPressScale,
                        OffsetX = (thickness - thumbD) * 0.5f, OffsetY = Math.Clamp((1f - t) * length - thumbD * 0.5f, 0f, length - thumbD),
                        Children = [new BoxEl { Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter, Corners = Radii.Circle(s.InnerThumbDiameter), Fill = s.ThumbFill }],
                    },
                ],
            };
            return new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Children = o.TickFrequency > 0f ? [track, Ticks(true)] : [track] };
        }

        var hTrack = new BoxEl
        {
            Width = length, Height = thickness, ZStack = true, Role = AutomationRole.Slider,
            OnPointerDown = Set, OnDrag = Set,
            Children =
            [
                new BoxEl { Width = length, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = s.RailFill, OffsetY = (thickness - s.TrackHeight) * 0.5f },
                new BoxEl { Width = t * length, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = s.ValueFill, OffsetY = (thickness - s.TrackHeight) * 0.5f },
                new BoxEl
                {
                    Direction = 0, Width = length, Height = thickness, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Width = MathF.Max(0f, t * length - thumbD * 0.5f) },
                        new BoxEl
                        {
                            Width = thumbD, Height = thumbD, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Corners = CornerRadius4.All(s.ThumbCornerRadius), Fill = s.ThumbRing, BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
                            HoverScale = s.ThumbHoverScale, PressScale = s.ThumbPressScale,
                            Children = [new BoxEl { Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter, Corners = Radii.Circle(s.InnerThumbDiameter), Fill = s.ThumbFill }],
                        },
                    ],
                },
            ],
        };
        return new BoxEl { Direction = 1, Gap = 4f, Children = o.TickFrequency > 0f ? [hTrack, Ticks(false)] : [hTrack] };
    }

    public static BoxEl Create(float value, Action<float> onChange, float width = 200f, float height = 24f, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        float v = Math.Clamp(value, 0f, 1f);
        float thumbD = s.ThumbDiameter;
        void Set(Point2 p) => onChange(Math.Clamp(p.X / MathF.Max(width, 1f), 0f, 1f));   // track-relative (handlers on the outer box)

        return new BoxEl
        {
            Width = width, Height = height, ZStack = true, Role = AutomationRole.Slider,
            OnPointerDown = Set, OnDrag = Set,   // press-to-seek + drag-to-scrub, anywhere on the track
            Children =
            [
                // rail + value fill, vertically centred (composited OffsetY; non-interactive so the hit rect is irrelevant)
                new BoxEl
                {
                    ZStack = true, Width = width, Height = height,
                    Children =
                    [
                        new BoxEl { Width = width, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = s.RailFill, OffsetY = (height - s.TrackHeight) * 0.5f },
                        new BoxEl { Width = v * width, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = s.ValueFill, OffsetY = (height - s.TrackHeight) * 0.5f },
                    ],
                },
                // thumb, layout-positioned by a leading spacer (so it tracks the value) + vertically centred by the row.
                // It is NOT interactive (drag stays on the track), but it grows from the track's eased hover/press.
                new BoxEl
                {
                    Direction = 0, Width = width, Height = height, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Width = MathF.Max(0f, v * width - thumbD * 0.5f) },   // leading spacer → thumb at the value
                        new BoxEl
                        {
                            Width = thumbD, Height = thumbD,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Corners = CornerRadius4.All(s.ThumbCornerRadius), Fill = s.ThumbRing,
                            BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
                            HoverScale = s.ThumbHoverScale, PressScale = s.ThumbPressScale,
                            Children = [new BoxEl { Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter, Corners = Radii.Circle(s.InnerThumbDiameter), Fill = s.ThumbFill }],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>
    /// Signals-native slider (the "even better than React" path): the value is a <see cref="FloatSignal"/> bound straight
    /// to the value-fill's composited ScaleX and the thumb's composited OffsetX. A drag writes the signal, which updates
    /// exactly those two node transforms on the compositor fast path — <b>zero render, zero reconcile, zero relayout</b>
    /// per pointer-move (the slider-tank fix). <paramref name="onChange"/> is invoked for side effects (e.g. a value
    /// readout that takes its own scoped re-render); pass null for a purely-visual scrub.
    /// </summary>
    public static BoxEl Bind(FloatSignal value, Action<float>? onChange = null, float width = 200f, float height = 24f, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        float thumbD = s.ThumbDiameter;
        void Set(Point2 p) { float v = Math.Clamp(p.X / MathF.Max(width, 1f), 0f, 1f); value.Value = v; onChange?.Invoke(v); }

        return new BoxEl
        {
            Width = width, Height = height, ZStack = true, Role = AutomationRole.Slider,
            OnPointerDown = Set, OnDrag = Set,
            Children =
            [
                // rail + value fill (full width, grown from the left by a composited ScaleX bound to the signal — no layout)
                new BoxEl
                {
                    ZStack = true, Width = width, Height = height,
                    Children =
                    [
                        new BoxEl { Width = width, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = s.RailFill, OffsetY = (height - s.TrackHeight) * 0.5f },
                        new BoxEl
                        {
                            Width = width, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = s.ValueFill, OffsetY = (height - s.TrackHeight) * 0.5f,
                            TransformBind = () =>
                            {
                                float v = MathF.Max(Math.Clamp(value.Value, 0f, 1f), 1e-4f);
                                return Affine2D.Translation(-width * (1f - v) * 0.5f, 0f).Multiply(Affine2D.Scale(v, 1f));   // grow from the left
                            },
                        },
                    ],
                },
                // thumb at x=0, slid to the value by a composited OffsetX bound to the signal (no leading spacer, no layout)
                new BoxEl
                {
                    Direction = 0, Width = width, Height = height, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = thumbD, Height = thumbD,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Corners = CornerRadius4.All(s.ThumbCornerRadius), Fill = s.ThumbRing,
                            BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
                            HoverScale = s.ThumbHoverScale, PressScale = s.ThumbPressScale,
                            TransformBind = () => Affine2D.Translation(Math.Clamp(value.Value * width - thumbD * 0.5f, 0f, MathF.Max(0f, width - thumbD)), 0f),
                            Children = [new BoxEl { Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter, Corners = Radii.Circle(s.InnerThumbDiameter), Fill = s.ThumbFill }],
                        },
                    ],
                },
            ],
        };
    }
}
