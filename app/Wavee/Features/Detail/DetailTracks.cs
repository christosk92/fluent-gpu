using System;
using System.Collections.Generic;
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

// The right area: a FIXED chrome bar (toolbar + column header) above the track table. WIDTH-ADAPTIVE — as the area
// narrows it DROPS columns by breakpoint (Album → ♥ → art-thumb; #, Title and Duration always stay) so the title
// stays readable and the grid never overflows/overlaps. (The engine also shrink-to-fits an over-wide fixed grid —
// FlexLayout.ResolveColumns — as a backstop.) The active "tier" is derived from the measured width; a breakpoint cross
// REMOUNTS the list (keyed by tier) so every tier mounts one clean, consistent column set: within a tier the row arity
// is stable (recycle-safe), and across a tier the fresh mount sidesteps any recycle-shape churn. Selection survives the
// remount (external SelectionModel); the scroll offset resets on the (rare) breakpoint cross. The now-playing recolour
// re-skins the realized window IN PLACE via the epoch (displacementVersion) so a track change keeps the scroll offset.
// Rows are PLAIN/recyclable (no binds, no Components) → a steady scroll is zero-alloc.
sealed class TrackList : Component
{
    // Row geometry + the cell builders themselves now live in the shared TrackRow (Components/TrackRow.cs) so the detail
    // list, the library pane, artist "Popular" and search all render ONE identical cell. These re-export the shared
    // constants by name so the chrome/header code below is unchanged (the header must stay aligned to the row columns).
    const float RowHeight = TrackRow.RowHeight;     // density M
    const float HeaderHeight = TrackRow.HeaderHeight;
    const float ColGap = TrackRow.ColGap;           // shared by header + rows (alignment invariant)
    const float PadX = TrackRow.PadX;               // shared horizontal inset (header chrome padding == row grid padding)
    const float RowInset = TrackRow.RowInset;       // rounded row-highlight inset (rows pad PadX−RowInset so columns stay header-aligned)
    const float ThumbSize = TrackRow.ThumbSize;

    // The detail model is a Loadable: the HEADER bits (HasVideo, columns) read reactively from its current value
    // (preview → full), and the TRACK ROWS stream in via Skel.Region — which derives the shimmer from the REAL Row
    // template (ONE source, no hand-written skeleton). _model / _tracks are refreshed at the top of Render.
    readonly Loadable<DetailModel> _full;
    DetailModel _model = DetailModel.Empty;
    IReadOnlyList<Track> _tracks = Array.Empty<Track>();
    readonly Signal<Route> _route;                             // read reactively → cfg re-derived so ONE list serves successive detail routes
    DetailConfig _cfg = DetailConfig.Album;                     // derived from route kind + loaded ReleaseKind at the top of Render
    readonly PlaybackBridge? _bridge;
    readonly DetailHandlers _h;                                // carries the per-context sort signal + SetSort (set by DetailShell)
    readonly bool _showToolbar;                                // false in the vertical layout (the header owns the toolbar)
    readonly bool _embedded;                                   // true when hosted inside a compact pane (Library master-detail): the
                                                               // SAME virtualized list + cell, but no album trailing (About/Fans/More-by)
                                                               // so the rows ARE the scroller — the tier system still drops columns to fit.
    bool _hasDate;                                             // any track carries an AddedAt → the Date-added column exists
    bool _hasBy;                                               // collaborative (≥2 contributors) → the Added-by column exists
    readonly Signal<int> _tier = new(0);                       // width tier (0 = widest/full), written by OnBoundsChanged
    readonly SelectionModel _selection = new();                // external → survives a tier remount
    readonly Dictionary<int, TrackSize[]> _tracksByTier = new();
    (bool Date, bool By, bool Video) _lastCols = (false, false, false);   // the columns depend on the tracks (Date-added/Added-by/Video); when they
                                                                          // arrive (preview→full) the cached column SIZES must be recomputed or the grid wraps
    (TrackSort Sort, string Query, TrackFilterFlags Flags) _viewKey = (new((SortColumn)(-1), false), "\0", TrackFilterFlags.None);   // invalid sentinel
    int[] _view = Array.Empty<int>();                          // filtered + sorted → original track-index map (rows read via this)
    string? _topTrackId;                                       // album surfaces: the most-played track's id (gets a star); null with no play data
    AsyncCommandSet<string>? _play;                            // per-track-id play command in flight → the row's #-cell buffering spinner
    string? _lastCtxUri;                                       // last loaded context uri → detect a reused-slot album swap (invalidate view/columns/selection)
    LibraryBridge? _lib;                                       // Mutations bridge → per-row heart saved-state + toggle (null when no Mutations source)

    public TrackList(Signal<Route> route, Loadable<DetailModel> full, PlaybackBridge? bridge, DetailHandlers h, bool showToolbar = true, bool embedded = false)
    { _route = route; _full = full; _bridge = bridge; _h = h; _showToolbar = showToolbar; _embedded = embedded; }

    // The placeholder row the engine derives the shimmer from — the REAL Row(...) with an empty track, so the skeleton
    // rows always match the real rows (the single-source-of-truth the skeleton kit is built on).
    static readonly Track EmptyTrack = new("", "", "", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0L, false, null);

