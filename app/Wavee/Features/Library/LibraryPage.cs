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
// resizable. Selection drives the panes via stable per-selection loadables (UseResource re-driven by the selection
// key), so picking a different item reactively re-skins the pane in place — no navigation, no stale freeze.
sealed class LibraryPage : Component
{
    readonly string _kind;   // "albums" | "artists" | "podcasts"

    readonly IAppSettings? _settings;                  // per-kind persisted state (seed in ctor, save on change)
    readonly Signal<string> _selectedKey;              // selected item route key (album:/artist:/show: + uri)
    readonly Signal<string> _albumKey;                 // artists only: the release picked in the discography (3rd column)
    readonly Signal<int> _view;                        // 0 CompactList · 1 List · 2 CompactGrid · 3 Grid
    readonly Signal<int> _sort;                        // 0 Recents · 1 Recently added · 2 Alphabetical · 3 Creator · 4 Release date
    readonly Signal<bool> _desc;
    readonly Signal<int> _size;                        // grid card size: 0 S · 1 M · 2 L
    readonly Signal<string> _filter = new("");         // NOT persisted — the search filter starts empty each launch
    readonly Signal<float> _leftW, _midW;              // resizable column widths
    // Artists column-2 (discography) controls, mirrored from the left picker's set: sort/direction/view-type/grid-size.
    readonly Signal<int> _aSort, _aView, _aSize;
    readonly Signal<bool> _aDesc;
    readonly Signal<string> _aFilter = new("");        // NOT persisted
    readonly SelectionModel _navSel = new();           // master list/grid single-selection (the WinUI ItemsView selection)
    // Search-mode drill-down selection — INDEPENDENT of the browse selection so clearing the query restores browse.
    readonly Signal<string> _sArtist = new("");        // selected matched-artist uri (artists view)
    readonly Signal<string> _sAlbum = new("");         // selected matched-album uri
    readonly Signal<SearchSnapshot> _searchSnapshot = new(new("", LibrarySearchResults.Empty));
    Services? _svcRef;                                 // cached in Render → play a track from the search facets
    // Responsive collapse (F2): under a narrow content area the multi-column row becomes a single-column breadcrumb
    // drill-in. `_collapsed` is written from the root OnBoundsChanged (value-gated, hysteresis via LibraryLayoutBreakpoints);
    // `_depth` is the visible level (0 master · 1 discography/detail · 2 tracks). Both are ignored in the wide layout.
    readonly Signal<bool> _collapsed = new(false);
    readonly Signal<int> _depth = new(0);
    Action<string, string?>? _goRef;

    static readonly string[] NoSuggest = Array.Empty<string>();
    readonly record struct SearchSnapshot(string Query, LibrarySearchResults Results);
    // Immediate typeahead with small, local row motion. Stable URI keys let retained hits stay put; only actual
    // insert/remove/reorder changes animate, so fast typing never pulses the entire three-pane surface.
    static readonly LayoutTransition SearchRowChange = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(90f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: 3f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(70f, Easing.SmoothOut));

