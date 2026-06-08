using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI Viewbox: scales its single child uniformly by a composited transform. The child is wrapped in a
/// <see cref="BoxEl"/> carrying a uniform <see cref="BoxEl.ScaleX"/>/<see cref="BoxEl.ScaleY"/>, centered within the
/// outer box. The transform is composited (no relayout), so the child renders at <paramref name="scale"/>× its
/// natural size.</summary>
public static class Viewbox
{
    public static BoxEl Create(Element child, float scale = 1.5f) => new BoxEl
    {
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Children =
        [
            new BoxEl
            {
                ScaleX = scale,
                ScaleY = scale,
                Children = [child],
            },
        ],
    };
}
