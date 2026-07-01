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

// Row 3 — the adaptive player bar (72px dock), the MUSA single-row layout: now-playing · transport+seek · controls, all
// on ONE centered row that progressively collapses with the viewport width. A full-bleed TOP EDGE is a 1px hairline at
// rest and an animated indeterminate sweep while Loading/Buffering (a browser-style global-activity cue, kept SEPARATE
// from the centre seek bar's actual position).
//
// Cost discipline (signals-first): the bar's Render reads only the LOW-frequency signals (track/play-state/shuffle/repeat/
// buffering/error/viewport) so it re-renders only on those. The HOT values never re-render the bar — the SEEK is a
// bespoke compositor-bound SeekBar sub-component (smooth interpolated playhead + scrub-gate), the VOLUME is Slider.Bind
// (compositor-only), and the time labels + volume glyph are isolated sub-components that re-render at ~1 Hz.
enum PlayerState : byte { NoTrack, Loading, Error, Active }

readonly record struct PlayerBarLayout(
    int Band,
    bool ShowExpand,
    bool ShowDevices,
    bool ShowQueue,
    bool ShowVolumeSlider,
    bool ShowShuffleRepeat,
    bool ShowLikeSlot,
    bool ShowVolumeButton,
    bool ShowTimesElapsed,
    bool ShowPrevNext,
    bool ShowSubtitle,
    float ButtonBox,
    float ButtonGlyph,
    float PrimaryBox,
    float PrimaryGlyph,
    float LeftW,
    float ArtSize,
    float RowGap,
    float RowPad,
    float ClusterGap,
    float LeftGap,
    float SeekGap,
    float RightGap,
    float TopEdgeWidth)
{
    public static PlayerBarLayout FromWidth(float w)
    {
        int band = w >= 1240f ? 15 :
                   w >= 1180f ? 14 :
                   w >= 1100f ? 13 :
                   w >= 1010f ? 12 :
                   w >= 900f  ? 11 :
                   w >= 860f  ? 10 :
                   w >= 760f  ? 9  :
                   w >= 700f  ? 8  :
                   w >= 680f  ? 7  :
                   w >= 620f  ? 6  :
                   w >= 560f  ? 5  :
                   w >= 540f  ? 4  :
                   w >= 520f  ? 3  :
                   w >= 440f  ? 2  :
                   w >= 400f  ? 1  : 0;

        return new PlayerBarLayout(
            Band: band,
            ShowExpand: w >= 1240f,
            ShowDevices: w >= 1180f,
            ShowQueue: w >= 1100f,
            ShowVolumeSlider: w >= 1010f,
            ShowShuffleRepeat: w >= 680f,
            ShowLikeSlot: w >= 760f,
            ShowVolumeButton: w >= 540f,
            ShowTimesElapsed: w >= 540f,
            ShowPrevNext: w >= 440f,
            ShowSubtitle: w >= 620f,
            ButtonBox: w >= 900f ? 32f : w >= 620f ? 30f : w >= 440f ? 28f : 26f,
            ButtonGlyph: w >= 620f ? 16f : w >= 440f ? 15f : 14f,
            PrimaryBox: w >= 760f ? 36f : w >= 560f ? 34f : 30f,
            PrimaryGlyph: w >= 760f ? 20f : w >= 560f ? 18f : 16f,
            LeftW: w >= 1180f ? 260f :
                   w >= 860f ? 210f :
                   w >= 680f ? 172f :
                   w >= 520f ? 132f :
                   w >= 400f ? 96f : 44f,
            ArtSize: w >= 680f ? WaveeSize.ArtPlayerBar : 40f,
            RowGap: w >= 900f ? 8f : w >= 620f ? 6f : w >= 440f ? 4f : 3f,
            RowPad: w >= 700f ? 12f : w >= 520f ? 8f : 6f,
            ClusterGap: w >= 760f ? 4f : w >= 520f ? 3f : 2f,
            LeftGap: w >= 700f ? 8f : 6f,
            SeekGap: w >= 760f ? 6f : w >= 520f ? 5f : 4f,
            RightGap: w >= 760f ? 2f : w >= 520f ? 1f : 0f,
            TopEdgeWidth: 2400f);
    }
}

sealed class PlayerBar : ReactiveComponent
{
    static readonly bool DiagEnabled = Diag.EnvFlag("WAVEE_PLAYERBAR_DIAG");

    public override Element Setup()
    {
        var viewport = UseContextSignal(Viewport.Size);
        var layout = UseSignal(PlayerBarLayout.FromWidth(viewport.Peek().Width));

        UseSignalEffect(() =>
        {
            var next = PlayerBarLayout.FromWidth(viewport.Value.Width);
            var prev = layout.Peek();
            if (next.Equals(prev)) return;
            if (DiagEnabled)
                Console.Error.WriteLine($"[WAVEE_PLAYERBAR_DIAG] layout-band {prev.Band}->{next.Band} viewportW={viewport.Peek().Width:0.0}");
            layout.Value = next;
        });

        return Embed.Comp(() => new PlayerBarContent(layout));
    }
}

sealed class PlayerBarContent : Component
{
    readonly IReadSignal<PlayerBarLayout> _layout;
    static readonly bool DiagEnabled = Diag.EnvFlag("WAVEE_PLAYERBAR_DIAG");
    static int s_renderCount;

    public PlayerBarContent(IReadSignal<PlayerBarLayout> layout) => _layout = layout;

