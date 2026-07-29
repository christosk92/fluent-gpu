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
// now-playing track), TrackRow.Heart. Hard 5-row height; a single column pages ‹1/2›.
//
// Row chrome is the prototype's `.row` verbatim and deliberately has NO zebra: transparent fill over a
// 1px TRANSPARENT border (a present-but-invisible stroke so hover never nudges layout), painting only on
// hover/press — plus the prototype's `.row.is-playing` accent wash, which is then the ONE fill that carries
// meaning. This is also what SkeletonDeriver leaves standing (it strips Fill/Border/hover brushes), so the
// live chart and its own shimmer read identically. Do not "restore" the bands.
//
// The chart HUGS its rows: a flat 56px each, 8px apart, and the column simply ends where the last row does. It does
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

    public ArtistPopular(IReadOnlyList<Track> tracks, string ctx, PlaybackBridge? bridge, Services svc, string title, Func<ColorF> accent)
    {
        _tracks = tracks; _live = tracks; _ctx = ctx; _bridge = bridge; _svc = svc; _title = title; _accent = accent;
    }

    // The chart shows the FULL extended popular list (overview seed ∪ artist-top-tracks-extensions), not just the
    // overview's ten — the 5-row cap and the column pager already absorb the extra rows.
    const int MaxTracks = ArtistPopularTracks.ExtendedCap;
    const int SeedTracks = ArtistPopularTracks.OverviewSeedCap;   // the pre-extension skeleton can never exceed this
    const int MaxRows = 5;          // the band is NEVER taller than five rows — a single column pages via ‹1/2›
    const int MaxColumns = 3;
    const float MinColW = 260f;     // prototype --chart-min-col
    const float ColGap = 12f;       // prototype .chart gap: 2px 12px
    const float RowVGap = Spacing.S;
    const float RowH = 56f;

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var lib = UseContext(LibraryBridge.Slot);
        var acts = UseContext(ActionServices.Slot);
        var menuOverlay = UseContext(Overlay.Service);
        var measuredW = UseMeasuredWidth(1f);
        var page = UseSignal(0);

        // Step two of the chart, owned here rather than by the page: the overview seed paints immediately, and the
        // extended list (up to 50, play counts preserved on the head) revalidates into the SAME component — so the pager
        // simply grows instead of the whole band re-mounting. Offline / failure returns the seed, so this never blanks.
        var extended = UseResource(ct => _svc.ArtistPopularTracks.EnsureExtendedAsync(_ctx, _tracks, ct), _tracks, _ctx);
        _live = extended.Loadable.Value.Value is { Count: > 0 } merged ? merged : _tracks;

        int total = Math.Min(_live.Count, MaxTracks);
        float width = measuredW.Value > 0.5f ? measuredW.Value : 600f;
        // Prototype fit: floor((w+gap)/(minCol+gap)) capped at 3, but prefer 2 over a cramped 3 until 860px.
        int cols = Math.Clamp((int)((width + ColGap) / (MinColW + ColGap)), 1, MaxColumns);
        if (cols == 3 && width < 860f) cols = 2;
        cols = Math.Min(cols, Math.Max(1, (total + MaxRows - 1) / MaxRows));
        float cellW = (width - (cols - 1) * ColGap) / cols;
        // Pressure tiers (prototype): shrink art < 220, drop duration < 200; full play counts from 300.
        // Below 340 the subtitle stacks (feat / plays on their own lines) so the feat name isn't crushed.
        float art = cellW < 220f ? 40f : 44f;
        bool showDuration = cellW >= 200f;
        bool fullPlays = cellW >= 300f;
        bool stackSub = cellW < 340f;

        // Column-first pagination at the 5-row cap: with the overview's ten, 2–3 columns show them all and one column
        // pages 1–5 / 6–10; once the extended list lands, `pages` simply grows and the clamp below keeps `page` in range.
        int perPage = cols * MaxRows;
        int pages = Math.Max(1, (total + perPage - 1) / perPage);
        int pg = Math.Min(page.Value, pages - 1);
        UseEffect(() => { if (page.Peek() > pages - 1) page.Value = pages - 1; }, pages);
        int pageStart = pg * perPage;
        int pageCount = Math.Min(total - pageStart, perPage);
        int rowsPerCol = Math.Max(1, (pageCount + cols - 1) / cols);   // balanced: 10 across 3 cols → 4/4/2

        string tier = "|" + (int)art + (showDuration ? "d" : "-") + (fullPlays ? "p" : "-") + (stackSub ? "s" : "-");
        var colEls = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            var rows = new List<Element>(rowsPerCol);
            for (int r = 0; r < rowsPerCol; r++)
            {
                int i = pageStart + c * rowsPerCol + r;
                if (i >= pageStart + pageCount) break;
                var t = _live[i];
                // Density/position props freeze at mount (component-props contract) — key by tier + track index
                // so a width/column-count change remounts the row instead of leaving stale frozen props.
                Element row = Embed.Comp(() => new ChartRow(this, i, go, lib, art, showDuration, fullPlays, stackSub))
                    with { Key = "chart:" + t.Uri + "|" + i + tier };
                if (acts is { } a && menuOverlay is { } ov)
                {
                    var track = t;
                    // A pass-through wrapper: the row owns its own 56px height, so this must not add a height
                    // contract of its own (that is what let the old cap leak into a stretched slot).
                    row = new BoxEl { Direction = 1, Children = [row] }
                        .WithContextMenu(ov, () => TrackContextMenu.BuildSingle(a, track));
                }
                rows.Add(row);
            }
            // A short column (the 3-col 4/4/2 case) is simply shorter — no spacer slots. Nothing stretches, so there
            // is no per-row share to keep equal across columns.
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

        // NO vertical Grow anywhere in this chain: the chart is exactly as tall as its rows. The columns still grow
        // HORIZONTALLY (Grow on the row-direction box above) to share the band's width — that is a different axis.
        return new BoxEl
        {
            Direction = 1, Gap = 10f,
            Children =
            [
                header,
                new BoxEl
                {
                    Direction = 0, Gap = ColGap, MinWidth = 0f,
                    Children = colEls,
                },
            ],
        };
    }

    public static Element SkeletonShape(IReadOnlyList<Track> tracks, string title)
    {
        // The skeleton stands in for the FIRST paint, which is always the overview seed — never the extended list.
        int total = Math.Min(tracks.Count, SeedTracks);
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
                rows[r] = Row(tracks[index], index,
                    new TrackRow.State(false, false, false, false, false),
                    art: 44f, showDuration: true, fullPlays: false, stackSub: false, featLine: null,
                    onPlay: static () => { }, onLike: null);
            }
            // Same column contract as the live chart — shimmer and content must not drift geometrically.
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
    static Element Row(Track t, int index, in TrackRow.State st, float art, bool showDuration,
                       bool fullPlays, bool stackSub, Element? featLine, Action onPlay, Action? onLike)
    {
        // Tight cells: feat and plays stop competing for one line — feat keeps line 2, plays moves to line 3
        // (where the full count always fits). Rows without a feat line never cramped, so they stay 2-line.
        bool stacked = stackSub && featLine is not null && t.PlayCount > 0;

        var sub = new List<Element>(5);
        if (t.IsExplicit) sub.Add(TrackRow.ExplicitBadge());
        if (featLine is not null)
        {
            if (sub.Count > 0) sub.Add(Dot());
            sub.Add(featLine);
        }
        if (t.PlayCount > 0 && !stacked)
        {
            if (sub.Count > 0) sub.Add(Dot());
            sub.Add(new TextEl((fullPlays ? t.PlayCount.ToString("N0") : TrackRow.PlaysLabel(t.PlayCount)) + " plays")
            {
                Size = 12f, Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f,   // plays never disappear
            });
        }

        var trail = new List<Element>(2) { TrackRow.Heart(st.Saved, onLike) };
        if (showDuration)
            trail.Add(new TextEl(DetailFormat.TrackTime(t.DurationMs)) { Size = 13f, Color = Tok.TextSecondary });

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
                    Children = MidColumn(t, st, sub, stacked),
                },
                new BoxEl
                {
                    Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children = trail.ToArray(),
                },
            ],
        };
    }

    // Title + subtitle lines. Stacked (tight) rows get a third line: the full play count, which owns the
    // whole lane so it never needs the abbreviated form. Three 12/14px lines + 2×Gap(1) ≈ 53px < RowH 56.
    static Element[] MidColumn(Track t, in TrackRow.State st, List<Element> sub, bool stacked)
    {
        var title = new TextEl(t.Title)
        {
            Size = 14f, Weight = 600,
            Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element subLine = sub.Count > 0
            ? new BoxEl { Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub.ToArray() }
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
            var presentation = UseComputed(() =>
            {
                int n = Math.Min(_o._live.Count, MaxTracks);
                if ((uint)_index >= (uint)n) return default(Presentation);
                var track = _o._live[_index];
                return new Presentation(track, TrackRow.StateOf(_o._bridge, _lib, track));
            });
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
