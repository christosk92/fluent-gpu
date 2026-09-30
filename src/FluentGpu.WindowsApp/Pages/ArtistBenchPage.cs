using System;
using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.GalleryKit;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Effects;
using static FluentGpu.Dsl.Ui;

// A measurement surface shaped like Wavee's artist page — the GPU-relevant structure only, no data: a baked-blur stage
// behind the page, a full-bleed photo hero (ClipToBounds + bottom edge fade + top stretch + parallax) inside the app's
// pinned, leading-collapsing hero root (Sticky(0) + Collapse(Leading), no ClipToBounds) under a sticky-clipped colour
// wash, the compact-band sentinel, a magazine column cut at the band (StickyClip + a top feather) holding
// a popular-tracks band and eight horizontal shelves of image cards (each an edge-faded horizontal scroller), and an
// acrylic dock floating over the bottom of the page (a live backdrop blur of whatever scrolls under it).
//
//   dotnet run --project src/FluentGpu.WindowsApp -c Release -- --scroll-bench artist-bench --vertical --dipPerSec 2000
//   (+ --edge-fades-off / --freeze-uploads / --force-full-direct / --clear-only knockouts)
[GalleryPage("artist-bench", "Artist page (scroll bench)", "Diagnostics", Hidden = true)]
sealed class ArtistBenchPage : Component
{
    const float HeroH = 520f, BandInset = 56f, CardW = 176f, Gutter = 32f;

    // A/B arms for attributing the offscreen cost (gallery CLI args, read once): each removes ONE feature of the page.
    static readonly string[] Args = Environment.GetCommandLineArgs();
    static bool Off(string arm) => Array.IndexOf(Args, "--ab-no-" + arm) >= 0;
    static readonly bool NoDock = Off("dock"), NoStage = Off("stage"), NoPhotoFade = Off("photo-fade"),
        NoMagazineFade = Off("magazine-fade"), NoShelfFades = Off("shelf-fades"), NoPageFade = Off("page-fade"),
        NoIdentityFade = Off("identity-fade"), NoStickyClip = Off("stickyclip"), NoStretch = Off("stretch");

    static readonly string[] Art =
    [
        "Button.png", "CheckBox.png", "ComboBox.png", "Slider.png", "ToggleSwitch.png", "RadioButton.png", "ListView.png",
        "GridView.png", "TreeView.png", "Pivot.png", "InfoBar.png", "Expander.png", "ColorPicker.png", "CalendarView.png",
    ];