    // The visible order (cached, keyed by sort + filter): view[displayPos] = original track index, for the tracks that
    // pass the filter (search query + hide-explicit), in the current sort order. Read live by the frozen row template /
    // keyOf / invoke via .Peek(), so a SORT change re-skins the realized window in place; a FILTER change (which alters
    // the count) instead remounts the list via the keyed wrapper.
    int[] View()
    {
        var s = _h.Sort.Peek();
        string q = _h.Query.Peek().Trim();
        var flags = _h.Flags.Peek();
        var key = (s, q, flags);
        if (!key.Equals(_viewKey))
        {
            bool hideX = (flags & TrackFilterFlags.HideExplicit) != 0;
            bool vidOnly = (flags & TrackFilterFlags.VideosOnly) != 0;
            var tracks = _tracks;
            var list = new List<int>(tracks.Count);
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (hideX && t.IsExplicit) continue;
                if (vidOnly && !t.HasVideo) continue;
                if (q.Length > 0 && !MatchesQuery(t, q)) continue;
                list.Add(i);
            }
            Comparison<int> baseCmp = s.Column switch
            {
                SortColumn.Title => (a, b) => string.Compare(tracks[a].Title, tracks[b].Title, StringComparison.OrdinalIgnoreCase),
                SortColumn.Album => (a, b) => string.Compare(tracks[a].Album.Name, tracks[b].Album.Name, StringComparison.OrdinalIgnoreCase),
                SortColumn.Duration => (a, b) => tracks[a].DurationMs.CompareTo(tracks[b].DurationMs),
                SortColumn.Artist => (a, b) => string.Compare(DetailFormat.ArtistNames(tracks[a].Artists), DetailFormat.ArtistNames(tracks[b].Artists), StringComparison.OrdinalIgnoreCase),
                SortColumn.DateAdded => (a, b) => Nullable.Compare(tracks[a].AddedAt, tracks[b].AddedAt),
                SortColumn.Plays => (a, b) => tracks[a].PlayCount.CompareTo(tracks[b].PlayCount),
                _ => (a, b) => a.CompareTo(b),   // Index = original order
            };
            // Stable: ties break by original index (ascending), so descending only flips the primary key.
            list.Sort((a, b) => { int c = baseCmp(a, b); if (s.Descending) c = -c; return c != 0 ? c : a.CompareTo(b); });
            _view = list.ToArray(); _viewKey = key;
        }
        return _view;
    }

    // A track matches the search query if any of title / artist(s) / album contains it (case-insensitive).
    static bool MatchesQuery(Track t, string q) =>
        t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
        || DetailFormat.ArtistNames(t.Artists).Contains(q, StringComparison.OrdinalIgnoreCase)
        || t.Album.Name.Contains(q, StringComparison.OrdinalIgnoreCase);

    // The most-played track's id (gets the star), or null when there's no play data — so the star stays album-only.
    static string? TopTrack(IReadOnlyList<Track> tracks)
    {
        int best = -1;
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].PlayCount > 0 && (best < 0 || tracks[i].PlayCount > tracks[best].PlayCount)) best = i;
        return best >= 0 ? tracks[best].Id : null;
    }

    // Shown in place of the list when there's nothing to show — an empty playlist, or a filter that matched nothing.
    static Element FilterEmpty(bool noTracks) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XXL, WaveeSpace.L, WaveeSpace.XXL),
        Children = [new TextEl(noTracks ? Loc.Get(Strings.Detail.Empty.NoTracks) : Loc.Get(Strings.Detail.Empty.NoMatch)) { Size = 14f, Color = Tok.TextTertiary }],
    };

    // (ColumnSet — which optional columns are present at a tier — is the shared TrackRow.ColumnSet, so the header here and
    // every shared cell agree on the build order: # · ♥ · (thumb) · Title · Album · AddedBy · DateAdded · Video · Plays · Duration.)
    //
    // Drop order as the area narrows (most expendable first): Added-by (≥1) / Video (≥2) → Album (≥2) → Plays/Date (≥3)
    // → ♥ (≥4) → art-thumb (≥5). Video/Plays exist only on album surfaces (ShowPlays); Album/By/Date only on playlists.
    ColumnSet SetFor(int tier) => new(
        Album: _cfg.ShowAlbumColumn && tier < 2,
        By: _hasBy && tier < 1,
        Date: _hasDate && tier < 3,
        Video: _cfg.ShowPlays && _model.HasVideo && tier < 2,
        Plays: _cfg.ShowPlays && tier < 3,
        Heart: tier < 4,
        Thumb: _cfg.ShowArtThumb && tier < 5);

    // Right-area-width breakpoints (sized off the widest column set), so the Star Title keeps a usable width at each
    // tier's minimum. Fewer-column contexts just cross the same widths with nothing to drop until a present column.
    static int TierFor(float w) =>
        w <= 0f ? 0 : w >= 860f ? 0 : w >= 720f ? 1 : w >= 560f ? 2 : w >= 440f ? 3 : w >= 340f ? 4 : 5;

    // The tier's column tracks (cached): [#, Title*, Album?, AddedBy?, DateAdded?, ♥?, Duration]. Dropped columns are
    // truly removed (a fresh mount per tier makes the varying count safe), so there is no wasted gap.
    TrackSize[] TracksFor(int tier)
    {
        if (_tracksByTier.TryGetValue(tier, out var cached)) return cached;
        var s = SetFor(tier);
        var t = new List<TrackSize>(10) { TrackSize.Px(36f) };
        if (s.Heart) t.Add(TrackSize.Px(40f));         // ♥ moved to the LEFT cluster — between # and the art thumb
        if (s.Thumb) t.Add(TrackSize.Px(ThumbSize));   // dedicated art column: the Title header aligns over the title text, not the art
        t.Add(TrackSize.Star());
        if (s.Album) t.Add(TrackSize.Px(180f));
        if (s.By) t.Add(TrackSize.Px(132f));
        if (s.Date) t.Add(TrackSize.Px(108f));
        if (s.Video) t.Add(TrackSize.Px(28f));
        if (s.Plays) t.Add(TrackSize.Px(84f));
        t.Add(TrackSize.Px(64f));
        var arr = t.ToArray();
        _tracksByTier[tier] = arr;
        return arr;
    }

    public override Element Render()
    {
        _play = UseAsyncCommands<string>();      // keyed by track id; a row's #-cell shows the buffer spinner while its PlayAsync runs (same instance each render)
        _lib = UseContext(LibraryBridge.Slot);   // Mutations bridge for the per-row heart (saved-state + toggle)
        var model = _full.Value.Value;           // subscribe → re-render preview→full (chrome columns + trailing update on load)
        _model = model; _tracks = model.Tracks; _hasDate = model.HasDateAdded; _hasBy = model.HasAddedBy; _topTrackId = TopTrack(model.Tracks);
        if (_h.PlayAllOverride is { Length: > 0 } playAllCell) playAllCell[0] = () => StartVisible(0);   // rail "Play" → visible (sorted) order from the top
        _cfg = DetailPage.ResolveConfig(DetailPage.ParseDetail(_route.Value).Kind, model);   // route kind + loaded ReleaseKind (reused slot re-derives)
        // Embedded in a compact pane (Library): drop the album trailing so the rows are the scroller (the pane owns the
        // hero + actions above). Everything else — the cell, hover transport, now-playing, heart, tier columns — is identical.
        if (_embedded) _cfg = _cfg with { HasTrailing = false };
        // Reused slot: a detail-route swap changes the track set under a stable (sort,query,flags) view key, so the cached
        // view map + per-tier column sets + selection would be STALE (wrong / out-of-range indices). Invalidate them on a
        // context change so the new page recomputes cleanly.
        if (model.ContextUri != _lastCtxUri)
        {
            _lastCtxUri = model.ContextUri;
            _viewKey = (new((SortColumn)(-1), false), "\0", TrackFilterFlags.None);
            _tracksByTier.Clear();
            _selection.ClearSelection();
        }

        // The present columns depend on the tracks (Date-added/Added-by/Video appear once they load). When that set
        // changes, drop the cached per-tier column SIZES so the header + rows rebuild with a matching track count
        // (otherwise the grid has more cells than columns and wraps). The list key (below) folds it in to remount cleanly.
        var cols = (_hasDate, _hasBy, model.HasVideo);
        if (!cols.Equals(_lastCols)) { _tracksByTier.Clear(); _lastCols = cols; }

        int tier = _tier.Value;                  // subscribe → re-render (new header + remount list) on a breakpoint cross
        var set = SetFor(tier);
        var tracks = TracksFor(tier);
        var sort = _h.Sort.Value;                // subscribe → re-render (header carets) on sort change
        int density = _h.Density.Value;          // subscribe → remount with the new row height on density change
        string query = _h.Query.Value;           // subscribe → remount with the filtered set on query change
        var flags = _h.Flags.Value;              // subscribe → remount on a quick-filter toggle
        float rowH = TrackRow.RowHeightFor(density);

        // Now-playing / sort / column re-skin is per-row now: each bound row subscribes to the bridge + _h.Sort inside
        // its own binds (BoundRowContent / BoundTitle), so a track change recolours and a sort change reorders the
        // realized rows IN PLACE — no whole-list epoch, no list re-render. Tier/column changes alter the slot SET and
        // remount via the keyed wrapper below.
        bool labeled = tier <= 3;   // wide tiers (≥ 440px) show command-bar labels; narrow lists collapse to icon-only
        Element chrome = new BoxEl
        {
            Key = "chrome", Direction = 1, Padding = new Edges4(PadX, WaveeSpace.S, PadX, 0f),
            // The command bar (filter/sort/row-size) sits above the column header; the rail owns the context actions.
            Children = _showToolbar ? [Toolbar(labeled), Header(set, tracks, sort)] : [Header(set, tracks, sort)],
        };

        Element RealList() => View().Length == 0
            ? FilterEmpty(_tracks.Count == 0)     // empty playlist, or a filter that matched nothing
            // Bound rows (signals-first): each slot mounts ONCE and recycles by an index-signal write. Selection flips a
            // bound pill opacity — no list re-render, no remount, no Enter replay (the flash fix); now-playing/sort
            // re-skin each row's content in place via its own subscriptions. The row maps its display position → track
            // through View() inside its binds (sort-subscribed), so a sort change reorders in place; filter/density/tier
            // change the slot SET and remount via the keyed wrapper below.
            : ItemsView.CreateBound(
                View().Length,
                scope => BoundRowSkin(scope, BoundRow(scope, set, tracks, rowH), rowH),
                RepeatLayout.Stack(rowH),
                selectionMode: _cfg.Selection,
                selection: _selection,                // external → selection survives the tier remount
                isItemInvokedEnabled: true,
                itemInvoked: i => PlayRow(i),   // DoubleTap / Enter → same as a row click (visible-order play + now-playing toggle)
                grow: _cfg.HasTrailing ? 0f : 1f,
                // Alpha-mask edge fade: the page floats over a gradient wash (no opaque plate), so the surface-colour
                // EdgeCues fade self-skips — this feathers the rows' own alpha at the overflowing top/bottom instead.
                // Only when the ItemsView is itself the scroller (playlist/liked); the album path fades its outer scroll.
                autoEdgeFade: !_cfg.HasTrailing,
                // Cold-mount stagger: the track list is the heaviest part of a detail-page mount (the nav cold-mount
                // spike). Realize its initial window a few rows/frame so no single frame mounts the whole window — the
                // skeleton/reveal masks the brief fill-in, and the scroll extent stays correct (driven by the full count).
                staggerColdRealize: true,
                // Scroll-position restoration keyed by the detail content (route): navigate away from a 10k-track
                // playlist and back and the viewport seeds the saved row BEFORE its first realize — no scroll-to-top
                // flash, no jump (the engine scopes this per tab via the KeepAlive slot). A different album starts at top.
                scrollKey: _route.Value.Name);

        // The tracks stream in via the engine's skeleton boundary: while the model is Pending it shows shimmer rows the
        // engine DERIVES from the real Row(EmptyTrack) template (ONE definition — no hand-written shimmer, no drift); on
        // Ready it reveals the real virtualized list with a staggered row reveal.
        // SkelReveal.None: the ItemsView owns its entrance (per-row ItemCollectionTransition adds); the engine lingers
        // the shimmer across that entrance so the gray rows cross-dissolve INTO the real rows at the same positions —
        // no shimmer→empty→rows gap, and no exit-timing to hand-tune here.
        Element list = Skel.Region(_full, () => RowsShimmer(set, tracks, rowH), _ => RealList(), reveal: SkelReveal.None, smoothResize: false);

        // Key the list by tier + density + filter → any of those REMOUNTS it (a clean, shape-stable slot template with
        // the right column set / row height / filtered window). Sort is NOT in the key — each bound row re-skins itself
        // to the new order via its sort-subscribed binds (scroll preserved). Tier IS in the key now: the cell arity
        // (column set) is shape per slot, so a breakpoint cross rebuilds the slots (the rare-resize cost; the in-place
        // flash fix is the priority).
        float listGrow = _cfg.HasTrailing ? 0f : 1f;
        Element listKeyed = new BoxEl { Key = "list:t" + tier + ":d" + density + ":q" + query + ":f" + (int)flags, Grow = listGrow, Direction = 1, Children = [list] };

        Element rightBody = _cfg.HasTrailing ? TrailingBody(listKeyed) : listKeyed;

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f,
            // Measure the right-area width → the active tier. Value-gated, so a re-render happens only on a breakpoint
            // cross (not every resize frame); the new tier itself never changes this box's width → no feedback loop.
            OnBoundsChanged = r => { if (r.W > 0f) { int t = TierFor(r.W); if (t != _tier.Peek()) _tier.Value = t; } },
            Children = [chrome, rightBody],
        };
        // Float a multi-select command bar over the list (bottom-centre, above the player). The overlay layer is
        // hit-test transparent so it never steals events from the list — only the bar itself is interactive.
        Element overlay = new BoxEl
        {
            // Passthrough (not HitTestVisible=false, which would kill the bar too): clicks in the empty area fall
            // through to the list; only the bar itself is interactive.
            Direction = 1, Grow = 1f, HitTestPassThrough = true,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Padding = new Edges4(WaveeSpace.L, 0f, WaveeSpace.L, WaveeSpace.XL),
            Children = [Embed.Comp(() => new SelectionBar(_selection, _h, DisplayTrack))],
        };
        return ZStack(column, overlay) with { Grow = 1f };
    }

    // Resolve a display row index (what the SelectionModel stores) → the track, through the current filtered+sorted view.
    Track? DisplayTrack(int displayIndex)
    {
        var v = View();
        return displayIndex >= 0 && displayIndex < v.Length ? _tracks[v[displayIndex]] : null;
    }

    // album / single: an outer scroller carries the eager rows + the trailing sections, under the fixed chrome. The
    // trailing component owns one aggregate route-keyed async resource so the below-list content fades in as one block.
    Element TrailingBody(Element listKeyed)
    {
        Element trailing = Embed.Comp(() => new AlbumTrailing(_full, _route, _h));
        return ScrollView(new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            AlignSelf = FlexAlign.Stretch,
            Children = [listKeyed, trailing],
        }) with { Grow = 1f, AutoEdgeFade = true };
    }

    // ── chrome (fixed) ───────────────────────────────────────────────────────────────────────────────────
    // The track-list command bar: ONE left-aligned WinUI CommandBar above the column header, grouped by a vertical
    // separator — [Play · Shuffle] | [Sort · Row size] — with a "Find" search field docked alone on the RIGHT. The
    // search field carries the filter FUNNEL as its trailing affix (advanced filter folded into search — hide explicit /
    // videos only, a light checkable menu), so there's no separate Filter button. Every command shows icon + label at
    // wide tiers and collapses to icon-only at a narrow list (tier ≥ 4, < 440px) — the DefaultLabelPosition=Right +
    // dynamic-overflow behavior, in Wavee's own 32px pill styling. The rail owns Add-to-queue / Copy-to-playlist, so the
    // bar doesn't carry them. Keyed by the labeled state so a tier cross rebuilds cleanly. (Composed from ToolFx, not the
    // CommandBar control, which only does the classic labels-on-open mode.)
    Element Toolbar(bool labeled)
    {
        // Play / Shuffle: labeled (icon + text) at wide tiers, icon-only when the list narrows — the same collapse as the
        // view controls. Plain action buttons (no flyout) so they take the no-op NoAnchor.
        Element Cmd(string glyph, string label, Action onClick) => labeled
            ? ToolFx.LabeledButton(glyph, label, false, onClick, NoAnchor)
            : ToolFx.Button(glyph, false, onClick, NoAnchor);

        var left = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 2f,
            Children =
            [
                Cmd(Icons.Play, Loc.Get(Strings.Detail.Play), _h.PlayAll),
                Cmd(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), _h.Shuffle),
                ToolFx.Separator(),
                // The Sort button opens the "sort by" flyout — the only way to sort by Artist (no column of its own).
                Embed.Comp(() => new SortMenuButton(_h.Sort, _h.SetSort, _cfg.ShowAlbumColumn, _hasDate, labeled)),
                Embed.Comp(() => new ListButton(_h.Density, _h.SetDensity, labeled)),
            ],
        };
        // The "Find" box, docked alone on the right — a plain search field (no suggestions flyout), two-way bound to the
        // live filter query (View() matches case-insensitively on title / artist / album). Its trailing affix is the
        // filter funnel, so the "advanced filter" (hide explicit / videos only) folds into the search box itself.
        Element search = Embed.Comp(() => new EditableText
        {
            Placeholder = Loc.Get(Strings.Detail.Filter.SearchThisList),
            Width = labeled ? 220f : 150f, Height = 32f,
            Text = _h.Query,
            RightAffix = Embed.Comp(() => new FilterButton(_h.Flags, _h.SetFlags, _model.HasVideo)),
        });
        return new BoxEl
        {
            Key = labeled ? "cmdbar:lbl" : "cmdbar:icon",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.XS,
            Margin = new Edges4(0f, 0f, 0f, WaveeSpace.XS),
            Children = [left, new BoxEl { Grow = 1f }, search],
        };
    }

    // A no-op OnRealized for the plain action buttons (Play / Shuffle) — they open no flyout, so they need no anchor.
    static readonly Action<NodeHandle> NoAnchor = static _ => { };

    static Element ToolBtn(string glyph) => new BoxEl
    {
        Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(WaveeRadius.Control),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => { /* TODO: search-in-list / sort / view (visual stubs in v1) */ },
        Children = [Icon(glyph, 14f, Tok.TextSecondary)],
    };

    Element Header(ColumnSet set, TrackSize[] tracks, TrackSort sort)
    {
        var cells = new List<Element>(tracks.Length)
        {
            new BoxEl(),   // # column: no header label (the row numbers below need none) — declutters the left header slot
        };
        if (set.Heart) cells.Add(new BoxEl());   // ♥ header (blank, not sortable) — left cluster, between # and art
        if (set.Thumb) cells.Add(new BoxEl());   // art column header (blank) — keeps the Title header aligned over the title text
        // The Title header owns a Title→Artist cycle; SortLabel reads "Artist" while artist-sorting and cross-fades the word on the flip.
        cells.Add(SortCell(Embed.Comp(() => new SortLabel(_h.Sort)), SortColumn.Title, sort, FlexJustify.Start));
        if (set.Album) cells.Add(SortCell(HLabel(Loc.Get(Strings.Detail.Column.Album), SortColumn.Album, sort), SortColumn.Album, sort, FlexJustify.Start));
        if (set.By) cells.Add(PlainHeader(Loc.Get(Strings.Detail.Column.AddedBy)));   // not sortable (matches the reference)
        if (set.Date) cells.Add(SortCell(HLabel(Loc.Get(Strings.Detail.Column.DateAdded), SortColumn.DateAdded, sort), SortColumn.DateAdded, sort, FlexJustify.Start));
        if (set.Video) cells.Add(new BoxEl());   // video header (blank — the per-row indicator stands alone)
        if (set.Plays) cells.Add(SortCell(HLabel(Loc.Get(Strings.Detail.Column.Plays), SortColumn.Plays, sort), SortColumn.Plays, sort, FlexJustify.End));
        cells.Add(SortCell(Icon(Icons.Clock, 14f, sort.Column == SortColumn.Duration ? Tok.TextSecondary : Tok.TextTertiary),
                           SortColumn.Duration, sort, FlexJustify.End));

        var grid = new GridEl
        {
            Columns = tracks, ColGap = ColGap, RowHeight = HeaderHeight,
            Children = cells.ToArray(),
        };
        return new BoxEl
        {
            Direction = 1, ClipToBounds = true,   // safety net: clip any residual cell overflow (engine also shrink-fits)
            Children = [grid, new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }],
        };
    }

    // The sort-direction caret (Segoe Fluent CaretSolid — chosen over the chevrons).
    internal static readonly string CaretGlyph = Mdl.CaretSolidUp;   // track-list sort-direction caret (SortCaret rotates it 180° for descending)

    // Does this header own the active sort? The Title header also owns Artist (the title subline has no column of its
    // own), so it stays lit — and reads "Artist" — while the list is sorted by artist.
    static bool HeaderActive(SortColumn header, SortColumn active) =>
        header == active || (header == SortColumn.Title && active == SortColumn.Artist);

    // The owning header brightens — EXCEPT Index (#), the default "original order", which carries no indicator.
    static TextEl HLabel(string s, SortColumn col, TrackSort sort) =>
        new(s) { Size = 12f, Weight = 600, Color = HeaderActive(col, sort.Column) && col != SortColumn.Index ? Tok.TextSecondary : Tok.TextTertiary };

    // A non-sortable column header (Added by) — a static, left-aligned label with no click/caret.
    static Element PlainHeader(string label) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
        Children = [new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.TextTertiary }],
    };

    // A clickable column header: click to sort by this column (toggles asc/desc on repeat), with a caret on the active
    // column (before the content for the right-aligned duration, after it otherwise). The default Index/# column shows
    // NO caret and resets to the original order on click.
    Element SortCell(Element content, SortColumn col, TrackSort sort, FlexJustify justify)
    {
        bool showCaret = HeaderActive(col, sort.Column) && col != SortColumn.Index;
        // The caret is a self-animating component: it pops in when its column becomes the sort and springs its rotation
        // 0°↔180° on every direction flip (so the Title cell's ↑→↓→↑→↓ run reads as one continuous spin).
        Element Caret() => Embed.Comp(() => new SortCaret(_h.Sort));
        Element[] kids = !showCaret ? [content]
            : justify == FlexJustify.End ? [Caret(), content]
            : [content, Caret()];
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, Gap = WaveeSpace.XS,
            Corners = CornerRadius4.All(WaveeRadius.Control), HoverFill = Tok.FillSubtleSecondary,
            OnClick = () => _h.SetSort(NextSort(sort, col)),
            Children = kids,
        };
    }

    // The header-click cycle: each column steps ascending → descending → (back to the default original order), so a
    // run of clicks always lands back on the default. The Title header carries TWO fields — Title then Artist — so it
    // cycles Title↑ → Title↓ → Artist↑ → Artist↓ → default (the only header route to an artist sort). The # / Index
    // header always resets to the default.
    static TrackSort NextSort(TrackSort cur, SortColumn clicked)
    {
        if (clicked == SortColumn.Index) return TrackSort.Default;
        if (clicked == SortColumn.Title)
        {
            if (cur.Column == SortColumn.Title) return cur.Descending ? new TrackSort(SortColumn.Artist, false) : new TrackSort(SortColumn.Title, true);
            if (cur.Column == SortColumn.Artist) return cur.Descending ? TrackSort.Default : new TrackSort(SortColumn.Artist, true);
            return new TrackSort(SortColumn.Title, false);
        }
        if (cur.Column == clicked) return cur.Descending ? TrackSort.Default : new TrackSort(clicked, true);
        return new TrackSort(clicked, false);
    }

    // The shimmer source for the track list: N copies of the REAL Row built with an empty track. The engine derives the
    // grey shimmer bars from this (one source of truth — the row shape can never drift from the real rows).
    Element RowsShimmer(ColumnSet set, TrackSize[] tracks, float rowH)
    {
        var rows = new Element[12];
        // Static title (no bound slot index here) — the skeleton deriver only needs the row SHAPE. Plain TextEl (matches
        // the non-now-playing real rows now), so the skeleton mount carries no per-row marquee cost either.
        for (int i = 0; i < rows.Length; i++)
            rows[i] = RowGrid(EmptyTrack, i, isNow: false, isPlaying: false, isBuffering: false, isTop: false,
                              new TextEl(EmptyTrack.Title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                              set, tracks, rowH);
        return new BoxEl { Direction = 1, Children = rows };
    }

    // ── bound row ────────────────────────────────────────────────────────────────────────────────────────
    // A bound row: ONE self-subscribing content component (re-renders on recycle/sort/now-playing, patching cells in
    // place — never a remount, so no flash) wrapped in the shape-stable bound selection skin.
    Element BoundRow(RowScope scope, ColumnSet set, TrackSize[] tracks, float rowH)
        => Embed.Comp(() => new BoundRowContent(this, scope, set, tracks, rowH));

    // The track shown at a display position, through the current (sort-subscribed) view. Reading _h.Sort.Value subscribes
    // the calling bind/render so a SORT change re-skins in place; the caller also reads the slot index signal so a
    // RECYCLE rebinds. Out-of-range (overscan past the view) → EmptyTrack.
    internal Track BoundTrackAt(int displayPos)
    {
        _ = _h.Sort.Value;
        var v = View();
        return (uint)displayPos < (uint)v.Length ? _tracks[v[displayPos]] : EmptyTrack;
    }

    // The title element: a Marquee whose text + now-playing colour are bound to the slot's INDEX signal, so it re-skins
    // on recycle / now-playing WITHOUT remounting. The marquee is a reused autonomous child of BoundRowContent — frozen
    // constructor args would stick on the first track, so its inputs MUST read the signal, not a captured value.
    Element BoundTitle(IReadSignal<int> index) => Marquee.Of(
        Prop.Of(() => BoundTrackAt(index.Value).Title),
        new Marquee.Style
        {
            FontSize = 14f, Weight = 600,
            Foreground = Prop.Of(() =>
                _bridge is not null && _bridge.CurrentTrack.Value?.Id == BoundTrackAt(index.Value).Id
                    ? Tok.AccentTextPrimary : Tok.TextPrimary),
        });

    // PERF: the marquee is 2 nested components + a measure→re-render cycle + a perpetual TranslateX track PER ROW — on a
    // 12-row cold mount that was ~24 of ~60 components and the dominant slice of the flush spike (and every one re-rendered
    // by RethemeAll on a theme flip). A non-now-playing title never needs to scroll (Spotify only scrolls the now-playing
    // row), so render it as a plain, bound, ellipsis TextEl — ONE node, no extra component, no measure cycle, no animation.
    // Recycle-safe: a recycled row is non-playing → stays plain (no type swap); only the single now-playing row uses the
    // marquee, and BoundRowContent re-renders (swapping plain↔marquee for just that row) when now-playing changes.
    Element BoundTitlePlain(IReadSignal<int> index) => new TextEl(Prop.Of(() => BoundTrackAt(index.Value).Title))
    {
        Size = 14f, Weight = 600,
        Color = Prop.Of(() =>
            _bridge is not null && _bridge.CurrentTrack.Value?.Id == BoundTrackAt(index.Value).Id
                ? Tok.AccentTextPrimary : Tok.TextPrimary),
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    // The live content of a bound row: re-renders on its OWN subscriptions (recycle index, sort, now-playing) and
    // patches the GRID in place via diff — no remount, no flash. Child COMPONENTS (the title marquee) are reused across
    // these re-renders, so the title is built with index-signal binds (BoundTitle) to update despite frozen args.
    sealed class BoundRowContent : Component
    {
        readonly TrackList _o;
        readonly RowScope _scope;
        readonly ColumnSet _set;
        readonly TrackSize[] _tracks;
        readonly float _rowH;
        public BoundRowContent(TrackList o, RowScope scope, ColumnSet set, TrackSize[] tracks, float rowH)
        { _o = o; _scope = scope; _set = set; _tracks = tracks; _rowH = rowH; }

        public override Element Render()
        {
            int i = _scope.Index.Value;                                  // recycle → re-render
            var t = _o.BoundTrackAt(i);                                  // resolves the track; subscribes sort
            var br = _o._bridge;
            bool isNow = br?.CurrentTrack.Value?.Id == t.Id;             // now-playing → re-render
            bool isPlaying = isNow && (br?.IsPlaying.Value ?? false);
            // Buffering = this track's PlayAsync command is in flight (the Task-driven start spinner), OR the now-playing
            // track is mid-playback re-buffering (the bridge signal). Reading _play.IsRunning subscribes this row so the
            // spinner appears/clears as the command starts/finishes.
            bool isBuffering = (_o._play?.IsRunning(t.Id) ?? false) || (isNow && br is not null && br.IsBuffering.Value);
            bool isTop = _o._cfg.ShowPlays && _o._topTrackId is not null && t.Id == _o._topTrackId;
            bool saved = t.Uri.Length > 0 && (_o._lib?.IsSaved(t.Uri) ?? false);   // subscribe → heart re-skins on a like toggle
            // Marquee only for the now-playing row; every other row is a cheap plain ellipsis title (see BoundTitlePlain).
            Element title = isNow ? _o.BoundTitle(_scope.Index) : _o.BoundTitlePlain(_scope.Index);
            return _o.RowGrid(t, i, isNow, isPlaying, isBuffering, isTop, title, _set, _tracks, _rowH,
                              onPlay: () => _o.PlayRow(i),
                              saved: saved, onLike: t.Uri.Length > 0 ? (Action)(() => _o._lib?.ToggleSaved(t.Uri)) : null);
        }
    }

    // The # cell's play affordance: single-click PLAYS this track, or PAUSES/RESUMES it when it is already the
    // now-playing one (the PlayerBar.PrimaryClick toggle: optimistic IsPlaying write, then the player command). Called
    // from a click handler (not a reactive context), so the .Peek() reads here never subscribe.
    void PlayRow(int displayPos)
    {
        var v = View();
        if ((uint)displayPos >= (uint)v.Length) return;
        int orig = v[displayPos];
        var track = _tracks[orig];
        // Already the now-playing track → pause/resume toggle (a distinct action, optimistic write then the command).
        if (_bridge is not null && _bridge.CurrentTrack.Peek()?.Id == track.Id)
        {
            bool playing = _bridge.IsPlaying.Peek();
            _bridge.IsPlaying.Value = !playing;
            if (playing) _ = _bridge.Player.PauseAsync(); else _ = _bridge.Player.ResumeAsync();
            return;
        }
        StartVisible(displayPos);   // a DIFFERENT track → start the context at this row, in the visible (sorted/filtered) order
    }

    // Start THIS context at the given display row, sending the VISIBLE (sorted/filtered) order so a remote player mirrors
    // the screen (PlayOrderedAsync). Collections stay URI-only (sort rides on context.url, server-side). The keyed async
    // command drives the row's #-cell buffer spinner. Empty view (still loading / filtered out) → play the context top.
    void StartVisible(int displayPos)
    {
        var v = View();
        if (v.Length == 0) { _h.Play(0); return; }
        if ((uint)displayPos >= (uint)v.Length) displayPos = 0;
        int orig = v[displayPos];
        var track = _tracks[orig];
        if (_bridge is not null && _play is not null && _model.ContextUri is { } uri)
        {
            if (!Wavee.Backend.ContextResolve.IsCollection(uri))
            {
                var ordered = VisibleOrder(v);
                _play.Run(track.Id, ct => _bridge.Player.PlayOrderedAsync(uri, ordered, displayPos, ct));
            }
            else
                _play.Run(track.Id, ct => _bridge.Player.PlayAsync(uri, orig, ct));
        }
        else
            _h.Play(orig);
    }

    // The visible order as (uri, contextUid) pairs — the embedded page the outbound play command carries. ContextUid is
    // the per-row playlist-membership uid (skip_to-by-uid); "" outside a user playlist.
    PlaybackContextTrack[] VisibleOrder(int[] v)
    {
        var ordered = new PlaybackContextTrack[v.Length];
        for (int k = 0; k < v.Length; k++) { var t = _tracks[v[k]]; ordered[k] = new PlaybackContextTrack(t.Uri, t.ContextUid ?? string.Empty); }
        return ordered;
    }

    // ── row grid ─────────────────────────────────────────────────────────────────────────────────────────
    // The row cell is the shared TrackRow.Grid (Components/TrackRow.cs) — ONE definition rendered identically by the
    // detail list, the library pane, artist "Popular" and search. This threads the detail list's per-row state + the
    // column set + the navigation handler through; the bound title element (plain vs marquee) is decided by the caller
    // (BoundRowContent), and the skeleton passes a static title. Plain/diffable → a BoundRowContent re-render patches in place.
    Element RowGrid(Track t, int displayIndex, bool isNow, bool isPlaying, bool isBuffering, bool isTop, Element title,
                    ColumnSet set, TrackSize[] tracks, float rowH, Action? onPlay = null, bool saved = false, Action? onLike = null)
        => TrackRow.Grid(t, displayIndex, new TrackRow.State(isNow, isPlaying, isBuffering, isTop, saved),
                         set, tracks, rowH, title, _cfg.ShowTrackArtist, _h.Go, onPlay, onLike);

    // The custom row container, BOUND + shape-stable so it recycles without remounting: the zebra REST fill is bound to
    // the slot index (recycle-correct + theme-reactive via RethemeAll re-firing binds), and the left accent pill is an
    // ALWAYS-PRESENT child revealed by a BOUND opacity on scope.IsSelected — so a selection change is a compositor-only
    // re-skin (no list re-render, no remount, no Enter replay → no flash). Hover/press/border are uniform (HoverFill etc.
    // are not bindable), which keeps them recycle-correct; this drops only the light-mode zebra-vs-flush hover-depth
    // nuance + the at-rest zebra card stroke (the pill + bound fill are the cues).
    BoxEl BoundRowSkin(RowScope scope, Element content, float rowH)
    {
        var index = scope.Index;
        var isSel = scope.IsSelected;
        var onInteraction = scope.OnInteraction;
        var onFocusChanged = scope.OnFocusChanged;
        bool light = Tok.Theme == ThemeKind.Light;
        ColorF hoverFill = light ? ColorF.FromRgba(0xEC, 0xE9, 0xE2) : Tok.FillSubtleSecondary;
        ColorF pressedFill = light ? ColorF.FromRgba(0xE5, 0xE2, 0xDA) : Tok.FillSubtleTertiary;
        return new()
        {
            ZStack = true, MinHeight = rowH, ClipToBounds = true,    // ZStack → the left accent bar overlays the content
            Margin = new Edges4(RowInset, 0f, RowInset, 0f),         // inset → the highlight reads as a rounded pill (#32)
            Corners = CornerRadius4.All(6f),
            // Reveal on slot MOUNT only (first load + a tier/density/filter remount). Recycling reuses the slot (no
            // mount), so this never replays on scroll or selection — the flash fix — while keeping the entrance fade.
            Animate = new LayoutTransition(TransitionChannels.Opacity,
                TransitionDynamics.Tween(280f, Easing.FluentDecelerate),
                Enter: new EnterExit(Opacity: 0f, Active: true)),
            // Zebra REST fill bound to the slot index (recycle-correct), reading Tok so RethemeAll recolours it on a
            // theme switch. Selection does NOT change the fill — the left accent bar (below) is the ONLY selection cue.
            Fill = Prop.Of(() => index.Value % 2 != 0
                ? (Tok.Theme == ThemeKind.Light ? ColorF.FromRgba(0xF7, 0xF6, 0xF3) : Tok.FillSubtleTertiary)
                : ColorF.Transparent),
            HoverFill = hoverFill,
            PressedFill = pressedFill,
            PressScale = 0.985f,   // subtle push-down on press (a depth cue so the row isn't flat)

            BorderWidth = 1f,
            BorderColor = ColorF.Transparent,        // uniform (BorderColor is not bindable) → recycle-correct
            HoverBorderColor = Tok.StrokeCardDefault,
            FocusVisualMargin = Edges4.All(1f),
            Focusable = false,                       // the ItemsView roving effect owns the single tab stop
            Role = AutomationRole.Button,
            // Double-click invokes (plays), single click selects. DoubleTap is a POINTER trigger, so the ItemsView lands
            // focus + scrolls the row into view on it (EnterKey would skip that).
            OnPointerPressed = args => onInteraction(
                args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { onInteraction(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onInteraction(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = onFocusChanged,
            // A no-op pointer-exit registers PointerBit so the row counts as the "interactive ancestor" whose hover
            // progress its NON-interactive descendants inherit (SceneRecorder.TryResolveInteractionProgress) — that is what
            // lets the # cell reveal its play/pause glyph on hover ANYWHERE in the row. Cached static lambda → no per-row alloc.
            OnPointerExit = static () => { },
            // Content lane (Grow fills the ZStack) + the WinUI ListView-style left accent selection bar. The pill is
            // ALWAYS present (shape-stable) and revealed by a BOUND opacity (no mount-Enter spring — the slot never
            // remounts, so selection is a compositor-only re-skin); press still shrinks it (10/16).
            Children =
            [
                new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Children = [content] },
                new BoxEl
                {
                    Key = "row-pill", Width = 3f, Height = 16f, Margin = new Edges4(2f, 0f, 0f, 0f),
                    Corners = CornerRadius4.All(1.5f), Fill = _h.Accent, AlignSelf = FlexAlign.Center,
                    HitTestVisible = false, PressScale = 10f / 16f,
                    Opacity = Prop.Of(() => isSel() ? 1f : 0f),
                },
            ],
        };
    }

    // (The # cell's number↔play/pause transport, the now-playing equalizer, the buffer spinner, the per-row heart, the
    // artist/album links and the cell wrappers all live in the shared TrackRow now — see Components/TrackRow.cs.)
}


// The floating multi-select command bar: appears over the track list when ≥1 row is selected — overlapping "stacked"
// album thumbnails + "N selected" + quick actions (Add to playlist / Queue) + Clear. Self-subscribes to the
// SelectionModel (re-renders on every select/deselect) and slides in/out.
sealed class SelectionBar : Component
{
    readonly SelectionModel _sel;
    readonly DetailHandlers _h;
    readonly Func<int, Track?> _trackAt;
    public SelectionBar(SelectionModel sel, DetailHandlers h, Func<int, Track?> trackAt) { _sel = sel; _h = h; _trackAt = trackAt; }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        _ = _sel.Version.Value;          // subscribe → re-render on every selection change
        int count = _sel.SelectedCount;
        this.UseSoftReveal(key: count >= 2, dy: 14f, blur: 2f);   // slide + fade when the bar appears/dismisses
        // Only for a MULTI-selection (2+) — a single selected row needs no batch bar (matches WaveeMusic).
        if (count < 2) return new BoxEl();

        // First few selected tracks (by display order) → overlapping thumbnails. Cheap: stops after 4 (no full materialize).
        var thumbs = new List<Element>(4);
        for (int i = 0; i < _sel.ItemCount && thumbs.Count < 4; i++)
            if (_sel.IsSelected(i) && _trackAt(i) is { } t) thumbs.Add(Thumb(t, thumbs.Count));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Padding = new Edges4(WaveeSpace.M, WaveeSpace.S, WaveeSpace.S, WaveeSpace.S),
            Corners = CornerRadius4.All(WaveeRadius.Card), Shadow = Elevation.Flyout, ClipToBounds = true,
            // Frosted in-app acrylic backdrop (WaveeMusic's AcrylicInAppFill) — opaque enough that the rows don't bleed through.
            Acrylic = Tok.AcrylicFlyout, BorderWidth = 1f, BorderColor = Tok.StrokeFlyoutDefault,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = thumbs.ToArray() },
                new TextEl(Strings.Detail.SelectedCount(count)) { Size = 13f, Weight = 600, Color = Tok.TextPrimary },
                Divider(),
                ActionBtn(Icons.Play, Loc.Get(Strings.Detail.Play), () => { var s = Sel(); if (svc is not null && s.Count > 0) { _ = svc.Player.PlayTrackAsync(s[0].Uri); for (int i = 1; i < s.Count; i++) _ = svc.Player.EnqueueAsync(s[i].Uri); _sel.DeselectAll(); } }),
                ActionBtn(Icons.Next, Loc.Get(Strings.Detail.PlayNext), () =>
                {
                    var s = Sel();
                    if (svc is not null && s.Count > 0)
                    {
                        int n = DetailQueueActions.PlayNext(svc.Player, s, s.Count);
                        if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success);
                        _sel.DeselectAll();
                    }
                }),
                ActionBtn(Icons.Queue, Loc.Get(Strings.Detail.AddToEndOfQueue), () => { var s = Sel(); if (svc is not null && s.Count > 0) { int n = DetailQueueActions.AddToEnd(svc.Player, s, s.Count); if (n > 0) Toasts.Show(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), ToastSeverity.Success); _sel.DeselectAll(); } }),
                ActionBtn(Icons.Add, Loc.Get(Strings.Detail.AddToPlaylist), () => { var s = Sel(); if (lib is not null && s.Count > 0) { var (uri, name) = lib.AddToDefaultPlaylist(s); Toasts.Show(Strings.Detail.AddedToPlaylist(name), ToastSeverity.Success); _sel.DeselectAll(); } }),
                Divider(),
                ActionBtn(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), () => _sel.SelectAll()),
                RoundBtn(Icons.Cancel, () => _sel.DeselectAll()),   // Clear selection
            ],
        };
    }

    // The currently-selected tracks (display order), resolved through the row→track map — for the batch actions.
    List<Track> Sel()
    {
        var list = new List<Track>();
        for (int i = 0; i < _sel.ItemCount; i++) if (_sel.IsSelected(i) && _trackAt(i) is { } t) list.Add(t);
        return list;
    }

    static Element Thumb(Track t, int i) => new BoxEl
    {
        Width = 32f, Height = 32f, Shrink = 0f, Corners = CornerRadius4.All(6f), ClipToBounds = true,
        Margin = new Edges4(i == 0 ? 0f : -14f, 0f, 0f, 0f),       // overlap → stacked
        BorderWidth = 2f, BorderColor = Tok.FillCardSecondary,     // a ring in the bar's own colour separates the stack
        Children = [Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, 32f, 32f, 6f)],
    };

    static Element Divider() => new BoxEl { Width = 1f, Height = 22f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(WaveeSpace.XS, 0f, WaveeSpace.XS, 0f) };

    static Element ActionBtn(string glyph, string label, Action onClick) => new BoxEl
    {
        Direction = 0, Height = 36f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
        Padding = new Edges4(WaveeSpace.M, 0f, WaveeSpace.M, 0f), Corners = CornerRadius4.All(18f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onClick,
        Children = [Icon(glyph, 14f, Tok.TextSecondary), new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextSecondary }],
    };

    static Element RoundBtn(string glyph, Action onClick) => new BoxEl
    {
        Width = 36f, Height = 36f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(18f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onClick,
        Children = [Icon(glyph, 13f, Tok.TextSecondary)],
    };
}

