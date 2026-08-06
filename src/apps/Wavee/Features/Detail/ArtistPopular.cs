using System;
using System.Collections.Generic;
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

// Artist "Top tracks" chart — the ops/scratch/popular-releases-prototype.html row, verbatim geometry:
// rank · 44px art (40px under pressure) · title+subline (E · feat. X +N · plays) · heart · duration,
// 56px rows, 12px gutters,
// ≤2 columns: a third column stole the width the mid column needs for title+feat+plays, and one column
// below ColBreakW of CHART COLUMN for the same reason (paging is the better trade there).
// Behavior stays canonical: the # cell is TrackRow.NumberCell
// (number↔play/pause hover transport + live equalizer), row click = TrackRow.Invoke (toggles pause on the
// now-playing track), TrackRow.Heart. Hard 5-row height.
//
// The chart is ONE PagedShelf collection — a 5-row virtual strip with page-mandatory snapping — NOT a page-swapped
// slice: a page flip slides the realized window, so a row that stays on screen keeps its identity and its state. That
// is what the keys encode: a realized cell is keyed by its TRACK URI and never by its ordinal (an ordinal in the key is
// what used to remount all five rows on every flip); only the density TIER rides along, because those props freeze at
// mount and a tier flip must be a deliberate remount. PagedShelf owns the page state, the snap interval, the glide and
// the pips — this file must not re-add a page signal, a pages clamp, or a scroll animation of its own.
//
// Row chrome is the prototype's `.row` verbatim and deliberately has NO zebra: transparent fill over a
// 1px TRANSPARENT border (a present-but-invisible stroke so hover never nudges layout), painting only on
// hover/press. The row body paints NO fill for the now-playing track either — that state is carried by content
// (the NumberCell equalizer + the accent title), never by a wash. This is also what SkeletonDeriver leaves
// standing (it strips Fill/Border/hover brushes), so the
// live chart and its own shimmer read identically. Do not "restore" the bands.
//
// The chart HUGS its rows: a flat 56px each, one gap apart, and the strip simply ends where the last row does. It does
// NOT stretch to meet the (usually taller) Releases column beside it. Both earlier attempts at using that leftover
// height failed the eye — growing the rows made chunky slabs, and distributing it as spacing (SpaceBetween) floated
// small rows in ~137px slots. A ragged band bottom is the correct answer; the rhythm is the shimmer's.
sealed class ArtistPopular : Component
{
    readonly IReadOnlyList<Track> _tracks;   // the overview seed, frozen at mount (component-props contract)
    readonly string _ctx, _title;
    readonly PlaybackBridge? _bridge;
    readonly Services _svc;
    readonly Func<ColorF> _accent;
    // The list actually being charted: the seed until the extended fetch lands, then the merged one. Render writes it
    // BEFORE building the row children, so the frozen-prop ChartRows read the current list at their own render.
    IReadOnlyList<Track> _live;
    // The same back-channel, for everything the SHELF's closures need. PagedShelf freezes cardAt/keyOf/customPager at
    // its own mount, so those closures must read these fields (written by Render, which always runs first) instead of
    // capturing a render's locals — otherwise a card realized on page 4 would be wired to the very first render's
    // context values, and the count label would report the seed count forever.
    Action<string, string?> _go = static (_, _) => { };
    LibraryBridge? _lib;
    ActionServices? _acts;
    IOverlayService? _overlay;
    int _total;

    public ArtistPopular(IReadOnlyList<Track> tracks, string ctx, PlaybackBridge? bridge, Services svc, string title, Func<ColorF> accent)
    {
        _tracks = tracks; _live = tracks; _ctx = ctx; _bridge = bridge; _svc = svc; _title = title; _accent = accent;
    }

