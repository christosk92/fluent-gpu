using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>How a <see cref="Viewbox"/> scales its child to the available box. Mirrors WinUI <c>Stretch</c>
/// (default <see cref="Uniform"/>).</summary>
public enum ViewboxStretch : byte { None, Fill, Uniform, UniformToFill }

/// <summary>Constrains a <see cref="Viewbox"/> scale direction. Mirrors WinUI <c>StretchDirection</c>
/// (default <see cref="Both"/>).</summary>
public enum ViewboxStretchDirection : byte { UpOnly, DownOnly, Both }

/// <summary>A WinUI Viewbox: scales its single child by a composited transform (no relayout). WinUI computes the
/// scale from a layout measure pass; this engine has no measure-override seam on <see cref="BoxEl"/>, so the scale
/// is supplied either as an explicit factor (the <c>scale</c> overload) or computed from a known content size and
/// target box via <see cref="ViewboxStretch"/>/<see cref="ViewboxStretchDirection"/> logic (the sized overload). The
/// child is wrapped in a centered <see cref="BoxEl"/> carrying a uniform <see cref="BoxEl.ScaleX"/>/<see cref="BoxEl.ScaleY"/>.</summary>
public static class Viewbox
{
    /// <summary>Explicit-factor Viewbox (no layout measure): scales the child uniformly by <paramref name="scale"/>×.</summary>
    public static BoxEl Create(Element child, float scale = 1.5f) => Wrap(child, scale, scale);

    /// <summary>WinUI-faithful Viewbox: computes the scale from the child's natural size and a target box, applying
    /// <paramref name="stretch"/> and <paramref name="stretchDirection"/>. (Sizes are caller-supplied because the
    /// engine exposes no measure-override seam — see the design note in the type summary.)</summary>
    public static BoxEl Create(
        Element child,
        float contentWidth, float contentHeight,
        float availableWidth, float availableHeight,
        ViewboxStretch stretch = ViewboxStretch.Uniform,
        ViewboxStretchDirection stretchDirection = ViewboxStretchDirection.Both)
    {
        float scaleX = ComputeAxis(availableWidth, contentWidth);
        float scaleY = ComputeAxis(availableHeight, contentHeight);

        (float sx, float sy) = stretch switch
        {
            ViewboxStretch.None => (1f, 1f),
            ViewboxStretch.Fill => (scaleX, scaleY),                                            // independent axes
            ViewboxStretch.Uniform => Uni(MathF.Min(scaleX, scaleY)),                           // fit within
            ViewboxStretch.UniformToFill => Uni(MathF.Max(scaleX, scaleY)),                     // fill (may clip)
            _ => (1f, 1f),
        };

        sx = ClampDir(sx, stretchDirection);
        sy = ClampDir(sy, stretchDirection);
        return Wrap(child, sx, sy);

        static (float, float) Uni(float s) => (s, s);
    }

    static float ComputeAxis(float available, float content)
        => (float.IsInfinity(available) || content <= 0f) ? 1f : available / content;

    static float ClampDir(float scale, ViewboxStretchDirection dir) => dir switch
    {
        ViewboxStretchDirection.UpOnly => MathF.Max(1f, scale),
        ViewboxStretchDirection.DownOnly => MathF.Min(1f, scale),
        _ => scale,
    };

    static BoxEl Wrap(Element child, float sx, float sy) => new BoxEl
    {
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Children =
        [
            new BoxEl
            {
                ScaleX = sx,
                ScaleY = sy,
                Children = [child],
            },
        ],
    };
}
