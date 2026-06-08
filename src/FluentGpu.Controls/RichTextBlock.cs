using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>RichTextBlock</c> equivalent: read-only, multi-paragraph text laid out as a
/// vertical column of body paragraphs. Display only — no input or selection. The engine has
/// no inline-run model, so each paragraph is a single <see cref="TextEl"/>; paragraph breaks
/// come from the column gap. <see cref="Article"/> prepends a bold heading for a titled block.
/// </summary>
public static class RichTextBlock
{
    /// <summary>A column of body paragraphs (14px, primary text), 10px between paragraphs, capped at 560px wide.</summary>
    public static BoxEl Create(IReadOnlyList<string> paragraphs)
    {
        var children = new List<Element>(paragraphs.Count);
        for (int i = 0; i < paragraphs.Count; i++)
            children.Add(new TextEl(paragraphs[i]) { Size = 14f, Color = Tok.TextPrimary });

        return new BoxEl
        {
            Direction = 1,
            Gap = 10,
            MaxWidth = 560,
            Children = children.ToArray(),
        };
    }

    /// <summary>A titled rich-text block: a bold 20px heading followed by the body paragraphs.</summary>
    public static BoxEl Article(string heading, IReadOnlyList<string> paragraphs)
    {
        var children = new List<Element>(paragraphs.Count + 1)
        {
            new TextEl(heading) { Size = 20f, Bold = true, Color = Tok.TextPrimary },
        };
        for (int i = 0; i < paragraphs.Count; i++)
            children.Add(new TextEl(paragraphs[i]) { Size = 14f, Color = Tok.TextPrimary });

        return new BoxEl
        {
            Direction = 1,
            Gap = 10,
            MaxWidth = 560,
            Children = children.ToArray(),
        };
    }
}
