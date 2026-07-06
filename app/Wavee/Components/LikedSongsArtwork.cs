using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

static class LikedSongsArtwork
{
    public const string Uri = "spotify:collection:tracks";

    public static bool IsLikedUri(string? uri) => string.Equals(uri, Uri, System.StringComparison.Ordinal);

    public static Element Cover(float size, float radius, string? morphKey = null)
    {
        var deep = Tok.Theme == ThemeKind.Dark ? ColorF.FromRgba(0x41, 0x18, 0x39) : ColorF.FromRgba(0xD8, 0x35, 0x73);
        var hot = ColorF.FromRgba(0xFF, 0x4F, 0x91);
        var glow = ColorF.FromRgba(0x35, 0xD6, 0xF2);
        return new BoxEl
        {
            Width = size, Height = size, ZStack = true, ClipToBounds = true,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(radius), MorphId = morphKey,
            Gradient = LinearGradient(135f,
                new GradientStop(0f, hot),
                new GradientStop(0.58f, deep),
                new GradientStop(1f, glow)),
            Children =
            [
                new BoxEl
                {
                    Transform = Affine2D.Translation(size * 0.14f, size * 0.10f),
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl(Mdl.HeartFill)
                        {
                            Size = size * 0.82f, LineHeight = size * 0.82f, FontFamily = Theme.IconFont,
                            Color = ColorF.FromRgba(255, 255, 255, 42),
                        },
                    ],
                },
                new BoxEl
                {
                    Width = size * 0.58f, Height = size * 0.58f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(size * 0.18f),
                    Fill = ColorF.FromRgba(255, 255, 255, 34),
                    BorderWidth = 1f, BorderColor = ColorF.FromRgba(255, 255, 255, 80),
                    Shadow = Elevation.Card,
                    Children =
                    [
                        new TextEl(Mdl.HeartFill)
                        {
                            Size = size * 0.27f, LineHeight = size * 0.27f, FontFamily = Theme.IconFont,
                            Color = ColorF.FromRgba(255, 255, 255),
                        },
                    ],
                },
            ],
        };
    }
}
