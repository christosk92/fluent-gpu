using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.GalleryKit;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Overview grids + registry-projected tiles (G8c2) ──────────────────────────────────────────────────────────────
// A top-level group header routes here (GalleryShell.Page): a registry-projected LANDING GRID of visual tiles — icon,
// title, one-liner (PageInfo.Subtitle), a difficulty badge (GalleryPageInfo.Level), and a Favorite star. The tiles are
// the ONE tile everything shares (Home Recent/Favorites, category overviews, the All-controls grid), so the badge/star
// upgrade lands everywhere at once. No hand-maintained page lists — the grid IS the registry.

/// <summary>Registry lookup helpers (the generated GalleryRegistry, indexed by key).</summary>
static class GalleryProjection
{
    static Dictionary<string, GalleryPageInfo>? _byKey;
    static Dictionary<string, GalleryPageInfo> ByKey =>
        _byKey ??= FluentGpu.Generated.GalleryRegistry.Pages.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public static GalleryPageInfo? Info(string key) => ByKey.TryGetValue(key, out var p) ? p : null;

    /// <summary>The difficulty facet for a page key (Basic when unregistered).</summary>
    public static GalleryLevel LevelOf(string key) => Info(key)?.Level ?? GalleryLevel.Basic;
}

/// <summary>A difficulty facet pill (Compose Material Catalog "difficulty badge"): color-coded Basic / Real-world /
/// Advanced. <paramref name="compact"/> drops the label to a dot+glyph for dense rows.</summary>
static class LevelBadge
{
    public static string Label(GalleryLevel level) => level switch
    {
        GalleryLevel.RealWorld => "Real-world",
        GalleryLevel.Advanced => "Advanced",
        _ => "Basic",
    };

    static (ColorF Bg, ColorF Fg) Colors(GalleryLevel level) => level switch
    {
        GalleryLevel.RealWorld => (Tok.AccentSubtle, Tok.AccentDefault),
        GalleryLevel.Advanced => (Tok.SystemFillCautionBackground, Tok.SystemFillCaution),
        _ => (Tok.FillSubtleSecondary, Tok.TextSecondary),
    };

    public static Element Of(GalleryLevel level)
    {
        var (bg, fg) = Colors(level);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(8, 2, 8, 2), Corners = Radii.PillAll, Fill = bg, AlignSelf = FlexAlign.Start,
            Children = [new TextEl(Label(level)) { Size = 11f, Bold = true, Color = fg }],
        };
    }
}

/// <summary>A star affordance that toggles the page's Favorite state (persisted via GalleryPrefs). A component so it
/// subscribes the favorites signal and re-renders the glyph on toggle; its own OnClick consumes the tap (the gesture
/// arena enrolls it innermost) so clicking the star never also opens the tile.</summary>
sealed class StarButton : Component
{
    public string PageKey = "";
    public float Size = 16f;

    public override Element Render()
    {
        bool fav = Array.IndexOf(GalleryPrefs.Favorites.Value, PageKey) >= 0;   // reading subscribes
        return new BoxEl
        {
            Width = Size + 14f, Height = Size + 14f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Focusable = true, Role = AutomationRole.Button,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => GalleryPrefs.ToggleFavorite(PageKey),
            Children = [Icon(fav ? Icons.FavoriteStarFill : Icons.FavoriteStar, Size).Foreground(fav ? Tok.AccentDefault : Tok.TextTertiary)],
        };
    }
}

/// <summary>The one registry-projected tile: icon + title + one-liner + difficulty badge + Favorite star, navigating on
/// click. Used by every gallery grid (Home Recent/Favorites, category overviews, All-controls).</summary>
sealed class GalleryTile : Component
{
    public string PageKey = "";
    public Action? OnOpen;
    public bool ShowLevel = true;

    public override Element Render()
    {
        var info = GalleryProjection.Info(PageKey);
        var meta = PageInfo.Find(PageKey);
        string title = (info?.Title.Length ?? 0) > 0 ? info!.Title : PageKey;
        string glyph = (info?.Icon.Length ?? 0) > 0 ? info!.Icon : Icons.Tag;
        string? subtitle = meta?.Subtitle;
        var level = info?.Level ?? GalleryLevel.Basic;

        var col = new List<Element>
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
                Children =
                [
                    new BoxEl { Grow = 1f, Children = [BodyStrong(title)] },
                    Embed.Comp(() => new StarButton { PageKey = PageKey }),
                ],
            },
        };
        if (subtitle is not null)
            col.Add(new TextEl(subtitle) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 });
        if (ShowLevel)
            col.Add(LevelBadge.Of(level));

        return new BoxEl
        {
            MinHeight = 96f, Direction = 0, Gap = 14f, AlignItems = FlexAlign.Start, Padding = Edges4.All(14),
            Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
            HoverFill = Tok.FillCardSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = OnOpen,
            Children =
            [
                new BoxEl
                {
                    Width = 44f, Height = 44f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Icon(glyph, 26f).Foreground(Tok.AccentDefault)],
                },
                new BoxEl { Grow = 1f, Direction = 1, Gap = 6f, Children = col.ToArray() },
            ],
        };
    }
}

/// <summary>The generic, registry-projected overview grid for a top-level category (the G8c2 group-header landing).
/// Fixes the Resolve-miss→Fallback bug at the root: a group NEVER falls through to Home.</summary>
sealed class OverviewPage : Component
{
    public string Category = "";

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var keys = GalleryShell.PagesInCategory(Category);
        var tiles = new Element[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            tiles[i] = Embed.Comp(() => new GalleryTile { PageKey = k, OnOpen = () => navigate(k) }) with { Key = k };
        }
        return GalleryPage.Shell(Category, GallerySections.Blurb(Category), AutoGrid(300f, 12f, float.NaN, tiles));
    }
}
