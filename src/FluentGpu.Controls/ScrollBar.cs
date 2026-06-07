using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A vertical scrollbar control (draggable thumb). WinUI 3 metrics: 8px collapsed thumb, 30px min length, radius 3,
/// thumb = ControlStrongFillColorDefault (no hover recolour — it grows instead). <paramref name="fraction"/> =
/// viewport/content (thumb size), <paramref name="position"/> = offset/(content−viewport) in 0..1; drag reports the new
/// position. (The auto-hiding overlay scrollbar on virtualized viewports is separate — see ScrollAnimator/EmitScrollbar.)
/// </summary>
public static partial class ScrollBar
{
    public sealed record Style
    {
        public float ThumbWidth { get; init; } = 8f;                 // ScrollBarVerticalThumbMinWidth (collapsed)
        public float MinThumb { get; init; } = 30f;                  // ScrollBarVerticalThumbMinHeight
        public float CornerRadius { get; init; } = 3f;               // ScrollBarCornerRadius
        public ColorF Thumb { get; init; }                          // ControlStrongFillColorDefault
        public float ThumbHoverScale { get; init; } = 1.15f;
        public float ThumbPressScale { get; init; } = 1.15f;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Thumb = Tok.FillControlStrong,
    };

    public static BoxEl Create(float fraction, float position, Action<float> onScroll, float height = 200f, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        fraction = Math.Clamp(fraction, 0.05f, 1f);
        position = Math.Clamp(position, 0f, 1f);
        float thumbH = MathF.Max(s.MinThumb, fraction * height);
        float travel = MathF.Max(1f, height - thumbH);
        void Set(Point2 p) => onScroll(Math.Clamp((p.Y - thumbH * 0.5f) / travel, 0f, 1f));
        return new BoxEl
        {
            Width = s.ThumbWidth, Height = height, Direction = 1, Role = AutomationRole.ScrollBar,
            OnPointerDown = Set, OnDrag = Set,
            Children =
            [
                new BoxEl { Height = position * travel },   // spacer above the thumb
                new BoxEl
                {
                    Width = s.ThumbWidth, Height = thumbH, Corners = CornerRadius4.All(s.CornerRadius), Fill = s.Thumb,
                    HoverScale = s.ThumbHoverScale, PressScale = s.ThumbPressScale,
                },
            ],
        };
    }
}