// The sort-direction caret: pops in (scale + fade) when its column becomes the active sort, and springs its rotation
// 0°↔180° (up↔down) on every direction flip — so the Title header's Title↑→Title↓→Artist↑→Artist↓ run reads as one
// continuous rotation rather than four glyph swaps. Self-contained, so it survives the header's re-render on each sort
// (its column persists → the spring is velocity-continuous; a column change remounts it → it pops in afresh).
sealed class SortCaret : Component
{
    readonly IReadSignal<TrackSort> _sort;
    public SortCaret(IReadSignal<TrackSort> sort) { _sort = sort; }

    public override Element Render()
    {
        bool desc = _sort.Value.Descending;   // subscribe → re-seed the rotation spring on each flip
        UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.EaseInOut, "in");
        UseTransition(AnimChannel.ScaleX, 0.3f, 1f, Expressive.Fast, Easing.Overshoot, "in");
        UseTransition(AnimChannel.ScaleY, 0.3f, 1f, Expressive.Fast, Easing.Overshoot, "in");
        UseSpring(AnimChannel.Rotation, desc ? 180f : 0f, SpringParams.FromResponse(0.30f, 0.7f), desc);
        return new BoxEl
        {
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Icon(TrackList.CaretGlyph, 9f, Tok.TextSecondary)],
        };
    }
}