    static readonly LayoutTransition MoveMotion = new(
        TransitionChannels.Bounds,
        TransitionDynamics.Tween(Motion.ControlFast, Easing.FluentPopOpen),
        SizeMode.Reveal);

    // WinUI AddDeleteThemeTransition: the entrant FADES in (opacity, OS decelerate curve) AND its layout width eases
    // 0→natural so the NEIGHBOURS reflow — they slide over to make room instead of snapping (the WinUI "make room"
    // feel). SizeMode.Reflow routes through RunReflowLayout, which is NOT resize-gated, so this animates even while the
    // window is dragged across a breakpoint (where the FLIP/projection path stays suppressed by design). ~250ms in.
    static readonly LayoutTransition ItemMotion = new(
        TransitionChannels.Bounds | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Motion.ControlNormal, Easing.SmoothOut),
        SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Motion.ControlFast, Easing.SmoothOut));

    // A soft critical red for the Error state's title (app-local; the chrome stays WinUI-faithful elsewhere).
    static readonly ColorF Critical = new(0.93f, 0.42f, 0.45f, 1f);
    // Shared marquee cadence for the now-playing title + subtitle: a FIXED cycle (vs constant speed) so the two lines
    // advance through the same fraction of their own text in lockstep and reset together — i.e. they read as synced.
    const float MarqueeCycleMs = 12000f;
    // Dark glyph for the WHITE primary play/pause face (the musa/Spotify treatment — a white circle, dark icon).
    // Thin volume rail — the WinUI Slider trimmed to a 4px track + a small 12px thumb so it reads as a level line.
    static readonly Slider.Style RailStyle = Slider.DefaultStyle with
    {
        TrackHeight = 4f, TrackCornerRadius = 2f, ThumbRingDiameter = 12f, InnerThumbDiameter = 8f, ThumbCornerRadius = 6f,
    };

    public override Element Render()
    {
        // Hooks FIRST (stable call order — rule #7), before any early return.
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);    // Mutations: the now-playing like reflects + toggles the saved-set
        var go = UseContext(HistoryStore.NavCtx);    // navigate to the now-playing album / artist on click
        var ui = UseContext(ShellUi.Slot);           // right-rail (lyrics) toggle state
        var titleHover = UseSignal(false);           // hover the now-playing text → BOTH lines scroll together (synced); idle = static + edge fade
        var L = _layout.Value;                       // coarse breakpoint signal; does NOT change for every resize pixel
        if (DiagEnabled)
            Console.Error.WriteLine($"[WAVEE_PLAYERBAR_DIAG] render #{++s_renderCount} band={L.Band}");

        if (b is null)
            return new BoxEl { Height = WaveeSize.PlayerBarH, Fill = WaveeColors.PlayerBar };

        // ── state derivation (low-frequency signals only) ──────────────────────────────────────────
        var track = b.CurrentTrack.Value;
        bool liked = track is not null && (lib?.IsSaved(track.Uri) ?? false);   // subscribe → the heart re-skins on a like toggle / track change
        string? err = b.Error.Value;
        bool loading = b.IsLoading.Value;
        bool buffering = b.IsBuffering.Value;
        bool playing = b.IsPlaying.Value;
        bool shuffle = b.IsShuffle.Value;
        var repeat = b.Repeat.Value;
        bool hasVideo = b.CurrentTrackHasVideo.Value;   // async-detected music video (VideoService) for the now-playing track
        bool preferVideo = b.PreferVideo.Value;          // UI-only swap toggle; the video surface/host is a follow-up
        var accent = Tok.AccentDefault;

        PlayerState st =
            err is not null ? PlayerState.Error :
            track is null ? PlayerState.NoTrack :
            loading ? PlayerState.Loading :
                            PlayerState.Active;
        bool active = st == PlayerState.Active;
        bool canTransport = active || buffering;     // buffering still lets you pause / skip
        var remoteDevice = active ? RemoteDevice(b) : null;
        // Primary is live for Active/Buffering and for Error (a retry); dead only for NoTrack/Loading.
        bool primaryEnabled = st != PlayerState.NoTrack && st != PlayerState.Loading;
        // (SeekBar derives its own enabled state reactively from the bridge signals — see SeekBar.Render.)

        // ── responsive breakpoints (coarse layout signal; no raw viewport subscription in this component) ──
        // Below 440 ⇒ only art+title, Primary, SeekBar. Play + SeekBar ALWAYS render at every width.
        bool showExpand = L.ShowExpand;
        bool showDevices = L.ShowDevices;
        bool showQueue = L.ShowQueue;
        bool showVolumeSlider = L.ShowVolumeSlider;
        bool showShuffleRepeat = L.ShowShuffleRepeat;
        bool showLike = L.ShowLikeSlot && active;
        bool showVolumeButton = L.ShowVolumeButton;
        bool showTimesRemaining = true;            // duration is a priority; keep it before secondary controls
        bool showTimesElapsed = L.ShowTimesElapsed;
        bool showPrevNext = L.ShowPrevNext;
        bool showLeft = true;
        bool showArtwork = true;                   // album art never disappears
        bool showSubtitle = L.ShowSubtitle;
        float buttonBox = L.ButtonBox;
        float buttonGlyph = L.ButtonGlyph;
        float primaryBox = L.PrimaryBox;
        float primaryGlyph = L.PrimaryGlyph;
        float leftW = L.LeftW;
        float artSize = L.ArtSize;
        float rowGap = L.RowGap;
        float rowPad = L.RowPad;
        float clusterGap = L.ClusterGap;

        // ── LEFT — now playing ─────────────────────────────────────────────────────────────────────
        // Title/subtitle/colour are bound REACTIVELY off the bridge (Prop thunks), not passed as frozen strings: the
        // Marquee is an autonomous component, so a parent re-render does not push new constructor args into it — the
        // thunk (captured once, reading the stable bridge's signals) is what keeps the inner TextEl re-measuring and
        // repainting as the track changes. Passing the strings directly would freeze them at the first-mount value.
        // Both lines scroll only on hover (TriggerMode.Hover) and share ONE gate (titleHover) driven by the meta column
        // below — so they start together and stay phase-locked (CycleMs), instead of each toggling under its own line.
        // The edge fade is unaffected: a right-edge "there's more" cue at rest, both edges while scrolling.
        // Now-playing is clickable: art + title → the album; the subtitle splits into an artist link + an album link.
        var npAlbum = track?.Album;
        bool albumNav = npAlbum is { Uri.Length: > 0 };
        void NavAlbum() { if (albumNav) go?.Invoke("album:" + npAlbum!.Uri, npAlbum.Name); }

        var titleLinkHover = UseSignal(false);
        bool titleHot = albumNav && titleLinkHover.Value;
        Element titleEl = Marquee.Of(Prop.Of(() => NowPlaying(b).Title),
            new Marquee.Style { FontSize = 14f, Weight = 700, Foreground = Prop.Of(() => titleHot ? Tok.AccentTextPrimary : NowPlaying(b).Color), CycleMs = MarqueeCycleMs, Trigger = Marquee.TriggerMode.Hover },
            scrollWhen: titleHover);
        if (albumNav)   // the title opens the album (click); hover still scrolls the marquee (the metaCol drives titleHover)
            titleEl = new BoxEl
            {
                MinWidth = 0f, Grow = 1f, Shrink = 1f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
                Cursor = CursorId.Hand, OnClick = NavAlbum,
                OnHoverMove = _ =>
                {
                    if (!titleLinkHover.Peek()) titleLinkHover.Value = true;
                    if (!titleHover.Peek()) titleHover.Value = true;
                },
                OnPointerExit = () => { if (titleLinkHover.Peek()) titleLinkHover.Value = false; },
                Role = AutomationRole.Hyperlink, Focusable = true,
                Children = [titleEl],
            };

        var metaKids = new List<Element>(remoteDevice is null ? 2 : 3);
        if (remoteDevice is not null)
            metaKids.Add(new BoxEl
            {
                Key = "remote-device-line", Animate = ItemMotion,
                Children = [Embed.Comp(() => new RemoteDeviceLine(b))],
            });
        metaKids.Add(titleEl);
        if (showSubtitle && track is not null && err is null)
        {
            var subKids = new List<Element>(Math.Max(3, track.Artists.Count * 2 + 2));
            AddArtistLinks(subKids, track.Artists, go);
            if (subKids.Count > 0)
                metaKids.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true, Children = subKids.ToArray() });
        }

        var metaCol = new BoxEl
        {
            Key = "meta", Animate = MoveMotion,
            Direction = 1, Grow = 1f, Shrink = 1f, Gap = 2f, Justify = FlexJustify.Center, ClipToBounds = true,
            // The shared hover target: the title marquee reads titleHover, so this column is the deepest pointer target
            // over the now-playing text → hovering anywhere on it scrolls the title.
            OnHoverMove = _ => { if (!titleHover.Peek()) titleHover.Value = true; },
            OnPointerExit = () => { if (titleHover.Peek()) titleHover.Value = false; },
            Children = metaKids.ToArray(),
        };

        var leftKids = new List<Element>(3);
        if (showArtwork)
            leftKids.Add(new BoxEl
            {
                Key = "art", Width = artSize, Height = artSize, Animate = ItemMotion,
                Cursor = albumNav ? CursorId.Hand : (CursorId?)null,
                OnClick = albumNav ? NavAlbum : null,   // album art → the album
                Children = [Surfaces.Artwork(track?.Image, SeedOf(track), artSize, artSize, 6f)]
            });
        leftKids.Add(metaCol);
        if (showLike)
            leftKids.Add(Transport(liked ? Mdl.HeartFill : Icons.Heart, () => { if (track is { } lt) lib?.ToggleSaved(lt.Uri); }, true, liked, accent, MathF.Min(30f, buttonBox), 15f)
                with { Key = "like", Animate = ItemMotion });

        var left = new BoxEl
        {
            Key = "left", Width = leftW, Shrink = 0f, MinWidth = 0f, Animate = MoveMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = L.LeftGap, ClipToBounds = true, Children = leftKids.ToArray(),
        };

        // ── CENTRE — transport group + seek (the SeekBar Grows to fill the remaining center width) ───
        var transport = new List<Element>(3);
        if (showPrevNext)
            transport.Add(Transport(Icons.Previous, () => { _ = b.Player.PreviousAsync(); }, canTransport, false, accent, buttonBox, buttonGlyph)
                with { Key = "prev", Animate = ItemMotion });
        transport.Add(Primary(
            st == PlayerState.Error ? Icons.Play : playing ? Icons.Pause : Icons.Play,
            () => PrimaryClick(b, st), primaryEnabled, accent, primaryBox, primaryGlyph)
            with { Key = "primary", Animate = MoveMotion });
        if (showPrevNext)
            transport.Add(Transport(Icons.Next, () => { _ = b.Player.NextAsync(); }, canTransport, false, accent, buttonBox, buttonGlyph)
                with { Key = "next", Animate = ItemMotion });

        var transportGroup = new BoxEl
        {
            Key = "transport", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Gap = 0f, Animate = MoveMotion, Children = transport.ToArray(),
        };

        // Stable Keys so keyed matching preserves SeekBar identity across the 440 breakpoint (all three children are
        // ComponentEl/ElementTypeId 3 — without keys, dropping the elapsed label shifts SeekBar to index 0 where it is
        // matched against the old TimeText node, a ComponentType mismatch that REMOUNTS SeekBar, losing its scrub state /
        // interpolation anchor / cached width on resize-during-playback).
        var seekKids = new List<Element>(3);
        if (showTimesElapsed)
            seekKids.Add(new BoxEl { Key = "elapsed", Animate = ItemMotion, Children = [Embed.Comp(() => new TimeText(b, remaining: false))] });
        seekKids.Add(new BoxEl { Key = "seek", Grow = 1f, Shrink = 1f, MinWidth = 0f, Animate = MoveMotion, Children = [Embed.Comp(() => new SeekBar(b))] });
        if (showTimesRemaining)
            seekKids.Add(new BoxEl { Key = "remaining", Animate = ItemMotion, Children = [Embed.Comp(() => new TimeText(b, remaining: true))] });

        var seekRow = new BoxEl
        {
            Key = "seek-row", Direction = 0, Grow = 1f, Shrink = 1f, AlignItems = FlexAlign.Center,
            Gap = L.SeekGap, Animate = MoveMotion, Children = seekKids.ToArray(),
        };

        var centre = new BoxEl
        {
            Key = "centre", Grow = 1f, Shrink = 1f, MinWidth = 0f, Animate = MoveMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = clusterGap,
            Children = [transportGroup, seekRow],
        };

        // ── RIGHT — shuffle/repeat · volume · queue/devices/expand ───────────────────────────────────
        var overflowCommands = new List<AppBarCommand>(8);
        if (!showPrevNext)
        {
            overflowCommands.Add(new AppBarCommand(Icons.Previous, Loc.Get(Strings.Player.Previous), () => { _ = b.Player.PreviousAsync(); }, Enabled: canTransport));
            overflowCommands.Add(new AppBarCommand(Icons.Next, Loc.Get(Strings.Player.Next), () => { _ = b.Player.NextAsync(); }, Enabled: canTransport));
        }
        if (!showShuffleRepeat)
        {
            overflowCommands.Add(new AppBarCommand(Icons.Shuffle, Loc.Get(Strings.Player.Shuffle), () => ToggleShuffle(b), AppBarCommandKind.ToggleButton, shuffle, canTransport));
            overflowCommands.Add(new AppBarCommand(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll, Loc.Get(Strings.Player.Repeat), () => CycleRepeat(b), AppBarCommandKind.ToggleButton, repeat != RepeatMode.Off, canTransport));
        }
        if (!showLike && active)
            overflowCommands.Add(new AppBarCommand(liked ? Mdl.HeartFill : Icons.Heart, Loc.Get(Strings.Player.Like), () => { if (track is { } lt) lib?.ToggleSaved(lt.Uri); }, AppBarCommandKind.ToggleButton, liked, true));
        // In the small-window overflow, Queue / Devices / Now Playing all open the full now-playing view (where those
        // controls live — the queue rail, the device picker, the big transport).
        if (!showQueue)
            overflowCommands.Add(new AppBarCommand(Icons.Queue, Loc.Get(Strings.Player.Queue), () => { b.Expanded.Value = true; }, Enabled: canTransport));
        if (!showDevices)
            overflowCommands.Add(new AppBarCommand(Icons.Devices, Loc.Get(Strings.Player.Devices), () => { b.Expanded.Value = true; }, Enabled: canTransport));
        if (!showExpand)
            overflowCommands.Add(new AppBarCommand(Icons.ChevronUp, Loc.Get(Strings.Player.NowPlaying), () => { b.Expanded.Value = true; }, Enabled: canTransport));

        var rightKids = new List<Element>(8);
        if (showShuffleRepeat)
        {
            rightKids.Add(Transport(Icons.Shuffle, () => ToggleShuffle(b), canTransport, shuffle, accent, buttonBox, buttonGlyph)
                with { Key = "shuffle", Animate = ItemMotion });
            rightKids.Add(Transport(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll,
                () => CycleRepeat(b), canTransport, repeat != RepeatMode.Off, accent, buttonBox, buttonGlyph)
                with { Key = "repeat", Animate = ItemMotion });
        }
        // The mute glyph is SECONDARY: drop it below 440 so the <440 row is center-controls-only (play/pause + seek),
        // per the responsive table. The slider→icon collapse above 440 is already handled by showVolumeSlider.
        if (showVolumeButton)
            rightKids.Add(new BoxEl
            {
                Key = "volume", Animate = ItemMotion,
                Children =
                [
                    Embed.Comp(() => new VolumeButton(b, !showVolumeSlider, buttonBox, buttonGlyph))
                        with { Key = (showVolumeSlider ? "volume-inline-" : "volume-popup-") + buttonBox + "-" + buttonGlyph }
                ]
            });
        if (showVolumeSlider)
            rightKids.Add(Slider.Bind(b.Volume, v => { _ = b.Player.SetVolumeAsync(v); }, 96f, 16f, RailStyle) with { Key = "volume-slider", Animate = ItemMotion });
        if (ui is not null && active)
            rightKids.Add(Transport(Mdl.Lyrics, () => ui.Toggle(RailMode.Lyrics), true,
                ui.RailOpen.Value && ui.Mode.Value == RailMode.Lyrics, accent, buttonBox, buttonGlyph)
                with { Key = "lyrics", Animate = ItemMotion });
        // Switch-to-video toggle — shown only when the now-playing track has a music video (async-detected). The swap is a
        // seam for now (sets PreferVideo); the actual video surface/host is a follow-up. Tooltip explains the affordance.
        if (active && hasVideo)
            rightKids.Add(ToolTip.Wrap(
                Transport(Icons.Movie, () => { b.PreferVideo.Value = !b.PreferVideo.Value; }, true, preferVideo, accent, buttonBox, buttonGlyph),
                Loc.Get(preferVideo ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo))
                with { Key = "video" });
        if (showQueue)
            rightKids.Add(Embed.Comp(() => new QueueButton(b, accent, buttonBox, buttonGlyph)) with { Key = "queue" });
        if (showDevices)
            rightKids.Add(Embed.Comp(() => new DevicesButton(b, accent, buttonBox, buttonGlyph)) with { Key = "devices" });
        if (showExpand)
            rightKids.Add(Transport(Icons.ChevronUp, () => { b.Expanded.Value = true; }, canTransport, false, accent, buttonBox, buttonGlyph)
                with { Key = "expand", Animate = ItemMotion });
        rightKids.Add(MoreButton(overflowCommands, buttonBox, buttonGlyph));

        var right = new BoxEl
        {
            Key = "right", Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Gap = L.RightGap, Animate = MoveMotion,
            Children = rightKids.ToArray(),
        };

        // ── assemble: top activity edge + the single centered row ───────────────────────────────────
        Element topEdge = (loading || buffering)
            ? ProgressBar.Indeterminate(L.TopEdgeWidth)
            : new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault };

        var rowKids = new List<Element>(3);
        if (showLeft) rowKids.Add(left);
        rowKids.Add(centre);
        if (rightKids.Count > 0) rowKids.Add(right);

        var row = new BoxEl
        {
            Key = "player-row", Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = rowGap,
            Padding = new Edges4(rowPad, 0f, rowPad, 0f), Animate = MoveMotion,
            Children = rowKids.ToArray(),
        };

        return new BoxEl
        {
            Direction = 1, Height = WaveeSize.PlayerBarH, Fill = WaveeColors.PlayerBar, ClipToBounds = true,
            Children = [topEdge, row],
        };
    }

    // ── intents (optimistic: write the signal first so the UI is instant, then the bridge reconciles) ──
    internal static void ToggleShuffle(PlaybackBridge b)
    {
        bool s = b.IsShuffle.Peek(); b.IsShuffle.Value = !s; _ = b.Player.SetShuffleAsync(!s);
    }

    internal static void CycleRepeat(PlaybackBridge b)
    {
        var r = b.Repeat.Peek();
        var next = r == RepeatMode.Off ? RepeatMode.Context : r == RepeatMode.Context ? RepeatMode.Track : RepeatMode.Off;
        b.Repeat.Value = next; _ = b.Player.SetRepeatAsync(next);
    }

    static void PrimaryClick(PlaybackBridge b, PlayerState st)
    {
        if (st == PlayerState.Error) { b.Error.Value = null; _ = b.Player.ResumeAsync(); return; }
        if (st is PlayerState.NoTrack or PlayerState.Loading) return;
        bool p = b.IsPlaying.Peek();
        b.IsPlaying.Value = !p;
        if (p) _ = b.Player.PauseAsync(); else _ = b.Player.ResumeAsync();
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────
    // The now-playing title + its colour, derived LIVE from the bridge (same state machine as PlayerState). Called from
    // a Prop thunk so the Marquee's bound TextEl re-measures/repaints when the track or play-state changes — see metaKids.
    static (string Title, ColorF Color) NowPlaying(PlaybackBridge b)
    {
        var track = b.CurrentTrack.Value;
        string? err = b.Error.Value;
        bool loading = b.IsLoading.Value;
        PlayerState st = err is not null ? PlayerState.Error
                       : track is null ? PlayerState.NoTrack
                       : loading ? PlayerState.Loading
                       : PlayerState.Active;
        return st switch
        {
            PlayerState.NoTrack => (Loc.Get(Strings.Player.NothingPlaying), Tok.TextSecondary),
            PlayerState.Loading => (track?.Title ?? Loc.Get(Strings.Player.Loading), Tok.TextPrimary),
            PlayerState.Error   => (err ?? Loc.Get(Strings.Player.CannotPlay), Critical),
            _                   => (track?.Title ?? "", Tok.TextPrimary),
        };
    }

    static string ArtistsOf(Track? t) => t is null ? "" : string.Join(", ", t.Artists.Select(a => a.Name));

    static int SeedOf(Track? t) => t is null ? 11 : Math.Abs((t.Uri ?? t.Id).Length * 7 + t.Title.Length);

    internal static PlaybackDevice? RemoteDevice(PlaybackBridge b)
    {
        var devices = b.Devices.Value;
        string? activeId = b.ActiveDeviceId.Value;
        PlaybackDevice? active = null;

        if (!string.IsNullOrEmpty(activeId))
            active = devices.FirstOrDefault(d => d.Id == activeId);
        active ??= devices.FirstOrDefault(d => d.IsActive);

        return active is { Kind: not DeviceKind.ThisDevice } ? active : null;
    }

    internal static List<MenuFlyoutItem> DeviceMenuItems(PlaybackBridge b, IReadOnlyList<PlaybackDevice> devices, string? activeId)
    {
        var items = new List<MenuFlyoutItem>(Math.Max(1, devices.Count));
        if (devices.Count == 0)
        {
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Player.Devices), null, false, () => { }));
            return items;
        }

        foreach (var d in devices)
        {
            var dev = d;
            bool isActive = dev.Id == activeId || dev.IsActive;
            items.Add(MenuFlyoutItem.Toggle(dev.Name, isActive,
                () => { _ = b.DeviceControl.TransferAsync(dev.Id); }, Icons.Devices, enabled: true));
        }
        return items;
    }

    internal static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        long total = ms / 1000, m = total / 60, s = total % 60;
        return m.ToString() + ":" + (s < 10 ? "0" + s : s.ToString());
    }

    static void AddArtistLinks(List<Element> into, IReadOnlyList<ArtistRef> artists, Action<string, string?>? go)
    {
        for (int i = 0; i < artists.Count; i++)
        {
            var a = artists[i];
            if (a.Name.Length == 0) continue;
            if (into.Count > 0) into.Add(new TextEl(", ") { Size = 12f, Color = Tok.TextSecondary });
            bool enabled = a.Uri.Length > 0;
            into.Add(NavSpan(a.Name, () => { if (enabled) go?.Invoke("artist:" + a.Uri, a.Name); }, enabled)
                with { Key = "artist:" + (a.Uri.Length > 0 ? a.Uri : a.Id + ":" + a.Name) });
        }
    }

    // A clickable now-playing meta link (artist / album). It drives its own foreground because TextEl.HoverColor follows
    // the engine's ancestor hover path too, which makes the album look hovered when the pointer is over the title line.
    static Element NavSpan(string text, Action onClick, bool enabled)
        => Embed.Comp(() => new NowPlayingMetaLink(text, onClick, enabled));

    sealed class NowPlayingMetaLink : Component
    {
        readonly string _text;
        readonly Action _onClick;
        readonly bool _enabled;

        public NowPlayingMetaLink(string text, Action onClick, bool enabled)
        {
            _text = text;
            _onClick = onClick;
            _enabled = enabled;
        }

        public override Element Render()
        {
            var hover = UseSignal(false);
            return new BoxEl
            {
                Cursor = _enabled ? CursorId.Hand : (CursorId?)null,
                OnClick = _enabled ? _onClick : null,
                OnHoverMove = _enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null,
                OnPointerExit = _enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                ClipToBounds = true,
                Role = _enabled ? AutomationRole.Hyperlink : AutomationRole.Text,
                Children = [new TextEl(_text) { Size = 12f, Color = hover.Value ? Tok.TextPrimary : Tok.TextSecondary }],
            };
        }
    }

    /// <summary>A transport TOGGLE/command glyph button. The active state is shown by an ACCENT glyph + a 3px accent dot
    /// under it — never a filled background (that's reserved for the hover/press interaction axis). Box never fills at
    /// rest; hover/press use the WinUI subtle fills, the glyph carries the foreground ramp + AnimatedIcon-style scale.</summary>
    internal static BoxEl Transport(string glyph, Action onClick, bool enabled, bool active, ColorF accent, float box = 36f, float glyphSize = 16f, Action<NodeHandle>? onRealized = null)
    {
        ColorF fg = !enabled ? Tok.TextDisabled : active ? accent : Tok.TextSecondary;
        ColorF hover = !enabled ? Tok.TextDisabled : active ? accent : Tok.TextPrimary;
        var glyphLayer = new BoxEl
        {
            Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Theme.IconFont, Color = fg, HoverColor = hover }],
        };
        var dotLayer = new BoxEl
        {
            Width = box, Height = 3f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            AlignSelf = FlexAlign.End, Margin = new Edges4(0f, 0f, 0f, 3f), HitTestVisible = false,
            Children = [new BoxEl { Width = 3f, Height = 3f, Corners = CornerRadius4.All(1.5f), Fill = accent, Opacity = active ? 1f : 0f }],
        };
        return new BoxEl
        {
            Width = box, Height = box, ZStack = true,
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            HoverScale = enabled ? 1.06f : 1f, PressScale = enabled ? 0.92f : 1f,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            OnRealized = onRealized,
            IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            Children = [glyphLayer, dotLayer],
        };
    }

    internal static Element MoreButton(IReadOnlyList<AppBarCommand> commands, float box, float glyphSize)
    {
        // Open the overflow as a PLAIN MenuFlyout through the overlay service (the same path the toolbar "⋯" uses) so it
        // gets the engine's clean MenuPopupThemeTransition clip-reveal. We do NOT use CommandBarFlyout here: it layers
        // its own overflow-expand clip on top of the OverlayHost reveal, which made the chrome pop empty then fill in
        // (two out-of-sync clips → the "ugly open"). Toggle commands become E73E-checked ToggleMenuFlyoutItems.
        var items = new List<MenuFlyoutItem>(commands.Count);
        int vh = commands.Count;
        for (int i = 0; i < commands.Count; i++)
        {
            var c = commands[i];
            items.Add(c.Kind == AppBarCommandKind.ToggleButton
                ? MenuFlyoutItem.Toggle(c.Label, c.IsChecked, c.Invoke, c.Glyph, c.Enabled)
                : new MenuFlyoutItem(c.Label, c.Glyph, c.Enabled, c.Invoke));
            // Cheap, alloc-light version: the command SET only changes at breakpoint crossings / toggle flips (not per
            // resize pixel), so fold a hash so the menu component re-mounts with fresh rows when the set actually changes.
            vh = vh * 31 + (c.Glyph?.GetHashCode() ?? 0);
            vh = vh * 31 + (c.Label?.GetHashCode() ?? 0);
            if (c.IsChecked) vh ^= 0x55555555;
            if (c.Enabled) vh ^= 0x0F0F0F0F;
        }
        string version = "more#" + vh;
        return new BoxEl
        {
            Key = "more", Width = box, Height = box, Animate = ItemMotion,
            Children = [Embed.Comp(() => new PlayerMoreMenu(items, box, glyphSize)) with { Key = version }],
        };
    }

    // The player-bar "⋯" overflow: opens a plain MenuFlyout UPWARD out of the button (TopEdgeAlignedRight) via the
    // overlay service, so it gets the engine's MenuPopupThemeTransition clip-reveal (grows up from the anchor edge) —
    // the same clean open as the toolbar overflow. Replaces the old CommandBarFlyout whose extra overflow-expand clip
    // fought the reveal (empty-then-fill flash) and read darker/heavier than a WinUI MenuFlyout.
    sealed class PlayerMoreMenu : Component
    {
        readonly IReadOnlyList<MenuFlyoutItem> _items;
        readonly float _box, _glyph;
        public PlayerMoreMenu(IReadOnlyList<MenuFlyoutItem> items, float box, float glyph) { _items = items; _box = box; _glyph = glyph; }

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Build(_items, () => handle.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Width = _box, Height = _box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
                HoverScale = 1.06f, PressScale = 0.92f,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = Toggle, Cursor = CursorId.Hand, OnRealized = h => anchor.Value = h,
                Children = [new TextEl(Icons.More) { Size = _glyph, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
            };
        }
    }

    /// <summary>The primary play/pause action — a filled accent circle (the bar's focal point), glyph on-accent. Hover
    /// brightens to the accent-secondary/tertiary ramp; disabled drops to the neutral control fill.</summary>
    internal static BoxEl Primary(string glyph, Action onClick, bool enabled, ColorF accent, float box = 40f, float glyphSize = 23f)
    {
        var inner = new BoxEl
        {
            Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Theme.IconFont, Color = enabled ? Tok.TextPrimary : Tok.TextDisabled, HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary }],
        };
        return new BoxEl
        {
            Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            HoverScale = enabled ? 1.07f : 1f, PressScale = enabled ? 0.9f : 1f,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            Children = [inner],
        };
    }
}

