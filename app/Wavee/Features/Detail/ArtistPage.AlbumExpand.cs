using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// iTunes-style inline album expansion for the artist discography grids. Clicking an album card opens a FULL-WIDTH track
// drawer directly after that album's ROW (so the row's neighbours stay put and the rows below slide down), revealing the
// album's tracks in place — no navigation. Clicking the album again collapses it; clicking another moves the drawer.
//
// Layout: a self-measuring responsive grid (one component owns the measured width AND the expanded-uri state, so both a
// resize and a toggle rebuild it). The column count is derived from the measured width; each row is a flex row of equal
// cells (the last row padded so card width stays uniform). The drawer is a sibling inserted between rows, full width.
sealed class ExpandableAlbumGrid : Component
{
    readonly IReadOnlyList<Album> _albums;
    readonly Services _svc;
    readonly Action<string, string?> _go;
    readonly Action<string> _play;
    const float MinCol = 180f;
    static readonly float Gap = WaveeSpace.M;

    readonly Signal<string?> _expanded = new(null);

    public ExpandableAlbumGrid(IReadOnlyList<Album> albums, Services svc, Action<string, string?> go, Action<string> play)
    { _albums = albums; _svc = svc; _go = go; _play = play; }

    // The engine's AutoGrid (GridEl, repeat(auto-fill, minmax(MinCol, 1fr))) owns ALL the sizing — responsive column count
    // + equal card widths, resolved against the live width, no hand-computed widths. For the iTunes inline drawer we split
    // the album list at the expanded item's ROW boundary and drop the drawer between two AutoGrids; both grids resolve the
    // SAME column count from the same width (GridColCount: floor((w+gap)/(MinCol+gap))) so the two halves stay aligned.
    // Responsive.Of supplies the width ONLY to find that split row; the grids themselves size on their own. _expanded is
    // read inside the build (ResponsiveBox's reactive scope), so a toggle rebuilds just this section.
    public override Element Render() => Responsive.Of(BuildGrid, fallback: 900f);

    Element BuildGrid(float w)
    {
        string? expanded = _expanded.Value;                  // subscribe (ResponsiveBox re-renders on expand/collapse)
        int idx = expanded is null ? -1 : IndexOf(expanded);
        if (idx < 0) return Grid("agrid-before", 0, _albums.Count);   // collapsed → one grid (shares the before-grid key)

        int cols = Math.Max(1, (int)((w + Gap) / (MinCol + Gap)));   // mirrors FlexLayout.GridColCount
        int rowEnd = Math.Min(((idx / cols) + 1) * cols, _albums.Count);   // first index AFTER the expanded item's row

        // STABLE keys: the drawer + both grids persist across an album switch, so switching A→B resizes/moves the SAME
        // drawer (it reads _expanded reactively and Size-reflows) instead of unmount→remount — no collapse-then-reshow.
        var children = new List<Element>(3)
        {
            Grid("agrid-before", 0, rowEnd),
            Embed.Comp(() => new AlbumDrawer(_albums, _expanded, _svc, _go, _play)) with { Key = "albumdrawer" },
        };
        if (rowEnd < _albums.Count) children.Add(Grid("agrid-after", rowEnd, _albums.Count));
        return new BoxEl { Direction = 1, Gap = Gap, Children = children.ToArray() };
    }

    // An AutoGrid over _albums[from..to) with a STABLE key so the before/after halves reconcile their cells in place
    // (matched by index) across a switch instead of remounting.
    Element Grid(string key, int from, int to)
    {
        var cells = new Element[to - from];
        for (int i = from; i < to; i++) cells[i - from] = Cell(_albums[i]);
        return AutoGrid(MinCol, Gap, float.NaN, cells) with { Key = key };
    }

    Element Cell(Album al)
    {
        string subtitle = al.Year > 0 ? al.Year + "  " + ArtistPage.KindLabel(al.Kind) : ArtistPage.KindLabel(al.Kind);
        // The grid track sizes the card (GridCard's cover fills its cell, CSS aspect-ratio 1) — nothing hand-sized here.
        // Click toggles the inline drawer (the iTunes affordance); the cover's hover FAB still plays; the drawer header
        // links to the full album page, so navigation stays reachable.
        return MediaCard.GridCard(al.Cover, al.Name, subtitle, al.Uri,
            onClick: () => _expanded.Value = _expanded.Peek() == al.Uri ? null : al.Uri,
            onPlay: () => _play(al.Uri),
            onNavigate: () => _go("album:" + al.Uri, al.Name));
    }

