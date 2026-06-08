using System.Collections.Generic;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>A single absolutely-positioned child placed at (<see cref="X"/>, <see cref="Y"/>) within a <see cref="Canvas"/>.</summary>
public sealed record CanvasChild(float X, float Y, Element Child);

/// <summary>
/// WinUI-style Canvas: a fixed-size surface that positions each child by absolute (X, Y) offset from the top-left
/// origin. Implemented as a ZStack (children overlap at the origin) where every child is wrapped in a BoxEl whose
/// OffsetX/OffsetY translate it into place. Children are clipped to the canvas bounds.
/// </summary>
public static class Canvas
{
    public static BoxEl Create(float width, float height, IReadOnlyList<CanvasChild> children)
    {
        var positioned = new List<Element>(children.Count);
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            positioned.Add(new BoxEl
            {
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
