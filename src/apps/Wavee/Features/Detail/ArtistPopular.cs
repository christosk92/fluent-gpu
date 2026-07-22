using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Artist "Top tracks" chart: up to 10 ranked rows in 1–2 columns, built from the ONE canonical track cell
// (TrackRow.Row) — the same number↔play/pause hover transport, live equalizer, per-row heart, zebra skin and
// column grid as search "Songs" and the detail lists, so this surface can never drift from the rest of the app.
// Width pressure changes column count / plays density only (no Show more, no pager).
sealed class ArtistPopular : Component
{
    readonly IReadOnlyList<Track> _tracks;
    readonly string _ctx, _title;
    readonly PlaybackBridge? _bridge;
    readonly Services _svc;
    readonly Func<ColorF> _accent;

    public ArtistPopular(IReadOnlyList<Track> tracks, string ctx, PlaybackBridge? bridge, Services svc, string title, Func<ColorF> accent)
    {
        _tracks = tracks; _ctx = ctx; _bridge = bridge; _svc = svc; _title = title; _accent = accent;
    }

    const int MaxTracks = 10;
    const int MaxRows = 5;          // the band is NEVER taller than five rows — narrow widths page via ‹1/2› instead
    const int MaxColumns = 3;       // wide panes split eagerly: 2–3 columns beat one tall stack
    const float MinColW = 360f;     // the compact grid tier (# · ♥ · art · title · plays · time) fits from ~360px
    const float ColGap = Spacing.XL;
    const float RowContentH = 56f;

    // Full tier: exact comma-separated play counts + the trailing "…" lane. Compact tier: abbreviated plays, no "…".
    static readonly ColumnSet ColsFull = new(Album: false, By: false, Date: false, Video: false, Plays: true, Heart: true, Thumb: true, FullPlays: true);
    static readonly ColumnSet ColsCompact = new(Album: false, By: false, Date: false, Video: false, Plays: true, Heart: true, Thumb: true);
    static readonly TrackSize[] ColumnsFull =
        [TrackSize.Px(36f), TrackSize.Px(40f), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(), TrackSize.Px(96f), TrackSize.Px(48f), TrackSize.Px(40f)];
    static readonly TrackSize[] ColumnsCompact =
        [TrackSize.Px(36f), TrackSize.Px(40f), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(), TrackSize.Px(56f), TrackSize.Px(48f)];

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var lib = UseContext(LibraryBridge.Slot);
        var acts = UseContext(ActionServices.Slot);
        var menuOverlay = UseContext(Overlay.Service);
        var measuredW = UseMeasuredWidth(1f);
        var page = UseSignal(0);

        int total = Math.Min(_tracks.Count, MaxTracks);
        float width = measuredW.Value > 0.5f ? measuredW.Value : 600f;
        // Split EAGERLY: as many columns as the width fits (≤3), never more than the tracks need at the 5-row cap.
        int cols = Math.Clamp((int)((width + ColGap) / (MinColW + ColGap)), 1, MaxColumns);
        cols = Math.Min(cols, Math.Max(1, (total + MaxRows - 1) / MaxRows));
        float cellW = (width - (cols - 1) * ColGap) / cols;
        bool full = cellW >= 460f;
        bool hasMenu = acts is not null && menuOverlay is not null;

        // Column-first pagination at a hard 5-row height: 2+ columns show all 10; 1 column pages 1–5 / 6–10 via ‹1/2›.
        int perPage = cols * MaxRows;
        int pages = Math.Max(1, (total + perPage - 1) / perPage);
        int pg = Math.Min(page.Value, pages - 1);
        UseEffect(() => { if (page.Peek() > pages - 1) page.Value = pages - 1; }, pages);
        int pageStart = pg * perPage;
        int pageCount = Math.Min(total - pageStart, perPage);
        int rowsPerCol = Math.Max(1, (pageCount + cols - 1) / cols);   // balanced: 10 across 3 cols → 4/4/2, not 5/5/0

