using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// "Your Library" — WaveeMusic's master–detail skeleton (AlbumsLibraryView / ArtistsLibraryView). A LEFT navigator (a
// sort/view-size dropdown + filter, then a list-or-grid bound to the cached LibraryStore) and a RIGHT pane that is a
// COMPACT detail panel (104px hero + actions + content) for the selected item — NOT the full page. Albums/podcasts =
// two columns; ARTISTS = three (artist list | discography | the picked release's tracks). Columns are GridSplitter-
// resizable. Selection drives the panes via stable per-selection loadables (UseAsyncResource re-driven by the selection
// key), so picking a different item reactively re-skins the pane in place — no navigation, no stale freeze.
sealed class LibraryPage : Component
{
    readonly string _kind;   // "albums" | "artists" | "podcasts"

    readonly Signal<string> _selectedKey = new("");   // selected item route key (album:/artist:/show: + uri)
    readonly Signal<string> _albumKey = new("");       // artists only: the release picked in the discography (3rd column)
    readonly Signal<int> _view = new(1);               // 0 CompactList · 1 List · 2 CompactGrid · 3 Grid
    readonly Signal<int> _sort = new(0);               // 0 Recents · 1 Recently added · 2 Alphabetical · 3 Creator · 4 Release date
    readonly Signal<bool> _desc = new(false);
    readonly Signal<int> _size = new(1);               // grid card size: 0 S · 1 M · 2 L
    readonly Signal<string> _filter = new("");
    readonly Signal<float> _leftW, _midW;              // resizable column widths
    readonly SelectionModel _navSel = new();           // master list/grid single-selection (the WinUI ItemsView selection)

    static readonly string[] NoSuggest = Array.Empty<string>();

    public LibraryPage(string kind)
    {
        _kind = kind;
        _leftW = new(kind == "artists" ? 280f : 340f);
        _midW = new(440f);
    }

    readonly record struct NavItem(Image? Cover, string Title, string Subtitle, string Uri, bool Circular, string RouteKey, int Year);

    bool IsArtists => _kind == "artists";
    bool HasCreator => _kind != "artists";    // album → artist, podcast → publisher
    bool HasRelease => _kind == "albums";

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var store = UseContext(LibraryStore.Slot);
        var bridge = UseContext(PlaybackBridge.Slot);
        if (svc is null || store is null) return new BoxEl { Grow = 1f };
        this.UseSoftReveal(dy: 0f, blur: 0f);

        var shown = Filtered(Project(store));
        // Keep the ItemsView SelectionModel pointed at the selected item across load/filter/sort/view + auto-select first.
        UseEffect(() => SyncNav(shown), NavHash(shown) + "|" + _selectedKey.Value);

        string sel = _selectedKey.Value;   // subscribe
        bool artists = IsArtists;
        string albumKey = _albumKey.Value;   // subscribe

        // Hooks must NEVER be branched. All three kinds are the same LibraryPage type, so a branched hook count let the
        // reconciler reuse a sibling's hook slot → an EffectCell→AsyncResourceCell cast crash. Call all three loads
        // unconditionally in a FIXED order; the off-kind ones key on "" → resolve to Empty with no real fetch.
        var detail = UseAsyncResource(ct => LoadDetail(svc, artists ? "" : sel, ct), DetailModel.Empty, artists ? "" : sel);
        var artist = UseAsyncResource(ct => LoadArtist(svc, artists ? sel : "", ct), EmptyArtist(""), artists ? sel : "");
        var albumTracks = UseAsyncResource(ct => LoadDetail(svc, artists ? albumKey : "", ct), DetailModel.Empty, artists ? albumKey : "");

        Element right = artists
            ? ArtistColumns(artist, albumTracks, svc, bridge, sel.Length > 0)
            : DetailColumn(detail, svc, bridge, sel.Length > 0);

