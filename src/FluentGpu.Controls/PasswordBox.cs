using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>PasswordBox</c>: a single-line field built on <see cref="EditableText"/> with <c>Mask = true</c>, so the
/// model text is kept verbatim but rendered as dots. Same shape as <see cref="TextBox"/> — header-less it is the bare
/// masked field; with a header it becomes a 4px-gap column of a secondary-text label over the field.
/// </summary>
public static class PasswordBox
{
    public static Element Create(string placeholder = "Password", float width = 280f, string? header = null)
    {
        if (header is null)
            return Embed.Comp(() => new EditableText { Placeholder = placeholder, Width = width, Mask = true });

        return new BoxEl
        {
            Direction = 1,
            Gap = 4f,
            Children = new Element[]
            {
                new TextEl(header) { Size = 14f, Color = Tok.TextSecondary },
                Embed.Comp(() => new EditableText { Placeholder = placeholder, Width = width, Mask = true }),
            },
        };
    }
}
