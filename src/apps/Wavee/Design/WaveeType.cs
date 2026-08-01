using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// Semantic type aliases so call sites read INTENT, not raw sizes. Every alias maps to the engine's WinUI type ramp
// (Ui.Caption/Body/BodyStrong/Subtitle/Title/Display in Dsl/Typography.cs). Never author a raw `TextEl { Size = … }`.
public static class WaveeType
{
    /// <summary>Track / album / playlist titles in lists. → Ui.BodyStrong (14/20 Semibold).</summary>
    public static TextEl TrackTitle(string s) => Ui.BodyStrong(s);

    /// <summary>Artist · duration · metadata. → Ui.Caption secondary (12/16).</summary>
    public static TextEl TrackMeta(string s) => Ui.Caption(s).Secondary();

    /// <summary>"Because you played…" section / rail headers. → Ui.Subtitle (20/28 Semibold).</summary>
    public static TextEl RailHeader(string s) => Ui.Subtitle(s);

    /// <summary>A rail heading plus baseline-aligned compact metadata. Both runs shape as one paragraph, so the smaller
    /// suffix shares the heading's baseline instead of bottom-aligning two unrelated line boxes.</summary>
    public static SpanTextEl RailHeader(string title, string meta)
    {
        var heading = Ui.Subtitle("");
        var caption = Ui.Caption("");
        return new SpanTextEl(
        [
            new TextSpan(title),
            new TextSpan("  " + meta, Weight: caption.ResolvedWeight,
                Color: Tok.TextTertiary, Size: caption.Size),
        ])
        {
            Size = heading.Size,
            Weight = heading.ResolvedWeight,
            LineHeight = heading.LineHeight,
            LineStacking = heading.LineStacking,
            LineBounds = heading.LineBounds,
            Wrap = TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis,
            MaxLines = 1,
            MinWidth = 0f,
            Shrink = 1f,
        };
    }

    /// <summary>Page hero (playlist / album name). → Ui.Title (28/36 Semibold).</summary>
    public static TextEl PageHero(string s) => Ui.Title(s);

    /// <summary>Wide artist identity display: a larger, tightly tracked Display face for editorial heroes.</summary>
    public static TextEl ArtistDisplay(string s) => Ui.Display(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Size = 84f,
        LineHeight = 96f,
        Weight = 700,
        CharSpacing = -28f,
        MinSize = 68f,
    };

    /// <summary>Medium artist identity title retaining the display face and weight under pressure.</summary>
    public static TextEl ArtistTitle(string s) => Ui.TitleLarge(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Size = 48f,
        LineHeight = 60f,
        Weight = 700,
        CharSpacing = -20f,
        MinSize = 40f,
    };

    /// <summary>Compact artist identity title with the same bold, tightly tracked voice.</summary>
    public static TextEl ArtistCompactTitle(string s) => Ui.Title(s) with
    {
        FontFamily = "Segoe UI Variable Display",
        Size = 32f,
        LineHeight = 40f,
        Weight = 700,
        CharSpacing = -12f,
        MinSize = 28f,
    };

    /// <summary>Now-playing track title. → Ui.Subtitle.</summary>
    public static TextEl NowPlayingTitle(string s) => Ui.Subtitle(s);
}
