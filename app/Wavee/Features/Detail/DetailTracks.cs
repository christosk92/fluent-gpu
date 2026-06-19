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
    const float RowHeight = 48f;            // density M
    const float HeaderHeight = 36f;
    const float ColGap = WaveeSpace.M;      // shared by header + rows (alignment invariant)
    const float PadX = WaveeSpace.L;        // shared horizontal inset (header chrome padding == row grid padding)
    const float RowInset = WaveeSpace.S;    // rounded row-highlight inset (rows pad PadX−RowInset so columns stay header-aligned)
    const float ThumbSize = 36f;

    readonly DetailModel _m;
    readonly DetailConfig _cfg;
    readonly PlaybackBridge? _bridge;
    readonly DetailHandlers _h;                                // carries the per-context sort signal + SetSort (set by DetailShell)
    readonly bool _showToolbar;                                // false in the vertical layout (the header owns the toolbar)
    readonly bool _hasDate;                                    // any track carries an AddedAt → the Date-added column exists
    readonly bool _hasBy;                                      // collaborative (≥2 contributors) → the Added-by column exists
    readonly Signal<int> _tier = new(0);                       // width tier (0 = widest/full), written by OnBoundsChanged
    readonly SelectionModel _selection = new();                // external → survives a tier remount
    readonly Dictionary<int, TrackSize[]> _tracksByTier = new();
    (TrackSort Sort, string Query, TrackFilterFlags Flags) _viewKey = (new((SortColumn)(-1), false), "\0", TrackFilterFlags.None);   // invalid sentinel
    int[] _view = Array.Empty<int>();                          // filtered + sorted → original track-index map (rows read via this)
    readonly string? _topTrackId;                              // album surfaces: the most-played track's id (gets a star); null with no play data

    public TrackList(DetailModel m, DetailConfig cfg, PlaybackBridge? bridge, DetailHandlers h, bool showToolbar = true)
    { _m = m; _cfg = cfg; _bridge = bridge; _h = h; _showToolbar = showToolbar; _hasDate = m.HasDateAdded; _hasBy = m.HasAddedBy; _topTrackId = TopTrack(m.Tracks); }

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
            var tracks = _m.Tracks;
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

    // Track row height by density (0 Compact · 1 Default · 2 Cozy · 3 Comfortable).
    static float RowHeightFor(int density) => density switch { 0 => 40f, 2 => 56f, 3 => 64f, _ => RowHeight };

    const string TopStar = "";   // FavoriteStarFill - the album top-track star

    // The most-played track's id (gets the star), or null when there's no play data — so the star stays album-only.
    static string? TopTrack(IReadOnlyList<Track> tracks)
    {
        int best = -1;
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].PlayCount > 0 && (best < 0 || tracks[i].PlayCount > tracks[best].PlayCount)) best = i;
        return best >= 0 ? tracks[best].Id : null;
    }

    // Stream count → "11.8M" / "654.8K".
    static string PlaysLabel(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000f:0.#}M" : n >= 1_000 ? $"{n / 1_000f:0.#}K" : n.ToString("N0");

    // Shown in place of the list when there's nothing to show — an empty playlist, or a filter that matched nothing.
    static Element FilterEmpty(bool noTracks) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XXL, WaveeSpace.L, WaveeSpace.XXL),
        Children = [new TextEl(noTracks ? Loc.Get(Strings.Detail.Empty.NoTracks) : Loc.Get(Strings.Detail.Empty.NoMatch)) { Size = 14f, Color = Tok.TextTertiary }],
    };

    // Which optional columns are present at a tier. #, Title and Duration are always present. Cell build order (and the
    // matching track widths) is: # · Title(+thumb) · Album · AddedBy · DateAdded · Video · Plays · ♥ · Duration.
    readonly record struct ColumnSet(bool Album, bool By, bool Date, bool Video, bool Plays, bool Heart, bool Thumb);

    // Drop order as the area narrows (most expendable first): Added-by (≥1) / Video (≥2) → Album (≥2) → Plays/Date (≥3)
    // → ♥ (≥4) → art-thumb (≥5). Video/Plays exist only on album surfaces (ShowPlays); Album/By/Date only on playlists.
    ColumnSet SetFor(int tier) => new(
        Album: _cfg.ShowAlbumColumn && tier < 2,
        By: _hasBy && tier < 1,
        Date: _hasDate && tier < 3,
        Video: _cfg.ShowPlays && _m.HasVideo && tier < 2,
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
        if (s.Thumb) t.Add(TrackSize.Px(ThumbSize));   // dedicated art column: the Title header aligns over the title text, not the art
        t.Add(TrackSize.Star());
        if (s.Album) t.Add(TrackSize.Px(180f));
        if (s.By) t.Add(TrackSize.Px(132f));
        if (s.Date) t.Add(TrackSize.Px(108f));
        if (s.Video) t.Add(TrackSize.Px(28f));
        if (s.Plays) t.Add(TrackSize.Px(84f));
        if (s.Heart) t.Add(TrackSize.Px(40f));
        t.Add(TrackSize.Px(64f));
        var arr = t.ToArray();
        _tracksByTier[tier] = arr;
        return arr;
    }

    public override Element Render()
    {
        int tier = _tier.Value;                  // subscribe → re-render (new header + remount list) on a breakpoint cross
        var set = SetFor(tier);
        var tracks = TracksFor(tier);
        var items = _m.Tracks;
        var sort = _h.Sort.Value;                // subscribe → re-render (header carets) on sort change
        int density = _h.Density.Value;          // subscribe → remount with the new row height on density change
        string query = _h.Query.Value;           // subscribe → remount with the filtered set on query change
        var flags = _h.Flags.Value;              // subscribe → remount on a quick-filter toggle
        float rowH = RowHeightFor(density);

        // Re-skin epoch: now-playing/buffering/play-state AND the sort (NOT the tier — the tier remounts via the keyed
        // wrapper). A bump re-realizes the visible rows IN PLACE, so a track change recolours and a sort change REORDERS
        // them, both keeping the scroll offset. Rows read the order/now-playing via .Peek() (recyclable, no per-row bind).
        var nowEpoch = UseComputed(() => HashCode.Combine(
            _bridge?.CurrentTrack.Value?.Id, _bridge?.IsBuffering.Value ?? false, _bridge?.IsPlaying.Value ?? false,
            _h.Sort.Value));

        Element chrome = new BoxEl
        {
            Key = "chrome", Direction = 1, Padding = new Edges4(PadX, WaveeSpace.S, PadX, 0f),
            // Vertical layout: the header already shows the toolbar (right of Play), so the chrome is just the columns.
            Children = _showToolbar ? [Toolbar(), Header(set, tracks, sort)] : [Header(set, tracks, sort)],
        };

        ItemContainerFactory rowSkin = (i, content, st, oi, of) => RowSkin(i, content, st, oi, of, rowH);
        Element list = View().Length == 0
            ? FilterEmpty(items.Count == 0)       // empty playlist, or a filter that matched nothing
            : ItemsView.Create(
                // View()[displayPos] = original track index → rows render filtered+sorted; play / key map back to it.
                View().Length, i => Row(items[View()[i]], i, set, tracks, _bridge, rowH), RepeatLayout.Stack(rowH),
                selectionMode: _cfg.Selection,
                selection: _selection,                // external → selection survives the tier remount
                isItemInvokedEnabled: true,
                itemInvoked: i => _h.Play(View()[i]),   // DoubleTap / Enter → play this track (original context order)
                containerFactory: rowSkin,
                keyOf: i => items[View()[i]].Id,
                displacementVersion: nowEpoch,        // now-playing / buffering / sort re-skin (ItemDisplacement null → no-op)
                grow: _cfg.HasTrailing ? 0f : 1f,
                // Alpha-mask edge fade: the page floats over a gradient wash (no opaque plate), so the surface-colour
                // EdgeCues fade self-skips — this feathers the rows' own alpha at the overflowing top/bottom instead.
                // Only when the ItemsView is itself the scroller (playlist/liked); the album path fades its outer scroll.
                autoEdgeFade: !_cfg.HasTrailing);

        // Key the list by tier + density + filter → any of those REMOUNTS it (a clean mount with the right column set /
        // row height / filtered window). Sort is NOT in the key — it re-skins in place via the epoch (scroll preserved).
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
        return displayIndex >= 0 && displayIndex < v.Length ? _m.Tracks[v[displayIndex]] : null;
    }

    // album / single: an outer scroller carries the (eager) rows + the trailing sections, under the fixed chrome.
    Element TrailingBody(Element listKeyed)
    {
        var trailing = DetailTrailing.Build(_m, _h);
        var body = new Element[1 + trailing.Length];
        body[0] = listKeyed;
        Array.Copy(trailing, 0, body, 1, trailing.Length);
        return ScrollView(new BoxEl { Direction = 1, Children = body }) with { Grow = 1f, AutoEdgeFade = true };
    }

    // ── chrome (fixed) ───────────────────────────────────────────────────────────────────────────────────
    // The list controls, right-aligned (filter · more · sort · density). No count/duration summary here — that lives in
    // the rail's meta line, so the strip doesn't duplicate it.
    Element Toolbar() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = WaveeSpace.XS,
        Margin = new Edges4(0f, 0f, 0f, WaveeSpace.XS),
        Children =
        [
            Embed.Comp(() => new FilterButton(_h.Query, _h.Flags, _h.SetFlags, _m.HasVideo)),
            Embed.Comp(() => new MoreButton(_m, _h)),
            // The Sort button opens the "sort by" flyout — the only way to sort by Artist (no column of its own).
            Embed.Comp(() => new SortMenuButton(_h.Sort, _h.SetSort, _cfg.ShowAlbumColumn, _hasDate)),
            Embed.Comp(() => new ListButton(_h.Density, _h.SetDensity)),
        ],
    };

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
        if (set.Thumb) cells.Add(new BoxEl());   // art column header (blank) — keeps the Title header aligned over the title text
        // The Title header owns a Title→Artist cycle; SortLabel reads "Artist" while artist-sorting and cross-fades the word on the flip.
        cells.Add(SortCell(Embed.Comp(() => new SortLabel(_h.Sort)), SortColumn.Title, sort, FlexJustify.Start));
        if (set.Album) cells.Add(SortCell(HLabel(Loc.Get(Strings.Detail.Column.Album), SortColumn.Album, sort), SortColumn.Album, sort, FlexJustify.Start));
        if (set.By) cells.Add(PlainHeader(Loc.Get(Strings.Detail.Column.AddedBy)));   // not sortable (matches the reference)
        if (set.Date) cells.Add(SortCell(HLabel(Loc.Get(Strings.Detail.Column.DateAdded), SortColumn.DateAdded, sort), SortColumn.DateAdded, sort, FlexJustify.Start));
        if (set.Video) cells.Add(new BoxEl());   // video header (blank — the per-row indicator stands alone)
        if (set.Plays) cells.Add(SortCell(HLabel(Loc.Get(Strings.Detail.Column.Plays), SortColumn.Plays, sort), SortColumn.Plays, sort, FlexJustify.End));
        if (set.Heart) cells.Add(new BoxEl());   // ♥ header (blank, not sortable)
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
    internal const string CaretGlyph = "";   // CaretSolidUp; SortCaret rotates it 180° for the descending state

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

    // ── row ──────────────────────────────────────────────────────────────────────────────────────────────
    Element Row(Track t, int index, ColumnSet set, TrackSize[] tracks, PlaybackBridge? bridge, float rowH)
    {
        bool isNow = bridge is not null && bridge.CurrentTrack.Peek()?.Id == t.Id;
        ColorF titleColor = isNow ? Tok.AccentTextPrimary : Tok.TextPrimary;
        float thumb = ThumbSize;   // fixed art size → a stable dedicated art column (see TracksFor)

        var cells = new List<Element>(tracks.Length);

        // # cell: now-playing glyph → top-track star → the track number. (Selection shows as the row's left accent bar.)
        bool isTop = _cfg.ShowPlays && _topTrackId is not null && t.Id == _topTrackId;
        cells.Add(CenterCell(isNow
            ? Icon(Icons.Play, 12f, Tok.AccentTextPrimary)
            : isTop
                ? Icon(TopStar, 11f, Tok.AccentTextPrimary)
                : new TextEl((index + 1).ToString()) { Size = 13f, Color = Tok.TextTertiary }));

        // Art thumb (playlist/liked) gets its OWN column before Title — so the "Title" header aligns over the title TEXT,
        // not the artwork (the WaveeMusic RowArtColDef pattern). Then the title + artist subline (subline hidden on
        // single-artist albums/singles/EPs).
        if (set.Thumb)
            cells.Add(CenterCell(Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, thumb, thumb, WaveeRadius.Control)));
        Element title = Marquee.Of(t.Title, new Marquee.Style { FontSize = 14f, Weight = 600, Foreground = titleColor });
        var titleCol = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
            Children = _cfg.ShowTrackArtist
                ? [title, Marquee.Of(DetailFormat.ArtistNames(t.Artists), new Marquee.Style { FontSize = 12f, Foreground = Tok.TextSecondary })]
                : [title],
        };
        cells.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = [titleCol] });

        if (set.Album)
            cells.Add(LeftCell(new TextEl(t.Album.Name) { Size = 13f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }));
        if (set.By)
            cells.Add(AddedByCell(t.AddedBy));
        if (set.Date)
            cells.Add(LeftCell(new TextEl(DetailFormat.DateAddedLabel(t.AddedAt)) { Size = 13f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }));
        if (set.Video)
            cells.Add(CenterCell(t.HasVideo ? Icon(Icons.Movie, 13f, Tok.TextTertiary) : new BoxEl()));
        if (set.Plays)
            cells.Add(EndCell(new TextEl(PlaysLabel(t.PlayCount)) { Size = 13f, Color = Tok.TextTertiary }));
        if (set.Heart)
            cells.Add(CenterCell(Heart(_cfg)));
        cells.Add(EndCell(new TextEl(DetailFormat.TrackTime(t.DurationMs)) { Size = 13f, Color = Tok.TextSecondary }));

        return new GridEl
        {
            Columns = tracks, ColGap = ColGap, RowHeight = rowH, Grow = 1f,   // fill the RowSkin lane (ZStack content layer)
            // Pad PadX − RowInset: with the RowSkin's RowInset margin, columns still start at PadX (header-aligned).
            Padding = new Edges4(PadX - RowInset, 0f, PadX - RowInset, 0f),
            Children = cells.ToArray(),
        };
    }

    // The Added-by cell: a small initial-avatar (we carry no avatar URL in the model yet) + the contributor name.
    static Element AddedByCell(string? by)
    {
        if (string.IsNullOrEmpty(by)) return new BoxEl();
        string initial = by.Substring(0, 1).ToUpperInvariant();
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = WaveeSpace.S,
            Children =
            [
                new BoxEl
                {
                    Width = 22f, Height = 22f, Shrink = 0f, Corners = CornerRadius4.All(11f), Fill = Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(initial) { Size = 11f, Weight = 600, Color = Tok.TextSecondary }],
                },
                new TextEl(by) { Size = 13f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    static Element Heart(DetailConfig cfg)
    {
        bool liked = cfg.Heart == HeartMode.None;   // the Liked-Songs context: every row is already liked
        return new BoxEl
        {
            Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(14f), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            OnClick = () => { /* TODO: ILibraryMutations like/save (no Core command yet) */ },
            Children = [Icon(Icons.Heart, 14f, liked ? Tok.AccentTextPrimary : Tok.TextTertiary)],
        };
    }

    // The custom row container: full-row hover/selection fill (Spotify-style), exact insets, interaction wiring.
    // Plain BoxEl (no binds/Component/OnRealized) → recyclable; matches the SelectorVisuals press/key contract.
    static BoxEl RowSkin(int index, Element content, ItemChromeState state,
                         Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged, float rowH)
    {
        bool odd = index % 2 != 0;   // even/odd zebra striping (WaveeMusic): odd rows get a subtle fill + card-stroke border
        return new()
        {
            ZStack = true, MinHeight = rowH, ClipToBounds = true,    // ZStack → the left accent bar overlays the content
            Margin = new Edges4(RowInset, 0f, RowInset, 0f),         // inset → the highlight reads as a rounded pill (#32)
            Corners = CornerRadius4.All(6f),
            // Zebra at rest (odd → a subtle fill) + hover feedback. Selection does NOT change the fill/border — the
            // left accent bar (below) is the ONLY selection cue.
            Fill = odd ? Tok.FillSubtleTertiary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            BorderWidth = 1f,
            BorderColor = odd ? Tok.StrokeCardDefault : ColorF.Transparent,
            HoverBorderColor = Tok.StrokeCardDefault,
            Opacity = state.IsEnabled ? 1f : 0.4f,
            IsEnabled = state.IsEnabled,
            Focusable = true,
            FocusVisualMargin = Edges4.All(1f),
            Role = AutomationRole.Button,
            OnPointerPressed = args => onInteraction(
                args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { onInteraction(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onInteraction(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = onFocusChanged,
            // Content lane (Grow fills the ZStack) + the WinUI ListView-style left accent selection bar (when selected).
            // The pill spring-grows (ScaleY 0→1) + fades in on select and shrinks on press — the WinUI ListViewItem
            // SelectionIndicator motion, identical to the engine's built-in SelectorVisuals.AccentPill (NavPill spring
            // 0.30/0.85; press shrink 10/16). Keyed so the reconciler tracks its enter across the selection re-skin.
            Children = state.IsSelected
                ? [new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Children = [content] },
                   new BoxEl
                   {
                       Key = "row-pill", Width = 3f, Height = 16f, Margin = new Edges4(2f, 0f, 0f, 0f),
                       Corners = CornerRadius4.All(1.5f), Fill = Tok.AccentDefault, AlignSelf = FlexAlign.Center,
                       HitTestVisible = false, PressScale = 10f / 16f,
                       Animate = new LayoutTransition(TransitionChannels.Opacity,
                           new TransitionDynamics(DynamicsKind.Spring, 0.30f, 0.85f),
                           Enter: new EnterExit(Sy: 0f, Opacity: 0f, Active: true)),
                   }]
                : [new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Children = [content] }],
        };
    }

    // ── cell wrappers (the cell fills its grid rect; these vertical-center + horizontally place the content) ──
    static Element CenterCell(Element content) =>
        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [content] };
    static Element LeftCell(Element content) =>
        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Children = [content] };
    static Element EndCell(Element content) =>
        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Children = [content] };
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
                ActionBtn(Icons.Play, Loc.Get(Strings.Detail.Play), () => { /* TODO: play the selection */ }),
                ActionBtn(Icons.List, Loc.Get(Strings.Detail.AddToQueue), () => { /* TODO: queue API */ }),
                ActionBtn(Icons.Add, Loc.Get(Strings.Detail.AddToPlaylist), () => { /* TODO: ILibraryMutations (no Core command yet) */ }),
                Divider(),
                ActionBtn(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), () => _sel.SelectAll()),
                RoundBtn(Icons.Cancel, () => _sel.DeselectAll()),   // Clear selection
            ],
        };
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
    readonly bool _hasAlbum, _hasDate;
    public SortMenuButton(IReadSignal<TrackSort> sort, Action<TrackSort> setSort, bool hasAlbum, bool hasDate)
    { _sort = sort; _setSort = setSort; _hasAlbum = hasAlbum; _hasDate = hasDate; }

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

        return ToolFx.Button(Icons.Sort, active, Toggle, h => anchor.Value = h);
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
            Corners = CornerRadius4.All(WaveeRadius.Control),
            Fill = active ? accent with { A = 0.16f } : ColorF.Transparent,
            HoverFill = active ? accent with { A = 0.24f } : Tok.FillSubtleSecondary,
            PressedFill = active ? accent with { A = 0.12f } : Tok.FillSubtleTertiary,
            OnClick = onClick, OnRealized = onRealized,
            Children = [Ui.Icon(glyph, 14f, active ? accent : Tok.TextSecondary)],
        };
    }

    public static PopupOptions Popup => new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false };
}