    int IndexOf(string uri)
    {
        for (int i = 0; i < _albums.Count; i++) if (_albums[i].Uri == uri) return i;
        return -1;
    }
}

// The full-width track drawer — ONE persistent node (stable key) shared across album switches. It reads the expanded uri
// from the signal and resolves the album, so switching A→B swaps content IN PLACE; the Size/Reflow transition eases the
// height (e.g. 3→2 tracks resizes down, the rows below reflow) instead of the node collapsing and re-showing. A header
// repeats the album name (→ open the full page), year/kind, and a play-all; each row plays the album from that track.
sealed class AlbumDrawer : Component
{
    readonly IReadOnlyList<Album> _albums;
    readonly Signal<string?> _expanded;
    readonly Services _svc;
    readonly Action<string, string?> _go;
    readonly Action<string> _play;
    Album? _last;   // keeps the last album during the close frame so the EXIT animates with real content, not an empty box
    public AlbumDrawer(IReadOnlyList<Album> albums, Signal<string?> expanded, Services svc, Action<string, string?> go, Action<string> play)
    { _albums = albums; _expanded = expanded; _svc = svc; _go = go; _play = play; }

    // Position + Opacity drive enter (open) / exit (close); Size = Reflow eases the height when the content changes on a
    // switch, with the rows below reflowing through real layout each tick (no snap).
    static readonly LayoutTransition Reveal = new(
        TransitionChannels.Opacity | TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Spring(0.32f, 0.92f),
        Size: SizeMode.Reflow,
        Enter: new EnterExit(Dy: -10f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -10f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.SmoothOut));

    public override Element Render()
    {
        string? uri = _expanded.Value;                       // subscribe → content swaps in place when the album changes
        var album = uri is not null ? Find(uri) : null;
        if (album is not null) _last = album;
        var show = album ?? _last;
        // Fetch the full album (the discography album is thin — no tracklist). Unconditional hook (stable order); keyed by
        // uri so it re-fetches per album. Cached by GetAlbumAsync, so a re-expand is instant.
        var full = UseAsyncResource(
            ct => show is null ? System.Threading.Tasks.Task.FromResult<Album?>(null) : _svc.Library.GetAlbumAsync(show.Uri, ct),
            (Album?)null, show?.Uri ?? "");
        if (show is null) return new BoxEl();

        var ts = (full.Value.Value?.Tracks ?? show.Tracks) ?? System.Array.Empty<Track>();
        Element body = ts.Count > 0 ? Rows(show, ts)
                     : full.State.Value == (byte)LoadState.Pending ? ShimmerRows(show.TrackCount)
                     : EmptyNote();
        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.S, Animate = Reveal,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card), ClipToBounds = true,
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = [ Header(show), body ],
        };
    }

    Element Header(Album album) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(0f, 0f, 0f, WaveeSpace.XS),
        Children =
        [
            new BoxEl   // play all (album context from the top)
            {
                Width = 36f, Height = 36f, Shrink = 0f, Corners = CornerRadius4.All(18f), Fill = Tok.AccentDefault,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverScale = 1.06f, PressScale = 0.94f,
                OnClick = () => _play(album.Uri),
                Children = [ Icon(Icons.Play, 14f, Tok.TextOnAccentPrimary) ],
            },
            new BoxEl   // album name → open the full album page
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, OnClick = () => _go("album:" + album.Uri, album.Name),
                Children =
                [
                    new TextEl(album.Name) { Size = 15f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(album.Year > 0 ? album.Year + "  " + ArtistPage.KindLabel(album.Kind) : ArtistPage.KindLabel(album.Kind))
                        { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
            AlbumNavAction.Create(() => _go("album:" + album.Uri, album.Name)),
        ],
    };

    Element Rows(Album album, IReadOnlyList<Track> tracks)
    {
        var rows = new Element[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) rows[i] = Row(album, i + 1, tracks[i]);
        return new BoxEl { Direction = 1, Children = rows };
    }

    Element Row(Album album, int num, Track t) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Height = 40f,
        Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f), Corners = CornerRadius4.All(6f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => _ = _svc.Player.PlayAsync(album.Uri, num - 1),
        Children =
        [
            new TextEl(num.ToString()) { Width = 24f, Size = 13f, Color = Tok.TextTertiary },
            new TextEl(t.Title) { Grow = 1f, Basis = 0f, Size = 14f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(Dur(t.DurationMs)) { Size = 12f, Color = Tok.TextSecondary },
        ],
    };

    static Element EmptyNote() => new BoxEl
    {
        Padding = new Edges4(WaveeSpace.S, WaveeSpace.M, WaveeSpace.S, WaveeSpace.M),
        Children = [ new TextEl(Loc.Get(Strings.Search.NoResults)) { Size = 13f, Color = Tok.TextTertiary } ],
    };

    // Skeleton rows while the album's tracklist loads (the count from the thin album's TrackCount).
    static Element ShimmerRows(int count)
    {
        int n = Math.Clamp(count, 3, 10);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
            rows[i] = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Height = 40f,
                Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f),
                Children =
                [
                    new BoxEl { Width = 18f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Grow = 1f, Basis = 0f, Height = 12f, MaxWidth = 260f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Width = 32f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                ],
            };
        return new BoxEl { Direction = 1, Children = rows };
    }

    Album? Find(string uri)
    {
        for (int i = 0; i < _albums.Count; i++) if (_albums[i].Uri == uri) return _albums[i];
        return null;
    }

    static string Dur(long ms) { var t = TimeSpan.FromMilliseconds(ms); return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}"; }
}

