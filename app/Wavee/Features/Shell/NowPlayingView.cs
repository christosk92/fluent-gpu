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

        ColorF accent = palette is { } p0 ? WaveePalette.Accent(p0) : Tok.AccentDefault;
        ColorF bg = palette is { } p1 ? WaveePalette.BackgroundDark(p1) : WaveePalette.BackgroundDark(WaveePalette.Neutral);

        float artSize = Math.Clamp(MathF.Min(vp.Width * 0.30f, vp.Height * 0.42f), 160f, 440f);
        bool showQueueRail = vp.Width >= 1040f && track is not null;

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
                new BoxEl { Width = 40f },   // balance the chevron so the label is truly centered
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
                Embed.Comp(() => new DevicesButton(_b, accent, 36f, 16f)),
                new TextEl(Icons.Volume) { Size = 15f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
                Slider.Bind(_b.Volume, v => { _ = _b.Player.SetVolumeAsync(v); }, 160f, 16f),
            ],
        };

        var centerCol = new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 22f,
            Padding = new Edges4(16f, 0f, 16f, 24f),
            Children = [hero, seekRow, transportRow, footer],
        };

        Element body = showQueueRail
            ? new BoxEl { Grow = 1f, Direction = 0, Children = [centerCol, QueueRail(accent, track)] }
            : centerCol;

        return Shell(bg, [topBar, body]);
    }

    // The "Up next" rail (wide windows): now-playing + the live queue, each row playing that track on click.
    Element QueueRail(ColorF accent, Track current)
    {
        var queue = _b.Queue.Value;
        var rows = new List<Element>(queue.Count + 2)
        {
            new TextEl(Loc.Get(Strings.Player.NowPlaying).ToUpperInvariant()) { Size = 11f, Weight = 700, Color = Tok.TextSecondary, Margin = new Edges4(4f, 2f, 0f, 8f) },
            QueueRow(current, accent, isNow: true),
        };
        bool headerDone = false;
        foreach (var e in queue)
        {
            if (e.Bucket == QueueBucket.NowPlaying) continue;
            if (!headerDone)
            {
                rows.Add(new TextEl(Loc.Get(Strings.Player.Queue).ToUpperInvariant()) { Size = 11f, Weight = 700, Color = Tok.TextSecondary, Margin = new Edges4(4f, 16f, 0f, 8f) });
                headerDone = true;
            }
            rows.Add(QueueRow(e.Track, accent, isNow: false));
        }

        var list = new BoxEl { Direction = 1, Gap = 2f, Padding = new Edges4(8f, 0f, 8f, 16f), Children = rows.ToArray() };
        return new BoxEl
        {
            Width = 360f, Shrink = 0f, Direction = 1, Padding = new Edges4(12f, 16f, 8f, 8f),
            Children =
            [
                new TextEl(Loc.Get(Strings.Player.Queue)) { Size = 16f, Weight = 700, Color = Tok.TextPrimary, Margin = new Edges4(4f, 0f, 0f, 8f) },
                new BoxEl { Grow = 1f, MinHeight = 0f, Children = [ScrollView(list)] },
            ],
        };
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
        bool p = _b.IsPlaying.Peek();
        _b.IsPlaying.Value = !p;
        if (p) _ = _b.Player.PauseAsync(); else _ = _b.Player.ResumeAsync();
    }

    BoxEl Shell(ColorF bg, Element[] children) => new BoxEl
    {
        Grow = 1f, Direction = 1, Fill = bg, ClipToBounds = true,
        Children = children,
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