// The Filter button: opens a flyout with a live search box (filters the list by title / artist / album) plus an
// advanced toggle (hide explicit). The button shows the accent "on" state whenever a filter is set.
sealed class FilterButton : Component
{
    readonly Signal<string> _query;
    readonly IReadSignal<TrackFilterFlags> _flags;
    readonly Action<TrackFilterFlags> _setFlags;
    readonly bool _hasVideo;
    public FilterButton(Signal<string> query, IReadSignal<TrackFilterFlags> flags, Action<TrackFilterFlags> setFlags, bool hasVideo)
    { _query = query; _flags = flags; _setFlags = setFlags; _hasVideo = hasVideo; }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        bool active = !string.IsNullOrWhiteSpace(_query.Value) || _flags.Value != TrackFilterFlags.None;   // subscribe → accent when filtering

        // The body is its OWN component so the toggle rows track the LIVE flags — a static snapshot would let the
        // controlled ToggleSwitch revert to its stale value after a tap (the reported bug).
        Element Content() => Embed.Comp(() => new FilterPanel(_query, _flags, _setFlags, _hasVideo));

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, Content, FlyoutPlacement.BottomEdgeAlignedRight, ToolFx.Popup);
            handle.Value.ClosedAction = () => handle.Value = null;
        }
        return ToolFx.Button(Icons.Filter, active, Toggle, h => anchor.Value = h);
    }
}

