using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>A WinUI AppBarToggleButton: like <see cref="AppBarButton"/> (a vertically-stacked 16px icon glyph
/// over a 12px label) but a stateful toggle. When checked it paints an accent-subtle fill and the icon/label
/// switch to the accent color; unchecked it is transparent with the standard subtle hover/press states.
/// Used inside a CommandBar; the whole control is one click target that flips the checked state.</summary>
public sealed class AppBarToggleButton : Component
{
    public string Glyph = "";
    public string Label = "";
    public bool InitialChecked = false;

    public static Element Create(string glyph, string label, bool initiallyChecked = false) =>
        Embed.Comp(() => new AppBarToggleButton { Glyph = glyph, Label = label, InitialChecked = initiallyChecked });

    public override Element Render()
    {
        var (on, setOn) = UseState(InitialChecked);

        return new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Gap = 4,
            MinWidth = 48,
            MinHeight = 48,
            Padding = new Edges4(4, 6, 4, 6),
            Corners = Radii.ControlAll,
            Fill = on ? Tok.AccentSubtle : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => setOn(!on),
            Role = AutomationRole.ToggleButton,
            Children =
            [
                new TextEl(Glyph) { Size = 16, Color = on ? Tok.AccentDefault : Tok.TextPrimary, FontFamily = Theme.IconFont },
                new TextEl(Label) { Size = 12, Color = on ? Tok.AccentDefault : Tok.TextPrimary },
            ],
        };
    }
}
