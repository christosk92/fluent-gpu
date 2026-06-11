using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Reconciler;
using static FluentGpu.Dsl.Ui;

// The Home page — mirrors the WinUI Gallery landing: a gradient hero, a Recent/Favorites selector, and a wrapping
// grid of sample cards that entrance-animate and navigate the shell on click.
sealed class WelcomePage : Component
{
    static readonly (string Key, string Glyph, string Title, string Sub)[] Demos =
    {
        ("state", Icons.Refresh, "State & components", "Hooks, reducers, context"),
        ("typography", Icons.Font, "Typography", "The Fluent type ramp"),
        ("icons", Icons.Star, "Iconography", "All 1,500+ Segoe Fluent glyphs"),
        ("flex", Icons.Tag, "Flexbox", "justify · align · grow · wrap"),
        ("grid", Icons.Grid, "CSS Grid", "Track-based + uniform grid"),
        ("repeater", Icons.List, "ItemsRepeater", "Data-driven, virtualized"),
        ("virtualization", Icons.List, "List virtualization", "100k rows, recycled"),
        ("buttons", Icons.Accept, "Buttons & commands", "Accent · standard · toggle"),
        ("inputs", Icons.Volume, "Inputs & sliders", "Sliders, scrollbars, toggles"),
        ("images", Icons.Picture, "Images", "Async album art + placeholders"),
        ("scrolling", Icons.Document, "Scrolling", "Smooth, inertial, auto-bars"),
        ("animation", Icons.Movie, "Animation", "Springs · keyframes · reflow · FLIP"),
        ("compositor", Icons.Brush, "Compositor", "GPU transform + opacity"),
        ("wavee", Icons.MusicNote, "Wavee skeleton", "The driving app"),
    };

    public override Element Render()
    {
        var (tab, setTab) = UseState("recent");
        var navigate = UseContext(NavigationView.Nav);

        var cards = new Element[Demos.Length];
        for (int i = 0; i < Demos.Length; i++)
        {
            int idx = i; var d = Demos[i];
            cards[i] = Embed.Comp(() => new SampleCard
            {
                Index = idx, Glyph = d.Glyph, Title = d.Title, Subtitle = d.Sub,
                OnOpen = () => navigate(d.Key),
            }) with { Key = d.Key };
        }

        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = 0,
            Children =
            [
                Embed.Comp(() => new HomeHero()),
                new BoxEl { Margin = new Edges4(36, 8, 36, 12), Children = [SelectorBar(tab, setTab)] },
                // Responsive: as many equal columns as fit at >=280 DIP, stretched to fill the width (reflows on resize).
                AutoGrid(280f, 12f, 88f, cards) with { Padding = new Edges4(36, 0, 36, 36) },
            ],
        });
    }

    // Stateless factory — selection is owned by this page (component props are mount-only, so a stateful SelectorBar
    // would freeze its Selected at mount).
    static Element SelectorBar(string selected, Action<string> onSelect)
        => HStack(6, Tab("recent", Icons.Clock, "Recent", selected, onSelect),
                     Tab("favorites", Icons.FavoriteStar, "Favorites", selected, onSelect));

    static Element Tab(string tag, string glyph, string label, string selected, Action<string> onSelect)
    {
        bool sel = selected == tag;
        return new BoxEl
        {
            Direction = 0, Gap = 6, AlignItems = FlexAlign.Center, Padding = new Edges4(14, 6, 14, 6), MinHeight = 34,
            Corners = Radii.PillAll, OnClick = () => onSelect(tag),
            Fill = sel ? Tok.AccentSubtle : Tok.FillSubtleTransparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Children =
            [
                Icon(glyph, 14f).Foreground(sel ? Tok.AccentDefault : Tok.TextSecondary),
                new TextEl(label) { Size = 14f, Color = sel ? Tok.TextPrimary : Tok.TextSecondary },
            ],
        };
    }
}

/// <summary>The gradient hero header (entrance-animated).</summary>
sealed class HomeHero : Component
{
    public override Element Render()
    {
        this.UseEntrance();
        return new BoxEl
        {
            Height = 200, Direction = 1, Justify = FlexJustify.End, Gap = 4, Padding = new Edges4(36, 0, 36, 22),
            Gradient = LinearGradient(118f, new GradientStop(0f, Tok.HeroGradientTop), new GradientStop(1f, Tok.HeroGradientBottom)),
            // The full-bleed hero sits at the NavigationView content's rounded top-left corner; gradients aren't rounded-
            // clipped by the content frame, so round the fill itself to match (Radii.Overlay = ContentLeftTopCorner).
            Corners = new(Radii.Overlay, 0f, 0f, 0f),
            Children =
            [
                Caption("WinUI-style capability gallery").Secondary(),
                new TextEl("fluent-gpu") { Size = 40f, Bold = true, Color = Tok.TextPrimary },
                Body("A from-scratch, GPU-rendered, near-zero-allocation UI framework for .NET — the React model over Direct3D 12.").Secondary(),
            ],
        };
    }
}

/// <summary>A WinUI ControlItemTemplate-style sample card (icon + title + caption), entrance-animated, clickable.</summary>
sealed class SampleCard : Component
{
    public int Index;
    public string Glyph = "", Title = "", Subtitle = "";
    public Action? OnOpen;

    public override Element Render()
    {
        this.UseEntrance(key: Index);
        return new BoxEl
        {
            Height = 88, Direction = 0, Gap = 14, AlignItems = FlexAlign.Center, Padding = Edges4.All(14),   // width = the grid column (responsive)
            Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
            HoverFill = Tok.FillCardSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = OnOpen,
            Children =
            [
                Icon(Glyph, 28f).Foreground(Tok.AccentDefault),
                new BoxEl { Direction = 1, Gap = 2, Grow = 1, Children = [BodyStrong(Title), Caption(Subtitle).Secondary()] },
            ],
        };
    }
}