        var colEls = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            var rows = new List<Element>(rowsPerCol);
            for (int r = 0; r < rowsPerCol; r++)
            {
                int i = pageStart + c * rowsPerCol + r;
                if (i >= pageStart + pageCount) break;
                var t = _tracks[i];
                int vr = r;
                // Density/position props freeze at mount (component-props contract) — key by tier + visual row so a
                // width/column-count change remounts the row instead of leaving stale frozen props.
                Element row = Embed.Comp(() => new ChartRow(this, i, vr, go, lib, full, hasMenu))
                    with { Key = "chart:" + t.Uri + "|" + r + (full ? "f" : "c") };
                if (acts is { } a && menuOverlay is { } ov)
                {
                    var track = t;
                    row = new BoxEl { Direction = 1, Children = [row] }.WithContextMenu(ov, () => TrackContextMenu.BuildSingle(a, track));
                }
                rows.Add(row);
            }
            colEls[c] = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = rows.ToArray(),
            };
        }

        var header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Children =
            [
                Surfaces.AccentHeader(_title, _accent()) with { Grow = 1f, Basis = 0f },
                pages > 1
                    ? Pager(pg, pages, to => page.Value = Math.Clamp(to, 0, pages - 1))
                    : new TextEl(total.ToString()) { Size = 12f, Weight = 600, Color = Tok.TextTertiary },
            ],
        };

        return new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                header,
                new BoxEl
                {
                    Direction = 0, Gap = ColGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                    Children = colEls,
                },
            ],
        };
    }

    public static Element SkeletonShape(IReadOnlyList<Track> tracks, string title)
    {
        int total = Math.Min(tracks.Count, MaxTracks);
        int cols = total > 5 ? 2 : 1;
        int rowsPerCol = Math.Max(1, (total + cols - 1) / cols);
        var colEls = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            int n = Math.Min(rowsPerCol, Math.Max(0, total - c * rowsPerCol));
            var rows = new Element[n];
            for (int r = 0; r < n; r++)
                rows[r] = TrackRow.Row(tracks[c * rowsPerCol + r], c * rowsPerCol + r,
                    new TrackRow.State(false, false, false, false, false),
                    ColsFull, ColumnsFull, RowContentH, showTrackArtist: false, static (_, _) => { },
                    onPlay: static () => { }, onLike: null, zebra: true,
                    actionsCell: TrackRow.MoreButton(false),   // reserve the "…" lane so the shimmer matches live rows
                    zebraIndex: r);
            colEls[c] = new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Children = rows };
        }
        return new BoxEl
        {
            Direction = 1, Gap = 12f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Children = [Surfaces.AccentHeader(title, Tok.AccentDefault) with { Grow = 1f, Basis = 0f }],
                },
                new BoxEl { Direction = 0, Gap = ColGap, Children = colEls },
            ],
        };
    }

    // ── the ‹ 1/2 › header pager (the pre-rework chart's pager chrome, unchanged) ───────────────────────────
    static Element Pager(int pg, int pages, Action<int> goTo) => new BoxEl
    {
        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Children =
        [
            Chevron(Icons.ChevronLeft, pg > 0, () => goTo(pg - 1)),
            new TextEl($"{pg + 1}/{pages}") { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
            Chevron(Icons.ChevronRight, pg < pages - 1, () => goTo(pg + 1)),
        ],
    };

    static Element Chevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f), HoverFill = enabled ? Tok.FillSubtleSecondary : default,
        HoverScale = enabled ? 1.06f : 1f, OnClick = enabled ? onClick : null,
        Children = [Icon(glyph, 12f, enabled ? Tok.TextSecondary : Tok.TextTertiary)],
    };

    // ── chart row: the canonical cell bound to this chart's playback context ────────────────────────────────
    sealed class ChartRow : Component
    {
        readonly ArtistPopular _o;
        readonly int _index, _visualRow;
        readonly Action<string, string?> _go;
        readonly LibraryBridge? _lib;
        readonly bool _full, _menu;

        public ChartRow(ArtistPopular o, int index, int visualRow, Action<string, string?> go, LibraryBridge? lib, bool full, bool menu)
        {
            _o = o; _index = index; _visualRow = visualRow; _go = go; _lib = lib; _full = full; _menu = menu;
        }

        public override Element Render()
        {
            int total = Math.Min(_o._tracks.Count, MaxTracks);
            if ((uint)_index >= (uint)total) return new BoxEl();
            var t = _o._tracks[_index];
            var st = TrackRow.StateOf(_o._bridge, _lib, t);
            Element? featLine = FeatLine(t, _o._ctx, _go);

            return TrackRow.Row(t, _index, st, _full ? ColsFull : ColsCompact, _full ? ColumnsFull : ColumnsCompact,
                RowContentH, showTrackArtist: featLine is not null, _go,
                onPlay: () => TrackRow.Invoke(_o._bridge, t, () => _ = _o._svc.Player.PlayAsync(_o._ctx, _index)),
                onLike: t.Uri.Length > 0 ? () => _lib?.ToggleSaved(t.Uri, t.Title) : null,
                zebra: true,
                actionsCell: _full ? TrackRow.MoreButton(_menu) : null,
                zebraIndex: _visualRow,
                artistsLine: featLine,
                explicitBadge: true);
        }
    }

    /// <summary>The "feat. X, Y" credits subline: only when the page artist is identifiable in the credits AND someone
    /// else is credited too (repeating the page artist's own name under all ten rows is noise). Each featured name is
    /// its own clickable span; the whole line ellipsizes under pressure.</summary>
    static Element? FeatLine(Track t, string pageArtistUri, Action<string, string?> go)
    {
        if (t.Artists.Count == 0 || pageArtistUri.Length == 0) return null;
        var featured = new List<ArtistRef>(t.Artists.Count);
        bool pageInCredits = false;
        for (int i = 0; i < t.Artists.Count; i++)
        {
            var a = t.Artists[i];
            if (string.Equals(a.Uri, pageArtistUri, StringComparison.OrdinalIgnoreCase)) { pageInCredits = true; continue; }
            featured.Add(a);
        }
        if (!pageInCredits || featured.Count == 0) return null;

        var spans = new TextSpan[featured.Count * 2];
        int n = 0;
        spans[n++] = new TextSpan(Loc.Get(Strings.Artist.Feat) + " ");
        for (int i = 0; i < featured.Count; i++)
        {
            if (i > 0) spans[n++] = new TextSpan(", ");
            var a = featured[i];   // fresh per-iteration capture → each link navigates to its OWN artist
            spans[n++] = new TextSpan(a.Name, OnClick: () => go("artist:" + a.Uri, a.Name));
        }
        return new SpanTextEl(spans)
        {
            Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            MaxLines = 1, MinWidth = 0f,
        };
    }
}