        return new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch,
            Children = [LeftColumn(shown), Grip(_leftW, 240f, 560f), right],
        };
    }

    // The page already lives INSIDE the shell's content card (rounded FileArea + shadow, WaveeShell.cs), so the columns
    // must NOT be nested cards — that double-cards and reads heavy. Depth here is subtle + WinUI: the navigator gets a
    // faint recede layer, the detail panes stay on the base content surface, and a 1px hairline (the resize grip) divides
    // them. Outer corners come free from the content card's rounded clip, so the columns themselves stay square.
    static BoxEl NavPanel => new() { Direction = 1, ClipToBounds = true, Fill = Tok.FillLayerDefault };
    static BoxEl Pane => new() { Direction = 1, ClipToBounds = true };

    void Select(NavItem it)
    {
        _selectedKey.Value = it.RouteKey;
        if (IsArtists) _albumKey.Value = "";   // a new artist resets the 3rd-column release selection
    }

    // ── data ──
    NavItem[] Project(LibraryStore store) => _kind switch
    {
        "artists" => Warm(store.EnsureArtists, store.Artists).Select(a => new NavItem(a.Image, a.Name, Loc.Get(Strings.Search.TypeArtist), a.Uri, true, "artist:" + a.Uri, 0)).ToArray(),
        "podcasts" => Warm(store.EnsureShows, store.Shows).Select(s => new NavItem(s.Cover, s.Name, s.Publisher, s.Uri, false, "show:" + s.Uri, 0)).ToArray(),
        _ => Warm(store.EnsureAlbums, store.Albums).Select(a => new NavItem(a.Cover, a.Name, a.Artists.Count > 0 ? a.Artists[0].Name : "", a.Uri, false, "album:" + a.Uri, a.Year)).ToArray(),
    };

    static IReadOnlyList<T> Warm<T>(Action ensure, Loadable<IReadOnlyList<T>> cell) { ensure(); return cell.Value.Value; }

    NavItem[] Filtered(NavItem[] items)
    {
        string q = _filter.Value.Trim(); int sort = _sort.Value; bool desc = _desc.Value;   // subscribe
        var arr = (q.Length == 0 ? items : items.Where(it => it.Title.Contains(q, StringComparison.OrdinalIgnoreCase))).ToArray();
        Comparison<NavItem>? cmp = sort switch
        {
            2 => (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase),
            3 => (a, b) => string.Compare(a.Subtitle, b.Subtitle, StringComparison.OrdinalIgnoreCase),
            4 => (a, b) => a.Year.CompareTo(b.Year),
            _ => null,
        };
        if (cmp is not null) Array.Sort(arr, cmp);
        else if (sort == 1) Array.Reverse(arr);   // "recently added" ≈ reverse of the cached (recents) order
        if (desc && cmp is not null) Array.Reverse(arr);
        return arr;
    }

    static string Strip(string key, string prefix) => key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : "";
    static Artist EmptyArtist(string uri) => new("", uri, "", null);

    static Task<DetailModel> LoadDetail(Services svc, string routeKey, CancellationToken ct)
    {
        if (routeKey.Length == 0) return Task.FromResult(DetailModel.Empty);
        var (kind, id) = DetailPage.ParseDetail(new Route(routeKey));
        return DetailPage.LoadAsync(svc, kind, id, ct);
    }

    static Task<Artist> LoadArtist(Services svc, string routeKey, CancellationToken ct)
    {
        string uri = Strip(routeKey, "artist:");
        return uri.Length == 0 ? Task.FromResult(EmptyArtist("")) : svc.Library.GetArtistAsync(uri, ct);
    }

    // ── left navigator ──
    Element LeftColumn(NavItem[] shown) => NavPanel with
    {
        Width = _leftW.Value, Shrink = 0f,
        Children = [Toolbar(), ListBody(shown)],   // ListBody returns a self-scrolling ItemsView (grow:1) — no outer ScrollView
    };

    Element Toolbar() => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.S, Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.S),
        Children =
        [
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = [Embed.Comp(() => new LibrarySortView(_sort, _desc, _view, _size, HasCreator, HasRelease)), new BoxEl { Grow = 1f }] },
            AutoSuggestBox.Create(NoSuggest, Loc.Get(Strings.Library.Filter), text: _filter, queryIcon: Icons.Search, grow: 1f, maxFillWidth: 9999f, minHeight: 32f, cornerRadius: 8f),
        ],
    };

    // The master list/grid IS the engine's ItemsView (WinUI ListView/GridView): single-selection, keyboard nav, and the
    // proper accent-bar / selected-state chrome painted by the item container. Keyed by the displayed set so a
    // filter/sort/view change remounts with the right slots; the SelectionModel is external so selection survives.
    // NavRow/NavCard supply only the CONTENT — the container paints selection.
    Element ListBody(NavItem[] shown)
    {
        int view = _view.Value; int size = _size.Value;   // subscribe
        if (shown.Length == 0)
            return new BoxEl { Padding = new Edges4(WaveeSpace.M, WaveeSpace.XL, WaveeSpace.M, WaveeSpace.XL), Children = [new TextEl(_filter.Peek().Length > 0 ? Loc.Get(Strings.Library.NoMatch) : "…") { Size = 13f, Color = Tok.TextTertiary }] };

        bool grid = view >= 2; bool compact = view == 0 || view == 2;
        string key = "nav:" + view + ":" + size + ":" + NavHash(shown);

        if (grid)
            return new BoxEl
            {
                Key = key, Grow = 1f, Direction = 1, Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.S, 0f),
                Children = [ItemsView.Create(shown.Length, i => NavCardContent(shown[i], compact),
                    RepeatLayout.GridFit((compact ? 88f : 116f) + size * (compact ? 16f : 24f), 8f),
                    selectionMode: ItemsSelectionMode.Single, selection: _navSel, selector: SelectorVisual.Border,
                    selectionChanged: () => OnNavSel(shown), grow: 1f)],
            };

        return new BoxEl
        {
            Key = key, Grow = 1f, Direction = 1,
            Children = [ItemsView.List(shown.Length, i => NavRowContent(shown[i], compact),
                selectionMode: ItemsSelectionMode.Single, selection: _navSel,
                onSelectionIndexChanged: i => OnNavSelIdx(shown, i), itemExtent: compact ? 40f : 60f, grow: 1f)],
        };
    }

    void OnNavSel(NavItem[] shown) => OnNavSelIdx(shown, _navSel.FirstSelectedIndex);
    void OnNavSelIdx(NavItem[] shown, int i) { if (i >= 0 && i < shown.Length) Select(shown[i]); }

    void SyncNav(NavItem[] shown)
    {
        if (shown.Length == 0) return;
        string key = _selectedKey.Peek();
        int idx = key.Length == 0 ? 0 : Array.FindIndex(shown, it => it.RouteKey == key);
        if (idx < 0) idx = 0;
        if (key.Length == 0) Select(shown[idx]);
        if (_navSel.FirstSelectedIndex != idx) _navSel.Select(idx);
    }

    static string NavHash(NavItem[] shown) { int h = 17; foreach (var it in shown) h = h * 31 + it.RouteKey.GetHashCode(); return (h & 0x7fffffff).ToString(); }

    Element NavRowContent(NavItem it, bool compact)
    {
        var children = new List<Element>(2);
        if (!compact)
            children.Add(new BoxEl { Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(it.Circular ? 20f : 5f), ClipToBounds = true,
                Children = [Surfaces.Artwork(it.Cover, it.Uri.GetHashCode() & 0x7fffffff, 40f, 40f, it.Circular ? 20f : 5f)] });
        children.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
            Children =
            [
                new TextEl(it.Title) { Size = compact ? 13f : 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                compact ? new BoxEl() : new TextEl(it.Subtitle) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ] });
        return new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f), Children = children.ToArray() };
    }

    // Pure content — fills whatever cell the grid layouter hands it (no width passed in); the engine measures it at the
    // slot width so a long title truncates. Circular (artist) covers get extra pad so the round, blurry covers don't touch.
    Element NavCardContent(NavItem it, bool compact)
    {
        float pad = it.Circular ? 16f : WaveeSpace.S;
        var children = new List<Element>(2) { Surfaces.ArtworkFill(it.Cover, it.Circular ? 9999f : 6f) };
        if (!compact) children.Add(new TextEl(it.Title) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, AlignSelf = it.Circular ? FlexAlign.Center : FlexAlign.Start });
        return new BoxEl { Direction = 1, Gap = WaveeSpace.S, ClipToBounds = true, Padding = new Edges4(pad, pad, pad, pad), Children = children.ToArray() };
    }

    // ── right pane(s) ──
    Element DetailColumn(Loadable<DetailModel> detail, Services svc, PlaybackBridge? bridge, bool hasSel)
    {
        if (!hasSel) return Placeholder(_kind == "podcasts" ? Strings.Library.SelectShow : Strings.Library.SelectAlbum);
        return Pane with { Key = "lib:detail", Grow = 1f, Basis = 0f,
            Children = [Embed.Comp(() => new LibraryDetailPane(detail, _kind == "podcasts", svc, bridge))] };
    }

    Element ArtistColumns(Loadable<Artist> artist, Loadable<DetailModel> albumTracks, Services svc, PlaybackBridge? bridge, bool hasSel)
    {
        if (!hasSel) return Placeholder(Strings.Library.SelectArtist);
        Element artistPane = Pane with
        {
            // Basis = the user's chosen width, but Shrink so a narrow shell never paints the grid wider than this column
            // (Shrink=0 + fixed Width let the viewport outgrow the flex slot → discography tiles under the tracks pane).
            Basis = _midW.Value, MinWidth = 300f, MaxWidth = _midW.Value, Shrink = 1f, Grow = 0f,
            Children = [Embed.Comp(() => new LibraryArtistPane(artist, svc, bridge, _albumKey))],
        };
        Element tracksPane = _albumKey.Value.Length > 0
            ? Pane with { Key = "lib:tracks", Grow = 1f, Basis = 0f, MinWidth = 220f, Shrink = 1f,
                Children = [Embed.Comp(() => new LibraryDetailPane(albumTracks, false, svc, bridge))] }
            : Pane with { Key = "lib:tracks:empty", Grow = 1f, Basis = 0f, MinWidth = 220f, Shrink = 1f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl(Loc.Get(Strings.Library.SelectAlbumTracks)) { Size = 13f, Color = Tok.TextTertiary }] };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
            Children = [artistPane, Grip(_midW, 300f, 620f), tracksPane],
        };
    }

    static Element Placeholder(string key) => Pane with
    {
        Key = "lib:empty", Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(Loc.Get(key)) { Size = 14f, Color = Tok.TextTertiary }],
    };

    // The grip's ColumnGrip carries Grow=1 to fill the column HEIGHT — so it must be boxed in a fixed Width=10 / Shrink=0
    // wrapper, else that Grow leaks into the horizontal row and the grip eats half the leftover width (the empty-gap bug).
    static Element Grip(Signal<float> w, float min, float max) => new BoxEl
    {
        Width = 7f, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Stretch,
        Children = [Embed.Comp(() => new ColumnGrip(w, min, max))],
    };
}

