using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Reconciler;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// The Wavee skeleton on the real GPU path: a sidebar (nav) → PageHost back stack; a Home page (album-art card Grid in
// a ScrollView) and a Playlist page (5,000-row virtualized track list with art thumbnails); a now-playing PlayerBar
// (Slider + transport IconButtons + ToggleButton). Composes every subsystem built this session.
//
//   dotnet run --project src/FluentGpu.WindowsApp -- --demo wavee
[Route("wavee")]
sealed class WaveeShell : Component
{
    static readonly ColorF Grey = ColorF.FromRgba(0x9A, 0x9A, 0x9A);
    static readonly string[] Titles = { "Midnight City", "Redbone", "Nightcall", "Teardrop", "Open Eye Signal", "Avril 14th" };
    static readonly string[] Artists = { "M83", "Childish Gambino", "Kavinsky", "Massive Attack", "Jon Hopkins", "Aphex Twin" };

    readonly Navigator _nav = new(new Route("home"));

    public override Element Render()
    {
        var (playing, setPlaying) = UseState(false);
        var seek = UseFloatSignal(0.3f);
        var shuffle = UseSignal(false);
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                new BoxEl { Direction = 0, Grow = 1, Children = [Sidebar(), Embed.Comp(() => new PageHost(_nav, Page))] },
                PlayerBar(playing, setPlaying, seek, shuffle),
            ],
        };
    }

    Element Sidebar() => new BoxEl
    {
        Width = 220, Direction = 1, Gap = 4, Padding = Edges4.All(14), Fill = ColorF.FromRgba(0x0B, 0x0B, 0x0B),
        Children =
        [
            new TextEl("WAVEE") { Size = 18f, Bold = true },
            new BoxEl { Height = 12 },
            NavItem("Home", Icons.Home, "home"), NavItem("Search", Icons.Search, "search"), NavItem("Your Library", Icons.MusicNote, "playlist"),
        ],
    };
    Element NavItem(string label, string glyph, string route) => new BoxEl
    {
        Direction = 0, Gap = 12, AlignItems = FlexAlign.Center,
        Padding = new Edges4(10, 9, 10, 9), Corners = CornerRadius4.All(6f), HoverFill = ColorF.FromRgba(0x24, 0x24, 0x24),
        OnClick = () => _nav.Push(route), Children = [Ui.Icon(glyph, 16f, Grey), new TextEl(label) { Color = Grey }],
    };

    Element Page(Route r) => r.Name == "playlist" ? Playlist() : Home();

    Element Home()
    {
        var cards = new Element[24];
        for (int i = 0; i < cards.Length; i++) cards[i] = AlbumCard(i);
        return new BoxEl
        {
            Direction = 1, Grow = 1, Padding = Edges4.All(24), Fill = ColorF.FromRgba(0x12, 0x12, 0x12),
            Children = [new TextEl("Good evening") { Size = 26f, Bold = true }, new BoxEl { Height = 16 },
                       ScrollView(UniformGrid(4, 16f, 230f, cards))],
        };
    }
    Element AlbumCard(int i) => new BoxEl
    {
        Direction = 1, Gap = 8, Padding = Edges4.All(10), Corners = CornerRadius4.All(8f),
        Fill = ColorF.FromRgba(0x1A, 0x1A, 0x1A), HoverFill = ColorF.FromRgba(0x26, 0x26, 0x26),
        OnClick = () => _nav.Push("playlist", "p" + i),
        Children =
        [
            Image("album/" + i, 150, 150, 6f),
            new TextEl(Titles[i % Titles.Length]) { Bold = true },
            new TextEl(Artists[i % Artists.Length]) { Size = 12f, Color = Grey },
        ],
    };

    Element Playlist() => new BoxEl
    {
        Direction = 1, Grow = 1, Fill = ColorF.FromRgba(0x12, 0x12, 0x12),
        Children =
        [
            new BoxEl { Height = 72, Padding = new Edges4(24, 18, 24, 18), AlignItems = FlexAlign.Center, Direction = 0, Gap = 12,
                Children = [new TextEl("Liked Songs") { Size = 24f, Bold = true }, new TextEl("5,000 songs") { Size = 13f, Color = Grey }] },
            Virtual.List(5000, 56f, TrackRow, keyOf: i => "t" + i) with { Grow = 1f },
        ],
    };
    Element TrackRow(int i) => new BoxEl
    {
        Direction = 0, Height = 56, Gap = 14, AlignItems = FlexAlign.Center, Padding = new Edges4(24, 8, 24, 8),
        Fill = (i & 1) == 0 ? ColorF.FromRgba(0x16, 0x16, 0x16) : ColorF.FromRgba(0x12, 0x12, 0x12),
        HoverFill = ColorF.FromRgba(0x2A, 0x2A, 0x2A), OnClick = () => { },
        Children =
        [
            new BoxEl { Width = 24, Children = [new TextEl($"{i + 1}") { Size = 12f, Color = Grey }] },
            Image("art/" + i, 40, 40, 4f),
            new BoxEl { Direction = 1, Grow = 1, Gap = 2, Children = [new TextEl(Titles[i % Titles.Length]), new TextEl(Artists[i % Artists.Length]) { Size = 12f, Color = Grey }] },
            new TextEl($"{2 + i % 4}:{(i * 37) % 60:00}") { Size = 12f, Color = Grey },
        ],
    };

    Element PlayerBar(bool playing, Action<bool> setPlaying, FloatSignal seek, Signal<bool> shuffle) => new BoxEl
    {
        Direction = 0, Height = 84, AlignItems = FlexAlign.Center, Gap = 16, Padding = new Edges4(16, 0, 16, 0),
        Fill = ColorF.FromRgba(0x18, 0x18, 0x18),
        Children =
        [
            Image("nowplaying", 56, 56, 4f),
            new BoxEl { Direction = 1, Width = 160, Gap = 2, Children = [new TextEl(Titles[0]) { Bold = true }, new TextEl(Artists[0]) { Size = 12f, Color = Grey }] },
            IconButton.Create(Icons.Previous, () => { }),
            IconButton.Create(playing ? Icons.Pause : Icons.Play, () => setPlaying(!playing), IconButton.DefaultStyle with { Size = 44f }),
            IconButton.Create(Icons.Next, () => { }),
            Slider.Create(seek, length: 260f),
            ToggleButton.Create("Shuffle", shuffle),
        ],
    };
}