// ── Discography facets (Albums / Singles / Compilations) ───────────────────────────────────────────────
// The artist page shows a collapsible header + the first Cap items (DiscographySection → DiscoGrid), then a "See all N"
// link that NAVIGATES to the full facet page (DiscographyPage). The grid is virtualized over a VirtualCollection<Album>
// that pages in on scroll, with iTunes-style inline track drawers — shared by the section (capped) and the full page.

static class AlbumNavAction
{
    public static Element Create(Action onClick, float size = 34f) => ToolTip.Wrap(new BoxEl
    {
        Width = size, Height = size, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(size / 2f),
        Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        OnClick = onClick, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
        Children = [ Icon(Icons.OpenInNewWindow, size * 0.42f, Tok.TextSecondary) ],
    }, "Go to album");
}

// Builds the paged data source for one (artist, facet): pages of 60; the source reports the facet total from page 0.
static class DiscoVc
{
    public static VirtualCollection<Album> Make(Services svc, string artistUri, DiscographyKind kind, Action<Action> post)
        => new(async (off, cnt, ct) =>
        {
            var p = await svc.Library.GetDiscographyAsync(artistUri, kind, off, cnt, ct);
            var arr = p.Items as Album[] ?? p.Items.ToArray();
            return new PageResult<Album>(p.Total, arr);
        }, pageSize: 60, post: post);
}

// The reusable, virtualized discography grid over a (host-owned) VirtualCollection<Album>, with iTunes-style inline track
// drawers. cap > 0 limits the rendered count (artist page); cap == 0 renders the whole facet (the dedicated facet page).
// The DiscoGrid expand drawer body: the discography album is THIN (no tracklist), so this FETCHES the full album on
// expand (cached by GetAlbumAsync) and shimmers the right number of rows while it loads — then reveals the real rows.
sealed class AlbumDrawerPanel : Component
{
    readonly Services _svc; readonly Album _thin; readonly float _panelH;
    readonly Action<string> _play; readonly Action<string, string?> _go;
    readonly ColorF _accent;
    public AlbumDrawerPanel(Services svc, Album thin, float panelH, Action<string> play, Action<string, string?> go, ColorF accent)
    { _svc = svc; _thin = thin; _panelH = panelH; _play = play; _go = go; _accent = accent; }

    static string Dur(long ms) { var t = TimeSpan.FromMilliseconds(ms); return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}"; }

