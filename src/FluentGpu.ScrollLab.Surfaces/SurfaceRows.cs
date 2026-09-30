using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.ScrollLab.Surfaces;

/// <summary>
/// The synthetic track model and the one track-row template the list surfaces share: a Wavee-shaped 8-cell
/// <see cref="ListRowEl"/> (index, art, title/artist, album, heart, duration, divider) beside the fixed-column
/// <see cref="PositionBarcode"/>. Every per-index value comes from small static pools or <see cref="FormatCache"/>, so a
/// recycled slot's rebind allocates nothing once the caches are warm — the lab must not perturb the frames it measures.
/// </summary>
public static class SurfaceRows
{
    static readonly string[] Titles =
    {
        "Midnight City", "Intro", "Wait", "Outro", "Reunion", "Steve McQueen", "Claudia Lewis", "OK Pal",
        "Holograms", "Echoes of Mine", "Splendor", "Soon, My Friend", "Raconte-Moi Une Histoire", "Where the Boats Go",
        "Year One, One UFO", "Fountains", "Train to Pluton", "Another Wave from You", "Solitude", "Klaus I Love You",
        "Kim & Jessie", "Graveyard Girl", "Couleurs", "Skin of the Night", "We Own the Sky", "Teen Angst",
        "Lower Your Eyelids to Die with the Sun", "Moonchild", "Too Late", "Oblivion", "Lost Souls",
        "Do It, Try It", "Go!", "Walkway Blues", "Road Blaster", "Laser Gun", "Mirror", "Tension",
        "Sitting", "Atlantique Sud", "Sister", "Violence", "Photograph", "Sleep", "Afterglow", "Aurora",
        "Parallel Lines", "Neon Rain", "Glasshouse", "Northern Lights", "Paper Planes", "Undertow",
        "Low Tide", "Headlights", "Satellite", "Vapour Trail", "Static", "Bloom", "Ember", "Drift",
        "Signal Fire", "Horizon", "Canopy", "Tessellate",
    };

    static readonly string[] Artists =
    {
        "M83", "Daft Punk", "Air", "Justice", "Phoenix", "Sébastien Tellier", "Kavinsky", "Cassius",
        "Bonobo", "Tycho", "Boards of Canada", "Four Tet", "Jon Hopkins", "Caribou", "Floating Points", "Moderat",
        "Rival Consoles", "Nils Frahm", "Ólafur Arnalds", "Kiasmos", "ODESZA", "Com Truise", "Washed Out", "Toro y Moi",
        "Beach House", "Tame Impala", "The xx", "Jamie xx", "Burial", "Aphex Twin", "Bicep", "Fred again..",
    };

    static readonly string[] Albums =
    {
        "Hurry Up, We're Dreaming", "Before the Dawn Heals Us", "Saturdays = Youth", "Dead Cities", "Discovery",
        "Moon Safari", "Cross", "Wolfgang Amadeus Phoenix", "OutRun", "Migration", "Dive", "Geogaddi",
        "Rounds", "Immunity", "Our Love", "Crush", "III", "Spaces", "Re:member", "Blurry",
        "A Moment Apart", "Galactic Melt", "Within and Without", "Anything in Return", "Bloom", "Currents",
        "Coexist", "In Colour", "Untrue", "Syro", "Isles", "Actual Life",
    };

    /// <summary>A deterministic per-index hash (Knuth multiplicative).</summary>
    public static int Hash(int i) => (int)(((uint)i * 2654435761u) >> 8);

    public static string Title(int i) => Titles[Hash(i) % Titles.Length];
    public static string Artist(int i) => Artists[Hash(i + 7) % Artists.Length];
    public static string Album(int i) => Albums[Hash(i + 13) % Albums.Length];
    public static long DurationMs(int i) => 120_000L + Hash(i + 29) % 240_000;
    public static bool Liked(int i) => Hash(i + 31) % 5 == 0;

    /// <summary>The art tile's colour: a stable muted hue per index.</summary>
    public static ColorF ArtColor(int i)
    {
        int h = Hash(i + 3);
        return ColorF.FromRgba((byte)(60 + (h & 0x7F)), (byte)(60 + ((h >> 7) & 0x7F)), (byte)(60 + ((h >> 14) & 0x7F)));
    }

    /// <summary>The track row for a bound slot: the 8-cell content row (Grow) + the barcode column (fixed) — one row
    /// wrapper whose root Grows so it fills the ItemsView's row slot.</summary>
    public static Element TrackRow(IReadSignal<int> index, int bits, float rowHeight)
    {
        var cells = new RowCellBuffer();
        ColorF primary = Tok.TextPrimary;
        ColorF secondary = Tok.TextSecondary;
        ColorF divider = Tok.StrokeDividerDefault;
        string iconFont = Theme.IconFont;
        float textTop = MathF.Max(0f, rowHeight * 0.5f - 19f);
        return new BoxEl
        {
            Direction = 0,
            Height = rowHeight,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            Children =
            [
                new ListRowEl(Prop.Of<RowCells>(() =>
                {
                    int i = index.Value;
                    cells.Clear();
                    cells.Add(new RowCell { Kind = RowCellKind.Text, Rect = new RectF(8f, rowHeight * 0.5f - 10f, 52f, 20f), Text = FormatCache.Int(i + 1), Color = secondary, FontSize = 13f, Trim = TextTrim.Clip });
                    cells.Add(new RowCell { Kind = RowCellKind.Rect, Rect = new RectF(64f, rowHeight * 0.5f - 20f, 40f, 40f), Color = ArtColor(i), Corners = CornerRadius4.All(4f) });
                    cells.Add(new RowCell { Kind = RowCellKind.Text, Rect = new RectF(116f, textTop, 300f, 20f), Text = Title(i), Color = primary, FontSize = 14f, Trim = TextTrim.CharacterEllipsis });
                    cells.Add(new RowCell { Kind = RowCellKind.Text, Rect = new RectF(116f, textTop + 20f, 300f, 18f), Text = Artist(i), Color = secondary, FontSize = 12f, Trim = TextTrim.CharacterEllipsis });
                    cells.Add(new RowCell { Kind = RowCellKind.Text, Rect = new RectF(436f, rowHeight * 0.5f - 10f, 240f, 20f), Text = Album(i), Color = secondary, FontSize = 13f, Trim = TextTrim.CharacterEllipsis });
                    cells.Add(new RowCell { Kind = RowCellKind.Glyph, Rect = new RectF(692f, rowHeight * 0.5f - 8f, 16f, 16f), Text = Liked(i) ? Icons.HeartFill : Icons.Heart, Color = secondary, FontSize = 14f, FontFamily = iconFont });
                    cells.Add(new RowCell { Kind = RowCellKind.Text, Rect = new RectF(724f, rowHeight * 0.5f - 10f, 48f, 20f), Text = FormatCache.DurationMmSs(DurationMs(i)), Color = secondary, FontSize = 13f, Trim = TextTrim.Clip });
                    cells.Add(new RowCell { Kind = RowCellKind.Rect, Rect = new RectF(8f, rowHeight - 1f, 764f, 1f), Color = divider });
                    return cells.Current;
                }))
                {
                    Grow = 1f,
                    Shrink = 1f,
                    MinWidth = 0f,
                    Height = rowHeight,
                    HoverFill = Tok.FillSubtleSecondary,
                },
                PositionBarcode.Column(index, bits, _ => rowHeight),
            ],
        };
    }
}
