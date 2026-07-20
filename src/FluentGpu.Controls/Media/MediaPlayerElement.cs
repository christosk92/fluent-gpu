using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls.Media;

/// <summary>How the video frame is scaled into the element's video area (WinUI <c>Stretch</c>).</summary>
public enum MediaStretch : byte
{
    /// <summary>Native size, centered, never scaled up (down-clamped to fit).</summary>
    None,
    /// <summary>Stretch to fill the whole area (aspect not preserved).</summary>
    Fill,
    /// <summary>Scale to fit, preserving aspect — letterbox/pillarbox (the default).</summary>
    Uniform,
    /// <summary>Scale to fill, preserving aspect — the overflow is clipped to the area.</summary>
    UniformToFill
}

/// <summary>
/// The real <c>MediaPlayerElement</c> (spec §4.3) — replaces the chrome-only mockup. Binds a headless
/// <see cref="IMediaPlayer"/> to a composited video layer and pure-FluentGpu transport chrome:
/// <list type="bullet">
/// <item>Acquires a composited video surface (<c>UseVideoSurface</c>), draws a premultiplied-0 hole at the video rect,
///   and drives the player's per-frame video pump (<see cref="IMediaPlayer.PumpVideo"/>) which binds the produced
///   DirectComposition handle and positions the child z-below the UI.</item>
/// <item><b>Degrades to audio-only chrome</b> when <see cref="IMediaPlayer.NaturalSize"/> is <c>(0,0)</c> — no hole, just
///   the poster/audio surface + transport.</item>
/// <item>The default transport is pure FluentGpu (our own GPU text + a scrub <c>Slider</c>) — there is NO OS control to
///   crash on (the WinUI <c>MediaTransportControls</c> #7702 <c>E_NOINTERFACE</c> process-crash is unreachable here).</item>
/// </list>
/// TerraFX-free: references only Engine/Controls types + the <see cref="IMediaPlayer"/>/<see cref="VideoBinding"/> seam.
/// </summary>
public sealed class MediaPlayerElement : Component
{
    private static readonly LayoutTransition ChromeMotion = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(180f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: 12f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: 12f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(140f, Easing.EaseInOut));
    private static readonly LayoutTransition LoadingMotion = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.SmoothOut),
        Enter: new EnterExit(Sx: 0.96f, Sy: 0.96f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: 0.98f, Sy: 0.98f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(140f, Easing.EaseInOut));

    /// <summary>The player this element presents (headless contract; the MF backend drives real video).</summary>
    public required IMediaPlayer Player { get; init; }
    /// <summary>Show the transport controls. Never crashes across OS versions (there is no OS control). Default true.</summary>
    public bool AreTransportControlsEnabled { get; init; } = true;
    /// <summary>Hide overlay chrome after inactivity while playback advances. Pointer/focus/touch reveal it.</summary>
    public bool AutoHideTransportControls { get; init; } = true;
    /// <summary>Idle time before playing chrome fades away. Defaults to 2.5 seconds.</summary>
    public float TransportControlsHideDelayMs { get; init; } = 2500f;
    /// <summary>How the frame scales into the video area. Default <see cref="MediaStretch.Uniform"/>.</summary>
    public MediaStretch Stretch { get; init; } = MediaStretch.Uniform;
    /// <summary>Optional dynamic professional aspect policy. When present it overrides <see cref="Stretch"/>.</summary>
    public IReadSignal<VideoAspectMode>? AspectMode { get; init; }
    /// <summary>Display aspect used by <see cref="VideoAspectMode.Custom"/> (for example 2.39). Defaults to 16:9.</summary>
    public IReadSignal<double>? CustomAspectRatio { get; init; }
    /// <summary>Notification for the built-in aspect menu. Writable Signal inputs are also updated directly.</summary>
    public Action<VideoAspectMode, double>? AspectModeChanged { get; init; }
    /// <summary>Opaque bars painted around Uniform/Native content. Defaults to video black.</summary>
    public ColorF LetterboxColor { get; init; } = ColorF.FromRgba(0, 0, 0);
    public bool ShowLetterboxBars { get; init; } = true;
    /// <summary>Shown over the video area until the first frame / when audio-only. Null → a default poster.</summary>
    public Element? PosterContent { get; init; }
    /// <summary>Bring-your-own transport (still reads the same bound player). Null → the default FluentGpu transport.</summary>
    public Element? TransportOverride { get; init; }

    internal Signal<bool>? FullscreenState { get; init; }
    internal bool IsFullscreenPresentation { get; init; }
    internal Action? ExitFullscreen { get; init; }
    /// <summary>The inline element's surface slot, handed to the fullscreen presentation. A second
    /// <c>UseVideoSurface</c> slot would bind the same swapchain handle to a second DComp visual, and destroying that
    /// visual on exit clobbers the shared content while the inline slot's value-gated re-bind no-ops — the video hole
    /// then permanently shows the window backdrop ("the player goes grey").</summary>
    internal VideoBinding ExternalBinding { get; init; }

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);
        // IsFullscreenPresentation freezes at mount, so the hook sequence is stable for this instance's lifetime.
        var binding = IsFullscreenPresentation ? ExternalBinding : UseVideoSurface();
        float scale = UseContext(Viewport.Scale);
        var hooks = UseContext(InputHooks.Current);
        var overlayService = UseContext(Overlay.Service);
        var areaRef = UseRef<NodeHandle>(default);
        var moreAnchor = UseRef<NodeHandle>(default);
        var ccAnchor = UseRef<NodeHandle>(default);
        var qualityAnchor = UseRef<NodeHandle>(default);
        var rateAnchor = UseRef<NodeHandle>(default);
        var fullscreenHandle = UseRef<OverlayHandle?>(null);
        var localFullscreen = UseSignal(false);
        var fullscreen = FullscreenState ?? localFullscreen;
        var localAspect = UseSignal(ToAspectMode(Stretch));
        var localCustomAspect = UseSignal(16.0 / 9.0);
        var chromeVisible = UseSignal(true);
        var hideEpoch = UseSignal(0);
        var overlayPin = UseSignal(0);   // >0 while a player-spawned flyout is open → chrome must not auto-hide under it

        var natural = Player.NaturalSize.Value;
        var state = Player.State.Value;
        bool playIntent = Player.IsPlayRequested.Value;
        var bufferingInfo = Player.Buffering.Value;
        TimedCue? activeCue = Player.ActiveCue.Value;
        bool audioOnly = IsAudioOnly(natural);
        bool videoReady = !audioOnly && state is not (PlaybackState.Idle or PlaybackState.Opening);
        bool fullscreenActive = fullscreen.Value;
        // Exactly ONE instance drives the shared surface slot at any time: the inline element while windowed, the
        // fullscreen presentation while fullscreen — including the overlay's closing-animation frames.
        bool pumpingHere = IsFullscreenPresentation == fullscreenActive;

        VideoAspectMode aspect = AspectMode?.Value ?? localAspect.Value;
        double customAspect = CustomAspectRatio?.Value ?? localCustomAspect.Value;
        RectF area = default;
        if (hooks?.GetNodeRect is { } getRect && !areaRef.Value.IsNull)
            area = getRect(areaRef.Value);
        RectF videoRect = (audioOnly || area.W <= 0f) ? area : FitVideoRect(area, natural, aspect, customAspect);
        if (pumpingHere)
        {
            binding.SetViewport(area);
            Player.PumpVideo(binding, videoRect, scale <= 0f ? 1f : scale);
            if (audioOnly) binding.SetVisible(false);
        }
        // While the fullscreen view owns the shared binding, the inline element must not touch it at all — a hide or
        // viewport write here would fight the fullscreen pump on the same registry slot.

        void RevealChrome()
        {
            if (!AreTransportControlsEnabled) return;
            chromeVisible.Value = true;
            hideEpoch.Value = hideEpoch.Peek() + 1;
        }

        void SetAspect(VideoAspectMode mode, double ratio = 0)
        {
            double nextRatio = ratio > 0 ? ratio : customAspect;
            if (AspectMode is Signal<VideoAspectMode> writableAspect) writableAspect.Value = mode;
            else localAspect.Value = mode;
            if (CustomAspectRatio is Signal<double> writableRatio && ratio > 0) writableRatio.Value = ratio;
            else if (ratio > 0) localCustomAspect.Value = ratio;
            AspectModeChanged?.Invoke(mode, nextRatio);
            RevealChrome();
        }

        void LeaveFullscreen()
        {
            if (!fullscreen.Peek() && fullscreenHandle.Value is null) return;
            fullscreen.Value = false;
            hooks?.WindowSetFullscreen?.Invoke(false);
            var h = fullscreenHandle.Value;
            fullscreenHandle.Value = null;
            if (h is { IsOpen: true }) h.Close();
            RevealChrome();
        }

        void ToggleFullscreen()
        {
            if (IsFullscreenPresentation) { ExitFullscreen?.Invoke(); return; }
            if (fullscreen.Peek()) { LeaveFullscreen(); return; }
            fullscreen.Value = true;
            hooks?.WindowSetFullscreen?.Invoke(true);
            var h = overlayService.OpenAt(
                static () => new RectF(0, 0, 1, 1),
                () => Embed.Comp(() => new FullscreenMediaView
                {
                    Player = Player,
                    Binding = binding,
                    AspectMode = AspectMode ?? localAspect,
                    CustomAspectRatio = CustomAspectRatio ?? localCustomAspect,
                    AspectModeChanged = AspectModeChanged,
                    FullscreenState = fullscreen,
                    Exit = LeaveFullscreen,
                    LetterboxColor = LetterboxColor,
                }),
                FlyoutPlacement.BottomLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Modal));
            fullscreenHandle.Value = h;
            h.ClosedAction = LeaveFullscreen;
        }

        bool forceChrome = ShouldForceChrome(playIntent, state);
        bool menuPinned = overlayPin.Value > 0;
        bool showChrome = AreTransportControlsEnabled && (forceChrome || menuPinned || chromeVisible.Value);

        Element? statusOverlay = !videoReady && (state is PlaybackState.Opening or PlaybackState.Buffering || playIntent)
            ? OpeningOverlay(playIntent)
            : bufferingInfo.IsBuffering || state is PlaybackState.Buffering or PlaybackState.Stalled
                ? BufferingOverlay(bufferingInfo)
                : null;

        // Base layer under the (transparent) video hole: while a loading/buffering overlay is up, show a plain dark
        // stage — the poster's play glyph underneath the spinner reads as two competing affordances.
        var videoChildren = new System.Collections.Generic.List<Element>(8)
        {
            videoReady
                ? new BoxEl { Grow = 1f }
                : statusOverlay is not null
                    ? new BoxEl { Grow = 1f, Fill = ColorF.FromRgba(0x0A, 0x0A, 0x0A) }
                    : PosterContent ?? DefaultPoster(),
        };
        if (ShowLetterboxBars && videoReady) AddLetterboxBars(videoChildren, area, videoRect, LetterboxColor);

        if (statusOverlay is not null)
            videoChildren.Add(new BoxEl
            {
                Key = videoReady ? "media-buffering" : "media-opening",
                Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                Animate = LoadingMotion,
                Children = [statusOverlay],
            });
        if (videoReady && activeCue is { } cue) videoChildren.Add(CaptionOverlay(cue));

        var videoArea = new BoxEl
        {
            ZStack = true,
            Direction = 1,
            Grow = 1f,
            MinHeight = IsFullscreenPresentation ? 0f : 160f,
            ClipToBounds = true,
            Corners = IsFullscreenPresentation ? default : Radii.OverlayAll,
            Fill = ColorF.Transparent,
            OnRealized = h => areaRef.Value = h,
            Children = videoChildren.ToArray(),
        };

        var layers = new System.Collections.Generic.List<Element>(4) { videoArea };
        if (showChrome)
            layers.Add(new BoxEl
            {
                Key = "media-chrome",
                Grow = 1f,
                Direction = 1,
                Justify = FlexJustify.End,
                HitTestPassThrough = true,
                Animate = ChromeMotion,
                Children = [TransportOverride ?? BuildTransport(area.W, aspect, customAspect, SetAspect, ToggleFullscreen,
                    moreAnchor, ccAnchor, qualityAnchor, rateAnchor, overlayService, overlayPin)],
            });
        if (showChrome && AutoHideTransportControls && !menuPinned && playIntent && state == PlaybackState.Playing)
            layers.Add(Embed.Comp(() => new ToolTipClock
            {
                DurationMs = MathF.Max(500f, TransportControlsHideDelayMs),
                OnElapsed = () => chromeVisible.Value = false,
            }) with { Key = "media-hide:" + hideEpoch.Value });

        void HandleKey(KeyEventArgs e)
        {
            RevealChrome();
            switch (e.KeyCode)
            {
                case Keys.Space:
                    if (playIntent) _ = Player.PauseAsync(); else _ = Player.PlayAsync();
                    e.Handled = true;
                    break;
                case Keys.Left:
                    _ = Player.SeekAsync(Player.Position.Peek() - TimeSpan.FromSeconds(10)); e.Handled = true; break;
                case Keys.Right:
                    _ = Player.SeekAsync(Player.Position.Peek() + TimeSpan.FromSeconds(10)); e.Handled = true; break;
                case Keys.F11:
                    ToggleFullscreen(); e.Handled = true; break;
                case Keys.Escape when IsFullscreenPresentation:
                    ExitFullscreen?.Invoke(); e.Handled = true; break;
            }
        }

        _ = tick;
        return new BoxEl
        {
            ZStack = true,
            Grow = 1f,
            Corners = IsFullscreenPresentation ? default : Radii.OverlayAll,
            ClipToBounds = true,
            BorderColor = IsFullscreenPresentation ? ColorF.Transparent : Tok.StrokeFlyoutDefault,
            BorderWidth = IsFullscreenPresentation ? 0f : 1f,
            Focusable = true,
            OnKeyDown = HandleKey,
            OnPointerMoveWithin = _ => RevealChrome(),
            OnPointerPressed = _ => RevealChrome(),
            OnFocusChanged = focused => { if (focused) RevealChrome(); },
            Children = layers.ToArray(),
        };
    }

    // ── default transport (pure FluentGpu: play/pause, scrub Slider, GPU time text, mute) ────────────────────────────

    private Element BuildTransport(float areaWidth, VideoAspectMode aspect, double customAspect,
        Action<VideoAspectMode, double> setAspect, Action toggleFullscreen, Ref<NodeHandle> moreAnchor,
        Ref<NodeHandle> ccAnchor, Ref<NodeHandle> qualityAnchor, Ref<NodeHandle> rateAnchor,
        IOverlayService overlayService, Signal<int> overlayPin)
    {
        // The transport render reads only LOW-frequency signals (play-state + muted) so it does NOT re-render each frame
        // as the playhead advances. The seek scrub bar is an AUTONOMOUS component (its own scrub gate + compositor-bound
        // playhead — see MediaSeekBar), and the time label is an isolated leaf that re-renders on its own position tick.
        // The old per-frame Slider.Create(frac, …) recreated a controlled slider every frame, destroying any in-flight
        // drag (the thumb snapped back on release) — the seek bug this replaces.
        bool playIntent = Player.IsPlayRequested.Value;        // intent wins during Opening/Buffering (early Play)
        bool muted = Player.Muted.Value;                       // subscribe (low-frequency)
        MediaCommandFlags commands = Player.Commands.Available.Value;
        TimelineInfo timeline = Player.Timeline.Value;
        _ = Player.Tracks.Audio.Version.Value;
        _ = Player.Tracks.Text.Version.Value;
        _ = Player.Qualities.Variants.Version.Value;
        MediaTrack? audio = Player.Tracks.SelectedAudio.Value;
        MediaTrack? text = Player.Tracks.SelectedText.Value;
        QualitySelection quality = Player.Qualities.Selected.Value;
        float rate = Player.Rate.Value;
        _ = areaWidth;                                         // the seek bar now Grows to fill; width is self-measured

        var playPause = IconButton(playIntent ? Icons.Pause : Icons.Play, () =>
        {
            if (playIntent) _ = Player.PauseAsync(); else _ = Player.PlayAsync();
        });

        var muteBtn = IconButton(muted ? Icons.Mute : Icons.Volume, () => Player.SetMuted(!muted));

        var seek = new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center,
            Children = new Element[] { Embed.Comp(() => new MediaSeekBar { Player = Player }) },
        };

        bool compact = IsCompactTransport(areaWidth);
        var controls = new System.Collections.Generic.List<Element>(12) { playPause, muteBtn };
        if (areaWidth <= 0f || areaWidth >= 420f)
            controls.Add(Embed.Comp(() => new MediaTransportTime { Player = Player }));
        controls.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        if ((commands & MediaCommandFlags.GoLive) != 0)
            controls.Add(TextButton(timeline.IsAtLiveEdge ? "● LIVE" : "Go live", () => _ = Player.GoLiveAsync(), timeline.IsAtLiveEdge));
        // Each inline button opens a PICKER flyout at its own anchor (never blind-cycles through the options).
        if (!compact && (commands & MediaCommandFlags.SelectTextTrack) != 0 && Player.Tracks.Text.Count > 0)
            controls.Add(TextButton(text is null ? "CC" : $"CC {text.Language ?? text.Label}",
                () => OpenPicker(ccAnchor, CaptionItems()), text is not null) with { OnRealized = h => ccAnchor.Value = h });
        if (!compact && (commands & MediaCommandFlags.SelectVideoQuality) != 0 && Player.Qualities.Variants.Count > 0)
            controls.Add(TextButton(QualityLabel(quality), () => OpenPicker(qualityAnchor, QualityItems()))
                with { OnRealized = h => qualityAnchor.Value = h });
        if (!compact && (commands & MediaCommandFlags.Rate) != 0)
            controls.Add(TextButton($"{rate:0.##}×", () => OpenPicker(rateAnchor, SpeedItems()))
                with { OnRealized = h => rateAnchor.Value = h });
        controls.Add(IconButton(Icons.More, OpenMore) with { OnRealized = h => moreAnchor.Value = h });
        controls.Add(IconButton(IsFullscreenPresentation ? Icons.BackToWindow : Icons.FullScreen, toggleFullscreen));

        return new BoxEl
        {
            Direction = 1,
            Gap = 2f,
            Padding = new Edges4(14, 34, 14, 8),
            // A bottom-anchored scrim ramp instead of a flat band: the controls sit on darkness that dissolves into the
            // video, the YouTube/Netflix-style overlay read.
            Gradient = new GradientSpec(GradientShape.Linear, 90f,
            [
                new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 0x00)),
                new GradientStop(0.45f, ColorF.FromRgba(0, 0, 0, 0x66)),
                new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 0xB8)),
            ]),
            Children =
            [
                seek,
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Children = controls.ToArray() },
            ],
        };

        void OpenPicker(Ref<NodeHandle> anchor, System.Collections.Generic.List<MenuFlyoutItem> items)
        {
            OverlayHandle? m = null;
            m = overlayService.Open(() => anchor.Value,
                () => MenuFlyout.Build(items, () => m?.Close()), FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            // Pin the chrome while the flyout is open: auto-hide collapsing the transport would destroy the anchor
            // mid-flight and make the open flyout jump.
            overlayPin.Value++;
            bool unpinned = false;
            m.ClosedAction = () => { if (!unpinned) { unpinned = true; overlayPin.Value = Math.Max(0, overlayPin.Peek() - 1); } };
        }

        System.Collections.Generic.List<MenuFlyoutItem> CaptionItems()
        {
            var items = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Tracks.Text.Count + 1)
            { MenuFlyoutItem.RadioItem("Off", text is null, () => _ = Player.SelectTrackAsync(null)) };
            for (int i = 0; i < Player.Tracks.Text.Count; i++)
            {
                MediaTrack track = Player.Tracks.Text[i];
                items.Add(MenuFlyoutItem.RadioItem(track.Label ?? track.Language ?? $"Captions {i + 1}", text?.Id == track.Id,
                    () => _ = Player.SelectTrackAsync(track)));
            }
            return items;
        }

        System.Collections.Generic.List<MenuFlyoutItem> QualityItems()
        {
            var items = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Qualities.Variants.Count + 1)
            { MenuFlyoutItem.RadioItem("Auto", quality.IsAuto, () => _ = Player.SelectQualityAsync(QualitySelection.Auto)) };
            for (int i = 0; i < Player.Qualities.Variants.Count; i++)
            {
                QualityVariant variant = Player.Qualities.Variants[i];
                string id = variant.Id;
                string label = variant.Resolution.Height > 0 ? $"{variant.Resolution.Height}p" : variant.Label ?? id;
                items.Add(MenuFlyoutItem.RadioItem(label, !quality.IsAuto && quality.VariantId == id,
                    () => _ = Player.SelectQualityAsync(QualitySelection.Pin(id))));
            }
            return items;
        }

        System.Collections.Generic.List<MenuFlyoutItem> AudioItems()
        {
            var items = new System.Collections.Generic.List<MenuFlyoutItem>(Player.Tracks.Audio.Count);
            for (int i = 0; i < Player.Tracks.Audio.Count; i++)
            {
                MediaTrack track = Player.Tracks.Audio[i];
                items.Add(MenuFlyoutItem.RadioItem(track.Label ?? track.Language ?? $"Audio {i + 1}", audio?.Id == track.Id,
                    () => _ = Player.SelectTrackAsync(track)));
            }
            return items;
        }

        System.Collections.Generic.List<MenuFlyoutItem> SpeedItems() =>
        [
            Speed(0.5), Speed(0.75), Speed(1), Speed(1.25), Speed(1.5), Speed(2),
        ];

        MenuFlyoutItem Speed(double value) => MenuFlyoutItem.RadioItem($"{value:0.##}×", Math.Abs(rate - value) < 0.01,
            () => Player.SetRate(value));

        void OpenMore()
        {
            var items = new System.Collections.Generic.List<MenuFlyoutItem>(12);
            items.Add(MenuFlyoutItem.SubMenu("Aspect ratio",
            [
                MenuFlyoutItem.RadioItem("Fit · black bars", aspect == VideoAspectMode.Uniform, () => setAspect(VideoAspectMode.Uniform, 0)),
                MenuFlyoutItem.RadioItem("Crop · fill frame", aspect == VideoAspectMode.UniformToFill, () => setAspect(VideoAspectMode.UniformToFill, 0)),
                MenuFlyoutItem.RadioItem("Stretch", aspect == VideoAspectMode.Fill, () => setAspect(VideoAspectMode.Fill, 0)),
                MenuFlyoutItem.RadioItem("None · native pixels", aspect == VideoAspectMode.Native, () => setAspect(VideoAspectMode.Native, 0)),
                MenuFlyoutItem.Separator,
                MenuFlyoutItem.RadioItem("16:9", aspect == VideoAspectMode.Custom && Near(customAspect, 16.0 / 9.0), () => setAspect(VideoAspectMode.Custom, 16.0 / 9.0)),
                MenuFlyoutItem.RadioItem("4:3", aspect == VideoAspectMode.Custom && Near(customAspect, 4.0 / 3.0), () => setAspect(VideoAspectMode.Custom, 4.0 / 3.0)),
                MenuFlyoutItem.RadioItem("21:9", aspect == VideoAspectMode.Custom && Near(customAspect, 21.0 / 9.0), () => setAspect(VideoAspectMode.Custom, 21.0 / 9.0)),
                MenuFlyoutItem.RadioItem("2.39:1", aspect == VideoAspectMode.Custom && Near(customAspect, 2.39), () => setAspect(VideoAspectMode.Custom, 2.39)),
            ], Icons.Movie));
            if ((commands & MediaCommandFlags.Rate) != 0)
                items.Add(MenuFlyoutItem.SubMenu("Playback speed", SpeedItems()));
            if ((commands & MediaCommandFlags.SelectVideoQuality) != 0 && Player.Qualities.Variants.Count > 0)
                items.Add(MenuFlyoutItem.SubMenu("Quality", QualityItems()));
            if ((commands & MediaCommandFlags.SelectAudioTrack) != 0 && Player.Tracks.Audio.Count > 1)
                items.Add(MenuFlyoutItem.SubMenu("Audio track", AudioItems()));
            if ((commands & MediaCommandFlags.SelectTextTrack) != 0 && Player.Tracks.Text.Count > 0)
                items.Add(MenuFlyoutItem.SubMenu("Captions", CaptionItems()));
            if ((commands & MediaCommandFlags.Chapters) != 0)
            {
                items.Add(MenuFlyoutItem.Separator);
                items.Add(new MenuFlyoutItem("Previous chapter", Icons.Previous, Invoke: () => _ = Player.PreviousChapterAsync()));
                items.Add(new MenuFlyoutItem("Next chapter", Icons.Next, Invoke: () => _ = Player.NextChapterAsync()));
            }
            items.Add(MenuFlyoutItem.Separator);
            items.Add(new MenuFlyoutItem(IsFullscreenPresentation ? "Exit fullscreen" : "Fullscreen",
                IsFullscreenPresentation ? Icons.BackToWindow : Icons.FullScreen, Invoke: toggleFullscreen) { AcceleratorText = "F11" });

            OpenPicker(moreAnchor, items);
        }
    }

    /// <summary>The <c>elapsed / total</c> time label as its OWN component so it re-renders on the ~per-frame position
    /// tick WITHOUT re-rendering the transport (whose seek bar is compositor-only).</summary>
    private sealed class MediaTransportTime : Component
    {
        public required IMediaPlayer Player { get; init; }

        public override Element Render()
        {
            var pos = Player.Position.Value;    // subscribe → this leaf re-renders on the position tick
            var dur = Player.Duration.Value;
            return new TextEl($"{FormatTime(pos)} / {FormatTime(dur)}")
            {
                Size = 12f, Color = ColorF.FromRgba(235, 235, 235),
            };
        }
    }

    private sealed class FullscreenMediaView : Component
    {
        public required IMediaPlayer Player { get; init; }
        public required VideoBinding Binding { get; init; }
        public required IReadSignal<VideoAspectMode> AspectMode { get; init; }
        public required IReadSignal<double> CustomAspectRatio { get; init; }
        public required Signal<bool> FullscreenState { get; init; }
        public required Action Exit { get; init; }
        public Action<VideoAspectMode, double>? AspectModeChanged { get; init; }
        public ColorF LetterboxColor { get; init; }

        public override Element Render()
        {
            Size2 viewport = UseContext(Viewport.Size);
            return new BoxEl
            {
                Width = viewport.Width,
                Height = viewport.Height,
                Fill = LetterboxColor,
                Children =
                [
                    Embed.Comp(() => new MediaPlayerElement
                    {
                        Player = Player,
                        ExternalBinding = Binding,
                        AspectMode = AspectMode,
                        CustomAspectRatio = CustomAspectRatio,
                        AspectModeChanged = AspectModeChanged,
                        LetterboxColor = LetterboxColor,
                        FullscreenState = FullscreenState,
                        IsFullscreenPresentation = true,
                        ExitFullscreen = Exit,
                    }),
                ],
            };
        }
    }

    private static BoxEl IconButton(string glyph, Action onClick) => new()
    {
        Width = 40f, Height = 40f, Corners = Radii.ControlAll,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        HoverFill = ColorF.FromRgba(255, 255, 255, 0x18), PressedFill = ColorF.FromRgba(255, 255, 255, 0x28),
        Role = AutomationRole.Button,
        OnClick = onClick,
        Children = new Element[] { new TextEl(glyph) { Size = 16f, Color = ColorF.FromRgba(255, 255, 255), FontFamily = Theme.IconFont } },
    };

    private static BoxEl TextButton(string label, Action onClick, bool active = false) => new()
    {
        Height = 34f, MinWidth = 42f, Padding = new Edges4(8f, 0f, 8f, 0f), Corners = Radii.ControlAll,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = active ? Tok.AccentDefault : ColorF.Transparent,
        HoverFill = active ? Tok.AccentSecondary : ColorF.FromRgba(255, 255, 255, 0x18),
        PressedFill = active ? Tok.AccentTertiary : ColorF.FromRgba(255, 255, 255, 0x28),
        Role = AutomationRole.Button, OnClick = onClick,
        Children = [new TextEl(label) { Size = 12f, Color = ColorF.FromRgba(255, 255, 255) }],
    };

    private string QualityLabel(QualitySelection selection)
    {
        if (selection.IsAuto) return "Auto";
        for (int i = 0; i < Player.Qualities.Variants.Count; i++)
        {
            QualityVariant q = Player.Qualities.Variants[i];
            if (q.Id == selection.VariantId)
                return q.Resolution.Height > 0 ? $"{q.Resolution.Height}p" : q.Label ?? q.Id;
        }
        return "Auto";
    }

    private Element DefaultPoster() => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = ColorF.FromRgba(0x0A, 0x0A, 0x0A),
        Children = new Element[]
        {
            new BoxEl
            {
                Width = 56f, Height = 56f, Corners = Radii.Circle(56f),
                Fill = ColorF.FromRgba(0, 0, 0, 0x80),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = new Element[]
                {
                    new TextEl(Icons.Play) { Size = 24f, Color = ColorF.FromRgba(0xFF, 0xFF, 0xFF), FontFamily = Theme.IconFont },
                },
            },
        },
    };

    private static Element OpeningOverlay(bool playIntent) => new BoxEl
    {
        // No card, no border: a quiet centered spinner over the dark stage (the WinUI/streaming-player loading read).
        Direction = 1,
        Gap = 14f,
        AlignItems = FlexAlign.Center,
        Children =
        [
            ProgressRing.Indeterminate(40f),
            new TextEl(playIntent ? "Starting playback…" : "Loading…")
            { Size = 13f, Color = ColorF.FromRgba(255, 255, 255, 0xDC) },
        ],
    };

    private static void AddLetterboxBars(System.Collections.Generic.List<Element> children,
        RectF area, RectF video, ColorF color)
    {
        Span<RectF> bars = stackalloc RectF[4];
        int count = CalculateLetterboxBars(area, video, bars);
        for (int i = 0; i < count; i++)
        {
            RectF r = bars[i];
            children.Add(new BoxEl
            {
                Width = r.W, Height = r.H, OffsetX = r.X, OffsetY = r.Y,
                Fill = color, HitTestVisible = false,
            });
        }
    }

    internal static int CalculateLetterboxBars(RectF area, RectF video, Span<RectF> bars)
    {
        if (area.W <= 0f || area.H <= 0f || bars.Length < 4) return 0;
        float x = video.X - area.X, y = video.Y - area.Y;
        float left = Math.Clamp(x, 0f, area.W);
        float top = Math.Clamp(y, 0f, area.H);
        float right = Math.Clamp(area.W - (x + video.W), 0f, area.W);
        float bottom = Math.Clamp(area.H - (y + video.H), 0f, area.H);
        int count = 0;
        if (left > 0.5f) bars[count++] = new RectF(0, 0, left, area.H);
        if (right > 0.5f) bars[count++] = new RectF(area.W - right, 0, right, area.H);
        float middleW = MathF.Max(0, area.W - left - right);
        if (top > 0.5f) bars[count++] = new RectF(left, 0, middleW, top);
        if (bottom > 0.5f) bars[count++] = new RectF(left, area.H - bottom, middleW, bottom);
        return count;
    }

    internal static bool ShouldForceChrome(bool playIntent, PlaybackState state)
        => !playIntent || state is not PlaybackState.Playing;

    internal static bool IsCompactTransport(float width) => width > 0f && width < 760f;

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    private static Element BufferingOverlay(BufferingInfo info)
    {
        string reason = info.Reason switch
        {
            BufferingReason.Seeking => "Seeking…",
            BufferingReason.QualitySwitch => "Changing quality…",
            BufferingReason.TrackSwitch => "Changing track…",
            BufferingReason.LiveCatchUp => "Catching up to live…",
            BufferingReason.NetworkRecovery => "Reconnecting…",
            BufferingReason.Rebuffering => "Buffering…",
            _ => "Loading…",
        };
        Element ring = info.Percent is >= 0 and <= 1
            ? ProgressRing.Determinate((float)info.Percent, 36f)
            : ProgressRing.Indeterminate(36f);
        return new BoxEl
        {
            Direction = 1, Gap = 8f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(14f, 12f, 14f, 12f), Corners = Radii.OverlayAll,
            Fill = ColorF.FromRgba(0, 0, 0, 0x8A),
            Children = [ring, new TextEl(reason) { Size = 13f, Color = ColorF.FromRgba(255, 255, 255) }],
        };
    }

    private static Element CaptionOverlay(TimedCue cue) => new BoxEl
    {
        AlignSelf = FlexAlign.Center,
        MaxWidth = 880f,
        Margin = new Edges4(24f, 0f, 24f, 28f),
        Padding = new Edges4(10f, 5f, 10f, 6f),
        Corners = Radii.ControlAll,
        Fill = ColorF.FromRgba(0, 0, 0, 0xB8),
        Children =
        [
            new TextEl(cue.Text)
            {
                Size = Math.Clamp(18f * cue.Style.FontScale, 12f, 40f),
                Color = cue.Style.ArgbColor == 0 ? ColorF.FromRgba(255, 255, 255) : FromArgb(cue.Style.ArgbColor),
                Wrap = TextWrap.Wrap,
            },
        ],
    };

    private static ColorF FromArgb(uint argb)
        => ColorF.FromRgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    // ── pure helpers (unit-tested) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Degrade decision: audio-only (no hole-punch) iff the source has no video.</summary>
    internal static bool IsAudioOnly(SizeI natural) => natural.IsEmpty;

    /// <summary>Fit a <paramref name="natural"/>-sized frame into <paramref name="area"/> (DIP) per <paramref name="stretch"/>.
    /// Returns the placed video rect (DIP), centered; never larger than the area for <see cref="MediaStretch.UniformToFill"/>
    /// (the overflow axis is clipped to the area — true center-crop via an MF source-rect is a later refinement).</summary>
    internal static RectF FitVideoRect(RectF area, SizeI natural, MediaStretch stretch)
        => FitVideoRect(area, natural, ToAspectMode(stretch), 16.0 / 9.0);

    internal static RectF FitVideoRect(RectF area, SizeI natural, VideoAspectMode aspectMode, double customAspect)
    {
        if (area.W <= 0f || area.H <= 0f || natural.IsEmpty) return area;
        float aw = area.W, ah = area.H;
        float vw = natural.Width, vh = natural.Height;
        if (aspectMode == VideoAspectMode.Custom && double.IsFinite(customAspect) && customAspect > 0.01)
        { vw = (float)customAspect * 1000f; vh = 1000f; }
        switch (aspectMode)
        {
            case VideoAspectMode.Fill:
                return area;
            case VideoAspectMode.Native:
            {
                float w = MathF.Min(vw, aw), h = MathF.Min(vh, ah);
                return Center(area, w, h);
            }
            case VideoAspectMode.UniformToFill:
            {
                float s = MathF.Max(aw / vw, ah / vh);
                return Center(area, vw * s, vh * s);
            }
            case VideoAspectMode.Custom:
            case VideoAspectMode.Uniform:
            default:
            {
                float s = MathF.Min(aw / vw, ah / vh);
                return Center(area, vw * s, vh * s);
            }
        }
    }

    private static VideoAspectMode ToAspectMode(MediaStretch stretch) => stretch switch
    {
        MediaStretch.None => VideoAspectMode.Native,
        MediaStretch.Fill => VideoAspectMode.Fill,
        MediaStretch.UniformToFill => VideoAspectMode.UniformToFill,
        _ => VideoAspectMode.Uniform,
    };

    private static RectF Center(RectF area, float w, float h)
        => new(area.X + (area.W - w) * 0.5f, area.Y + (area.H - h) * 0.5f, w, h);

    /// <summary>DIP→device-px rect (the hole-punch device rect the presenter places at).</summary>
    internal static RectF ToDeviceRect(RectF dip, float scale)
    {
        float s = scale <= 0f ? 1f : scale;
        return new RectF(dip.X * s, dip.Y * s, dip.W * s, dip.H * s);
    }

    /// <summary><c>m:ss</c> (or <c>h:mm:ss</c> past an hour); a negative/unknown span reads <c>0:00</c>.</summary>
    internal static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero || t == TimeSpan.MinValue) t = TimeSpan.Zero;
        int total = (int)t.TotalSeconds;
        int h = total / 3600, m = (total % 3600) / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }
}
