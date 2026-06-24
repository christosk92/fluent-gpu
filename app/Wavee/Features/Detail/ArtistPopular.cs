using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The "Popular" top-tracks list for an artist. Its own component so the now-playing equalizer re-skins on playback
// changes WITHOUT re-rendering the whole artist page. Each row plays the artist context at its index (the same ordered
// set FakeData.ContextTracks resolves for the artist uri), so #1 here is #1 in the queue.
sealed class ArtistPopular : Component
{
    readonly IReadOnlyList<Track> _tracks;
    readonly string _ctx, _title;
    readonly PlaybackBridge? _bridge;
    readonly Services _svc;
    public ArtistPopular(IReadOnlyList<Track> tracks, string ctx, PlaybackBridge? bridge, Services svc, string title)
    { _tracks = tracks; _ctx = ctx; _bridge = bridge; _svc = svc; _title = title; }

    const int Rows = 4;          // WinUI ColumnsFirstGridLayout MaxRows
    const int MaxTracks = 10;
    const float ItemHeight = 68f;
    const float RowGap = 4f;
    const float ColumnGap = 8f;
    const float MinItemWidth = 280f;
    const int MaxColumns = 3;

    // WinUI ArtistPage: ColumnsFirstGridLayout, four 68px rows, 280px minimum cells, column-first pagination.

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var lib = UseContext(LibraryBridge.Slot);
        var width = UseSignal(600f);                     // self-measured → responsive column count
        var page = UseSignal(0);
        var dragX = UseRef(0f);                          // horizontal swipe anchor (trackpad/touch/mouse) → page flip
        var cur = _bridge?.CurrentTrack.Value;          // subscribe → now-playing equalizer
        bool playing = _bridge?.IsPlaying.Value ?? false;
        bool buffering = _bridge?.IsBuffering.Value ?? false;

        int total = Math.Min(_tracks.Count, MaxTracks);
        int cols = Math.Clamp((int)MathF.Floor((width.Value + ColumnGap) / (MinItemWidth + ColumnGap)), 1, MaxColumns);
        int perPage = cols * Rows;
        int pages = Math.Max(1, (total + perPage - 1) / perPage);
        int pg = Math.Min(page.Value, pages - 1);

        Element Cell(int c, int r)                       // column-first: cell(r,c) = the (c*Rows+r)-th track on this page
        {
            int gi = pg * perPage + c * Rows + r;
            if (gi >= total) return new BoxEl { Grow = 1f, Basis = 0f };
            var t = _tracks[gi];
            int idx = gi;
            bool isNow = cur is not null && cur.Id == t.Id;
            var st = new TrackRow.State(isNow, isNow && playing, isNow && buffering, IsTop: false,
                                        Saved: t.Uri.Length > 0 && (lib?.IsSaved(t.Uri) ?? false));   // subscribe → heart re-skins on toggle
            return CompactTrack(
                t, st, go,
                onPlay: () => PlayRow(idx, t),
                onLike: t.Uri.Length > 0 ? () => lib?.ToggleSaved(t.Uri) : null,
                onPointerDown: p => dragX.Value = p.X,
                onDrag: Swipe);
        }

        var rowEls = new Element[Rows];
        for (int r = 0; r < Rows; r++)
        {
            var cells = new Element[cols];
            for (int c = 0; c < cols; c++) cells[c] = Cell(c, r);
            rowEls[r] = new BoxEl { Direction = 0, Gap = ColumnGap, Children = cells };
        }