    public override Element Render()
    {
        var full = UseAsyncResource(ct => _svc.Library.GetAlbumAsync(_thin.Uri, ct), (Album?)null, _thin.Uri);
        var tracks = (full.Value.Value?.Tracks ?? _thin.Tracks) ?? System.Array.Empty<Track>();
        bool loading = tracks.Count == 0 && full.State.Value == (byte)LoadState.Pending;
        int n = loading ? Math.Clamp(_thin.TrackCount, 1, 10) : Math.Min(tracks.Count, 10);

        var rows = new Element[n];
        for (int i = 0; i < n; i++) rows[i] = loading ? ShimmerRow() : Row(i + 1, tracks[i]);

        return new BoxEl
        {
            Direction = 1, Height = _panelH, ClipToBounds = true,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.S, WaveeSpace.L, WaveeSpace.S),
            Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = [ Head(), .. rows ],
        };
    }

    Element Head() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Height = 40f,
        Children =
        [
            new BoxEl { Width = 30f, Height = 30f, Shrink = 0f, Corners = CornerRadius4.All(15f), Fill = _accent,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, OnClick = () => _play(_thin.Uri),
                Children = [ Icon(Icons.Play, 12f, Tok.TextOnAccentPrimary) ] },
            new BoxEl { Grow = 1f, Basis = 0f, OnClick = () => _go("album:" + _thin.Uri, _thin.Name),
                Children = [ new TextEl(_thin.Name) { Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } ] },
            AlbumNavAction.Create(() => _go("album:" + _thin.Uri, _thin.Name), 32f),
        ],
    };

    Element Row(int num, Track t) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Height = 34f,
        Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f), Corners = CornerRadius4.All(6f), HoverFill = Tok.FillSubtleSecondary,
        OnClick = () => _ = _svc.Player.PlayAsync(_thin.Uri, num - 1),
        Children =
        [
            new TextEl(num.ToString()) { Width = 22f, Size = 12f, Color = Tok.TextTertiary },
            new TextEl(t.Title) { Grow = 1f, Basis = 0f, Size = 13f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(Dur(t.DurationMs)) { Size = 11f, Color = Tok.TextSecondary },
        ],
    };

    static Element ShimmerRow() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Height = 34f, Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f),
        Children =
        [
            new BoxEl { Width = 16f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Grow = 1f, Basis = 0f, Height = 11f, MaxWidth = 240f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Width = 30f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
        ],
    };
}

sealed class DiscoGrid : Component
{
    readonly VirtualCollection<Album> _vc;
    readonly Services _svc;
    readonly Action<string, string?> _go;
    readonly Action<string> _play;
    readonly int _cap;
    readonly int _initialIndex;
    readonly ColorF _accent;
    readonly Signal<int> _expanded = new(-1);

    const float MinCol = 180f;
    static readonly float Gap = WaveeSpace.L;          // column gap (the vertical row gap is RowGap, folded into rowExtra)

    // Uniform-card geometry → predictable drawer spacing. Cards are forced to ONE height (square cover + chrome) so every
    // row has the SAME slack; the drawer can then hug the clicked card with a known gap above it and breathe below.
    const float CardChrome = 70f;       // a GridCard's height ABOVE the square cover (2-line title + paddings)
    const float RowGap     = 20f;       // vertical gap between card rows  (rowExtra = CardChrome + RowGap)
    const float TopGap     = 8f;        // expanded card → drawer (hug it)
    const float BottomGap  = 30f;       // drawer → next row (breathing room below it)
    const float DrawerLift = RowGap - TopGap;              // paint pull-up: the drawer rises into the row slack to hug the card
    const float SlotExtra  = TopGap + BottomGap - RowGap;  // reserved drawer slot beyond the panel → becomes the bottom room

