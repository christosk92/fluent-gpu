using FluentGpu.Foundation;
using FluentGpu.Dsl;

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
        public float ThumbHoverScale { get; init; } = 1.16f;           // ~14/12 grow on hover
        public float ThumbPressScale { get; init; } = 0.92f;           // ~ shrink on press
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
                            Corners = Radii.Circle(thumbD), Fill = s.ThumbRing,
                            BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
                            HoverScale = s.ThumbHoverScale, PressScale = s.ThumbPressScale,
                            Children = [new BoxEl { Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter, Corners = Radii.Circle(s.InnerThumbDiameter), Fill = s.ThumbFill }],
                        },
                    ],
                },
            ],
        };
    }
}