    // Seed every persisted signal from settings in the ctor (like the sidebar width) so the FIRST frame already uses the
    // saved widths/sort/view/selection — no default→saved flash. A null store (or a missing key) falls back to the key's
    // default. Filter signals are created empty above and never persisted.
    public LibraryPage(string kind, IAppSettings? settings = null)
    {
        _kind = kind; _settings = settings;
        float Gf(SettingKey<float> k) => settings is null ? k.Default : settings.Get(k);
        int Gi(SettingKey<int> k) => settings is null ? k.Default : settings.Get(k);
        bool Gb(SettingKey<bool> k) => settings is null ? k.Default : settings.Get(k);
        string Gs(SettingKey<string> k) => settings is null ? k.Default : settings.Get(k);

        _leftW = new(Gf(LibraryStateKeys.LeftW(kind)));
        _midW = new(Gf(LibraryStateKeys.MidW(kind)));
        _sort = new(Gi(LibraryStateKeys.Sort(kind)));
        _desc = new(Gb(LibraryStateKeys.Desc(kind)));
        _view = new(Gi(LibraryStateKeys.View(kind)));
        _size = new(Gi(LibraryStateKeys.Size(kind)));
        _selectedKey = new(Gs(LibraryStateKeys.Selected(kind)));
        _albumKey = new(Gs(LibraryStateKeys.AlbumKey(kind)));
        _aSort = new(Gi(LibraryStateKeys.AlbumSort(kind)));
        _aDesc = new(Gb(LibraryStateKeys.AlbumDesc(kind)));
        _aView = new(Gi(LibraryStateKeys.AlbumView(kind)));
        _aSize = new(Gi(LibraryStateKeys.AlbumSize(kind)));
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
        var ui = UseContext(ShellUi.Slot);   // rail state (Task B4): the 3-column artist layout tightens its mid pane when the rail is open
        _goRef = UseContext(HistoryStore.NavCtx);
        if (svc is null || store is null) return new BoxEl { Grow = 1f };
        _svcRef = svc;
        var shown = Filtered(Project(store));
        // Warm the collection cover art at the kind-matched decode size the moment the list lands, so a first scroll
        // reveals resident textures instead of decoding+uploading on the UI thread mid-scroll (the first-pass jank).
        // Prefetch priority → background workers (visible cards still decode first); idempotent (a re-hit cache entry is a
        // dictionary lookup), so a re-render costs nothing. The engine ImageCache + MemoryGovernor bound the residency.
        int warmPx = _size.Value == 0 ? 64 : _size.Value == 2 ? 256 : 168;
        foreach (var it in shown)
            if (it.Cover?.Url is { Length: > 0 } warmUrl) PrefetchImage(warmUrl, warmPx);
        // Full-text library search REPLACES the browse title-filter for the album/artist views: typing searches the
        // followed artists ▸ their albums ▸ tracks (artists view) or saved albums ▸ tracks (albums view), grouped +
        // highlighted. Podcasts keep the plain title filter. Cache-only + off-thread; keyed on kind+query so it re-drives
        // per query only. Placed with the other loads below (fixed hook order); the flag/query are computed here.
        string query = _filter.Value.Trim();   // subscribe
        // Full search needs the persistent store-backed catalog (its cached discographies + tracklists). On the fake/demo
        // backend (RealStore null) it has no corpus, so we keep the plain browse title-filter there instead of a dead
        // "Nothing matches". Podcasts always keep the title filter.
        bool fullSearch = query.Length > 0 && _kind != "podcasts" && svc.RealStore is not null;
        UseEffect(() =>
        {
            if (!fullSearch) _searchSnapshot.Value = new SearchSnapshot("", LibrarySearchResults.Empty);
        }, fullSearch);

        // Keep the ItemsView SelectionModel pointed at the selected item across load/filter/sort/view + auto-select first.
        // Skips while the search results view is up — selection there is driven by result clicks. Includes fullSearch in
        // the key so toggling in/out of search re-syncs the browse selection.
        UseEffect(() => SyncNav(shown, fullSearch), NavHash(shown) + "|" + _selectedKey.Value + "|" + fullSearch);
        // Persist per-kind page state (column widths persist on drag-end via the grips, NOT here). Keyed on a composite of
        // every persisted signal so it writes only on discrete user actions — never per-frame. Filter is excluded on purpose.
        UseEffect(SaveState, $"{_sort.Value}|{_desc.Value}|{_view.Value}|{_size.Value}|{_selectedKey.Value}|{_albumKey.Value}|{_aSort.Value}|{_aDesc.Value}|{_aView.Value}|{_aSize.Value}");

        string sel = _selectedKey.Value;   // subscribe
        bool artists = IsArtists;
        bool railOpen = ui?.RailOpen.Value ?? false;   // subscribe → re-render (tighter mid pane) on a rail toggle
        string albumKey = _albumKey.Value;   // subscribe

        // Hooks must NEVER be branched. All three kinds are the same LibraryPage type, so a branched hook count let the
        // reconciler reuse a sibling's hook slot → an EffectCell→AsyncResourceCell cast crash. Call all three loads
        // unconditionally in a FIXED order; the off-kind ones key on "" → resolve to Empty with no real fetch.
        var detail = UseResource(ct => LoadDetail(svc, artists ? "" : sel, ct), DetailModel.Empty, artists ? "" : sel).Loadable;
        var artist = UseResource(ct => LoadArtist(svc, artists ? sel : "", ct), EmptyArtist(""), artists ? sel : "").Loadable;
        var albumTracks = UseResource(ct => LoadDetail(svc, artists ? albumKey : "", ct), DetailModel.Empty, artists ? albumKey : "").Loadable;
        var search = UseResource(ct => SearchLib(svc, _kind, fullSearch ? query : "", ct), LibrarySearchResults.Empty,
            fullSearch ? _kind + "|" + query : "").Loadable;

        // Stale-while-revalidate: publish only complete results. The previous rows stay mounted while the next query is
        // pending, avoiding the three-pane rows -> ellipsis -> rows flash on every keystroke.
        bool completedSearch = fullSearch && query.Length > 0 && (LoadState)search.State.Value == LoadState.Ready;
        var completedResults = completedSearch ? search.Value.Value : LibrarySearchResults.Empty;
        UseEffect(() =>
        {
            if (completedSearch) _searchSnapshot.Value = new SearchSnapshot(query, completedResults);
        }, DepKey.From(HashCode.Combine(completedSearch, query, completedResults)));

        // Resolve the hierarchical results once (subscribes). They drive a DRILL-DOWN across the master-detail columns:
        // matched artists (left) ▸ the selected artist's matched albums (middle) ▸ the selected album's matched tracks
        // (right). Auto-select the first artist/album so results appear immediately. Browse selection is untouched (only
        // an explicit click commits into it), so clearing the query restores wherever the user was.
        var searchSnapshot = _searchSnapshot.Value;
        bool searchReady = fullSearch && searchSnapshot.Query.Length > 0;
        var sr = fullSearch ? searchSnapshot.Results : LibrarySearchResults.Empty;
        string rhash = sr.Artists.Count + ":" + sr.Albums.Count + ":" + searchSnapshot.Query;
        string sArtist = _sArtist.Value;   // subscribe
        string sAlbum = _sAlbum.Value;     // subscribe

        UseEffect(() => AutoSelectTop(sr, fullSearch), "sauto|" + fullSearch + "|" + rhash);
        UseEffect(() => AutoSelectAlbum(sr, fullSearch && artists), "salbum|" + sArtist + "|" + rhash);

        // F2 — responsive collapse. `_collapsed` is written from the root's OnBoundsChanged below; entering collapsed
        // resets the drill-in to the master list (depth 0) so a narrow window always starts at the list.
        bool collapsed = _collapsed.Value;   // subscribe
        UseEffect(() => { if (collapsed) _depth.Value = 0; }, "collapse|" + collapsed);

        Element inner;
        if (collapsed)
            inner = CollapsedLayout(shown, sr, searchReady, fullSearch, sArtist, sAlbum, svc, bridge, artist, albumTracks, detail);
        else
        {
            Element right = fullSearch
                ? (artists ? SearchArtistColumns(sr, searchReady, sArtist, sAlbum, railOpen) : SearchAlbumDetail(sr, searchReady, sAlbum))
                : (artists ? ArtistColumns(artist, albumTracks, svc, bridge, sel.Length > 0, railOpen)
                           : DetailColumn(detail, svc, bridge, sel.Length > 0));
            inner = new BoxEl
            {
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch,
                Children = [LeftColumn(shown, sr, searchReady, fullSearch, sArtist, sAlbum), Grip(_leftW, 240f, 560f, () => _settings?.Set(LibraryStateKeys.LeftW(_kind), _leftW.Peek())), right],
            };
        }

        // Self-measure the content-area width (the real slot — accounts for the sidebar/rail without any ShellUi math) and
        // flip `_collapsed` across the breakpoint. Value-gated write → re-renders only on a boundary cross, no feedback loop.
        return new BoxEl
        {
            Direction = 1, Grow = 1f, AlignItems = FlexAlign.Stretch,
            OnBoundsChanged = r =>
            {
                if (r.W <= 0f) return;
                bool c = LibraryLayoutBreakpoints.Collapsed(r.W, _collapsed.Peek());
                if (c != _collapsed.Peek()) _collapsed.Value = c;
            },
            Children = [inner],
        };
    }