    // The inline drawer's open/close + switch motion. Opacity+Position animate the enter (drop-in) / exit (lift-out);
    // Size=Reflow eases the height when switching to an album with a different track count (the rows below reflow).
    static readonly LayoutTransition DrawerReveal = new(
        TransitionChannels.Opacity | TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(280f, Easing.SmoothOut),
        Size: SizeMode.Reflow,
        Enter: new EnterExit(Dy: -8f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -8f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Spring(0.24f, 1f));

    public DiscoGrid(VirtualCollection<Album> vc, Services svc, Action<string, string?> go, Action<string> play, int cap, int initialIndex = 0, ColorF? accent = null)
    { _vc = vc; _svc = svc; _go = go; _play = play; _cap = cap; _initialIndex = initialIndex; _accent = accent ?? Tok.AccentDefault; }

    public override Element Render() => Embed.Comp(() => new LazyGrid(
        // Capped to _cap when >0 (the artist page); cap 0 → the full facet. The count delegate reads the live total.
        count: () => { _ = _vc.Version.Value; int t = _vc.CountOr0; return _cap > 0 ? Math.Min(_cap, t) : t; },
        cell: Cell,
        ensureRange: (f, l) => _vc.EnsureRange(f, l - 1),
        minColWidth: MinCol, gap: Gap, rowExtra: CardChrome + RowGap, overscanRows: 2,
        expanded: _expanded,
        drawer: DrawerFor,
        drawerHeight: DrawerHeight,
        initialIndex: _initialIndex));

    Element Cell(int idx, float cardW)
    {
        var al = _vc![idx];
        if (al is null) return Placeholder(cardW);
        string subtitle = al.Year > 0 ? al.Year + "  " + ArtistPage.KindLabel(al.Kind) : ArtistPage.KindLabel(al.Kind);
        Element card = MediaCard.GridCard(al.Cover, al.Name, subtitle, al.Uri,
            onClick: () => _expanded.Value = _expanded.Peek() == idx ? -1 : idx,
            onPlay: () => _play(al.Uri),
            onNavigate: () => _go("album:" + al.Uri, al.Name));
        if (card is BoxEl b)
        {
            // Force ONE height (square cover + chrome) so every card is uniform → the drawer's hug spacing is exact.
            b = b with { Height = cardW + CardChrome };
            // Highlight the expanded card (accent border + brighter fill) so it's unmistakably the drawer's owner —
            // pairs with the connector bar at the drawer's top edge.
            if (_expanded.Peek() == idx)
                b = b with { BorderColor = _accent, BorderWidth = 2f, Fill = Tok.FillCardDefault };
            card = b;
        }
        return card;
    }

    // A self-sizing shimmer cell, SAME height as a real card: the cover fills the (engine-laid-out) cell width and squares
    // itself via AspectRatio — no hardcoded width, so it tracks the real card exactly. The bars stretch to the cell width.
    static Element Placeholder(float cardW) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.S, Height = cardW + CardChrome,
        Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.S, WaveeSpace.M),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            // Fluid square cover: Width left NaN + AspectRatio 1f → fills the engine-laid-out cell width and derives its
            // height (the same self-sizing the real ArtworkFill cover uses) — no hardcoded dimensions.
            new ImageEl { Source = "", AspectRatio = 1f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(WaveeRadius.Card), Placeholder = Tok.FillSubtleSecondary },
            new BoxEl { Height = 13f, AlignSelf = FlexAlign.Stretch, MaxWidth = 150f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 11f, Width = 92f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
        ],
    };

    float PanelHeight(int idx) { int n = Math.Min(_vc?[idx]?.TrackCount ?? 0, 10); return 56f + n * 34f; }
    // Reserved slot the windowing math accounts for = panel + the bottom breathing room (the panel is pulled UP by
    // DrawerLift to hug the card, so the slack falls BELOW it as BottomGap).
    float DrawerHeight(int idx) => PanelHeight(idx) + SlotExtra;

    Element DrawerFor(int idx, GridDrawerInfo info)
    {
        var al = _vc?[idx];
        if (al is null) return new BoxEl();
        // The discography album is THIN (Tracks null) — the drawer fetches the full album on expand + shimmers while it
        // loads. Keyed by uri so each album gets its own fetch (the slot is reused across expands).
        var panel = Embed.Comp(() => new AlbumDrawerPanel(_svc, al, PanelHeight(idx), _play, _go, _accent)) with { Key = "drawer:" + al.Uri };

        // Connector: a short accent bar at the panel's top edge, spanning exactly the expanded card's column → reads as
        // "this drawer belongs to THAT card" (paired with the card's accent border).
        var connector = new BoxEl
        {
            Direction = 0, HitTestVisible = false,
            Children =
            [
                new BoxEl { Width = MathF.Max(0f, info.Left), Height = 0f },
                new BoxEl { Width = info.CellWidth, Height = 3f, Corners = CornerRadius4.All(1.5f), Fill = _accent },
            ],
        };

        // The OUTER slot owns the transition because its height participates in parent layout. Its negative top margin
        // moves the clipping box into the row's spare gap while preserving the exact reserved contribution:
        // (panel + BottomGap) - DrawerLift == panel + SlotExtra. The stable key keeps ONE node across
        // A→B switches (Position/Size-reflow); a true open/close runs the enter/exit channels.
        var inner = new BoxEl { ZStack = true, Children = [ panel, connector ] };
        return new BoxEl
        {
            // MinHeight neutralizes the negative margin at the zero-size start. The panel begins fully clipped, while
            // following rows start collapsed and move as SizeMode.Reflow grows the real layout slot.
            Key = "disco-drawer", Direction = 1,
            Height = PanelHeight(idx) + BottomGap, MinHeight = DrawerLift,
            Margin = new Edges4(0f, -DrawerLift, 0f, 0f),
            ClipToBounds = true, Animate = DrawerReveal,
            Children = [ inner ],
        };
    }

    static string Dur(long ms) { var t = TimeSpan.FromMilliseconds(ms); return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}"; }
}

