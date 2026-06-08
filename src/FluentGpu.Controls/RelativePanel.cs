using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A single child positioned at (<see cref="X"/>, <see cref="Y"/>) within a <see cref="RelativePanel"/>.</summary>
public sealed record RelativeChild(float X, float Y, Element Child);

/// <summary>
/// WinUI-style RelativePanel: positions children relative to the panel and to each other. WinUI resolves a
/// constraint graph (AlignLeftWith, RightOf, AlignTopWithPanel, …) into absolute offsets; for this self-contained
/// demo we accept the already-resolved absolute (X, Y) offsets (like <see cref="Canvas"/>) and render them in a
/// ZStack where each child is wrapped in a BoxEl whose OffsetX/OffsetY translate it into place — enough to show a
/// relative layout result. Children are clipped to the panel bounds.
/// </summary>
public static class RelativePanel
{
    public static BoxEl Create(float width, float height, IReadOnlyList<RelativeChild> children)
    {
        var positioned = new List<Element>(children.Count);
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            positioned.Add(new BoxEl
            {
                // The wrapper fills the ZStack; AlignItems=Start keeps the child at its NATURAL size (not stretched to the
                // full panel height) so each positioned child renders at (X,Y) without distorting its size.
                AlignItems = FlexAlign.Start,
                OffsetX = c.X,
                OffsetY = c.Y,
                Children = new[] { c.Child },
            });
        }

        return new BoxEl
        {
            ZStack = true,
            Width = width,
            Height = height,
            ClipToBounds = true,
            Children = positioned.ToArray(),
        };
    }
}