        var header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Children =
            [
                ArtistPage.AccentHeader(_title) with { Grow = 1f, Basis = 0f },
                pages > 1 ? Pager(pg, pages, page) : new BoxEl(),
            ],
        };

        // Trackpad / touch / mouse horizontal swipe flips pages (FlipView-style). DragYieldsToPan lets a vertical drag
        // fall through to the page scroller while a horizontal drag pages here; a tap still reaches the track rows.
        void Swipe(Point2 p)
        {
            if (pages <= 1) return;
            float dx = p.X - dragX.Value;
            if (MathF.Abs(dx) < 56f) return;
            int np = Math.Clamp(pg + (dx < 0f ? 1 : -1), 0, pages - 1);
            if (np != page.Peek()) page.Value = np;
            dragX.Value = p.X;                           // re-anchor so a long drag can page again
        }

        return new BoxEl
        {
            Direction = 1, Gap = 20f,
            OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - width.Peek()) > 0.5f) width.Value = r.W; },
            Children = [ header, new BoxEl { Direction = 1, Gap = RowGap, Children = rowEls } ],
        };
    }

    // The skeleton shape the deriver walks (wired as the SkeletonProxy at the Embed.Comp site, since the deriver can't
    // run this component's Render): the real header + the real CompactTrack grid built from the (fake-data) tracks with
    // no-op handlers — so the shimmer matches the live top-tracks list instead of collapsing to one bar.
    public static Element SkeletonShape(IReadOnlyList<Track> tracks, string title)
    {
        int total = Math.Min(tracks.Count, MaxTracks);
        int cols = total > Rows ? 2 : 1;
        var rowEls = new Element[Rows];
        for (int r = 0; r < Rows; r++)
        {
            var cells = new Element[cols];
            for (int c = 0; c < cols; c++)
            {
                int gi = c * Rows + r;
                cells[c] = gi < total
                    ? CompactTrack(tracks[gi], new TrackRow.State(false, false, false, IsTop: false, Saved: false),
                                   static (_, _) => { }, static () => { }, null, static _ => { }, static _ => { })
                    : new BoxEl { Grow = 1f, Basis = 0f };
            }
            rowEls[r] = new BoxEl { Direction = 0, Gap = ColumnGap, Children = cells };
        }
        return new BoxEl
        {
            Direction = 1, Gap = 20f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                            Children = [ ArtistPage.AccentHeader(title) with { Grow = 1f, Basis = 0f } ] },
                new BoxEl { Direction = 1, Gap = RowGap, Children = rowEls },
            ],
        };
    }

    // Source-matched WinUI TrackItem Compact cell: transparent at rest, one-cell hover plate, 48px artwork,
    // title + explicit/video/artists + full play count, then heart and duration. Swipe input belongs to each cell,
    // rather than the shared band, so hovering one track cannot light every track at once.
    static Element CompactTrack(Track t, in TrackRow.State st, Action<string, string?> go,
                                Action onPlay, Action? onLike, Action<Point2> onPointerDown, Action<Point2> onDrag)
    {
        var metadata = new List<Element>(4);
        if (t.IsExplicit) metadata.Add(ExplicitBadge());
        if (t.HasVideo)
        {
            metadata.Add(new BoxEl
            {
                Opacity = 0.7f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Movie, 13f, Tok.TextTertiary)],
            });
            metadata.Add(new TextEl("·") { Size = 12f, Color = Tok.TextTertiary });
        }
        metadata.Add(TrackRow.ArtistLinks(t.Artists, go));

        Element artOverlay = st.IsBuffering
            ? TrackRow.Spinner()
            : new BoxEl
            {
                Width = 48f, Height = 48f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Fill = ColorF.FromRgba(0, 0, 0, (byte)(Tok.Theme == ThemeKind.Light ? 70 : 125)),
                Opacity = 0f, HoverOpacity = 1f, HoverDurationMs = 140f,
                Children = [Icon(st.IsNow && st.IsPlaying ? Icons.Pause : Icons.Play, 20f, ColorF.FromRgba(255, 255, 255))],
            };

        Element nowPlaying = st.IsNow
            ? new BoxEl
            {
                Grow = 1f, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                Padding = new Edges4(0f, 0f, 3f, 3f),
                Children =
                [
                    new BoxEl
                    {
                        Padding = new Edges4(2f, 2f, 2f, 2f), Corners = CornerRadius4.All(6f),
                        Fill = ColorF.FromRgba(0, 0, 0, 204),
                        Children = [WaveeEqualizer.Of(st.IsPlaying, Tok.AccentTextPrimary, 14f)],
                    },
                ],
            }
            : new BoxEl();

        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = ItemHeight,
            Gap = 12f, Padding = new Edges4(8f, 4f, 8f, 4f), AlignItems = FlexAlign.Center,
            Corners = CornerRadius4.All(6f), ClipToBounds = true,
            Fill = ColorF.Transparent, HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleSecondary,
            BorderWidth = 1f, BorderColor = ColorF.Transparent, HoverBorderColor = Tok.StrokeCardDefault,
            PressScale = 0.99f, Role = AutomationRole.Button, OnClick = onPlay,
            OnPointerDown = onPointerDown, OnDrag = onDrag, DragYieldsToPan = true,
            OnPointerExit = static () => { },
            Children =
            [
                new BoxEl
                {
                    Width = 48f, Height = 48f, Shrink = 0f, ZStack = true, ClipToBounds = true,
                    Corners = CornerRadius4.All(4f),
                    Children =
                    [
                        Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, 48f, 48f, 4f, decodePx: 64),
                        nowPlaying,
                        artOverlay,
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f, Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl(t.Title)
                        {
                            Size = 13f, Weight = 600, Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Children = metadata.ToArray() },
                        new TextEl($"{t.PlayCount:N0} plays")
                        {
                            Size = 10f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                TrackRow.Heart(st.Saved, onLike),
                new BoxEl
                {
                    Padding = new Edges4(6f, 0f, 6f, 0f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(DetailFormat.TrackTime(t.DurationMs)) { Size = 12f, Color = Tok.TextSecondary }],
                },
            ],
        };
    }

    static Element ExplicitBadge() => new BoxEl
    {
        MinWidth = 13f, Height = 13f, Padding = new Edges4(2f, 0f, 2f, 0f),
        Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = Tok.TextTertiary,
        Opacity = 0.6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl("E") { Size = 8f, Weight = 600, Color = Tok.TextTertiary }],
    };

    static Element Pager(int pg, int pages, Signal<int> page) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.XS, AlignItems = FlexAlign.Center,
        Children =
        [
            Chevron(Mdl.ChevronLeft, pg > 0, () => page.Value = pg - 1),
            new TextEl($"{pg + 1}/{pages}") { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
            Chevron(Mdl.ChevronRight, pg < pages - 1, () => page.Value = pg + 1),
        ],
    };

    static Element Chevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f), HoverFill = enabled ? Tok.FillSubtleSecondary : default,
        HoverScale = enabled ? 1.06f : 1f, OnClick = enabled ? onClick : null,
        Children = [ Icon(glyph, 12f, enabled ? Tok.TextSecondary : Tok.TextTertiary) ],
    };

    // Single click PLAYS this track in the artist context (so #1 here == #1 in the queue), or pauses/resumes it when it's
    // already the now-playing one — the same transport semantics as the detail list's row.
    void PlayRow(int i, Track t)
    {
        if (_bridge is not null && _bridge.CurrentTrack.Peek()?.Id == t.Id)
        {
            bool p = _bridge.IsPlaying.Peek();
            _bridge.IsPlaying.Value = !p;
            if (p) _ = _bridge.Player.PauseAsync(); else _ = _bridge.Player.ResumeAsync();
            return;
        }
        _ = _svc.Player.PlayAsync(_ctx, i);
    }
}