    // The chart shows the FULL extended popular list (overview seed ∪ artist-top-tracks-extensions), not just the
    // overview's ten — the 5-row cap and the column pager already absorb the extra rows.
    const int MaxTracks = ArtistPopularTracks.ExtendedCap;
    const int SeedTracks = ArtistPopularTracks.OverviewSeedCap;   // the pre-extension skeleton can never exceed this
    const int MaxRows = 5;          // the band is NEVER taller than five rows — it pages instead
    const int MaxColumns = 2;       // two at most — the band pages instead of packing a third column
    const float RowH = 56f;
    // ONE gap, both axes: PagedShelf/FillRowVirtualLayout expose a single knob and it is the column stride AND the row
    // pitch. The prototype's horizontal 12px wins, so the row pitch is 12 rather than the pre-shelf 8.
    const float CellGap = 12f;      // prototype .chart gap: 2px 12px
    const float HeaderGap = 10f;    // header → first row, on screen (PagedShelf keeps a multi-row header gap verbatim)
    // Below this the band is ONE column. It is measured on the CHART COLUMN, which is ⅔ of the responsive band (TopBand
    // gives the chart Grow 2 against the releases column's Grow 1) — so the 720 here was reading as a ~1080 band and the
    // chart never got a second column at any window this app is used at. 540 of chart column ⇔ a ~810 band, which is the
    // prototype's two-column point.
    const float ColBreakW = 540f;
    // The shelf's auto-fit knob — and here it is a BREAKPOINT, not a card width: with maxColumns 2 and an uncapped
    // maxCardW the fitted card is always (w − Gap)/cols, so this only decides WHERE the second column appears
    // (floor((w+gap)/(min+gap)) ≥ 2 ⟺ w ≥ 2·min + gap). 264px columns are the narrowest that still seat
    // title + feat + plays in the mid cell (the 200/220/300/340 pressure tiers in Card below are what make that true).
    const float MinCardW = (ColBreakW - CellGap) / 2f;   // 264

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var lib = UseContext(LibraryBridge.Slot);
        var acts = UseContext(ActionServices.Slot);
        var menuOverlay = UseContext(Overlay.Service);

        // Step two of the chart, owned here rather than by the page: the overview seed paints immediately, and the
        // extended list (up to 50, play counts preserved on the head) revalidates into the SAME component — so the chart
        // simply grows instead of the whole band re-mounting. Offline / failure returns the seed, so this never blanks.
        var extended = UseResource(ct => _svc.ArtistPopularTracks.EnsureExtendedAsync(_ctx, _tracks, ct), _tracks, _ctx);
        _live = extended.Loadable.Value.Value is { Count: > 0 } merged ? merged : _tracks;

        int total = Math.Min(_live.Count, MaxTracks);
        // The back-channel the shelf's frozen closures read (see the field docs) — written BEFORE the shelf builds.
        _total = total; _go = go; _lib = lib; _acts = acts; _overlay = menuOverlay;
        ColorF accent = _accent();
        // Never offer more columns than the rows can fill: a ≤5-track chart is ONE full-width column, not two
        // half-empty ones (the shelf's own fit is count-independent, so this is where that clamp lives).
        int maxCols = Math.Clamp((total + MaxRows - 1) / MaxRows, 1, MaxColumns);