    void DrillToTracks() { if (_collapsed.Peek()) _depth.Value = 2; }

    // ── F2: collapsed single-column drill-in (master ▸ discography/detail ▸ tracks) with breadcrumbs ──
    Element CollapsedLayout(NavItem[] shown, LibrarySearchResults sr, bool searchReady, bool fullSearch,
        string sArtist, string sAlbum, Services svc, PlaybackBridge? bridge,
        Loadable<Artist> artist, Loadable<DetailModel> albumTracks, Loadable<DetailModel> detail)
    {
        int maxDepth = IsArtists ? 2 : 1;
        int depth = Math.Clamp(_depth.Value, 0, maxDepth);   // subscribe

        // Crumb names come from the live loadables (browse) or the matched groups (search); "…" while a name loads.
        string level1 = IsArtists
            ? Crumb(fullSearch ? (FindArtist(sr, sArtist)?.Name ?? "") : artist.Value.Value.Name)
            : Crumb(fullSearch ? (FindAlbum(sr.Albums, sAlbum)?.Name ?? "") : detail.Value.Value.Title);
        string level2 = Crumb(fullSearch
            ? (FindAlbum(FindArtist(sr, sArtist)?.Albums ?? Array.Empty<LibraryAlbumGroup>(), sAlbum)?.Name ?? "")
            : albumTracks.Value.Value.Title);

        var crumbs = new List<string>(3) { KindCrumb() };
        if (depth >= 1) crumbs.Add(level1);
        if (depth >= 2) crumbs.Add(level2);

        Element body = depth == 0
            ? new BoxEl { Direction = 1, Grow = 1f, Children = [Toolbar(), fullSearch ? LeftSearchBody(sr, searchReady, sArtist, sAlbum) : ListBody(shown)] }
            : depth == 1
                ? (IsArtists ? CollapsedDiscography(sr, searchReady, fullSearch, sArtist, sAlbum, artist)
                             : CollapsedDetail(sr, searchReady, fullSearch, sAlbum, detail, svc, bridge))
                : CollapsedTracks(sr, searchReady, fullSearch, sArtist, sAlbum, albumTracks, svc, bridge);

        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [CollapsedCrumbBar(crumbs), body],
        };
    }

    Element CollapsedCrumbBar(IReadOnlyList<string> crumbs) => new BoxEl
    {
        Direction = 1, Shrink = 0f, Fill = Tok.FillLayerDefault,
        Children =
        [
            new BoxEl { Padding = new Edges4(WaveeSpace.M, WaveeSpace.S, WaveeSpace.M, WaveeSpace.S),
                Children = [BreadcrumbBar.Create(crumbs, i => _depth.Value = i)] },
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    // Artists view, depth 1 — the selected artist's discography (browse) or matched albums (search).
    Element CollapsedDiscography(LibrarySearchResults sr, bool ready, bool fullSearch, string sArtist, string sAlbum, Loadable<Artist> artist) => Pane with
    {
        Key = "col:disco", Grow = 1f, Basis = 0f,
        Children = [ fullSearch
            ? (ready ? SearchScroll(FindArtist(sr, sArtist)?.Albums ?? Array.Empty<LibraryAlbumGroup>(), a => AlbumRow(a, a.Uri == sAlbum)) : SearchMessage("…"))
            : Embed.Comp(() => new LibraryArtistPane(artist, _albumKey, _aSort, _aDesc, _aView, _aSize, _aFilter, onDrill: DrillToTracks)) ],
    };

    // Artists view, depth 2 — the selected album's tracks (browse detail pane) or matched tracks (search).
    Element CollapsedTracks(LibrarySearchResults sr, bool ready, bool fullSearch, string sArtist, string sAlbum,
        Loadable<DetailModel> albumTracks, Services svc, PlaybackBridge? bridge)
    {
        if (fullSearch)
        {
            var albG = FindAlbum(FindArtist(sr, sArtist)?.Albums ?? Array.Empty<LibraryAlbumGroup>(), sAlbum);
            var tracks = albG?.Tracks ?? Array.Empty<LibraryTrackHit>();
            string albumUri = albG?.Uri ?? "";
            return Pane with { Key = "col:tracks", Grow = 1f, Basis = 0f,
                Children = [ ready ? SearchScroll(tracks, t => TrackHitRow(t, albumUri)) : SearchMessage("…") ] };
        }
        return Pane with { Key = "col:tracks", Grow = 1f, Basis = 0f,
            Children = [Embed.Comp(() => new LibraryDetailPane(albumTracks, false, svc, bridge))] };
    }

    // Albums/podcasts view, depth 1 — the album/show detail (browse) or the album's matched tracks (search).
    Element CollapsedDetail(LibrarySearchResults sr, bool ready, bool fullSearch, string sAlbum,
        Loadable<DetailModel> detail, Services svc, PlaybackBridge? bridge)
    {
        if (fullSearch)
        {
            var albG = FindAlbum(sr.Albums, sAlbum);
            var tracks = albG?.Tracks ?? Array.Empty<LibraryTrackHit>();
            string albumUri = albG?.Uri ?? "";
            return Pane with { Key = "col:detail", Grow = 1f, Basis = 0f,
                Children = [ ready ? SearchScroll(tracks, t => TrackHitRow(t, albumUri)) : SearchMessage("…") ] };
        }
        return Pane with { Key = "col:detail", Grow = 1f, Basis = 0f,
            Children = [Embed.Comp(() => new LibraryDetailPane(detail, _kind == "podcasts", svc, bridge))] };
    }

    string KindCrumb() => _kind switch
    {
        "artists" => Loc.Get(Strings.Search.Artists),
        "albums" => Loc.Get(Strings.Search.Albums),
        _ => Loc.Get(Strings.Podcast.Show),
    };
    static string Crumb(string s) => s.Length > 0 ? s : "…";

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

    // Persist the current per-kind page state (invoked by the composite-keyed effect on any change). Widths are handled
    // separately (drag-end). Album-side keys only apply to the artists (3-column) view.
    void SaveState()
    {
        if (_settings is null) return;
        _settings.Set(LibraryStateKeys.Sort(_kind), _sort.Peek());
        _settings.Set(LibraryStateKeys.Desc(_kind), _desc.Peek());
        _settings.Set(LibraryStateKeys.View(_kind), _view.Peek());
        _settings.Set(LibraryStateKeys.Size(_kind), _size.Peek());
        _settings.Set(LibraryStateKeys.Selected(_kind), _selectedKey.Peek());
        if (IsArtists)
        {
            _settings.Set(LibraryStateKeys.AlbumKey(_kind), _albumKey.Peek());
            _settings.Set(LibraryStateKeys.AlbumSort(_kind), _aSort.Peek());
            _settings.Set(LibraryStateKeys.AlbumDesc(_kind), _aDesc.Peek());
            _settings.Set(LibraryStateKeys.AlbumView(_kind), _aView.Peek());
            _settings.Set(LibraryStateKeys.AlbumSize(_kind), _aSize.Peek());
        }
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
    Element LeftColumn(NavItem[] shown, LibrarySearchResults sr, bool searchReady, bool fullSearch, string sArtist, string sAlbum) => NavPanel with
    {
        Width = _leftW.Value, Shrink = 0f,
        // Searching swaps the browse list for the top-level matches — matched artists (artists view) or matched albums
        // (albums view); the detail columns drill into the selection. Otherwise the normal self-scrolling ItemsView.
        Children = [Toolbar(), fullSearch ? LeftSearchBody(sr, searchReady, sArtist, sAlbum) : ListBody(shown)],
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
    void OnNavSelIdx(NavItem[] shown, int i)
    {
        if (i < 0 || i >= shown.Length) return;
        Select(shown[i]);
        if (_collapsed.Peek()) _depth.Value = 1;   // collapsed: tapping a master item drills into it
    }

    void SyncNav(NavItem[] shown, bool fullSearch)
    {
        if (fullSearch) return;   // search results view drives selection via clicks (+ the search-empty clear effect)
        if (shown.Length == 0)
        {
            // A title filter (podcasts) that matched nothing → drop the stale selection so the right pane clears. An
            // empty set with NO filter text is the still-loading state — keep the persisted selection (launch restore).
            if (_filter.Peek().Length > 0 && _selectedKey.Peek().Length > 0)
            {
                _selectedKey.Value = "";
                if (IsArtists) _albumKey.Value = "";
                if (_navSel.SelectedCount > 0) _navSel.DeselectAll();
            }
            return;
        }
        string key = _selectedKey.Peek();
        int idx = key.Length == 0 ? 0 : Array.FindIndex(shown, it => it.RouteKey == key);
        if (idx < 0) idx = 0;
        if (key.Length == 0) Select(shown[idx]);
        if (_navSel.FirstSelectedIndex != idx) _navSel.Select(idx);
    }

    // ── search-mode selection (drill-down) ──
    void AutoSelectTop(LibrarySearchResults sr, bool fullSearch)
    {
        if (!fullSearch) return;
        if (IsArtists)
        {
            if (sr.Artists.Count == 0) { if (_sArtist.Peek().Length > 0) { _sArtist.Value = ""; _sAlbum.Value = ""; } return; }
            if (FindArtist(sr, _sArtist.Peek()) is null) { _sArtist.Value = sr.Artists[0].Uri; _sAlbum.Value = ""; }
        }
        else
        {
            if (sr.Albums.Count == 0) { if (_sAlbum.Peek().Length > 0) _sAlbum.Value = ""; return; }
            if (FindAlbum(sr.Albums, _sAlbum.Peek()) is null) _sAlbum.Value = sr.Albums[0].Uri;
        }
    }

    void AutoSelectAlbum(LibrarySearchResults sr, bool active)
    {
        if (!active) return;
        var g = FindArtist(sr, _sArtist.Peek());
        var albums = g?.Albums;
        if (albums is not { Count: > 0 }) { if (_sAlbum.Peek().Length > 0) _sAlbum.Value = ""; return; }
        if (FindAlbum(albums, _sAlbum.Peek()) is null) _sAlbum.Value = albums[0].Uri;
    }

    // Search hits are destinations: route through the shell so Back returns to this query and the destination/tab label
    // uses the matched display name.
    void SelectArtist(string uri, string name)
    {
        _sArtist.Value = uri; _sAlbum.Value = ""; _selectedKey.Value = "artist:" + uri; _albumKey.Value = "";
        _goRef?.Invoke("artist:" + uri, name);
    }
    void SelectAlbum(string uri, string name)
    {
        _sAlbum.Value = uri;
        if (IsArtists) _albumKey.Value = "album:" + uri; else _selectedKey.Value = "album:" + uri;
        _goRef?.Invoke("album:" + uri, name);
    }
    void PlayTrack(string albumUri, int index)
    {
        if (albumUri.Length > 0 && _svcRef is { } svc) _ = svc.Player.PlayAsync(albumUri, System.Math.Max(0, index));
    }

    static LibraryArtistGroup? FindArtist(LibrarySearchResults sr, string uri)
    { if (uri.Length == 0) return null; foreach (var a in sr.Artists) if (a.Uri == uri) return a; return null; }
    static LibraryAlbumGroup? FindAlbum(IReadOnlyList<LibraryAlbumGroup> albums, string uri)
    { if (uri.Length == 0) return null; foreach (var a in albums) if (a.Uri == uri) return a; return null; }

    // ── search-mode column bodies ──
    static Task<LibrarySearchResults> SearchLib(Services svc, string kind, string query, CancellationToken ct)
    {
        if (query.Length == 0) return Task.FromResult(LibrarySearchResults.Empty);
        var scope = kind == "artists" ? LibrarySearchScope.Artists : LibrarySearchScope.Albums;
        return svc.Library.SearchLibraryAsync(query, scope, ct);
    }

    // Left column while searching: the top-level matches (artists, or albums in the albums view).
    Element LeftSearchBody(LibrarySearchResults sr, bool ready, string sArtist, string sAlbum)
    {
        if (!ready) return SearchMessage("…");
        if (IsArtists)
            return sr.Artists.Count == 0 ? SearchMessage(Loc.Get(Strings.Library.NoMatch))
                : SearchScroll(sr.Artists, g => ArtistRow(g, g.Uri == sArtist));
        return sr.Albums.Count == 0 ? SearchMessage(Loc.Get(Strings.Library.NoMatch))
            : SearchScroll(sr.Albums, g => AlbumRow(g, g.Uri == sAlbum));
    }

    // Artists-view detail columns while searching: the selected artist's albums | grip | the selected album's tracks.
    Element SearchArtistColumns(LibrarySearchResults sr, bool ready, string sArtist, string sAlbum, bool railOpen)
    {
        var albums = FindArtist(sr, sArtist)?.Albums ?? Array.Empty<LibraryAlbumGroup>();
        var albG = FindAlbum(albums, sAlbum);
        var tracks = albG?.Tracks ?? Array.Empty<LibraryTrackHit>();
        string albumUri = albG?.Uri ?? "";

        Element albumPane = Pane with
        {
            Key = "s:albums", Basis = _midW.Value, MinWidth = railOpen ? 220f : 300f, MaxWidth = _midW.Value, Shrink = 1f, Grow = 0f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Albums), ready ? albums.Count : -1),
                        ready ? SearchScroll(albums, a => AlbumRow(a, a.Uri == sAlbum)) : SearchMessage("…")],
        };
        Element trackPane = Pane with
        {
            Key = "s:tracks", Grow = 1f, Basis = 0f, MinWidth = 220f, Shrink = 1f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Songs), ready ? tracks.Count : -1),
                        ready ? SearchScroll(tracks, t => TrackHitRow(t, albumUri)) : SearchMessage("…")],
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
            Children = [albumPane, Grip(_midW, 300f, 620f, () => _settings?.Set(LibraryStateKeys.MidW(_kind), _midW.Peek())), trackPane],
        };
    }

    // Albums-view single right pane while searching: the selected album's matched tracks.
    Element SearchAlbumDetail(LibrarySearchResults sr, bool ready, string sAlbum)
    {
        var albG = FindAlbum(sr.Albums, sAlbum);
        var tracks = albG?.Tracks ?? Array.Empty<LibraryTrackHit>();
        string albumUri = albG?.Uri ?? "";
        return Pane with
        {
            Key = "s:detail", Grow = 1f, Basis = 0f,
            Children = [FacetHeader(Loc.Get(Strings.Search.Songs), ready ? tracks.Count : -1),
                        ready ? SearchScroll(tracks, t => TrackHitRow(t, albumUri)) : SearchMessage("…")],
        };
    }

    static Element SearchScroll<T>(IReadOnlyList<T> items, Func<T, Element> row)
    {
        var rows = new Element[items.Count];
        for (int i = 0; i < items.Count; i++) rows[i] = row(items[i]);
        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = 2f,
            Padding = new Edges4(WaveeSpace.S, WaveeSpace.XS, WaveeSpace.S, PlayerDock.Reserve + WaveeSpace.XL),
            Children = rows,
        }) with { Grow = 1f };
    }

    static Element FacetHeader(string label, int count) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.XS,
        Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.S),
        Children =
        [
            new TextEl(label) { Size = 11f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 50f },
            count >= 0 ? new TextEl(count.ToString()) { Size = 11f, Weight = 700, Color = Tok.TextTertiary } : new BoxEl(),
        ],
    };

    static Element SearchMessage(string text) => new BoxEl
    {
        Padding = new Edges4(WaveeSpace.M, WaveeSpace.XL, WaveeSpace.M, WaveeSpace.XL),
        Children = [new TextEl(text) { Size = 13f, Color = Tok.TextTertiary }],
    };

    // ── search-mode rows ──
    Element ArtistRow(LibraryArtistGroup g, bool selected) =>
        SelectableRow(g.Image, g.Uri, g.Name, "", circular: true, selected, g.MatchStart, g.MatchLen, () => SelectArtist(g.Uri, g.Name));

    Element AlbumRow(LibraryAlbumGroup g, bool selected) =>
        SelectableRow(g.Cover, g.Uri, g.Name, (g.Year > 0 ? g.Year + " · " : "") + KindLabelOf(g.Kind), circular: false, selected, g.MatchStart, g.MatchLen, () => SelectAlbum(g.Uri, g.Name));

    static Element SelectableRow(Image? cover, string uri, string title, string subtitle, bool circular, bool selected,
        int matchStart, int matchLen, Action onClick)
    {
        var textKids = new List<Element>(2) { HighlightRow(title, matchStart, matchLen, 14f, 600, Tok.TextPrimary) };
        if (subtitle.Length > 0)
            textKids.Add(new TextEl(subtitle) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        return new BoxEl
        {
            Key = "search:" + uri, Animate = SearchRowChange,
            Direction = 0, Height = 56f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, ClipToBounds = true,
            Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f), Corners = CornerRadius4.All(6f),
            Fill = selected ? Tok.AccentSubtle : ColorF.Transparent,
            HoverFill = selected ? Tok.AccentSubtle : Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = onClick,
            Children =
            [
                new BoxEl { Width = 44f, Height = 44f, Shrink = 0f, Corners = CornerRadius4.All(circular ? 22f : 5f), ClipToBounds = true,
                    Children = [Surfaces.Artwork(cover, uri.GetHashCode() & 0x7fffffff, 44f, 44f, circular ? 22f : 5f)] },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, ClipToBounds = true, Children = textKids.ToArray() },
            ],
        };
    }

    Element TrackHitRow(LibraryTrackHit t, string albumUri) => new BoxEl
    {
        Key = "search:" + t.Uri, Animate = SearchRowChange,
        Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, ClipToBounds = true,
        Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f), Corners = CornerRadius4.All(6f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => PlayTrack(albumUri, t.AlbumIndex),
        Children =
        [
            new BoxEl { Width = 36f, Height = 36f, Shrink = 0f, Corners = CornerRadius4.All(4f), ClipToBounds = true,
                Children = [Surfaces.Artwork(t.Cover, t.Uri.GetHashCode() & 0x7fffffff, 36f, 36f, 4f)] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, ClipToBounds = true,
                Children = [HighlightRow(t.Title, t.MatchStart, t.MatchLen, 13f, 600, Tok.TextPrimary)] },
        ],
    };

    static string KindLabelOf(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };

    // The accent-tinted highlight pill (Outlook-style, on-brand): a flex row of [before] [pill:match] [after]. Rows are
    // single-line, so a real background plate needs no engine change (TextSpan carries no background). The matched run
    // sits in a rounded AccentSelectedTextBackground box; the flanks stay in the base color and ellipsize.
    static Element HighlightRow(string text, int matchStart, int matchLen, float size, ushort weight, ColorF baseColor)
    {
        if (matchLen <= 0 || matchStart < 0 || matchStart + matchLen > text.Length)
            return new TextEl(text) { Size = size, Weight = weight, Color = baseColor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

        Element Seg(string s, bool grow) => new TextEl(s)
        { Size = size, Weight = weight, Color = baseColor, Grow = grow ? 1f : 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis };

        var kids = new List<Element>(3);
        if (matchStart > 0) kids.Add(Seg(text.Substring(0, matchStart), false));
        kids.Add(new BoxEl
        {
            Shrink = 0f, Corners = CornerRadius4.All(4f), Fill = Tok.AccentSelectedTextBackground,
            Padding = new Edges4(3f, 1f, 3f, 1f),
            Children = [new TextEl(text.Substring(matchStart, matchLen)) { Size = size, Weight = weight, Color = Tok.TextOnAccentSelectedText, MaxLines = 1, Wrap = TextWrap.NoWrap }],
        });
        int after = matchStart + matchLen;
        kids.Add(after < text.Length ? Seg(text.Substring(after), true) : new BoxEl { Grow = 1f });
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, ClipToBounds = true, Children = kids.ToArray() };
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

    Element ArtistColumns(Loadable<Artist> artist, Loadable<DetailModel> albumTracks, Services svc, PlaybackBridge? bridge, bool hasSel, bool railOpen)
    {
        if (!hasSel) return Placeholder(Strings.Library.SelectArtist);
        Element artistPane = Pane with
        {
            // Basis = the user's chosen width, but Shrink so a narrow shell never paints the grid wider than this column
            // (Shrink=0 + fixed Width let the viewport outgrow the flex slot → discography tiles under the tracks pane).
            // When the rail is open the 3-column sum-of-minimums must fit a narrower content region → drop this floor 300→220.
            Basis = _midW.Value, MinWidth = railOpen ? 220f : 300f, MaxWidth = _midW.Value, Shrink = 1f, Grow = 0f,
            Children = [Embed.Comp(() => new LibraryArtistPane(artist, _albumKey, _aSort, _aDesc, _aView, _aSize, _aFilter, onDrill: DrillToTracks))],
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
            Children = [artistPane, Grip(_midW, 300f, 620f, () => _settings?.Set(LibraryStateKeys.MidW(_kind), _midW.Peek())), tracksPane],
        };
    }

    static Element Placeholder(string key) => Pane with
    {
        Key = "lib:empty", Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(Loc.Get(key)) { Size = 14f, Color = Tok.TextTertiary }],
    };

    // The grip's ColumnGrip carries Grow=1 to fill the column HEIGHT — so it must be boxed in a fixed Width=10 / Shrink=0
    // wrapper, else that Grow leaks into the horizontal row and the grip eats half the leftover width (the empty-gap bug).
    static Element Grip(Signal<float> w, float min, float max, Action onCommit) => new BoxEl
    {
        Width = 7f, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Stretch,
        Children = [Embed.Comp(() => new ColumnGrip(w, min, max, onCommit))],
    };
}