// The filter flyout body: a search box (filters title/artist/album) over label-left / switch-right toggle rows + Clear.
// A Component so a flag change re-renders it and the controlled switches stay in sync (no revert).
sealed class FilterPanel : Component
{
    readonly Signal<string> _query;
    readonly IReadSignal<TrackFilterFlags> _flags;
    readonly Action<TrackFilterFlags> _setFlags;
    readonly bool _hasVideo;
    public FilterPanel(Signal<string> query, IReadSignal<TrackFilterFlags> flags, Action<TrackFilterFlags> setFlags, bool hasVideo)
    { _query = query; _flags = flags; _setFlags = setFlags; _hasVideo = hasVideo; }

    public override Element Render()
    {
        var flags = _flags.Value;   // subscribe → the switches reflect the live state
        bool Has(TrackFilterFlags f) => (flags & f) != 0;

        var rows = new List<Element>(6)
        {
            new TextEl(Loc.Get(Strings.Detail.Filter.Title)) { Size = 13f, Weight = 700, Color = Tok.TextPrimary },
            TextBox.Create(placeholder: Loc.Get(Strings.Detail.Filter.SearchThisList), width: 248f, text: _query),
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(0f, WaveeSpace.XS, 0f, WaveeSpace.XS) },
            ToggleRow(Loc.Get(Strings.Detail.Filter.HideExplicit), Has(TrackFilterFlags.HideExplicit), () => _setFlags(flags ^ TrackFilterFlags.HideExplicit)),
        };
        if (_hasVideo)
            rows.Add(ToggleRow(Loc.Get(Strings.Detail.Filter.VideosOnly), Has(TrackFilterFlags.VideosOnly), () => _setFlags(flags ^ TrackFilterFlags.VideosOnly)));
        rows.Add(new BoxEl
        {
            Direction = 0, Justify = FlexJustify.End, Margin = new Edges4(0f, WaveeSpace.XS, 0f, 0f),
            Children = [ClearButton(() => { _query.Value = ""; _setFlags(TrackFilterFlags.None); })],
        });