        // No measured width, no page signal, no pages clamp: the shelf self-measures, owns the page, and snaps. The only
        // width-derived thing left in this file is the density tier, which is computed from the fitted column width the
        // shelf hands each card (that IS the prototype's cellW).
        //
        // The shelf is a keyed CHILD of a pass-through wrapper, never this component's ROOT element. A component's output
        // lands in TreeReconciler.ReconcileSingleChild, which pairs old↔new by ElementTypeId ALONE — only
        // ReconcileChildren reads Key — so a key on the root is INERT and the remount below would silently never happen
        // (that is exactly how this chart froze at its seed count while the extended list revalidated behind it). The
        // wrapper adds no geometry: a Direction-1 box whose default AlignItems.Stretch hands the shelf the full band
        // width it self-measures from, and whose height is the shelf's own.
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                PagedShelf.Create(
                    total,
                    cardAt: Card,
                    cardHeight: static _ => RowH,
                    header: Surfaces.AccentHeader(_title, accent),
                    // The stock pager row can't express this chart's two needs: it degrades to a bare count label at one
                    // page (stock chevrons would sit there permanently disabled instead), and the chevron chrome on this
                    // surface is its own 28px transparent pill, not the stock 32px filled circle. ctx.GoTo is still the
                    // shelf's own navigation, so the same-page re-arm (a click while the strip rests mid-page) works.
                    customPager: ctx => Embed.Comp(
                        new ChartPager.Props(ctx.Page, ctx.PageCount, ctx.CanPrev, ctx.CanNext, ctx.Prev, ctx.Next, ctx.GoTo, _total),
                        () => new ChartPager()),
                    pager: ShelfPager.None,
                    rows: MaxRows,
                    minCardW: MinCardW,
                    // UNCAPPED on purpose: with maxColumns 2 the fitted card must keep filling the band, and a real
                    // maximum would strand the row short of its column (the duration cell floating mid-air).
                    maxCardW: 9999f,
                    gap: CellGap,
                    headerGap: HeaderGap,
                    // ≥ the shelf's 12px halo-bleed gutter (the fade is what keeps a mid-glide neighbor soft) and no
                    // more: past that the fade reaches into the row's trailing cell, and the duration must stay crisp.
                    edgeFade: 16f,
                    keyOf: RowKey,
                    maxColumns: maxCols,
                    snap: ShelfSnap.Page)
                    // PagedShelf freezes its ctor props at mount (component-props contract), so every input that can
                    // still change belongs in this key: the count (the extended list revalidates 10 → ≤50 in place) and
                    // the header element (the cover palette lands after first paint). Each changes at most once per
                    // visit, while the chart is still resting on page one, so the remount costs nothing anyone can see.
                    with { Key = "chart:" + total + ":" + accent.GetHashCode() },
            ],
        };
    }

    // ── one chart row, at the fitted column width ───────────────────────────────────────────────────────────
    // The pressure tiers derive from THAT width — never from a captured measurement: this closure is frozen at the
    // shelf's mount, so a render-time local would be the mount-time width forever.
    Element Card(int i, float cellW)
    {
        var list = _live;
        if ((uint)i >= (uint)list.Count) return new BoxEl();
        var t = list[i];
        // Pressure tiers (prototype): shrink art < 220, drop duration < 200; full play counts from 300.
        // Below 340 the subtitle stacks (feat / plays on their own lines) so the feat name isn't crushed.
        float art = cellW < 220f ? 40f : 44f;
        bool showDuration = cellW >= 200f;
        bool fullPlays = cellW >= 300f;
        bool stackSub = cellW < 340f;
        string tier = TierTag(art, showDuration, fullPlays, stackSub);
        // Density props freeze at mount (component-props contract), so the tier is IN the key — a tier flip is a
        // deliberate remount. The ordinal deliberately is not (see RowKey); the row still RECEIVES it, for the rank
        // number and its own _live read.
        Element content = Embed.Comp(() => new ChartRow(this, i, _go, _lib, art, showDuration, fullPlays, stackSub))
            with { Key = "row:" + t.Uri + tier };
        // A pass-through wrapper carrying the drag source (and, when services exist, the context menu): the row owns
        // its own 56px height (which is exactly the shelf's cell), so this must not add a height contract of its own
        // (that is what let the old cap leak into a stretched slot).
        //
        // Axis arbitration is the ENGINE's, and it lands the right way here for free: DragController's arena-lite
        // reads the item's reorder axis off its PARENT container's main axis, and a shelf CELL is a column — so a
        // VERTICAL lift runs along that axis and the drag wins outright, while a HORIZONTAL sweep is perpendicular
        // to it, finds the shelf's overflowing horizontal viewport, and yields to the pan that pages the chart.
        BoxEl row = new BoxEl
        {
            Direction = 1,
            // No selection model on this chart — one track, always a COPY.
            Draggable = Drag.Source(WaveeDragKinds.Resource, () => WaveeResourceDragPayload.ForTrack(t)),
            Children = [content],
        };
        return _acts is { } a && _overlay is { } ov
            ? row.WithContextMenu(ov, () => TrackContextMenu.BuildSingle(a, t))
            : row;
    }

    // The density-tier tag that rides in the row KEY. A LITERAL per band, never a concat: Card runs on every realize, a
    // column crossing realizes five rows in one frame (the mandatory band is exempt from the realize budget), and a
    // four-part string concat there is pure scroll-path garbage. The four flags come from ORDERED cellW thresholds
    // (200/220/300/340), so only five combinations are reachable and this stays a total function of them.
    static string TierTag(float art, bool showDuration, bool fullPlays, bool stackSub)
        => art < 44f ? (showDuration ? "|40d-s" : "|40--s")
                     : (!stackSub ? "|44dp-" : fullPlays ? "|44dps" : "|44d-s");

    // The realized cell's identity is the TRACK, not its ordinal: that is what lets a page flip slide the window
    // instead of remounting every row in it. An empty uri (a local/synthetic track) falls back to the ordinal, the only
    // thing that keeps such a row's key unique.
    string RowKey(int i)
    {
        var list = _live;
        return (uint)i < (uint)list.Count && list[i].Uri.Length > 0 ? "chart:" + list[i].Uri : "chart#" + i;
    }

    public static Element SkeletonShape(IReadOnlyList<Track> tracks, string title)
    {
        // The skeleton stands in for the FIRST paint, which is always the overview seed — never the extended list. Two
        // columns is now the live chart's own maximum (MaxColumns), so the shimmer and the wide chart agree exactly;
        // there is no measured width here, so the sub-ColBreakW single-column tier is not reproduced (it costs one reflow at
        // reveal on a narrow window, against a whole width broker to avoid it).
        int total = Math.Min(tracks.Count, SeedTracks);
        int cols = total > MaxRows ? MaxColumns : 1;
        int rowsPerCol = Math.Min(MaxRows, Math.Max(1, (total + cols - 1) / cols));
        var colEls = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            int n = Math.Min(rowsPerCol, Math.Max(0, total - c * rowsPerCol));
            var rows = new Element[n];
            for (int r = 0; r < n; r++)
            {
                int index = c * rowsPerCol + r;
                rows[r] = Row(tracks[index], index,
                    new TrackRow.State(false, false, false, false, false),
                    art: 44f, showDuration: true, fullPlays: false, stackSub: false, featLine: null,
                    onPlay: static () => { }, onLike: null);
            }
            // Same column contract as the live chart — shimmer and content must not drift geometrically. One gap on
            // both axes, because that is all the live shelf has.
            colEls[c] = new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = CellGap, Children = rows };
        }
        return new BoxEl
        {
            Direction = 1, Gap = HeaderGap,
            Children =
            [
                // The header is the live one's shape exactly: NATURAL width (the shelf's own spacer is what pushes the
                // pager to the trailing edge), so the derived shimmer bar is title-width in both trees.
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [Surfaces.AccentHeader(title, Tok.AccentDefault)],
                },
                new BoxEl { Direction = 0, Gap = CellGap, Children = colEls },
            ],
        };
    }

    // ── the prototype row (shared by live rows and the skeleton) ────────────────────────────────────────────
    static Element Row(Track t, int index, in TrackRow.State st, float art, bool showDuration,
                       bool fullPlays, bool stackSub, Element? featLine, Action onPlay, Action? onLike,
                       IReadSignal<bool>? hoverPaused = null)
    {
        // Tight cells: feat and plays stop competing for one line — feat keeps line 2, plays moves to line 3
        // (where the full count always fits). Rows without a feat line never cramped, so they stay 2-line.
        bool stacked = stackSub && featLine is not null && t.PlayCount > 0;

        // EXACT-SIZE arrays, not List+ToArray. This row builder runs on every realize and a column crossing realizes the
        // whole mandatory band (five rows) in ONE frame, so each avoided List+copy pair is a real slice of that burst.
        // parts·2−1 is exactly the interleaved "part · part · part" length; 0 parts needs no array at all.
        bool hasExplicit = t.IsExplicit, hasFeat = featLine is not null, hasPlays = t.PlayCount > 0 && !stacked;
        int parts = (hasExplicit ? 1 : 0) + (hasFeat ? 1 : 0) + (hasPlays ? 1 : 0);
        var sub = parts == 0 ? Array.Empty<Element>() : new Element[parts * 2 - 1];
        int n = 0;
        if (hasExplicit) sub[n++] = TrackRow.ExplicitBadge();
        if (hasFeat) { if (n > 0) sub[n++] = Dot(); sub[n++] = featLine!; }
        if (hasPlays)
        {
            if (n > 0) sub[n++] = Dot();
            sub[n++] = new TextEl((fullPlays ? t.PlayCount.ToString("N0") : TrackRow.PlaysLabel(t.PlayCount)) + " plays")
            {
                Size = 12f, Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f,   // plays never disappear
            };
        }

        var trail = new Element[showDuration ? 2 : 1];
        trail[0] = TrackRow.Heart(st.Saved, onLike);
        if (showDuration)
            trail[1] = new TextEl(DetailFormat.TrackTime(t.DurationMs)) { Size = 13f, Color = Tok.TextSecondary };

        return new BoxEl
        {
            Direction = 0, MinHeight = RowH, AlignItems = FlexAlign.Center, Gap = 8f,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = CornerRadius4.All(6f), MinWidth = 0f,
            // Fluent: no resting fill (hover/press only). Now-playing is content state — NumberCell EQ +
            // AccentTextPrimary title — same cues as BoundRowSkin / TrackRow; selection pill is orthogonal.
            Fill = ColorF.Transparent,
            HoverFill = WaveeColors.RowHover,
            PressedFill = WaveeColors.RowPressed,
            PressScale = 0.985f, BorderWidth = 1f,
            BorderColor = ColorF.Transparent,
            HoverBorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, OnClick = onPlay,
            // Enter/exit write hoverPaused (EQ stop-tick) and keep PointerBit for HoverOpacity inheritance.
            OnHoverMove = hoverPaused is Signal<bool> hs
                ? _ => { if (!hs.Peek()) hs.Value = true; }
                : null,
            OnPointerExit = hoverPaused is Signal<bool> hs2
                ? () => { if (hs2.Peek()) hs2.Value = false; }
                : static () => { },
            Children =
            [
                new BoxEl
                {
                    Width = 24f, Height = 24f, Shrink = 0f,
                    Children = [TrackRow.NumberCell(index, st.IsNow, st.IsPlaying, st.IsBuffering, false, onPlay, hoverPaused)],
                },
                new BoxEl
                {
                    Width = art, Height = art, Shrink = 0f, ClipToBounds = true,
                    Corners = CornerRadius4.All(Radii.Control),
                    Children = [Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, art, art, Radii.Control, decodePx: 96)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 1f, Justify = FlexJustify.Center,
                    Children = MidColumn(t, st, sub, stacked),
                },
                new BoxEl
                {
                    Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children = trail,
                },
            ],
        };
    }

    // Title + subtitle lines. Stacked (tight) rows get a third line: the full play count, which owns the
    // whole lane so it never needs the abbreviated form. Three 12/14px lines + 2×Gap(1) ≈ 53px < RowH 56.
    static Element[] MidColumn(Track t, in TrackRow.State st, Element[] sub, bool stacked)
    {
        var title = new TextEl(t.Title)
        {
            Size = 14f, Weight = 600,
            Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element subLine = sub.Length > 0
            ? new BoxEl { Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub }
            : new BoxEl();
        if (!stacked) return [title, subLine];
        return
        [
            title, subLine,
            new TextEl(t.PlayCount.ToString("N0") + " plays")
            {
                Size = 12f, Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f,   // plays never disappear
            },
        ];
    }

    static Element Dot() => new TextEl("·") { Size = 12f, Color = Tok.TextTertiary, Shrink = 0f };

    // ── the ‹ ●● › header pager: the chart's own chevron chrome around a WinUI PipsPager ────────────────────
    // The chevrons are the pre-shelf chart's, unchanged (28px, no resting fill); the pips replace the old "1/2" text as
    // the page indicator. At ONE page there is no pager at all — just the track count, which is the shape this header
    // has always degraded to.
    sealed class ChartPager : Component
    {
        internal sealed record Props(int Page, int PageCount, bool CanPrev, bool CanNext,
                                     Action Prev, Action Next, Action<int> GoTo, int Total);

        // The pips are a CONTROLLED pager and the shelf owns the page truth (it re-syncs it from the settled scroll
        // offset, so a touchpad pan moves it too). Mirror that truth in here through an EFFECT — a signal is never
        // written during render — and let the pips write back through onChange/onReselect.
        readonly Signal<int> _selected = new(0);

        public override Element Render()
        {
            var p = UseProps<Props>();
            UseEffect(() => { if (_selected.Peek() != p.Page) _selected.Value = p.Page; }, p.Page);

            if (p.PageCount <= 1)
                return new TextEl(p.Total.ToString()) { Size = 12f, Weight = 600, Color = Tok.TextTertiary };
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                Children =
                [
                    Chevron(Icons.ChevronLeft, p.CanPrev, p.Prev),
                    // onReselect is load-bearing on a snapping shelf: after a partial pan the strip rests BETWEEN pages
                    // while the pip still reads that page, and the re-click is the request to be put back on the
                    // boundary — the value channel swallows it (WinUI semantics), this channel does not.
                    PipsPager.Create(p.PageCount, _selected, onChange: p.GoTo, onReselect: p.GoTo),
                    Chevron(Icons.ChevronRight, p.CanNext, p.Next),
                ],
            };
        }
    }

    static Element Chevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f), HoverFill = enabled ? Tok.FillSubtleSecondary : default,
        HoverScale = enabled ? 1.06f : 1f, OnClick = enabled ? onClick : null,
        Children = [Icon(glyph, 12f, enabled ? Tok.TextSecondary : Tok.TextTertiary)],
    };

    // ── chart row component: signal-scoped state reads so playback changes re-skin ONE row ──────────────────
    sealed class ChartRow : Component
    {
        readonly ArtistPopular _o;
        readonly int _index;
        readonly Action<string, string?> _go;
        readonly LibraryBridge? _lib;
        readonly float _art;
        readonly bool _showDuration, _fullPlays, _stackSub;

        public ChartRow(ArtistPopular o, int index, Action<string, string?> go, LibraryBridge? lib,
                        float art, bool showDuration, bool fullPlays, bool stackSub)
        {
            _o = o; _index = index; _go = go; _lib = lib;
            _art = art; _showDuration = showDuration; _fullPlays = fullPlays; _stackSub = stackSub;
        }

        // This row's own track + visual state, equality-gated. TrackRow.StateOf reads the bridge Identity/IsPlaying/
        // IsBuffering signals and the library saved-set; read bare at render scope those subscribe the WHOLE row to
        // every playback event, so a skip between two OTHER tracks re-rendered all ten charted rows (measured:
        // ChartRow×10 per play/pause/skip/save, each rebuilding a full row grid + the FeatLine allocations). Behind a
        // Memo the recompute still runs, but a render is scheduled only when THIS row's tuple actually changed — the
        // same shape DetailTracks.BoundRowContent already uses for its presentation record.
        readonly record struct Presentation(Track? Track, TrackRow.State State);

        public override Element Render()
        {
            // CONSTRAINT: _o._live is a plain FIELD, not a signal — reading it inside this UseComputed subscribes to
            // nothing, so a row can only pick up a longer list when it re-renders for some other reason. That is sound
            // ONLY because the count-keyed wrapper remount (see Render's `Key = "chart:" + total + …`) rebuilds this
            // whole shelf whenever _live's charted length changes. Anything that weakens that key must turn _live into a
            // Signal here first, or these rows will keep charting the seed list.
            var presentation = UseComputed(() =>
            {
                int n = Math.Min(_o._live.Count, MaxTracks);
                if ((uint)_index >= (uint)n) return default(Presentation);
                var track = _o._live[_index];
                return new Presentation(track, TrackRow.StateOf(_o._bridge, _lib, track));
            });
            var hovered = UseSignal(false);
            if (presentation.Value.Track is not { } t) return new BoxEl();
            var st = presentation.Value.State;
            return Row(t, _index, st, _art, _showDuration, _fullPlays, _stackSub,
                featLine: FeatLine(t, _o._ctx, _go),
                // Start BY URI, not by index. The artist context is a server list (popular-release-segments-main-roles)
                // whose order is its own — with an extended chart, this row's ordinal is not that list's ordinal, and
                // ContextResolve deliberately refuses a blind index across divergent orderings (F2). The index rides
                // along only as the fallback for a uri the server list doesn't carry.
                onPlay: () => TrackRow.Invoke(_o._bridge, t, () => _ = _o._svc.Player.PlayContextTrackAsync(
                    _o._ctx, new PlaybackContextTrack(t.Uri), _index)),
                onLike: t.Uri.Length > 0 ? () => _lib?.ToggleSaved(t.Uri, t.Title) : null,
                hoverPaused: hovered);
        }
    }

    /// <summary>The "feat. X (+N)" credits line: only when the page artist is identifiable in the credits AND
    /// someone else is credited too (repeating the page artist's own name under all ten rows is noise). The
    /// first featured name is a clickable link; "+N" opens a MenuFlyout of the rest (each navigates).</summary>
    static Element? FeatLine(Track t, string pageArtistUri, Action<string, string?> go)
    {
        if (t.Artists.Count == 0 || pageArtistUri.Length == 0) return null;
        // COUNT-then-build, so the common row (one featured artist) allocates no collection at all: this runs on every
        // ChartRow render, five of them per column crossing. The featured LIST is built only on the "+N" branch, which is
        // its only consumer (ArtistMoreButton's flyout).
        bool pageInCredits = false;
        int featCount = 0, firstFeat = -1;
        for (int i = 0; i < t.Artists.Count; i++)
        {
            if (string.Equals(t.Artists[i].Uri, pageArtistUri, StringComparison.OrdinalIgnoreCase)) { pageInCredits = true; continue; }
            if (firstFeat < 0) firstFeat = i;
            featCount++;
        }
        if (!pageInCredits || featCount == 0) return null;

        var first = t.Artists[firstFeat];
        var kids = new Element[featCount > 1 ? 3 : 2];
        kids[0] = new TextEl(Loc.Get(Strings.Artist.Feat)) { Size = 12f, Color = Tok.TextTertiary, Shrink = 0f };
        kids[1] = new SpanTextEl([new TextSpan(first.Name, OnClick: () => go("artist:" + first.Uri, first.Name))])
        {
            Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            MinWidth = 0f, Shrink = 1f,
        };
        if (featCount > 1)
        {
            var featured = new List<ArtistRef>(featCount);
            for (int i = 0; i < t.Artists.Count; i++)
            {
                var a = t.Artists[i];
                if (!string.Equals(a.Uri, pageArtistUri, StringComparison.OrdinalIgnoreCase)) featured.Add(a);
            }
            kids[2] = Embed.Comp(() => new ArtistMoreButton(featured, go)) with { Key = "featmore:" + first.Uri };
        }
        return new BoxEl
        {
            Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f,
            Children = kids,
        };
    }

}