// The Title header label: "Title", or "Artist" while the list is sorted by artist (the Title header owns the artist
// sort). A small rise + fade plays when the word flips (keyed on the text), so the Title↔Artist swap reads as a
// transition rather than a snap.
sealed class SortLabel : Component
{
    readonly IReadSignal<TrackSort> _sort;
    public SortLabel(IReadSignal<TrackSort> sort) { _sort = sort; }

    public override Element Render()
    {
        var col = _sort.Value.Column;
        string text = col == SortColumn.Artist ? Loc.Get(Strings.Detail.Column.Artist) : Loc.Get(Strings.Detail.Column.Title);
        bool active = col == SortColumn.Title || col == SortColumn.Artist;
        UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.SmoothOut, text);
        UseTransition(AnimChannel.TranslateY, 4f, 0f, Expressive.Fast, Easing.SmoothOut, text);
        return new TextEl(text) { Size = 12f, Weight = 600, Color = active ? Tok.TextSecondary : Tok.TextTertiary };
    }
}

// The "sort by" flyout button (Icons.Sort). Opens a MenuFlyout via the overlay service — the same DropDownButton path as
// ShellToolbar — listing the sort FIELDS as a radio group (Custom order / Title / Artist / Album / Date added /
// Duration, each gated by what the context actually has) plus an Ascending/Descending pair. This is the only way to sort
// by Artist (no column header of its own). Used in both the chrome toolbar and the vertical-header toolbar.
sealed class SortMenuButton : Component
{
    readonly IReadSignal<TrackSort> _sort;
    readonly Action<TrackSort> _setSort;
    readonly bool _hasAlbum, _hasDate, _labeled;
    public SortMenuButton(IReadSignal<TrackSort> sort, Action<TrackSort> setSort, bool hasAlbum, bool hasDate, bool labeled = false)
    { _sort = sort; _setSort = setSort; _hasAlbum = hasAlbum; _hasDate = hasDate; _labeled = labeled; }