// One artist-page discography facet: a collapsible header + the first Cap items (DiscoGrid), and a "See all N {facet}" link
// that NAVIGATES to the full facet page — never an in-place reveal (DiscographyRoute → DiscographyPage).
sealed class DiscographySection : Component
{
    readonly string _artistUri, _artistName, _title;
    readonly DiscographyKind _kind;
    readonly Services _svc;
    readonly Action<string, string?> _go;
    readonly Action<string> _play;
    readonly ColorF _accent;
    readonly Signal<bool> _collapsed = new(false);
    VirtualCollection<Album>? _vc;

    const int Cap = DiscographyRoute.PreviewCap;

    public DiscographySection(string artistUri, string artistName, DiscographyKind kind, string title, Services svc, Action<string, string?> go, Action<string> play, ColorF accent)
    { _artistUri = artistUri; _artistName = artistName; _kind = kind; _title = title; _svc = svc; _go = go; _play = play; _accent = accent; }

    public override Element Render()
    {
        var post = UsePost();
        _vc ??= DiscoVc.Make(_svc, _artistUri, _kind, post);
        _ = _vc.Version.Value;                       // subscribe → header count + "See all N" update as the facet loads
        int total = _vc.CountOr0;
        bool collapsed = _collapsed.Value;

        var children = new List<Element>(3) { Header(total, collapsed) };
        if (!collapsed)
        {
            children.Add(Embed.Comp(() => new DiscoGrid(_vc!, _svc, _go, _play, cap: Cap, accent: _accent)));
            if (total > Cap) children.Add(SeeAllButton(total));
        }
        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.M, Padding = new Edges4(0f, 0f, 0f, WaveeSpace.XXL),
            Children = children.ToArray(),
        };
    }

    Element Header(int total, bool collapsed) => new BoxEl
    {
        // Left padding 0 so the accent bar aligns with the non-collapsible AccentHeader sections (Top tracks / Appears on).
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f,
        Corners = CornerRadius4.All(6f), HoverFill = Tok.FillSubtleSecondary,
        Padding = new Edges4(0f, WaveeSpace.XS, WaveeSpace.S, WaveeSpace.XS),
        OnClick = () => _collapsed.Value = !_collapsed.Peek(),
        Children =
        [
            new BoxEl { Width = 3f, MinHeight = 22f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(1.5f), Fill = _accent },
            WaveeType.RailHeader(_title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            total > 0 ? new TextEl(total.ToString()) { Size = 15f, Weight = 600, Color = Tok.TextTertiary } : new BoxEl(),
            new BoxEl { Grow = 1f },
            Icon(collapsed ? Icons.ChevronDown : Icons.ChevronUp, 14f, Tok.TextSecondary),
        ],
    };

    // Full-width "See all N {facet}" link → navigates to the dedicated facet page (breadcrumb + the whole grid).
    Element SeeAllButton(int total) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Height = 56f,
        Padding = new Edges4(WaveeSpace.L, 0f, WaveeSpace.M, 0f), Corners = CornerRadius4.All(WaveeRadius.Card),
        Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        OnClick = () => _go(DiscographyRoute.Make(_kind, _artistUri), _artistName),
        Children =
        [
            new TextEl($"See all {total} {DiscographyRoute.FacetWord(_kind, total)}") { Grow = 1f, Basis = 0f, Size = 14f, Weight = 700, Color = Tok.TextPrimary },
            Icon(Icons.ChevronRight, 16f, Tok.TextSecondary),
        ],
    };
}
