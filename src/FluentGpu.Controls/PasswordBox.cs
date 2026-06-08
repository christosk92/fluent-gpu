using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>PasswordBox</c>: a single-line field built on <see cref="EditableText"/> with <c>Mask = true</c>, so the
/// model text is kept verbatim but rendered as dots. Inherits the EditableText focus affordance (1px border + a 2px
/// accent bar pinned to the bottom edge). A reveal ("eye") button sits flush against the right edge. Same outer shape as
/// <see cref="TextBox"/> — header-less it is the bare masked field; with a header a 4px-gap column of a label over it.
/// </summary>
public static class PasswordBox
{
    // The reveal "eye" cell. WinUI RevealButton: MinWidth=34, VerticalAlignment=Stretch (fills the field's inner height),
    // glyph FontSize=PasswordBoxIconFontSize=12. Subtle hover/press fills. Non-functional toggle for now (renders + hovers).
    private static BoxEl RevealButton() => new()
    {
        Width = 34f,
        AlignSelf = FlexAlign.Stretch,   // WinUI VerticalAlignment=Stretch — the button is the full field height
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button,
        OnClick = () => { },   // reveal toggle is a v1 stub; the cell still renders + hovers
        Children = [new TextEl(Icons.RevealPassword) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
    };

    private static Element Field(string placeholder, float width)
        => Embed.Comp(() => new EditableText
        {
            Placeholder = placeholder, Width = width, Mask = true, RightAffix = RevealButton(),
        });

    public static Element Create(string placeholder = "Password", float width = 280f, string? header = null)
    {
        if (header is null)
            return Field(placeholder, width);

        return new BoxEl
        {
            Direction = 1,
            Gap = 4f,
            Children = new Element[]
            {
                new TextEl(header) { Size = 14f, Color = Tok.TextSecondary },
                Field(placeholder, width),
            },
        };
    }
}