    static string Label(SortColumn c) => c switch
    {
        SortColumn.Index => Loc.Get(Strings.Detail.Sort.CustomOrder),
        SortColumn.Title => Loc.Get(Strings.Detail.Sort.Title),
        SortColumn.Artist => Loc.Get(Strings.Detail.Sort.Artist),
        SortColumn.Album => Loc.Get(Strings.Detail.Sort.Album),
        SortColumn.DateAdded => Loc.Get(Strings.Detail.Sort.DateAdded),
        SortColumn.Duration => Loc.Get(Strings.Detail.Sort.Duration),
        _ => "",
    };

    IReadOnlyList<MenuFlyoutItem> Items()
    {
        var cur = _sort.Peek();
        var fields = new List<SortColumn>(6) { SortColumn.Index, SortColumn.Title, SortColumn.Artist };
        if (_hasAlbum) fields.Add(SortColumn.Album);
        if (_hasDate) fields.Add(SortColumn.DateAdded);
        fields.Add(SortColumn.Duration);

        var items = new List<MenuFlyoutItem>(fields.Count + 3);
        foreach (var col in fields)
            items.Add(MenuFlyoutItem.RadioItem(Label(col), cur.Column == col,
                () => _setSort(col == SortColumn.Index ? TrackSort.Default : new TrackSort(col, cur.Descending))));

        // Direction — irrelevant for Custom order, so it's disabled (and neither shown checked) there.
        bool custom = cur.Column == SortColumn.Index;
        items.Add(MenuFlyoutItem.Separator);
        items.Add(MenuFlyoutItem.RadioItem(Loc.Get(Strings.Detail.Sort.Ascending), !custom && !cur.Descending,
            () => { var s = _sort.Peek(); if (s.Column != SortColumn.Index) _setSort(s with { Descending = false }); }, enabled: !custom));
        items.Add(MenuFlyoutItem.RadioItem(Loc.Get(Strings.Detail.Sort.Descending), !custom && cur.Descending,
            () => { var s = _sort.Peek(); if (s.Column != SortColumn.Index) _setSort(s with { Descending = true }); }, enabled: !custom));
        return items;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        bool active = _sort.Value.Column != SortColumn.Index;   // subscribe → accent "on" state while a sort is active

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(Items(), () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return _labeled
            ? ToolFx.LabeledButton(Icons.Sort, Loc.Get(Strings.Library.SortBy), active, Toggle, h => anchor.Value = h)
            : ToolFx.Button(Icons.Sort, active, Toggle, h => anchor.Value = h);
    }
}

// Shared chrome for the track-list toolbar buttons: the 32px icon button (with the accent "on" pill when active) and the
// flyout panel surface. Keeps Filter/Sort/More/List visually identical.
static class ToolFx
{
    // The toolbar icon button. Active → a subtle accent pill + accent glyph (the WinUI ToggleButton "on" look); idle → ghost.
    public static Element Button(string glyph, bool active, Action onClick, Action<NodeHandle> onRealized)
    {
        ColorF accent = Tok.AccentTextPrimary;
        return new BoxEl
        {
            Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(16f),
            Fill = active ? accent with { A = 0.16f } : ColorF.Transparent,
            HoverFill = active ? accent with { A = 0.24f } : Tok.FillSubtleTertiary,
            PressedFill = active ? accent with { A = 0.12f } : Tok.FillSubtleTertiary,
            OnClick = onClick, OnRealized = onRealized,
            Children = [Ui.Icon(glyph, 14f, active ? accent : Tok.TextSecondary)],
        };
    }