// A thin drag-to-resize seam between two library columns. Reuses the engine's eager pointer capture (BoxEl.OnDrag) and
// reconstructs the true window-X each frame (the grip moves as the column resizes). Invisible at rest, a hairline on hover.
sealed class ColumnGrip : Component
{
    readonly Signal<float> _width;
    readonly float _min, _max;
    readonly Action _onCommit;
    NodeHandle _self;
    float _startW, _startPx;
    public ColumnGrip(Signal<float> width, float min, float max, Action onCommit) { _width = width; _min = min; _max = max; _onCommit = onCommit; }

    public override Element Render() => new BoxEl
    {
        // A persistent 1px divider centred in a 7px hit strip (always visible = the column seam; the strip is the drag
        // target). Brightens slightly on hover to cue that it's draggable.
        Grow = 1f, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Cursor = CursorId.SizeWE,
        OnRealized = h => _self = h, OnPointerDown = OnDown, OnDrag = OnMove,
        OnClick = _onCommit,   // for an OnDrag node, OnClick IS the release/commit edge (drag-end) — persist the chosen width
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
            ? ScrollView(CompactEpisodes(m.Episodes ?? Array.Empty<Episode>(), i => { if (uri.Length > 0) _ = _svc.Player.PlayAsync(uri, i); })) with { Grow = 1f }
            : Embed.Comp(() => new TrackList(_trackRoute, _model, _bridge, TrackHandlers(go, lib), showToolbar: false, embedded: true));

        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [Hero(m), Actions(uri, m.Title, Play, Shuffle), body],
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
            int n = DetailQueueActions.AddToEnd(_svc.Player, m.Tracks);
            if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
        }
        void PlayNext()
        {
            var m = Cur(); if (m is null) return;
            int n = DetailQueueActions.PlayNext(_svc.Player, m.Tracks);
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
            PlayNext, AddToQueue, AddToPlaylist,
            // The embedded TrackList has no trailing shelves, so these are never invoked here; route through DetailNav
            // (no preview/morph store) so behaviour stays a plain nav if that ever changes.
            a => DetailNav.OpenAlbum(null, go, a), p => DetailNav.OpenPlaylist(null, go, p));
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

