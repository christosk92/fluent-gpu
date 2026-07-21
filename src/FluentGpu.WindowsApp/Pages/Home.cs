using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Reconciler;
using static FluentGpu.Dsl.Ui;

// The Home page — mirrors the WinUI Gallery landing: a gradient hero, a Recent/Favorites selector, and a wrapping
// grid of sample cards that entrance-animate and navigate the shell on click.
[GalleryPage("welcome", "Home", "Home", Icon = Icons.Home)]
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
        ("Image", Icons.Picture, "Image", "Async art · object-fit · corners"),
        ("scrolling", Icons.Document, "Scrolling", "Smooth, inertial, auto-bars"),
        ("animation", Icons.Movie, "Animation", "Springs · keyframes · reflow · FLIP"),
        ("compositor", Icons.Brush, "Compositor", "GPU transform + opacity"),
        ("wavee", Icons.MusicNote, "Wavee skeleton", "The driving app"),
    };

    public override Element Render()
    {
        var (tab, setTab) = UseState("recent");
        var navigate = UseContext(NavigationView.Nav);

        // Recent = real visit history; Favorites = starred pages (both persisted via GalleryPrefs). Reading the signals
        // subscribes Home so a star toggle / new visit re-renders the active tab. Recent falls back to the curated demo
        // cards on first run (empty history) so the landing is never blank.
        var recent = GalleryPrefs.Recent.Value;
        var favs = GalleryPrefs.Favorites.Value;

        Element grid = tab == "favorites"
            ? (favs.Length > 0 ? TileGrid(favs, navigate) : EmptyState("No favorites yet", "Tap the star on any tile or page header to pin it here."))
            : (recent.Length > 0 ? TileGrid(recent, navigate) : CuratedGrid(navigate));

        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = 0,
            Children =
            [
                Embed.Comp(() => new HomeHero()),
                new BoxEl
                {
                    Margin = new Edges4(36, 8, 36, 12), Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [SelectorBar(tab, setTab), new BoxEl { Grow = 1f }, HyperlinkButton.Create("Browse all controls", () => navigate("all"))],
                },
                grid,
            ],
        });
    }

    // Registry-projected tiles for a set of page keys (Recent / Favorites) — the shared GalleryTile (badge + star).
    static Element TileGrid(string[] keys, Action<string> navigate)
    {
        var tiles = new Element[keys.Length];
        for (int i = 0; i < keys.Length; i++) { var k = keys[i]; tiles[i] = GalleryPage.TileFor(k, () => navigate(k)); }
        return AutoGrid(280f, 12f, float.NaN, tiles) with { Padding = new Edges4(36, 0, 36, 36) };
    }

    // The first-run landing: the curated, entrance-animated sample cards.
    static Element CuratedGrid(Action<string> navigate)
    {
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
        return AutoGrid(280f, 12f, 88f, cards) with { Padding = new Edges4(36, 0, 36, 36) };
    }

    static Element EmptyState(string title, string sub) => new BoxEl
    {
        Direction = 1, Gap = 6f, Padding = new Edges4(36, 24, 36, 36),
        Children = [BodyStrong(title), Caption(sub).Secondary()],
    };

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
            Corners = new CornerRadius4(Radii.Overlay, 0f, 0f, 0f),
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