// A thin drag-to-resize seam between two library columns. Reuses the engine's eager pointer capture (BoxEl.OnDrag) and
// reconstructs the true window-X each frame (the grip moves as the column resizes). Invisible at rest, a hairline on hover.
sealed class ColumnGrip : Component
{
    readonly Signal<float> _width;
    readonly float _min, _max;
    NodeHandle _self;
    float _startW, _startPx;
    public ColumnGrip(Signal<float> width, float min, float max) { _width = width; _min = min; _max = max; }

    public override Element Render() => new BoxEl
    {
        // A persistent 1px divider centred in a 7px hit strip (always visible = the column seam; the strip is the drag
        // target). Brightens slightly on hover to cue that it's draggable.
        Grow = 1f, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Cursor = CursorId.SizeWE,
        OnRealized = h => _self = h, OnPointerDown = OnDown, OnDrag = OnMove,
        Children = [new BoxEl { Width = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault, HoverFill = Tok.TextTertiary }],
    };

    void OnDown(Point2 local)
    {
        var s = Context.Scene;
        if (s is null || _self.IsNull || !s.IsLive(_self)) return;
        // NOTE: deliberately do NOT flip Motion.ReducedMotion here (SidebarResizeGrip does, to kill its width spring).
        // Column widths aren't sprung, so there's nothing to suppress — and toggling that global mid-drag is exactly what
        // shifted UseSoftReveal/UseEntrance's hook count and crashed (now also hardened engine-side).
        _startW = _width.Peek();
        _startPx = local.X + s.AbsoluteRect(_self).X;
    }

