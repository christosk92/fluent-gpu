using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Artist "Top tracks" chart — the ops/scratch/popular-releases-prototype.html row, verbatim geometry:
// rank · 44px art (40px under pressure) · title+subline (E · feat. X +N · plays) · heart · duration,
// 56px rows, 12px gutters,
// ~260px min columns (≤3, prefer 2 until 860px). Behavior stays canonical: the # cell is TrackRow.NumberCell
// (number↔play/pause hover transport + live equalizer), row click = TrackRow.Invoke (toggles pause on the
// now-playing track), TrackRow.Heart, zebra by track ordinal. Hard 5-row height; a single column pages ‹1/2›.
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
    const int MaxRows = 5;          // the band is NEVER taller than five rows — a single column pages via ‹1/2›
    const int MaxColumns = 3;
    const float MinColW = 260f;     // prototype --chart-min-col
    const float ColGap = 12f;       // prototype .chart gap: 2px 12px
    const float RowVGap = 2f;
    const float RowH = 56f;

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
        // Prototype fit: floor((w+gap)/(minCol+gap)) capped at 3, but prefer 2 over a cramped 3 until 860px.
        int cols = Math.Clamp((int)((width + ColGap) / (MinColW + ColGap)), 1, MaxColumns);
        if (cols == 3 && width < 860f) cols = 2;
        cols = Math.Min(cols, Math.Max(1, (total + MaxRows - 1) / MaxRows));
        float cellW = (width - (cols - 1) * ColGap) / cols;
        // Pressure tiers (prototype): shrink art < 220, drop duration < 200; full play counts from 300.
        float art = cellW < 220f ? 40f : 44f;
        bool showDuration = cellW >= 200f;
        bool fullPlays = cellW >= 300f;

        // Column-first pagination at the 5-row cap: 2–3 columns show all 10; 1 column pages 1–5 / 6–10.
        int perPage = cols * MaxRows;
        int pages = Math.Max(1, (total + perPage - 1) / perPage);
        int pg = Math.Min(page.Value, pages - 1);
        UseEffect(() => { if (page.Peek() > pages - 1) page.Value = pages - 1; }, pages);
        int pageStart = pg * perPage;
        int pageCount = Math.Min(total - pageStart, perPage);
        int rowsPerCol = Math.Max(1, (pageCount + cols - 1) / cols);   // balanced: 10 across 3 cols → 4/4/2

        string tier = "|" + (int)art + (showDuration ? "d" : "-") + (fullPlays ? "p" : "-");
        var colEls = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            var rows = new List<Element>(rowsPerCol);
            for (int r = 0; r < rowsPerCol; r++)
            {
                int i = pageStart + c * rowsPerCol + r;
                if (i >= pageStart + pageCount) break;
                var t = _tracks[i];
                // Density/position props freeze at mount (component-props contract) — key by tier + track index
                // so a width/column-count change remounts the row instead of leaving stale frozen props.
                Element row = Embed.Comp(() => new ChartRow(this, i, i, go, lib, art, showDuration, fullPlays))
                    with { Key = "chart:" + t.Uri + "|" + i + tier };
                if (acts is { } a && menuOverlay is { } ov)
                {
                    var track = t;
                    row = new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Children = [row] }
                        .WithContextMenu(ov, () => TrackContextMenu.BuildSingle(a, track));
                }
                rows.Add(row);
            }
            // Short columns (3-col 4/4/2) get invisible spacer slots so every column stretches by the same
            // per-row share — rows stay height-aligned across columns when the band cross-stretches.
            while (rows.Count < rowsPerCol)
                rows.Add(new BoxEl { Grow = 1f, Basis = 0f, MinHeight = RowH });
            colEls[c] = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = RowVGap,
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

        // Grow chain: the band cross-stretches this component to the (possibly taller) releases column, the
        // chart area absorbs the leftover, and each row's Grow share makes the cells taller — bottom-aligned
        // columns instead of a dead band under the chart.
        return new BoxEl
        {
            Direction = 1, Gap = 10f, Grow = 1f,
            Children =
            [
                header,
                new BoxEl
                {
                    Direction = 0, Gap = ColGap, MinWidth = 0f, Grow = 1f,
                    Children = colEls,
                },
            ],
        };
    }

    public static Element SkeletonShape(IReadOnlyList<Track> tracks, string title)
    {
        int total = Math.Min(tracks.Count, MaxTracks);
        int cols = total > MaxRows ? 2 : 1;
        int rowsPerCol = Math.Min(MaxRows, Math.Max(1, (total + cols - 1) / cols));
        var colEls = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            int n = Math.Min(rowsPerCol, Math.Max(0, total - c * rowsPerCol));
            var rows = new Element[n];
            for (int r = 0; r < n; r++)
            {
                int index = c * rowsPerCol + r;
                rows[r] = Row(tracks[index], index, index,
                    new TrackRow.State(false, false, false, false, false),
                    art: 44f, showDuration: true, fullPlays: false, featLine: null,
                    onPlay: static () => { }, onLike: null);
            }
            colEls[c] = new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = RowVGap, Children = rows };
        }
        return new BoxEl
        {
            Direction = 1, Gap = 10f,
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

    // ── the prototype row (shared by live rows and the skeleton) ────────────────────────────────────────────
    static Element Row(Track t, int index, int zebraIndex, in TrackRow.State st, float art, bool showDuration,
                       bool fullPlays, Element? featLine, Action onPlay, Action? onLike)
    {
        bool shaded = zebraIndex % 2 != 0;   // zero-based odd = displayed tracks 2, 4, 6, 8, 10

        var sub = new List<Element>(5);
        if (t.IsExplicit) sub.Add(TrackRow.ExplicitBadge());
        if (featLine is not null)
        {
            if (sub.Count > 0) sub.Add(Dot());
            sub.Add(featLine);
        }
        if (t.PlayCount > 0)
        {
            if (sub.Count > 0) sub.Add(Dot());
            sub.Add(new TextEl((fullPlays ? t.PlayCount.ToString("N0") : TrackRow.PlaysLabel(t.PlayCount)) + " plays")
            {
                Size = 12f, Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f,   // plays never disappear
            });
        }

        var trail = new List<Element>(2) { TrackRow.Heart(st.Saved, onLike) };
        if (showDuration)
            trail.Add(new TextEl(DetailFormat.TrackTime(t.DurationMs)) { Size = 12f, Color = Tok.TextSecondary });

        return new BoxEl
        {
            Direction = 0, MinHeight = RowH, Grow = 1f, Basis = 0f, AlignItems = FlexAlign.Center, Gap = 8f,
            Padding = new Edges4(6f, 0f, 6f, 0f), Corners = CornerRadius4.All(6f), MinWidth = 0f,
            Fill = shaded ? WaveeColors.RowZebra : ColorF.Transparent,
            HoverFill = shaded ? WaveeColors.RowHoverZebra : WaveeColors.RowHover,
            PressedFill = shaded ? WaveeColors.RowPressedZebra : WaveeColors.RowPressed,
            PressScale = 0.985f, BorderWidth = 1f,
            BorderColor = shaded ? Tok.StrokeCardDefault : ColorF.Transparent,
            HoverBorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, OnClick = onPlay,
            // No-op pointer-exit → registers PointerBit so this row is the "interactive ancestor" whose hover
            // progress the # cell inherits — that's what reveals play/pause on row hover (TrackRow.Row idiom).
            OnPointerExit = static () => { },
            Children =
            [
                new BoxEl
                {
                    Width = 24f, Height = 24f, Shrink = 0f,
                    Children = [TrackRow.NumberCell(index, st.IsNow, st.IsPlaying, st.IsBuffering, false, onPlay)],
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
                    Children =
                    [
                        new TextEl(t.Title)
                        {
                            Size = 14f, Weight = 600,
                            Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        sub.Count > 0
                            ? new BoxEl { Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub.ToArray() }
                            : new BoxEl(),
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children = trail.ToArray(),
                },
            ],
        };
    }

    static Element Dot() => new TextEl("·") { Size = 12f, Color = Tok.TextTertiary, Shrink = 0f };

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

    // ── chart row component: signal-scoped state reads so playback changes re-skin ONE row ──────────────────
    sealed class ChartRow : Component
    {
        readonly ArtistPopular _o;
        readonly int _index, _zebraIndex;
        readonly Action<string, string?> _go;
        readonly LibraryBridge? _lib;
        readonly float _art;
        readonly bool _showDuration, _fullPlays;

        public ChartRow(ArtistPopular o, int index, int zebraIndex, Action<string, string?> go, LibraryBridge? lib,
                        float art, bool showDuration, bool fullPlays)
        {
            _o = o; _index = index; _zebraIndex = zebraIndex; _go = go; _lib = lib;
            _art = art; _showDuration = showDuration; _fullPlays = fullPlays;
        }

        public override Element Render()
        {
            int total = Math.Min(_o._tracks.Count, MaxTracks);
            if ((uint)_index >= (uint)total) return new BoxEl();
            var t = _o._tracks[_index];
            var st = TrackRow.StateOf(_o._bridge, _lib, t);
            return Row(t, _index, _zebraIndex, st, _art, _showDuration, _fullPlays,
                featLine: FeatLine(t, _o._ctx, _go),
                onPlay: () => TrackRow.Invoke(_o._bridge, t, () => _ = _o._svc.Player.PlayAsync(_o._ctx, _index)),
                onLike: t.Uri.Length > 0 ? () => _lib?.ToggleSaved(t.Uri, t.Title) : null);
        }
    }

    /// <summary>The "feat. X (+N)" credits line: only when the page artist is identifiable in the credits AND
    /// someone else is credited too (repeating the page artist's own name under all ten rows is noise). The
    /// first featured name is a clickable link; "+N" opens a MenuFlyout of the rest (each navigates).</summary>
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

        var first = featured[0];
        var kids = new List<Element>(3)
        {
            new TextEl(Loc.Get(Strings.Artist.Feat)) { Size = 12f, Color = Tok.TextTertiary, Shrink = 0f },
            new SpanTextEl([new TextSpan(first.Name, OnClick: () => go("artist:" + first.Uri, first.Name))])
            {
                Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f, Shrink = 1f,
            },
        };
        if (featured.Count > 1)
            kids.Add(Embed.Comp(() => new ArtistMoreButton(featured, go)) with { Key = "featmore:" + first.Uri });
        return new BoxEl
        {
            Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f,
            Children = kids.ToArray(),
        };
    }

}
