using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI FlipView: a clipped frame showing exactly one item at a time, with left/right circular
/// navigation chevrons overlaid on the frame edges and a row of dot indicators beneath. The current index lives
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
            // Empty-state guard: an empty clipped frame, no chevrons, no dots.
            return new BoxEl
            {
                Width = Width, Height = Height, Corners = Radii.OverlayAll,
                Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                ClipToBounds = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            };
        }

        int cur = idx % count;
        if (cur < 0) cur += count;

        const float ChevD = 34f;
        const float Inset = 8f;
        float chevY = (Height - ChevD) / 2f;

        // Circular chevron button. In a ZStack children stack at the origin, so we position with OffsetX/OffsetY.
        BoxEl Chevron(string glyph, float offsetX, Action onClick) => new()
        {
            Width = ChevD, Height = ChevD, Corners = Radii.Circle(ChevD),
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            AlignSelf = FlexAlign.Center,
            OffsetX = offsetX, OffsetY = chevY,
            Role = AutomationRole.Button, OnClick = onClick,
            Children = [new TextEl(glyph) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextPrimary }],
        };

        var frame = new BoxEl
        {
            ZStack = true,
            Width = Width, Height = Height, Corners = Radii.OverlayAll,
            Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
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
                Chevron("\uE76B", Inset, () => setIdx((cur - 1 + count) % count)),
                Chevron("\uE76C", Width - ChevD - Inset, () => setIdx((cur + 1) % count)),
            ],
        };

        var dots = new Element[count];
        for (int i = 0; i < count; i++)
        {
            bool selected = i == cur;
            float d = selected ? 6f : 4f;
            dots[i] = new BoxEl
            {
                Width = d, Height = d, Corners = Radii.Circle(d),
                Fill = selected ? Tok.AccentDefault : Tok.FillControlStrong,
                AlignSelf = FlexAlign.Center,
            };
        }

        var dotRow = new BoxEl
        {
            Direction = 0, Gap = 4f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children = dots,
        };

        return new BoxEl
        {
            Direction = 1, Gap = 8f, AlignItems = FlexAlign.Center,
            Children = [frame, dotRow],
        };
    }
}