    void OnMove(Point2 local)
    {
        var s = Context.Scene;
        if (s is null || _self.IsNull || !s.IsLive(_self)) return;
        float px = local.X + s.AbsoluteRect(_self).X;
        _width.Value = Math.Clamp(_startW + (px - _startPx), _min, _max);
    }
}

// The compact detail pane (WaveeMusic LibraryDetailPanel) for an album/show: a 104px hero + an action row + the content
// list (tracks or episodes). Reads a STABLE Loadable<DetailModel> (re-driven by the host's selection key) so a new
// selection re-skins it in place. NOT the full DetailPage — this is the right pane of the master-detail split.
sealed class LibraryDetailPane : Component
{
    readonly Loadable<DetailModel> _model;
    readonly bool _show;
    readonly Services _svc;
    readonly PlaybackBridge? _bridge;
    // Track-list view state, so the embedded TrackList (the SAME virtualized list + cell as the full detail page) has its
    // own per-pane sort/filter/density. The route is fixed to kind=Album (the pane only ever shows album/release tracks);
    // the actual cfg — Album vs Compilation (various-artists → per-track artist subline) — is re-derived from the loaded
    // model's ReleaseKind inside TrackList, so a compilation in the library shows artists just like its detail page would.
    readonly Signal<TrackSort> _sort = new(TrackSort.Default);
    readonly Signal<string> _query = new("");
    readonly Signal<TrackFilterFlags> _flags = new(TrackFilterFlags.None);
    readonly Signal<int> _density = new(1);
    readonly Signal<Route> _trackRoute = new(new Route("album:lib"));
    public LibraryDetailPane(Loadable<DetailModel> model, bool show, Services svc, PlaybackBridge? bridge)
    { _model = model; _show = show; _svc = svc; _bridge = bridge; }

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var lib = UseContext(LibraryBridge.Slot);
        var st = (LoadState)_model.State.Value;   // subscribe
        var m = _model.Value.Value;               // subscribe
        if (st != LoadState.Ready || m is null || m.Title.Length == 0) return Skeleton();