    public override Element Render()
    {
        var sections = new Element[9];
        sections[0] = PopularBand();
        for (int i = 1; i < sections.Length; i++) sections[i] = Shelf(i);

        Element photo = new BoxEl
        {
            Height = HeroH, ZStack = true, ClipToBounds = true, TransformOriginX = 0.5f, TransformOriginY = 0f,
            EdgeFade = NoPhotoFade ? null : new EdgeFadeSpec(EdgeMask.Bottom, 160f),
            Children = [new ImageEl { Source = Assets.Header, Height = HeroH, Fit = ImageFit.Cover, DecodePx = 1600f }],
        };
        if (!NoStretch) photo = photo.StretchFromTop().ParallaxY(0.35f, HeroH);

        Element identity = new BoxEl
        {
            Direction = 1, Padding = new Edges4(Gutter, 0f, Gutter, 32f), Gap = 8f,
            Children =
            [
                new TextEl("Artist Name") { Size = 64f, Bold = true },
                Text("12,345,678 monthly listeners").Foreground(ColorF.FromRgba(230, 230, 230)),
            ],
        };
        if (!NoIdentityFade) identity = identity.OnScroll(ScrollEffect.Fade((HeroH - BandInset) * 0.5, HeroH - BandInset, 1f, 0f));

        // The app's hero shape (Wavee Artist.UI.cs HeroBanner): the expanded presentation slides away over the collapse
        // distance inside a root pinned at the top and COLLAPSING into the compact band. Leading-anchored and NO
        // ClipToBounds: the collapse cuts its children at the presented edge by itself, and a box clip on the root would
        // cut the photo's top overscroll stretch (the dark band above the photo).
        const float CollapseOver = HeroH - BandInset;
        Element expanded = new BoxEl
        {
            ZStack = true, Height = HeroH,
            Children = [photo, new BoxEl { Direction = 1, Justify = FlexJustify.End, Children = [identity] }],
        }.OnScroll(ScrollEffect.Parallax(0.0, CollapseOver, 0f, -CollapseOver));
        Element hero = new BoxEl
        {
            Direction = 1, ZStack = true, Height = HeroH,
            Children = [expanded],
        }.Sticky(0f).Collapse(CollapseOver, BandInset, CollapseAnchor.Leading);

        Element sentinel = new BoxEl { Height = 0f, HitTestVisible = false }.Sticky(BandInset);

        Element magazine = new BoxEl
        {
            Direction = 1, Gap = 32f, Padding = new Edges4(Gutter, 16f, Gutter, 160f),
            EdgeFade = NoMagazineFade ? null : new EdgeFadeSpec(EdgeMask.Top, 24f),
            Children = sections,
        };
        if (!NoStickyClip) magazine = magazine.StickyClip(BandInset);

        Element wash = new BoxEl
        {
            Height = HeroH + 360f, HitTestVisible = false,
            Fill = ColorF.FromRgba(96, 48, 140, 200),
        };
        if (!NoStickyClip) wash = wash.StickyClip(BandInset);

        Element page = new ScrollEl
        {
            Grow = 1f, AutoEdgeFade = !NoPageFade,
            Content = new BoxEl
            {
                ZStack = true,
                Children = [wash, new BoxEl { Direction = 1, Children = [hero, sentinel, magazine] }],
            },
        };

        Element stage = new ImageEl
        {
            Source = Assets.Header, Fit = ImageFit.Cover, DecodePx = 512f,
            BakedBlur = new BakedBlurSpec(44f, 0.25f), Saturation = 1.5f,
        };

        Element dock = new BoxEl
        {
            Height = 88f, Margin = new Edges4(12f, 0f, 12f, 12f), Corners = CornerRadius4.All(8f),
            Fill = ColorF.Transparent,
            Acrylic = new AcrylicSpec(ColorF.FromRgba(32, 32, 32), 0.6f, 30f, 0.02f, 0.85f, ColorF.FromRgba(40, 40, 40)),
            Children = [Text("Now playing").Foreground(ColorF.FromRgba(240, 240, 240))],
            Padding = Edges4.All(16f),
        };

        return new BoxEl
        {
            ZStack = true, Grow = 1f, MinHeight = 0f,
            Children =
            [
                NoStage ? new BoxEl { Fill = ColorF.FromRgba(20, 20, 24) } : stage,
                new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Children = [page] },
                NoDock ? new BoxEl() : new BoxEl { Direction = 1, Justify = FlexJustify.End, HitTestVisible = false, Children = [dock] },
            ],
        };
    }

    static Element PopularBand()
    {
        var rows = new Element[10];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Direction = 0, Height = 56f, Gap = 12f, AlignItems = FlexAlign.Center,
                Children =
                [
                    new ImageEl { Source = Assets.ControlImage(Art[i % Art.Length]), Width = 40f, Height = 40f, Corners = CornerRadius4.All(4f) },
                    Text("Popular track " + (i + 1)),
                ],
            };
        return new BoxEl { Direction = 1, Gap = 4f, Children = [new TextEl("Popular") { Size = 24f, Bold = true }, new BoxEl { Direction = 1, Children = rows }] };
    }

    static Element Shelf(int index)
    {
        var cards = new Element[14];
        for (int i = 0; i < cards.Length; i++)
            cards[i] = new BoxEl
            {
                Direction = 1, Width = CardW, Gap = 6f,
                Children =
                [
                    new ImageEl { Source = Assets.ControlImage(Art[(i + index) % Art.Length]), Width = CardW, Height = CardW, Corners = CornerRadius4.All(6f) },
                    Text("Release " + (i + 1)),
                    Text("2024 · Album").Foreground(ColorF.FromRgba(170, 170, 170)).FontSize(12f),
                ],
            };
        return new BoxEl
        {
            Direction = 1, Gap = 8f,
            Children =
            [
                new TextEl("Shelf " + index) { Size = 24f, Bold = true },
                new ScrollEl
                {
                    Horizontal = true, AutoEdgeFade = !NoShelfFades, Height = CardW + 52f,
                    Content = new BoxEl { Direction = 0, Gap = 16f, Children = cards },
                },
            ],
        };
    }
}