        return Layer(Edges4.All(WaveeSpace.M), new BoxEl { Direction = 1, Gap = WaveeSpace.S, MinWidth = 248f, Children = rows.ToArray() });
    }

    // A settings-style row: label left (grows), switch right — no stacked header, no wasted left gutter.
    static Element ToggleRow(string label, bool on, Action toggle) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, MinHeight = 32f,
        Children = [new TextEl(label) { Size = 13f, Color = Tok.TextPrimary, Grow = 1f }, ToggleSwitch.Create(on, toggle)],
    };

    static Element ClearButton(Action onClick) => new BoxEl
    {
        Padding = new Edges4(WaveeSpace.S, WaveeSpace.XS, WaveeSpace.S, WaveeSpace.XS),
        Corners = CornerRadius4.All(WaveeRadius.Control), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        OnClick = onClick, Children = [new TextEl(Loc.Get(Strings.Detail.Filter.Clear)) { Size = 13f, Color = Tok.AccentTextPrimary }],
    };
}

// The List button: opens a flyout with a stepped slider (Compact · Default · Cozy · Comfortable) controlling row height.
sealed class ListButton : Component
{
    readonly IReadSignal<int> _density;
    readonly Action<int> _setDensity;
    public ListButton(IReadSignal<int> density, Action<int> setDensity) { _density = density; _setDensity = setDensity; }

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
        return ToolFx.Button(Icons.List, false, Toggle, h => anchor.Value = h);
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

// The More (⋯) button: a MenuFlyout of list-level actions.
sealed class MoreButton : Component
{
    readonly DetailModel _m;
    readonly DetailHandlers _h;
    public MoreButton(DetailModel m, DetailHandlers h) { _m = m; _h = h; }

    IReadOnlyList<MenuFlyoutItem> Items() =>
    [
        new(Loc.Get(Strings.Detail.Play), Icons.Play, Invoke: () => _h.PlayAll()),
        new(Loc.Get(Strings.Detail.AddToQueue), Icons.Add, Invoke: () => { /* TODO: queue API */ }),
        MenuFlyoutItem.Separator,
        new(Loc.Get(Strings.Detail.GoToArtist), Enabled: _m.Artists.Count > 0,
            Invoke: () => { if (_m.Artists.Count > 0) _h.Go(_m.Artists[0].Uri, _m.Artists[0].Name); }),
        new(Loc.Get(Strings.Detail.Share), Icons.Share, Invoke: () => { /* TODO: share */ }),
    ];

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, () => MenuFlyout.Build(Items(), () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight, ToolFx.Popup);
            handle.Value.ClosedAction = () => handle.Value = null;
        }
        return ToolFx.Button(Icons.More, false, Toggle, h => anchor.Value = h);
    }
}