        string uri = m.ContextUri ?? "";
        void Play() { if (uri.Length > 0) _ = _svc.Player.PlayAsync(uri, 0); }
        void Shuffle() { if (uri.Length > 0) { _ = _svc.Player.SetShuffleAsync(true); _ = _svc.Player.PlayAsync(uri, 0); } }

        // Podcast show → the compact episode list (episodes aren't tracks). Album / release → the SAME virtualized
        // TrackList the detail page uses, embedded (no album trailing, no toolbar — the pane owns the hero + actions
        // above). So the rows get the identical cell: number↔play/pause on hover, the now-playing equalizer, the per-row
        // heart, art/columns the tier system fits to the pane width, and multi-select — image #2 now matches image #3.
        Element body = _show
            ? ScrollView(CompactEpisodes(m.Episodes ?? Array.Empty<Episode>(), i => { if (uri.Length > 0) _ = _svc.Player.PlayAsync(uri, i); })) with { Grow = 1f, AutoEdgeFade = true }
            : Embed.Comp(() => new TrackList(_trackRoute, _model, _bridge, TrackHandlers(go, lib), showToolbar: false, embedded: true));

        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [Hero(m), Actions(uri, Play, Shuffle), body],
        };
    }

    // Minimal handlers for the embedded TrackList. They act on the pane's CURRENT model (read at call-time via _model, so
    // a later selection is honoured even though the component froze these at mount) + the host navigation. Sort/filter/
    // density are per-pane signals; the list's own toolbar is hidden, so they just carry sensible defaults.
    DetailHandlers TrackHandlers(Action<string, string?> go, LibraryBridge? lib)
    {
        DetailModel? Cur() => _model.Value.Peek();
        string Ctx() => Cur()?.ContextUri ?? "";
        void Play(int i) { var u = Ctx(); if (u.Length > 0) _ = _svc.Player.PlayAsync(u, Math.Max(0, i)); }
        void Shuffle() { var u = Ctx(); if (u.Length > 0) { _ = _svc.Player.SetShuffleAsync(true); _ = _svc.Player.PlayAsync(u, 0); } }
        void PlayContext(string u) { if (u.Length > 0) _ = _svc.Player.PlayAsync(u, 0); }
        void AddToQueue()
        {
            var m = Cur(); if (m is null) return;
            int n = Math.Min(m.Tracks.Count, 50);
            for (int i = 0; i < n; i++) _ = _svc.Player.EnqueueAsync(m.Tracks[i].Uri);
            if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
        }
        void AddToPlaylist()
        {
            var m = Cur(); if (lib is null || m is null || m.Tracks.Count == 0) return;
            var (plUri, plName) = lib.AddToDefaultPlaylist(m.Tracks);
            Toasts.Show(Strings.Detail.AddedToPlaylist(plName), ToastSeverity.Success,
                actionLabel: Loc.Get(Strings.Detail.GoToPlaylist), onAction: () => go("pl:" + plUri, plName));
        }
        return new DetailHandlers(Play, () => Play(0), Shuffle, PlayContext, go, Tok.AccentDefault,
            _sort, s => _sort.Value = s, _query, _flags, f => _flags.Value = f, _density, d => _density.Value = d,
            AddToQueue, AddToPlaylist);
    }

    Element Hero(DetailModel m) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.M),
        Children =
        [
            new BoxEl { Width = 104f, Height = 104f, Shrink = 0f, Corners = CornerRadius4.All(8f), ClipToBounds = true, Shadow = Elevation.Card,
                Children = [Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, 104f, 104f, 8f, decodePx: 256)] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 3f,
                Children =
                [
                    new TextEl(m.BadgeType ?? (_show ? Loc.Get(Strings.Podcast.Show) : "")) { Size = 11f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 50f },
                    new TextEl(m.Title) { Size = 23f, Weight = 800, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(m.Publisher ?? DetailFormat.ArtistNames(m.Artists)) { Size = 13f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(m.MetaLine) { Size = 12f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ] },
        ],
    };

    Element Actions(string uri, Action play, Action shuffle) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.XL, 0f, WaveeSpace.XL, WaveeSpace.M),
        Children =
        [
            new BoxEl { Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Corners = CornerRadius4.All(20f), Padding = new Edges4(18f, 9f, 18f, 9f),
                Fill = Tok.AccentDefault, HoverScale = 1.04f, PressScale = 0.97f, Shadow = Elevation.Card, OnClick = play,
                Children = [Icon(Icons.Play, 14f, Tok.TextOnAccentPrimary), new TextEl(Loc.Get(Strings.Detail.Play)) { Size = 14f, Weight = 700, Color = Tok.TextOnAccentPrimary }] },
            Fab(Icons.Shuffle, shuffle),
            _show ? Embed.Comp(() => new FollowButton(uri)) : Embed.Comp(() => new SaveButton(uri)),
        ],
    };

    static Element Fab(string glyph, Action onClick) => new BoxEl
    {
        Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(20f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, HoverScale = 1.06f, PressScale = 0.94f, OnClick = onClick,
        Children = [Icon(glyph, 16f, Tok.TextSecondary)],
    };

    static Element CompactEpisodes(IReadOnlyList<Episode> eps, Action<int> onPlay)
    {
        var rows = new Element[eps.Count];
        for (int i = 0; i < eps.Count; i++)
        {
            int idx = i; var e = eps[i];
            rows[i] = new BoxEl
            {
                Direction = 0, MinHeight = 56f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.S, WaveeSpace.S), Corners = CornerRadius4.All(6f),
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = () => onPlay(idx),
                Children =
                [
                    new BoxEl { Width = 32f, Height = 32f, Shrink = 0f, Corners = CornerRadius4.All(16f), Fill = Tok.FillSubtleSecondary, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [Icon(Icons.Play, 12f, Tok.TextSecondary)] },
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                        Children =
                        [
                            new TextEl(e.Title) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(DetailFormat.TrackTime(e.DurationMs)) { Size = 11f, Color = Tok.TextTertiary },
                        ] },
                ],
            };
        }
        return new BoxEl { Direction = 1, Gap = 2f, Padding = new Edges4(WaveeSpace.M, 0f, WaveeSpace.M, PlayerDock.Reserve + WaveeSpace.XL), Children = rows };
    }

    static Element Skeleton() => new BoxEl
    {
        Direction = 1, Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL), Gap = WaveeSpace.L,
        Children =
        [
            new BoxEl { Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
                Children = [new BoxEl { Width = 104f, Height = 104f, Corners = CornerRadius4.All(8f), Fill = Tok.FillCardDefault },
                    new BoxEl { Direction = 1, Grow = 1f, Gap = WaveeSpace.S, Children = [new BoxEl { Width = 80f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }, new BoxEl { Width = 200f, Height = 22f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }, new BoxEl { Width = 140f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }] }] },
            new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = Enumerable.Range(0, 6).Select(_ => (Element)new BoxEl { Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault, Margin = new Edges4(0f, WaveeSpace.S, 0f, 0f) }).ToArray() },
        ],
    };
}

