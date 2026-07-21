using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Shared page scaffolding (WinUI Gallery look) ──────────────────────────────────
static class GalleryPage
{
    /// <summary>Standard page scaffold. <paramref name="title"/> doubles as the PageInfo key (control pages use the
    /// control name); pages whose display title differs from their nav key use <see cref="ShellKeyed"/>.</summary>
    public static Element Shell(string title, string description, params Element[] body)
        => ShellKeyed(title, title, description, body);

    public static Element ShellKeyed(string key, string title, string description, params Element[] body)
    {
        var meta = PageInfo.Find(key);
        var header = PageInfo.HeaderFor(title, description, meta);
        string[]? related = meta is not null ? PageInfo.RoutableRelated(meta) : null;
        return GalleryScaffold.Page(header, related, body);
    }

    /// <summary>A bold readout whose text rides a signal-reading thunk — only the text node updates (no page re-render).</summary>
    public static TextEl LiveText(Func<string> text) => new(text) { Size = 14f, Bold = true, Color = Tok.TextPrimary };

    // A clickable tile (image or glyph + title + optional subtitle) that navigates the shell — the WinUI
    // ControlItemTemplate card. Used by the overview / All / home pages.
    public static Element Tile(string title, string? image, string? glyph, Action onOpen, string? subtitle = null) => new BoxEl
    {
        Height = 90f, Direction = 0, Gap = 14f, AlignItems = FlexAlign.Center, Padding = Edges4.All(14),
        Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
        HoverFill = Tok.FillCardSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onOpen,
        Children =
        [
            image is not null
                ? Image(Assets.ControlImage(image), 48f, 48f, 6f)
                : new BoxEl { Width = 48f, Height = 48f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(glyph ?? Icons.Tag, 28f).Foreground(Tok.AccentDefault)] },
            subtitle is null
                ? new BoxEl { Grow = 1f, Children = [BodyStrong(title)] }
                : new BoxEl
                {
                    Grow = 1f, Direction = 1, Gap = 2f, Justify = FlexJustify.Center,
                    Children = [BodyStrong(title), new TextEl(subtitle) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 }],
                },
        ],
    };

    /// <summary>A tile for a registered page key — image/subtitle pulled from the PageInfo registry.</summary>
    public static Element TileFor(string key, Action onOpen)
    {
        var meta = PageInfo.Find(key);
        return Tile(key, meta?.Image, null, onOpen, meta?.Subtitle);
    }

    /// <summary>The tile grid for one nav category (the WinUI category overview page body).</summary>
    public static Element CategoryGrid(string category, Action<string> navigate)
    {
        var keys = GalleryShell.CategoryKeys(category);
        var tiles = new Element[keys.Length];
        for (int i = 0; i < keys.Length; i++) { var k = keys[i]; tiles[i] = TileFor(k, () => navigate(k)); }
        return AutoGrid(300f, 12f, 90f, tiles);
    }
}

// ── Overview / category pages ─────────────────────────────────────────────────────
[GalleryPage("fundamentals", "Fundamentals", "Overview", Hidden = true)]
sealed class FundamentalsPage : Component
{
    // The engine model — kept in lockstep with the "fundamentals" nav group's children (Gallery.Items).
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("state", Icons.Refresh, "State & components"), ("flex", Icons.Tag, "Flexbox"), ("grid", Icons.Grid, "CSS Grid"),
        ("repeater", Icons.List, "ItemsRepeater"), ("virtualization", Icons.List, "List virtualization"),
        ("animation", Icons.Movie, "Animation"), ("compositor", Icons.Brush, "Compositor"),
        ("edge-fade", Icons.Brush, "Edge fade"), ("scrolling", Icons.Document, "Scrolling"),
    };

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var tiles = new Element[Items.Length];
        for (int i = 0; i < Items.Length; i++) { var d = Items[i]; tiles[i] = GalleryPage.Tile(d.Title, null, d.Glyph, () => navigate(d.Key)); }
        return GalleryPage.Shell("Fundamentals", "The engine model — the React/Reactor surface, layout, virtualization, and the motion/compositor pipeline.",
            AutoGrid(300f, 12f, 90f, tiles));
    }
}

[GalleryPage("patterns", "Patterns", "Overview", Hidden = true)]
sealed class PatternsPage : Component
{
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("motion-recipes", Icons.Movie, "Motion recipes"), ("async-skeletons", Icons.Refresh, "Async & skeletons"),
    };

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var tiles = new Element[Items.Length];
        for (int i = 0; i < Items.Length; i++) { var d = Items[i]; tiles[i] = GalleryPage.Tile(d.Title, null, d.Glyph, () => navigate(d.Key)); }
        return GalleryPage.Shell("Patterns", "UX recipes built on the engine — the Expressive Motion Kit and skeleton/shimmer-while-loading.",
            AutoGrid(300f, 12f, 90f, tiles));
    }
}

[GalleryPage("app-services", "App services", "Overview", Hidden = true)]
sealed class AppServicesPage : Component
{
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("localization", Icons.Globe, "Localization"), ("validation-guide", Icons.Accept, "Validation"),
        ("windowsapi", Icons.Globe, "Windows APIs"),
    };

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var tiles = new Element[Items.Length];
        for (int i = 0; i < Items.Length; i++) { var d = Items[i]; tiles[i] = GalleryPage.Tile(d.Title, null, d.Glyph, () => navigate(d.Key)); }
        return GalleryPage.Shell("App services", "Engine features WinUI lacks — JSON/ICU localization, signals-native form validation, and the OS-services pillars.",
            AutoGrid(300f, 12f, 90f, tiles));
    }
}

[GalleryPage("design", "Design", "Overview", Hidden = true)]
sealed class DesignPage : Component
{
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("typography", Icons.Font, "Typography"), ("icons", Icons.Star, "Iconography"),
    };

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var tiles = new Element[Items.Length];
        for (int i = 0; i < Items.Length; i++) { var d = Items[i]; tiles[i] = GalleryPage.Tile(d.Title, null, d.Glyph, () => navigate(d.Key)); }
        return GalleryPage.Shell("Design", "Design guidance — the Fluent type ramp and iconography.", AutoGrid(300f, 12f, 90f, tiles));
    }
}

[GalleryPage("basic-input", "Basic input", "Overview", Hidden = true)]
sealed class BasicInputOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Basic input", "Buttons, selection, and value controls — the WinUI Gallery Basic input set, built on the engine's controls layer.",
            GalleryPage.CategoryGrid("Basic input", navigate));
    }
}

[GalleryPage("all", "All controls", "Overview", Hidden = true)]
sealed class AllControlsPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var sections = new List<Element>();
        foreach (var (title, keys) in GalleryShell.ControlCatalog)
        {
            sections.Add(new BoxEl { Height = 12f });
            sections.Add(Subtitle(title));
            var tiles = new Element[keys.Length];
            for (int i = 0; i < keys.Length; i++) { var k = keys[i]; tiles[i] = GalleryPage.TileFor(k, () => navigate(k)); }
            sections.Add(AutoGrid(300f, 12f, 90f, tiles));
        }
        return GalleryPage.Shell("All controls", "Every control in the gallery, grouped by category.", sections.ToArray());
    }
}
