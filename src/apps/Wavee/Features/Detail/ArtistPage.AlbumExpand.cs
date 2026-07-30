using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// iTunes-style inline album expansion for the artist discography grids: DiscographySection → DiscoGrid →
// AlbumDrawerPanel. Clicking an album card opens a FULL-WIDTH track drawer directly after that album's ROW (so the
// row's neighbours stay put and the rows below slide down), revealing the album's tracks in place — no navigation.
// Clicking the album again collapses it; clicking another moves the drawer. The grid is the virtualized LazyGrid
// (DiscoGrid owns the expanded index + the one full-album fetch); the drawer body is AlbumDrawerPanel, fed LIVE via
// re-pushed props. (The earlier non-virtualized ExpandableAlbumGrid/AlbumDrawer pair is deleted — DiscoGrid subsumed it.)

// Task<T> is invariant, so the Task<Album> the catalog returns is not a Task<Album?> — and the UseResource seed pins
// T = Album?. Awaiting and re-wrapping is the conversion; the loaders below all go through it.
static class AlbumLoader
{
    internal static async System.Threading.Tasks.Task<Album?> LoadAlbumAsync(Services svc, string uri, System.Threading.CancellationToken ct)
        => await svc.Library.GetAlbumAsync(uri, ct).ConfigureAwait(false);
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
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        OnClick = onClick, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
        Children = [ Icon(Icons.OpenInNewWindow, size * 0.42f, Tok.TextSecondary) ],
    }.Interactive(Interaction.Subtle), "Go to album");
}

// Builds the paged data source for one (artist, facet): pages of 60; the source reports the facet total from page 0.
static class DiscoVc
{
    public static VirtualCollection<Album> Make(Services svc, string artistUri, DiscographyKind kind, Action<Action> post, System.Threading.CancellationToken ct)
        => new(async (off, cnt, c) =>
        {
            var p = await svc.Library.GetDiscographyAsync(artistUri, kind, off, cnt, c);
            var arr = p.Items as Album[] ?? p.Items.ToArray();
            return new PageResult<Album>(p.Total, arr);
        }, pageSize: 60, post: post, ct: ct);
}

// The DiscoGrid expand drawer body. The discography album is THIN (no tracklist); the one full-album fetch lives in
// DiscoGrid (where the drawer SLOT is sized), and this panel receives the album + tracks + row/state verdict as
// RE-PUSHED props — so the reserved height and the rendered rows derive from the SAME DrawerState and cannot disagree.
sealed class AlbumDrawerPanel : Component
{
    readonly Services _svc;
    readonly Action<string> _play; readonly Action<string, string?> _go;
    readonly Func<ColorF> _accent;
    readonly SelectionModel _sel = new();
    readonly SwipeGroup _swipeGroup = new();
    readonly Func<bool> _showChecks;
    // Non-reactive fields written from Render (the existing `_rows` pattern): the frozen selection-bar / context-menu
    // lambdas below read THESE, so they always see the current album/tracks without re-registration.
    Album _thin = null!;
    IReadOnlyList<Track> _rows = Array.Empty<Track>();

    /// <summary>LIVE drawer slots re-pushed from DiscoGrid on every render (the SelectorBar/ToolTip props idiom). The
    /// panel's rows and loading/empty state all change AFTER mount (Pending → Ready on the same uri), and ctor args
    /// freeze — which is exactly how the old frozen <c>panelH</c> ctor argument locked the panel to the height computed
    /// on the expand frame. An immutable record so an unchanged re-push coalesces (no child re-render). The
    /// <c>"drawer:"+uri</c> Key still remounts per ALBUM, so per-album SelectionModel / SwipeGroup state stays scoped.</summary>
    internal sealed record Props(Album Thin, IReadOnlyList<Track> Tracks, int Rows, bool Loading, bool ReadyEmpty, Action Retry);

    public AlbumDrawerPanel(Services svc, Action<string> play, Action<string, string?> go, Func<ColorF> accent)
    {
        _svc = svc; _play = play; _go = go; _accent = accent;
        _showChecks = () => { _ = _sel.Version.Value; return _sel.SelectedCount > 1; };   // 2+ only (a plain click must not summon checkboxes)
    }

