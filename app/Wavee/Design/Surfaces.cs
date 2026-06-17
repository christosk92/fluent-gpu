using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Surface helpers. The shell is a dark canvas with a FLOATING rounded content card (soft shadow) and a SUBTLE,
// edge-transparent accent band tint over it — the WaveeMusic "Files-style" look, not edge-to-edge Mica. Kept here: the
// accent hero wash for page headers, and the album-art PLACEHOLDER (a per-item gradient cover + a faint disc glyph) so
// art tiles with no bitmap yet read as art, not holes — used by cards, rows and the bar.
public static class Surfaces
{
    /// <summary>A top-anchored accent → transparent wash for a page header, over Mica.</summary>
    public static GradientSpec HeroWash(ColorF accent) => new(
        GradientShape.Linear, 180f,
        [new GradientStop(0f, accent with { A = 0.22f }), new GradientStop(1f, accent with { A = 0f })]);

    /// <summary>A deterministic per-item cover gradient (a diagonal deep→darker wash). <paramref name="seed"/> picks the
    /// hue so every tile gets its own art-derived feel; the disc glyph rides on top (see <see cref="ArtPlaceholder"/>).</summary>
    public static GradientSpec CoverWash(int seed)
    {
        float hue = (seed * 47) % 360;                              // spread hues across the catalog
        var top = ColorF.FromHsv(hue, 0.42f, 0.40f);               // deep, saturated
        var bottom = ColorF.FromHsv((hue + 24f) % 360f, 0.55f, 0.20f); // darker, hue-shifted corner
        return new(GradientShape.Linear, 135f, [new GradientStop(0f, top), new GradientStop(1f, bottom)]);
    }

    /// <summary>An album-art placeholder tile: a per-item <see cref="CoverWash"/> gradient with a centered, dimmed disc
    /// glyph and a 1px hairline rim — so an art slot with no bitmap yet reads as a cover, not an empty hole. Square unless
    /// <paramref name="height"/> is given; the disc scales to the tile.</summary>
    public static BoxEl ArtPlaceholder(int seed, float size, float corners, float? height = null) => new()
    {
        Width = size, Height = height ?? size,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Gradient = CoverWash(seed), Corners = CornerRadius4.All(corners),
        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        Children = [Icon(Icons.MusicNote, MathF.Max(12f, MathF.Min(size, height ?? size) * 0.30f), new ColorF(1f, 1f, 1f, 0.22f))],
    };

    /// <summary>Artwork slot with a gradient fallback underneath the async image.</summary>
    public static Element Artwork(Image? image, int seed, float width, float height, float corners)
    {
        var fallback = ArtPlaceholder(seed, width, corners, height);
        if (image?.Url is not { Length: > 0 } url) return fallback;

        return new BoxEl
        {
            ZStack = true, Width = width, Height = height, ClipToBounds = true,
            Corners = CornerRadius4.All(corners),
            Children =
            [
                fallback,
                Ui.Image(url, width, height, corners, ColorF.Transparent, image.BlurHash),
            ],
        };
    }
}
