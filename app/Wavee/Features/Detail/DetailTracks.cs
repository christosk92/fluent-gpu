using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
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
    const float ActionsColWidth = 40f;              // trailing "..." overflow column (28px button + breathing room)
    const int VerticalHeroIndex = 0;
    const int VerticalChromeIndex = 1;
    const int VerticalTrackStart = 2;
    const float VerticalHeaderFallbackHeight = 260f;
    const float VerticalScrollBucket = 2f;
    const int TrackOverscanItems = 16;

    // The detail model is a Loadable: the HEADER bits (HasVideo, columns) read reactively from its current value
    // (preview → full), and the TRACK ROWS stream in via Skel.Region — which derives the shimmer from the REAL Row
    // template (ONE source, no hand-written skeleton). _model / _tracks are refreshed at the top of Render.
    readonly Loadable<DetailModel> _full;
    DetailModel _model = DetailModel.Empty;
    IReadOnlyList<Track> _tracks = Array.Empty<Track>();
    readonly Signal<Route> _route;                             // read reactively → cfg re-derived so ONE list serves successive detail routes
    DetailConfig _cfg = DetailConfig.Album;                     // derived from route kind + loaded ReleaseKind at the top of Render
    readonly PlaybackBridge? _bridge;
    readonly DetailHandlers _initialH;                         // first-frame fallback before DetailShell publishes live handlers
    DetailHandlers _h;                                         // refreshed from _liveHandlers at the top of every render
    readonly IReadSignal<DetailHandlers?>? _liveHandlers;       // signal parent→child path: accent/actions must not freeze
    readonly bool _showToolbar;                                // false in the vertical layout (the header owns the toolbar)
    readonly bool _embedded;                                   // true when hosted inside a compact pane (Library master-detail): the
                                                               // SAME virtualized list + cell, but no album trailing (About/Fans/More-by)
                                                               // so the rows ARE the scroller — the tier system still drops columns to fit.
    readonly bool _verticalHeader;                              // narrow detail mode: hero + chrome are measured rows in this list's scroller
    readonly Signal<bool>? _verticalHeaderPinned;                // flipped from scroll offset so the pinned chrome bar takes over
    readonly Signal<float> _verticalScrollOffset = new(0f);
    readonly Signal<float> _verticalHeaderHeight = new(0f);
    readonly Signal<float> _verticalHeroW = new(0f);           // measured page width (vertical mode) → the hero's orientation/art size
    bool _hasDate;                                             // any track carries an AddedAt → the Date-added column exists
    bool _hasBy;                                               // collaborative (≥2 contributors) → the Added-by column exists
    readonly Signal<int> _tier = new(0);                       // width tier (0 = widest/full), written by OnBoundsChanged
    readonly Signal<int> _visibleCount = new(0);
    readonly Signal<int> _verticalItemCount = new(VerticalTrackStart + 1);
    float _lastRightW;                                         // last measured right-area width — replayed once when the rail layout-lock clears (Task C)
    readonly SelectionModel _selection = new();                // external → survives a tier remount
    readonly Dictionary<int, TrackSize[]> _tracksByTier = new();
    (bool Date, bool By, bool Video) _lastCols = (false, false, false);   // the columns depend on the tracks (Date-added/Added-by/Video); when they
                                                                          // arrive (preview→full) the cached column SIZES must be recomputed or the grid wraps
    (TrackSort Sort, string Query, TrackFilterFlags Flags) _viewKey = (new((SortColumn)(-1), false), "\0", TrackFilterFlags.None);   // invalid sentinel
    IReadOnlyList<Track>? _viewTrackSet;                       // source-list identity paired with the sort/filter cache key
    int[] _view = Array.Empty<int>();                          // filtered + sorted → original track-index map (rows read via this)
    Memo<TrackRowsSnapshot>? _rowsSnapshot;                    // atomic model/config/handlers/sort/filter value for persistent rows
    BoundItemsSource<Track>? _rowItems;                        // snapshot + recycled slot index resolve together
    AsyncCommandSet<string>? _play;                            // per-track-id play command in flight → the row's #-cell buffering spinner
    string? _lastCtxUri;                                       // last loaded context uri → detect a reused-slot album swap (invalidate view/columns/selection)
    IReadOnlyList<Track>? _lastTrackSet;                       // last model.Tracks INSTANCE → an in-place refresh (same ContextUri, new list) invalidates the view cache

    // ── §4.6 realtime membership choreography (live add/remove/move/reset narration) ──────────────────────────────────
    // Pure item transitions, seeded IN the render that commits the new order (same frame — a late seed reads as a
    // jump-then-snap-back flash): a removed row simply vanishes while the rows below FLIP-glide up to reclaim the space;
    // an added row's neighbors part (FLIP down) and the row itself fades in at its slot. No overlays, no remounts.
    readonly ItemsViewController _listCtl = new();             // scroll anchoring (ScrollOffset/ScrollBy)
    readonly Signal<int> _dispVer = new(0);                    // bump → the ItemsView displacement seed re-runs (FLIP/fade)
    readonly Signal<int> _resetEpoch = new(0);                 // curated re-cut → keyed remount crossfade (fresh scroll state)
    readonly Dictionary<int, (float dx, float dy)> _flip = new();        // new display index → FLIP start residual (dy DIP)
    readonly Dictionary<int, (float from, float delayMs)> _fade = new(); // added display index → opacity ease-in + stagger
    Track[]? _lastDisplayed;                                   // displayed (view-ordered) snapshot — the keyed-diff baseline
    LibraryBridge? _lib;                                       // Mutations bridge → per-row heart saved-state + toggle (null when no Mutations source)
    ActionServices? _acts;                                     // the signals-first action system (row context menus + batch bar) — cached in Render like _lib
    IOverlayService? _menuOverlay;                             // the overlay service the rows' attached context menus open through (cached in Render)

    // ── "Recommended songs" (playlist extender) — appended into the SAME bound list, after the track rows ──────────────
    // Owned/collaborative playlists only (gated in Render). The list carries: track rows · one "Recommended" header row ·
    // N recommendation rows. The header's mount is the lazy first-fetch trigger; Refresh + Add re-fetch with the ACCUMULATED
    // skip set so every batch is fresh. State is signals-first so a batch landing / an optimistic add re-skins in place.
    const int RecBatch = 20;                                   // one extender page (matches the HAR capture)
    static readonly ColumnSet RecColumns = new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: false, Thumb: false);
    readonly Signal<IReadOnlyList<Track>> _recs = new(Array.Empty<Track>());
    readonly Signal<int> _recState = new(0);                   // 0 idle · 1 loading · 2 loaded
    readonly HashSet<string> _recShown = new(StringComparer.Ordinal);   // every id ever shown → the accumulated skip set (non-repeating batches)
    readonly System.Threading.CancellationTokenSource _recCts = new();
    readonly Signal<int> _listCount = new(0);                  // ItemsView TOTAL (track rows + rec rows); _visibleCount stays the track count for §4.6
    Services? _svc;                                            // cached in Render → the header/add handlers reach the extender + the post seam
    Action<Action>? _post;
    readonly HashSet<string> _recAdding = new(StringComparer.Ordinal);
    Memo<bool>? _checksVisible;                                 // equality-gated: checkbox lane visible (toggle OR selection)
    Func<bool> _checksVisibleRead = static () => false;        // stable thunk for bound row lanes (repointed each render)

    public TrackList(Signal<Route> route, Loadable<DetailModel> full, PlaybackBridge? bridge, DetailHandlers h,
                     bool showToolbar = true, bool embedded = false, bool verticalHeader = false,
                     Signal<bool>? verticalHeaderPinned = null, IReadSignal<DetailHandlers?>? liveHandlers = null)
    {
        _route = route; _full = full; _bridge = bridge; _initialH = _h = h; _liveHandlers = liveHandlers;
        _showToolbar = showToolbar; _embedded = embedded;
        _verticalHeader = verticalHeader && !embedded;
        _verticalHeaderPinned = verticalHeaderPinned;
    }

    // The placeholder row the engine derives the shimmer from — the REAL Row(...) with an empty track, so the skeleton
    // rows always match the real rows (the single-source-of-truth the skeleton kit is built on).
    static readonly Track EmptyTrack = new("", "", "", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0L, false, null);

    readonly record struct TrackRowsSnapshot(
        DetailModel Model, DetailConfig Config, DetailHandlers Handlers,
        TrackSort Sort, string Query, TrackFilterFlags Flags,
        bool MarqueeDisabled, string? TopTrackId);

    // The visible order (cached, keyed by sort + filter): view[displayPos] = original track index, for the tracks that
    // pass the filter (search query + hide-explicit), in the current sort order. Read live by the frozen row template /
    // keyOf / invoke via .Peek(), so a SORT change re-skins the realized window in place; a FILTER change (which alters
    // the count) instead remounts the list via the keyed wrapper.
    int[] View(TrackRowsSnapshot snapshot)
    {
        var s = snapshot.Sort;
        string q = snapshot.Query;
        var flags = snapshot.Flags;
        var key = (s, q, flags);
        var tracks = snapshot.Model.Tracks;
        if (!ReferenceEquals(_viewTrackSet, tracks) || !key.Equals(_viewKey))
        {
            bool hideX = (flags & TrackFilterFlags.HideExplicit) != 0;
            bool vidOnly = (flags & TrackFilterFlags.VideosOnly) != 0;
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
            _view = list.ToArray(); _viewKey = key; _viewTrackSet = tracks;
        }
        return _view;
    }

    int[] View() => _rowsSnapshot is { } rows ? View(rows.Peek()) : _view;

    Track TrackAt(TrackRowsSnapshot snapshot, int displayPos)
    {
        var view = View(snapshot);
        if ((uint)displayPos >= (uint)view.Length) return EmptyTrack;
        int original = view[displayPos];
        var tracks = snapshot.Model.Tracks;
        return (uint)original < (uint)tracks.Count ? tracks[original] : EmptyTrack;
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
    ColumnSet SetFor(int tier) => _verticalHeader
        // Vertical (Apple Music) profile: a simplified # · (thumb) · Song(title + artist subline) · (Album) · Time · [⋯]
        // table. The artist rides the title subline (Spotify-style, per config), never its own lane; Album appears at wide
        // tiers (playlists/Liked) on the SAME gate as the standard profile; album surfaces retain their Plays lane so
        // stacked and automatic layouts expose the same hydrated metadata. No by/date/video/heart lanes (liking stays
        // via hover ⋯ / context menu).
        ? new(Album: _cfg.ShowAlbumColumn && tier < 2, By: false, Date: false, Video: false,
              Plays: _cfg.ShowPlays && tier < 3, Heart: false,
              Thumb: _cfg.ShowArtThumb && tier < 5, Actions: tier < 6, Tier: tier)
        : new(
            Album: _cfg.ShowAlbumColumn && tier < 2,
            By: _hasBy && tier < 1,
            Date: _hasDate && tier < 3,
            Video: _cfg.ShowPlays && _model.HasVideo && tier < 2,
            Plays: _cfg.ShowPlays && tier < 3,
            Heart: tier < 4,
            Thumb: _cfg.ShowArtThumb && tier < 5,
            Actions: tier < 6,          // ultra-compact tier drops the "…" lane (still on the row context menu)
            Tier: tier);

    // Right-area-width breakpoints (sized off the widest column set), so the Star Title keeps a usable width at each
    // tier's minimum. Fewer-column contexts just cross the same widths with nothing to drop until a present column.
    static int TierFor(float w, int prev) => DetailLayoutBreakpoints.TierFor(w, prev);

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
        if (s.Actions) t.Add(TrackSize.Px(ActionsColWidth));   // trailing "..." overflow lane (dropped at the ultra-compact tier)
        var arr = t.ToArray();
        _tracksByTier[tier] = arr;
        return arr;
    }

    public override Element Render()
    {
        _play = UseAsyncCommands<string>();      // keyed by track id; a row's #-cell shows the buffer spinner while its PlayAsync runs (same instance each render)
        _lib = UseContext(LibraryBridge.Slot);   // Mutations bridge for the per-row heart (saved-state + toggle)
        _acts = UseContext(ActionServices.Slot); // the action system behind the row context menus + the batch bar
        _menuOverlay = UseContext(Overlay.Service);   // the overlay service the rows' attached menus open through
        var svc = UseContext(Services.Slot);     // extender client (recommended songs) + gate on live edits
        _svc = svc; _post = UsePost();           // cached so the rec fetch/add handlers reach the extender + marshal results back to the UI thread
        Context.UseSignalEffect(() => Reactive.OnCleanup(() => { try { _recCts.Cancel(); _recCts.Dispose(); } catch { } }));   // cancel in-flight rec fetches on unmount
        var ui = UseContext(ShellUi.Slot);       // rail layout-defer lock (Task C): gate tier churn during a rail reflow
        bool railLocked = ui?.RailLayoutLocked.Value ?? false;   // subscribe → flush the settled tier when the lock clears
        // One stable reactive snapshot owns every non-slot input a persistent row can observe. The memo reads the real
        // sources directly, so child rows never depend on this parent publishing mutable fields first.
        var rowsSnapshot = UseComputed(() =>
        {
            var handlers = _liveHandlers?.Value ?? _initialH;
            var currentModel = _full.Value.Value;
            var config = DetailPage.ResolveConfig(DetailPage.ParseDetail(_route.Value).Kind, currentModel);
            if (_embedded) config = config with { HasTrailing = false };
            _ = AppearancePrefs.Epoch.Value;
            bool noMarquee = svc?.Settings.Get(WaveeSettings.DisableMarquee) ?? false;
            return new TrackRowsSnapshot(
                currentModel, config, handlers,
                handlers.Sort.Value, handlers.Query.Value.Trim(), handlers.Flags.Value,
                noMarquee, TopTrack(currentModel.Tracks));
        });
        var rowItems = UseMemo(() => BoundItems.Project(
            rowsSnapshot,
            snapshot => View(snapshot).Length,
            (snapshot, displayIndex) => TrackAt(snapshot, displayIndex),
            EmptyTrack), DepKey.Empty);
        _rowsSnapshot = rowsSnapshot;
        _rowItems = rowItems;

        var rowState = rowsSnapshot.Value;
        var model = rowState.Model;
        _h = rowState.Handlers;
        _model = model; _tracks = model.Tracks; _hasDate = model.HasDateAdded; _hasBy = model.HasAddedBy;
        if (_h.PlayAllOverride is { Length: > 0 } playAllCell) playAllCell[0] = () => StartVisible(0);   // rail "Play" → visible (sorted) order from the top
        _cfg = rowState.Config;
        // Embedded in a compact pane (Library): drop the album trailing so the rows are the scroller (the pane owns the
        // hero + actions above). Everything else — the cell, hover transport, now-playing, heart, tier columns — is identical.
        // Reused slot: a detail-route swap changes the track set under a stable (sort,query,flags) view key, so the cached
        // view map + per-tier column sets + selection would be STALE (wrong / out-of-range indices). Invalidate them on a
        // context change so the new page recomputes cleanly.
        if (model.ContextUri != _lastCtxUri)
        {
            _lastCtxUri = model.ContextUri;
            _viewKey = (new((SortColumn)(-1), false), "\0", TrackFilterFlags.None);
            _tracksByTier.Clear();
            _selection.ClearSelection();
            // §4.6 — navigation is not an edit: never choreograph across a context swap.
            _lastDisplayed = null;
            _flip.Clear(); _fade.Clear();
            _resetEpoch.Value = _resetEpoch.Peek() + 1;   // remount the virtual list — bound rows must not recycle A's cells under B's route
            if (_verticalHeader) ResetVerticalHeaderScroll();
        }

        // The present columns depend on the tracks (Date-added/Added-by/Video appear once they load). When that set
        // changes, drop the cached per-tier column SIZES so the header + rows rebuild with a matching track count
        // (otherwise the grid has more cells than columns and wraps). The list key (below) folds it in to remount cleanly.
        var cols = (_hasDate, _hasBy, model.HasVideo);
        if (!cols.Equals(_lastCols)) { _tracksByTier.Clear(); _lastCols = cols; }

        // In-place refresh guard (§4.2): the view map is keyed only on (sort,query,flags), but JoinMembership hands us a
        // FRESH list instance whenever the tracks change — and a same-ContextUri live edit (a phone-side add/remove/move)
        // keeps the ContextUri guard above from firing. Key the view cache on the track-set IDENTITY so a shrink can't
        // leave a stale over-range index map (the latent IndexOutOfRange) and a same-size change can't show wrong rows.
        // Selection/scroll are NOT touched here — a same-playlist refresh must preserve them (the ContextUri guard owns that).
        bool trackSetChanged = !ReferenceEquals(_lastTrackSet, model.Tracks);
        if (trackSetChanged)
        {
            _lastTrackSet = model.Tracks;
            _viewKey = (new((SortColumn)(-1), false), "\0", TrackFilterFlags.None);
            _tracksByTier.Clear();
        }

        int tier = _tier.Value;                  // subscribe → re-render (new header + remount list) on a breakpoint cross
        // Self-heal (fail-safe #2): never RENDER a tier wider than the last measured width supports. If the tier signal
        // is stale (a stuck rail lock, a lost flush) a too-wide column set meets a too-narrow pane and the grid's
        // overflow guard crushes columns into overlapping glyphs; clamping here makes every render structurally safe
        // regardless of how the signal got stale. (Narrower-than-needed is fine — the next OnBoundsChanged widens it.)
        if (_lastRightW > 0f && ui?.RailLockActive != true) { int fit = TierFor(_lastRightW, tier); if (fit > tier) tier = fit; }
        var set = SetFor(tier);
        var tracks = TracksFor(tier);
        var sort = _h.Sort.Value;                // subscribe → re-render (header carets) on sort change
        int density = _h.Density.Value;          // subscribe → remount with the new row height on density change
        string query = _h.Query.Value;           // subscribe → remount with the filtered set on query change
        var flags = _h.Flags.Value;              // subscribe → remount on a quick-filter toggle
        float rowH = TrackRow.RowHeightFor(density);
        var verticalLayout = UseMemo(() => new MeasuredStackVirtualLayout(rowH), rowH);
        _checksVisible = UseComputed(() =>
        {
            if (_h.MultiSelect?.Value == true) return true;
            // Auto-show only for a MULTI-selection (2+): a plain single click selects a row constantly during normal
            // browsing and must not flip the whole list into checkbox mode.
            _ = _selection.Version.Value;
            int n = 0;
            for (int i = 0; i < _selection.ItemCount && n < 2; i++)
                if (_selection.IsSelected(i) && DisplayTrack(i, _verticalHeader ? VerticalTrackStart : 0) is not null)
                    n++;
            return n >= 2;
        });
        _checksVisibleRead = () => _checksVisible.Value;
        bool checkInset = _checksVisible.Value;

        // §4.6 — a same-context in-place track-set swap (a live push / /diff / a background refresh landing via SetReady)
        // narrates itself. Everything happens HERE, in the render that commits the new order, so the anchor adjust and
        // the FLIP/fade seeds land with the SAME frame — a post-layout seed is one frame late and reads as a
        // jump-then-snap-back flash. The snapshot refreshes EVERY render (sort/filter also reorder the displayed
        // sequence), so the diff baseline is always the order the user was actually looking at.
        int resetEpochBefore = _resetEpoch.Peek();
        {
            var vNow = View();
            var displayedNow = new Track[vNow.Length];
            for (int i = 0; i < vNow.Length; i++) displayedNow[i] = _tracks[vNow[i]];
            if (trackSetChanged && _lastDisplayed is { Length: > 0 } prevDisplayed && model.ContextUri == _lastCtxUri)
                Choreograph(prevDisplayed, displayedNow, rowH);
            _lastDisplayed = displayedNow;
        }
        // Curated re-cut (reset epoch) remounts replay row mount-opacity; tier/density/filter remounts do not.
        bool narrateRemount = _resetEpoch.Peek() != resetEpochBefore;
        // The bound slots are cheap and persistent. Partial cold materialization leaves the track window visibly
        // catching up during fast scroll, especially when this list is embedded in the trailing page scroller.
        bool staggerCold = false;
        // Task C flush: when the rail layout-lock clears, apply the SETTLED tier once from the last measured width (the
        // intermediate reflow widths were skipped in OnBoundsChanged while locked, so the list Key never churned/remounted
        // mid-reflow). Keyed on railLocked → fires on the false-edge; the write converges (dep is the bool).
        UseLayoutEffect(() =>
        {
            if (!railLocked && _lastRightW > 0f) { int t = TierFor(_lastRightW, _tier.Peek()); if (t != _tier.Peek()) _tier.Value = t; }
        }, railLocked);
        // Now-playing / sort / column re-skin is per-row now: each bound row subscribes to the bridge + _h.Sort inside
        // its own binds (BoundRowContent / BoundTitle), so a track change recolours and a sort change reorders the
        // realized rows IN PLACE — no whole-list epoch, no list re-render. Tier/column changes alter the slot SET and
        // remount via the keyed wrapper below.
        // Labels only at the widest tiers (≥ 720px): the LABELED bar alone is ~400px, so with the fixed 220px search box
        // it needs ~630px — at the old tier-3 threshold (440px) the bar overflowed and the card clip cut the search box
        // mid-control. Icon-only + the tiered search width below always fit each tier's minimum.
        bool labeled = tier <= 1;
        Element chrome = Chrome(set, tracks, sort, labeled, tier, checkInset, padX: TrackRow.PadXFor(tier));
        int visible = View().Length;
        // "Recommended songs": owned/collaborative playlists only, non-embedded, non-vertical, live edits available. When
        // ON, the header (+ rec rows) are appended AFTER the track rows in the SAME bound list — the list TOTAL is a
        // SEPARATE signal (_listCount) so _visibleCount stays the track count the §4.6 choreography seeds are keyed to.
        bool recsOn =
            _cfg.Recommendations && !_embedded && !_verticalHeader
            && svc?.RealExtender is not null
            && PlaylistInlineEdit.SpotifyEditsLive(svc)
            && model.Capabilities.CanEditItems
            && model.ContextUri is { Length: > 0 };
        int recCount = recsOn ? _recs.Value.Count : 0;   // subscribe (only when ON) → the list re-sizes when a batch lands / an add removes one
        int listTotal = recsOn ? visible + 1 + recCount : visible;   // +1 = the always-present "Recommended" header row
        UseLayoutEffect(() =>
        {
            if (_visibleCount.Peek() != visible) _visibleCount.Value = visible;
            if (_listCount.Peek() != listTotal) _listCount.Value = listTotal;
            int verticalCount = VerticalTrackStart + Math.Max(visible, 1);
            if (_verticalItemCount.Peek() != verticalCount) _verticalItemCount.Value = verticalCount;
        }, (visible, listTotal));

        Element RealList()
        {
            if (_verticalHeader)
                return VerticalList(visible, set, tracks, sort, labeled, tier, rowH, narrateRemount, staggerCold, verticalLayout);
            if (recsOn)
            {
                // A search/filter that matched nothing still shows the no-match message (recs browse the whole list, not
                // the filtered slice); an EMPTY owned playlist still renders — the header at index 0 fetches immediately.
                if (visible == 0 && _tracks.Count > 0) return FilterEmpty(noTracks: false);
                // The bound slots branch on the recycled index (RowOrRecContent): track rows keep the selection skin;
                // the "Recommended" header + rec rows render their OWN content, so they never join the track multi-select
                // (exactly like the vertical hero/chrome rows). The out-of-range guards (PlayRow / DisplayTrack / the
                // SelectionBar null-track skip) already reject the appended indices.
                return ItemsView.CreateBound(
                    listTotal,
                    scope => Embed.Comp(() => new RowOrRecContent(this, scope, set, tracks, rowH, narrateRemount)),
                    RepeatLayout.Stack(rowH),
                    selectionMode: _cfg.Selection,
                    selection: _selection,
                    isItemInvokedEnabled: true,
                    itemInvoked: i => { if (rowItems.TryPeek(i, out _)) PlayRow(i); },
                    isItemEnabled: i => rowItems.TryPeek(i, out _),   // only track rows are roving-focus / selection targets
                    overscan: TrackOverscanItems,
                    grow: _cfg.HasTrailing ? 0f : 1f,
                    autoEdgeFade: !_cfg.HasTrailing,
                    staggerColdRealize: staggerCold,
                    scrollKey: _route.Value.Name + ":r" + _resetEpoch.Value,
                    controller: _listCtl,
                    itemDisplacement: static _ => (0f, 0f),
                    displacementVersion: _dispVer,
                    itemFlipFrom: i => _flip.TryGetValue(i, out var f) ? f : null,
                    itemFadeFrom: i => _fade.TryGetValue(i, out var f) ? f : null,
                    itemCountSignal: _listCount,
                    onScrollGeometryChanged: SwipeCloseObserver());
            }
            return visible == 0
            ? FilterEmpty(_tracks.Count == 0)     // empty playlist, or a filter that matched nothing
            // Bound rows (signals-first): each slot mounts ONCE and recycles by an index-signal write. Selection flips a
            // bound pill opacity — no list re-render, no remount, no Enter replay (the flash fix); now-playing/sort
            // re-skin each row's content in place via its own subscriptions. The row maps its display position → track
            // through View() inside its binds (sort-subscribed), so a sort change reorders in place; filter/density/tier
            // change the slot SET and remount via the keyed wrapper below.
            : ItemsView.CreateBound(
                rowItems,
                scope => WrapRowSwipe(scope.Row, BoundRowSkin(scope.Row, BoundRow(scope.Row, scope.Item, set, tracks, rowH, 0), rowH, narrateRemount, 0), 0, scope.Item),
                RepeatLayout.Stack(rowH),
                selectionMode: _cfg.Selection,
                selection: _selection,                // external → selection survives the tier remount
                isItemInvokedEnabled: true,
                itemInvoked: (i, _) => PlayRow(i),   // DoubleTap / Enter → same as a row click (visible-order play + now-playing toggle)
                overscan: TrackOverscanItems,
                grow: _cfg.HasTrailing ? 0f : 1f,
                // Alpha-mask edge fade: the page floats over a gradient wash (no opaque plate), so the surface-colour
                // EdgeCues fade self-skips — this feathers the rows' own alpha at the overflowing top/bottom instead.
                // Only when the ItemsView is itself the scroller (playlist/liked); the album path fades its outer scroll.
                autoEdgeFade: !_cfg.HasTrailing,
                // Realize the full oversized row window immediately. Bound slots are persistent; exposing partial
                // materialization during scroll reads as cut-off rows under the fixed chrome.
                staggerColdRealize: staggerCold,
                // Scroll-position restoration keyed by the detail content (route): navigate away from a 10k-track
                // playlist and back and the viewport seeds the saved row BEFORE its first realize — no scroll-to-top
                // flash, no jump (the engine scopes this per tab via the KeepAlive slot). A different album starts at
                // top. The reset epoch folds in so a curated re-cut starts a FRESH scroll state (top) instead of
                // restoring the pre-reset offset into all-new content.
                scrollKey: _route.Value.Name + ":r" + _resetEpoch.Value,
                // §4.6 choreography: the controller reads/adjusts the scroll for anchoring; the displacement seed
                // (target always rest) starts each row from its FLIP residual and eases added rows' opacity in.
                controller: _listCtl,
                itemDisplacement: static _ => (0f, 0f),
                displacementVersion: _dispVer,
                itemFlipFrom: i => _flip.TryGetValue(i, out var f) ? f : null,
                itemFadeFrom: i => _fade.TryGetValue(i, out var f) ? f : null,
                onScrollGeometryChanged: SwipeCloseObserver());
        }

        // The tracks stream in via the engine's skeleton boundary: while the model is Pending it shows shimmer rows the
        // engine DERIVES from the real Row(EmptyTrack) template (ONE definition — no hand-written shimmer, no drift); on
        // Ready it reveals the real virtualized list.
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
        // The reset epoch folds into the key: a curated re-cut REMOUNTS the list — the slots' mount-opacity entrance
        // replays as the §4.6 "playlist was refreshed" crossfade (the truthful narration for an editorial re-cut).
        // recsOn folds into the key: the ItemsView template + count-signal freeze at mount, so a gate flip (preview→full
        // model load turns CanEditItems on) must REMOUNT the list to swap in the recommendations template. Constant once
        // the full model has landed, so this is a one-time remount, not per-render churn.
        Element listKeyed = new BoxEl { Key = "list:" + _route.Value.Name + ":" + (_verticalHeader ? "vh:" : "") + "t" + tier + ":d" + density + ":q" + query + ":f" + (int)flags + ":r" + _resetEpoch.Value + (recsOn ? ":rec" : ""), Grow = listGrow, Shrink = 1f, MinHeight = 0f, Direction = 1, Children = [list] };

        Element rightBody = _cfg.HasTrailing ? TrailingBody(listKeyed) : listKeyed;

        var column = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            // Measure the right-area width → the active tier. Value-gated, so a re-render happens only on a breakpoint
            // cross (not every resize frame); the new tier itself never changes this box's width → no feedback loop.
            OnBoundsChanged = r => { if (r.W <= 0f) return; _lastRightW = r.W; if (_verticalHeader && MathF.Abs(_verticalHeroW.Peek() - r.W) > 4f) _verticalHeroW.Value = r.W; if (ui?.RailLockActive == true) return; int t = TierFor(r.W, _tier.Peek()); if (t != _tier.Peek()) _tier.Value = t; },
            Children = _verticalHeader ? [rightBody] : [chrome, rightBody],
        };
        // A component anchor does not mirror HitTestPassThrough from its rendered child. Keep a PLAIN full-bleed
        // pass-through positioner as the ZStack child so wheel/pointer input reaches the ItemsView below. Stretching the
        // component inside that positioner still gives SelectionCommandBar the real pane width for its responsive fit.
        int selectionTrackStart = _verticalHeader ? VerticalTrackStart : 0;
        Element selectionOverlay = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            HitTestPassThrough = true,
            AlignItems = FlexAlign.Stretch, Justify = FlexJustify.End,
            Children = [Embed.Comp(() => new SelectionCommandBar(_selection, i => DisplayTrack(i, selectionTrackStart), host: HostInfo))],
        };
        // Vertical/hero mode: the chrome (toolbar + column header) is a scrolling list row, so once the hero scrolls
        // past, PIN a slim copy to the top of the page — identity (Play + artwork + title) + search + the column headers.
        // Sort/density/select drop from the pinned state (still reached by scrolling up; column-header click-to-sort keeps
        // sorting reachable while pinned). Lives as a ZStack sibling of `column` — OUTSIDE listKeyed — so a query/filter
        // remount of the list never remounts the pinned bar (focus stays in the pinned search box while typing filters the
        // list), and it sits above BOTH the list and TrailingBody's album scroller.
        if (_verticalHeader && _verticalHeaderPinned is not null)
        {
            // A SEPARATE pinned chrome (the in-list/non-vertical `chrome` above stays plain): the sticky bar carries the
            // Spotify-style context identity (artwork + accent Play circle + title) as its left-cluster `lead`, so the
            // identity lives INSIDE the bar. The bar itself is a FLOATING GLASS PILL — inset from the page edges with
            // the app's card elevation + hairline (the ArtistShyPill surface language), not an edge-to-edge strip.
            Element pinnedChrome = Chrome(set, tracks, sort, labeled, tier, checkInset,
                                          lead: PinnedIdentity(), padX: TrackRow.PadXFor(tier), pinned: true);   // header columns align with the rows
            // Mostly-neutral glass: the accent only whispers through the tint. The previous 0.30/0.18 accent mix read
            // as a loud saturated color band over the list — worst in dark mode, where the lifted accent glows.
            float tintMix = Tok.Theme == ThemeKind.Dark ? 0.08f : 0.12f;
            ColorF glassTint = ColorF.Lerp(Tok.FillSolidBaseAlt, _h.Accent, tintMix) with { A = 1f };
            var glass = new AcrylicSpec(
                Tint: glassTint,
                TintOpacity: Tok.Theme == ThemeKind.Dark ? 0.70f : 0.78f,   // lighter so rows stay faintly visible
                BlurSigma: 14f,
                NoiseOpacity: 0.012f,
                LuminosityOpacity: Tok.Theme == ThemeKind.Dark ? 0.34f : 0.55f,
                Fallback: glassTint);
            Element bar = new BoxEl
            {
                Key = $"pinned-chrome:{(int)sort.Column}:{sort.Descending}",
                Direction = 1,
                Acrylic = glass, Fill = ColorF.Transparent,
                Corners = CornerRadius4.All(16f),
                Margin = new Edges4(12f, 8f, 16f, 0f),   // right 16 clears the list scrollbar
                BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
                Shadow = Elevation.Card,
                ClipToBounds = true,
                Animate = PinnedChromePresence, TransformOriginY = 0f,
                Children = [pinnedChrome],
            };
            Element pinnedOverlay = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                HitTestPassThrough = true,
                AlignItems = FlexAlign.Stretch, Justify = FlexJustify.Start,
                Children =
                [
                    Flow.Show(() => _verticalHeaderPinned is { } p && p.Value, bar),
                ],
            };
            return ZStack(column, pinnedOverlay, selectionOverlay) with { Grow = 1f, Shrink = 1f, MinHeight = 0f };
        }
        return ZStack(column, selectionOverlay) with { Grow = 1f, Shrink = 1f, MinHeight = 0f };
    }

    // Resolve a display row index (what the SelectionModel stores) → the track, through the current filtered+sorted view.
    Track? DisplayTrack(int itemIndex, int trackStart)
    {
        return _rowItems is { } items && items.TryPeek(itemIndex, out var track, trackStart) ? track : null;
    }

    // The hosting-playlist descriptor for the context menu / batch bar: only when this context is an editable playlist.
    // Maps the CURRENTLY SELECTED item indices through the live view to PlaylistRowRef(originalIndex, uri, itemId) —
    // called AFTER TrackContextMenu settles the selection, so the rows cover exactly the menu's target set (display
    // order; Index = the ORIGINAL playlist position the remove op needs).
    PlaylistHost? HostInfo()
    {
        if (_rowsSnapshot is not { } source) return null;
        var snapshot = source.Peek();
        var model = snapshot.Model;
        if (model.ContextUri is not { Length: > 0 } uri || !model.Capabilities.CanEditItems) return null;
        int trackStart = _verticalHeader ? VerticalTrackStart : 0;
        var v = View(snapshot);
        var tracks = model.Tracks;
        var rows = new List<PlaylistRowRef>();
        for (int i = 0; i < _selection.ItemCount; i++)
        {
            if (!_selection.IsSelected(i)) continue;
            int d = i - trackStart;
            if ((uint)d >= (uint)v.Length) continue;
            int orig = v[d];
            if ((uint)orig >= (uint)tracks.Count) continue;
            var t = tracks[orig];
            rows.Add(new PlaylistRowRef(orig, t.Uri, t.ContextUid ?? string.Empty));
        }
        return rows.Count == 0 ? null : new PlaylistHost(uri, model.Capabilities, rows);
    }

    Element VerticalList(int visible, ColumnSet set, TrackSize[] tracks, TrackSort sort, bool labeled, int tier,
                         float rowH, bool narrateRemount, bool staggerCold, MeasuredStackVirtualLayout layout)
    {
        int itemCount = VerticalTrackStart + Math.Max(visible, 1);
        int DisplayOf(int itemIndex) => itemIndex - VerticalTrackStart;
        (float dx, float dy)? FlipFrom(int itemIndex)
        {
            int d = DisplayOf(itemIndex);
            return d >= 0 && _flip.TryGetValue(d, out var f) ? f : null;
        }
        (float from, float delayMs)? FadeFrom(int itemIndex)
        {
            int d = DisplayOf(itemIndex);
            return d >= 0 && _fade.TryGetValue(d, out var f) ? f : null;
        }

        return ItemsView.CreateBound(
            itemCount,
            scope => Embed.Comp(() => new VerticalItemContent(this, scope, set, tracks, rowH, labeled, tier, narrateRemount)),
            RepeatLayout.Measured(layout),
            selectionMode: visible > 0 ? _cfg.Selection : ItemsSelectionMode.None,
            selection: _selection,
            isItemInvokedEnabled: true,
            itemInvoked: i => { if (_rowItems!.TryPeek(i, out _, VerticalTrackStart)) PlayRow(DisplayOf(i)); },
            itemText: i => _rowItems!.TryPeek(i, out var item, VerticalTrackStart) ? item.Title : "",
            isItemEnabled: i => _rowItems!.TryPeek(i, out _, VerticalTrackStart),
            overscan: TrackOverscanItems,
            grow: _cfg.HasTrailing ? 0f : 1f,
            autoEdgeFade: !_cfg.HasTrailing,
            staggerColdRealize: staggerCold,
            scrollKey: _route.Value.Name + ":r" + _resetEpoch.Value,
            controller: _listCtl,
            itemDisplacement: static _ => (0f, 0f),
            displacementVersion: _dispVer,
            itemFlipFrom: FlipFrom,
            itemFadeFrom: FadeFrom,
            onScrollGeometryChanged: _cfg.HasTrailing ? null : VerticalScrollObserver(),
            itemCountSignal: _verticalItemCount);
    }

    // ── §4.6 — the choreography pass. Runs INSIDE the render that commits the new order (the ItemsView child renders
    // after this and reads the bumped displacement version in the same pass), so the anchor adjust + the FLIP/fade seeds
    // land with the SAME frame — never a jump-then-animate flash. Pure item transitions: a removed row's slot rebinds to
    // the next track and every row below FLIP-glides up to reclaim the space; an added row's neighbors part downward and
    // the row fades in at its slot. Everything is bounded: the engine's displacement seed walks only the REALIZED window.
    void Choreograph(Track[] old, Track[] next, float rowH)
    {
        var delta = MembershipDiff.Diff(old, next);
        if (delta.IsEmpty) return;

        if (delta.IsReset)
        {
            // Curated re-cut (Discover-Weekly style): ONE deliberate crossfade — the keyed remount replays the slots'
            // mount entrance and the fresh scroll state starts at top — never a 40-row animation storm.
            _flip.Clear(); _fade.Clear();
            _resetEpoch.Value = _resetEpoch.Peek() + 1;   // read below in the same render — the remount lands this frame
            return;
        }

        float offset = _listCtl.ScrollOffset;
        int firstVis = Math.Max(0, (int)(offset / rowH));

        // (1) Anchor: the first visible SURVIVING row keeps its screen Y — adjust the offset by its index shift so an
        // add/remove ABOVE the viewport never yanks the content (the single most jarring live-list failure mode).
        var oldKeys = MembershipDiff.Keys(old);
        var newKeys = MembershipDiff.Keys(next);
        var newIdxByKey = new Dictionary<string, int>(newKeys.Length, StringComparer.Ordinal);
        for (int i = 0; i < newKeys.Length; i++) newIdxByKey[newKeys[i]] = i;
        int shift = 0;
        for (int i = Math.Clamp(firstVis, 0, Math.Max(0, oldKeys.Length - 1)); i < oldKeys.Length; i++)
            if (newIdxByKey.TryGetValue(oldKeys[i], out int ni)) { shift = ni - i; break; }
        if (shift != 0) _listCtl.ScrollBy(shift * rowH);

        // (2) FLIP residuals for EVERY survivor — an unmoved row is still screen-displaced when the anchor shifted
        // ((o−n+shift)·rowH; the anchor row itself resolves to 0) — and fade/slide-in for adds (staggered, capped at 8;
        // the delay HOLDS the from-value, so a staggered add is invisible until its turn — no pop).
        _flip.Clear(); _fade.Clear();
        var oldIdxByKey = new Dictionary<string, int>(oldKeys.Length, StringComparer.Ordinal);
        for (int i = 0; i < oldKeys.Length; i++) oldIdxByKey[oldKeys[i]] = i;
        for (int n = 0; n < newKeys.Length; n++)
            if (oldIdxByKey.TryGetValue(newKeys[n], out int o))
            {
                float resid = (o - n + shift) * rowH;
                if (MathF.Abs(resid) > 0.5f) _flip[n] = (0f, resid);
            }
        int addOrd = 0;
        foreach (var a in delta.Adds)
        {
            int n = a.NewIndex!.Value;
            _flip[n] = (0f, -6f);                                      // ease in from a slight rise…
            _fade[n] = (0f, Math.Min(addOrd++, 8) * 20f);              // …with a 20ms/row stagger, capped (no cascades)
        }

        _dispVer.Value = _dispVer.Peek() + 1;   // the ItemsView (a child, renders after this) seeds in the SAME frame
    }

    void SetVerticalPinned(bool value)
    {
        if (_verticalHeaderPinned is { } pinned && pinned.Peek() != value) pinned.Value = value;
    }

    void ResetVerticalHeaderScroll()
    {
        if (_verticalScrollOffset.Peek() != 0f) _verticalScrollOffset.Value = 0f;
        if (_verticalHeaderHeight.Peek() != 0f) _verticalHeaderHeight.Value = 0f;
        SetVerticalPinned(false);
    }

    float VerticalHeaderHeight(bool subscribe = false)
    {
        float h = subscribe ? _verticalHeaderHeight.Value : _verticalHeaderHeight.Peek();
        return h > 1f ? h : VerticalHeaderFallbackHeight;
    }

    // The pin moment: when the in-list chrome's top reaches the viewport top (i.e. the hero has fully scrolled past), so
    // the pinned copy takes over seamlessly from the row that just left.
    float VerticalPinnedThreshold() => MathF.Max(96f, VerticalHeaderHeight());

    // Pin hysteresis deadband: pin at >= threshold, unpin only below threshold - deadband, so scroll jitter right on the
    // pin line (touch, settle bounce) can't flicker the pinned chrome on and off every frame.
    const float VerticalPinDeadbandPx = 32f;

    long VerticalScrollKey(ScrollGeometry g)
    {
        float offset = MathF.Max(0f, g.OffsetY);
        float threshold = VerticalPinnedThreshold();
        long bucket = (long)MathF.Floor(offset / VerticalScrollBucket);
        // BOTH hysteresis edges as key bits — the observer must dispatch at the pin edge AND the unpin edge (the action
        // resolves which one applies from the current pinned state).
        long pinEdge = offset >= threshold ? 1L : 0L;
        long unpinEdge = offset >= threshold - VerticalPinDeadbandPx ? 1L : 0L;
        return (bucket << 2) | (pinEdge << 1) | unpinEdge;
    }

    void ApplyVerticalPin(float offset)
    {
        float threshold = VerticalPinnedThreshold();
        if (offset >= threshold) SetVerticalPinned(true);
        else if (offset < threshold - VerticalPinDeadbandPx) SetVerticalPinned(false);
        // inside the deadband: hold the current state
    }

    void OnVerticalScroll(ScrollGeometry g)
    {
        _swipeGroup.Close();
        float offset = MathF.Max(0f, g.OffsetY);
        if (MathF.Abs(_verticalScrollOffset.Peek() - offset) > 0.5f) _verticalScrollOffset.Value = offset;
        ApplyVerticalPin(offset);
    }

    (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? VerticalScrollObserver()
        => _verticalHeader ? (VerticalScrollKey, OnVerticalScroll) : null;

    (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) SwipeCloseObserver()
        => (g => _swipeGroup.AnyOpen ? BitConverter.SingleToInt32Bits(g.OffsetY) : 0L, _ => _swipeGroup.Close());

    float VerticalHeaderOpacity()
    {
        float h = VerticalHeaderHeight(subscribe: true);
        float offset = MathF.Max(0f, _verticalScrollOffset.Value);
        float start = MathF.Max(48f, h * 0.35f);
        float end = MathF.Max(start + 1f, h * 0.85f);
        if (offset <= start) return 1f;
        if (offset >= end) return 0f;
        return 1f - ((offset - start) / (end - start));
    }

    void MeasureVerticalHeader(RectF r)
    {
        if (r.H <= 1f) return;
        if (MathF.Abs(_verticalHeaderHeight.Peek() - r.H) <= 1f) return;
        _verticalHeaderHeight.Value = r.H;
        ApplyVerticalPin(_verticalScrollOffset.Peek());
    }

    Element VerticalHero()
    {
        // This method runs inside VerticalItemContent's OWN render computation. Read the live signal here as well as in
        // TrackList.Render so palette hydration invalidates the virtual hero slot itself, not only its retained parent.
        var h = _liveHandlers?.Value ?? _h;
        // The width subscription lands HERE (VerticalItemContent's hero-slot render), so a width change re-renders only
        // the hero slot — never the whole list. First frame uses the 540 fallback and corrects on the first bounds pass.
        // Composition is purely width-driven (stacks below 440); the page-layout preference lives in DetailShell.
        float availW = _verticalHeroW.Value;
        var orientation = DetailVerticalLayout.OrientationFor(availW);
        float artSize = DetailVerticalLayout.ArtworkFor(availW, orientation);
        Element header = new BoxEl
        {
            Key = "vhero:header",
            Direction = 1,
            OpacityGroup = true,
            Opacity = Prop.Of(VerticalHeaderOpacity),
            OnBoundsChanged = MeasureVerticalHeader,
            Children = [DetailVerticalHero.Build(_model, _cfg, h, _full, orientation, artSize, availW)],
        };
        return new BoxEl
        {
            Key = "vitem:hero", Direction = 1,
            Children = [header],
        };
    }

    // album / single: an outer scroller carries the eager rows + the trailing sections, under the fixed chrome. The
    // trailing component owns one aggregate route-keyed async resource so the below-list content fades in as one block.
    Element TrailingBody(Element listKeyed)
    {
        Element trailing = Embed.Comp(() => new AlbumTrailing(_full, _route, _h));
        var children = new List<Element>(3) { listKeyed };
        if (_verticalHeader && _cfg.Badges == BadgeStyle.TypeYear && AlbumTrailing.HasReleasePanel(_model))
            children.Add(AlbumTrailing.ReleasePanel(_model, _h));
        children.Add(trailing);

        return ScrollView(new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            AlignSelf = FlexAlign.Stretch,
            Children = children.ToArray(),
        }) with
        {
            Grow = 1f,
            OnScrollGeometryChanged = _verticalHeader ? VerticalScrollObserver() : SwipeCloseObserver(),
        };
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
    Element Chrome(ColumnSet set, TrackSize[] tracks, TrackSort sort, bool labeled, int tier, bool checkInset,
                   Element? lead = null, float padX = PadX, float? padRight = null, bool pinned = false) => new BoxEl
    {
        Key = "chrome", Direction = 1, Padding = new Edges4(padX, WaveeSpace.S, padRight ?? padX, 0f),
        Children = _showToolbar ? [Toolbar(labeled, tier, lead, pinned), Header(set, tracks, sort, checkInset)] : [Header(set, tracks, sort, checkInset)],
    };

    // The Spotify-style sticky-bar identity: artwork, then the accent Play circle, then the context title — the cover
    // leads so the cluster reads "this context" before "play it". Built from live render state (_model / _h) and fed
    // into the PINNED toolbar's left cluster. The title shrinks/ellipsizes so the whole cluster fits a ~350-DIP page
    // beside the search box.
    Element PinnedIdentity()
    {
        var kids = new List<Element>(4)
        {
            Surfaces.Artwork(_model.Cover, _model.ContextUri?.GetHashCode() ?? 0,
                             32f, 32f, WaveeRadius.Control),
            new BoxEl
            {
                Width = 32f, Height = 32f, Shrink = 0f, Corners = CornerRadius4.All(16f),
                Fill = _h.Accent, BrushTransitionMs = 420f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                OnClick = _h.PlayAll, HoverScale = 1.06f, PressScale = 0.94f,
                Cursor = CursorId.Hand, Role = AutomationRole.Button,
                Children = [Icon(Icons.Play, 13f, WaveePalette.OnAccent(_h.Accent))],
            },
            new TextEl(_model.Title)
            {
                Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                MaxWidth = 220f, Shrink = 1f, MinWidth = 0f,
            },
        };
        // The pinned bar keeps identity + search only; the heart drops from the sticky state (it stays in the in-list
        // chrome reached by scrolling up).
        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }

    Element Toolbar(bool labeled, int tier, Element? lead = null, bool pinned = false)
    {
        // Play / Shuffle: labeled (icon + text) at wide tiers, icon-only when the list narrows — the same collapse as the
        // view controls. Plain action buttons (no flyout) so they take the no-op NoAnchor.
        Element Cmd(string glyph, string label, Action onClick) => labeled
            ? ToolFx.LabeledButton(glyph, label, false, onClick, NoAnchor)
            : ToolFx.Button(glyph, false, onClick, NoAnchor);

        // The vertical (Apple Music) hero carries the prominent Play + Shuffle pills, so the list toolbar drops them (and
        // the leading separator) and keeps only the view controls: Sort · density · multi-select.
        var leftKids = new List<Element>(6);
        // Pinned bar only: a context-identity cluster (accent Play circle + title + heart) takes the visual slot the
        // vertical toolbar leaves empty (no Play/Shuffle), so the sticky bar reads like Spotify's — identity on the left.
        if (lead is not null)
        {
            leftKids.Add(lead);
            if (!pinned) leftKids.Add(ToolFx.Separator());   // nothing follows the identity in the pinned bar
        }
        if (!_verticalHeader)
        {
            leftKids.Add(Cmd(Icons.Play, Loc.Get(Strings.Detail.Play), _h.PlayAll));
            leftKids.Add(Cmd(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), _h.Shuffle));
            leftKids.Add(ToolFx.Separator());
        }
        // The pinned bar drops the view controls (Sort · density · multi-select) — they stay in the in-list chrome
        // reached by scrolling up (column-header click-to-sort keeps sorting reachable while pinned).
        if (!pinned)
        {
            // The Sort button opens the "sort by" flyout — the only way to sort by Artist (no column of its own).
            leftKids.Add(Embed.Comp(() => new SortMenuButton(_h.Sort, _h.SetSort, _cfg.ShowAlbumColumn, _hasDate, labeled)));
            leftKids.Add(Embed.Comp(() => new ListButton(_h.Density, _h.SetDensity, labeled)));
            if (_h.MultiSelect is not null && _h.SetMultiSelect is not null)
                leftKids.Add(Embed.Comp(() => new MultiSelectButton(_h.MultiSelect!, _h.SetMultiSelect!, _selection, labeled)));
        }
        var left = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 2f,
            Children = leftKids.ToArray(),
        };
        // The "Find" box, docked alone on the right — a plain search field (no suggestions flyout), two-way bound to the
        // live filter query (View() matches case-insensitively on title / artist / album). Its trailing affix is the
        // filter funnel, so the "advanced filter" (hide explicit / videos only) folds into the search box itself.
        Element search = Embed.Comp(() => new EditableText
        {
            Placeholder = Loc.Get(Strings.Detail.Filter.SearchThisList),
            // Tiered width so icon-bar (~200px) + search always fit the tier's minimum pane width (720/440/340).
            Width = labeled ? 220f : tier >= 4 ? 120f : 150f, Height = 32f,
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

    // The pinned pill's presence — a top-anchored slide+fade with a slight settle-scale, matching the floating
    // ArtistShyPill's entrance language now that the bar is a pill rather than edge-to-edge chrome.
    static readonly LayoutTransition PinnedChromePresence = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: -10f, Sx: 0.97f, Sy: 0.97f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -6f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.SmoothOut));

    static Element ToolBtn(string glyph) => new BoxEl
    {
        Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(WaveeRadius.Control),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => { /* TODO: search-in-list / sort / view (visual stubs in v1) */ },
        Children = [Icon(glyph, 14f, Tok.TextSecondary)],
    };

    Element Header(ColumnSet set, TrackSize[] tracks, TrackSort sort, bool checkInset)
    {
        var cells = new List<Element>(tracks.Length)
        {
            new BoxEl(),   // # column: no header label
        };
        if (set.Heart) cells.Add(new BoxEl());
        if (set.Thumb) cells.Add(new BoxEl());
        // Title / Song header: the standard Title→Artist SortLabel cycle everywhere (the artist rides the title subline,
        // so this header is the only column route to an artist sort). The `song` flag reads "Song"/"Artist" in the
        // vertical profile, "Title"/"Artist" elsewhere; SortMenuButton stays the always-available artist-sort route too.
        Element titleHeader = Embed.Comp(() => new SortLabel(_h.Sort, song: _verticalHeader));
        cells.Add(SortCell(titleHeader, SortColumn.Title, sort, FlexJustify.Start));
        if (set.Album) cells.Add(SortCell(HLabel(Loc.Get(Strings.Detail.Column.Album), SortColumn.Album, sort), SortColumn.Album, sort, FlexJustify.Start));
        if (set.By) cells.Add(PlainHeader(Loc.Get(Strings.Detail.Column.AddedBy)));
        if (set.Date) cells.Add(SortCell(HLabel(Loc.Get(Strings.Detail.Column.DateAdded), SortColumn.DateAdded, sort), SortColumn.DateAdded, sort, FlexJustify.Start));
        if (set.Video) cells.Add(new BoxEl());
        if (set.Plays) cells.Add(SortCell(HLabel(Loc.Get(Strings.Detail.Column.Plays), SortColumn.Plays, sort), SortColumn.Plays, sort, FlexJustify.End));
        // Duration header: a "Time" text label in the vertical (Apple Music) profile, the clock icon everywhere else.
        cells.Add(SortCell(_verticalHeader
                ? HLabel(Loc.Get(Strings.Detail.Column.Time), SortColumn.Duration, sort)
                : Icon(Icons.Clock, 14f, sort.Column == SortColumn.Duration ? Tok.TextSecondary : Tok.TextTertiary),
                           SortColumn.Duration, sort, FlexJustify.End));
        if (set.Actions) cells.Add(new BoxEl());   // trailing "..." overflow lane: no header label (keeps rows aligned)

        var grid = new GridEl
        {
            Columns = tracks, ColGap = TrackRow.ColGapFor(set.Tier), RowHeight = HeaderHeight,
            Children = cells.ToArray(),
        };
        var headerGrid = new BoxEl
        {
            Direction = 1, ClipToBounds = true,
            Padding = new Edges4(checkInset ? 28f : 0f, 0f, 0f, 0f),
            Animate = new LayoutTransition(TransitionChannels.Position,
                TransitionDynamics.Tween(333f, Easing.FluentDecelerate)),
            Children = [grid, new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }],
        };
        return headerGrid;
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
                              set, tracks, rowH, more: false);
        return new BoxEl { Direction = 1, Children = rows };
    }

    // ── bound row ────────────────────────────────────────────────────────────────────────────────────────
    // A bound row: ONE self-subscribing content component (re-renders on recycle/sort/now-playing, patching cells in
    // place — never a remount, so no flash) wrapped in the shape-stable bound selection skin.
    Element BoundRow(RowScope scope, IReadSignal<Track> item, ColumnSet set, TrackSize[] tracks, float rowH, int trackStart)
        => Embed.Comp(() => new BoundRowContent(this, scope, item, _rowsSnapshot!, set, tracks, rowH, trackStart));

    // ── Phase-D touch swipe-to-action for the VIRTUALIZED track rows (OFF by default) ────────────────────────────────
    // FLAGGED OFF: shipping the swipe layer on the eager queue/preview lists first. Before flipping this on, three things
    // need on-device verification (they don't affect the eager lists): (1) the ActionContext built here freezes at the
    // wrap — a recycled slot rebinds by index but the swipe actions capture the mount-time track, so the actions must be
    // made to read scope.Index (a lazy ctx) or the row re-wrapped per recycle; (2) close-on-scroll (the flat list does
    // not pass onScrollGeometryChanged — recycle+ResetKey covers scroll-OUT, but a small in-place scroll leaves an open
    // row open, unlike Spotify); (3) the ItemsView roving-focus toggles the slot ROOT's focusability imperatively — with
    // a SwipeControl now the root, that must still land on the focusable content. ResetKey (scope.Index) already
    // snap-closes on recycle. See app/Wavee/Components/RowSwipe.cs and the touch-design doc §4.2 / risks.
    static readonly bool RowSwipeOnVirtualizedRows = true;
    readonly SwipeGroup _swipeGroup = new();   // one single-open group per list instance (remounts with the keyed list)

    Element WrapRowSwipe(RowScope scope, Element row, int trackStart, IReadSignal<Track> item)
    {
        if (!RowSwipeOnVirtualizedRows || _acts is not { } acts) return row;
        ActionContext? Current()
        {
            var t = item.Peek();
            if (ReferenceEquals(t, EmptyTrack) || t.Id.Length == 0) return null;
            return new ActionContext(ActionTarget.ForTracks(new[] { t }), acts);
        }
        return RowSwipe.WrapBound(row, Current, group: _swipeGroup,
            leading: TrackActions.ToggleLike, trailing: TrackActions.AddToQueue, resetKey: scope.Index);
    }

    // The track shown at a display position, through the current (sort-subscribed) view. Reading _h.Sort.Value subscribes
    // the calling bind/render so a SORT change re-skins in place; the caller also reads the slot index signal so a
    // RECYCLE rebinds. Out-of-range (overscan past the view) → EmptyTrack.
    internal Track BoundTrackAt(int displayPos)
    {
        _ = _h.Sort.Value;
        var v = View();
        if ((uint)displayPos >= (uint)v.Length) return EmptyTrack;
        // Defense in depth: even if a stale view survives one frame after a shrink, the mapped ORIGINAL index must stay
        // within the CURRENT _tracks — never index past it (the R2 IndexOutOfRange). The identity guard in Render clears
        // the view on a track-set swap, so this is belt-and-suspenders for the one-frame window.
        int orig = v[displayPos];
        return (uint)orig < (uint)_tracks.Count ? _tracks[orig] : EmptyTrack;
    }

    // The title element: a Marquee whose text + now-playing colour are bound to the slot's INDEX signal, so it re-skins
    // on recycle / now-playing WITHOUT remounting. The marquee is a reused autonomous child of BoundRowContent — frozen
    // constructor args would stick on the first track, so its inputs MUST read the signal, not a captured value.
    Element BoundTitle(IReadSignal<Track> item) => Marquee.Of(
        Prop.Of(() => item.Value.Title),
        new Marquee.Style
        {
            FontSize = 14f, Weight = 600,
            Foreground = Prop.Of(() =>
                _bridge is not null && _bridge.CurrentTrack.Value?.Id == item.Value.Id
                    ? Tok.AccentTextPrimary : Tok.TextPrimary),
        });

    // PERF: the marquee is 2 nested components + a measure→re-render cycle + a perpetual TranslateX track PER ROW — on a
    // 12-row cold mount that was ~24 of ~60 components and the dominant slice of the flush spike (and every one re-rendered
    // by RethemeAll on a theme flip). A non-now-playing title never needs to scroll (Spotify only scrolls the now-playing
    // row), so render it as a plain, bound, ellipsis TextEl — ONE node, no extra component, no measure cycle, no animation.
    // Recycle-safe: a recycled row is non-playing → stays plain (no type swap); only the single now-playing row uses the
    // marquee, and BoundRowContent re-renders (swapping plain↔marquee for just that row) when now-playing changes.
    Element BoundTitlePlain(IReadSignal<Track> item) => new TextEl(Prop.Of(() => item.Value.Title))
    {
        Size = 14f, Weight = 600,
        Color = Prop.Of(() =>
            _bridge is not null && _bridge.CurrentTrack.Value?.Id == item.Value.Id
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
        readonly IReadSignal<Track> _item;
        readonly IReadSignal<TrackRowsSnapshot> _state;
        readonly ColumnSet _set;
        readonly TrackSize[] _tracks;
        readonly float _rowH;
        readonly int _trackStart;
        public BoundRowContent(TrackList o, RowScope scope, IReadSignal<Track> item, IReadSignal<TrackRowsSnapshot> state,
                               ColumnSet set, TrackSize[] tracks, float rowH, int trackStart)
        { _o = o; _scope = scope; _item = item; _state = state; _set = set; _tracks = tracks; _rowH = rowH; _trackStart = trackStart; }

        public override Element Render()
        {
            var likePrev = UseRef(((string?)null, false));               // hook FIRST (stable order) — per-slot like-edge memory
            _ = _o._full.Value.Value;                                    // subscribe → re-skin when the track set / context model changes (playlist nav, live /diff)
            int i = _scope.Index.Value;                                  // recycle → re-render
            int displayIndex = i - _trackStart;
            var rowState = _state.Value;
            var t = _item.Value;
            bool isTop = rowState.Config.ShowPlays && rowState.TopTrackId is not null && t.Id == rowState.TopTrackId;
            var st = TrackRow.StateOf(_o._bridge, _o._lib, t, isTop, _o._play?.IsRunning(t.Id) ?? false);
            // Buffering = this track's PlayAsync command is in flight (the Task-driven start spinner), OR the now-playing
            // track is mid-playback re-buffering (the bridge signal). Reading _play.IsRunning subscribes this row so the
            // spinner appears/clears as the command starts/finishes.
            bool likePop = TrackRow.LikeEdge(likePrev, t.Uri, st.Saved);   // pop only on the SAME-uri unsaved→saved edge

            // Marquee only for the now-playing row; every other row is a cheap plain ellipsis title (see BoundTitlePlain).
            Element title = st.IsNow && !rowState.MarqueeDisabled
                ? _o.BoundTitle(_item)
                : _o.BoundTitlePlain(_item);
            return _o.RowGrid(t, displayIndex, st.IsNow, st.IsPlaying, st.IsBuffering, st.IsTop, title, _set, _tracks, _rowH,
                              onPlay: () => _o.PlayRow(displayIndex),
                              saved: st.Saved, onLike: t.Uri.Length > 0 ? (Action)(() =>
                              {
                                  var current = _item.Peek();
                                  if (current.Uri.Length > 0) _o._lib?.ToggleSaved(current.Uri, current.Title);
                              }) : null,
                              likePop: likePop, rowState: rowState);
        }
    }

    sealed class VerticalItemContent : Component
    {
        readonly TrackList _o;
        readonly RowScope _scope;
        readonly IReadSignal<Track> _item;
        readonly ColumnSet _set;
        readonly TrackSize[] _tracks;
        readonly float _rowH;
        readonly bool _labeled;
        readonly int _tier;
        readonly bool _entrance;

        public VerticalItemContent(TrackList o, RowScope scope, ColumnSet set, TrackSize[] tracks, float rowH, bool labeled, int tier, bool entrance)
        { _o = o; _scope = scope; _item = o._rowItems!.BindItem(scope.Index, VerticalTrackStart); _set = set; _tracks = tracks; _rowH = rowH; _labeled = labeled; _tier = tier; _entrance = entrance; }

        public override Element Render()
        {
            _ = _o._full.Value.Value;   // same subscription as BoundRowContent — vertical slots must not show a prior context's rows
            int i = _scope.Index.Value;
            Element child;
            if (i == VerticalHeroIndex)
            {
                child = _o.VerticalHero();
            }
            else if (i == VerticalChromeIndex)
            {
                child = _o.Chrome(_set, _tracks, _o._h.Sort.Value, _labeled, _tier, _o._checksVisible?.Value ?? false) with { Key = "vitem:chrome" };
            }
            else
            {
                int displayIndex = i - VerticalTrackStart;
                int visible = _o._rowItems!.Count.Value;
                child = displayIndex >= 0 && displayIndex < visible
                    ? _o.WrapRowSwipe(_scope,
                        _o.BoundRowSkin(_scope, _o.BoundRow(_scope, _item, _set, _tracks, _rowH, VerticalTrackStart), _rowH, _entrance, VerticalTrackStart),
                        VerticalTrackStart, _item) with { Key = "vitem:row" }
                    : new BoxEl { Key = "vitem:empty", MinHeight = 160f, Direction = 1, Children = [FilterEmpty(_o._tracks.Count == 0)] };
            }

            return new BoxEl
            {
                Direction = 1,
                Children = [child],
            };
        }
    }

    // ── "Recommended songs" — the appended header + rec rows (playlist extender) ──────────────────────────────────────
    // The bound-slot content when recommendations are ON: ONE bound list carries the track rows, then the "Recommended"
    // header, then the recommendation rows — branching on the recycled slot index (mirrors VerticalItemContent). Track
    // rows use the normal bound selection skin (multi-select); the header + rec rows render their OWN content, so they
    // never join the track selection (exactly like the vertical hero/chrome rows).
    sealed class RowOrRecContent : Component
    {
        readonly TrackList _o;
        readonly RowScope _scope;
        readonly IReadSignal<Track> _item;
        readonly ColumnSet _set;
        readonly TrackSize[] _tracks;
        readonly float _rowH;
        readonly bool _entrance;
        public RowOrRecContent(TrackList o, RowScope scope, ColumnSet set, TrackSize[] tracks, float rowH, bool entrance)
        { _o = o; _scope = scope; _item = o._rowItems!.BindItem(scope.Index); _set = set; _tracks = tracks; _rowH = rowH; _entrance = entrance; }

        public override Element Render()
        {
            _ = _o._full.Value.Value;              // re-skin on context / track-set change (same subscription as BoundRowContent)
            int i = _scope.Index.Value;            // recycle → re-render
            int visible = _o._rowItems!.Count.Value;
            Element child;
            if (i < visible)
                child = _o.WrapRowSwipe(_scope,
                    _o.BoundRowSkin(_scope, _o.BoundRow(_scope, _item, _set, _tracks, _rowH, 0), _rowH, _entrance, 0),
                    0, _item) with { Key = "rec:track" };
            else if (i == visible)
                child = Embed.Comp(() => new RecHeader(_o, _rowH)) with { Key = "rec:header" };
            else
            {
                int k = i - visible - 1;
                var recs = _o._recs.Value;         // subscribe → rec rows re-render when the batch changes
                child = k >= 0 && k < recs.Count
                    ? _o.RecRow(recs[k], _rowH)    // keyed by track id inside RecRow → a recycled slot remounts for the new track
                    : new BoxEl { Key = "rec:empty" };
            }
            return new BoxEl { Direction = 1, Children = [child] };
        }
    }

    // The always-present "Recommended songs" header row (the first appended slot). Its MOUNT is the lazy first-fetch
    // trigger — it realizes only once the user scrolls to the bottom — and it hosts the Refresh control plus the
    // loading spinner / empty note. Self-subscribing so those track the fetch state without re-rendering the whole list.
    sealed class RecHeader : Component
    {
        readonly TrackList _o;
        readonly float _rowH;
        public RecHeader(TrackList o, float rowH) { _o = o; _rowH = rowH; }

        public override Element Render()
        {
            var svc = UseContext(Services.Slot);
            var post = UsePost();
            int state = _o._recState.Value;        // subscribe → spinner ↔ refresh, empty note
            int count = _o._recs.Value.Count;      // subscribe → "no suggestions" only once loaded-empty

            // Lazy first fetch when THIS header realizes (scrolled to bottom). Constant dep ⇒ runs once per mount;
            // FetchRecs(force:false) no-ops unless idle, so a recycle remount never re-fetches.
            UseEffect(() =>
            {
                if (svc?.RealExtender is not null && _o._model.ContextUri is { Length: > 0 } uri)
                    _o.FetchRecs(svc, post, uri, force: false);
            }, "rec-header-once");

            void Refresh()
            {
                if (svc?.RealExtender is not null && _o._model.ContextUri is { Length: > 0 } uri)
                    _o.FetchRecs(svc, post, uri, force: true);
            }

            var trailing = new List<Element>(2);
            if (state == 2 && count == 0)
                trailing.Add(new TextEl("No suggestions right now") { Size = 12f, Color = Tok.TextTertiary });
            trailing.Add(state == 1
                ? new BoxEl { Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [TrackRow.Spinner()] }
                : RefreshButton(Refresh));

            return new BoxEl
            {
                Key = "rec:header-row",
                Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, MinHeight = _rowH,
                Padding = new Edges4(TrackRow.PadX, 0f, TrackRow.PadX, 0f),
                Children =
                [
                    new TextEl("Recommended songs")
                    {
                        Size = 15f, Weight = 700, Color = Tok.TextPrimary,
                        Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    .. trailing,
                ],
            };
        }

        static Element RefreshButton(Action onClick) => new BoxEl
        {
            Width = 32f, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(16f),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = 1.06f, PressScale = 0.94f, Cursor = CursorId.Hand,
            Role = AutomationRole.Button, OnClick = onClick,
            Children = [Icon(Icons.Refresh, 14f, Tok.TextSecondary)],
        };
    }

    // One recommendation row: the shared art-forward ArtCard with a "+" Add button. Keyed by track id so a recycled slot
    // remounts cleanly for the new track (the NowPlayingOverlay inside ArtCard freezes its uri at mount). Renders its OWN
    // content (no bound selection skin) → it never joins the track multi-select. Right-click / long-press / the "…"
    // cell open the SINGLE-track menu (rec rows carry the full Track — the eager TrackAttach shape, search-"Songs" precedent).
    Element RecRow(Track t, float rowH) => new BoxEl
    {
        Key = "rec:" + t.Id,
        // Pin to the uniform stride (RepeatLayout.Stack): the ArtCard's own min-height (~52) would otherwise overflow the
        // 48px default row and overlap its neighbour — cap + clip keeps the 40px art (centred) fully visible.
        Direction = 0, AlignItems = FlexAlign.Center, MinHeight = rowH, MaxHeight = rowH, ClipToBounds = true,
        Padding = new Edges4(TrackRow.PadX - TrackRow.RowInset, 0f, TrackRow.PadX - TrackRow.RowInset, 0f),
        Children =
        [
            TrackRow.ArtCard(
                t, TrackRow.StateOf(_bridge, _lib, t), RecColumns, _h.Go,
                onPlay: () => { _ = _bridge?.Player.PlayAsync(t.Uri, 0); },
                art: 40f, showArtists: true, explicitBadge: true, showDuration: true,
                onAdd: () => AddRec(t),
                showMore: _acts is not null && _menuOverlay is not null),
        ],
    }.WithMenu(_menuOverlay is { } ov ? Menus.TrackAttach(_acts, ov, t) : null);

    // Fetch a fresh, non-repeating batch. force:false = the lazy trigger (fires only from idle); force:true = Refresh /
    // auto-refill. The skip set carries every id ever shown, so the server never repeats. Marshalled back to the UI thread.
    void FetchRecs(Services svc, Action<Action> post, string uri, bool force)
    {
        if (svc.RealExtender is not { } extender) return;
        if (_recState.Peek() == 1) return;                     // already loading
        if (!force && _recState.Peek() != 0) return;           // the lazy trigger fires only once (idle → loading)
        _recState.Value = 1;
        string[] skip = _recShown.Count == 0 ? Array.Empty<string>() : new string[_recShown.Count];
        if (skip.Length > 0) _recShown.CopyTo(skip);
        var ct = _recCts.Token;
        _ = Run();

        async System.Threading.Tasks.Task Run()
        {
            IReadOnlyList<Track> batch;
            try { batch = await extender.ExtendAsync(uri, skip, RecBatch, ct).ConfigureAwait(false); }
            catch { batch = Array.Empty<Track>(); }
            post(() =>
            {
                for (int i = 0; i < batch.Count; i++) { var id = batch[i].Id; if (id.Length > 0) _recShown.Add(id); }
                _recs.Value = batch;
                _recState.Value = 2;
            });
        }
    }

    // Add a recommendation to THIS playlist (reuses LibraryBridge.AddTracksAsync → PlaylistOpKind.Add). Keep the card
    // until the serialized /changes write is confirmed: the old fire-and-forget path removed it and showed success before
    // the sync loop had even attempted the server write.
    void AddRec(Track t)
    {
        if (_lib is not { } lib || _post is not { } post || _model.ContextUri is not { Length: > 0 } uri) return;
        string key = t.Uri.Length > 0 ? t.Uri : t.Id;
        if (!_recAdding.Add(key)) return;
        _ = Run();

        async System.Threading.Tasks.Task Run()
        {
            try { await lib.AddTracksAsync(uri, new[] { t }, _recCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                post(() => { _recAdding.Remove(key); PlaylistEditErrors.Toast(ex); });
                return;
            }

            post(() =>
            {
                _recAdding.Remove(key);
                if (t.Id.Length > 0) _recShown.Add(t.Id);
                var cur = _recs.Peek();
                var next = new List<Track>(Math.Max(0, cur.Count - 1));
                for (int i = 0; i < cur.Count; i++) { var o = cur[i]; if (!ReferenceEquals(o, t) && o.Id != t.Id) next.Add(o); }
                _recs.Value = next;
                Toasts.Show(Strings.Detail.AddedToPlaylist(_model.Title), ToastSeverity.Success);
                if (next.Count == 0 && _svc is not null && _post is not null)
                    FetchRecs(_svc, _post, uri, force: true);
            });
        }
    }

    // The # cell's play affordance: single-click PLAYS this track, or PAUSES/RESUMES it when it is already the
    // now-playing one (the PlayerBar.PrimaryClick toggle: optimistic IsPlaying write, then the player command). Called
    // from a click handler (not a reactive context), so the .Peek() reads here never subscribe.
    void PlayRow(int displayPos)
    {
        if (_rowsSnapshot is not { } source) return;
        var snapshot = source.Peek();
        var v = View(snapshot);
        if ((uint)displayPos >= (uint)v.Length) return;
        int orig = v[displayPos];
        var track = snapshot.Model.Tracks[orig];
        TrackRow.Invoke(_bridge, track, () => StartVisible(displayPos, snapshot));
    }

    // Start THIS context at the given display row, sending the VISIBLE (sorted/filtered) order so a remote player mirrors
    // the screen (PlayOrderedAsync). Collections stay URI-only on the wire (sort rides on context.url, server-side) — but
    // still identity-first: the clicked track's uri+uid is carried, the index is only a diagnostic fallback (§7.3), so a
    // divergent server order can never land on an unrelated row (the collection "always plays the first song" regression).
    // The keyed async command drives the row's #-cell buffer spinner. Empty view (still loading / filtered out) → context top.
    void StartVisible(int displayPos)
    {
        if (_rowsSnapshot is { } source) StartVisible(displayPos, source.Peek());
    }

    void StartVisible(int displayPos, TrackRowsSnapshot snapshot)
    {
        var v = View(snapshot);
        var model = snapshot.Model;
        var handlers = snapshot.Handlers;
        var tracks = model.Tracks;
        if (v.Length == 0) { handlers.Play(0); return; }
        if ((uint)displayPos >= (uint)v.Length) displayPos = 0;
        int orig = v[displayPos];
        var track = tracks[orig];
        if (_bridge is not null && _play is not null && model.ContextUri is { } uri)
        {
            // Collection OR natural order → identity-first skip (uri+uid; index diagnostic only), URI-only on the wire (no
            // embedded pages). Only a SORTED/FILTERED non-collection view embeds the visible order (the correct shape there).
            if (Wavee.Backend.ContextResolve.IsCollection(uri) || IsNaturalContextOrder(snapshot, v))
                _play.Run(track.Id, ct => _bridge.Player.PlayContextTrackAsync(
                    uri, new PlaybackContextTrack(track.Uri, track.ContextUid ?? string.Empty), orig, ct));
            else
            {
                var ordered = VisibleOrder(tracks, v);
                _play.Run(track.Id, ct => _bridge.Player.PlayOrderedAsync(uri, ordered, displayPos, ct));
            }
        }
        else
            handlers.Play(orig);
    }

    // The visible order as (uri, contextUid) pairs — the embedded page the outbound play command carries. ContextUid is
    // the per-row playlist-membership uid (skip_to-by-uid); "" outside a user playlist.
    static PlaybackContextTrack[] VisibleOrder(IReadOnlyList<Track> tracks, int[] v)
    {
        var ordered = new PlaybackContextTrack[v.Length];
        for (int k = 0; k < v.Length; k++) { var t = tracks[v[k]]; ordered[k] = new PlaybackContextTrack(t.Uri, t.ContextUid ?? string.Empty); }
        return ordered;
    }

    static bool IsNaturalContextOrder(TrackRowsSnapshot snapshot, int[] v)
    {
        if (snapshot.Sort != TrackSort.Default) return false;
        if (snapshot.Query.Length != 0 || snapshot.Flags != TrackFilterFlags.None) return false;
        if (v.Length != snapshot.Model.Tracks.Count) return false;
        for (int i = 0; i < v.Length; i++)
            if (v[i] != i) return false;
        return true;
    }

    // ── row grid ─────────────────────────────────────────────────────────────────────────────────────────
    // The row cell is the shared TrackRow.Grid (Components/TrackRow.cs) — ONE definition rendered identically by the
    // detail list, the library pane, artist "Popular" and search. This threads the detail list's per-row state + the
    // column set + the navigation handler through; the bound title element (plain vs marquee) is decided by the caller
    // (BoundRowContent), and the skeleton passes a static title. Plain/diffable → a BoundRowContent re-render patches in place.
    Element RowGrid(Track t, int displayIndex, bool isNow, bool isPlaying, bool isBuffering, bool isTop, Element title,
                    ColumnSet set, TrackSize[] tracks, float rowH, Action? onPlay = null, bool saved = false, Action? onLike = null,
                    bool likePop = false, bool more = true, TrackRowsSnapshot? rowState = null)
    {
        var snapshot = rowState ?? _rowsSnapshot!.Peek();
        return TrackRow.Grid(t, displayIndex, new TrackRow.State(isNow, isPlaying, isBuffering, isTop, saved),
                         set, tracks, rowH, title, snapshot.Config.ShowTrackArtist, snapshot.Handlers.Go,
                         onPlay, onLike, AddedByProfile(snapshot.Model, t), likePop,
                         // The trailing "…" — ClickRequestsContext opens the row's own context menu anchored at the
                         // button (input-a11y §6.5.1). Disabled for the shimmer rows: a skeleton keeps the identical
                         // reserved lane but stays non-interactive and hidden.
                         // The ultra-compact tier drops the "…" lane (set.Actions false) → no button built.
                         actionsCell: set.Actions ? TrackRow.MoreButton(more) : null);
    }

    static Owner? AddedByProfile(DetailModel model, Track t)
    {
        if (t.AddedBy is not { Length: > 0 } raw || model.UserProfilesById is not { Count: > 0 } profiles) return null;
        if (profiles.TryGetValue(raw, out var owner)) return owner;
        var canonical = UserProfileIds.Normalize(raw);
        return canonical is not null && profiles.TryGetValue(canonical, out owner) ? owner : null;
    }

    // The custom row container, BOUND + shape-stable so it recycles without remounting: the zebra REST fill AND the
    // hover/press fills are bound to the slot index (recycle-correct + theme-reactive via RethemeAll re-firing binds),
    // and the left accent pill is an ALWAYS-PRESENT child revealed by a BOUND opacity on scope.IsSelected — so a
    // selection change is a compositor-only re-skin (no list re-render, no remount, no Enter replay → no flash).
    // Border stays uniform; the bound hover/press restores the zebra-vs-flush hover-depth nuance.
    BoxEl BoundRowSkin(RowScope scope, Element content, float rowH, bool entrance, int trackStart)
    {
        var index = scope.Index;
        var isSel = scope.IsSelected;
        var onInteraction = scope.OnInteraction;
        var onFocusChanged = scope.OnFocusChanged;
        int DisplayIndex() => Math.Max(0, index.Value - trackStart);
        var skin = new BoxEl
        {
            ZStack = true, MinHeight = rowH, ClipToBounds = true,    // ZStack → the left accent bar overlays the content
            Margin = new Edges4(RowInset, 0f, RowInset, 0f),         // inset → the highlight reads as a rounded pill (#32)
            Corners = CornerRadius4.All(6f),
            // Reveal on slot MOUNT for navigation cold load + curated re-cut (reset epoch). Tier/density/filter remounts
            // skip the entrance — recycling reuses the slot (no mount), so this never replays on scroll or selection.
            Animate = entrance ? new LayoutTransition(TransitionChannels.Opacity,
                TransitionDynamics.Tween(280f, Easing.FluentDecelerate),
                Enter: new EnterExit(Opacity: 0f, Active: true)) : null,
            // Zebra REST fill bound to the slot index (recycle-correct), reading WaveeColors so RethemeAll recolours
            // it on a theme/palette switch (RowZebra chooses the theme-appropriate neutral overlay).
            // Selection does NOT change the fill — the left accent bar (below) is the ONLY selection cue.
            Fill = Prop.Of(() => DisplayIndex() % 2 != 0 ? WaveeColors.RowZebra : ColorF.Transparent),
            HoverFill = Prop.Of(() => DisplayIndex() % 2 != 0 ? WaveeColors.RowHoverZebra : WaveeColors.RowHover),
            PressedFill = Prop.Of(() => DisplayIndex() % 2 != 0 ? WaveeColors.RowPressedZebra : WaveeColors.RowPressed),
            PressScale = 0.985f,   // subtle push-down on press (a depth cue so the row isn't flat)

            BorderWidth = 1f,
            BorderColor = ColorF.Transparent,        // uniform (BorderColor is not bindable) → recycle-correct
            HoverBorderColor = Tok.StrokeCardDefault,
            FocusVisualMargin = Edges4.All(1f),
            Focusable = false,                       // the ItemsView roving effect owns the single tab stop
            Role = AutomationRole.Button,
            // Double-click invokes (plays), single click selects. DoubleTap is a POINTER trigger, so the ItemsView lands
            // focus + scrolls the row into view on it (EnterKey would skip that). While the check lane is visible a
            // plain tap/space TOGGLES the row into the selection (synthesized Ctrl) — WinUI multi-select semantics.
            OnPointerReleased = args =>
            {
                if (args.ClickCount >= 2) onInteraction(ItemContainerTrigger.DoubleTap, args.Mods);
                else onInteraction(ItemContainerTrigger.Tap, SelectorVisualsBound.MultiSelectMods(_checksVisibleRead(), args.Mods));
            },
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { onInteraction(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onInteraction(ItemContainerTrigger.SpaceKey, SelectorVisualsBound.MultiSelectMods(_checksVisibleRead(), args.Mods)); args.Handled = true; }
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
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center,
                    Animate = new LayoutTransition(TransitionChannels.Position,
                        TransitionDynamics.Tween(333f, Easing.FluentDecelerate)),
                    Children =
                    [
                        SelectorVisualsBound.BoundCheckLane(_checksVisibleRead, isSel, onInteraction, leftMargin: 4f),
                        content,
                    ],
                },
                new BoxEl
                {
                    Key = "row-pill", Width = 3f, Height = 16f, Margin = new Edges4(2f, 0f, 0f, 0f),
                    Corners = CornerRadius4.All(1.5f),
                    Fill = Prop.Of(() => _rowsSnapshot!.Value.Handlers.Accent), AlignSelf = FlexAlign.Center,
                    HitTestVisible = false, PressScale = 10f / 16f,
                    Opacity = Prop.Of(() => isSel() && !_checksVisibleRead() ? 1f : 0f),
                },
            ],
        };
        // Win11 context menu (right-click / Menu key / touch long-press): attached ONCE per recycled slot (chains, never
        // clobbers, the skin's press/key handlers); the factory runs lazily AT OPEN, reading the slot's live index + the
        // current selection (Explorer semantics in TrackContextMenu). Non-track slots (recommendation header/rows,
        // vertical hero/chrome, overscan) resolve no track → no menu. Covers the flat, recommendations and vertical
        // layouts at once — all three go through this skin.
        if (_acts is { } acts && _menuOverlay is { } menuSvc)
            return skin.WithContextMenu(menuSvc, () => TrackContextMenu.Build(
                acts, _selection, i => DisplayTrack(i, trackStart), index.Peek(), HostInfo,
                showGoToAlbum: _rowsSnapshot?.Peek().Config.ShowAlbumColumn ?? _cfg.ShowAlbumColumn));
        return skin;
    }

    // (The # cell's number↔play/pause transport, the now-playing equalizer, the buffer spinner, the per-row heart, the
    // artist/album links and the cell wrappers all live in the shared TrackRow now — see Components/TrackRow.cs.)
}

// The multi-select toggle: shows/hides row checkboxes; turning OFF also clears the current selection.
sealed class MultiSelectButton : Component
{
    readonly IReadSignal<bool> _mode;
    readonly Action<bool> _setMode;
    readonly SelectionModel _selection;
    readonly bool _labeled;
    public MultiSelectButton(IReadSignal<bool> mode, Action<bool> setMode, SelectionModel selection, bool labeled)
    { _mode = mode; _setMode = setMode; _selection = selection; _labeled = labeled; }

    public override Element Render()
    {
        bool on = _mode.Value;
        void Toggle()
        {
            bool next = !on;
            if (!next) _selection.ClearSelection();
            _setMode(next);
        }
        string label = Loc.Get(Strings.Detail.Select);
        return _labeled
            ? ToolFx.LabeledButton(Icons.MultiSelect, label, on, Toggle, static _ => { })
            : ToolTip.Wrap(ToolFx.Button(Icons.MultiSelect, on, Toggle, static _ => { }), label);
    }
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

// The Title header label: "Title" (or the vertical profile's "Song"), or "Artist" while the list is sorted by artist
// (the Title header owns the artist sort). A small rise + fade plays when the word flips (keyed on the text), so the
// Title↔Artist swap reads as a transition rather than a snap.
sealed class SortLabel : Component
{
    readonly IReadSignal<TrackSort> _sort;
    readonly bool _song;   // vertical (Apple Music) profile: the base label reads "Song" instead of "Title"
    public SortLabel(IReadSignal<TrackSort> sort, bool song = false) { _sort = sort; _song = song; }

    public override Element Render()
    {
        var col = _sort.Value.Column;
        string text = col == SortColumn.Artist ? Loc.Get(Strings.Detail.Column.Artist)
            : Loc.Get(_song ? Strings.Detail.Column.Song : Strings.Detail.Column.Title);
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
        var current = _sort.Value;   // subscribe: active field + direction survive into the pinned/shy projection
        bool active = current.Column != SortColumn.Index;

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
            ? ToolFx.LabeledButton(Icons.Sort,
                Label(current.Column),
                active, Toggle, h => anchor.Value = h,
                trailing: active ? Embed.Comp(() => new SortCaret(_sort)) : null)
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
    public static Element LabeledButton(string glyph, string label, bool active, Action onClick,
                                        Action<NodeHandle> onRealized, Element? trailing = null)
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
            Children = trailing is null
                ?
                [
                    Ui.Icon(glyph, 14f, active ? accent : Tok.TextSecondary),
                    new TextEl(label) { Size = 13f, Weight = 600, Color = active ? accent : Tok.TextSecondary },
                ]
                :
                [
                    Ui.Icon(glyph, 14f, active ? accent : Tok.TextSecondary),
                    new TextEl(label) { Size = 13f, Weight = 600, Color = active ? accent : Tok.TextSecondary },
                    trailing,
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