    static readonly ColumnSet DrawerCols = new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: true, Thumb: false);
    static readonly TrackSize[] DrawerColumns =
        [TrackSize.Px(30f), TrackSize.Px(40f), TrackSize.Star(), TrackSize.Px(52f), TrackSize.Px(40f)];   // trailing "…" lane
    const float DrawerRowContentH = 40f;

    public override Element Render()
    {
        var bridge = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var acts = UseContext(ActionServices.Slot);
        var menuOverlay = UseContext(Overlay.Service);   // row context menus (right-click / long-press / the "…" cell)
        var p = UsePropsOrDefault<Props>();              // subscribes → a re-push (Pending → Ready) re-renders here
        if (p is null) return new BoxEl();               // mounted without props (never happens from DiscoGrid)
        _thin = p.Thin;
        _rows = p.Tracks;
        int n = p.Rows;                                  // the ONE row verdict — same DrawerState the slot was sized from

        Element body = p.ReadyEmpty ? EmptyNote(p.Retry)
                     : p.Loading ? ShimmerRows(n)
                     : Rows(p.Tracks, n, bridge, lib, acts, menuOverlay);

        // No Height here: the panel HUGS its content — the OUTER drawer slot (DiscoGrid.DrawerHeight) owns the reserved
        // number, and both read the same DrawerState, so the slot never clips a row mid-row. ClipToBounds stays as the
        // belt-and-braces card clip.
        return new BoxEl
        {
            Direction = 1, ClipToBounds = true,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                Head(),
                ZStack(body, Embed.Comp(() => new SelectionCommandBar(_sel,
                    // Live _rows/RowCap read (NOT a captured `n`): this factory freezes at mount — while the panel is
                    // still loading, a frozen row count would index past the empty track list.
                    i => (uint)i < (uint)Math.Min(_rows.Count, DiscoGrid.RowCap) ? _rows[i] : null,
                    bottomPadding: Spacing.S))),
            ],
        };
    }

    Element Head() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = 40f,
        Children =
        [
            new BoxEl { Width = 30f, Height = 30f, Shrink = 0f, Corners = CornerRadius4.All(15f), Fill = _accent(),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, OnClick = () => _play(_thin.Uri),
                // Artwork-derived fill ⇒ ink from the FILL's luminance (the WaveeCta.Palette rule), never the theme's
                // on-accent token: the lifted cover accent is often pale, where TextOnAccentPrimary's glyph vanished.
                Children = [ Icon(Icons.Play, 12f, ColorContrast.PickContrast(_accent())) ] },
            new BoxEl { Grow = 1f, Basis = 0f, OnClick = () => _go("album:" + _thin.Uri, _thin.Name),
                Children = [ new TextEl(_thin.Name) { Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } ] },
            AlbumNavAction.Create(() => _go("album:" + _thin.Uri, _thin.Name), 32f),
        ],
    };

    Element Rows(IReadOnlyList<Track> tracks, int n, PlaybackBridge? bridge, LibraryBridge? lib, ActionServices? acts, IOverlayService? menuOverlay)
        => ItemsView.CreateBound(
            n,
            scope =>
            {
                Element row = SelectorVisualsBound.AccentPill(scope, Embed.Comp(() => new DrawerTrackRow(this, scope, tracks, n, bridge, lib)), _showChecks);
                // Right-click / long-press / the row's "…" cell: the selection-aware track menu (Explorer semantics —
                // inside a multi-selection acts on all of it). Attached on a wrapper (the SearchPage songs pattern:
                // AccentPill's root carries bound visuals, so the menu chains on a plain BoxEl above it).
                if (acts is { } a && menuOverlay is { } ov)
                    row = new BoxEl { Direction = 1, Children = [row] }.WithContextMenu(ov, () => TrackContextMenu.Build(
                        a, _sel, i => (uint)i < (uint)n ? tracks[i] : null,
                        scope.Index.Peek(), static () => null));
                if (acts is null) return row;
                return RowSwipe.WrapBound(row, () =>
                {
                    int i = scope.Index.Peek();
                    return (uint)i < (uint)n
                        ? new ActionContext(ActionTarget.ForTracks(new[] { tracks[i] }), acts)
                        : null;
                }, _swipeGroup, TrackActions.ToggleLike, TrackActions.AddToQueue, scope.Index);
            },
            RepeatLayout.Stack(TrackRow.CompactListItemExtent),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.Extended,
                Selection = _sel,
                IsItemInvokedEnabled = true,
                OnInvoked = i =>
                {
                    if ((uint)i >= (uint)n) return;
                    var t = tracks[i];
                    TrackRow.Invoke(bridge, t, () => _ = _svc.Player.PlayAsync(_thin.Uri, i));
                },
                ItemText = i => (uint)i < (uint)n ? tracks[i].Title : "",
                Grow = 0f,
                Scroll = new ScrollOptions { OnScrollGeometryChanged = (g => _swipeGroup.AnyOpen ? BitConverter.SingleToInt32Bits(g.OffsetY) : 0L, _ => _swipeGroup.Close()) },
            });

    sealed class DrawerTrackRow : Component
    {
        readonly AlbumDrawerPanel _o;
        readonly RowScope _scope;
        readonly IReadOnlyList<Track> _tracks;
        readonly int _count;
        readonly PlaybackBridge? _bridge;
        readonly LibraryBridge? _lib;
        public DrawerTrackRow(AlbumDrawerPanel o, RowScope scope, IReadOnlyList<Track> tracks, int count, PlaybackBridge? bridge, LibraryBridge? lib)
        { _o = o; _scope = scope; _tracks = tracks; _count = count; _bridge = bridge; _lib = lib; }

        public override Element Render()
        {
            int i = _scope.Index.Value;
            if ((uint)i >= (uint)_count) return new BoxEl();
            var t = _tracks[i];
            var st = TrackRow.StateOf(_bridge, _lib, t);
            Element title = new TextEl(t.Title)
            {
                Size = 13f,
                Weight = 600,
                Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            };
            return TrackRow.Grid(t, i, st, DrawerCols, DrawerColumns, DrawerRowContentH, title, showTrackArtist: false, _o._go,
                onPlay: () => TrackRow.Invoke(_bridge, t, () => _ = _o._svc.Player.PlayAsync(_o._thin.Uri, i)),
                onLike: t.Uri.Length > 0 ? () => _lib?.ToggleSaved(t.Uri, t.Title) : null,
                actionsCell: TrackRow.MoreButton(true));   // "…" raises the row's context request (ClickRequestsContext)
        }
    }

    // READY BUT EMPTY. GetAlbumAsync swallows its fetch failure (StoreLibrarySource.EnsureFetchedAsync's `catch { }`)
    // and returns whatever the store holds, so the resource legitimately settles Ready on a trackless album — offline,
    // a failed envelope, or a cold-restored stub. That used to be a 0-row slot with nothing in it and no way out:
    // VirtualCollection has no invalidation, so the stale snapshot never healed. Retry re-runs the loader keeping the
    // current value visible (Resource.Refresh — stale-while-revalidate). Sized by DiscoGrid.NoteRows in the slot.
    static Element EmptyNote(Action retry) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.M),
        Children =
        [
            new TextEl(Loc.Get(Strings.Detail.Empty.NoTracks))
                { Grow = 1f, Basis = 0f, MinWidth = 0f, Size = 13f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Button.Standard(Loc.Get(Strings.Common.Retry), retry),
        ],
    };

    static Element ShimmerRows(int n)
    {
        var rows = new Element[n];
        for (int i = 0; i < n; i++) rows[i] = ShimmerRow();
        return new BoxEl { Direction = 1, Children = rows };
    }

    static Element ShimmerRow() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = TrackRow.CompactListItemExtent, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Children =
        [
            new BoxEl { Width = 16f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Grow = 1f, Basis = 0f, Height = 11f, MaxWidth = 240f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Width = 30f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Width = 40f },   // the reserved "…" lane (matches DrawerColumns' trailing 40px)
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
    static readonly Func<ColorF> ThemeAccent = static () => Tok.AccentDefault;
    readonly Func<ColorF> _accent;
    readonly Signal<int> _expanded = new(-1);
    ActionServices? _acts;            // card context menus (Menus.CardAttach) — resolved in Render, read by Cell
    IOverlayService? _menuOverlay;

    const float MinCol = 180f;
    static readonly float Gap = Spacing.L;          // column gap (the vertical row gap is RowGap, folded into rowExtra)

    // Uniform-card geometry → predictable drawer spacing. GridCard's cover is cardW-16; adding 20px vertical padding,
    // an 8px card gap, and 38px for one title + one metadata line yields an exact cardW+50 card. Keeping this separate
    // from RowGap leaves an actual gutter instead of flex-growing the card through it.
    const float CardChrome = 50f;
    const float RowGap     = 20f;       // vertical gap between card rows  (rowExtra = CardChrome + RowGap)
    const float BottomGap  = 30f;       // drawer → next row (breathing room below it)
    const float DrawerHeaderH = 56f;

    /// <summary>Max rows the inline drawer shows. THE single definition — it used to be a literal 10 repeated in
    /// PanelHeight, the panel's shimmer clamp and row clamp, and the selection-bar index guard, all free to drift.</summary>
    internal const int RowCap = 10;
    /// <summary>Rows reserved while loading when the card carries no usable count (a cold-restored stub): enough to
    /// read as "something is coming" without reserving a screenful for what may be a 2-track single.</summary>
    const int MinShimmerRows = 3;
    /// <summary>Rows' worth of height the Ready-but-empty note + retry occupies.</summary>
    const int NoteRows = 2;

    /// <summary>Rows the drawer will actually render, and why. ONE derivation, read by BOTH the slot sizing
    /// (PanelHeight/DrawerHeight) and the panel body (via the re-pushed Props) — so reserved height and rendered rows
    /// cannot disagree, which is what clipped rows mid-row and left trackless albums with a 0-row slot.</summary>
    readonly record struct DrawerState(string Uri, int Rows, bool Loading, bool ReadyEmpty);

    Resource<Album?> _full;             // the ONE full-album fetch for the open drawer (re-assigned every render)
    Memo<DrawerState>? _drawer;         // the ONE row/state derivation (see Render)
    Action? _retryFull;                 // stable delegate so the re-pushed Props coalesce (a fresh lambda would defeat record equality)

    // The inline drawer's open/close + switch motion. Opacity+Position animate the enter (drop-in) / exit (lift-out);
    // Size=Reflow eases the height when switching to an album with a different track count (the rows below reflow).
    static readonly LayoutTransition DrawerReveal = new(
        TransitionChannels.Opacity | TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(280f, Easing.SmoothOut),
        Size: SizeMode.Reflow,
        Enter: new EnterExit(Dy: -8f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -8f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Spring(0.24f, 1f));

    public DiscoGrid(VirtualCollection<Album> vc, Services svc, Action<string, string?> go, Action<string> play, int cap, int initialIndex = 0, Func<ColorF>? accent = null)
    { _vc = vc; _svc = svc; _go = go; _play = play; _cap = cap; _initialIndex = initialIndex; _accent = accent ?? ThemeAccent; }

    public override Element Render()
    {
        _acts = UseContext(ActionServices.Slot);
        _menuOverlay = UseContext(Overlay.Service);

        // ONE fetch for the open drawer, keyed by uri (collapsed ⇒ "" ⇒ a completed no-op task, never a request).
        // Lifted out of AlbumDrawerPanel so the row count is known where the drawer's SLOT is sized. GetAlbumAsync is
        // cached, so re-expanding the same album costs nothing.
        _ = _vc.Version.Value;                       // subscribe → the expanded uri resolves once its page lands
        int expandedIdx = _expanded.Value;           // subscribe → the fetch re-keys on expand/collapse/switch
        string expandedUri = (expandedIdx >= 0 ? _vc[expandedIdx]?.Uri : null) ?? "";
        _full = UseResource(ct => expandedUri.Length == 0
                ? System.Threading.Tasks.Task.FromResult<Album?>(null)
                : AlbumLoader.LoadAlbumAsync(_svc, expandedUri, ct),
            (Album?)null, expandedUri);

        // A MEMO, not a field and not a signal. The LazyGrid below is a propless Embed.Comp: its ctor delegates freeze
        // at mount and it re-renders only on its OWN subscriptions, so a field written here would be read stale. The
        // frozen DrawerHeight/DrawerFor delegates read this memo, which subscribes LazyGrid to the resource
        // transitively — correct invalidation with zero writes (a write from Render would be a backwards write).
        // NOTE: UseComputed keeps the FIRST closure for the component's lifetime, so the compute derives everything
        // from signals/fields it reads itself — no per-render local may be captured here.
        _drawer = UseComputed(() =>
        {
            _ = _vc.Version.Value;
            int idx = _expanded.Value;
            var al = idx >= 0 ? _vc[idx] : null;
            if (al is null || al.Uri.Length == 0) return new DrawerState("", 0, false, false);
            var l = _full.Loadable;
            // Same fallback chain as the props push in DrawerFor (keep the two in lockstep): the fetched tracklist,
            // else whatever the thin card already carries (usually null in a discography), else empty.
            IReadOnlyList<Track> tracks = l.Value.Value?.Tracks ?? al.Tracks ?? Array.Empty<Track>();
            bool pending = l.State.Value == (byte)LoadState.Pending;
            // LOADING semantics: a resolved SHORT list must not be treated as loading just because the card's stub
            // count is larger — but a still-Pending fetch that has produced nothing yet stays loading no matter how
            // small the hint is. `tracks.Count == 0 && pending` is exactly that rule.
            bool loading = tracks.Count == 0 && pending;
            bool readyEmpty = !pending && tracks.Count == 0;   // Ready OR Failed with nothing to show → note + retry
            int rows = loading ? Math.Clamp(al.TrackCount, MinShimmerRows, RowCap) : Math.Min(tracks.Count, RowCap);
            return new DrawerState(al.Uri, rows, loading, readyEmpty);
        });

        return Embed.Comp(() => new LazyGrid(
        // Capped to _cap when >0 (the artist page); cap 0 → the full facet. The count delegate reads the live total.
        count: () => { _ = _vc.Version.Value; int t = _vc.CountOr0; return _cap > 0 ? Math.Min(_cap, t) : t; },
        cell: Cell,
        ensureRange: (f, l) => _vc.EnsureRange(f, l - 1),
        minColWidth: MinCol, gap: Gap, rowExtra: CardChrome + RowGap, overscanRows: 4,
        expanded: _expanded,
        drawer: DrawerFor,
        drawerHeight: DrawerHeight,
        initialIndex: _initialIndex));
    }

    Element Cell(int idx, float cardW)
    {
        var al = _vc![idx];
        if (al is null) return Placeholder(cardW);
        string date = ReleaseDateLabel(al);
        string subtitle = al.TrackCount > 0
            ? date.Length > 0
                ? Strings.Artist.ReleaseMeta(date, Strings.Artist.TrackCount(al.TrackCount))
                : Strings.Artist.TrackCount(al.TrackCount)
            : date;
        Element card = MediaCard.GridCard(al.Cover, al.Name, subtitle, al.Uri,
            onClick: () => _expanded.Value = _expanded.Peek() == idx ? -1 : idx,
            onPlay: () => _play(al.Uri),
            onNavigate: () => _go("album:" + al.Uri, al.Name),
            accent: Surfaces.SchemeFor(al.Cover?.Url) is { } p ? WaveePalette.Lift(WaveePalette.Accent(p)) : null,
            menu: _menuOverlay is { } ov ? Menus.CardAttach(_acts, ov, al.Uri, al.Name, al.Cover, subtitle) : null);
        if (card is BoxEl b)
        {
            // Force ONE height (square cover + chrome) so every card is uniform → the drawer's hug spacing is exact.
            b = b with { Key = "album:" + al.Uri, Height = cardW + CardChrome };
            // Highlight the expanded card (accent border + brighter fill) so it's unmistakably the drawer's owner —
            // pairs with the connector bar at the drawer's top edge.
            if (_expanded.Peek() == idx)
                b = b with { BorderColor = _accent(), BorderWidth = 2f, Fill = Tok.FillCardDefault };
            card = b;
        }
        return card;
    }

    static string ReleaseDateLabel(Album album)
    {
        if (album.ReleaseDate is not { Length: > 0 } iso ||
            !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return album.Year > 0 ? album.Year.ToString(CultureInfo.InvariantCulture) : "";
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(Loc.CurrentCulture); }
        catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
        return (album.ReleaseDatePrecision ?? "").ToUpperInvariant() switch
        {
            "YEAR" => date.ToString("yyyy", culture),
            "MONTH" => date.ToString("MMM yyyy", culture),
            _ => date.ToString("MMM d, yyyy", culture),
        };
    }

    // A self-sizing shimmer cell, SAME height as a real card: the cover fills the (engine-laid-out) cell width and squares
    // itself via AspectRatio — no hardcoded width, so it tracks the real card exactly. The bars stretch to the cell width.
    static Element Placeholder(float cardW) => new BoxEl
    {
        Key = "album:placeholder",
        Direction = 1, Gap = Spacing.S, Height = cardW + CardChrome,
        Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
        // Borderless like the restyled GridCard's resting state (the plate is hover-only now) — a plated skeleton
        // would flash a different silhouette than the card it becomes.
        Corners = CornerRadius4.All(Radii.Card),
        Children =
        [
            // Fluid square cover: Width left NaN + AspectRatio 1f → fills the engine-laid-out cell width and derives its
            // height (the same self-sizing the real ArtworkFill cover uses) — no hardcoded dimensions.
            new ImageEl { Source = "", AspectRatio = 1f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(Radii.Card), Placeholder = Tok.FillSubtleSecondary },
            new BoxEl { Height = 13f, AlignSelf = FlexAlign.Stretch, MaxWidth = 150f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 11f, Width = 92f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
        ],
    };

    // Reserve EXACTLY what the panel will render. `rows` comes from the same DrawerState the panel body reads, so a
    // stale/absent stub TrackCount can no longer under-reserve (0 rows for a trackless card while the panel clamped
    // its shimmer to >=1, which ClipToBounds then cut mid-row).
    float PanelHeight(int idx)
    {
        _ = idx;                                        // LazyGrid's Func<int,float> shape; sizing is per-DRAWER and only one is open
        var d = _drawer?.Value ?? default;
        int rows = d.ReadyEmpty ? NoteRows : d.Rows;
        return DrawerHeaderH + rows * TrackRow.CompactListItemExtent;
    }
    // The preceding grid row already includes RowGap, so the drawer starts beyond the card's shadow halo. Its own slot
    // only needs to reserve the panel plus the breathing room before the following row.
    float DrawerHeight(int idx) => PanelHeight(idx) + BottomGap;

    Element DrawerFor(int idx, GridDrawerInfo info)
    {
        var al = _vc?[idx];
        if (al is null) return new BoxEl();
        var d = _drawer?.Value ?? default;
        _retryFull ??= () => _full.Refresh();
        // Re-pushed PROPS, not ctor args. The panel's rows and loading/empty state all change AFTER mount
        // (Pending → Ready on the same uri), and ctor args freeze — which is exactly how the old frozen `panelH` ctor
        // argument locked the panel to the height computed on the expand frame. The Key still remounts per ALBUM, so
        // per-album SelectionModel / SwipeGroup state stays scoped.
        var panel = Embed.Comp(
            new AlbumDrawerPanel.Props(al, _full.Loadable.Value.Value?.Tracks ?? al.Tracks ?? Array.Empty<Track>(),
                                       d.Rows, d.Loading, d.ReadyEmpty, _retryFull),
            () => new AlbumDrawerPanel(_svc, _play, _go, _accent))
            with { Key = "drawer:" + al.Uri };

        // Connector: a short accent bar at the panel's top edge, spanning exactly the expanded card's column → reads as
        // "this drawer belongs to THAT card" (paired with the card's accent border).
        var connector = new BoxEl
        {
            Direction = 0, HitTestVisible = false,
            Children =
            [
                new BoxEl { Width = MathF.Max(0f, info.Left), Height = 0f },
                new BoxEl { Width = info.CellWidth, Height = 3f, Corners = CornerRadius4.All(1.5f), Fill = _accent() },
            ],
        };

        // The OUTER slot owns the transition because its height participates in parent layout. The stable key keeps ONE
        // node across A→B switches (Position/Size-reflow); a true open/close runs the enter/exit channels.
        var inner = new BoxEl { ZStack = true, Children = [ panel, connector ] };
        return new BoxEl
        {
            Key = "disco-drawer", Direction = 1,
            Height = DrawerHeight(idx),
            ClipToBounds = true, Animate = DrawerReveal,
            Children = [ inner ],
        };
    }
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
    readonly Func<ColorF> _accent;
    readonly Signal<bool> _collapsed = new(false);
    // :stuck — true only while the body's sticky clip is actually cutting (ScrollBindDsl.OnFlag edge, never per-frame).
    readonly Signal<bool> _stickyClipped = new(false);
    VirtualCollection<Album>? _vc;
    System.Threading.CancellationTokenSource? _cts;   // per-instance; cancelled on unmount (feeds the VC + the seed probe)
    bool _seeded;                                      // one-shot latch: guards the provisional Seed (Seed bumps Version)

    const int Cap = DiscographyRoute.PreviewCap;

    /// <param name="stickyClearance">Viewport-top offset the sticky header pins BELOW — the artist page passes
    /// <see cref="ArtistShyPill.Clearance"/> so the pinned header never slides under the floating shy pill; the default
    /// 0 is for hosts with no top overlay (DiscographyPage). STATIC on purpose (a ctor float, never a reactive
    /// `pinned.Value` read): every facet sits ≥1000px below the sentinel flip, so a reactive read would evaluate to
    /// the clearance in every reachable state while costing a two-section re-render + ScrollBinds re-bake per flip;
    /// the one wrong state (header pinned, pill hidden) only exists while Artist != Ready, where the page shows
    /// skeleton/error, not a scrollable grid.</param>
    public DiscographySection(string artistUri, string artistName, DiscographyKind kind, string title, Services svc, Action<string, string?> go, Action<string> play, Func<ColorF> accent, float stickyClearance = 0f)
    { _artistUri = artistUri; _artistName = artistName; _kind = kind; _title = title; _svc = svc; _go = go; _play = play; _accent = accent; _pinTop = stickyClearance; }

    readonly float _pinTop;   // see the ctor doc: the sticky header's viewport-top offset (ArtistShyPill.Clearance / 0)

    public override Element Render()
    {
        var post = UsePost();
        _cts ??= new System.Threading.CancellationTokenSource();
        _vc ??= DiscoVc.Make(_svc, _artistUri, _kind, post, _cts.Token);
        // Once per mount (a signal-free effect runs exactly once): cancel the CTS on unmount — cancelling the VC's paged
        // fetches AND the seed probe — and fire the total-only probe so shimmer-up-to-N renders before page 0 lands.
        // Mirrors the HomePage / LyricsTicker UseSignalEffect + Reactive.OnCleanup lifecycle pattern.
        UseSignalEffect(() =>
        {
            Reactive.OnCleanup(() => { _cts?.Cancel(); _cts?.Dispose(); });
            SeedProbe(post);
        });
        _ = _vc.Version.Value;                       // subscribe → header count + "See all N" update as the facet loads
        int total = _vc.CountOr0;
        bool collapsed = _collapsed.Value;

        // Body stays mounted so collapse can animate (Reflow height) and collapsed mode can peek the first album
        // tops through a clipped, bottom-faded, softly blurred window. Leading anchor keeps content top-pinned.
        Element grid = new BoxEl
        {
            Direction = 1,
            // Collapsed peek is decorative — taps expand via the clip wrapper; cards must not open drawers.
            HitTestVisible = !collapsed,
            Children = [Embed.Comp(() => new DiscoGrid(_vc!, _svc, _go, _play, cap: Cap, accent: _accent))],
        };
        var bodyKids = new List<Element>(2) { grid };
        if (!collapsed && total > Cap) bodyKids.Add(SeeAllButton(total));

        Element body = new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            ClipToBounds = true,
            // Collapsed → short peek of the first cards; expanded → natural height. SizeMode.Reflow eases the layout
            // height so sections below slide smoothly (WinUI Expander timings).
            Height = collapsed ? CollapsedPeekH : float.NaN,
            Animate = BodyMotion,
            EdgeFade = collapsed ? new EdgeFadeSpec(EdgeMask.Bottom, CollapsedFadeBand) : null,
            Blur = collapsed ? CollapsedPeekBlur : 0f,
            Opacity = collapsed ? 0.92f : 1f,
            // Peek is tappable to expand; expanded body keeps sticky-clip under the pinned header.
            HitTestVisible = true,
            OnClick = collapsed ? () => _collapsed.Value = false : null,
            Children = bodyKids.ToArray(),
        };
        if (!collapsed)
            body = ((BoxEl)body) with
            {
                ScrollBinds = [new() { ClipTopAtViewport = HeaderClipInset, OnFlag = on => _stickyClipped.Value = on }],
                // The sticky clip is a hard cut: cards vanish on an exact pixel line under the pinned header, which
                // reads as content being sliced rather than passing behind it. A short top band softens that line into
                // a dissolve, the same cue the shelves already use at their scroll edges. ONLY while the clip is cutting
                // (the OnFlag :stuck edge): at rest there is no cut to soften, so the band instead feathered the FIRST
                // card row's own tops (the "black card tops" defect) and forced the whole facet body through an
                // offscreen RT for a fade that had nothing to fade.
                EdgeFade = _stickyClipped.Value ? new EdgeFadeSpec(EdgeMask.Top, 0f, StickyFadeBand, 0f, 0f) : null,
            };

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Padding = new Edges4(0f, 0f, 0f, Spacing.XXL),
            Children = [Header(total, collapsed), body],
        };
    }

    // Peek of the first album-card tops when the facet is collapsed (~one card row tip).
    const float CollapsedPeekH = 96f;
    const float CollapsedFadeBand = 64f;
    const float CollapsedPeekBlur = 8f;

    // Expand 333ms / collapse ~220ms — Expander timings, height-only. Leading keeps album tops in the peek.
    static readonly LayoutTransition BodyMotion = new(
        TransitionChannels.Size,
        TransitionDynamics.Tween(333f, Easing.FluentPopOpen),
        Size: SizeMode.Reflow,
        ExitDynamics: TransitionDynamics.Tween(220f, EasingSpec.CubicBezier(1f, 1f, 0f, 1f)),
        Anchor: SizeAnchor.Leading,
        Axes: SizeAxes.Height);

    // Total-only probe (limit 0 → NO network; resolves same-tick from the cached artist the page header already fetched).
    // Seeds the VC COUNT ONLY (items = default) as PROVISIONAL so shimmers render instantly; the first real page reconciles
    // the count. Behind a one-shot latch because Seed bumps Version → an unlatched seed would re-render-loop. A cancelled or
    // failed probe, or a non-positive total, leaves _seeded clear so a remount retries (existing total>0 gate still governs).
    async void SeedProbe(Action<Action> post)
    {
        var cts = _cts;
        if (_seeded || cts is null) return;
        var ct = cts.Token;
        var vc = _vc;
        try
        {
            var p = await _svc.Library.GetDiscographyAsync(_artistUri, _kind, 0, 0, ct).ConfigureAwait(false);
            int total = p.Total;
            post(() =>
            {
                if (_seeded || ct.IsCancellationRequested || total <= 0 || vc is null) return;
                _seeded = true;
                vc.Seed(total, default, provisional: true);
            });
        }
        catch { /* OCE (nav away) or a failed probe → _seeded stays clear so a remount retries */ }
    }

    /// <summary>The header's text row. RailHeader's 20px type measures ~28 DIP with its line box; the accent bar's
    /// MinHeight 22 is shorter, so the text sets the row height.</summary>
    const float HeaderRowH = 28f;
    /// <summary>Header padding ABOVE the text row. Deliberately tighter than the pad below (and than the symmetric
    /// Spacing.M it replaced): a symmetric 12/12 made the pinned band 52px tall, which — stacked under the shy pill's
    /// own 56px — ate ~120px of viewport before the first card. The heading only needs to clear the pill, not float in
    /// the middle of its own strip. Keep in lockstep with Header()'s Padding — PinnedHeaderH derives from it.</summary>
    const float HeaderPadTop = Spacing.XS;
    /// <summary>Header padding BELOW the text row — larger than <see cref="HeaderPadTop"/> so the heading sits ON its
    /// content (the optical grouping every heading wants) rather than centred in a band.</summary>
    const float HeaderPadBottom = Spacing.XS;
    /// <summary>The header's real pinned height. This used to be a hand-written 38 against an actual 44, so ~6px of
    /// card leaked above the clip line on every scroll. Derived now, so a padding or type change cannot desync it.</summary>
    const float PinnedHeaderH = HeaderRowH + HeaderPadTop + HeaderPadBottom;   // 36
    /// <summary>Where the section body's sticky clip cuts: everything above (the overlay clearance the pill owns, plus
    /// the pinned header itself) must be free of card pixels. 44 + 36 = 80 on the artist page; 36 with no clearance.</summary>
    float HeaderClipInset => _pinTop + PinnedHeaderH;

    /// <summary>Depth of the dissolve at the sticky-clip line. Widened from 14: the clip line sits ~80px down the
    /// viewport with a translucent acrylic pill immediately above it, where a 14px feather still read as a cut.</summary>
    const float StickyFadeBand = 22f;

    Element Header(int total, bool collapsed) => new BoxEl
    {
        // Left padding 0 so the accent bar aligns with the non-collapsible AccentHeader sections (Top tracks / Appears on).
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f,
        Corners = CornerRadius4.All(6f), HoverFill = Tok.FillSubtleSecondary,
        // Breathing room while PINNED, ASYMMETRIC: the named HeaderPadTop/HeaderPadBottom (PinnedHeaderH derives from
        // them — keep them in lockstep). Less above than below, so the heading reads as attached to its own grid rather
        // than centred in a fat strip, and the pinned band stays slim enough to leave the cards the viewport. The right
        // pad keeps it clear of the edge.
        Padding = new Edges4(0f, HeaderPadTop, Spacing.L, HeaderPadBottom),
        // CSS-sticky wayfinding: the header pins BELOW the floating shy pill (_pinTop = ArtistShyPill.Clearance on the
        // artist page; 0 on hosts with no overlay) while ITS section (the parent column = the containing block) is in
        // view, clamps at the section's end, and releases on scroll-back — so mid-grid the user always sees which facet
        // (Albums / Singles & EPs) they're in. STATIC by design — see the ctor's stickyClearance doc. The header itself
        // never changes looks; the section BODY's sticky-clip (below) keeps the page backdrop behind it.
        ScrollBinds = [ new() { PinTop = _pinTop } ],
        OnClick = () => _collapsed.Value = !_collapsed.Peek(),
        Children =
        [
            new BoxEl { Width = 3f, MinHeight = 22f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(1.5f), Fill = _accent() },
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
        Padding = new Edges4(Spacing.L, 0f, Spacing.M, 0f), Corners = CornerRadius4.All(Radii.Card),
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
