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
            ShowDevices: true,   // the device picker is the ONLY way to play (local playback unsupported) → always visible
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
                WaveeLog.Instance.Event(WaveeLogLevel.Debug, "ui", "playerbar.layout_band", "Player bar layout band changed",
                    fields:
                    [
                        WaveeLogField.Of("from", prev.Band),
                        WaveeLogField.Of("to", next.Band),
                        WaveeLogField.Of("viewportW", viewport.Peek().Width),
                    ]);
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
    // Shared marquee cadence for the now-playing title: a capped one-way travel time keeps long sibling lines aligned,
    // while Marquee.Speed prevents a barely-overflowing title from creeping invisibly. PingPong holds at the tail.
    const float MarqueeCycleMs = 10000f;
    const float MarqueeEndPauseMs = 2500f;
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
        var svc = UseContext(Services.Slot);
        var acts = UseContext(ActionServices.Slot);  // now-playing cluster context menu (Menus.NowPlaying)
        var menuOverlay = UseContext(Overlay.Service);
        var titleHover = UseSignal(false);           // hover the now-playing text → BOTH lines scroll together (synced); idle = static + edge fade
        var L = _layout.Value;                       // coarse breakpoint signal; does NOT change for every resize pixel
        _ = AppearancePrefs.Epoch.Value;
        bool marqueeDisabled = svc?.Settings.Get(WaveeSettings.DisableMarquee) ?? false;
        if (DiagEnabled)
            WaveeLog.Instance.Event(WaveeLogLevel.Debug, "ui", "playerbar.render", "Player bar rendered",
                fields:
                [
                    WaveeLogField.Of("render", ++s_renderCount),
                    WaveeLogField.Of("band", L.Band),
                ]);

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

        // Heart pop (transitions.dev icon swap) on the SAME-track unsaved→saved edge only: a track change flips
        // `liked` but also the uri (no pop); unlike stays a plain swap. Imperative seed on the captured button node —
        // a keyed remount of the focusable Transport button would reset its hover/focus state mid-toggle. (Hooks here
        // sit after the `b is null` early return, matching this component's existing practice — the bridge is
        // provided at the shell root and never goes null in a live session.)
        var likeNode = UseRef<NodeHandle>(default);
        var likePrev = UseRef(((string?)null, false));
        UseLayoutEffect(() =>
        {
            var (pUri, pLiked) = likePrev.Value;
            bool edge = liked && !pLiked && pUri == track?.Uri;
            likePrev.Value = (track?.Uri, liked);
            if (edge && showLike && !likeNode.Value.IsNull && Context.Anim is { } a)
                a.IconSwapIn(likeNode.Value);   // kit recipe — honors Motion.ReducedMotion internally
        }, (track?.Uri ?? "") + (liked ? "|1" : "|0"));
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
        // Both lines auto-scroll when the title overflows (PauseOnHover) and share ONE hover gate (titleHover) on the
        // meta column — hovering pauses so the user can read / click; CycleMs keeps any sibling lines phase-locked.
        // The edge fade is unaffected: a right-edge "there's more" cue at rest, both edges while scrolling.
        // Now-playing is clickable: art + title → the album; artists are per-name links inside a scrolling row.
        var npAlbum = track?.Album;
        bool albumNav = npAlbum is { Uri.Length: > 0 };
        void NavAlbum()
        {
            // Resolve at invoke time: the mounted click target survives track/metadata changes, so never navigate with
            // the render-time album capture (which can be the pre-hydration empty AlbumRef).
            if (b.CurrentTrack.Peek()?.Album is { Uri.Length: > 0 } album)
                go?.Invoke("album:" + album.Uri, album.Name);
        }

        var titleLinkHover = UseSignal(false);
        bool titleHot = albumNav && titleLinkHover.Value;
        Element titleEl = marqueeDisabled
            ? new BoxEl
            {
                ClipToBounds = true,
                Children = [new TextEl(Prop.Of(() => NowPlaying(b).Title))
                {
                    Size = 14f, Weight = 700,
                    Color = Prop.Of(() => titleHot ? Tok.AccentTextPrimary : NowPlaying(b).Color),
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                }],
            }
            : Marquee.Of(Prop.Of(() => NowPlaying(b).Title),
                new Marquee.Style
                {
                    FontSize = 14f, Weight = 700,
                    Foreground = Prop.Of(() => titleHot ? Tok.AccentTextPrimary : NowPlaying(b).Color),
                    Speed = 18f, CycleMs = MarqueeCycleMs, EndPauseMs = MarqueeEndPauseMs,
                    Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.PauseOnHover,
                },
                scrollWhen: titleHover);
        if (albumNav)   // make the marquee VIEWPORT itself the stable click target; no wrapper/input-boundary ambiguity
            titleEl = ((BoxEl)titleEl) with
            {
                Cursor = CursorId.Hand, OnClick = NavAlbum,
                OnHoverMove = _ =>
                {
                    if (!titleLinkHover.Peek()) titleLinkHover.Value = true;
                    if (!titleHover.Peek()) titleHover.Value = true;
                },
                OnPointerExit = () => { if (titleLinkHover.Peek()) titleLinkHover.Value = false; },
                Role = AutomationRole.Hyperlink, Focusable = true,
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
            metaKids.Add(marqueeDisabled
                ? new BoxEl { ClipToBounds = true, Children = [Embed.Comp(() => new NowPlayingArtistLinks())] }
                : Marquee.Content(() => new NowPlayingArtistLinks(),
                    new Marquee.Style
                    {
                        Speed = 18f, CycleMs = MarqueeCycleMs, EndPauseMs = MarqueeEndPauseMs,
                        Mode = Marquee.ScrollMode.PingPong, Trigger = Marquee.TriggerMode.PauseOnHover,
                    },
                    scrollWhen: titleHover));
        }

        var metaCol = new BoxEl
        {
            Key = "meta", Animate = MoveMotion,
            Direction = 1, Grow = 1f, Shrink = 1f, Gap = 2f, Justify = FlexJustify.Center, ClipToBounds = true,
            // The shared hover target: the title marquee reads titleHover, so hovering anywhere on the meta column pauses it.
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
            leftKids.Add(Transport(liked ? Mdl.HeartFill : Icons.Heart, () => { if (track is { } lt) lib?.ToggleSaved(lt.Uri, lt.Title); }, true, liked, accent, MathF.Min(30f, buttonBox), 15f,
                    onRealized: h => likeNode.Value = h)
                with { Key = "like", Animate = ItemMotion });

        var left = new BoxEl
        {
            Key = "left", Width = leftW, Shrink = 0f, MinWidth = 0f, Animate = MoveMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = L.LeftGap, ClipToBounds = true, Children = leftKids.ToArray(),
        };
        // Right-click / Menu key / long-press on the now-playing cluster: the track menu (Host = null → no Remove
        // rows). The factory Peeks the CURRENT track at open — never the render-time capture.
        if (acts is { } nowActs && menuOverlay is { } menuSvc)
            left = left.WithContextMenu(menuSvc, () =>
                b.CurrentTrack.Peek() is { } nowTrack ? Menus.NowPlaying(nowActs, nowTrack) : (ContextMenuModel?)null);

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
            overflowCommands.Add(new AppBarCommand(liked ? Mdl.HeartFill : Icons.Heart, Loc.Get(Strings.Player.Like), () => { if (track is { } lt) lib?.ToggleSaved(lt.Uri, lt.Title); }, AppBarCommandKind.ToggleButton, liked, true));
        // In the small-window overflow, Queue / Now Playing open their right-rail panels (the rail floats over the
        // content when it doesn't fit inline).
        if (!showQueue)
            overflowCommands.Add(new AppBarCommand(Icons.Queue, Loc.Get(Strings.Player.Queue), () => ui?.Toggle(RailMode.Queue), Enabled: ui is not null));
        // (Devices button is always visible now — no overflow fallback.)
        if (!showExpand)
            overflowCommands.Add(new AppBarCommand(Icons.ChevronUp, Loc.Get(Strings.Player.NowPlaying), () => ui?.Toggle(RailMode.Details), Enabled: ui is not null));

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
            rightKids.Add(Transport(WaveeIcons.Lyrics,
                () => ui.Toggle(RailMode.Lyrics),
                true,
                ui.RailOpen.Value && ui.Mode.Value == RailMode.Lyrics, accent, buttonBox, buttonGlyph,
                font: WaveeIcons.Font)
                with { Key = "lyrics", Animate = ItemMotion });
        // Switch-to-video toggle — shown only when the now-playing track has a music video (async-detected). The swap is a
        // seam for now (sets PreferVideo); the actual video surface/host is a follow-up. Tooltip explains the affordance.
        if (active && hasVideo)
            rightKids.Add(ToolTip.Wrap(
                Transport(Icons.Movie, () => { b.PreferVideo.Value = !b.PreferVideo.Value; }, true, preferVideo, accent, buttonBox, buttonGlyph),
                Loc.Get(preferVideo ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo))
                with { Key = "video" });
        if (showQueue)
            rightKids.Add(Transport(Icons.Queue, () => ui?.Toggle(RailMode.Queue), ui is not null,
                ui?.RailOpen.Value == true && ui.Mode.Value == RailMode.Queue, accent, buttonBox, buttonGlyph)
                with { Key = "queue", Animate = ItemMotion });
        if (showDevices)
            rightKids.Add(Embed.Comp(() => new DevicesButton(b, buttonBox, buttonGlyph, DevicePickerScope.Bar)) with { Key = "devices" });
        if (showExpand)
            rightKids.Add(Transport(Icons.ChevronUp, () => ui?.Toggle(RailMode.Details), ui is not null,
                ui?.RailOpen.Value == true && ui.Mode.Value == RailMode.Details, accent, buttonBox, buttonGlyph)
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
            Shadow = Elevation.DockTop,
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
        // A placeholder track seeds Title=Uri before metadata resolves; never surface the raw URI to the user.
        string title = track is { } t && !string.IsNullOrEmpty(t.Title) && t.Title != t.Uri ? t.Title : "";
        return st switch
        {
            PlayerState.NoTrack => (Loc.Get(Strings.Player.NothingPlaying), Tok.TextSecondary),
            PlayerState.Loading => (title.Length > 0 ? title : Loc.Get(Strings.Player.Loading), Tok.TextPrimary),
            PlayerState.Error   => (err ?? Loc.Get(Strings.Player.CannotPlay), Critical),
            _                   => (title, Tok.TextPrimary),
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

    // Two-section device picker (plan §C2): "This computer" (System default + local endpoints) + "Spotify Connect" (the
    // roster, ThisDevice filtered — this PC is section 1). The pure composition lives in DevicePickerModel (unit-tested);
    // this maps its rows to MenuFlyoutItems and wires the intents. Disabled Command rows stand in as section headers
    // (MenuFlyout has no header kind — risk 9.1; WinUI itself has no MenuFlyoutHeader).
    internal static List<MenuFlyoutItem> DevicePickerItems(PlaybackBridge b)
    {
        var connect = b.Devices.Value;
        string? activeId = b.ActiveDeviceId.Value;
        var lo = b.LocalOutputs;
        IReadOnlyList<LocalAudioDevice> local = lo?.Devices.Value ?? Array.Empty<LocalAudioDevice>();
        string? selectedLocal = lo?.SelectedOutputId.Value;
        bool supported = b.LocalPlaybackSupported.Value;
        bool weAreActiveOutput = RemoteDevice(b) is null;   // no remote Connect device active ⇒ we are the output

        var rows = DevicePickerModel.Build(local, selectedLocal, supported, weAreActiveOutput, connect, activeId,
            Loc.Get(Strings.Player.ThisComputer), Loc.Get(Strings.Player.SystemDefault), Loc.Get(Strings.Player.SpotifyConnect),
            Loc.Get(Strings.Player.Unavailable), Loc.Get(Strings.Player.NoDevices), Loc.Get(Strings.Player.NoDevicesHint));

        var items = new List<MenuFlyoutItem>(rows.Count);
        foreach (var r in rows) items.Add(MapRow(b, r));
        return items;
    }

    static MenuFlyoutItem MapRow(PlaybackBridge b, DevicePickerRow r) => r.Kind switch
    {
        DevicePickerRowKind.Separator => MenuFlyoutItem.Separator,
        DevicePickerRowKind.Header => new MenuFlyoutItem(r.Label, default, false, () => { }),
        DevicePickerRowKind.Empty => new MenuFlyoutItem(r.Label, default, false, () => { }),
        DevicePickerRowKind.LocalDefault => MenuFlyoutItem.RadioItem(r.Label, r.IsChecked,
            r.Enabled ? () => { _ = b.LocalOutputs?.SelectAsync(null); } : null, Mdl.ThisPc, enabled: r.Enabled)
            with { AcceleratorText = r.Accelerator },
        DevicePickerRowKind.LocalDevice => MenuFlyoutItem.RadioItem(r.Label, r.IsChecked,
            r.Enabled ? () => { var id = r.DeviceId; _ = b.LocalOutputs?.SelectAsync(id); } : null, LocalGlyph(r.LocalKind), enabled: r.Enabled)
            with { AcceleratorText = r.Accelerator },
        DevicePickerRowKind.ConnectDevice => MenuFlyoutItem.RadioItem(r.Label, r.IsChecked,
            () => { var id = r.DeviceId; if (id is not null) _ = b.DeviceControl.TransferAsync(id); }, DeviceGlyph(r.ConnectKind)),
        _ => new MenuFlyoutItem(r.Label, default, false, () => { }),
    };

    // Segoe Fluent glyph for a local (this-computer) output form factor.
    static string LocalGlyph(LocalAudioDeviceKind k) => k switch
    {
        LocalAudioDeviceKind.Speakers => Mdl.Speakers,
        LocalAudioDeviceKind.Headphones or LocalAudioDeviceKind.Headset => Mdl.Headphones,
        LocalAudioDeviceKind.Hdmi => Mdl.TvMonitor,
        _ => Mdl.ThisPc,
    };

    // Segoe Fluent glyph per Connect device kind (app-local Mdl set; the engine Icons.* set doesn't carry these).
    static string DeviceGlyph(DeviceKind k) => k switch
    {
        DeviceKind.Phone => Mdl.CellPhone,
        DeviceKind.Speaker => Mdl.Speakers,
        DeviceKind.Tv => Mdl.TvMonitor,
        _ => Mdl.ThisPc,   // ThisDevice / Computer
    };

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

    // Reactive artist row for the player-bar marquee — reads bridge/nav from context so track changes re-skin without remount.
    sealed class NowPlayingArtistLinks : Component
    {
        public override Element Render()
        {
            var b = UseContext(PlaybackBridge.Slot);
            var go = UseContext(HistoryStore.NavCtx);
            var artists = b?.CurrentTrack.Value?.Artists;
            var kids = new List<Element>(artists is { Count: > 0 } ? artists.Count * 2 : 0);
            if (artists is { Count: > 0 })
                AddArtistLinks(kids, artists, go);
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children = kids.ToArray(),
            };
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
    internal static BoxEl Transport(string glyph, Action onClick, bool enabled, bool active, ColorF accent, float box = 36f, float glyphSize = 16f,
        Action<NodeHandle>? onRealized = null, string? font = null)
    {
        ColorF fg = !enabled ? Tok.TextDisabled : active ? accent : Tok.TextSecondary;
        ColorF hover = !enabled ? Tok.TextDisabled : active ? accent : Tok.TextPrimary;
        var glyphLayer = new BoxEl
        {
            Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = font ?? Theme.IconFont, Color = fg, HoverColor = hover }],
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
                ? MenuFlyoutItem.Toggle(c.Label, c.IsChecked, c.Invoke, c.Icon, c.Enabled)
                : new MenuFlyoutItem(c.Label, c.Icon, c.Enabled, c.Invoke));
            // Cheap, alloc-light version: the command SET only changes at breakpoint crossings / toggle flips (not per
            // resize pixel), so fold a hash so the menu component re-mounts with fresh rows when the set actually changes.
            vh = vh * 31 + c.Icon.GetHashCode();
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

/// <summary>Cross-surface player-bar preference epoch: bumped when <see cref="WaveeSettings.PlayerBarShowRemaining"/>
/// changes from either the Settings toggle or the player-bar time label, so both surfaces stay in sync.</summary>
static class PlayerBarPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;
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
        // Either surface bumps PlayerBarPrefs after writing the setting → re-seed the mounted label live.
        int prefsEpoch = PlayerBarPrefs.Epoch.Value;
        UseEffect(() => setShowRemaining(svc?.Settings.Get(WaveeSettings.PlayerBarShowRemaining) ?? true), prefsEpoch);
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
            PlayerBarPrefs.Bump();
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
// The live two-section device-picker flyout body. Reads the bridge signals in Render so the roster updates WHILE the
// flyout is open (roster/selection/active-device/supported all fall out of signals), unlike a frozen open-time snapshot.
sealed class DevicePickerMenu : Component
{
    readonly PlaybackBridge _b; readonly Action _close;
    public DevicePickerMenu(PlaybackBridge b, Action close) { _b = b; _close = close; }
    public override Element Render() => MenuFlyout.Build(PlayerBarContent.DevicePickerItems(_b), _close);
}

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
        bool muted = _b.OutputMuted.Value;       // subscribe → external/session mute drives the glyph too (Phase B4)
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        string g = (muted || v <= 0.001f) ? Icons.Mute : Icons.Volume;
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
        // With a real output-device control (local audio wired) mute the Windows session directly (Phase B4); our own
        // session set is filtered by the engine's context guard, so update the optimistic UI here. Otherwise (fake
        // backend) keep today's software 0 ⇄ 0.7 toggle.
        if (_b.LocalOutputs is { } lo)
        {
            bool nowMuted = !_b.OutputMuted.Peek();
            _b.OutputMuted.Value = nowMuted;
            lo.SetMuted(nowMuted);
            return;
        }
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
        var remote = PlayerBarContent.RemoteDevice(_b);   // reads Devices + ActiveDeviceId → subscribes for re-render
        if (remote is null) return new BoxEl { Height = 0f, HitTestVisible = false };

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new DevicePickerMenu(_b, () => handle.Value?.Close())),
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

// Which mounted DevicesButton instance responds to a bridge DevicePickerRequest (the "Choose device" toast action):
// Bar = the player-bar button; None = never auto-open (embedded pickers).
enum DevicePickerScope : byte { None, Bar }

// The Connect device picker: opens a MenuFlyout of the live device roster (from the bridge) UPWARD out of the button.
// Each row toggles active + transfers playback to that device on click. Re-renders when the roster / active device changes.
// Also opens itself when the bridge's DevicePickerRequest is bumped (the critical "playback unsupported" toast's action) —
// only the Bar-scoped instance responds, so the toast opens exactly one picker.
sealed class DevicesButton : Component
{
    // No accent ctor arg: ctor args freeze at mount (Embed.Comp preserves the instance across parent re-renders),
    // so the accent is read in Render() where it stays live — RethemeAll re-renders this component on any Tok.Epoch
    // bump, and the now-playing scope's TrackPalette read subscribes to art changes.
    readonly PlaybackBridge _b; readonly float _box, _glyph; readonly DevicePickerScope _scope;
    public DevicesButton(PlaybackBridge b, float box = 36f, float glyph = 16f, DevicePickerScope scope = DevicePickerScope.None)
    {
        _b = b; _box = box; _glyph = glyph; _scope = scope;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        _ = _b.Devices.Value;                         // subscribe → re-render on roster change (flyout content reads its own)
        string? activeId = _b.ActiveDeviceId.Value;   // subscribe → the active row shows a check + the glyph highlights
        bool active = !string.IsNullOrEmpty(activeId);
        int req = _b.DevicePickerRequest.Value;       // subscribe → re-render (and re-run the effect) on a toast "Choose device" click
        var lastReq = UseRef(req);                     // seeded at mount → a request that predates this mount is ignored

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new DevicePickerMenu(_b, () => handle.Value?.Close())),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        // Open the picker in response to the toast action — in an effect (post-render), never during render. Both mounted
        // instances consume the request (each has its own lastReq), but only the scope matching Expanded actually opens it.
        UseEffect(() =>
        {
            if (_scope != DevicePickerScope.Bar || req == lastReq.Value) return;
            lastReq.Value = req;
            if (handle.Value is not { IsOpen: true }) Toggle();
        }, req);

        return PlayerBarContent.Transport(Icons.Devices, Toggle, true, active, Tok.AccentDefault, _box, _glyph, h => anchor.Value = h);
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
