using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI Border: draws a border and background around a single child. A thin wrapper over <see cref="BoxEl"/>.</summary>
public static class Border
{
    public static BoxEl Create(
        Element child,
        float borderWidth = 1f,
        float cornerRadius = 8f,
        ColorF? borderColor = null,
        ColorF? background = null,
        float padding = 12f)
        => new BoxEl
        {
            BorderWidth = borderWidth,
            BorderColor = borderColor ?? Tok.StrokeCardDefault,
            Corners = CornerRadius4.All(cornerRadius),
            Fill = background ?? Tok.FillCardDefault,
            Padding = Edges4.All(padding),
            Children = new[] { child },
        };
}
