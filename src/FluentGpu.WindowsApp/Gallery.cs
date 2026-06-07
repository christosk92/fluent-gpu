using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Reconciler;
using static FluentGpu.Dsl.Ui;

// The capability gallery — a nav-driven showcase of everything fluent-gpu can do, styled to mirror the WinUI 3 Gallery:
// an integrated title bar + search, a grouped adaptive NavigationView (Expanded → Compact → Minimal), a sliding
// selection indicator, page-transition entrances, and the ControlExample pattern on every demo page.
//   dotnet run --project src/FluentGpu.WindowsApp
sealed class GalleryApp : Component
{
    static readonly NavItem[] Items =
    {
        new("welcome", Icons.Home, "Home"),
        new("h-fund", "", "Fundamentals", IsHeader: true),
        new("state", Icons.Refresh, "State & components"),
        new("typography", Icons.Font, "Typography"),
        new("icons", Icons.Star, "Icons & fonts"),
        new("h-layout", "", "Layout", IsHeader: true),
        new("flex", Icons.Tag, "Flexbox"),
        new("grid", Icons.Grid, "CSS Grid"),
        new("repeater", Icons.List, "ItemsRepeater"),
        new("virtualization", Icons.List, "List virtualization"),
        new("h-controls", "", "Controls", IsHeader: true),
        new("buttons", Icons.Accept, "Buttons & commands"),
        new("inputs", Icons.Volume, "Inputs & sliders"),
        new("h-media", "", "Media", IsHeader: true),
        new("images", Icons.Picture, "Images"),
        new("scrolling", Icons.Document, "Scrolling"),
        new("h-motion", "", "Motion & GPU", IsHeader: true),
        new("animation", Icons.Movie, "Animation"),
        new("compositor", Icons.Brush, "Compositor"),
        new("h-samples", "", "Samples", IsHeader: true),
        new("wavee", Icons.MusicNote, "Wavee skeleton"),
    };

    public override Element Render()
    {
        var shell = VStack(0,
            TitleBar(),
            Embed.Comp(() => new NavigationView
            {
                Header = "fluent-gpu",
                Initial = "welcome",
                Items = Items,
                Content = Page,   // each page is a distinct component type → the reconciler remounts it on navigation
            })
        ) with { Grow = 1 };

        return ZStack(shell, DiagnosticsOverlay()) with { Grow = 1 };
    }

    // Integrated title bar (transparent → window Mica shows through): app identity left, a centered search pill,
    // a right inset clearing the system caption buttons.
    static Element TitleBar() => new BoxEl
    {
        Direction = 0, Height = 48, AlignItems = FlexAlign.Center, Gap = 10, Padding = new Edges4(14, 0, 140, 0),
        Children =
        [
            Icon(Icons.Grid, 16f).Foreground(Tok.AccentDefault),
            new TextEl("fluent-gpu") { Size = 12f, Bold = true, Color = Tok.TextPrimary },
            new BoxEl { Grow = 1 },
            SearchBox(),
            new BoxEl { Grow = 1 },
        ],
    };

    // A search box (visual; text input is a future Text-subsystem capability). Mirrors the WinUI AutoSuggestBox.
    static Element SearchBox() => new BoxEl
    {
        Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, MaxWidth = 460, Grow = 1, Height = 30,
        Padding = new Edges4(11, 0, 11, 0), Corners = Radii.ControlAll,
        Fill = Tok.FillControlDefault, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
        HoverFill = Tok.FillControlSecondary,
        Children =
        [
            Icon(Icons.Search, 14f).Foreground(Tok.TextSecondary),
            Caption("Search controls and samples").Tertiary(),
        ],
    };

    static Element DiagnosticsOverlay() => new BoxEl
    {
        Direction = 0,
        Justify = FlexJustify.End,
        AlignItems = FlexAlign.Start,
        Padding = new Edges4(12, 56, 152, 0),
        Children = [Embed.Comp(() => new FrameDiagnosticsHud())],
    };

    static Element Page(string key) => key switch
    {
        "typography" => Embed.Comp(() => new TypographyPage()),
        "icons" => Embed.Comp(() => new IconsPage()),
        "buttons" => Embed.Comp(() => new ButtonsPage()),
        "inputs" => Embed.Comp(() => new InputsPage()),
        "flex" => Embed.Comp(() => new FlexPage()),
        "grid" => Embed.Comp(() => new GridPage()),
        "repeater" => Embed.Comp(() => new RepeaterPage()),
        "images" => Embed.Comp(() => new ImagesPage()),
        "scrolling" => Embed.Comp(() => new ScrollPage()),
        "virtualization" => Embed.Comp(() => new VirtualizationPage()),
        "animation" => Embed.Comp(() => new AnimationPage()),
        "compositor" => Embed.Comp(() => new CompositorPage()),
        "state" => Embed.Comp(() => new StatePage()),
        "wavee" => Embed.Comp(() => new WaveeShell()),
        _ => Embed.Comp(() => new WelcomePage()),
    };
}

sealed class FrameDiagnosticsHud : Component
{
    public override Element Render()
    {
        var stats = UseContext(FrameDiagnostics.Current);
        var (tick, setTick) = UseState(0);
        UseEffect(() => setTick((tick + 1) & 0x3FFF_FFFF), tick);

        string fps = stats.Fps <= 0.0 ? "--" : stats.Fps.ToString("0");
        string ms = stats.FrameMs <= 0.0 ? "--" : stats.FrameMs.ToString("0.0");

        return new BoxEl
        {
            Direction = 0,
            Gap = 10,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(10, 5, 10, 5),
            MinHeight = 30,
            Fill = ColorF.FromRgba(0x13, 0x15, 0x1A, 0xCC),
            BorderColor = Tok.StrokeSurfaceDefault,
            BorderWidth = 1f,
            Corners = Radii.ControlAll,
            Children =
            [
                Metric("fps", fps, Tok.AccentDefault),
                Metric("cmd", stats.DrawCommandCount.ToString(), Tok.TextPrimary),
                Metric("draw", stats.DrawNodeCount.ToString(), Tok.TextPrimary),
                Metric("cull", stats.CulledNodeCount.ToString(), Tok.TextPrimary),
                Metric("ms", ms, Tok.TextSecondary),
            ],
        };
    }

    static Element Metric(string label, string value, ColorF valueColor) => new BoxEl
    {
        Direction = 0,
        Gap = 4,
        AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
            new TextEl(value) { Size = 12f, Bold = true, Color = valueColor, FontFamily = "Cascadia Code" },
        ],
    };
}
