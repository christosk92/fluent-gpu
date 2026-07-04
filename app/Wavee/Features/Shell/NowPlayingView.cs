using System;
using System.Collections.Generic;
using System.Linq;
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

// The full-screen NOW PLAYING view — the immersive expand of the player bar (Spotify's full now-playing). Big palette-tinted
// backdrop, large album art, title/artist + like, a big seek bar with times, the full transport row (shuffle/prev/play/
// next/repeat), a volume + device footer, and an "Up next" queue rail on wide windows. Reuses the player-bar building
// blocks (SeekBar, TimeText, Transport/Primary, the shuffle/repeat intents) so it stays in lockstep with the docked bar.
//
// Mounted as a top ZStack layer by WaveeShell, gated on bridge.Expanded. Opening animates in (compositor soft-reveal).
sealed class NowPlayingLayer : Component
{
    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        if (b is null) return new BoxEl();
        if (!b.Expanded.Value) return new BoxEl { HitTestVisible = false };   // closed → nothing, lets the shell through
        return Embed.Comp(() => new NowPlayingView(b));
    }
}

sealed class NowPlayingView : Component
{
    readonly PlaybackBridge _b;
    public NowPlayingView(PlaybackBridge b) => _b = b;

    public override Element Render()
    {
        this.UseSoftReveal();   // entrance (compositor-only, reduced-motion-aware)

        var viewport = UseContextSignal(Viewport.Size);
        var vp = viewport.Value;

        var track = _b.CurrentTrack.Value;
        var palette = _b.TrackPalette.Value;
        bool playing = _b.IsPlaying.Value;
        bool shuffle = _b.IsShuffle.Value;
        var repeat = _b.Repeat.Value;
        bool canNext = _b.CanSkipNext.Value;
        bool canPrev = _b.CanSkipPrev.Value;
        var lib = UseContext(LibraryBridge.Slot);
        bool liked = track is not null && (lib?.IsSaved(track.Uri) ?? false);
        // Seed fullscreen-lyrics from the one-shot flag: opened via the player-bar lyrics button when the rail didn't fit
        // (see PlayerBar / PlaybackBridge.ExpandedWithLyrics). Read at mount, then cleared below so a later normal reopen
        // seeds hero (false).
        var (showLyrics, setShowLyrics) = UseState(Diag.EnvFlag("WAVEE_LYRICS_FULLSCREEN") || _b.ExpandedWithLyrics.Peek());   // fullscreen lyrics mode (replaces the hero)
        UseEffect(() => { if (_b.ExpandedWithLyrics.Peek()) _b.ExpandedWithLyrics.Value = false; }, "");   // one-shot: clear once consumed at mount

        ColorF accent = palette is { } p0 ? WaveePalette.Accent(p0) : Tok.AccentDefault;
        ColorF bg = palette is { } p1 ? WaveePalette.BackgroundDark(p1) : WaveePalette.BackgroundDark(WaveePalette.Neutral);

        float artSize = Math.Clamp(MathF.Min(vp.Width * 0.30f, vp.Height * 0.42f), 160f, 440f);
        bool showQueueRail = vp.Width >= 1040f && track is not null && !showLyrics;   // lyrics mode is full-width centered (no queue beside)

        // ── top bar: collapse + context label ──────────────────────────────────────────────────────
        var topBar = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Height = 56f, Padding = new Edges4(12f, 8f, 16f, 8f),
            Children =
            [
                PlayerBarContent.Transport(Mdl.ChevronDown, () => _b.Expanded.Value = false, true, false, accent, 40f, 18f),
                new BoxEl
                {
                    Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Player.NowPlaying).ToUpperInvariant()) { Size = 11f, Weight = 700, Color = Tok.TextSecondary },
                        new TextEl(ContextLabel(_b.CurrentContext.Value)) { Size = 13f, Weight = 600, Color = Tok.TextPrimary },
                    ],
                },
                PlayerBarContent.Transport(Mdl.Lyrics, () => setShowLyrics(!showLyrics), track is not null, showLyrics, accent, 40f, 18f),
            ],
        };

        if (track is null)
        {
            // graceful empty state (kept openable even with nothing queued)
            var empty = new BoxEl
            {
                Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl(Loc.Get(Strings.Player.NothingPlaying)) { Size = 16f, Color = Tok.TextSecondary }],
            };
            return Shell(bg, [topBar, empty]);
        }

        // ── hero: big art, title, artist, like ─────────────────────────────────────────────────────
        var art = new BoxEl
        {
            Width = artSize, Height = artSize,
            Children = [Surfaces.Artwork(track.Image, SeedOf(track), artSize, artSize, 10f)],
        };

        var titleRow = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 12f, Width = artSize,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Direction = 1, Gap = 4f, ClipToBounds = true,
                    Children =
                    [
                        new TextEl(track.Title) { Size = 28f, Weight = 800, Color = Tok.TextPrimary, MaxLines = 2 },
                        new TextEl(string.Join(", ", track.Artists.Select(a => a.Name))) { Size = 15f, Weight = 500, Color = Tok.TextSecondary, MaxLines = 1 },
                    ],
                },
                PlayerBarContent.Transport(liked ? Mdl.HeartFill : Icons.Heart, () => lib?.ToggleSaved(track.Uri), true, liked, accent, 40f, 20f),
            ],
        };

        var hero = new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 28f,
            Padding = new Edges4(24f, 8f, 24f, 8f),
            Children = [art, titleRow],
        };

        // ── seek + times ───────────────────────────────────────────────────────────────────────────
        var seekRow = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, Width = MathF.Max(artSize, 420f),
            Children =
            [
                new BoxEl { Children = [Embed.Comp(() => new TimeText(_b, remaining: false))] },
                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [Embed.Comp(() => new SeekBar(_b))] },
                new BoxEl { Children = [Embed.Comp(() => new TimeText(_b, remaining: true))] },
            ],
        };

        // ── transport row ──────────────────────────────────────────────────────────────────────────
        var transportRow = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 18f,
            Children =
            [
                PlayerBarContent.Transport(Icons.Shuffle, () => PlayerBarContent.ToggleShuffle(_b), true, shuffle, accent, 40f, 18f),
                PlayerBarContent.Transport(Icons.Previous, () => { _ = _b.Player.PreviousAsync(); }, canPrev, false, accent, 44f, 22f),
                PlayerBarContent.Primary(playing ? Icons.Pause : Icons.Play, TogglePlay, true, accent, 64f, 30f),
                PlayerBarContent.Transport(Icons.Next, () => { _ = _b.Player.NextAsync(); }, canNext, false, accent, 44f, 22f),
                PlayerBarContent.Transport(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll,
                    () => PlayerBarContent.CycleRepeat(_b), true, repeat != RepeatMode.Off, accent, 40f, 18f),
            ],
        };

        // ── footer: device + volume ────────────────────────────────────────────────────────────────
        var footer = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 16f, Width = MathF.Max(artSize, 420f),
            Children =
            [
                Embed.Comp(() => new DevicesButton(_b, 36f, 16f, DevicePickerScope.NowPlaying)),
                new TextEl(Icons.Volume) { Size = 15f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
                Slider.Bind(_b.Volume, v => { _ = _b.Player.SetVolumeAsync(v); }, 160f, 16f),
            ],
        };

        // Fullscreen lyrics mode swaps the hero for the large, centered lyrics view (kept above the seek/transport).
        Element heroArea = showLyrics
            ? new BoxEl
            {
                Grow = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
                Children = [Embed.Comp(() => new LyricsView(large: true, visible: () => true))],
            }
            : hero;

        var centerCol = new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 22f,
            Padding = new Edges4(16f, 0f, 16f, 24f),
            Children = [heroArea, seekRow, transportRow, footer],
        };

        Element body = showQueueRail
            ? new BoxEl { Grow = 1f, Direction = 0, Children = [centerCol, QueueRail(track, lib)] }
            : centerCol;

        return Shell(bg, [topBar, body], CoverBackdrop(track, bg, palette, vp.Width, vp.Height));
    }

    // The "Up next" rail (wide windows): now-playing + the live queue, each row playing that track on click.
    readonly record struct QueueRailItem(string? Header, Track? Track);
    static readonly ColumnSet QueueCols = new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: false, Thumb: false);
    const float QueueItemExtent = 60f;

    Element QueueRail(Track current, LibraryBridge? lib)
    {
        var queue = _b.Queue.Value;
        var items = new List<QueueRailItem>(queue.Count + 3)
        {
            new(Loc.Get(Strings.Player.NowPlaying).ToUpperInvariant(), null),
            new(null, current),
        };
        bool headerDone = false;
        foreach (var e in queue)
        {
            if (e.Bucket == QueueBucket.NowPlaying) continue;
            if (!headerDone)
            {
                items.Add(new QueueRailItem(Loc.Get(Strings.Player.Queue).ToUpperInvariant(), null));
                headerDone = true;
            }
            items.Add(new QueueRailItem(null, e.Track));
        }

        var list = ItemsView.CreateBound(
            items.Count,
            scope => Embed.Comp(() => new QueueRailSlot(this, scope, items, lib)),
            RepeatLayout.Stack(QueueItemExtent),
            selectionMode: ItemsSelectionMode.Single,
            isItemInvokedEnabled: true,
            itemInvoked: i =>
            {
                if ((uint)i >= (uint)items.Count || items[i].Track is not { } t) return;
                TrackRow.Invoke(_b, t, () => _b.Player.PlayTrackAsync(t.Uri));
            },
            itemText: i => (uint)i < (uint)items.Count ? items[i].Track?.Title ?? items[i].Header ?? "" : "",
            isItemEnabled: i => (uint)i < (uint)items.Count && items[i].Track is not null,
            grow: 1f);
        return new BoxEl
        {
            Width = 360f, Shrink = 0f, Direction = 1, Padding = new Edges4(12f, 16f, 8f, 8f),
            Children =
            [
                new TextEl(Loc.Get(Strings.Player.Queue)) { Size = 16f, Weight = 700, Color = Tok.TextPrimary, Margin = new Edges4(4f, 0f, 0f, 8f) },
                new BoxEl { Grow = 1f, MinHeight = 0f, Children = [list] },
            ],
        };
    }

    sealed class QueueRailSlot : Component
    {
        readonly NowPlayingView _o;
        readonly RowScope _scope;
        readonly IReadOnlyList<QueueRailItem> _items;
        readonly LibraryBridge? _lib;
        public QueueRailSlot(NowPlayingView o, RowScope scope, IReadOnlyList<QueueRailItem> items, LibraryBridge? lib)
        { _o = o; _scope = scope; _items = items; _lib = lib; }

        public override Element Render()
        {
            int i = _scope.Index.Value;
            if ((uint)i >= (uint)_items.Count) return new BoxEl();
            var item = _items[i];
            if (item.Header is { Length: > 0 } header)
                return new TextEl(header) { Size = 11f, Weight = 700, Color = Tok.TextSecondary, Margin = new Edges4(8f, i == 0 ? 2f : 16f, 0f, 8f) };
            if (item.Track is not { } t) return new BoxEl();

            var st = TrackRow.StateOf(_o._b, _lib, t);
            var content = TrackRow.ArtCard(
                t, st, QueueCols, go: null,
                onPlay: () => TrackRow.Invoke(_o._b, t, () => _o._b.Player.PlayTrackAsync(t.Uri)),
                onLike: null,
                art: 40f,
                showArtists: true,
                explicitBadge: false,
                showDuration: false,
                kind: TrackRow.ArtCardKind.Rail);
            return SelectorVisualsBound.AccentPill(_scope, content);
        }
    }

    Element QueueRow(Track t, ColorF accent, bool isNow)
    {
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, Padding = new Edges4(8f, 6f, 8f, 6f),
            Corners = CornerRadius4.All(6f), Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
            OnClick = isNow ? null : () => { _ = _b.Player.PlayTrackAsync(t.Uri); },
            Children =
            [
                new BoxEl { Width = 40f, Height = 40f, Children = [Surfaces.Artwork(t.Image, SeedOf(t), 40f, 40f, 4f)] },
                new BoxEl
                {
                    Grow = 1f, Direction = 1, Gap = 2f, ClipToBounds = true,
                    Children =
                    [
                        new TextEl(t.Title) { Size = 13f, Weight = 600, Color = isNow ? accent : Tok.TextPrimary, MaxLines = 1 },
                        new TextEl(string.Join(", ", t.Artists.Select(a => a.Name))) { Size = 11f, Color = Tok.TextSecondary, MaxLines = 1 },
                    ],
                },
            ],
        };
    }

    void TogglePlay()
    {
        TrackRow.TogglePlayPause(_b);
    }

    // Full-bleed BELOW the title bar: the top 48px stays transparent + pass-through so the window caption (min/max/close +
    // drag) keeps working while the immersive view fills the rest.
    BoxEl Shell(ColorF bg, Element[] children, Element? backdrop = null) => new BoxEl
    {
        Grow = 1f, Direction = 1,
        Children =
        [
            new BoxEl { Height = TitleBar.ExpandedHeight, HitTestPassThrough = true },
            new BoxEl
            {
                Grow = 1f, ZStack = true, ClipToBounds = true, Fill = bg,
                Children = backdrop is null
                    ? [new BoxEl { Grow = 1f, Direction = 1, Children = children }]
                    : [backdrop, new BoxEl { Grow = 1f, Direction = 1, Children = children }],
            },
        ],
    };

    // BetterLyrics-style cover backdrop: a heavily-downscaled (→ soft/blurry when upscaled) full-bleed album cover, dimmed
    // under a dark scrim for text readability. Cheap — a ~96px decode upscaled to fill, no per-frame Gaussian-blur pass.
    Element CoverBackdrop(Track t, ColorF bg, Palette? palette, float vpW, float vpH) => new BoxEl
    {
        ZStack = true, Grow = 1f, ClipToBounds = true,
        Children =
        [
            new BoxEl
            {
                Grow = 1f, ClipToBounds = true, Opacity = 0.5f,
                Children = [Surfaces.Artwork(t.Image, SeedOf(t), MathF.Max(vpW, 200f), MathF.Max(vpH, 200f), 0f, decodePx: 96)],
            },
            new BoxEl { Grow = 1f, Fill = bg with { A = 0.5f } },
        ],
    };

    static int SeedOf(Track? t) => t is null ? 11 : Math.Abs((t.Uri ?? t.Id).Length * 7 + t.Title.Length);

    static string ContextLabel(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return Loc.Get(Strings.Player.NowPlaying);
        if (uri.Contains(":playlist:")) return "Playing from playlist";
        if (uri.Contains(":album:")) return "Playing from album";
        if (uri.Contains(":artist:")) return "Playing from artist";
        if (uri.Contains("collection")) return "Playing from Liked Songs";
        return Loc.Get(Strings.Player.NowPlaying);
    }
}
