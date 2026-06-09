using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>TextBox</c>: a single-line text field built on <see cref="EditableText"/>, optionally with a header label
/// stacked above it. Header-less it is just the bare field; with a header it becomes an 8px-gap column (WinUI
/// TextBoxTopHeaderMargin = 0,0,0,8) of a secondary-text label over the field. EditableText already supplies the bordered
/// surface (fill, gradient ControlElevationBorder, focus affordance, 4px corners) — TextBox wraps no second border.
/// </summary>
public static class TextBox
{
    // WinUI TextControlThemeMinWidth = 64; the field never narrows below this even if a caller passes a smaller width.
    private const float MinWidth = 64f;
    // WinUI TextBoxTopHeaderMargin = 0,0,0,8 (the vertical gap between the header label and the field).
    private const float HeaderMargin = 8f;

    public static Element Create(string placeholder = "", float width = 280f, string? header = null)
    {
        float w = Math.Max(width, MinWidth);

        // ShowDeleteButton: the WinUI TextBox DeleteButton (✕ while focused ∧ non-empty) — TextBox is the one composer
        // that turns it on (PasswordBox/NumberBox put their reveal/spin affixes in that lane instead).
        if (header is null)
            return Embed.Comp(() => new EditableText { Placeholder = placeholder, Width = w, ShowDeleteButton = true });

        return new BoxEl
        {
            Direction = 1,
            Gap = HeaderMargin,
            Children = new Element[]
            {
                new TextEl(header) { Size = 14f, Color = Tok.TextSecondary },
                Embed.Comp(() => new EditableText { Placeholder = placeholder, Width = w, ShowDeleteButton = true }),
            },
        };
    }
}