    // The labeled (icon + text) command-bar form of Button — the wide-layout variant; opens the same flyout on click and
    // carries the same accent "on" ramp so an active filter/sort reads identically to the icon-only form.
    public static Element LabeledButton(string glyph, string label, bool active, Action onClick, Action<NodeHandle> onRealized)
    {
        ColorF accent = Tok.AccentTextPrimary;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Height = 32f,
            Padding = new Edges4(10f, 0f, 12f, 0f),
            Corners = CornerRadius4.All(16f),
            Fill = active ? accent with { A = 0.16f } : ColorF.Transparent,
            HoverFill = active ? accent with { A = 0.24f } : Tok.FillSubtleTertiary,
            PressedFill = active ? accent with { A = 0.12f } : Tok.FillSubtleTertiary,
            OnClick = onClick, OnRealized = onRealized,
            Children =
            [
                Ui.Icon(glyph, 14f, active ? accent : Tok.TextSecondary),
                new TextEl(label) { Size = 13f, Weight = 600, Color = active ? accent : Tok.TextSecondary },
            ],
        };
    }

    // A vertical group separator (the WinUI AppBarSeparator) — a 1px divider between command groups in the bar.
    public static Element Separator() => new BoxEl
    {
        Width = 1f, Height = 20f, AlignSelf = FlexAlign.Center, Fill = Tok.StrokeDividerDefault,
        Margin = new Edges4(WaveeSpace.XS, 0f, WaveeSpace.XS, 0f),
    };

