using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Reconciler;
using static FluentGpu.Dsl.Ui;

// A Wavee-flavoured virtualized track list: 5,000 rows, each 56px, scrolled with the mouse wheel. Only the
// ~viewport window of rows is ever realized (recycled via the slab free-list as you scroll), so it stays smooth
// and flat in memory at any list size — the workload the WaveeMusic "Liked Songs" / playlist screens demand.
//
// Run it with:  dotnet run --project src/FluentGpu.WindowsApp -- --demo list
sealed class TrackListDemo : Component
{
    const int N = 5_000;
    static readonly string[] Titles =
        { "Midnight City", "Redbone", "Nightcall", "Teardrop", "Svefn-g-englar", "Open Eye Signal", "Avril 14th", "An Ending (Ascent)" };
    static readonly string[] Artists =
        { "M83", "Childish Gambino", "Kavinsky", "Massive Attack", "Sigur Rós", "Jon Hopkins", "Aphex Twin", "Brian Eno" };

    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            // sticky-ish header band (a normal row above the scroller)
            new BoxEl
            {
                Height = 64, Padding = new Edges4(24, 16, 24, 16), AlignItems = FlexAlign.Center,
                Fill = ColorF.FromRgba(0x18, 0x18, 0x18),
                Children = [Heading("Liked Songs"), Text($"   {N:N0} songs").Foreground(Grey)],
            },
            // the virtualized list fills the rest of the window and scrolls
            Virtual.List(N, 56f, Row, keyOf: i => "t" + i) with { Grow = 1f },
        ],
    };

    static readonly ColorF Grey = ColorF.FromRgba(0x9A, 0x9A, 0x9A);

    static Element Row(int i)
    {
        string title = Titles[i % Titles.Length];
        string artist = Artists[i % Artists.Length];
        int mins = 2 + (i % 4), secs = (i * 37) % 60;
        return new BoxEl
        {
            Direction = 0, Height = 56, Gap = 14, Padding = new Edges4(24, 8, 24, 8),
            AlignItems = FlexAlign.Center,
            Fill = (i & 1) == 0 ? ColorF.FromRgba(0x1B, 0x1B, 0x1B) : ColorF.FromRgba(0x15, 0x15, 0x15),
            HoverFill = ColorF.FromRgba(0x2C, 0x2C, 0x2C),
            OnClick = () => { },   // hoverable + clickable rows
            Children =
            [
                new BoxEl { Width = 28, Children = [Text($"{i + 1}").Foreground(Grey)] },
                // album-art stand-in (deterministic tint — the async image pipeline is a later subsystem)
                new BoxEl { Width = 40, Height = 40, Corners = CornerRadius4.All(4), Fill = AlbumTint(i) },
                new BoxEl
                {
                    Direction = 1, Grow = 1, Gap = 2,
                    Children = [Text(title), Text(artist).Foreground(Grey).FontSize(12f)],
                },
                Text($"{mins}:{secs:00}").Foreground(Grey),
            ],
        };
    }

    static ColorF AlbumTint(int i)
        => ColorF.FromRgba((byte)(60 + (i * 53) % 180), (byte)(60 + (i * 97) % 180), (byte)(60 + (i * 151) % 180));
}
