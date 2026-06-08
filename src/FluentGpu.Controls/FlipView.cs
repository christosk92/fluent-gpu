using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI FlipView: a clipped frame showing exactly one item at a time, with left/right rounded-rect
/// navigation bars hugging the frame edges (no dot/pip indicator — WinUI FlipView has none). The current index lives
/// in <see cref="UseState"/>; navigation wraps around. <see cref="Items"/> renders each entry's string centered.</summary>
public sealed class FlipView : Component
{
    public IReadOnlyList<string> Items = [];
    public float Width = 400f;
    public float Height = 240f;

    public static Element Create(IReadOnlyList<string> items, float width = 400f, float height = 240f)
        => Embed.Comp(() => new FlipView { Items = items, Width = width, Height = height });

    public override Element Render()
    {
        var (idx, setIdx) = UseState(0);

        int count = Items.Count;
        if (count == 0)
        {
            // Empty-state guard: an empty clipped frame, no nav bars. Solid background, no resting border.
            return new BoxEl
            {
                Width = Width, Height = Height, Corners = Radii.OverlayAll,
                Fill = Tok.FillSolidBase,        // FlipViewBackground = SolidBackgroundFillColorBase
                ClipToBounds = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            };
        }

        int cur = idx % count;
        if (cur < 0) cur += count;

        // WinUI FlipView nav buttons: thin rounded-rect bars (16w \u00D7 38h) hugging the left/right edges.
        const float BarW = 16f;
        const float BarH = 38f;
        float barY = (Height - BarH) / 2f;

        // In a ZStack children stack at the origin, so we position with OffsetX/OffsetY.
        // WinUI FlipView nav button: a single AcrylicInApp fill across ALL states (no hover/pressed ramp) \u2014 mapped to
        // FillControlDefault. The arrow recolours instead: ControlStrong at rest \u2192 TextSecondary on hover/press. Press
        // also scales the button to 0.875 (FlipViewButtonScalePressed).
        BoxEl NavBar(string glyph, float offsetX, Action onClick) => new()
        {
            Width = BarW, Height = BarH, Corners = Radii.ControlAll,
            Fill = Tok.FillControlDefault,
            PressScale = 0.875f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            AlignSelf = FlexAlign.Start,
            OffsetX = offsetX, OffsetY = barY,
            Role = AutomationRole.Button, OnClick = onClick,
            Children = [new TextEl(glyph) { Size = 8f, FontFamily = Theme.IconFont, Color = Tok.FillControlStrong }],
        };

        return new BoxEl
        {
            ZStack = true,
            Width = Width, Height = Height, Corners = Radii.OverlayAll,
            Fill = Tok.FillSolidBase,        // FlipViewBackground = SolidBackgroundFillColorBase
            ClipToBounds = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                // Content layer fills the stack and centers the current item's label.
                new BoxEl
                {
                    Width = Width, Height = Height,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(Items[cur]) { Size = 20f, Color = Tok.TextPrimary }],
                },
                // Previous (left) = \uEDDA, Next (right) = \uEDD9 (WinUI HorizontalPrevious/Next templates).
                NavBar("\uEDDA", 0f, () => setIdx((cur - 1 + count) % count)),
                NavBar("\uEDD9", Width - BarW, () => setIdx((cur + 1) % count)),
            ],
        };
    }
}