    public static PopupOptions Popup => new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false };
}

// The search box's trailing filter funnel (mounted as the EditableText RightAffix — so "advanced filter" lives INSIDE
// the Find box). Opens a light CHECKABLE MenuFlyout of the quick-filter toggles (Hide explicit / Videos only) — the same
// lightweight menu style as Sort, replacing the old heavy titled Layer panel. Shows an accent pill + glyph when a
// filter is set.
sealed class FilterButton : Component
{
    readonly IReadSignal<TrackFilterFlags> _flags;
    readonly Action<TrackFilterFlags> _setFlags;
    readonly bool _hasVideo;
    public FilterButton(IReadSignal<TrackFilterFlags> flags, Action<TrackFilterFlags> setFlags, bool hasVideo)
    { _flags = flags; _setFlags = setFlags; _hasVideo = hasVideo; }

    // Checkable menu items (WinUI AppBarToggleButton-in-menu): the ✓ shows the live flag; clicking toggles it.
    IReadOnlyList<MenuFlyoutItem> Items()
    {
        var f = _flags.Peek();
        var items = new List<MenuFlyoutItem>(2)
        {
            MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.Filter.HideExplicit), (f & TrackFilterFlags.HideExplicit) != 0,
                () => _setFlags(_flags.Peek() ^ TrackFilterFlags.HideExplicit)),
        };
        if (_hasVideo)
            items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.Filter.VideosOnly), (f & TrackFilterFlags.VideosOnly) != 0,
                () => _setFlags(_flags.Peek() ^ TrackFilterFlags.VideosOnly)));
        return items;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        bool active = _flags.Value != TrackFilterFlags.None;   // subscribe → accent when a quick-filter toggle is on
        ColorF accent = Tok.AccentTextPrimary;

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, () => MenuFlyout.Build(Items(), () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight, ToolFx.Popup);
            handle.Value.ClosedAction = () => handle.Value = null;
        }
        // Mirror the WinUI TextControlButton affix (EditableText.InnerButton): Width 30 + the inner-button margin 0,4,4,4,
        // and NO explicit height / AlignSelf — the field's affix row (AlignItems=Stretch) fills it to full height, while
        // AlignItems/Justify=Center vertically centers the glyph. Accent pill + glyph when a filter is active.
        return new BoxEl
        {
            Width = 30f, Margin = new Edges4(0f, 4f, 4f, 4f),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(WaveeRadius.Control),
            Fill = active ? accent with { A = 0.16f } : ColorF.Transparent,
            HoverFill = active ? accent with { A = 0.24f } : Tok.FillSubtleSecondary,
            PressedFill = active ? accent with { A = 0.12f } : Tok.FillSubtleTertiary,
            OnClick = Toggle, OnRealized = h => anchor.Value = h,
            Children = [Icon(Icons.Filter, 14f, active ? accent : Tok.TextSecondary)],
        };
    }
}

