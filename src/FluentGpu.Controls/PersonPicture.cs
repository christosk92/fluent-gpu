using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI PersonPicture: a circular avatar showing initials (or a Contact/Group glyph fallback) on the
/// quaternary alt control fill (ControlAltFillColorQuarternary), ringed by a 1px card stroke. Default diameter 96; the
/// initials scale proportionally with the picture size (40px at the 96px default).</summary>
public static class PersonPicture
{
    // Segoe Fluent Icons fallback glyphs: "Contact" (single person) and "People" (group).
    private const string ContactGlyph = "\uE77B";
    private const string GroupGlyph = "\uE716";

    /// <param name="isGroup">When true, the NoPhotoOrInitials fallback uses the group (People) glyph.</param>
    public static BoxEl Create(string initials, float size = 96f, ColorF? fill = null, bool isGroup = false)
    {
        bool hasInitials = !string.IsNullOrWhiteSpace(initials);
        // Initials/fallback glyph scale linearly with diameter (40px @ 96px == WinUI 96dp avatar).
        float textSize = 40f * (size / 96f);
        TextEl label = hasInitials
            ? new TextEl(initials) { Size = textSize, Bold = true, Color = Tok.TextPrimary }
            : new TextEl(isGroup ? GroupGlyph : ContactGlyph) { Size = textSize, FontFamily = Theme.IconFont, Color = Tok.TextPrimary };

        return new BoxEl
        {
            Width = size,
            Height = size,
            Corners = Radii.Circle(size),
            Fill = fill ?? Tok.FillControlAltQuaternary,   // PersonPictureEllipseFill = ControlAltFillColorQuarternary
            BorderWidth = 1f,                              // PersonPictureEllipseStrokeThickness
            BorderColor = Tok.StrokeCardDefault,           // PersonPictureEllipseFillStroke = CardStrokeColorDefault
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            ClipToBounds = true,
            Children = [label],
        };
    }
}