    Element Actions(string uri, string? name, Action play, Action shuffle) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.XL, 0f, WaveeSpace.XL, WaveeSpace.M),
        Children =
        [
            new BoxEl { Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Corners = CornerRadius4.All(20f), Padding = new Edges4(18f, 9f, 18f, 9f),
                Fill = Tok.AccentDefault, HoverScale = 1.04f, PressScale = 0.97f, Shadow = Elevation.Card, OnClick = play,
                Children = [Icon(Icons.Play, 14f, Tok.TextOnAccentPrimary), new TextEl(Loc.Get(Strings.Detail.Play)) { Size = 14f, Weight = 700, Color = Tok.TextOnAccentPrimary }] },
            Fab(Icons.Shuffle, shuffle),
            _show ? Embed.Comp(() => new FollowButton(uri, name)) : Embed.Comp(() => new SaveButton(uri, name: name)),
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

// The compact artist pane (WaveeMusic ArtistsLibraryView column 2): the artist's releases grid only. Picking a release
// sets the host's _albumKey → the 3rd column (tracks).
sealed class LibraryArtistPane : Component
{
    readonly Loadable<Artist> _artist;
    readonly Signal<string> _albumKey;
    readonly Signal<int> _aSort, _aView, _aSize;
    readonly Signal<bool> _aDesc;
    readonly Signal<string> _aFilter;
    readonly Action? _onDrill;                   // collapsed drill-in: notify the host when a release is picked (→ tracks level)
    readonly SelectionModel _discoSel = new();   // discography grid single-selection (drives the 3rd column)
    static readonly string[] NoSuggest = Array.Empty<string>();

    public LibraryArtistPane(Loadable<Artist> artist, Signal<string> albumKey,
        Signal<int> aSort, Signal<bool> aDesc, Signal<int> aView, Signal<int> aSize, Signal<string> aFilter, Action? onDrill = null)
    { _artist = artist; _albumKey = albumKey; _aSort = aSort; _aDesc = aDesc; _aView = aView; _aSize = aSize; _aFilter = aFilter; _onDrill = onDrill; }

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var st = (LoadState)_artist.State.Value;   // subscribe
        var a = _artist.Value.Value;               // subscribe
        var albums = a?.TopAlbums ?? Array.Empty<Album>();
        var shown = FilterSortAlbums(albums, _aFilter.Value, _aSort.Value, _aDesc.Value);   // subscribe (filter/sort/direction)
        // Keep the discography selection synced to the chosen release — UNCONDITIONAL hook, BEFORE any early return (else
        // the effect-slot count changes when the artist flips Pending→Ready → an out-of-range hook crash). Driven off the
        // SHOWN (filtered/sorted) list so the selection index matches the rendered ItemsView.
        UseEffect(() => SyncDisco(shown), _albumKey.Value + "|" + shown.Length + "|" + (shown.Length > 0 ? shown[0].Uri : ""));

        // The toolbar (album sort/view controls + Filter + "Go to artist") renders even while the artist loads; only the
        // body swaps skeleton→grid/list. So the controls never flash in/out and stay put across a selection change.
        Element body = (st != LoadState.Ready || a is null || a.Name.Length == 0) ? Skeleton() : Body(shown);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [Toolbar(a, go), body],
        };
    }

    // The discography toolbar — the SAME controls as the left picker (LibrarySortView pill + Filter box), but over the
    // artist's releases: hasCreator:false (a single artist), hasRelease:true (Release-date sort applies). "Go to artist"
    // stays top-right (shown once the artist is loaded).
    Element Toolbar(Artist? a, Action<string, string?>? go) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.S, Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.S),
        Children =
        [
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S, Children =
            [
                Embed.Comp(() => new LibrarySortView(_aSort, _aDesc, _aView, _aSize, hasCreator: false, hasRelease: true)),
                new BoxEl { Grow = 1f },
                a is not null && go is not null ? GoToArtist(a, go) : new BoxEl(),
            ] },
            AutoSuggestBox.Create(NoSuggest, Loc.Get(Strings.Library.Filter), text: _aFilter, queryIcon: Icons.Search, grow: 1f, maxFillWidth: 9999f, minHeight: 32f, cornerRadius: 8f),
        ],
    };

    // The "Go to artist" pill — extracted verbatim from the former ArtistNav so it can sit inline in the toolbar row.
    static Element GoToArtist(Artist a, Action<string, string?> go) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.XS, AlignItems = FlexAlign.Center,
        Corners = CornerRadius4.All(16f), Padding = new Edges4(WaveeSpace.M, WaveeSpace.XS, WaveeSpace.M, WaveeSpace.XS),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, HoverScale = 1.02f, PressScale = 0.98f,
        OnClick = () => go("artist:" + a.Uri, a.Name),
        Children =
        [
            new TextEl(Loc.Get(Strings.Detail.GoToArtist)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary },
            Icon(Icons.OpenInNewWindow, 14f, Tok.TextSecondary),
        ],
    };

    // Filter (title contains) + sort over the artist's releases. Sort codes mirror the picker: 0 = as returned by the API
    // (≈ release-date desc), 1 = reversed, 2 = Alphabetical, 4 = Release date (by Year). Direction flips the sorted forms.
    static Album[] FilterSortAlbums(IReadOnlyList<Album> albums, string filter, int sort, bool desc)
    {
        string q = filter.Trim();
        var arr = (q.Length == 0 ? albums : albums.Where(al => al.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToArray();
        Comparison<Album>? cmp = sort switch
        {
            2 => (x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase),
            4 => (x, y) => x.Year.CompareTo(y.Year),
            _ => null,
        };
        if (cmp is not null) Array.Sort(arr, cmp);
        else if (sort == 1) Array.Reverse(arr);
        if (desc && cmp is not null) Array.Reverse(arr);
        return arr;
    }

    // Grid (view>=2) vs list, grid-size S/M/L — the SAME view-type semantics as the left picker's ListBody. Keyed by
    // view/size/set so a view change remounts with the right slots; the external _discoSel keeps the selection. Picking a
    // release sets _albumKey → 3rd column. size=1 non-compact grid → 124px min width (matches the previous fixed grid).
    Element Body(IReadOnlyList<Album> albums)
    {
        if (albums.Count == 0)
            return new BoxEl { Padding = new Edges4(WaveeSpace.M, WaveeSpace.XL, WaveeSpace.M, WaveeSpace.XL),
                Children = [new TextEl(_aFilter.Peek().Length > 0 ? Loc.Get(Strings.Library.NoMatch) : "…") { Size = 13f, Color = Tok.TextTertiary }] };

        int view = _aView.Value, size = _aSize.Value;   // subscribe
        bool grid = view >= 2, compact = view == 0 || view == 2;
        string key = "disco:" + view + ":" + size + ":" + albums.Count + ":" + albums[0].Uri;
        void Pick(int i) { if (i < 0 || i >= albums.Count) return; _albumKey.Value = "album:" + albums[i].Uri; _onDrill?.Invoke(); }

        if (grid)
            return new BoxEl
            {
                Key = key, Grow = 1f, Basis = 0f, MinHeight = 0f, Direction = 1, ClipToBounds = true,
                Padding = new Edges4(WaveeSpace.M, 0f, WaveeSpace.M, 0f),
                Children = [ItemsView.Create(albums.Count, i => DiscoCardContent(albums[i], compact),
                    RepeatLayout.GridFit((compact ? 84f : 100f) + size * (compact ? 16f : 24f), 8f),
                    selectionMode: ItemsSelectionMode.Single, selection: _discoSel, selector: SelectorVisual.Border,
                    selectionChanged: () => Pick(_discoSel.FirstSelectedIndex), grow: 1f)],
            };

        return new BoxEl
        {
            Key = key, Grow = 1f, Basis = 0f, MinHeight = 0f, Direction = 1,
            Children = [ItemsView.List(albums.Count, i => DiscoRowContent(albums[i], compact),
                selectionMode: ItemsSelectionMode.Single, selection: _discoSel,
                onSelectionIndexChanged: Pick, itemExtent: compact ? 44f : 60f, grow: 1f)],
        };
    }

    void SyncDisco(IReadOnlyList<Album> albums)
    {
        string ak = _albumKey.Peek();
        int idx = -1;
        if (ak.Length > 0) for (int i = 0; i < albums.Count; i++) if ("album:" + albums[i].Uri == ak) { idx = i; break; }
        if (idx < 0 && albums.Count > 0) _albumKey.Value = "album:" + albums[0].Uri;
        else if (idx < 0) { if (_discoSel.SelectedCount > 0) _discoSel.DeselectAll(); }
        else if (_discoSel.FirstSelectedIndex != idx) _discoSel.Select(idx);
    }

    // Grid card: cover + title, plus a "year · KIND" subtitle in non-compact grids (compact drops it, like the picker).
    static Element DiscoCardContent(Album al, bool compact)
    {
        var children = new List<Element>(3)
        {
            Surfaces.ArtworkFill(al.Cover, 6f),
            new TextEl(al.Name) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (!compact)
            children.Add(new TextEl((al.Year > 0 ? al.Year + " · " : "") + KindLabel(al.Kind)) { Size = 11f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        return new BoxEl { Direction = 1, Gap = WaveeSpace.XS, ClipToBounds = true, Padding = new Edges4(WaveeSpace.XS, WaveeSpace.XS, WaveeSpace.XS, WaveeSpace.XS), Children = children.ToArray() };
    }

    // List row: 40px cover (dropped when compact) + title + "year · KIND" subtitle — mirrors the left picker's NavRowContent.
    static Element DiscoRowContent(Album al, bool compact)
    {
        var children = new List<Element>(2);
        if (!compact)
            children.Add(new BoxEl { Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(5f), ClipToBounds = true,
                Children = [Surfaces.Artwork(al.Cover, al.Uri.GetHashCode() & 0x7fffffff, 40f, 40f, 5f)] });
        children.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
            Children =
            [
                new TextEl(al.Name) { Size = compact ? 13f : 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                compact ? new BoxEl() : new TextEl((al.Year > 0 ? al.Year + " · " : "") + KindLabel(al.Kind)) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ] });
        return new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f), Children = children.ToArray() };
    }

    static string KindLabel(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };

    static Element Skeleton() => new BoxEl
    {
        Direction = 1, Grow = 1f, Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.M), Gap = WaveeSpace.S,
        Children = Enumerable.Range(0, 8).Select(_ => (Element)new BoxEl { Height = 148f, Corners = CornerRadius4.All(6f), Fill = Tok.FillCardDefault }).ToArray(),
    };
}