// The List button: opens a flyout with a stepped slider (Compact · Default · Cozy · Comfortable) controlling row height.
sealed class ListButton : Component
{
    readonly IReadSignal<int> _density;
    readonly Action<int> _setDensity;
    readonly bool _labeled;
    public ListButton(IReadSignal<int> density, Action<int> setDensity, bool labeled = false) { _density = density; _setDensity = setDensity; _labeled = labeled; }

    internal static string Label(int d) => d switch { 0 => Loc.Get(Strings.Detail.Density.Compact), 2 => Loc.Get(Strings.Detail.Density.Cozy), 3 => Loc.Get(Strings.Detail.Density.Comfortable), _ => Loc.Get(Strings.Detail.Density.Default) };

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        Element Content() => Embed.Comp(() => new DensityPanel(_density, _setDensity));

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, Content, FlyoutPlacement.BottomEdgeAlignedRight, ToolFx.Popup);
            handle.Value.ClosedAction = () => handle.Value = null;
        }
        // Never accent — density is a view preference, not an active filter/sort (matches the reasoning in the design).
        return _labeled
            ? ToolFx.LabeledButton(Icons.List, Loc.Get(Strings.Detail.Density.RowSize), false, Toggle, h => anchor.Value = h)
            : ToolFx.Button(Icons.List, false, Toggle, h => anchor.Value = h);
    }
}

// The density flyout's body — its own Component so the slider + label re-render as the value changes during a drag.
sealed class DensityPanel : Component
{
    readonly IReadSignal<int> _density;
    readonly Action<int> _setDensity;
    public DensityPanel(IReadSignal<int> density, Action<int> setDensity) { _density = density; _setDensity = setDensity; }

    public override Element Render()
    {
        int d = _density.Value;   // subscribe → the slider thumb + label track the value
        return Layer(Edges4.All(WaveeSpace.M),
            new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.S, MinWidth = 240f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Detail.Density.RowSize)) { Size = 13f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f },
                            new TextEl(ListButton.Label(d)) { Size = 12f, Color = Tok.TextSecondary },
                        ],
                    },
                    Slider.Ranged((float)d, v => _setDensity(Math.Clamp((int)MathF.Round(v), 0, 3)),
                        new Slider.Options { Min = 0f, Max = 3f, Step = 1f, TickFrequency = 1f },   // Step=1 → snaps to each level
                        length: 216f),
                ],
            });
    }
}
