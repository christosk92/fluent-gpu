using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// Showcases font families + the standardized icon set. Every text run carries a FontFamily (a system name or a
// custom "path/to.ttf#Family Name", the WinUI FontIcon syntax); Ui.Icon renders a glyph from Theme.IconFont, and the
// Icons class holds named codepoints so glyphs are consistent everywhere.
sealed class IconsPage : Component
{
    static readonly ColorF Grey = ColorF.FromRgba(0x9A, 0x9A, 0x9A);
    static readonly ColorF Mono = ColorF.FromRgba(0x8A, 0xD0, 0xFF);

    static readonly (string Name, string Glyph)[] Set =
    {
        ("Home", Icons.Home), ("Search", Icons.Search), ("Settings", Icons.Settings), ("Menu", Icons.Menu),
        ("Back", Icons.Back), ("More", Icons.More), ("Add", Icons.Add), ("Cancel", Icons.Cancel),
        ("Accept", Icons.Accept), ("Refresh", Icons.Refresh), ("Play", Icons.Play), ("Pause", Icons.Pause),
        ("Previous", Icons.Previous), ("Next", Icons.Next), ("Shuffle", Icons.Shuffle), ("RepeatAll", Icons.RepeatAll),
        ("Volume", Icons.Volume), ("Mute", Icons.Mute), ("Heart", Icons.Heart), ("Star", Icons.Star),
        ("List", Icons.List), ("Grid", Icons.Grid), ("MusicNote", Icons.MusicNote), ("Picture", Icons.Picture),
        ("Download", Icons.Download), ("Share", Icons.Share), ("Folder", Icons.Folder), ("Movie", Icons.Movie),
    };

    public override Element Render()
    {
        var cards = new Element[Set.Length];
        for (int i = 0; i < cards.Length; i++) cards[i] = IconCard(Set[i].Name, Set[i].Glyph);

        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = 18, Padding = Edges4.All(24),
            Children =
            [
                Heading("Icons & font families"),
                Text("Every text run has a FontFamily — a system name (\"Segoe UI\", \"Segoe Fluent Icons\") or a custom file as \"path/to.ttf#Family Name\" (the WinUI FontIcon syntax). Ui.Icon(glyph) renders from Theme.IconFont; the Icons class holds named codepoints.")
                    .Foreground(Grey),

                CodeNote("Ui.Icon(Icons.Play)                       // a named glyph in Segoe Fluent Icons"),
                CodeNote("Ui.Icon(glyph, family: \"Assets/MediaIcons.ttf#Media Player Fluent Icons\")"),
                CodeNote("Ui.Text(\"hi\").Font(\"Consolas\")             // any run can set its family"),

                Label("Named icons (Theme.IconFont)"),
                UniformGrid(6, 12f, 84f, cards),

                Label("Same text, three font families"),
                new BoxEl
                {
                    Direction = 1, Gap = 6, Padding = Edges4.All(14), Corners = CornerRadius4.All(8f), Fill = ColorF.FromRgba(0x1A, 0x1A, 0x1A),
                    Children =
                    [
                        Text("The quick brown fox — Segoe UI").Font("Segoe UI").FontSize(16f),
                        Text("The quick brown fox — Consolas").Font("Consolas").FontSize(16f),
                        Text("The quick brown fox — Georgia").Font("Georgia").FontSize(16f),
                    ],
                },
            ],
        });
    }

    static Element Label(string s) => new TextEl(s) { Size = 13f, Bold = true, Color = Grey };

    static Element CodeNote(string s) => new BoxEl
    {
        Fill = ColorF.FromRgba(0x0E, 0x0E, 0x14), Corners = CornerRadius4.All(6f), Padding = Edges4.All(10),
        Children = [Text(s).Font("Consolas").Foreground(Mono).FontSize(13f)],
    };

    static Element IconCard(string name, string glyph) => new BoxEl
    {
        Direction = 1, Gap = 8, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = Edges4.All(8),
        Corners = CornerRadius4.All(8f), Fill = ColorF.FromRgba(0x1A, 0x1A, 0x1A),
        Children = [Icon(glyph, 28f), Text(name).FontSize(11f).Foreground(Grey)],
    };
}
