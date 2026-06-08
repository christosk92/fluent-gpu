using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>TextBox</c>: a single-line text field built on <see cref="EditableText"/>, optionally with a header label
/// stacked above it. Header-less it is just the bare field; with a header it becomes a 4px-gap column of a secondary-text
/// label over the field.
/// </summary>
public static class TextBox
{
    public static Element Create(string placeholder = "", float width = 280f, string? header = null)
    {
        if (header is null)
            return Embed.Comp(() => new EditableText { Placeholder = placeholder, Width = width });

        return new BoxEl
        {
            Direction = 1,
            Gap = 4f,
            Children = new Element[]
            {
                new TextEl(header) { Size = 14f, Color = Tok.TextSecondary },
                Embed.Comp(() => new EditableText { Placeholder = placeholder, Width = width }),
            },
        };
    }
}