/// <summary>A single time label (elapsed, or "-remaining"). Its OWN component so it re-renders at ~1 Hz on the position
/// tick WITHOUT re-rendering the whole bar (the seek bar beside it is compositor-only).</summary>
sealed class TimeText : Component
{
    readonly PlaybackBridge _b; readonly bool _remaining;
    public TimeText(PlaybackBridge b, bool remaining) { _b = b; _remaining = remaining; }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var (showRemaining, setShowRemaining) = UseState(svc?.Settings.Get(WaveeSettings.PlayerBarShowRemaining) ?? true);
        long pos = _b.PositionMs.Value;          // subscribe → 1 Hz tick
        long dur = _b.DurationMs.Value;
        bool rightDuration = _remaining;
        bool remainingMode = rightDuration && showRemaining;
        long ms = rightDuration ? (remainingMode ? Math.Max(0, dur - pos) : dur) : pos;
        string s = (remainingMode ? "-" : "") + PlayerBarContent.Fmt(ms);
        void ToggleDuration()
        {
            if (!rightDuration) return;
            bool next = !showRemaining;
            setShowRemaining(next);
            svc?.Settings.Set(WaveeSettings.PlayerBarShowRemaining, next);
        }
        return new BoxEl
        {
            Width = 44f, Direction = 0, AlignItems = FlexAlign.Center,
            Justify = _remaining ? FlexJustify.Start : FlexJustify.End,
            Fill = ColorF.Transparent,
            HoverFill = rightDuration ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = rightDuration ? Tok.FillSubtleTertiary : ColorF.Transparent,
            Corners = CornerRadius4.All(WaveeRadius.Control),
            OnClick = rightDuration ? ToggleDuration : null,
            Cursor = rightDuration ? CursorId.Hand : (CursorId?)null,
            Children = [Caption(s).Secondary() with { Wrap = TextWrap.NoWrap }],
        };
    }
}

