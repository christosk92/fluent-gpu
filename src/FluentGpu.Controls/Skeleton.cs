using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// Common skeleton SHAPES for the native skeleton-loading kit (<c>Skel.Region</c>): pass one as the <c>rowTemplate</c>
/// (the deriver turns its leaves into shimmer bars), or drop one in directly as a standalone placeholder. Thin
/// composition over the engine's elements — refs Engine only (TerraFX-free), like the rest of the control kit.
/// </summary>
public static class Skeletons
{
    /// <summary>A list-item row shape: an optional leading thumbnail + a two-line title/subtitle stack. The canonical
    /// <c>Skel.Region(items, Skeletons.ListItemRow, …)</c> row template.</summary>
    public static Element ListItemRow(bool withThumbnail = true, float thumbnail = 40f)
    {
        var lines = new BoxEl
        {
            Direction = 1, Gap = 6f, Grow = 1f,
            Children =
            [
                new TextEl("Loading title") { Size = 14f, Grow = 1f },
                new TextEl("Loading subtitle that is a little shorter") { Size = 12.5f, Width = 180f },
            ],
        };
        Element[] kids = withThumbnail
            ? new Element[] { new BoxEl { Width = thumbnail, Height = thumbnail, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary }, lines }
            : new Element[] { lines };
        return new BoxEl { Direction = 0, Gap = 12f, Padding = new Edges4(0, 8, 0, 8), AlignItems = FlexAlign.Center, Children = kids };
    }

    /// <summary>A card body shape: a media block over a title + two body lines.</summary>
    public static Element CardBody(float mediaHeight = 120f) => new BoxEl
    {
        Direction = 1, Gap = 10f, Padding = Edges4.All(12),
        Corners = Radii.OverlayAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new BoxEl { Height = mediaHeight, Corners = Radii.OverlayAll, Fill = Tok.FillSubtleSecondary },
            new TextEl("Card title") { Size = 16f, Width = 160f },
            new TextEl("First body line of the card placeholder") { Size = 13f, Grow = 1f },
            new TextEl("Second body line, a bit shorter") { Size = 13f, Width = 140f },
        ],
    };
}