// The compact artist pane (WaveeMusic ArtistsLibraryView column 2): a circular hero + actions + an "In library /
// Discography" toggle + the artist's releases. Picking a release sets the host's _albumKey → the 3rd column (tracks).
sealed class LibraryArtistPane : Component
{
    readonly Loadable<Artist> _artist;
    readonly Services _svc;
    readonly PlaybackBridge? _bridge;
    readonly Signal<string> _albumKey;
    readonly Signal<int> _mode = new(1);   // 0 In library · 1 Discography (both show TopAlbums for the synth catalog)
    readonly SelectionModel _discoSel = new();   // discography grid single-selection (drives the 3rd column)
    public LibraryArtistPane(Loadable<Artist> artist, Services svc, PlaybackBridge? bridge, Signal<string> albumKey)
    { _artist = artist; _svc = svc; _bridge = bridge; _albumKey = albumKey; }

    public override Element Render()
    {
        var st = (LoadState)_artist.State.Value;   // subscribe
        var a = _artist.Value.Value;               // subscribe
        var albums = a?.TopAlbums ?? Array.Empty<Album>();
        // Keep the discography selection synced to the chosen release — UNCONDITIONAL hook, BEFORE any early return (else
        // the effect-slot count changes when the artist flips Pending→Ready → an out-of-range hook crash).
        UseEffect(() => SyncDisco(albums), _albumKey.Value + "|" + albums.Count + "|" + (albums.Count > 0 ? albums[0].Uri : ""));

        if (st != LoadState.Ready || a is null || a.Name.Length == 0) return Skeleton();

        int mode = _mode.Value;                    // subscribe
        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children =
            [
                Hero(a),
                Actions(a.Uri),
                new BoxEl { Padding = new Edges4(WaveeSpace.XL, 0f, WaveeSpace.XL, WaveeSpace.M), Children = [SelectorBar.Create([Loc.Get(Strings.Library.InLibrary), Loc.Get(Strings.Library.FullDiscography)], mode, i => _mode.Value = i)] },
                Discography(albums),
            ],
        };
    }

    Element Hero(Artist a) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.M),
        Children =
        [
            new BoxEl { Width = 104f, Height = 104f, Shrink = 0f, Corners = CornerRadius4.All(52f), ClipToBounds = true, Shadow = Elevation.Card,
                Children = [Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 104f, 104f, 52f, decodePx: 256)] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 3f,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Search.TypeArtist).ToUpperInvariant()) { Size = 11f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 50f },
                    new TextEl(a.Name) { Size = 23f, Weight = 800, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(Strings.Artist.MonthlyListeners(a.MonthlyListeners.ToString("N0"))) { Size = 12f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ] },
        ],
    };

    Element Actions(string uri) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.XL, 0f, WaveeSpace.XL, WaveeSpace.M),
        Children =
        [
            new BoxEl { Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Corners = CornerRadius4.All(20f), Padding = new Edges4(18f, 9f, 18f, 9f),
                Fill = Tok.AccentDefault, HoverScale = 1.04f, PressScale = 0.97f, Shadow = Elevation.Card, OnClick = () => { _ = _svc.Player.PlayAsync(uri, 0); },
                Children = [Icon(Icons.Play, 14f, Tok.TextOnAccentPrimary), new TextEl(Loc.Get(Strings.Artist.Play)) { Size = 14f, Weight = 700, Color = Tok.TextOnAccentPrimary }] },
            Embed.Comp(() => new FollowButton(uri)),
        ],
    };

    // A GRID of release cards (WaveeMusic's artist discography is a grid, not a list). Tapping a card selects it for the
    // 3rd column (its tracks).
    // A GRID of release cards (WaveeMusic's artist discography is a grid, not a list) — responsive: cards size to fill the
    // pane width exactly and to their own content height (no clip). Tapping a card selects it for the 3rd column.
    // The discography is an ItemsView grid (proper WinUI selection chrome) — single-select drives the 3rd column. Keyed by
    // the album set so an artist change remounts it; responsive cols via Responsive.Of; it self-scrolls (no outer ScrollView).
    Element Discography(IReadOnlyList<Album> albums) => new BoxEl
    {
        Key = "disco:" + albums.Count + ":" + (albums.Count > 0 ? albums[0].Uri : ""),
        Grow = 1f, Basis = 0f, MinHeight = 0f, Direction = 1, ClipToBounds = true,
        Padding = new Edges4(WaveeSpace.M, 0f, WaveeSpace.M, 0f),
        Children = [ItemsView.Create(albums.Count, i => DiscoCardContent(albums[i]), RepeatLayout.GridFit(124f, 8f),
            selectionMode: ItemsSelectionMode.Single, selection: _discoSel, selector: SelectorVisual.Border,
            selectionChanged: () => { int i = _discoSel.FirstSelectedIndex; if (i >= 0 && i < albums.Count) _albumKey.Value = "album:" + albums[i].Uri; },
            grow: 1f)],
    };

    void SyncDisco(IReadOnlyList<Album> albums)
    {
        string ak = _albumKey.Peek();
        int idx = -1;
        if (ak.Length > 0) for (int i = 0; i < albums.Count; i++) if ("album:" + albums[i].Uri == ak) { idx = i; break; }
        if (idx < 0) { if (_discoSel.SelectedCount > 0) _discoSel.DeselectAll(); }
        else if (_discoSel.FirstSelectedIndex != idx) _discoSel.Select(idx);
    }

    static Element DiscoCardContent(Album al) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.XS, ClipToBounds = true, Padding = new Edges4(WaveeSpace.XS, WaveeSpace.XS, WaveeSpace.XS, WaveeSpace.XS),
        Children =
        [
            Surfaces.ArtworkFill(al.Cover, 6f),
            new TextEl(al.Name) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl((al.Year > 0 ? al.Year + " · " : "") + KindLabel(al.Kind)) { Size = 11f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    static string KindLabel(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };

    static Element Skeleton() => new BoxEl
    {
        Direction = 1, Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL), Gap = WaveeSpace.L,
        Children =
        [
            new BoxEl { Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
                Children = [new BoxEl { Width = 104f, Height = 104f, Corners = CornerRadius4.All(52f), Fill = Tok.FillCardDefault },
                    new BoxEl { Direction = 1, Grow = 1f, Gap = WaveeSpace.S, Children = [new BoxEl { Width = 200f, Height = 22f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }, new BoxEl { Width = 140f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }] }] },
            new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = Enumerable.Range(0, 5).Select(_ => (Element)new BoxEl { Height = 48f, Corners = CornerRadius4.All(6f), Fill = Tok.FillCardDefault }).ToArray() },
        ],
    };
}