/// <summary>The volume / mute glyph. Its OWN component so the glyph swap (volume crossing 0) doesn't re-render the bar
/// during a volume drag (the slider beside it is compositor-only).</summary>
sealed class VolumeButton : Component
{
    readonly PlaybackBridge _b; readonly bool _popup; readonly float _box; readonly float _glyphSize;
    public VolumeButton(PlaybackBridge b, bool popup = false, float box = 32f, float glyphSize = 16f)
    {
        _b = b; _popup = popup; _box = box; _glyphSize = glyphSize;
    }

    public override Element Render()
    {
        float v = _b.Volume.Value;               // subscribe → swap Mute/Volume glyph at the 0 boundary
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        string g = v <= 0.001f ? Icons.Mute : Icons.Volume;
        void TogglePopup()
        {
            if (!_popup) { ToggleMute(); return; }
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new VolumePopup(_b)),
                FlyoutPlacement.TopCenter);
        }
        return PlayerBarContent.Transport(g, TogglePopup, true, false, Tok.AccentDefault, _box, _glyphSize, h => anchor.Value = h);
    }

    void ToggleMute()
    {
        float v = _b.Volume.Peek();
        float nv = v > 0.001f ? 0f : 0.7f;       // mute ⇄ restore (a fuller mute-with-memory is the device-panel pass)
        _b.Volume.Value = nv; _ = _b.Player.SetVolumeAsync(nv);
    }
}

