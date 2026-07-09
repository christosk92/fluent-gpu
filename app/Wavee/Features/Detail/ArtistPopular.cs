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
    readonly ColorF _accent;
    readonly ItemsViewController _ctl = new();
    readonly SelectionModel _sel = new();
    readonly Func<bool> _showChecks;
    FillRowVirtualLayout? _layout;
    int _touchpadSnapPage = -1;
    int _programmaticPage = -1;
    public ArtistPopular(IReadOnlyList<Track> tracks, string ctx, PlaybackBridge? bridge, Services svc, string title, ColorF accent)
    {
        _tracks = tracks; _ctx = ctx; _bridge = bridge; _svc = svc; _title = title; _accent = accent;
        _showChecks = () => { _ = _sel.Version.Value; return _sel.SelectedCount > 1; };   // 2+ only (a plain click must not summon checkboxes)
    }

    const int Rows = 4;          // WinUI ColumnsFirstGridLayout MaxRows
    const int MaxTracks = 10;
    const float ItemHeight = 68f;
    const float ItemGap = 6f;
    const float MinItemWidth = 280f;
    const float MaxItemWidth = 420f;
    const int MaxColumns = 3;
    static readonly ColumnSet TopCols = new(Album: false, By: false, Date: false, Video: true, Plays: true, Heart: true, Thumb: false);

    // WinUI ArtistPage: ColumnsFirstGridLayout, four 68px rows, 280px minimum cells, column-first pagination.

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var lib = UseContext(LibraryBridge.Slot);
        var width = UseSignal(600f);                     // self-measured → responsive column count
        var page = UseSignal(0);
        int total = Math.Min(_tracks.Count, MaxTracks);
        var layout = _layout ??= new FillRowVirtualLayout(MinItemWidth, MaxItemWidth, ItemGap, Rows);
        var (colsFit, cardW) = FillRowVirtualLayout.Fit(width.Value, MinItemWidth, MaxItemWidth, ItemGap);
        int perPage = Math.Max(1, colsFit * Rows);
        int pages = Math.Max(1, (total + perPage - 1) / perPage);
        int pg = Math.Min(page.Value, pages - 1);
        UseEffect(() => { if (page.Peek() > pages - 1) page.Value = pages - 1; }, pages);

        void GoTo(int to)
        {
            int target = Math.Clamp(to, 0, pages - 1);
            page.Value = target;
            _programmaticPage = target;
            _ctl.StartBringItemIntoView(target * perPage, 0f, animate: true);
        }

        float PageOffset(int p, in ScrollGeometry g)
        {
            float stride = MathF.Max(1f, (layout.CardW > 0f ? layout.CardW : cardW) + ItemGap);
            float maxOffset = MathF.Max(0f, g.ContentW - g.ViewportW);
            return Math.Clamp(p * colsFit * stride, 0f, maxOffset);
        }

        int PageOf(float offset, in ScrollGeometry g)
        {
            if (pages <= 1) return 0;

            float maxOffset = MathF.Max(0f, g.ContentW - g.ViewportW);
            if (maxOffset <= 0.5f) return 0;

            float x = Math.Clamp(offset, 0f, maxOffset);
            int best = 0;
            float bestDist = MathF.Abs(x - PageOffset(0, in g));
            for (int p = 1; p < pages; p++)
            {
                float dist = MathF.Abs(x - PageOffset(p, in g));
                if (dist <= bestDist)
                {
                    best = p;
                    bestDist = dist;
                }
            }
            return best;
        }

        long ScrollKey(ScrollGeometry g)
        {
            int p = PageOf(g.OffsetX, in g);
            int moving = (g.Flags & ScrollState.MovingNowBit) != 0 ? 1 : 0;
            return ((long)p << 1) | (uint)moving;
        }

        void OnScrollSettled(ScrollGeometry g)
        {
            int np = PageOf(g.OffsetX, in g);
            bool moving = (g.Flags & ScrollState.MovingNowBit) != 0;
            if (moving)
            {
                if (_programmaticPage >= 0) return;
                _touchpadSnapPage = np;
                if (np != page.Peek()) page.Value = np;
                return;
            }

            if (_programmaticPage >= 0)
            {
                _programmaticPage = -1;
                _touchpadSnapPage = -1;
                return;
            }

            int snapPage = _touchpadSnapPage >= 0 ? _touchpadSnapPage : np;
            _touchpadSnapPage = -1;
            if (snapPage != page.Peek()) page.Value = snapPage;
            if (pages <= 1) return;

            float target = PageOffset(snapPage, in g);
            if (MathF.Abs(g.OffsetX - target) <= 0.75f) return;
            _programmaticPage = snapPage;
            _ctl.StartBringItemIntoView(snapPage * perPage, 0f, animate: true);
        }

        var header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Children =
            [
                Surfaces.AccentHeader(_title, _accent) with { Grow = 1f, Basis = 0f },
                pages > 1 ? Pager(pg, pages, GoTo) : new BoxEl(),
            ],
        };

        var strip = ItemsView.CreateBound(
            total,
            scope => TrackRow.ArtCardSelectSkin(scope, Embed.Comp(() => new TopTrackSlot(this, scope, go, lib)), TrackRow.ArtCardKind.Grid, _showChecks)
                with
                {
                    // Enroll this horizontal ItemsView in the engine's scroll-flag pass. The bind writes identity, but
                    // lets ScrollGeometry.Flags carry MovingNowBit so snap alignment waits for touchpad/touch release.
                    ScrollBinds = [new ScrollBindDsl { From = ScrollChannel.Offset, To = BindSink.TransX, OutStart = 0f, OutEnd = 0f }],
                },
            RepeatLayout.Custom(layout, horizontal: true),
            selectionMode: ItemsSelectionMode.Extended,
            selection: _sel,
            isItemInvokedEnabled: true,
            itemInvoked: i =>
            {
                if ((uint)i >= (uint)total) return;
                var t = _tracks[i];
                TrackRow.Invoke(_bridge, t, () => _ = _svc.Player.PlayAsync(_ctx, i));
            },
            itemText: i => (uint)i < (uint)total ? _tracks[i].Title : "",
            controller: _ctl,
            overscan: 8,
            grow: 1f,
            suppressScrollBar: true,
            onScrollGeometryChanged: (ScrollKey, OnScrollSettled));

        var stripHost = new BoxEl { Height = Rows * ItemHeight + (Rows - 1) * ItemGap, ClipToBounds = true, Children = [strip] };
        return new BoxEl
        {
            Direction = 1, Gap = 20f,
            OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - width.Peek()) > 0.5f) width.Value = r.W; },
            Children =
            [
                header,
                ZStack(stripHost, Embed.Comp(() => new SelectionCommandBar(_sel,
                    i => (uint)i < (uint)Math.Min(_tracks.Count, MaxTracks) ? _tracks[i] : null,
                    bottomPadding: WaveeSpace.S))),
            ],
        };
    }

    sealed class TopTrackSlot : Component
    {
        readonly ArtistPopular _o;
        readonly RowScope _scope;
        readonly Action<string, string?> _go;
        readonly LibraryBridge? _lib;
        public TopTrackSlot(ArtistPopular o, RowScope scope, Action<string, string?> go, LibraryBridge? lib)
        { _o = o; _scope = scope; _go = go; _lib = lib; }

        public override Element Render()
        {
            int i = _scope.Index.Value;
            int total = Math.Min(_o._tracks.Count, MaxTracks);
            if ((uint)i >= (uint)total) return new BoxEl();
            var t = _o._tracks[i];
            var st = TrackRow.StateOf(_o._bridge, _lib, t);
            return TrackRow.ArtCard(
                t, st, TopCols, _go,
                onPlay: () => TrackRow.Invoke(_o._bridge, t, () => _ = _o._svc.Player.PlayAsync(_o._ctx, i)),
                onLike: t.Uri.Length > 0 ? () => _lib?.ToggleSaved(t.Uri, t.Title) : null,
                art: 48f,
                showArtists: true,
                explicitBadge: true,
                showDuration: true,
                kind: TrackRow.ArtCardKind.Grid);
        }
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
                    ? TrackRow.ArtCard(tracks[gi], new TrackRow.State(false, false, false, IsTop: false, Saved: false),
                                       TopCols, static (_, _) => { }, static () => { }, null,
                                       art: 48f, showArtists: true, explicitBadge: true, showDuration: true,
                                       kind: TrackRow.ArtCardKind.Grid)
                    : new BoxEl { Grow = 1f, Basis = 0f };
            }
            rowEls[r] = new BoxEl { Direction = 0, Gap = ItemGap, Children = cells };
        }
        return new BoxEl
        {
            Direction = 1, Gap = 20f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                            Children = [ Surfaces.AccentHeader(title, Tok.AccentDefault) with { Grow = 1f, Basis = 0f } ] },
                new BoxEl { Direction = 1, Gap = ItemGap, Children = rowEls },
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

    static Element Pager(int pg, int pages, Signal<int> page) => Pager(pg, pages, i => page.Value = i);

    static Element Pager(int pg, int pages, Action<int> goTo) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.XS, AlignItems = FlexAlign.Center,
        Children =
        [
            Chevron(Mdl.ChevronLeft, pg > 0, () => goTo(pg - 1)),
            new TextEl($"{pg + 1}/{pages}") { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
            Chevron(Mdl.ChevronRight, pg < pages - 1, () => goTo(pg + 1)),
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