sealed class RemoteDeviceLine : Component
{
    readonly PlaybackBridge _b;

    public RemoteDeviceLine(PlaybackBridge b) => _b = b;

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var devices = _b.Devices.Value;
        string? activeId = _b.ActiveDeviceId.Value;
        var remote = PlayerBarContent.RemoteDevice(_b);
        if (remote is null) return new BoxEl { Height = 0f, HitTestVisible = false };

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var items = PlayerBarContent.DeviceMenuItems(_b, devices, activeId);
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(items, () => handle.Value?.Close()),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        ColorF accent = Tok.AccentDefault;
        return new BoxEl
        {
            Height = 13f, Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f,
            MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = Toggle, OnRealized = h => anchor.Value = h, ClipToBounds = true,
            Children =
            [
                new TextEl(Icons.Devices) { Size = 10f, FontFamily = Theme.IconFont, Color = accent with { A = 0.88f }, HoverColor = accent },
                new BoxEl
                {
                    Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
                    Children =
                    [
                        new TextEl(Strings.Player.PlayingOn(remote.Name))
                        {
                            Size = 10.5f, Weight = 700, Color = accent with { A = 0.88f }, HoverColor = accent,
                            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            ],
        };
    }
}

// The Connect device picker: opens a MenuFlyout of the live device roster (from the bridge) UPWARD out of the button.
// Each row toggles active + transfers playback to that device on click. Re-renders when the roster / active device changes.
sealed class DevicesButton : Component
{
    readonly PlaybackBridge _b; readonly ColorF _accent; readonly float _box, _glyph;
    public DevicesButton(PlaybackBridge b, ColorF accent, float box = 36f, float glyph = 16f)
    {
        _b = b; _accent = accent; _box = box; _glyph = glyph;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var devices = _b.Devices.Value;               // subscribe → re-render on roster change
        string? activeId = _b.ActiveDeviceId.Value;   // subscribe → the active row shows a check + the glyph highlights
        bool active = !string.IsNullOrEmpty(activeId);

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var items = PlayerBarContent.DeviceMenuItems(_b, devices, activeId);
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(items, () => handle.Value?.Close()),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return PlayerBarContent.Transport(Icons.Devices, Toggle, true, active, _accent, _box, _glyph, h => anchor.Value = h);
    }
}

// The up-next queue: opens a MenuFlyout peek of the live queue (now-playing checked, then "Next in queue" = user q#,
// then "Next up" = the context tracks ahead). Clicking an up-next entry plays that track (best-effort; a true
// skip-to-queue-item needs a seam addition). Re-renders when the queue changes.
sealed class QueueButton : Component
{
    readonly PlaybackBridge _b; readonly ColorF _accent; readonly float _box, _glyph;
    public QueueButton(PlaybackBridge b, ColorF accent, float box = 36f, float glyph = 16f)
    {
        _b = b; _accent = accent; _box = box; _glyph = glyph;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var queue = _b.Queue.Value;   // subscribe → re-render when the queue changes

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var items = new List<MenuFlyoutItem>(Math.Max(1, queue.Count));
            if (queue.Count == 0)
                items.Add(new MenuFlyoutItem(Loc.Get(Strings.Player.Queue), null, false, () => { }));
            else
            {
                QueueBucket? last = null;
                foreach (var e in queue)
                {
                    var entry = e;
                    if (entry.Bucket != QueueBucket.NowPlaying && entry.Bucket != last)
                        items.Add(new MenuFlyoutItem(entry.Bucket == QueueBucket.UserQueue ? "Next in queue" : "Next up", null, false, () => { }));
                    last = entry.Bucket;
                    string label = entry.Track.Artists.Count > 0 ? entry.Track.Title + "  ·  " + entry.Track.Artists[0].Name : entry.Track.Title;
                    if (entry.Bucket == QueueBucket.NowPlaying)
                        items.Add(MenuFlyoutItem.Toggle(label, true, () => { }, Icons.Queue, enabled: false));
                    else
                        items.Add(new MenuFlyoutItem(label, null, true, () => { _ = _b.Player.PlayTrackAsync(entry.Track.Uri); }));
                }
            }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(items, () => handle.Value?.Close()),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return PlayerBarContent.Transport(Icons.Queue, Toggle, true, false, _accent, _box, _glyph, h => anchor.Value = h);
    }
}

sealed class VolumePopup : Component
{
    readonly PlaybackBridge _b;
    public VolumePopup(PlaybackBridge b) { _b = b; }

    public override Element Render()
    {
        float v = _b.Volume.Value;
        void SetVolume(float next) { _b.Volume.Value = next; _ = _b.Player.SetVolumeAsync(next); }
        return new BoxEl
        {
            Width = 52f, Height = 168f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(10f, 14f, 10f, 14f),
            Children =
            [
                Slider.Ranged(v, SetVolume,
                    new Slider.Options { Vertical = true, IsThumbToolTipEnabled = false },
                    length: 124f, thickness: 32f, style: Slider.DefaultStyle)
            ],
        };
    }
}
